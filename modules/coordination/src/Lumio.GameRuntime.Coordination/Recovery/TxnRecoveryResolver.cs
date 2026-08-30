using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Coordination;

public enum CrashBoundary
{
    BeforeCommitIntent,
    AfterCommitIntent,
    AfterVoxelApply,
    AfterVoxelMarker,
    AfterEcsApply,
    AfterEcsMarker,
    AfterCommittedMarker
}

public readonly record struct TxnRecoveryResult(
    TxnCommitStatus Status,
    TxnParticipantState VoxelParticipant,
    TxnParticipantState EcsParticipant,
    SessionRevisionVectorView? ResultRevision,
    CoordinationFailure? Failure,
    IReadOnlyList<string> Trace)
{
    public bool Succeeded => Status is TxnCommitStatus.Committed or TxnCommitStatus.AlreadyCommitted;

    public TxnCommitResult ToCommitResult(TxnRecord? record = null) =>
        new(Status, VoxelParticipant, EcsParticipant, ResultRevision, Trace, Failure, record);
}

/// <summary>Reconciles a durable intent using explicit participant queries; Unknown is never guessed.</summary>
public sealed class TxnRecoveryResolver
{
    private static readonly IReadOnlyList<string> EmptyTrace = Array.Empty<string>();
    private static readonly IReadOnlyList<string> ProvenTrace = new[] { "Recovery.ProvenApplied", "Recovery.Committed" };
    private readonly ITxnJournalPort? _journal;
    private readonly SessionRevisionVectorStore? _revisions;

    public TxnRecoveryResolver(ITxnJournalPort? journal = null, SessionRevisionVectorStore? revisions = null)
    {
        _journal = journal;
        _revisions = revisions;
    }

