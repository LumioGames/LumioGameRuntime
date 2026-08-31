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

/// <summary>Session-scoped durable intent and participant commit authority.</summary>
public sealed class CommitIntentCoordinator
{
    private readonly SessionCoordinationContext _context;
    private readonly SessionRevisionVectorStore _revisions;
    private readonly ITxnJournalPort _journal;
    private readonly ITxnResultEvidencePort _evidence;
    private readonly ParticipantApplyCoordinator _participants;
    private readonly Action<string>? _sessionFault;

    internal CommitIntentCoordinator(
        SessionRevisionVectorStore revisions,
        ITxnJournalPort journal,
        IVoxelWorldPort voxel,
        EcsCommandCommitExecutor ecs,
        Action<string>? sessionFault = null)
        : this(revisions, journal, voxel, ecs, null, new MissingTxnResultEvidencePort(), sessionFault)
    {
    }

    internal CommitIntentCoordinator(
        SessionRevisionVectorStore revisions,
        ITxnJournalPort journal,
        IVoxelWorldPort voxel,
        EcsCommandCommitExecutor ecs,
        ITxnResultEvidencePort evidence,
        Action<string>? sessionFault = null)
        : this(revisions, journal, voxel, ecs, null, evidence, sessionFault)
    {
    }

    internal CommitIntentCoordinator(
        SessionRevisionVectorStore revisions,
        ITxnJournalPort journal,
        IVoxelWorldPort voxel,
        EcsCommandCommitExecutor ecs,
        IEcsCommandCommitRevisionPort? ecsRevision,
        Action<string>? sessionFault = null)
        : this(revisions, journal, voxel, ecs, ecsRevision, new MissingTxnResultEvidencePort(), sessionFault)
    {
    }

    internal CommitIntentCoordinator(
        SessionRevisionVectorStore revisions,
        ITxnJournalPort journal,
        IVoxelWorldPort voxel,
        EcsCommandCommitExecutor ecs,
        IEcsCommandCommitRevisionPort? ecsRevision,
        ITxnResultEvidencePort evidence,
        Action<string>? sessionFault = null)
        : this(
            revisions,
            journal,
            new ParticipantApplyCoordinator(
                voxel ?? throw new ArgumentNullException(nameof(voxel)),
                ecs ?? throw new ArgumentNullException(nameof(ecs)),
                ecsRevision),
            sessionFault,
            evidence)
    {
    }

    internal CommitIntentCoordinator(
        SessionRevisionVectorStore revisions,
        ITxnJournalPort journal,
        IVoxelWorldPort voxel,
        CommandModule command,
        IEcsCommandCommitRevisionPort? ecsRevision,
        ITxnResultEvidencePort evidence,
        Action<string>? sessionFault = null)
        : this(
            revisions,
            journal,
            new ParticipantApplyCoordinator(
                voxel ?? throw new ArgumentNullException(nameof(voxel)),
                command ?? throw new ArgumentNullException(nameof(command)),
                ecsRevision),
            sessionFault,
            evidence)
    {
    }

