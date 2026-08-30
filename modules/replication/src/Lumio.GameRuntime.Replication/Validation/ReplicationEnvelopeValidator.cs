using System;
using System.Collections.Generic;
using System.Text;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Replication.Validation;

public enum ReplicationValidationCode
{
    Accepted,
    Invalid,
    UnknownBaseline,
    Gap,
    StaleRevision,
    DuplicateSequence,
    StaleConnectionGeneration,
    MappingMismatch,
    SchemaMismatch,
    RequiresResync
}

public readonly record struct ReplicationValidationResult(
    ReplicationValidationCode Code,
    string? Detail,
    bool RequiresResync,
    string? GeneratedErrorId)
{
    public bool Succeeded => Code == ReplicationValidationCode.Accepted;

    public bool IsAccepted => Succeeded;

    public static ReplicationValidationResult Accepted() =>
        new(ReplicationValidationCode.Accepted, null, false, null);

    public static ReplicationValidationResult Rejected(
        ReplicationValidationCode code,
        string detail,
        bool requiresResync,
        string? errorId = null) =>
        new(code, detail, requiresResync, errorId ?? (requiresResync ? "SnapshotBaseMismatch" : "InvalidArgument"));
}

/// <summary>
/// Validates generated replication envelopes before they enter an owner-thread
/// queue. Closed envelope metadata is strict; application payload fields remain
/// opaque and are only checked for the generated envelope shape.
/// </summary>
public sealed class ReplicationEnvelopeValidator
{
    public ReplicationValidationResult ValidateSequence(ulong sequence, ulong expectedSequence)
    {
        if (sequence == expectedSequence) return ReplicationValidationResult.Accepted();
        if (sequence > expectedSequence)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Gap, "Replication sequence has a gap.", true, "SnapshotBaseMismatch");
        return ReplicationValidationResult.Rejected(ReplicationValidationCode.DuplicateSequence, "Replication sequence is stale or duplicated.", false, "InvalidArgument");
    }

    public ReplicationValidationResult ValidateGeneration(ulong actualGeneration, ulong expectedGeneration) =>
        actualGeneration == expectedGeneration
            ? ReplicationValidationResult.Accepted()
            : ReplicationValidationResult.Rejected(ReplicationValidationCode.StaleConnectionGeneration, "Connection generation is stale.", false, "StaleConnectionGeneration");

    public ReplicationValidationResult ValidateBaseline(
        string snapshotId,
        ulong confirmedRevision,
        string expectedSnapshotId,
        ulong expectedRevision) =>
        !ReplicationValidation.IsIdentifier(snapshotId) || !ReplicationValidation.IsIdentifier(expectedSnapshotId)
            ? ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Baseline identity is invalid.", false)
            : snapshotId != expectedSnapshotId
                ? ReplicationValidationResult.Rejected(ReplicationValidationCode.UnknownBaseline, "Baseline identity is unknown.", true, "SnapshotBaseMismatch")
                : confirmedRevision != expectedRevision
                    ? ReplicationValidationResult.Rejected(ReplicationValidationCode.StaleRevision, "Baseline revision does not match.", true, "RevisionConflict")
                    : ReplicationValidationResult.Accepted();

    public ReplicationValidationResult ValidateRevision(ulong fromRevision, ulong toRevision) =>
        toRevision > fromRevision
            ? ReplicationValidationResult.Accepted()
            : ReplicationValidationResult.Rejected(ReplicationValidationCode.StaleRevision, "Revision range must advance.", true, "RevisionConflict");

    public ReplicationValidationResult ValidateGap(bool gapDetected, string? resyncReason) =>
        gapDetected && string.IsNullOrWhiteSpace(resyncReason)
            ? ReplicationValidationResult.Rejected(ReplicationValidationCode.RequiresResync, "A gap must carry a resync reason.", true, "SnapshotBaseMismatch")
            : !gapDetected && !string.IsNullOrWhiteSpace(resyncReason)
                ? ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "A resync reason requires a gap.", true, "SnapshotBaseMismatch")
                : ReplicationValidationResult.Accepted();

    public ReplicationValidationResult ValidateEnvelope(
        ReplicationEnvelope envelope,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null)
    {
        if (envelope is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope is required.", false);
        if (!ReplicationValidation.IsIdentifier(expectedSessionId) || !ReplicationValidation.IsProductId(expectedProductId) || !ReplicationValidation.IsReleaseId(expectedGameReleaseId))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Expected session identity is invalid.", false);
        if (!ReplicationValidation.IsIdentifier(envelope.SessionId) || !ReplicationValidation.IsProductId(envelope.ProductId) || !ReplicationValidation.IsReleaseId(envelope.GameReleaseId) || !ReplicationValidation.IsTraceId(envelope.TraceId))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope identity is invalid.", false);
        if (envelope.SessionId != expectedSessionId || envelope.ProductId != expectedProductId)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Session or product mismatch.", false, "SessionMismatch");
        if (envelope.GameReleaseId != expectedGameReleaseId)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Release mismatch.", false, "ReleaseMismatch");
        if (!Enum.IsDefined(typeof(ReplicationEnvelopeMessageType), envelope.MessageType) || !Enum.IsDefined(typeof(ReplicationEnvelopeReliability), envelope.Reliability))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope message metadata is unknown.", false, "MessagePermissionDenied");
        if (envelope.ProtocolVersion == 0 || envelope.Length == 0)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope protocol or length is invalid.", false);
        ReplicationValidationResult policyResult = ValidateTransportPolicy(envelope.TransportPolicy);
        if (!policyResult.Succeeded) return policyResult;
        if (envelope.Length > envelope.TransportPolicy.MaxMessageBytes)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope exceeds the transport message budget.", false, "CapacityExceeded");
        ReplicationValidationResult integrityResult = ValidateIntegrity(envelope.Integrity);
        if (!integrityResult.Succeeded) return integrityResult;
        if (expectedSequence.HasValue)
        {
            ReplicationValidationResult sequence = ValidateSequence(envelope.Sequence, expectedSequence.Value);
            if (!sequence.Succeeded) return sequence;
        }

        if (envelope.Body is null || string.IsNullOrWhiteSpace(envelope.Body.Json))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body is required.", false);
        bool projectionMessage = envelope.MessageType is ReplicationEnvelopeMessageType.FullSnapshot or ReplicationEnvelopeMessageType.Delta;
        if (projectionMessage && !ReplicationValidation.IsHash256(expectedMappingSetHash))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.MappingMismatch, "Mapping hash is invalid.", true, "SnapshotBaseMismatch");

#if NET10_0_OR_GREATER
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(envelope.Body.Json);
            System.Text.Json.JsonElement body = document.RootElement;
            if (body.ValueKind != System.Text.Json.JsonValueKind.Object)
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body must be an object.", false);
            return ValidateBody(envelope.MessageType, envelope.Reliability, body, expectedMappingSetHash, expectedSchemaEpoch);
        }
        catch (System.Text.Json.JsonException)
        {
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body is malformed JSON.", false);
        }
