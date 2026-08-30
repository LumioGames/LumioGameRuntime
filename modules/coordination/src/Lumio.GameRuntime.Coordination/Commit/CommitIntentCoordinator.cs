using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Coordination;

public readonly record struct TxnCommitResult(
    TxnCommitStatus Status,
    TxnParticipantState VoxelParticipant,
    TxnParticipantState EcsParticipant,
    SessionRevisionVectorView? ResultRevision,
    IReadOnlyList<string> Trace,
    CoordinationFailure? Failure,
    TxnRecord? Record = null)
{
    public bool Succeeded => Status is TxnCommitStatus.Committed or TxnCommitStatus.AlreadyCommitted;

    public static TxnCommitResult Retryable(TxnRecord record, IReadOnlyList<string> trace, CoordinationFailure failure) =>
        new(TxnCommitStatus.Retryable, record.VoxelParticipant, record.EcsParticipant, null, trace, failure, record);
}

/// <summary>Durable intent gate and fixed Voxel-then-ECS apply algorithm.</summary>
public sealed class CommitIntentCoordinator
{
    private readonly object _gate = new();
    private readonly SessionRevisionVectorStore _revisions;
    private readonly ITxnJournalPort _journal;
    private readonly ParticipantApplyCoordinator _participants;
    private readonly Action<string>? _sessionFault;
    private JournalChainState? _journalChain;

