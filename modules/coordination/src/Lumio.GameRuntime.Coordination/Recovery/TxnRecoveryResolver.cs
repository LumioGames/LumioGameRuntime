using System;
using System.Collections.Generic;
using Lumio.Gen.ContractTypes;

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

/// <summary>Builds and validates a durable recovery candidate before publishing any local authority state.</summary>
public sealed class TxnRecoveryResolver
{
    private static readonly IReadOnlyList<string> EmptyTrace = Array.Empty<string>();
    private static readonly IReadOnlyList<string> ProvenTrace = new[]
    {
        "Recovery.ProvenApplied",
        "Evidence.ResultRevision.Durable",
        "Recovery.Committed"
    };
    private static readonly IReadOnlyList<string> RestoredTrace = new[]
    {
        "Evidence.ResultRevision.Durable",
        "Recovery.Committed"
    };
    private readonly ITxnJournalPort? _journal;
    private readonly SessionRevisionVectorStore? _revisions;
    private readonly ITxnResultEvidencePort? _evidence;

    public TxnRecoveryResolver(
        ITxnJournalPort? journal = null,
        SessionRevisionVectorStore? revisions = null,
        ITxnResultEvidencePort? evidence = null)
    {
        _journal = journal;
        _revisions = revisions;
        _evidence = evidence;
    }

    public TxnRecoveryResult Resolve(TxnRecord record, ITxnParticipantQueryPort? queries)
    {
        if (record is null)
            return Failure(TxnCommitStatus.Fatal, "InvalidArgument", "Transaction is required.");
        if (_journal is null)
            return Unknown(record, "CapabilityMissing", "A durable journal is required for recovery.");
        if (_revisions is null)
            return Unknown(record, "CapabilityMissing", "A session revision authority is required for recovery.");
        if (_evidence is null)
            return Unknown(record, "EvidenceMissing", "A durable result evidence capability is required for recovery.");

        TxnIdentity identity;
        try { identity = TxnIdentity.From(record); }
        catch (ArgumentException ex)
        {
            return Failure(TxnCommitStatus.Fatal, "InvalidArgument", ex.Message);
        }

        SessionCoordinationContext context = SessionCoordinationContext.For(_revisions);
        if (!context.TryEnter(identity, out TxnAuthorityOperation operation, out CoordinationFailure? admissionFailure))
        {
            return admissionFailure?.Class == CoordinationFailureClass.Retryable
                ? new TxnRecoveryResult(
                    TxnCommitStatus.Retryable,
                    record.VoxelParticipant,
                    record.EcsParticipant,
                    null,
                    admissionFailure,
                    EmptyTrace)
                : Failure(
                    TxnCommitStatus.Fatal,
                    admissionFailure?.GeneratedErrorId ?? "InternalInvariant",
                    admissionFailure?.Detail ?? "Recovery authority admission failed.");
        }

        using (operation)
        {
            return ResolveCore(record, queries, operation);
        }
    }

    public TxnRecoveryResult Recover(TxnRecord record, ITxnParticipantQueryPort? queries) =>
        Resolve(record, queries);

    internal TxnRecoveryResult RecoverWithinAuthority(
        TxnRecord record,
        ITxnParticipantQueryPort? queries,
        TxnAuthorityOperation operation)
    {
        if (record is null || operation is null || _journal is null || _revisions is null || _evidence is null ||
            !operation.Owns(_revisions) || !operation.Identity.Matches(record))
            return Failure(TxnCommitStatus.Fatal, "InvalidArgument", "A matching live recovery authority is required.");
        return ResolveCore(record, queries, operation);
    }