#else
        return ValidateBodyText(envelope.MessageType, envelope.Reliability, envelope.Body.Json, expectedMappingSetHash, expectedSchemaEpoch);
#endif
    }

    public ReplicationValidationResult Validate(
        ReplicationEnvelope envelope,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null) =>
        ValidateEnvelope(envelope, expectedSessionId, expectedProductId, expectedGameReleaseId, expectedMappingSetHash, expectedSchemaEpoch, expectedSequence);

    private static ReplicationValidationResult ValidateTransportPolicy(ReplicationEnvelopeTransportPolicy? policy)
    {
        if (policy is null || policy.MaxMessageBytes is < 1 or > 1_048_576 || policy.MaxFragmentBytes is < 1 or > 65_536 || policy.MaxFragmentBytes > policy.MaxMessageBytes || policy.AntiReplayWindow == 0 ||
            !Enum.IsDefined(typeof(ReplicationEnvelopeTransportPolicyAuthBinding), policy.AuthBinding) || !Enum.IsDefined(typeof(ReplicationEnvelopeTransportPolicyErrorClass), policy.ErrorClass))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Transport policy is invalid.", false, "ManifestMalformed");
        return ReplicationValidationResult.Accepted();
    }

    private static ReplicationValidationResult ValidateIntegrity(ReplicationEnvelopeIntegrity? integrity)
    {
        if (integrity is null || !Enum.IsDefined(typeof(ReplicationEnvelopeIntegrityAlgorithm), integrity.Algorithm) || string.IsNullOrEmpty(integrity.Value))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope integrity metadata is invalid.", false, "ManifestMalformed");
        bool valid = integrity.Algorithm switch
        {
            ReplicationEnvelopeIntegrityAlgorithm.None => integrity.Value == "none",
            ReplicationEnvelopeIntegrityAlgorithm.CRC32C => IsLowerHex(integrity.Value, 8),
            ReplicationEnvelopeIntegrityAlgorithm.SHA256 => ReplicationValidation.IsHash256(integrity.Value),
            ReplicationEnvelopeIntegrityAlgorithm.AEAD => integrity.Value.Length is >= 24 and <= 256,
            _ => false
        };
        return valid
            ? ReplicationValidationResult.Accepted()
            : ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope integrity value is invalid.", false, "ManifestMalformed");
    }