    public TxnRecoveryResult Resolve(TxnRecord record, ITxnParticipantQueryPort queries)
    {
        if (record is null) return Failure(TxnCommitStatus.Fatal, "InvalidArgument", "Transaction is required.");
        if (queries is null) return Failure(TxnCommitStatus.Fatal, "InvalidArgument", "Participant query port is required.");

        if (record.State == CrossWorldTxnState.Committed)
        {
            return new TxnRecoveryResult(TxnCommitStatus.AlreadyCommitted, record.VoxelParticipant, record.EcsParticipant,
                record.ResultRevision, null, EmptyTrace);
        }
        if (record.State == CrossWorldTxnState.Aborted)
        {
            return new TxnRecoveryResult(TxnCommitStatus.Aborted, record.VoxelParticipant, record.EcsParticipant,
                null, null, EmptyTrace);
        }
        if (record.State == CrossWorldTxnState.Expired)
        {
            return new TxnRecoveryResult(TxnCommitStatus.Expired, record.VoxelParticipant, record.EcsParticipant,
                null, null, EmptyTrace);
        }

        bool intent = record.CommitIntentPersisted || record.State is CrossWorldTxnState.CommitIntent or CrossWorldTxnState.Indeterminate;
        if (!intent && _journal is not null)
        {
            TxnJournalQueryResult journal;
            try
            {
                journal = _journal.Query(record.SessionId, record.TxnId);
            }
            catch (Exception ex)
            {
                return Failure(TxnCommitStatus.Fatal, "PanicBoundary", ex.Message);
            }

            if (journal.Status == TxnJournalQueryStatus.Retryable)
            {
                return new TxnRecoveryResult(TxnCommitStatus.Retryable, record.VoxelParticipant, record.EcsParticipant,
                    null, CoordinationFailure.Retryable("QueueFull", "Transaction journal query is unavailable."), EmptyTrace);
            }
            if (journal.Status == TxnJournalQueryStatus.Fatal)
            {
                return Failure(TxnCommitStatus.Fatal, journal.GeneratedErrorId ?? "PanicBoundary",
                    "Transaction journal query failed; commit intent cannot be determined.");
            }

            bool committedMarker = false;
            foreach (Lumio.Gen.ContractTypes.TxnJournalRecord entry in journal.Records)
            {
                if (entry.RecordKind == Lumio.Gen.ContractTypes.TxnJournalRecordRecordKind.Committed &&
                    entry.CommitState == Lumio.Gen.ContractTypes.TxnJournalRecordCommitState.Committed &&
                    entry.DurabilityState == Lumio.Gen.ContractTypes.TxnJournalRecordDurabilityState.Durable)
                {
                    committedMarker = true;
                    break;
                }
            }
            if (committedMarker) return ConvergeCommittedMarker(record);

            foreach (Lumio.Gen.ContractTypes.TxnJournalRecord entry in journal.Records)
            {
                if (entry.RecordKind == Lumio.Gen.ContractTypes.TxnJournalRecordRecordKind.CommitIntent &&
                    entry.DurabilityState == Lumio.Gen.ContractTypes.TxnJournalRecordDurabilityState.Durable) intent = true;
            }
        }

        if (!intent)
        {
            // No durable intent means the transaction is still safely abortable.
            TxnTransitionResult aborted = record.Abort("ValidationFailed");
            if (!aborted.Succeeded)
                return Failure(TxnCommitStatus.Fatal, aborted.Failure?.GeneratedErrorId ?? "InternalInvariant",
                    "Transaction could not be safely aborted before durable intent.");
            return new TxnRecoveryResult(TxnCommitStatus.Aborted, record.VoxelParticipant, record.EcsParticipant,
                null, null, EmptyTrace);
        }

        TxnRecoveryResult transitionResult = EnterRecoveryState(record);
        if (transitionResult.Failure is not null) return transitionResult;

        TxnParticipantQueryResult voxel = QuerySafely(queries, record, TxnParticipantKind.VoxelCommit);
        TxnParticipantQueryResult ecs = QuerySafely(queries, record, TxnParticipantKind.EcsCommandBufferCommit);
        if (!voxel.Available || !ecs.Available)
        {
            if (!MarkParticipantSafely(record, TxnParticipantKind.VoxelCommit,
                    voxel.Available ? voxel.State : TxnParticipantState.Unknown, out TxnRecoveryResult markerFailure))
                return markerFailure;
            if (!MarkParticipantSafely(record, TxnParticipantKind.EcsCommandBufferCommit,
                    ecs.Available ? ecs.State : TxnParticipantState.Unknown, out markerFailure))
                return markerFailure;
            return Unknown(record, StableError(voxel.GeneratedErrorId ?? ecs.GeneratedErrorId));
        }

        if (!MarkParticipantSafely(record, TxnParticipantKind.VoxelCommit, voxel.State, out TxnRecoveryResult voxelFailure))
            return voxelFailure;
        if (!MarkParticipantSafely(record, TxnParticipantKind.EcsCommandBufferCommit, ecs.State, out TxnRecoveryResult ecsFailure))
            return ecsFailure;
        if (voxel.State == TxnParticipantState.Applied && ecs.State == TxnParticipantState.Applied)
        {
            SessionRevisionVectorView? revision = voxel.ResultRevision ?? ecs.ResultRevision;
            if (revision is not null && _revisions is not null)
            {
                RevisionAdvanceResult advance = _revisions.AdvanceCommitted(revision);
                if (!advance.Succeeded)
                {
                    return Failure(TxnCommitStatus.Fatal, advance.Failure?.GeneratedErrorId ?? "RevisionConflict",
                        "Recovered participant revision could not be advanced.");
                }
            }

            TxnTransitionResult transition = record.TryTransition(CrossWorldTxnState.Committed);
            if (!transition.Succeeded) return Failure(TxnCommitStatus.Fatal, "InternalInvariant", "Unable to converge recovered transaction.");
            if (revision is not null)
            {
                TxnTransitionResult resultRevision = record.MarkResultRevision(revision);
                if (!resultRevision.Succeeded)
                    return Failure(TxnCommitStatus.Fatal, resultRevision.Failure?.GeneratedErrorId ?? "InternalInvariant",
                        "Recovered result revision could not be recorded.");
            }
            return new TxnRecoveryResult(TxnCommitStatus.Committed, record.VoxelParticipant, record.EcsParticipant,
                revision, null, ProvenTrace);
        }

        return Unknown(record, "PanicBoundary");
    }

    public TxnRecoveryResult Recover(TxnRecord record, ITxnParticipantQueryPort queries) => Resolve(record, queries);

