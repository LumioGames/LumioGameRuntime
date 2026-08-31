using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Validation;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Replication.Projection;

public enum ProjectionStatus
{
    Succeeded,
    Rejected,
    Retryable,
    RequiresResync
}

public readonly record struct FullSnapshotProjectionResult(ProjectionStatus Status, FullSnapshotProjection? Snapshot, ReplicationFailure? Failure)
{
    public bool Succeeded => Status == ProjectionStatus.Succeeded && Snapshot is not null;
}

public readonly record struct DeltaProjectionResult(ProjectionStatus Status, DeltaProjection? Delta, ReplicationFailure? Failure)
{
    public bool Succeeded => Status == ProjectionStatus.Succeeded && Delta is not null;
}

public sealed class FullSnapshotProjection
{
    internal FullSnapshotProjection(string sessionId, string productId, string gameReleaseId, string snapshotId, RevisionVector revision, string mappingSetHash, string bodyJson, ulong sequence, string idempotencyKey)
    {
        SessionId = sessionId;
        ProductId = productId;
        GameReleaseId = gameReleaseId;
        SnapshotId = snapshotId;
        Revision = revision;
        TickId = revision.TickId;
        SchemaEpoch = revision.SchemaEpoch;
        MappingSetHash = mappingSetHash;
        BodyJson = bodyJson;
        Sequence = sequence;
        IdempotencyKey = idempotencyKey;
    }

    public string SessionId { get; }
    public string ProductId { get; }
    public string GameReleaseId { get; }
    public string SnapshotId { get; }
    public ulong TickId { get; }
    public RevisionVector Revision { get; }
    public ulong SchemaEpoch { get; }
    public string MappingSetHash { get; }
    public string BodyJson { get; }
    public ulong Sequence { get; }

    internal string IdempotencyKey { get; }

    internal FullSnapshotProjection WithSequence(ulong sequence) =>
        new(SessionId, ProductId, GameReleaseId, SnapshotId, Revision, MappingSetHash, BodyJson, sequence, IdempotencyKey);

    public ReplicationEnvelope ToEnvelope(string traceId, ulong length = 0)
    {
        if (!ReplicationValidation.IsIdentifier(traceId)) throw new ArgumentException("A valid trace ID is required.", nameof(traceId));
        ulong bodyLength = (ulong)Encoding.UTF8.GetByteCount(BodyJson);
        if (length == 0 || length < bodyLength) length = bodyLength;
        return new ReplicationEnvelope(
            SessionId,
            ProductId,
            GameReleaseId,
            1,
            length,
            Sequence,
            ReplicationEnvelopeMessageType.FullSnapshot,
            ReplicationEnvelopeReliability.Reliable,
            new ReplicationEnvelopeIntegrity(ReplicationEnvelopeIntegrityAlgorithm.SHA256, ReplicationValidation.Sha256Hex(Encoding.UTF8.GetBytes(BodyJson))),
            traceId,
            new ReplicationEnvelopeTransportPolicy(1_048_576, 65_536, 1_024, ReplicationEnvelopeTransportPolicyAuthBinding.SessionAdmission, ReplicationEnvelopeTransportPolicyErrorClass.Rejectable),
            new OpaqueJson(BodyJson));
    }
}

public sealed class DeltaProjection
{
    internal DeltaProjection(string sessionId, string productId, string gameReleaseId, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence, RevisionVector revision, string mappingSetHash, string bodyJson, ulong sequence, string idempotencyKey)
    {
        SessionId = sessionId;
        ProductId = productId;
        GameReleaseId = gameReleaseId;
        BaseSnapshotId = baseSnapshotId;
        FromRevision = fromRevision;
        ToRevision = toRevision;
        ConfirmationSequence = confirmationSequence;
        Revision = revision;
        MappingSetHash = mappingSetHash;
        BodyJson = bodyJson;
        Sequence = sequence;
        IdempotencyKey = idempotencyKey;
    }

    public string SessionId { get; }
    public string ProductId { get; }
    public string GameReleaseId { get; }
    public string BaseSnapshotId { get; }
    public ulong FromRevision { get; }
    public ulong ToRevision { get; }
    public ulong ConfirmationSequence { get; }
    public RevisionVector Revision { get; }
    public ulong SchemaEpoch => Revision.SchemaEpoch;
    public string MappingSetHash { get; }
    public string BodyJson { get; }
    public ulong Sequence { get; }