#if NET10_0_OR_GREATER
    private static ReplicationValidationResult ValidateBody(
        ReplicationEnvelopeMessageType messageType,
        ReplicationEnvelopeReliability reliability,
        System.Text.Json.JsonElement body,
        string mappingHash,
        int schemaEpoch)
    {
        string[] required;
        string[] allowed;
        switch (messageType)
        {
            case ReplicationEnvelopeMessageType.Handshake:
                required = new[] { "role" };
                allowed = required;
                break;
            case ReplicationEnvelopeMessageType.FullSnapshot:
                if (reliability != ReplicationEnvelopeReliability.Reliable)
                    return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "FullSnapshot must be reliable.", false);
                required = new[] { "snapshotId", "tickId", "sessionRevisionVector", "schemaEpoch", "mappingSetHash" };
                allowed = required;
                break;
            case ReplicationEnvelopeMessageType.BaselineAck:
                required = new[] { "snapshotId", "confirmedRevision" };
                allowed = required;
                break;
            case ReplicationEnvelopeMessageType.Delta:
                if (reliability != ReplicationEnvelopeReliability.Reliable)
                    return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Delta must be reliable.", false);
                required = new[] { "baseSnapshotId", "fromRevision", "toRevision", "mappingSetHash", "confirmationSequence", "tombstones" };
                allowed = new[] { "baseSnapshotId", "confirmationSequence", "fromRevision", "gapDetected", "mappingSetHash", "resyncReason", "toRevision", "tombstones" };
                break;
            case ReplicationEnvelopeMessageType.DeltaAck:
                required = new[] { "confirmationSequence", "toRevision" };
                allowed = required;
                break;
            case ReplicationEnvelopeMessageType.ResyncRequest:
                required = new[] { "resyncReason" };
                allowed = required;
                break;
            case ReplicationEnvelopeMessageType.MaintenanceKick:
                required = new[] { "reasonCode" };
                allowed = required;
                break;
            case ReplicationEnvelopeMessageType.Error:
                required = new[] { "errorClass", "reasonCode" };
                allowed = required;
                break;
            default:
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Message type is not registered.", false, "MessagePermissionDenied");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.Json.JsonProperty property in body.EnumerateObject())
        {
            if (!names.Add(property.Name) || Array.IndexOf(allowed, property.Name) < 0)
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body contains an unknown or duplicate member.", false, "ManifestMalformed");
        }
        foreach (string name in required)
            if (!body.TryGetProperty(name, out _))
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Required body member is missing: " + name, false, "ManifestMalformed");

        switch (messageType)
        {
            case ReplicationEnvelopeMessageType.Handshake:
                return RequiredString(body, "role", false);
            case ReplicationEnvelopeMessageType.FullSnapshot:
                if (!IsIdentifierProperty(body, "snapshotId") || !UIntProperty(body, "tickId", out _) || !UIntProperty(body, "schemaEpoch", out ulong epoch) || epoch != (ulong)Math.Max(schemaEpoch, 0) ||
                    !HashProperty(body, "mappingSetHash", mappingHash) || !ValidRevisionVector(body.GetProperty("sessionRevisionVector"), schemaEpoch))
                    return ReplicationValidationResult.Rejected(ReplicationValidationCode.SchemaMismatch, "FullSnapshot metadata does not match the expected contract.", true, "StaleEpoch");
                return ReplicationValidationResult.Accepted();
            case ReplicationEnvelopeMessageType.BaselineAck:
                return IsIdentifierProperty(body, "snapshotId") && UIntProperty(body, "confirmedRevision", out _)
                    ? ReplicationValidationResult.Accepted()
                    : ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "BaselineAck metadata is invalid.", false, "ManifestMalformed");
            case ReplicationEnvelopeMessageType.Delta:
                if (!IsIdentifierProperty(body, "baseSnapshotId") || !UIntProperty(body, "fromRevision", out ulong from) || !UIntProperty(body, "toRevision", out ulong to) ||
                    to <= from || !HashProperty(body, "mappingSetHash", mappingHash) || !UIntProperty(body, "confirmationSequence", out _) || !ValidTombstones(body.GetProperty("tombstones")))
                    return ReplicationValidationResult.Rejected(ReplicationValidationCode.StaleRevision, "Delta revision metadata is invalid.", true, "RevisionConflict");
                bool gap = false;
                if (body.TryGetProperty("gapDetected", out System.Text.Json.JsonElement gapValue))
                {
                    if (gapValue.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                        return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "gapDetected must be boolean.", false, "ManifestMalformed");
                    gap = gapValue.GetBoolean();
                }
                string? reason = null;
                if (body.TryGetProperty("resyncReason", out System.Text.Json.JsonElement reasonValue))
                {
                    if (reasonValue.ValueKind != System.Text.Json.JsonValueKind.String)
                        return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "resyncReason must be a string.", false, "ManifestMalformed");
                    reason = reasonValue.GetString();
                }
                return new ReplicationEnvelopeValidator().ValidateGap(gap, reason);
            case ReplicationEnvelopeMessageType.DeltaAck:
                return UIntProperty(body, "confirmationSequence", out _) && UIntProperty(body, "toRevision", out _)
                    ? ReplicationValidationResult.Accepted()
                    : ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "DeltaAck metadata is invalid.", false, "ManifestMalformed");
            case ReplicationEnvelopeMessageType.ResyncRequest:
                return RequiredString(body, "resyncReason", false);
            case ReplicationEnvelopeMessageType.MaintenanceKick:
                return RequiredString(body, "reasonCode", false);
            case ReplicationEnvelopeMessageType.Error:
                if (!RequiredString(body, "errorClass", false).Succeeded || !RequiredString(body, "reasonCode", false).Succeeded ||
                    !IsErrorClass(body.GetProperty("errorClass")))
                    return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Error metadata is invalid.", false, "ManifestMalformed");
                return ReplicationValidationResult.Accepted();
            default:
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Message type is not registered.", false, "MessagePermissionDenied");
        }
    }

    private static ReplicationValidationResult RequiredString(System.Text.Json.JsonElement body, string name, bool identifier)
    {
        if (!body.TryGetProperty(name, out System.Text.Json.JsonElement value) || value.ValueKind != System.Text.Json.JsonValueKind.String)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, name + " must be a string.", false, "ManifestMalformed");
        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || (identifier && !ReplicationValidation.IsIdentifier(text)))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, name + " is invalid.", false, "ManifestMalformed");
        return ReplicationValidationResult.Accepted();
    }

    private static bool IsIdentifierProperty(System.Text.Json.JsonElement body, string name) =>
        body.TryGetProperty(name, out System.Text.Json.JsonElement value) && value.ValueKind == System.Text.Json.JsonValueKind.String && ReplicationValidation.IsIdentifier(value.GetString());

    private static bool HashProperty(System.Text.Json.JsonElement body, string name, string expected) =>
        body.TryGetProperty(name, out System.Text.Json.JsonElement value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() == expected && ReplicationValidation.IsHash256(value.GetString());

    private static bool UIntProperty(System.Text.Json.JsonElement body, string name, out ulong value)
    {
        value = 0;
        return body.TryGetProperty(name, out System.Text.Json.JsonElement element) && element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetUInt64(out value);
    }

    private static bool ArrayProperty(System.Text.Json.JsonElement body, string name) => body.TryGetProperty(name, out System.Text.Json.JsonElement value) && value.ValueKind == System.Text.Json.JsonValueKind.Array;

    private static bool ValidRevisionVector(System.Text.Json.JsonElement value, int schemaEpoch)
    {
        if (value.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        string[] required = { "tickId", "gameRevision", "voxelWorldRevision", "chunkRevisionSet", "replicationRevision", "configRevision", "schemaEpoch" };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.Json.JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name) || Array.IndexOf(required, property.Name) < 0) return false;
        foreach (string name in required)
            if (!value.TryGetProperty(name, out _)) return false;
        if (!UIntProperty(value, "tickId", out _) || !UIntProperty(value, "gameRevision", out _) || !UIntProperty(value, "voxelWorldRevision", out _) ||
            !UIntProperty(value, "replicationRevision", out _) || !UIntProperty(value, "configRevision", out _) || !UIntProperty(value, "schemaEpoch", out ulong epoch) || epoch != (ulong)Math.Max(schemaEpoch, 0)) return false;
        System.Text.Json.JsonElement chunks = value.GetProperty("chunkRevisionSet");
        if (chunks.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (System.Text.Json.JsonProperty chunk in chunks.EnumerateObject())
            if (!ReplicationValidation.IsChunkId(chunk.Name) || chunk.Value.ValueKind != System.Text.Json.JsonValueKind.Number || !chunk.Value.TryGetUInt64(out _)) return false;
        return true;
    }

    private static bool ValidTombstones(System.Text.Json.JsonElement value)
    {
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
        foreach (System.Text.Json.JsonElement tombstone in value.EnumerateArray())
        {
            if (tombstone.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.Json.JsonProperty property in tombstone.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("netEntityId" or "untilRevision")) return false;
            if (!IsNetIdentifierProperty(tombstone, "netEntityId") || !UIntProperty(tombstone, "untilRevision", out _)) return false;
        }
        return true;
    }

    private static bool IsNetIdentifierProperty(System.Text.Json.JsonElement body, string name) =>
        body.TryGetProperty(name, out System.Text.Json.JsonElement value) && value.ValueKind == System.Text.Json.JsonValueKind.String && ReplicationValidation.IsNetId(value.GetString());

    private static bool IsErrorClass(System.Text.Json.JsonElement value) =>
        value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is "Retryable" or "Rejectable" or "Fatal";
#else
    private static ReplicationValidationResult ValidateBodyText(ReplicationEnvelopeMessageType messageType, ReplicationEnvelopeReliability reliability, string body, string mappingHash, int schemaEpoch)
    {
        string[] required = messageType switch
        {
            ReplicationEnvelopeMessageType.Handshake => new[] { "role" },
            ReplicationEnvelopeMessageType.FullSnapshot => new[] { "snapshotId", "tickId", "sessionRevisionVector", "schemaEpoch", "mappingSetHash" },
            ReplicationEnvelopeMessageType.BaselineAck => new[] { "snapshotId", "confirmedRevision" },
            ReplicationEnvelopeMessageType.Delta => new[] { "baseSnapshotId", "fromRevision", "toRevision", "mappingSetHash", "confirmationSequence", "tombstones" },
            ReplicationEnvelopeMessageType.DeltaAck => new[] { "confirmationSequence", "toRevision" },
            ReplicationEnvelopeMessageType.ResyncRequest => new[] { "resyncReason" },
            ReplicationEnvelopeMessageType.MaintenanceKick => new[] { "reasonCode" },
            ReplicationEnvelopeMessageType.Error => new[] { "errorClass", "reasonCode" },
            _ => Array.Empty<string>()
        };
        if (required.Length == 0) return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Message type is not registered.", false, "MessagePermissionDenied");
        foreach (string name in required)
            if (!body.Contains(string.Concat("\"", name, "\""), StringComparison.Ordinal)) return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Required body member is missing: " + name, false, "ManifestMalformed");
        if (messageType is ReplicationEnvelopeMessageType.FullSnapshot or ReplicationEnvelopeMessageType.Delta)
        {
            if (reliability != ReplicationEnvelopeReliability.Reliable) return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "State projections must be reliable.", false);
            if (!body.Contains(string.Concat("\"mappingSetHash\":\"", mappingHash, "\""), StringComparison.Ordinal)) return ReplicationValidationResult.Rejected(ReplicationValidationCode.MappingMismatch, "Mapping hash does not match.", true, "SnapshotBaseMismatch");
        }
        return ReplicationValidationResult.Accepted();
    }