    private TxnRecoveryResult ResolveCore(
        TxnRecord record,
        ITxnParticipantQueryPort? queries,
        TxnAuthorityOperation operation)
    {
        TxnJournalQueryResult query;
        try { query = _journal!.Query(record.SessionId, record.TxnId); }
        catch (Exception ex)
        {
            return Failure(TxnCommitStatus.Fatal, "PanicBoundary", ex.Message);
        }
        if (query.Status == TxnJournalQueryStatus.Retryable)
        {
            return new TxnRecoveryResult(
                TxnCommitStatus.Retryable,
                record.VoxelParticipant,
                record.EcsParticipant,
                null,
                CoordinationFailure.Retryable(
                    query.GeneratedErrorId ?? "QueueFull",
                    "Transaction journal query is unavailable."),
                EmptyTrace);
        }
        if (query.Status == TxnJournalQueryStatus.Fatal)
            return Failure(
                TxnCommitStatus.Fatal,
                query.GeneratedErrorId ?? "PanicBoundary",
                "Transaction journal query failed.");

        IReadOnlyList<TxnJournalRecord> records = query.Records;
        if (!TxnJournalAuthority.TryValidateRecordSet(
                records,
                operation.Identity,
                out CoordinationFailure? recordSetFailure))
            return FailureFrom(record, recordSetFailure!);
        if (!TxnJournalAuthority.TryFind(
                records,
                operation.Identity,
                TxnJournalStage.CommitIntent,
                Array.Empty<string>(),
                out TxnJournalProof? intent,
                out CoordinationFailure? intentFailure))
        {
            return FailureFrom(record, intentFailure!);
        }
        if (intent is null && records.Count != 0)
        {
            return Failure(
                TxnCommitStatus.Fatal,
                "EvidenceDigestMismatch",
                "Journal records exist for the transaction but none is the exact CommitIntent proof.");
        }
        if (intent is null)
            return WithoutIntent(record);

        if (record.State is CrossWorldTxnState.Aborted or CrossWorldTxnState.Expired ||
            record.VoxelParticipant == TxnParticipantState.Failed ||
            record.EcsParticipant == TxnParticipantState.Failed)
        {
            return Failure(
                TxnCommitStatus.Fatal,
                "InternalInvariant",
                "Local terminal or failed participant state contradicts durable commit intent.");
        }

        EvidenceOutcome evidenceOutcome = ReadEvidence(record);
        if (evidenceOutcome.Failure is not null && !evidenceOutcome.IsMissing)
            return FailureFrom(record, evidenceOutcome.Failure);

        if (evidenceOutcome.Evidence is TxnResultEvidence durableEvidence)
        {
            CertificateOutcome terminal = BuildCertificate(
                records,
                record,
                operation,
                intent,
                durableEvidence);
            if (terminal.Certificate is not null)
                return PublishRestored(record, terminal.Certificate, operation);
            if (terminal.Failure is not null)
                return FailureFrom(record, terminal.Failure);
            if (HasStageKey(records, operation.Identity, TxnJournalStage.Committed))
            {
                return Failure(
                    TxnCommitStatus.Fatal,
                    "EvidenceDigestMismatch",
                    "A terminal marker exists without a complete exact commit certificate.");
            }
        }
        else if (HasStageKey(records, operation.Identity, TxnJournalStage.Committed))
        {
            return Unknown(record, "EvidenceMissing", "A terminal marker has no exact durable result evidence.");
        }

        return RecoverIncomplete(
            records,
            record,
            queries,
            operation,
            intent,
            evidenceOutcome.Evidence);
    }