    private static TxnRecoveryResult EnterRecoveryState(TxnRecord record)
    {
        if (record.State == CrossWorldTxnState.Created)
        {
            TxnTransitionResult prepared = record.TryTransition(CrossWorldTxnState.Prepared);
            if (!prepared.Succeeded)
                return Failure(TxnCommitStatus.Fatal, prepared.Failure?.GeneratedErrorId ?? "InternalInvariant",
                    "Unable to reconstruct prepared transaction state.");
        }

        if (record.State == CrossWorldTxnState.Prepared)
        {
            TxnTransitionResult intent = record.TryTransition(CrossWorldTxnState.CommitIntent);
            if (!intent.Succeeded)
                return Failure(TxnCommitStatus.Fatal, intent.Failure?.GeneratedErrorId ?? "InternalInvariant",
                    "Unable to reconstruct durable commit intent state.");
        }

        if (record.State == CrossWorldTxnState.CommitIntent)
        {
            TxnTransitionResult indeterminate = record.TryTransition(CrossWorldTxnState.Indeterminate);
            if (!indeterminate.Succeeded)
                return Failure(TxnCommitStatus.Fatal, indeterminate.Failure?.GeneratedErrorId ?? "InternalInvariant",
                    "Unable to enter recovery state.");
        }

        return new TxnRecoveryResult(TxnCommitStatus.Indeterminate, record.VoxelParticipant,
            record.EcsParticipant, null, null, EmptyTrace);
    }

    private static TxnParticipantQueryResult QuerySafely(
        ITxnParticipantQueryPort queries,
        TxnRecord record,
        TxnParticipantKind participant)
    {
        try
        {
            return queries.Query(record.SessionId, record.TxnId, participant);
        }
        catch (Exception)
        {
            return TxnParticipantQueryResult.Unknown("PanicBoundary");
        }
    }

    private static bool MarkParticipantSafely(
        TxnRecord record,
        TxnParticipantKind participant,
        TxnParticipantState state,
        out TxnRecoveryResult failure)
    {
        TxnTransitionResult marked = record.MarkParticipant(participant, state);
        if (marked.Succeeded)
        {
            failure = default;
            return true;
        }

        failure = Failure(TxnCommitStatus.Fatal, marked.Failure?.GeneratedErrorId ?? "InternalInvariant",
            "Participant query returned a state that cannot be recorded.");
        return false;
    }

    private static TxnRecoveryResult ConvergeCommittedMarker(TxnRecord record)
    {
        TxnRecoveryResult entered = EnterRecoveryState(record);
        if (entered.Failure is not null) return entered;
        if (!MarkParticipantSafely(record, TxnParticipantKind.VoxelCommit, TxnParticipantState.Applied,
                out TxnRecoveryResult voxelFailure)) return voxelFailure;
        if (!MarkParticipantSafely(record, TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Applied,
                out TxnRecoveryResult ecsFailure)) return ecsFailure;
        TxnTransitionResult committed = record.TryTransition(CrossWorldTxnState.Committed);
        if (!committed.Succeeded)
            return Failure(TxnCommitStatus.Fatal, committed.Failure?.GeneratedErrorId ?? "InternalInvariant",
                "Durable committed marker could not be applied to the transaction state.");
        return new TxnRecoveryResult(TxnCommitStatus.Committed, record.VoxelParticipant, record.EcsParticipant,
            record.ResultRevision, null, ProvenTrace);
    }

    private static TxnRecoveryResult Unknown(TxnRecord record, string errorId) =>
        new(TxnCommitStatus.Indeterminate, record.VoxelParticipant, record.EcsParticipant, null,
            CoordinationFailure.Infrastructure(errorId, "Participant state is not proven."), EmptyTrace);

    private static TxnRecoveryResult Failure(TxnCommitStatus status, string errorId, string detail) =>
        new(status, TxnParticipantState.Unknown, TxnParticipantState.Unknown, null,
            CoordinationFailure.Fatal(errorId, detail), EmptyTrace);

    private static string StableError(string? errorId) => errorId switch
    {
        "QueueFull" or "PanicBoundary" or "InternalInvariant" or "RevisionConflict" or "InvalidArgument" => errorId,
        _ => "PanicBoundary"
    };
}