#endif

    private static bool IsLowerHex(string value, int length)
    {
        if (value.Length != length) return false;
        foreach (char item in value)
            if (!((item >= '0' && item <= '9') || (item >= 'a' && item <= 'f'))) return false;
        return true;
    }
}

public static class BaselineSequenceValidator
{
    private static readonly ReplicationEnvelopeValidator Validator = new();

    public static ReplicationValidationResult ValidateSequence(ulong sequence, ulong expectedSequence) => Validator.ValidateSequence(sequence, expectedSequence);

    public static ReplicationValidationResult ValidateGeneration(ulong actualGeneration, ulong expectedGeneration) => Validator.ValidateGeneration(actualGeneration, expectedGeneration);

    public static ReplicationValidationResult ValidateBaseline(string snapshotId, ulong confirmedRevision, string expectedSnapshotId, ulong expectedRevision) => Validator.ValidateBaseline(snapshotId, confirmedRevision, expectedRevision: expectedRevision, expectedSnapshotId: expectedSnapshotId);

    public static ReplicationValidationResult ValidateRevision(ulong fromRevision, ulong toRevision) => Validator.ValidateRevision(fromRevision, toRevision);

    public static ReplicationValidationResult ValidateGap(bool gapDetected, string? resyncReason) => Validator.ValidateGap(gapDetected, resyncReason);
}
