using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Mapping;
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
    internal FullSnapshotProjection(string sessionId, string productId, string gameReleaseId, string snapshotId, RevisionVector revision, string mappingSetHash, string bodyJson, ulong sequence)
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
    internal DeltaProjection(string sessionId, string productId, string gameReleaseId, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence, RevisionVector revision, string mappingSetHash, string bodyJson, ulong sequence)
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
    }

    public string SessionId { get; }
    public string ProductId { get; }
    public string GameReleaseId { get; }
    public string BaseSnapshotId { get; }
    public ulong FromRevision { get; }
    public ulong ToRevision { get; }
    public ulong ConfirmationSequence { get; }
    public RevisionVector Revision { get; }
    public string MappingSetHash { get; }
    public string BodyJson { get; }
    public ulong Sequence { get; }

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
    private ulong _nextSequence = 1;

    public ReplicationProjection(ReplicationBudget budget)
    {
        if (!budget.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));
        _budget = budget;
    }

    public FullSnapshotProjectionResult BuildFullSnapshot(string sessionId, string productId, string gameReleaseId, string snapshotId, RevisionVector revision, MappingSetView mappings)
    {
        if (!IsEnvelopeIdentityValid(sessionId, productId, gameReleaseId, snapshotId) || revision is null || mappings is null || !ReplicationValidation.IsHash256(mappings.MappingSetHash))
            return new FullSnapshotProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("InvalidArgument", "Snapshot identity, revision and mapping set are required."));
        if (mappings.Mappings.Count > _budget.ProjectionItemLimit)
            return new FullSnapshotProjectionResult(ProjectionStatus.Retryable, null, ReplicationFailure.Retryable("BudgetExceeded", "Projection item budget was exceeded."));
        if (revision.SchemaEpoch != Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch)
            return new FullSnapshotProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("StaleEpoch", "Schema epoch does not match generated contracts."));
        string body = BuildFullBody(snapshotId, revision, mappings.MappingSetHash);
        if (Encoding.UTF8.GetByteCount(body) > _budget.ProjectionBytes)
            return new FullSnapshotProjectionResult(ProjectionStatus.Retryable, null, ReplicationFailure.Retryable("BudgetExceeded", "Projection exceeds the configured byte budget."));
        var result = new FullSnapshotProjection(sessionId, productId, gameReleaseId, snapshotId, revision, mappings.MappingSetHash, body, NextSequence());
        return new FullSnapshotProjectionResult(ProjectionStatus.Succeeded, result, null);
    }

    public FullSnapshotProjectionResult BuildFullSnapshot(string sessionId, string productId, string gameReleaseId, string snapshotId, SessionRevisionVector revision, MappingSetView mappings) =>
        BuildFullSnapshot(sessionId, productId, gameReleaseId, snapshotId, new RevisionVector(revision), mappings);

    public DeltaProjectionResult BuildDelta(string sessionId, string productId, string gameReleaseId, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence, RevisionVector revision, MappingSetView mappings, IReadOnlyList<TombstoneView> tombstones, bool gapDetected = false, string? resyncReason = null)
    {
        if (!IsEnvelopeIdentityValid(sessionId, productId, gameReleaseId, baseSnapshotId) || revision is null || mappings is null || tombstones is null || !ReplicationValidation.IsHash256(mappings.MappingSetHash))
            return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("InvalidArgument", "Delta identity, revision and mapping set are required."));
        if (toRevision <= fromRevision) return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("RevisionConflict", "Delta revisions must advance."));
        if (tombstones.Count > _budget.ProjectionItemLimit) return new DeltaProjectionResult(ProjectionStatus.Retryable, null, ReplicationFailure.Retryable("BudgetExceeded", "Projection item budget was exceeded."));
        if (gapDetected && string.IsNullOrWhiteSpace(resyncReason)) return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("InvalidArgument", "A detected gap requires a resync reason."));
        if (!gapDetected && !string.IsNullOrWhiteSpace(resyncReason)) return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("InvalidArgument", "A resync reason requires gapDetected."));
        foreach (TombstoneView tombstone in tombstones)
            if (!tombstone.NetEntityId.IsValid) return new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("ManifestMalformed", "Tombstone identity is invalid."));
        string body = BuildDeltaBody(baseSnapshotId, fromRevision, toRevision, mappings.MappingSetHash, confirmationSequence, tombstones, gapDetected, resyncReason);
        if (Encoding.UTF8.GetByteCount(body) > _budget.ProjectionBytes)
            return new DeltaProjectionResult(ProjectionStatus.Retryable, null, ReplicationFailure.Retryable("BudgetExceeded", "Projection exceeds the configured byte budget."));
        var result = new DeltaProjection(sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision, confirmationSequence, revision, mappings.MappingSetHash, body, NextSequence());
        return new DeltaProjectionResult(ProjectionStatus.Succeeded, result, null);
    }

    public DeltaProjectionResult BuildDelta(string sessionId, string productId, string gameReleaseId, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence, SessionRevisionVector revision, MappingSetView mappings, IReadOnlyList<TombstoneView> tombstones, bool gapDetected = false, string? resyncReason = null) =>
        BuildDelta(sessionId, productId, gameReleaseId, baseSnapshotId, fromRevision, toRevision, confirmationSequence, new RevisionVector(revision), mappings, tombstones, gapDetected, resyncReason);

    private ulong NextSequence()
    {
        lock (_gate)
        {
            ulong value = _nextSequence;
            checked { _nextSequence++; }
            return value;
        }
    }

    private static bool IsEnvelopeIdentityValid(string sessionId, string productId, string releaseId, string snapshotId) =>
        ReplicationValidation.IsIdentifier(sessionId) && ReplicationValidation.IsIdentifier(productId) && ReplicationValidation.IsIdentifier(releaseId) && ReplicationValidation.IsIdentifier(snapshotId);

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
        return builder.ToString();
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