    private TxnRecoveryResult RecoverIncomplete(
        IReadOnlyList<TxnJournalRecord> records,
        TxnRecord record,
        ITxnParticipantQueryPort? queries,
        TxnAuthorityOperation operation,
        TxnJournalProof intent,
        TxnResultEvidence? durableEvidence)
    {
        bool hasVoxelKey = HasStageKey(records, operation.Identity, TxnJournalStage.VoxelMarker);
        bool hasEcsKey = HasStageKey(records, operation.Identity, TxnJournalStage.EcsMarker);
        if (hasEcsKey && !hasVoxelKey)
        {
            return Failure(
                TxnCommitStatus.Fatal,
                "EvidenceDigestMismatch",
                "An ECS marker cannot precede the Voxel marker.");
        }
        if (durableEvidence is null && hasVoxelKey && hasEcsKey)
            return Unknown(record, "EvidenceMissing", "Participant markers have no durable result vector.");

        TxnJournalProof? voxelMarker = null;
        TxnJournalProof? ecsMarker = null;
        if (durableEvidence is not null)
        {
            if (!TxnJournalAuthority.TryFind(
                    records,
                    operation.Identity,
                    TxnJournalStage.VoxelMarker,
                    new[] { intent.Checksum, durableEvidence.ResultRevision.CanonicalDigestHex },
                    out voxelMarker,
                    out CoordinationFailure? voxelMarkerFailure))
                return FailureFrom(record, voxelMarkerFailure!);
            if (voxelMarker is not null &&
                !TxnJournalAuthority.TryFind(
                    records,
                    operation.Identity,
                    TxnJournalStage.EcsMarker,
                    new[]
                    {
                        intent.Checksum,
                        voxelMarker.Checksum,
                        durableEvidence.ResultRevision.CanonicalDigestHex,
                        durableEvidence.CanonicalDigestHex
                    },
                    out ecsMarker,
                    out CoordinationFailure? ecsMarkerFailure))
                return FailureFrom(record, ecsMarkerFailure!);
        }

        if (voxelMarker is not null && voxelMarker.RecordSequence <= intent.RecordSequence)
            return Failure(TxnCommitStatus.Fatal, "EvidenceDigestMismatch", "Voxel marker precedes durable intent.");
        if (ecsMarker is not null &&
            (voxelMarker is null || ecsMarker.RecordSequence <= voxelMarker.RecordSequence))
            return Failure(TxnCommitStatus.Fatal, "EvidenceDigestMismatch", "ECS marker precedes the Voxel marker.");

        bool pendingVoxelValidation = durableEvidence is null && hasVoxelKey;
        bool needVoxel = voxelMarker is null && !pendingVoxelValidation;
        bool needEcs = ecsMarker is null;
        if ((needVoxel || needEcs) && queries is null)
            return Unknown(record, "CapabilityMissing", "Participant status query capability is required.");

        TxnParticipantQueryResult voxel = needVoxel
            ? QuerySafely(queries!, record, TxnParticipantKind.VoxelCommit)
            : TxnParticipantQueryResult.Applied(durableEvidence?.ResultRevision);
        TxnParticipantQueryResult ecs = needEcs
            ? QuerySafely(queries!, record, TxnParticipantKind.EcsCommandBufferCommit)
            : TxnParticipantQueryResult.Applied(durableEvidence?.ResultRevision);
        if (!voxel.Available || !ecs.Available)
        {
            return new TxnRecoveryResult(
                TxnCommitStatus.Indeterminate,
                voxel.Available ? voxel.State : TxnParticipantState.Unknown,
                ecs.Available ? ecs.State : TxnParticipantState.Unknown,
                null,
                CoordinationFailure.Infrastructure(
                    StableError(voxel.GeneratedErrorId ?? ecs.GeneratedErrorId),
                    "Participant status is unavailable."),
                EmptyTrace);
        }
        if (voxel.State != TxnParticipantState.Applied || ecs.State != TxnParticipantState.Applied)
        {
            return new TxnRecoveryResult(
                TxnCommitStatus.Indeterminate,
                voxel.State,
                ecs.State,
                null,
                CoordinationFailure.Infrastructure("PanicBoundary", "Both participants are not proven applied."),
                EmptyTrace);
        }
        SessionRevisionVectorView? revision = durableEvidence?.ResultRevision;
        if (needVoxel)
        {
            if (!ValidateRevision(record, voxel.ResultRevision, out CoordinationFailure? voxelFailure))
                return FailureFrom(record, voxelFailure!);
            revision = voxel.ResultRevision;
        }
        if (needEcs)
        {
            if (!ValidateRevision(record, ecs.ResultRevision, out CoordinationFailure? ecsFailure))
                return FailureFrom(record, ecsFailure!);
            if (revision is not null && !revision.Equals(ecs.ResultRevision))
                return FailureFrom(record,
                    CoordinationFailure.Infrastructure("RevisionConflict", "Participant result revisions disagree."));
            revision = ecs.ResultRevision;
        }
        if (revision is null)
            return Unknown(record, "EvidenceMissing", "No participant result revision can validate durable markers.");
        if (durableEvidence is not null && !durableEvidence.ResultRevision.Equals(revision))
            return Failure(
                TxnCommitStatus.Fatal,
                "EvidenceDigestMismatch",
                "Participant result does not match durable result evidence.");
        if (pendingVoxelValidation)
        {
            if (!TxnJournalAuthority.TryFind(
                    records,
                    operation.Identity,
                    TxnJournalStage.VoxelMarker,
                    new[] { intent.Checksum, revision.CanonicalDigestHex },
                    out voxelMarker,
                    out CoordinationFailure? pendingVoxelFailure))
                return FailureFrom(record, pendingVoxelFailure!);
            if (voxelMarker is null)
                return Failure(TxnCommitStatus.Fatal, "EvidenceDigestMismatch", "Voxel marker could not be validated.");
        }
        if (!record.CanPublishCommitted(revision, out CoordinationFailure? candidateFailure))
            return FailureFrom(record, candidateFailure!);

        RevisionReservationResult reserved = _revisions!.TryReserveStrict(
            record.ExpectedRevision,
            revision,
            operation);
        if (!reserved.Succeeded || reserved.Reservation is null)
            return FailureFromReservation(record, reserved);
        RevisionAdvanceReservation reservation = reserved.Reservation;

        TxnResultEvidence evidence = durableEvidence ?? new TxnResultEvidence(record, revision);
        if (durableEvidence is null)
        {
            TxnResultEvidenceWriteResult written;
            try { written = _evidence!.Write(in evidence); }
            catch (Exception ex)
            {
                reservation.Release();
                return Failure(TxnCommitStatus.Fatal, "PanicBoundary", ex.Message);
            }
            if (!written.IsDurable)
            {
                reservation.Release();
                return written.Status == TxnResultEvidenceWriteStatus.Retryable
                    ? new TxnRecoveryResult(
                        TxnCommitStatus.Retryable,
                        voxel.State,
                        ecs.State,
                        null,
                        CoordinationFailure.Retryable(
                            written.GeneratedErrorId ?? "QueueFull",
                            "Result evidence write is unavailable."),
                        EmptyTrace)
                    : Failure(
                        TxnCommitStatus.Fatal,
                        written.GeneratedErrorId ?? "EvidenceMissing",
                        "Durable result evidence could not be written.");
            }
        }

        if (voxelMarker is null)
        {
            AppendOutcome appended = AppendStage(
                operation.Identity,
                TxnJournalStage.VoxelMarker,
                intent.Checksum,
                revision.CanonicalDigestHex);
            if (!appended.Succeeded || appended.Proof is null)
            {
                reservation.Release();
                return FailureFromAppend(record, appended);
            }
            voxelMarker = appended.Proof;
        }
        if (ecsMarker is null)
        {
            AppendOutcome appended = AppendStage(
                operation.Identity,
                TxnJournalStage.EcsMarker,
                intent.Checksum,
                voxelMarker.Checksum,
                revision.CanonicalDigestHex,
                evidence.CanonicalDigestHex);
            if (!appended.Succeeded || appended.Proof is null)
            {
                reservation.Release();
                return FailureFromAppend(record, appended);
            }
            ecsMarker = appended.Proof;
        }

        AppendOutcome terminal = AppendStage(
            operation.Identity,
            TxnJournalStage.Committed,
            intent.Checksum,
            voxelMarker.Checksum,
            ecsMarker.Checksum,
            evidence.CanonicalDigestHex,
            revision.CanonicalDigestHex);
        if (!terminal.Succeeded || terminal.Proof is null)
        {
            reservation.Release();
            return FailureFromAppend(record, terminal);
        }

        var certificate = new TxnCommitCertificate(
            operation,
            intent,
            voxelMarker,
            ecsMarker,
            terminal.Proof,
            evidence,
            revision);
        if (!record.CanPublishCommitted(revision, out CoordinationFailure? publicationFailure))
        {
            reservation.Release();
            return FailureFrom(record, publicationFailure!);
        }
        RevisionAdvanceResult advanced = reservation.Commit();
        if (!advanced.Succeeded)
            return FailureFrom(record, advanced.Failure ??
                CoordinationFailure.Fatal("RevisionConflict", "Recovered revision could not be finalized."));
        TxnTransitionResult published = record.PublishCommitted(certificate);
        if (!published.Succeeded)
            return FailureFrom(record, published.Failure ??
                CoordinationFailure.Fatal("InternalInvariant", "Recovered transaction could not be published."));

        return new TxnRecoveryResult(
            TxnCommitStatus.Committed,
            TxnParticipantState.Applied,
            TxnParticipantState.Applied,
            revision,
            null,
            ProvenTrace);
    }