    internal CommitIntentCoordinator(
        SessionRevisionVectorStore revisions,
        ITxnJournalPort journal,
        ParticipantApplyCoordinator participants,
        Action<string>? sessionFault = null,
        ITxnResultEvidencePort? evidence = null)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _context = SessionCoordinationContext.For(revisions);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _evidence = evidence ?? new MissingTxnResultEvidencePort();
        _participants = participants ?? throw new ArgumentNullException(nameof(participants));
        _sessionFault = sessionFault;
    }

    public TxnCommitResult Commit(CrossWorldPreparedTxn prepared)
    {
        if (prepared is null)
        {
            return new TxnCommitResult(
                TxnCommitStatus.Fatal,
                TxnParticipantState.NotStarted,
                TxnParticipantState.NotStarted,
                null,
                Array.Empty<string>(),
                CoordinationFailure.Fatal("InvalidArgument", "Prepared transaction is required."));
        }

        TxnRecord record = prepared.Record;
        TxnIdentity identity;
        try { identity = TxnIdentity.From(record); }
        catch (ArgumentException ex)
        {
            return Fatal(record, Array.Empty<string>(), CoordinationFailure.Fatal("InvalidArgument", ex.Message));
        }

        if (!prepared.TryClaimForCommit(identity, out CoordinationFailure? leaseFailure))
        {
            return record.State switch
            {
                CrossWorldTxnState.Aborted => Result(TxnCommitStatus.Aborted, record, null, Array.Empty<string>(), null),
                CrossWorldTxnState.Expired => Result(TxnCommitStatus.Expired, record, null, Array.Empty<string>(), null),
                _ => Result(TxnCommitStatus.Fatal, record, null, Array.Empty<string>(), leaseFailure)
            };
        }

        if (!_context.TryEnter(identity, out TxnAuthorityOperation operation, out CoordinationFailure? admissionFailure))
        {
            prepared.ReleaseCommitClaim();
            return admissionFailure?.Class == CoordinationFailureClass.Retryable
                ? TxnCommitResult.Retryable(record, Array.Empty<string>(), admissionFailure)
                : Fatal(record, Array.Empty<string>(), admissionFailure ??
                    CoordinationFailure.Fatal("InternalInvariant", "Transaction authority admission failed."));
        }

        try
        {
            using (operation)
            {
                return CommitCore(prepared, operation);
            }
        }
        finally { prepared.ReleaseCommitClaim(); }
    }

    internal TxnCommitResult Commit(
        TxnRecord record,
        SessionRevisionVectorView? resultRevision = null,
        CrossWorldPreparedTxn? leases = null)
    {
        if (resultRevision is not null)
            return Fatal(record, Array.Empty<string>(),
                CoordinationFailure.Rejected("InvalidArgument", "Result revision must come from both participants."));
        if (leases is null || !ReferenceEquals(leases.Record, record))
            return Fatal(record, Array.Empty<string>(),
                CoordinationFailure.Rejected("CapabilityMissing", "An active prepared transaction capability is required."));
        return Commit(leases);
    }

    internal TxnCommitResult Commit(TxnRecord record, SessionRevisionVectorView resultRevision) =>
        Commit(record, resultRevision, null);

    private TxnCommitResult CommitCore(CrossWorldPreparedTxn prepared, TxnAuthorityOperation operation)
    {
        TxnRecord record = prepared.Record;
        var trace = new List<string>();

        if (record.State == CrossWorldTxnState.Committed)
            return TryConvergeCertificate(prepared, operation, trace);
        if (record.State == CrossWorldTxnState.Aborted)
            return Result(TxnCommitStatus.Aborted, record, null, trace, null);
        if (record.State == CrossWorldTxnState.Expired)
            return Result(TxnCommitStatus.Expired, record, null, trace, null);
        if (record.State is CrossWorldTxnState.CommitIntent or CrossWorldTxnState.Indeterminate)
            return TryConvergeCertificate(prepared, operation, trace);
        if (record.State != CrossWorldTxnState.Prepared || record.PreparedGameDelta is null ||
            !record.PreparedGameDelta.VerifyForApply())
        {
            return Fatal(record, trace,
                CoordinationFailure.Fatal("InternalInvariant", "Prepared game delta is not valid for commit."));
        }

        JournalAppendOutcome intentAppend = AppendStage(operation.Identity, TxnJournalStage.CommitIntent);
        if (!intentAppend.Succeeded || intentAppend.Proof is null)
        {
            return FromJournalFailure(record, trace, intentAppend);
        }
        TxnJournalProof intent = intentAppend.Proof;
        trace.Add("Journal.CommitIntent.Durable");
        TxnTransitionResult intentTransition = record.MarkCommitIntentPersisted(intent);
        if (!intentTransition.Succeeded)
            return Fatal(record, trace, intentTransition.Failure ??
                CoordinationFailure.Fatal("InternalInvariant", "Unable to publish durable commit intent."));
        prepared.MarkIntentPersisted();

        VoxelCommitParticipantResult voxel;
        try { voxel = _participants.ApplyVoxel(record); }
        catch (Exception ex)
        {
            record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Unknown);
            return Indeterminate(record, trace, "PanicBoundary", ex.Message);
        }
        trace.Add("Voxel.Apply");
        if (voxel.Status is not (VoxelCommitParticipantStatus.Applied or VoxelCommitParticipantStatus.AlreadyApplied))
        {
            record.MarkParticipant(
                TxnParticipantKind.VoxelCommit,
                voxel.Status == VoxelCommitParticipantStatus.Indeterminate
                    ? TxnParticipantState.Unknown
                    : TxnParticipantState.Failed);
            return Indeterminate(record, trace, voxel.GeneratedErrorId ?? "PanicBoundary",
                "Voxel participant did not return an applied receipt.");
        }
        if (!ValidateParticipantRevision(record, voxel.ResultRevision, out CoordinationFailure? voxelRevisionFailure))
        {
            record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Unknown);
            return FatalOrIndeterminate(record, trace, voxelRevisionFailure!);
        }

        SessionRevisionVectorView revision = voxel.ResultRevision!;
        JournalAppendOutcome voxelAppend = AppendStage(
            operation.Identity,
            TxnJournalStage.VoxelMarker,
            intent.Checksum,
            revision.CanonicalDigestHex);
        if (!voxelAppend.Succeeded || voxelAppend.Proof is null)
            return FromJournalFailureAfterIntent(record, trace, voxelAppend, TxnParticipantKind.VoxelCommit);
        TxnJournalProof voxelMarker = voxelAppend.Proof;
        trace.Add("Journal.VoxelMarker.Durable");
        TxnTransitionResult voxelState = record.MarkParticipant(
            TxnParticipantKind.VoxelCommit,
            TxnParticipantState.Applied);
        if (!voxelState.Succeeded)
            return Fatal(record, trace, voxelState.Failure ??
                CoordinationFailure.Fatal("InternalInvariant", "Voxel marker state could not be published."));

        EcsParticipantApplyResult ecs;
        try { ecs = _participants.ApplyEcsResult(record); }
        catch (Exception ex)
        {
            record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Unknown);
            return Indeterminate(record, trace, "PanicBoundary", ex.Message);
        }
        trace.Add("ECS.Apply");
        if (!ecs.IsApplied || ecs.Receipt.TickId != record.TickId ||
            !ecs.Receipt.CanonicalDigest.Span.SequenceEqual(record.PreparedGameDelta.CanonicalDigest.Span))
        {
            record.MarkParticipant(
                TxnParticipantKind.EcsCommandBufferCommit,
                ecs.Receipt.Status is CommandApplyStatus.Indeterminate
                    ? TxnParticipantState.Unknown
                    : TxnParticipantState.Failed);
            return Indeterminate(record, trace, ecs.Receipt.GeneratedErrorId ?? "EvidenceDigestMismatch",
                "ECS participant receipt does not match the prepared transaction.");
        }
        if (!ValidateParticipantRevision(record, ecs.ResultRevision, out CoordinationFailure? ecsRevisionFailure) ||
            !revision.Equals(ecs.ResultRevision))
        {
            record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Unknown);
            return FatalOrIndeterminate(record, trace,
                ecsRevisionFailure ?? CoordinationFailure.Infrastructure(
                    "RevisionConflict",
                    "Participant result revisions disagree."));
        }

        RevisionReservationResult reserved = _revisions.TryReserveStrict(record.ExpectedRevision, revision, operation);
        if (!reserved.Succeeded || reserved.Reservation is null)
            return FromReservationFailure(record, trace, reserved);
        RevisionAdvanceReservation reservation = reserved.Reservation;

        TxnResultEvidence evidence;
        try
        {
            evidence = new TxnResultEvidence(record, revision);
            TxnResultEvidenceWriteResult written = _evidence.Write(in evidence);
            if (!written.IsDurable)
            {
                reservation.Release();
                record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Unknown);
                return written.Status == TxnResultEvidenceWriteStatus.Retryable
                    ? Indeterminate(record, trace, written.GeneratedErrorId ?? "QueueFull",
                        "Result evidence is temporarily unavailable.")
                    : Fatal(record, trace, CoordinationFailure.Fatal(
                        written.GeneratedErrorId ?? "EvidenceMissing",
                        "Durable result evidence could not be written."));
            }
        }
        catch (Exception ex)
        {
            reservation.Release();
            record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Unknown);
            return Indeterminate(record, trace, "PanicBoundary", ex.Message);
        }
        trace.Add("Evidence.ResultRevision.Durable");

        JournalAppendOutcome ecsAppend = AppendStage(
            operation.Identity,
            TxnJournalStage.EcsMarker,
            intent.Checksum,
            voxelMarker.Checksum,
            revision.CanonicalDigestHex,
            evidence.CanonicalDigestHex);
        if (!ecsAppend.Succeeded || ecsAppend.Proof is null)
        {
            reservation.Release();
            return FromJournalFailureAfterIntent(record, trace, ecsAppend, TxnParticipantKind.EcsCommandBufferCommit);
        }
        TxnJournalProof ecsMarker = ecsAppend.Proof;
        trace.Add("Journal.EcsMarker.Durable");
        TxnTransitionResult ecsState = record.MarkParticipant(
            TxnParticipantKind.EcsCommandBufferCommit,
            TxnParticipantState.Applied);
        if (!ecsState.Succeeded)
        {
            reservation.Release();
            return Fatal(record, trace, ecsState.Failure ??
                CoordinationFailure.Fatal("InternalInvariant", "ECS marker state could not be published."));
        }

        JournalAppendOutcome terminalAppend = AppendStage(
            operation.Identity,
            TxnJournalStage.Committed,
            intent.Checksum,
            voxelMarker.Checksum,
            ecsMarker.Checksum,
            evidence.CanonicalDigestHex,
            revision.CanonicalDigestHex);
        if (!terminalAppend.Succeeded || terminalAppend.Proof is null)
        {
            reservation.Release();
            return FromJournalFailureAfterIntent(record, trace, terminalAppend, null);
        }
        TxnJournalProof terminal = terminalAppend.Proof;
        trace.Add("Journal.Committed.Durable");

        var certificate = new TxnCommitCertificate(
            operation,
            intent,
            voxelMarker,
            ecsMarker,
            terminal,
            evidence,
            revision);
        if (!record.CanPublishCommitted(revision, out CoordinationFailure? publicationFailure))
        {
            reservation.Release();
            return Fatal(record, trace, publicationFailure!);
        }

        RevisionAdvanceResult advanced = reservation.Commit();
        if (!advanced.Succeeded)
            return Fatal(record, trace, advanced.Failure ??
                CoordinationFailure.Fatal("RevisionConflict", "Result revision could not be finalized."));
        TxnTransitionResult published = record.PublishCommitted(certificate);
        if (!published.Succeeded)
            return Fatal(record, trace, published.Failure ??
                CoordinationFailure.Fatal("InternalInvariant", "Committed record publication failed."));
        if (!prepared.CommitReservations())
            return Fatal(record, trace,
                CoordinationFailure.Fatal("InternalInvariant", "Prepared reservations could not be finalized."));

        trace.Add("Revision.Advance");
        return Result(TxnCommitStatus.Committed, record, revision, trace, null);
    }

    private TxnCommitResult TryConvergeCertificate(
        CrossWorldPreparedTxn prepared,
        TxnAuthorityOperation operation,
        List<string> trace)
    {
        TxnRecord record = prepared.Record;
        TxnRecoveryResult recovered = new TxnRecoveryResolver(_journal, _revisions, _evidence)
            .RecoverWithinAuthority(record, null, operation);
        if (recovered.Succeeded && !prepared.CommitReservations())
            return Fatal(record, trace,
                CoordinationFailure.Fatal("InternalInvariant", "Prepared reservations could not be finalized."));
        return recovered.ToCommitResult(record);
    }

    private JournalAppendOutcome AppendStage(
        TxnIdentity identity,
        TxnJournalStage stage,
        params string[] links)
    {
        TxnJournalQueryResult existing;
        try { existing = _journal.Query(identity.SessionId, identity.TxnId); }
        catch (Exception ex)
        {
            return JournalAppendOutcome.Fatal("PanicBoundary", ex.Message);
        }
        if (existing.Status == TxnJournalQueryStatus.Retryable)
            return JournalAppendOutcome.Retryable(existing.GeneratedErrorId ?? "QueueFull", "Journal query is unavailable.");
        if (existing.Status == TxnJournalQueryStatus.Fatal)
            return JournalAppendOutcome.Fatal(existing.GeneratedErrorId ?? "PanicBoundary", "Journal query failed.");
        if (existing.Status == TxnJournalQueryStatus.Found)
        {
            if (!TxnJournalAuthority.TryValidateRecordSet(
                    existing.Records,
                    identity,
                    out CoordinationFailure? recordSetFailure))
                return JournalAppendOutcome.FromFailure(recordSetFailure!);
            if (!TxnJournalAuthority.TryFind(existing.Records, identity, stage, links,
                    out TxnJournalProof? existingProof, out CoordinationFailure? existingFailure))
                return JournalAppendOutcome.FromFailure(existingFailure!);
            if (existingProof is not null) return JournalAppendOutcome.Durable(existingProof, alreadyPresent: true);
        }

        TxnJournalTailResult tail;
        try { tail = _journal.ReadTail(); }
        catch (Exception ex)
        {
            return JournalAppendOutcome.Fatal("PanicBoundary", ex.Message);
        }
        if (!tail.IsAvailable || tail.Checksum is null)
        {
            return tail.Status == TxnJournalTailStatus.Retryable
                ? JournalAppendOutcome.Retryable(tail.GeneratedErrorId ?? "QueueFull", "Journal tail is unavailable.")
                : JournalAppendOutcome.Fatal(tail.GeneratedErrorId ?? "CapabilityMissing", "Journal tail capability is required.");
        }

        ulong sequence;
        try { sequence = checked(tail.RecordSequence + 1UL); }
        catch (OverflowException)
        {
            return JournalAppendOutcome.Fatal("InternalInvariant", "Journal sequence was exhausted.");
        }
        TxnJournalRecord row = TxnJournalAuthority.Create(identity, stage, sequence, tail.Checksum, links);
        TxnJournalAppendResult appended;
        try { appended = _journal.Append(in row); }
        catch (Exception ex)
        {
            return JournalAppendOutcome.Fatal("PanicBoundary", ex.Message);
        }
        if (!appended.IsDurable)
        {
            return appended.Status == TxnJournalAppendStatus.Backpressured
                ? JournalAppendOutcome.Retryable(appended.GeneratedErrorId ?? "QueueFull", "Journal append is backpressured.")
                : JournalAppendOutcome.Fatal(appended.GeneratedErrorId ?? "PanicBoundary", "Journal append failed.");
        }
        if (appended.AlreadyPresent)
        {
            TxnJournalQueryResult duplicate = _journal.Query(identity.SessionId, identity.TxnId);
            CoordinationFailure? duplicateFailure = null;
            if (duplicate.Status != TxnJournalQueryStatus.Found ||
                !TxnJournalAuthority.TryValidateRecordSet(duplicate.Records, identity, out duplicateFailure) ||
                !TxnJournalAuthority.TryFind(duplicate.Records, identity, stage, links,
                    out TxnJournalProof? duplicateProof, out duplicateFailure) ||
                duplicateProof is null)
            {
                return JournalAppendOutcome.FromFailure(duplicateFailure ??
                    CoordinationFailure.Fatal("EvidenceDigestMismatch", "Duplicate journal receipt could not be verified."));
            }
            return JournalAppendOutcome.Durable(duplicateProof, alreadyPresent: true);
        }

        if (appended.RecordSequence != sequence ||
            !string.Equals(appended.RecordChecksum, row.Checksum, StringComparison.Ordinal) ||
            !string.Equals(appended.PreviousHash, row.PreviousHash, StringComparison.Ordinal))
        {
            return JournalAppendOutcome.Fatal("EvidenceDigestMismatch", "Journal append receipt was not normalized to the requested tail.");
        }
        if (!TxnJournalAuthority.TryValidate(row, identity, stage, links,
                out TxnJournalProof? proof, out CoordinationFailure? proofFailure) || proof is null)
            return JournalAppendOutcome.FromFailure(proofFailure!);
        return JournalAppendOutcome.Durable(proof, alreadyPresent: false);
    }

    private static bool ValidateParticipantRevision(
        TxnRecord record,
        SessionRevisionVectorView? revision,
        out CoordinationFailure? failure)
    {
        if (revision is null)
        {
            failure = CoordinationFailure.Infrastructure("RevisionConflict", "Participant result revision is missing.");
            return false;
        }
        if (revision.TickId != record.TickId)
        {
            failure = CoordinationFailure.Fatal("RevisionConflict", "Participant result TickId does not match the transaction.");
            return false;
        }
        if (revision.SchemaEpoch != record.ExpectedRevision.SchemaEpoch)
        {
            failure = CoordinationFailure.Fatal("InternalInvariant", "Participant result schema epoch does not match.");
            return false;
        }
        if (revision.Equals(record.ExpectedRevision) || !revision.IsMonotonicFrom(record.ExpectedRevision))
        {
            failure = CoordinationFailure.Infrastructure("RevisionConflict", "Participant result does not strictly advance the expectation.");
            return false;
        }
        failure = null;
        return true;
    }

    private TxnCommitResult FromJournalFailure(
        TxnRecord record,
        IReadOnlyList<string> trace,
        JournalAppendOutcome outcome) =>
        outcome.IsRetryable
            ? TxnCommitResult.Retryable(record, trace, outcome.Failure!)
            : Fatal(record, trace, outcome.Failure!);

    private TxnCommitResult FromJournalFailureAfterIntent(
        TxnRecord record,
        IReadOnlyList<string> trace,
        JournalAppendOutcome outcome,
        TxnParticipantKind? participant)
    {
        if (participant is TxnParticipantKind value)
            record.MarkParticipant(value, TxnParticipantState.Unknown);
        return Indeterminate(
            record,
            trace,
            outcome.Failure?.GeneratedErrorId ?? "PanicBoundary",
            outcome.Failure?.Detail ?? "A durable journal stage is unavailable.");
    }

    private TxnCommitResult FromReservationFailure(
        TxnRecord record,
        IReadOnlyList<string> trace,
        RevisionReservationResult reserved)
    {
        record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Unknown);
        if (reserved.Status == RevisionReservationStatus.Fatal)
            return Fatal(record, trace, reserved.Failure ??
                CoordinationFailure.Fatal("InternalInvariant", "Revision reservation failed."));
        return Indeterminate(record, trace, reserved.Failure?.GeneratedErrorId ?? "RevisionConflict",
            reserved.Failure?.Detail ?? "Revision reservation was rejected.");
    }

    private TxnCommitResult FatalOrIndeterminate(
        TxnRecord record,
        IReadOnlyList<string> trace,
        CoordinationFailure failure) =>
        failure.Class == CoordinationFailureClass.Fatal
            ? Fatal(record, trace, failure)
            : Indeterminate(record, trace, failure.GeneratedErrorId, failure.Detail);

    private TxnCommitResult Fatal(TxnRecord? record, IReadOnlyList<string> trace, CoordinationFailure failure)
    {
        try { _sessionFault?.Invoke(failure.GeneratedErrorId); }
        catch (Exception) { }
        return record is null
            ? new TxnCommitResult(
                TxnCommitStatus.Fatal,
                TxnParticipantState.NotStarted,
                TxnParticipantState.NotStarted,
                null,
                trace,
                failure)
            : Result(TxnCommitStatus.Fatal, record, null, trace, failure);
    }

    private TxnCommitResult Indeterminate(
        TxnRecord record,
        IReadOnlyList<string> trace,
        string errorId,
        string detail)
    {
        record.TryTransition(CrossWorldTxnState.Indeterminate);
        CoordinationFailure failure = CoordinationFailure.Infrastructure(errorId, detail);
        try { _sessionFault?.Invoke(errorId); }
        catch (Exception) { }
        return Result(TxnCommitStatus.Indeterminate, record, null, trace, failure);
    }

    private static TxnCommitResult Result(
        TxnCommitStatus status,
        TxnRecord record,
        SessionRevisionVectorView? revision,
        IReadOnlyList<string> trace,
        CoordinationFailure? failure) =>
        new(status, record.VoxelParticipant, record.EcsParticipant, revision, trace, failure, record);

    private readonly record struct JournalAppendOutcome(
        bool Succeeded,
        bool IsRetryable,
        bool AlreadyPresent,
        TxnJournalProof? Proof,
        CoordinationFailure? Failure)
    {
        internal static JournalAppendOutcome Durable(TxnJournalProof proof, bool alreadyPresent) =>
            new(true, false, alreadyPresent, proof, null);

        internal static JournalAppendOutcome Retryable(string errorId, string detail) =>
            new(false, true, false, null, CoordinationFailure.Retryable(errorId, detail));

        internal static JournalAppendOutcome Fatal(string errorId, string detail) =>
            new(false, false, false, null, CoordinationFailure.Fatal(errorId, detail));

        internal static JournalAppendOutcome FromFailure(CoordinationFailure failure) =>
            failure.Class == CoordinationFailureClass.Retryable
                ? new JournalAppendOutcome(false, true, false, null, failure)
                : new JournalAppendOutcome(false, false, false, null, failure);
    }
}