    internal string IdempotencyKey { get; }

    internal DeltaProjection WithSequence(ulong sequence) =>
        new(SessionId, ProductId, GameReleaseId, BaseSnapshotId, FromRevision, ToRevision,
            ConfirmationSequence, Revision, MappingSetHash, BodyJson, sequence, IdempotencyKey);

    public ReplicationEnvelope ToEnvelope(string traceId, ulong length = 0)
    {
        if (!ReplicationValidation.IsIdentifier(traceId)) throw new ArgumentException("A valid trace ID is required.", nameof(traceId));
        ulong bodyLength = (ulong)Encoding.UTF8.GetByteCount(BodyJson);
        if (length == 0 || length < bodyLength) length = bodyLength;
        return new ReplicationEnvelope(
            SessionId,
            ProductId,
            GameReleaseId,
            1,
            length,
            Sequence,
            ReplicationEnvelopeMessageType.Delta,
            ReplicationEnvelopeReliability.Reliable,
            new ReplicationEnvelopeIntegrity(ReplicationEnvelopeIntegrityAlgorithm.SHA256, ReplicationValidation.Sha256Hex(Encoding.UTF8.GetBytes(BodyJson))),
            traceId,
            new ReplicationEnvelopeTransportPolicy(1_048_576, 65_536, 1_024, ReplicationEnvelopeTransportPolicyAuthBinding.SessionAdmission, ReplicationEnvelopeTransportPolicyErrorClass.Rejectable),
            new OpaqueJson(BodyJson));
    }
}

public sealed class ReplicationProjection
{
    private readonly object _gate = new();
    private readonly ReplicationBudget _budget;
    private readonly ProjectionIdentityCache<FullSnapshotProjection> _fullSnapshotIdentities;
    private readonly ProjectionIdentityCache<DeltaProjection> _deltaIdentities;
    private readonly ReplaySequenceLedger _fullSnapshotReplay;
    private readonly ReplaySequenceLedger _deltaReplay;
    private ulong _nextSequence = 1;