    private TxnRecoveryResult PublishRestored(
        TxnRecord record,
        TxnCommitCertificate certificate,
        TxnAuthorityOperation operation)
    {
        if (!record.CanPublishCommitted(certificate.ResultRevision, out CoordinationFailure? publicationFailure))
            return FailureFrom(record, publicationFailure!);
        RevisionAdvanceResult restored = _revisions!.RestoreCommitted(
            record.ExpectedRevision,
            certificate.ResultRevision,
            operation);
        if (!restored.Succeeded)
            return FailureFrom(record, restored.Failure ??
                CoordinationFailure.Fatal("RevisionConflict", "Committed revision could not be restored."));
        bool wasCommitted = record.State == CrossWorldTxnState.Committed;
        TxnTransitionResult published = record.PublishCommitted(certificate);
        if (!published.Succeeded)
            return FailureFrom(record, published.Failure ??
                CoordinationFailure.Fatal("InternalInvariant", "Committed recovery candidate could not be published."));

        return new TxnRecoveryResult(
            wasCommitted ? TxnCommitStatus.AlreadyCommitted : TxnCommitStatus.Committed,
            TxnParticipantState.Applied,
            TxnParticipantState.Applied,
            certificate.ResultRevision,
            null,
            RestoredTrace);
    }

    private static CertificateOutcome BuildCertificate(
        IReadOnlyList<TxnJournalRecord> records,
        TxnRecord record,
        TxnAuthorityOperation operation,
        TxnJournalProof intent,
        TxnResultEvidence evidence)
    {
        string resultDigest = evidence.ResultRevision.CanonicalDigestHex;
        if (!TxnJournalAuthority.TryFind(
                records,
                operation.Identity,
                TxnJournalStage.VoxelMarker,
                new[] { intent.Checksum, resultDigest },
                out TxnJournalProof? voxel,
                out CoordinationFailure? voxelFailure))
            return CertificateOutcome.Failed(voxelFailure!);
        if (voxel is null) return default;
        if (!TxnJournalAuthority.TryFind(
                records,
                operation.Identity,
                TxnJournalStage.EcsMarker,
                new[] { intent.Checksum, voxel.Checksum, resultDigest, evidence.CanonicalDigestHex },
                out TxnJournalProof? ecs,
                out CoordinationFailure? ecsFailure))
            return CertificateOutcome.Failed(ecsFailure!);
        if (ecs is null) return default;
        if (!TxnJournalAuthority.TryFind(
                records,
                operation.Identity,
                TxnJournalStage.Committed,
                new[] { intent.Checksum, voxel.Checksum, ecs.Checksum, evidence.CanonicalDigestHex, resultDigest },
                out TxnJournalProof? terminal,
                out CoordinationFailure? terminalFailure))
            return CertificateOutcome.Failed(terminalFailure!);
        if (terminal is null) return default;
        if (!(intent.RecordSequence < voxel.RecordSequence &&
              voxel.RecordSequence < ecs.RecordSequence &&
              ecs.RecordSequence < terminal.RecordSequence))
        {
            return CertificateOutcome.Failed(
                CoordinationFailure.Fatal("EvidenceDigestMismatch", "Journal certificate order or chain links are invalid."));
        }

        return new CertificateOutcome(
            new TxnCommitCertificate(operation, intent, voxel, ecs, terminal, evidence, evidence.ResultRevision),
            null);
    }