    public CommitIntentCoordinator(
        SessionRevisionVectorStore revisions,
        ITxnJournalPort journal,
        IVoxelWorldPort voxel,
        EcsCommandCommitExecutor ecs,
        Action<string>? sessionFault = null)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _participants = new ParticipantApplyCoordinator(voxel ?? throw new ArgumentNullException(nameof(voxel)), ecs ?? throw new ArgumentNullException(nameof(ecs)));
        _sessionFault = sessionFault;
    }

    public CommitIntentCoordinator(
        SessionRevisionVectorStore revisions,
        ITxnJournalPort journal,
        ParticipantApplyCoordinator participants,
        Action<string>? sessionFault = null)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _participants = participants ?? throw new ArgumentNullException(nameof(participants));
        _sessionFault = sessionFault;
    }

    public TxnCommitResult Commit(CrossWorldPreparedTxn prepared) =>
        prepared is null
            ? new TxnCommitResult(TxnCommitStatus.Fatal, TxnParticipantState.NotStarted, TxnParticipantState.NotStarted, null,
                Array.Empty<string>(), CoordinationFailure.Fatal("InvalidArgument", "Prepared transaction is required."))
            : Commit(prepared.Record, null, prepared);

    public TxnCommitResult Commit(TxnRecord record, SessionRevisionVectorView? resultRevision = null, CrossWorldPreparedTxn? leases = null)
    {
        // Commit includes the process-local journal chain cursor. Keep the
        // whole barrier operation serialized so concurrent retries cannot
        // interleave sequence/hash-chain updates or apply participants twice.
        lock (_gate)
        {
            return CommitCore(record, resultRevision, leases);
        }
    }

    private TxnCommitResult CommitCore(TxnRecord record, SessionRevisionVectorView? resultRevision, CrossWorldPreparedTxn? leases)
    {
        if (record is null)
            return new TxnCommitResult(TxnCommitStatus.Fatal, TxnParticipantState.NotStarted, TxnParticipantState.NotStarted, null,
                Array.Empty<string>(), CoordinationFailure.Fatal("InvalidArgument", "Transaction is required."));

        switch (record.State)
        {
            case CrossWorldTxnState.Committed:
                return Result(TxnCommitStatus.AlreadyCommitted, record, resultRevision, Array.Empty<string>(), null);
            case CrossWorldTxnState.Aborted:
                return Result(TxnCommitStatus.Aborted, record, null, Array.Empty<string>(), null);
            case CrossWorldTxnState.Expired:
                return Result(TxnCommitStatus.Expired, record, null, Array.Empty<string>(), null);
            case CrossWorldTxnState.CommitIntent:
            case CrossWorldTxnState.Indeterminate:
                return Result(TxnCommitStatus.Indeterminate, record, null, Array.Empty<string>(),
                    CoordinationFailure.Infrastructure("PanicBoundary", "Commit intent exists; participant recovery is required."));
            case CrossWorldTxnState.Created:
                return Result(TxnCommitStatus.Fatal, record, null, Array.Empty<string>(),
                    CoordinationFailure.Rejected("InvalidArgument", "Transaction is not prepared."));
        }

        if (record.PreparedGameDelta is null || !record.PreparedGameDelta.VerifyForApply())
            return Fatal(record, Array.Empty<string>(), CoordinationFailure.Fatal("InternalInvariant", "Prepared game delta is not valid for commit."));

        var trace = new List<string>();
        TxnJournalAppendResult intent;
        try
        {
            intent = Append(record, TxnJournalRecordRecordKind.CommitIntent, "commit-intent", TxnJournalRecordCommitState.Pending);
        }
        catch (Exception ex)
        {
            return Result(TxnCommitStatus.Fatal, record, null, trace, CoordinationFailure.Infrastructure("PanicBoundary", ex.Message));
        }
        if (!intent.IsDurable)
        {
            CoordinationFailure failure = intent.Status == TxnJournalAppendStatus.Backpressured
                ? CoordinationFailure.Retryable(intent.GeneratedErrorId ?? "QueueFull", "CommitIntent journal is backpressured.")
                : CoordinationFailure.Fatal(intent.GeneratedErrorId ?? "PanicBoundary", "CommitIntent journal failed.");
            if (failure.Class == CoordinationFailureClass.Fatal) return Fatal(record, trace, failure);
            return TxnCommitResult.Retryable(record, trace, failure);
        }

        trace.Add("Journal.CommitIntent.Durable");
        TxnTransitionResult intentTransition = record.MarkCommitIntentPersisted();
        if (!intentTransition.Succeeded)
            return Fatal(record, trace, intentTransition.Failure ?? CoordinationFailure.Fatal("InternalInvariant", "Unable to enter CommitIntent."));

        VoxelCommitParticipantResult voxel;
        try { voxel = _participants.ApplyVoxel(record); }
        catch (Exception) {
            record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Unknown);
            return Indeterminate(record, trace, "PanicBoundary");
        }
        trace.Add("Voxel.Apply");
        if (voxel.Status == VoxelCommitParticipantStatus.Rejected)
        {
            record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Failed);
            record.TryTransition(CrossWorldTxnState.Indeterminate);
            return Fatal(record, trace, CoordinationFailure.Fatal("PanicBoundary", "Participant rejected after durable CommitIntent."));
        }

        if (voxel.Status is VoxelCommitParticipantStatus.Faulted or VoxelCommitParticipantStatus.Indeterminate)
        {
            record.MarkParticipant(
                TxnParticipantKind.VoxelCommit,
                voxel.Status == VoxelCommitParticipantStatus.Faulted ? TxnParticipantState.Failed : TxnParticipantState.Unknown);
            return Indeterminate(record, trace, voxel.GeneratedErrorId ?? "PanicBoundary");
        }

        TxnJournalAppendResult voxelMarker;
        try { voxelMarker = Append(record, TxnJournalRecordRecordKind.ParticipantMarker, "voxel-marker", TxnJournalRecordCommitState.Pending); }
        catch (Exception) { return Indeterminate(record, trace, "PanicBoundary"); }
        if (!voxelMarker.IsDurable)
        {
            record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Unknown);
            return Indeterminate(record, trace, voxelMarker.GeneratedErrorId ?? "QueueFull");
        }
        trace.Add("Journal.VoxelMarker.Durable");
        TxnTransitionResult voxelMarkerState = record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Applied);
        if (!voxelMarkerState.Succeeded)
            return Fatal(record, trace, voxelMarkerState.Failure ?? CoordinationFailure.Fatal("InternalInvariant", "Unable to record voxel marker."));

        CommandApplyReceipt ecs;
        try { ecs = _participants.ApplyEcs(record); }
        catch (Exception) {
            record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Unknown);
            return Indeterminate(record, trace, "PanicBoundary");
        }
        trace.Add("ECS.Apply");
        if (!ecs.IsApplied)
        {
            record.MarkParticipant(
                TxnParticipantKind.EcsCommandBufferCommit,
                ecs.Status is CommandApplyStatus.Faulted or CommandApplyStatus.InfrastructureFault
                    ? TxnParticipantState.Failed
                    : TxnParticipantState.Unknown);
            return Indeterminate(record, trace, ecs.GeneratedErrorId ?? "PanicBoundary");
        }

        TxnJournalAppendResult ecsMarker;
        try { ecsMarker = Append(record, TxnJournalRecordRecordKind.ParticipantMarker, "ecs-marker", TxnJournalRecordCommitState.Pending); }
        catch (Exception) { return Indeterminate(record, trace, "PanicBoundary"); }
        if (!ecsMarker.IsDurable)
        {
            record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Unknown);
            return Indeterminate(record, trace, ecsMarker.GeneratedErrorId ?? "QueueFull");
        }
        trace.Add("Journal.EcsMarker.Durable");
        TxnTransitionResult ecsMarkerState = record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Applied);
        if (!ecsMarkerState.Succeeded)
            return Fatal(record, trace, ecsMarkerState.Failure ?? CoordinationFailure.Fatal("InternalInvariant", "Unable to record ECS marker."));

        TxnJournalAppendResult terminal;
        try { terminal = Append(record, TxnJournalRecordRecordKind.Committed, "committed", TxnJournalRecordCommitState.Committed); }
        catch (Exception) { return Indeterminate(record, trace, "PanicBoundary"); }
        if (!terminal.IsDurable) return Indeterminate(record, trace, terminal.GeneratedErrorId ?? "QueueFull");
        trace.Add("Journal.Committed.Durable");

        SessionRevisionVectorView revision;
        try { revision = resultRevision ?? NextRevision(record); }
        catch (Exception ex) { record.TryTransition(CrossWorldTxnState.Indeterminate); return Fatal(record, trace, CoordinationFailure.Infrastructure("PanicBoundary", ex.Message)); }
        RevisionAdvanceResult advance = _revisions.AdvanceCommitted(revision);
        if (!advance.Succeeded)
        {
            record.TryTransition(CrossWorldTxnState.Indeterminate);
            return Fatal(record, trace, advance.Failure ?? CoordinationFailure.Fatal("RevisionConflict", "Unable to advance result revision."));
        }

        TxnTransitionResult committedTransition = record.TryTransition(CrossWorldTxnState.Committed);
        if (!committedTransition.Succeeded)
            return Fatal(record, trace, committedTransition.Failure ?? CoordinationFailure.Fatal("InternalInvariant", "Unable to mark transaction committed."));
        record.MarkResultRevision(revision);
        leases?.VoxelReservation.Commit();
        leases?.GameReservation.Commit();
        trace.Add("Revision.Advance");
        return Result(TxnCommitStatus.Committed, record, revision, trace, null);
    }

    public TxnCommitResult Commit(TxnRecord record, SessionRevisionVectorView resultRevision) => Commit(record, resultRevision, null);

    private TxnJournalAppendResult Append(TxnRecord record, TxnJournalRecordRecordKind kind, string suffix, TxnJournalRecordCommitState state)
    {
        if (_journalChain is null && _journal is ITxnJournalChainTail tail && tail.TryGetTail(out ulong tailSequence, out string tailChecksum))
        {
            _journalChain = new JournalChainState(tailSequence, tailChecksum);
        }

        ulong sequence = _journalChain is null ? 1UL : checked(_journalChain.Sequence + 1UL);
        string previousHash = _journalChain?.Checksum ?? new string('0', 64);
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(string.Concat(record.RequestDigest, "|", suffix));
        TxnJournalRecord journalRecord = TxnJournalRecordFactory.Create(
            record.SessionId,
            record.GameReleaseId,
            record.TickId,
            record.TxnId,
            kind,
            string.Concat(record.TxnId, ":", suffix),
            state,
            TxnJournalRecordDurabilityState.Durable,
            record.CommandId,
            sequence,
            previousHash,
            payload);
        TxnJournalAppendResult result = _journal.Append(in journalRecord);
        if (result.IsDurable && !result.AlreadyPresent)
        {
            ulong actualSequence = result.RecordSequence == 0UL ? sequence : result.RecordSequence;
            string actualChecksum = TxnJournalRecordFactory.ComputeChecksum(
                actualSequence,
                previousHash,
                journalRecord.PayloadHash,
                journalRecord.IdempotencyKey);
            _journalChain = new JournalChainState(actualSequence, actualChecksum);
        }
        else if (result.IsDurable && result.AlreadyPresent)
        {
            RefreshJournalChain(record);
        }

        return result;
    }

    private void RefreshJournalChain(TxnRecord record)
    {
        if (_journal is ITxnJournalChainTail tail && tail.TryGetTail(out ulong tailSequence, out string tailChecksum))
        {
            _journalChain = new JournalChainState(tailSequence, tailChecksum);
            return;
        }

        // A persistence adapter may not expose its tail capability. Query the
        // transaction and recover the matching idempotency record so a replay
        // still advances from the durable checksum rather than a speculative
        // local value.
        try
        {
            TxnJournalQueryResult query = _journal.Query(record.SessionId, record.TxnId);
            if (query.Status != TxnJournalQueryStatus.Found) return;
            string keyPrefix = string.Concat(record.TxnId, ":");
            TxnJournalRecord? latest = null;
            foreach (TxnJournalRecord candidate in query.Records)
            {
                if (!candidate.IdempotencyKey.StartsWith(keyPrefix, StringComparison.Ordinal)) continue;
                if (latest is null || candidate.RecordSeq > latest.RecordSeq) latest = candidate;
            }

            if (latest is not null) _journalChain = new JournalChainState(latest.RecordSeq, latest.Checksum);
        }
        catch (Exception)
        {
            // The original append was durable; leave the existing cursor in
            // place and let the next append surface a deterministic chain
            // failure instead of fabricating a checksum.
        }
    }

    private sealed record JournalChainState(ulong Sequence, string Checksum);

    private SessionRevisionVectorView NextRevision(TxnRecord record)
    {
        SessionRevisionVectorView current = _revisions.Read();
        return new SessionRevisionVectorView(
            Math.Max(current.TickId, record.TickId),
            checked(current.GameRevision + 1UL),
            checked(current.VoxelWorldRevision + 1UL),
            current.ChunkRevisionSet,
            checked(current.ReplicationRevision + 1UL),
            current.ConfigRevision,
            current.SchemaEpoch);
    }

    private static TxnCommitResult Result(TxnCommitStatus status, TxnRecord record, SessionRevisionVectorView? revision,
        IReadOnlyList<string> trace, CoordinationFailure? failure) =>
        new(status, record.VoxelParticipant, record.EcsParticipant, revision ?? record.ResultRevision, trace, failure, record);

    private TxnCommitResult Fatal(TxnRecord record, IReadOnlyList<string> trace, CoordinationFailure failure)
    {
        try { _sessionFault?.Invoke(failure.GeneratedErrorId); }
        catch (Exception) { }
        return Result(TxnCommitStatus.Fatal, record, null, trace, failure);
    }

    private TxnCommitResult Indeterminate(TxnRecord record, IReadOnlyList<string> trace, string errorId)
    {
        record.TryTransition(CrossWorldTxnState.Indeterminate);
        CoordinationFailure failure = CoordinationFailure.Infrastructure(errorId, "Participant result is not proven.");
        try { _sessionFault?.Invoke(errorId); }
        catch (Exception) { }
        return Result(TxnCommitStatus.Indeterminate, record, null, trace, failure);
    }
}