    public ReplicationProjection(ReplicationBudget budget)
    {
        if (!budget.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));
        _budget = budget;
        _fullSnapshotIdentities = new ProjectionIdentityCache<FullSnapshotProjection>(budget);
        _deltaIdentities = new ProjectionIdentityCache<DeltaProjection>(budget);
        _fullSnapshotReplay = new ReplaySequenceLedger(budget);
        _deltaReplay = new ReplaySequenceLedger(budget);
    }

    public FullSnapshotProjectionResult BuildFullSnapshot(string sessionId, string productId, string gameReleaseId, string snapshotId, RevisionVector revision, MappingSetView mappings)
        => BuildFullSnapshotCore(sessionId, productId, gameReleaseId, snapshotId, revision, mappings, reserveSequence: true, useIdempotencyCache: true, lookupReplay: true, rememberReplay: true);

    internal FullSnapshotProjectionResult BuildFullSnapshotCandidate(string sessionId, string productId, string gameReleaseId, string snapshotId, RevisionVector revision, MappingSetView mappings)
        => BuildFullSnapshotCore(sessionId, productId, gameReleaseId, snapshotId, revision, mappings, reserveSequence: false, useIdempotencyCache: false, lookupReplay: true, rememberReplay: false);

    private FullSnapshotProjectionResult BuildFullSnapshotCore(
        string sessionId,
        string productId,
        string gameReleaseId,
        string snapshotId,
        RevisionVector revision,
        MappingSetView mappings,
        bool reserveSequence,
        bool useIdempotencyCache,
        bool lookupReplay,
        bool rememberReplay)
    {
        if (!IsEnvelopeIdentityValid(sessionId, productId, gameReleaseId, snapshotId) || revision is null || mappings is null || !ReplicationValidation.IsHash256(mappings.MappingSetHash))
            return new FullSnapshotProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("InvalidArgument", "Snapshot identity, revision and mapping set are required."));
        if (mappings.Mappings.Count > _budget.ProjectionItemLimit)
            return new FullSnapshotProjectionResult(ProjectionStatus.Retryable, null, ReplicationFailure.Retryable("BudgetExceeded", "Projection item budget was exceeded."));
        if (!revision.IsValid || revision.SchemaEpoch != Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch)
            return new FullSnapshotProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("StaleEpoch", "Schema epoch does not match generated contracts."));
        string body = BuildFullBody(snapshotId, revision, mappings.MappingSetHash);
        if (Encoding.UTF8.GetByteCount(body) > _budget.ProjectionBytes)
            return new FullSnapshotProjectionResult(ProjectionStatus.Retryable, null, ReplicationFailure.Retryable("BudgetExceeded", "Projection exceeds the configured byte budget."));
        string idempotencyKey = BuildFullSnapshotIdempotencyKey(sessionId, productId, gameReleaseId, snapshotId, revision, mappings, body);
        if (!TryGetCacheSize(idempotencyKey, body, out long cacheSize))
            return new FullSnapshotProjectionResult(ProjectionStatus.Retryable, null, RetentionFailure("FullSnapshot identity cannot be accounted within the configured budget."));
        lock (_gate)
        {
            if (useIdempotencyCache && _fullSnapshotIdentities.TryGet(idempotencyKey, out FullSnapshotProjection? retained) &&
                _fullSnapshotReplay.TryGet(idempotencyKey, out _))
                return new FullSnapshotProjectionResult(ProjectionStatus.Succeeded, retained, null);
            if (lookupReplay && _fullSnapshotReplay.TryGet(idempotencyKey, out ulong replaySequence))
            {
                var replay = new FullSnapshotProjection(sessionId, productId, gameReleaseId, snapshotId,
                    revision, mappings.MappingSetHash, body, replaySequence, idempotencyKey);
                return new FullSnapshotProjectionResult(ProjectionStatus.Succeeded, replay, null);
            }
            if (_nextSequence == ulong.MaxValue)
                return new FullSnapshotProjectionResult(
                    ProjectionStatus.Rejected,
                    null,
                    ReplicationFailure.Rejected("CapacityExceeded", "Replication sequence space is exhausted."));
            if (!_fullSnapshotReplay.CanAdd(idempotencyKey) ||
                useIdempotencyCache && !_fullSnapshotIdentities.CanAdd(idempotencyKey, cacheSize))
                return new FullSnapshotProjectionResult(ProjectionStatus.Retryable, null, RetentionFailure("FullSnapshot replay retention budget was exceeded."));
            if (!TryPeekSequenceLocked(out ulong sequence))
                return new FullSnapshotProjectionResult(
                    ProjectionStatus.Rejected,
                    null,
                    ReplicationFailure.Rejected("CapacityExceeded", "Replication sequence space is exhausted."));
            var result = new FullSnapshotProjection(sessionId, productId, gameReleaseId, snapshotId, revision, mappings.MappingSetHash, body, sequence, idempotencyKey);
            if (useIdempotencyCache && !_fullSnapshotIdentities.Add(idempotencyKey, result, cacheSize))
                return new FullSnapshotProjectionResult(ProjectionStatus.Retryable, null, RetentionFailure("FullSnapshot materialized retention budget was exceeded."));
            if (rememberReplay && !_fullSnapshotReplay.Add(idempotencyKey, sequence))
                return new FullSnapshotProjectionResult(ProjectionStatus.Retryable, null, RetentionFailure("FullSnapshot replay retention budget was exceeded."));
            if (reserveSequence && !CommitSequenceLocked(sequence))
                return new FullSnapshotProjectionResult(ProjectionStatus.Rejected, null,
                    ReplicationFailure.Rejected("CapacityExceeded", "Replication sequence space is exhausted."));
            return new FullSnapshotProjectionResult(ProjectionStatus.Succeeded, result, null);
        }
    }

    public FullSnapshotProjectionResult BuildFullSnapshot(string sessionId, string productId, string gameReleaseId, string snapshotId, SessionRevisionVector revision, MappingSetView mappings) =>
        BuildFullSnapshot(sessionId, productId, gameReleaseId, snapshotId, new RevisionVector(revision), mappings);

    public DeltaProjectionResult BuildDelta(string sessionId, string productId, string gameReleaseId, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence, RevisionVector revision, MappingSetView mappings, IReadOnlyList<TombstoneView> tombstones, bool gapDetected = false, string? resyncReason = null)
        => BuildDeltaCore(sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision, confirmationSequence, revision, mappings, tombstones, gapDetected, resyncReason, reserveSequence: true, useIdempotencyCache: true, lookupReplay: true, rememberReplay: true);

    internal DeltaProjectionResult BuildDeltaCandidate(string sessionId, string productId, string gameReleaseId, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence, RevisionVector revision, MappingSetView mappings, IReadOnlyList<TombstoneView> tombstones, bool gapDetected = false, string? resyncReason = null)
        => BuildDeltaCore(sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision, confirmationSequence, revision, mappings, tombstones, gapDetected, resyncReason, reserveSequence: false, useIdempotencyCache: false, lookupReplay: true, rememberReplay: false);

    private DeltaProjectionResult BuildDeltaCore(
        string sessionId,
        string productId,
        string gameReleaseId,
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        ulong confirmationSequence,
        RevisionVector revision,
        MappingSetView mappings,
        IReadOnlyList<TombstoneView> tombstones,
        bool gapDetected,
        string? resyncReason,
        bool reserveSequence,
        bool useIdempotencyCache,
        bool lookupReplay,
        bool rememberReplay)
    {
        if (!IsEnvelopeIdentityValid(sessionId, productId, gameReleaseId, baseSnapshotId) || revision is null || mappings is null || tombstones is null || !ReplicationValidation.IsHash256(mappings.MappingSetHash))
            return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("InvalidArgument", "Delta identity, revision and mapping set are required."));
        if (!revision.IsValid || revision.SchemaEpoch != Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch)
            return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("StaleEpoch", "Schema epoch does not match generated contracts."));
        if (toRevision <= fromRevision || toRevision != revision.ReplicationRevision)
            return new DeltaProjectionResult(ProjectionStatus.Rejected, null,
                ReplicationFailure.Rejected("RevisionConflict", "Delta revisions must satisfy 0 <= fromRevision < toRevision == authoritative replication revision."));
        if (tombstones.Count > _budget.ProjectionItemLimit) return new DeltaProjectionResult(ProjectionStatus.Retryable, null, ReplicationFailure.Retryable("BudgetExceeded", "Projection item budget was exceeded."));
        if (gapDetected && string.IsNullOrWhiteSpace(resyncReason)) return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("InvalidArgument", "A detected gap requires a resync reason."));
        if (!gapDetected && !string.IsNullOrWhiteSpace(resyncReason)) return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("InvalidArgument", "A resync reason requires gapDetected."));
        foreach (TombstoneView tombstone in tombstones)
            if (!tombstone.NetEntityId.IsValid) return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("ManifestMalformed", "Tombstone identity is invalid."));
        string body = BuildDeltaBody(baseSnapshotId, fromRevision, toRevision, mappings.MappingSetHash, confirmationSequence, tombstones, gapDetected, resyncReason);
        if (Encoding.UTF8.GetByteCount(body) > _budget.ProjectionBytes)
            return new DeltaProjectionResult(ProjectionStatus.Retryable, null, ReplicationFailure.Retryable("BudgetExceeded", "Projection exceeds the configured byte budget."));
        string idempotencyKey = BuildDeltaIdempotencyKey(sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision, confirmationSequence, revision, mappings, tombstones, gapDetected, resyncReason, body);
        if (!TryGetCacheSize(idempotencyKey, body, out long cacheSize))
            return new DeltaProjectionResult(ProjectionStatus.Retryable, null, RetentionFailure("Delta identity cannot be accounted within the configured budget."));
        lock (_gate)
        {
            if (useIdempotencyCache && _deltaIdentities.TryGet(idempotencyKey, out DeltaProjection? retained) &&
                _deltaReplay.TryGet(idempotencyKey, out _))
                return new DeltaProjectionResult(ProjectionStatus.Succeeded, retained, null);
            if (lookupReplay && _deltaReplay.TryGet(idempotencyKey, out ulong replaySequence))
            {
                var replay = new DeltaProjection(sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision,
                    toRevision, confirmationSequence, revision, mappings.MappingSetHash, body, replaySequence, idempotencyKey);
                return new DeltaProjectionResult(ProjectionStatus.Succeeded, replay, null);
            }
            if (_nextSequence == ulong.MaxValue)
                return new DeltaProjectionResult(
                    ProjectionStatus.Rejected,
                    null,
                    ReplicationFailure.Rejected("CapacityExceeded", "Replication sequence space is exhausted."));
            if (!_deltaReplay.CanAdd(idempotencyKey) ||
                useIdempotencyCache && !_deltaIdentities.CanAdd(idempotencyKey, cacheSize))
                return new DeltaProjectionResult(ProjectionStatus.Retryable, null, RetentionFailure("Delta replay retention budget was exceeded."));
            if (!TryPeekSequenceLocked(out ulong sequence))
                return new DeltaProjectionResult(
                    ProjectionStatus.Rejected,
                    null,
                    ReplicationFailure.Rejected("CapacityExceeded", "Replication sequence space is exhausted."));
            var result = new DeltaProjection(sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision, confirmationSequence, revision, mappings.MappingSetHash, body, sequence, idempotencyKey);
            if (useIdempotencyCache && !_deltaIdentities.Add(idempotencyKey, result, cacheSize))
                return new DeltaProjectionResult(ProjectionStatus.Retryable, null, RetentionFailure("Delta materialized retention budget was exceeded."));
            if (rememberReplay && !_deltaReplay.Add(idempotencyKey, sequence))
                return new DeltaProjectionResult(ProjectionStatus.Retryable, null, RetentionFailure("Delta replay retention budget was exceeded."));
            if (reserveSequence && !CommitSequenceLocked(sequence))
                return new DeltaProjectionResult(ProjectionStatus.Rejected, null,
                    ReplicationFailure.Rejected("CapacityExceeded", "Replication sequence space is exhausted."));
            return new DeltaProjectionResult(ProjectionStatus.Succeeded, result, null);
        }
    }

    public DeltaProjectionResult BuildDelta(string sessionId, string productId, string gameReleaseId, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence, SessionRevisionVector revision, MappingSetView mappings, IReadOnlyList<TombstoneView> tombstones, bool gapDetected = false, string? resyncReason = null) =>
        BuildDelta(sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision, confirmationSequence, new RevisionVector(revision), mappings, tombstones, gapDetected, resyncReason);

    private bool TryPeekSequenceLocked(out ulong sequence)
    {
        if (_nextSequence == ulong.MaxValue)
        {
            sequence = 0;
            return false;
        }

        sequence = _nextSequence;
        return true;
    }

    private bool CommitSequenceLocked(ulong sequence)
    {
        if (sequence != _nextSequence || _nextSequence == ulong.MaxValue) return false;
        _nextSequence++;
        return true;
    }

    internal bool CommitSequence(ulong sequence)
    {
        lock (_gate)
            return CommitSequenceLocked(sequence);
    }

    internal bool CommitFullSnapshot(ulong sequence, string idempotencyKey)
    {
        lock (_gate)
            return CommitReplayAndSequenceLocked(_fullSnapshotReplay, sequence, idempotencyKey);
    }

    internal bool CommitDelta(ulong sequence, string idempotencyKey)
    {
        lock (_gate)
            return CommitReplayAndSequenceLocked(_deltaReplay, sequence, idempotencyKey);
    }

    private bool CommitReplayAndSequenceLocked(ReplaySequenceLedger ledger, ulong sequence, string idempotencyKey)
    {
        if (sequence != _nextSequence || _nextSequence == ulong.MaxValue || !ledger.CanAdd(idempotencyKey)) return false;
        _nextSequence++;
        if (ledger.Add(idempotencyKey, sequence)) return true;
        // The preflight above makes this path unreachable in normal operation,
        // but keep the sequence transaction atomic if a ledger is inconsistent.
        _nextSequence--;
        return false;
    }

    internal void ResetIdempotency()
    {
        lock (_gate)
        {
            _fullSnapshotIdentities.Clear();
            _deltaIdentities.Clear();
            _fullSnapshotReplay.Clear();
            _deltaReplay.Clear();
        }
    }

    internal bool TryGetFullSnapshotReplay(string key, out ulong sequence)
    {
        lock (_gate) return _fullSnapshotReplay.TryGet(key, out sequence);
    }

    internal bool TryGetDeltaReplay(string key, out ulong sequence)
    {
        lock (_gate) return _deltaReplay.TryGet(key, out sequence);
    }

    internal bool RememberFullSnapshotReplay(string key, ulong sequence)
    {
        lock (_gate) return _fullSnapshotReplay.Add(key, sequence);
    }

    internal bool RememberDeltaReplay(string key, ulong sequence)
    {
        lock (_gate) return _deltaReplay.Add(key, sequence);
    }

    private static bool TryGetCacheSize(string key, string body, out long size)
    {
        size = 0;
        long keyBytes = Encoding.UTF8.GetByteCount(key);
        long bodyBytes = Encoding.UTF8.GetByteCount(body);
        if (keyBytes > long.MaxValue - bodyBytes) return false;
        size = keyBytes + bodyBytes;
        return true;
    }

    private static ReplicationFailure RetentionFailure(string detail) =>
        ReplicationFailure.Retryable("QueueFull", detail);

    internal static string BuildFullSnapshotIdempotencyKey(
        string sessionId,
        string productId,
        string gameReleaseId,
        string snapshotId,
        RevisionVector revision,
        MappingSetView mappings)
    {
        string body = BuildFullBody(snapshotId, revision, mappings.MappingSetHash);
        return BuildIdentityKey("FullSnapshot", sessionId, productId, gameReleaseId, snapshotId,
            body, BuildRevisionVector(revision), Convert.ToBase64String(mappings.GetCanonicalBytes().ToArray()));
    }

    internal static bool TryBuildFullSnapshotIdempotencyKey(
        string sessionId,
        string productId,
        string gameReleaseId,
        string snapshotId,
        RevisionVector? revision,
        MappingSetView? mappings,
        out string key)
    {
        key = string.Empty;
        if (!IsEnvelopeIdentityValid(sessionId, productId, gameReleaseId, snapshotId) ||
            revision is null || mappings is null || !ReplicationValidation.IsHash256(mappings.MappingSetHash) || !revision.IsValid)
            return false;
        key = BuildFullSnapshotIdempotencyKey(sessionId, productId, gameReleaseId, snapshotId, revision, mappings);
        return true;
    }

    private static string BuildFullSnapshotIdempotencyKey(
        string sessionId,
        string productId,
        string gameReleaseId,
        string snapshotId,
        RevisionVector revision,
        MappingSetView mappings,
        string body) =>
        BuildIdentityKey("FullSnapshot", sessionId, productId, gameReleaseId, snapshotId,
            body, BuildRevisionVector(revision), Convert.ToBase64String(mappings.GetCanonicalBytes().ToArray()));

    internal static string BuildDeltaIdempotencyKey(
        string sessionId,
        string productId,
        string gameReleaseId,
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        ulong confirmationSequence,
        RevisionVector revision,
        MappingSetView mappings,
        IReadOnlyList<TombstoneView> tombstones,
        bool gapDetected,
        string? resyncReason)
    {
        string body = BuildDeltaBody(baseSnapshotId, fromRevision, toRevision, mappings.MappingSetHash, confirmationSequence, tombstones, gapDetected, resyncReason);
        return BuildDeltaIdempotencyKey(
            sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision,
            confirmationSequence, revision, mappings, tombstones, gapDetected, resyncReason, body);
    }

    internal static bool TryBuildDeltaIdempotencyKey(
        string sessionId,
        string productId,
        string gameReleaseId,
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        ulong confirmationSequence,
        RevisionVector? revision,
        MappingSetView? mappings,
        IReadOnlyList<TombstoneView>? tombstones,
        bool gapDetected,
        string? resyncReason,
        out string key)
    {
        key = string.Empty;
        if (!IsEnvelopeIdentityValid(sessionId, productId, gameReleaseId, baseSnapshotId) ||
            revision is null || mappings is null || tombstones is null ||
            !ReplicationValidation.IsHash256(mappings.MappingSetHash) || !revision.IsValid ||
            toRevision <= fromRevision || toRevision != revision.ReplicationRevision ||
            (gapDetected && string.IsNullOrWhiteSpace(resyncReason)) ||
            (!gapDetected && !string.IsNullOrWhiteSpace(resyncReason)))
            return false;
        foreach (TombstoneView tombstone in tombstones)
            if (!tombstone.NetEntityId.IsValid) return false;
        try
        {
            key = BuildDeltaIdempotencyKey(
                sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision,
                confirmationSequence, revision, mappings, tombstones, gapDetected, resyncReason);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string BuildDeltaIdempotencyKey(
        string sessionId,
        string productId,
        string gameReleaseId,
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        ulong confirmationSequence,
        RevisionVector revision,
        MappingSetView mappings,
        IReadOnlyList<TombstoneView> tombstones,
        bool gapDetected,
        string? resyncReason,
        string body) =>
        BuildIdentityKey("Delta", sessionId, productId, gameReleaseId, baseSnapshotId,
            fromRevision.ToString(CultureInfo.InvariantCulture),
            toRevision.ToString(CultureInfo.InvariantCulture),
            confirmationSequence.ToString(CultureInfo.InvariantCulture),
            BuildRevisionVector(revision),
            Convert.ToBase64String(mappings.GetCanonicalBytes().ToArray()),
            body,
            gapDetected ? "1" : "0",
            string.IsNullOrWhiteSpace(resyncReason) ? string.Empty : resyncReason!);

    private static string BuildIdentityKey(string domain, params string[] parts)
    {
        // Keep the canonical material, rather than only a digest, so equal
        // hashes cannot cause two different payloads to share a sequence.
        var builder = new StringBuilder(domain.Length + parts.Sum(value => value.Length + 12));
        AppendIdentityPart(builder, domain);
        foreach (string part in parts) AppendIdentityPart(builder, part);
        return builder.ToString();
    }

    private static void AppendIdentityPart(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    // Projection has no history store of its own, so retain only the same
    // bounded window/byte budget used by replication history.
    private sealed class ProjectionIdentityCache<T> where T : class
    {
        private readonly int _capacity;
        private readonly long _byteCapacity;
        private readonly Dictionary<string, T> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        private long _bytes;

        public ProjectionIdentityCache(ReplicationBudget budget)
        {
            _capacity = budget.HistoryWindow;
            _byteCapacity = budget.HistoryBytes;
        }

        public bool TryGet(string key, out T? value) => _values.TryGetValue(key, out value);

        public bool CanAdd(string key, long size)
        {
            if (_values.ContainsKey(key)) return true;
            return _capacity > 0 && size >= 0 && size <= _byteCapacity;
        }

        public bool Add(string key, T value, long size)
        {
            if (!CanAdd(key, size)) return false;
            if (_values.ContainsKey(key)) return true;

            while (_values.Count >= _capacity || _bytes > _byteCapacity - size)
            {
                if (_order.Count == 0) return false;
                string oldest = _order.Dequeue();
                if (!_values.Remove(oldest)) continue;
                if (_sizes.Remove(oldest, out long previous)) _bytes -= previous;
            }

            _values[key] = value;
            _sizes[key] = size;
            _order.Enqueue(key);
            _bytes += size;
            return true;
        }

        public void Clear()
        {
            _values.Clear();
            _sizes.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }

    /// <summary>
    /// Stores only the sequence needed to replay an accepted request. Entries
    /// remain until an explicit lifecycle reset/release ends their retry
    /// authority; while full, a new request is rejected before allocation.
    /// </summary>
    private sealed class ReplaySequenceLedger
    {
        private readonly int _capacity;
        private readonly long _byteCapacity;
        private readonly Dictionary<string, ulong> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        private long _bytes;

        public ReplaySequenceLedger(ReplicationBudget budget)
        {
            _capacity = budget.HistoryWindow;
            _byteCapacity = budget.HistoryBytes;
        }

        public bool TryGet(string key, out ulong sequence)
        {
            sequence = 0;
            return !string.IsNullOrEmpty(key) && _values.TryGetValue(key, out sequence);
        }

        public bool CanAdd(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (_values.ContainsKey(key)) return true;
            if (_capacity <= 0 || _values.Count >= _capacity) return false;
            long keyBytes = Encoding.UTF8.GetByteCount(key);
            if (keyBytes > long.MaxValue - sizeof(ulong)) return false;
            long size = keyBytes + sizeof(ulong);
            return size <= _byteCapacity && _bytes <= _byteCapacity - size;
        }

        public bool Add(string key, ulong sequence)
        {
            if (_values.ContainsKey(key)) return true;
            if (!CanAdd(key)) return false;
            long keyBytes = Encoding.UTF8.GetByteCount(key);
            long size = keyBytes + sizeof(ulong);
            _values[key] = sequence;
            _sizes[key] = size;
            _order.Enqueue(key);
            _bytes += size;
            return true;
        }

        public void Clear()
        {
            _values.Clear();
            _sizes.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }

    private static bool IsEnvelopeIdentityValid(string sessionId, string productId, string releaseId, string snapshotId) =>
        ReplicationValidation.IsIdentifier(sessionId) && ReplicationValidation.IsProductId(productId) &&
        ReplicationValidation.IsReleaseId(releaseId) && ReplicationValidation.IsIdentifier(snapshotId);

    private static string BuildFullBody(string snapshotId, RevisionVector revision, string mappingHash)
    {
        var builder = new StringBuilder();
        builder.Append("{\"mappingSetHash\":\"").Append(mappingHash).Append("\",\"schemaEpoch\":").Append(revision.SchemaEpoch.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"sessionRevisionVector\":").Append(BuildRevisionVector(revision));
        builder.Append(",\"snapshotId\":\"").Append(Escape(snapshotId)).Append("\",\"tickId\":").Append(revision.TickId.ToString(CultureInfo.InvariantCulture)).Append('}');
        return builder.ToString();
    }

    private static string BuildDeltaBody(string baseSnapshotId, ulong fromRevision, ulong toRevision, string mappingHash, ulong confirmationSequence, IReadOnlyList<TombstoneView> tombstones, bool gapDetected, string? resyncReason)
    {
        var builder = new StringBuilder();
        builder.Append("{\"baseSnapshotId\":\"").Append(Escape(baseSnapshotId)).Append("\",\"confirmationSequence\":").Append(confirmationSequence.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"fromRevision\":").Append(fromRevision.ToString(CultureInfo.InvariantCulture));
        if (gapDetected) builder.Append(",\"gapDetected\":true");
        builder.Append(",\"mappingSetHash\":\"").Append(mappingHash).Append("\"");
        builder.Append(",\"toRevision\":").Append(toRevision.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(resyncReason)) builder.Append(",\"resyncReason\":\"").Append(Escape(resyncReason!)).Append("\"");
        builder.Append(",\"tombstones\":[");
        var ordered = tombstones.OrderBy(value => value.NetEntityId.Value, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append("{\"netEntityId\":\"").Append(ordered[i].NetEntityId.Value).Append("\",\"untilRevision\":").Append(ordered[i].UntilRevision.ToString(CultureInfo.InvariantCulture)).Append('}');
        }
        builder.Append("]}");
        string body = builder.ToString();
        if (!StructuredJsonParser.TryParse(body, out StructuredJsonValue? parsed) ||
            parsed is null || !StructuredJsonCanonicalizer.TryCanonicalize(parsed, out string canonical))
            throw new InvalidOperationException("Delta projection did not produce canonical JSON.");
        return canonical;
    }

    private static string BuildRevisionVector(RevisionVector revision)
    {
        var builder = new StringBuilder("{\"chunkRevisionSet\":{");
        var chunks = revision.ChunkRevisionSet.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < chunks.Length; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append('"').Append(Escape(chunks[i].Key)).Append("\":").Append(chunks[i].Value.ToString(CultureInfo.InvariantCulture));
        }
        builder.Append("},\"configRevision\":").Append(revision.ConfigRevision.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"gameRevision\":").Append(revision.GameRevision.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"replicationRevision\":").Append(revision.ReplicationRevision.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"schemaEpoch\":").Append(revision.SchemaEpoch.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"tickId\":").Append(revision.TickId.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"voxelWorldRevision\":").Append(revision.VoxelWorldRevision.ToString(CultureInfo.InvariantCulture)).Append('}');
        return builder.ToString();
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char item in value)
        {
            switch (item)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (item < 0x20) builder.Append("\\u").Append(((int)item).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(item);
                    break;
            }
        }
        return builder.ToString();
    }
}