    private EvidenceOutcome ReadEvidence(TxnRecord record)
    {
        TxnResultEvidenceIdentity identity = EvidenceIdentity(record);
        TxnResultEvidenceReadResult read;
        try { read = _evidence!.Read(in identity); }
        catch (Exception ex)
        {
            return EvidenceOutcome.Failed(
                CoordinationFailure.Infrastructure("PanicBoundary", ex.Message));
        }
        if (read.Status == TxnResultEvidenceReadStatus.NotFound)
            return EvidenceOutcome.Missing();
        if (read.Status == TxnResultEvidenceReadStatus.Retryable)
            return EvidenceOutcome.Failed(
                CoordinationFailure.Retryable(
                    read.GeneratedErrorId ?? "QueueFull",
                    "Result evidence read is unavailable."));
        if (!read.IsFound || read.Evidence is null || !read.Evidence.Matches(record))
        {
            return EvidenceOutcome.Failed(
                CoordinationFailure.Fatal(
                    read.GeneratedErrorId ?? "EvidenceDigestMismatch",
                    "Result evidence does not match the transaction identity."));
        }
        if (!ValidateRevision(record, read.Evidence.ResultRevision, out CoordinationFailure? validationFailure))
            return EvidenceOutcome.Failed(validationFailure!);
        return new EvidenceOutcome(read.Evidence, false, null);
    }

    private AppendOutcome AppendStage(
        TxnIdentity identity,
        TxnJournalStage stage,
        params string[] links)
    {
        TxnJournalQueryResult existing;
        try { existing = _journal!.Query(identity.SessionId, identity.TxnId); }
        catch (Exception ex)
        {
            return AppendOutcome.Fatal("PanicBoundary", ex.Message);
        }
        if (existing.Status == TxnJournalQueryStatus.Retryable)
            return AppendOutcome.Retryable(existing.GeneratedErrorId ?? "QueueFull", "Journal query is unavailable.");
        if (existing.Status == TxnJournalQueryStatus.Fatal)
            return AppendOutcome.Fatal(existing.GeneratedErrorId ?? "PanicBoundary", "Journal query failed.");
        if (existing.Status == TxnJournalQueryStatus.Found)
        {
            if (!TxnJournalAuthority.TryValidateRecordSet(
                    existing.Records,
                    identity,
                    out CoordinationFailure? recordSetFailure))
                return AppendOutcome.FromFailure(recordSetFailure!);
            if (!TxnJournalAuthority.TryFind(
                    existing.Records,
                    identity,
                    stage,
                    links,
                    out TxnJournalProof? existingProof,
                    out CoordinationFailure? existingFailure))
                return AppendOutcome.FromFailure(existingFailure!);
            if (existingProof is not null) return AppendOutcome.Durable(existingProof);
        }

        TxnJournalTailResult tail;
        try { tail = _journal.ReadTail(); }
        catch (Exception ex)
        {
            return AppendOutcome.Fatal("PanicBoundary", ex.Message);
        }
        if (!tail.IsAvailable || tail.Checksum is null)
        {
            return tail.Status == TxnJournalTailStatus.Retryable
                ? AppendOutcome.Retryable(tail.GeneratedErrorId ?? "QueueFull", "Journal tail is unavailable.")
                : AppendOutcome.Fatal(tail.GeneratedErrorId ?? "CapabilityMissing", "Journal tail capability is required.");
        }

        ulong sequence;
        try { sequence = checked(tail.RecordSequence + 1UL); }
        catch (OverflowException)
        {
            return AppendOutcome.Fatal("InternalInvariant", "Journal sequence was exhausted.");
        }
        TxnJournalRecord row = TxnJournalAuthority.Create(identity, stage, sequence, tail.Checksum, links);
        TxnJournalAppendResult appended;
        try { appended = _journal.Append(in row); }
        catch (Exception ex)
        {
            return AppendOutcome.Fatal("PanicBoundary", ex.Message);
        }
        if (!appended.IsDurable)
        {
            return appended.Status == TxnJournalAppendStatus.Backpressured
                ? AppendOutcome.Retryable(appended.GeneratedErrorId ?? "QueueFull", "Journal append is backpressured.")
                : AppendOutcome.Fatal(appended.GeneratedErrorId ?? "PanicBoundary", "Journal append failed.");
        }
        if (appended.AlreadyPresent)
        {
            TxnJournalQueryResult duplicate = _journal.Query(identity.SessionId, identity.TxnId);
            CoordinationFailure? duplicateFailure = null;
            if (duplicate.Status != TxnJournalQueryStatus.Found ||
                !TxnJournalAuthority.TryValidateRecordSet(duplicate.Records, identity, out duplicateFailure) ||
                !TxnJournalAuthority.TryFind(
                    duplicate.Records,
                    identity,
                    stage,
                    links,
                    out TxnJournalProof? duplicateProof,
                    out duplicateFailure) ||
                duplicateProof is null)
            {
                return AppendOutcome.FromFailure(duplicateFailure ??
                    CoordinationFailure.Fatal("EvidenceDigestMismatch", "Duplicate journal receipt could not be verified."));
            }
            return AppendOutcome.Durable(duplicateProof);
        }
        if (appended.RecordSequence != sequence ||
            !string.Equals(appended.RecordChecksum, row.Checksum, StringComparison.Ordinal) ||
            !string.Equals(appended.PreviousHash, row.PreviousHash, StringComparison.Ordinal))
        {
            return AppendOutcome.Fatal("EvidenceDigestMismatch", "Journal append receipt is not normalized to the requested tail.");
        }
        if (!TxnJournalAuthority.TryValidate(
                row,
                identity,
                stage,
                links,
                out TxnJournalProof? proof,
                out CoordinationFailure? proofFailure) ||
            proof is null)
            return AppendOutcome.FromFailure(proofFailure!);
        return AppendOutcome.Durable(proof);
    }

    private static TxnParticipantQueryResult QuerySafely(
        ITxnParticipantQueryPort queries,
        TxnRecord record,
        TxnParticipantKind participant)
    {
        try { return queries.Query(record.SessionId, record.TxnId, participant); }
        catch (Exception) { return TxnParticipantQueryResult.Unknown("PanicBoundary"); }
    }

    private static bool ValidateRevision(
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
            failure = CoordinationFailure.Fatal("RevisionConflict", "Result revision TickId does not match the transaction.");
            return false;
        }
        if (revision.SchemaEpoch != record.ExpectedRevision.SchemaEpoch)
        {
            failure = CoordinationFailure.Fatal("InternalInvariant", "Result revision schema epoch does not match.");
            return false;
        }
        if (revision.Equals(record.ExpectedRevision) || !revision.IsMonotonicFrom(record.ExpectedRevision))
        {
            failure = CoordinationFailure.Infrastructure("RevisionConflict", "Result revision does not strictly advance the expectation.");
            return false;
        }
        failure = null;
        return true;
    }

    private static bool HasStageKey(
        IReadOnlyList<TxnJournalRecord> records,
        TxnIdentity identity,
        TxnJournalStage stage)
    {
        string key = TxnJournalAuthority.Key(identity, stage);
        foreach (TxnJournalRecord record in records)
            if (string.Equals(record.IdempotencyKey, key, StringComparison.Ordinal)) return true;
        return false;
    }

    private static TxnRecoveryResult WithoutIntent(TxnRecord record)
    {
        if (record.State is CrossWorldTxnState.Created or CrossWorldTxnState.Prepared)
        {
            TxnTransitionResult aborted = record.Abort("ValidationFailed");
            if (aborted.Succeeded)
            {
                return new TxnRecoveryResult(
                    TxnCommitStatus.Aborted,
                    record.VoxelParticipant,
                    record.EcsParticipant,
                    null,
                    null,
                    EmptyTrace);
            }
        }
        return Unknown(
            record,
            "EvidenceMissing",
            "Local commit intent has no exact durable CommitIntent proof.");
    }

    private static TxnRecoveryResult FailureFromAppend(TxnRecord record, AppendOutcome outcome) =>
        outcome.IsRetryable
            ? new TxnRecoveryResult(
                TxnCommitStatus.Retryable,
                record.VoxelParticipant,
                record.EcsParticipant,
                null,
                outcome.Failure,
                EmptyTrace)
            : FailureFrom(record, outcome.Failure!);

    private static TxnRecoveryResult FailureFromReservation(
        TxnRecord record,
        RevisionReservationResult reserved) =>
        reserved.Status == RevisionReservationStatus.Rejected &&
        reserved.Failure?.Class == CoordinationFailureClass.Retryable
            ? new TxnRecoveryResult(
                TxnCommitStatus.Retryable,
                record.VoxelParticipant,
                record.EcsParticipant,
                null,
                reserved.Failure,
                EmptyTrace)
            : FailureFrom(
                record,
                reserved.Failure ??
                CoordinationFailure.Fatal("RevisionConflict", "Revision reservation failed."));

    private static TxnRecoveryResult FailureFrom(TxnRecord record, CoordinationFailure failure) =>
        failure.Class == CoordinationFailureClass.Retryable
            ? new TxnRecoveryResult(
                TxnCommitStatus.Retryable,
                record.VoxelParticipant,
                record.EcsParticipant,
                null,
                failure,
                EmptyTrace)
            : new TxnRecoveryResult(
                failure.Class == CoordinationFailureClass.Fatal
                    ? TxnCommitStatus.Fatal
                    : TxnCommitStatus.Indeterminate,
                record.VoxelParticipant,
                record.EcsParticipant,
                null,
                failure,
                EmptyTrace);

    private static TxnRecoveryResult Unknown(TxnRecord record, string errorId, string detail) =>
        new(
            TxnCommitStatus.Indeterminate,
            record.VoxelParticipant,
            record.EcsParticipant,
            null,
            CoordinationFailure.Infrastructure(errorId, detail),
            EmptyTrace);

    private static TxnRecoveryResult Failure(TxnCommitStatus status, string errorId, string detail) =>
        new(
            status,
            TxnParticipantState.Unknown,
            TxnParticipantState.Unknown,
            null,
            CoordinationFailure.Fatal(errorId, detail),
            EmptyTrace);

    private static TxnResultEvidenceIdentity EvidenceIdentity(TxnRecord record) =>
        new(
            record.SessionId,
            record.TxnId,
            record.CommandId,
            record.TickId,
            record.RequestDigest,
            record.ExpectedRevision.CanonicalDigestHex,
            record.GameReleaseId);

    private static string StableError(string? errorId) => errorId switch
    {
        "QueueFull" or "PanicBoundary" or "InternalInvariant" or "RevisionConflict" or
        "InvalidArgument" or "EvidenceMissing" or "EvidenceDigestMismatch" or "CapabilityMissing" => errorId,
        _ => "PanicBoundary"
    };

    private readonly record struct EvidenceOutcome(
        TxnResultEvidence? Evidence,
        bool IsMissing,
        CoordinationFailure? Failure)
    {
        internal static EvidenceOutcome Missing() => new(null, true, null);

        internal static EvidenceOutcome Failed(CoordinationFailure failure) => new(null, false, failure);
    }

    private readonly record struct CertificateOutcome(
        TxnCommitCertificate? Certificate,
        CoordinationFailure? Failure)
    {
        internal static CertificateOutcome Failed(CoordinationFailure failure) => new(null, failure);
    }

    private readonly record struct AppendOutcome(
        bool Succeeded,
        bool IsRetryable,
        TxnJournalProof? Proof,
        CoordinationFailure? Failure)
    {
        internal static AppendOutcome Durable(TxnJournalProof proof) => new(true, false, proof, null);

        internal static AppendOutcome Retryable(string errorId, string detail) =>
            new(false, true, null, CoordinationFailure.Retryable(errorId, detail));

        internal static AppendOutcome Fatal(string errorId, string detail) =>
            new(false, false, null, CoordinationFailure.Fatal(errorId, detail));

        internal static AppendOutcome FromFailure(CoordinationFailure failure) =>
            failure.Class == CoordinationFailureClass.Retryable
                ? new AppendOutcome(false, true, null, failure)
                : new AppendOutcome(false, false, null, failure);
    }
}
