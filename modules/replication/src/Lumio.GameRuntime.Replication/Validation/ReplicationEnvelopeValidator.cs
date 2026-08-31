using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lumio.Gen.ContractTypes;
using Lumio.Gen.ProtocolPermissionValidator;

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
    RequiresResync,
    IntegrityMismatch,
    MessagePermissionDenied,
    RoleMismatch,
    ClaimNotGranted
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
/// queue. The pre-queue overloads run the generated ADR-022 gate before body
/// parsing so rejected input cannot reach application or queue state.
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

    /// <summary>Legacy overload retained fail-closed; queue admission requires an explicit context.</summary>
    public ReplicationValidationResult ValidateEnvelope(
        ReplicationEnvelope envelope,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null) =>
        ReplicationValidationResult.Rejected(ReplicationValidationCode.ClaimNotGranted,
            "Admission context with role, claims, and connection generation is required.", false, "ClaimNotGranted");

    /// <summary>Validates admission and envelope metadata at the pre-queue boundary.</summary>
    public ReplicationValidationResult ValidatePreQueue(
        ReplicationEnvelope envelope,
        ReplicationAdmissionContext admission,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null)
    {
        if (admission is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission context is required.", false);
        return ValidateEnvelopeCore(envelope, expectedSessionId, expectedProductId, expectedGameReleaseId,
            expectedMappingSetHash, expectedSchemaEpoch, expectedSequence, admission);
    }

    public ReplicationValidationResult ValidateEnvelope(
        ReplicationEnvelope envelope,
        ReplicationAdmissionContext admission,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null) =>
        ValidatePreQueue(envelope, admission, expectedMappingSetHash, expectedSchemaEpoch, expectedSequence);

    /// <summary>Validates using identities supplied by the admission context.</summary>
    public ReplicationValidationResult ValidatePreQueue(
        ReplicationEnvelope envelope,
        ReplicationAdmissionContext admission,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null)
    {
        if (admission is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission context is required.", false);
        return ValidateEnvelopeCore(envelope, admission.AdmittedSessionId ?? string.Empty,
            admission.AdmittedProductId ?? string.Empty, admission.AdmittedGameReleaseId ?? string.Empty,
            expectedMappingSetHash, expectedSchemaEpoch, expectedSequence, admission);
    }

    /// <summary>Alias retained for callers that use ValidateEnvelope for pre-queue validation.</summary>
    public ReplicationValidationResult ValidateEnvelope(
        ReplicationEnvelope envelope,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ReplicationAdmissionContext admission,
        ulong? expectedSequence = null)
    {
        if (admission is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission context is required.", false);
        return ValidateEnvelopeCore(envelope, expectedSessionId, expectedProductId, expectedGameReleaseId,
            expectedMappingSetHash, expectedSchemaEpoch, expectedSequence, admission);
    }

    /// <summary>Runs the generated gate directly when a caller already owns GateInput.</summary>
    public ReplicationValidationResult ValidatePreQueue(
        ReplicationEnvelope envelope,
        GateInput gateInput,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null)
    {
        if (envelope is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope is required.", false);
        ReplicationValidationResult gateResult = EvaluateGate(gateInput);
        if (!gateResult.Succeeded) return gateResult;
        if (!string.Equals(gateInput.SessionId, envelope.SessionId, StringComparison.Ordinal) ||
            !string.Equals(gateInput.ProductId, envelope.ProductId, StringComparison.Ordinal) ||
            !string.Equals(gateInput.GameReleaseId, envelope.GameReleaseId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Gate input does not describe the envelope.", false, "SessionMismatch");
        if (!Enum.IsDefined(typeof(ReplicationEnvelopeMessageType), envelope.MessageType) ||
            !string.Equals(gateInput.MessageId, ReplicationEnvelopeMessageTypeWire.Value(envelope.MessageType), StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.MessagePermissionDenied, "Gate input message ID does not describe the envelope message type.", false, "MessagePermissionDenied");
        if (!string.Equals(gateInput.SessionId, expectedSessionId, StringComparison.Ordinal) ||
            !string.Equals(gateInput.ProductId, expectedProductId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Gate input does not match the expected session identity.", false, "SessionMismatch");
        if (!string.Equals(gateInput.GameReleaseId, expectedGameReleaseId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Gate input does not match the expected release identity.", false, "ReleaseMismatch");
        var admission = new ReplicationAdmissionContext(
            gateInput.SessionId, gateInput.ProductId, gateInput.GameReleaseId, gateInput.MessageId,
            gateInput.Role, gateInput.Claims, gateInput.ConnectionGeneration,
            gateInput.AdmittedSessionId, gateInput.AdmittedProductId, gateInput.AdmittedGameReleaseId,
            gateInput.AdmittedRole, gateInput.AdmittedClaims, gateInput.AdmittedConnectionGeneration);
        ReplicationValidationResult shape = ValidateAdmissionShape(admission);
        if (!shape.Succeeded) return shape;
        return ValidateEnvelopeCore(envelope, expectedSessionId, expectedProductId, expectedGameReleaseId,
            expectedMappingSetHash, expectedSchemaEpoch, expectedSequence, null);
    }

    public ReplicationValidationResult Validate(
        ReplicationEnvelope envelope,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null) =>
        ValidateEnvelope(envelope, expectedSessionId, expectedProductId, expectedGameReleaseId,
            expectedMappingSetHash, expectedSchemaEpoch, expectedSequence);

    public ReplicationValidationResult Validate(
        ReplicationEnvelope envelope,
        ReplicationAdmissionContext admission,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence = null) =>
        ValidatePreQueue(envelope, admission, expectedSessionId, expectedProductId, expectedGameReleaseId,
            expectedMappingSetHash, expectedSchemaEpoch, expectedSequence);

    private ReplicationValidationResult ValidateEnvelopeCore(
        ReplicationEnvelope envelope,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId,
        string expectedMappingSetHash,
        int expectedSchemaEpoch,
        ulong? expectedSequence,
        ReplicationAdmissionContext? admission)
    {
        if (envelope is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope is required.", false);

        // This is deliberately the first operation after the null check. In
        // particular, a stale generation wins over malformed or hostile body text.
        if (admission is not null)
        {
            ReplicationValidationResult gate = ValidateAdmissionGate(
                envelope, admission, expectedSessionId, expectedProductId, expectedGameReleaseId);
            if (!gate.Succeeded) return gate;
        }

        if (!ReplicationValidation.IsIdentifier(expectedSessionId) || !ReplicationValidation.IsProductId(expectedProductId) || !ReplicationValidation.IsReleaseId(expectedGameReleaseId))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Expected session identity is invalid.", false);
        if (expectedSchemaEpoch < 0 || expectedSchemaEpoch != Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.SchemaMismatch, "Schema epoch does not match generated contracts.", true, "StaleEpoch");
        if (!ReplicationValidation.IsIdentifier(envelope.SessionId) || !ReplicationValidation.IsProductId(envelope.ProductId) || !ReplicationValidation.IsReleaseId(envelope.GameReleaseId) || !ReplicationValidation.IsTraceId(envelope.TraceId))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope identity is invalid.", false);
        if (envelope.SessionId != expectedSessionId || envelope.ProductId != expectedProductId)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Session or product mismatch.", false, "SessionMismatch");
        if (envelope.GameReleaseId != expectedGameReleaseId)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Release mismatch.", false, "ReleaseMismatch");
        if (!Enum.IsDefined(typeof(ReplicationEnvelopeMessageType), envelope.MessageType) || !Enum.IsDefined(typeof(ReplicationEnvelopeReliability), envelope.Reliability))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope message metadata is unknown.", false, "MessagePermissionDenied");
        if (envelope.ProtocolVersion == 0)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope protocol or length is invalid.", false);
        ReplicationValidationResult policyResult = ValidateTransportPolicy(envelope.TransportPolicy);
        if (!policyResult.Succeeded) return policyResult;
        if (envelope.Length > envelope.TransportPolicy.MaxMessageBytes)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope exceeds the transport message budget.", false, "CapacityExceeded");
        if (expectedSequence.HasValue)
        {
            ReplicationValidationResult sequence = ValidateSequence(envelope.Sequence, expectedSequence.Value);
            if (!sequence.Succeeded) return sequence;
        }

        if (envelope.Body is null || string.IsNullOrWhiteSpace(envelope.Body.Json))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body is required.", false);
        if ((ulong)Encoding.UTF8.GetByteCount(envelope.Body.Json) > envelope.TransportPolicy.MaxMessageBytes)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body exceeds the transport message budget.", false, "CapacityExceeded");
        bool projectionMessage = envelope.MessageType is ReplicationEnvelopeMessageType.FullSnapshot or ReplicationEnvelopeMessageType.Delta;
        if (projectionMessage && !ReplicationValidation.IsHash256(expectedMappingSetHash))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.MappingMismatch, "Mapping hash is invalid.", true, "SnapshotBaseMismatch");

        if (!StructuredJsonParser.TryParse(envelope.Body.Json, out StructuredJsonValue? body) || body is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body is malformed JSON.", false);
        if (body.Kind != StructuredJsonKind.Object)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body must be an object.", false);

        ReplicationValidationResult bodyResult = ValidateBody(envelope.MessageType, envelope.Reliability, body,
            expectedMappingSetHash, expectedSchemaEpoch);
        if (!bodyResult.Succeeded) return bodyResult;

        if (!StructuredJsonCanonicalizer.TryCanonicalize(body, out string canonicalBody))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body cannot be represented as CanonicalJsonV1.", false, "ManifestMalformed");

        return ValidateIntegrity(envelope.Integrity, canonicalBody);
    }

    private static ReplicationValidationResult ValidateAdmissionGate(
        ReplicationEnvelope envelope,
        ReplicationAdmissionContext admission,
        string expectedSessionId,
        string expectedProductId,
        string expectedGameReleaseId)
    {
        // Actual connection facts must be supplied by the session owner. They
        // are checked before GateInput construction so envelope metadata cannot
        // manufacture an authoritative admission identity.
        ReplicationValidationResult actual = ValidateActualAdmissionFields(admission);
        if (!actual.Succeeded) return actual;

        string[] claims = admission.Claims?.ToArray() ?? Array.Empty<string>();
        string[] admittedClaims = admission.AdmittedClaims?.ToArray() ?? Array.Empty<string>();
        var input = new GateInput(
            admission.SessionId!,
            admission.ProductId!,
            admission.GameReleaseId!,
            admission.MessageId!,
            admission.Role ?? string.Empty,
            claims,
            admission.ConnectionGeneration,
            admission.AdmittedSessionId ?? string.Empty,
            admission.AdmittedProductId ?? string.Empty,
            admission.AdmittedGameReleaseId ?? string.Empty,
            admission.AdmittedRole ?? string.Empty,
            admittedClaims,
            admission.AdmittedConnectionGeneration);

        ReplicationValidationResult gateResult = EvaluateGate(input);
        if (!gateResult.Succeeded) return gateResult;
        if (!Enum.IsDefined(typeof(ReplicationEnvelopeMessageType), envelope.MessageType) ||
            !string.Equals(admission.MessageId, ReplicationEnvelopeMessageTypeWire.Value(envelope.MessageType), StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.MessagePermissionDenied, "Admission message ID does not match the envelope message type.", false, "MessagePermissionDenied");
        if (!string.Equals(admission.SessionId, envelope.SessionId, StringComparison.Ordinal) ||
            !string.Equals(admission.ProductId, envelope.ProductId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission identity does not describe the envelope.", false, "SessionMismatch");
        if (!string.Equals(admission.GameReleaseId, envelope.GameReleaseId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission release does not describe the envelope.", false, "ReleaseMismatch");
        if (!string.Equals(admission.SessionId, expectedSessionId, StringComparison.Ordinal) ||
            !string.Equals(admission.ProductId, expectedProductId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission identity does not match the expected session.", false, "SessionMismatch");
        if (!string.Equals(admission.GameReleaseId, expectedGameReleaseId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission release does not match the expected release.", false, "ReleaseMismatch");
        return ValidateAdmissionShape(admission);
    }

    private static ReplicationValidationResult EvaluateGate(GateInput input)
    {
        // GateInput is generated and nullable-oblivious; sanitize arrays at the
        // boundary so malformed callers cannot turn a reject into an exception.
        var safe = new GateInput(
            input.SessionId ?? string.Empty,
            input.ProductId ?? string.Empty,
            input.GameReleaseId ?? string.Empty,
            input.MessageId ?? string.Empty,
            input.Role ?? string.Empty,
            input.Claims?.ToArray() ?? Array.Empty<string>(),
            input.ConnectionGeneration,
            input.AdmittedSessionId ?? string.Empty,
            input.AdmittedProductId ?? string.Empty,
            input.AdmittedGameReleaseId ?? string.Empty,
            input.AdmittedRole ?? string.Empty,
            input.AdmittedClaims?.ToArray() ?? Array.Empty<string>(),
            input.AdmittedConnectionGeneration);
        return ProtocolGate.Evaluate(safe, out string? rejectReason) == Verdict.Reject
            ? MapGateRejection(rejectReason)
            : ReplicationValidationResult.Accepted();
    }

    private static ReplicationValidationResult ValidateAdmissionShape(ReplicationAdmissionContext admission)
    {
        ReplicationValidationResult actual = ValidateActualAdmissionFields(admission);
        if (!actual.Succeeded) return actual;
        if (!IsRole(admission.Role) || !IsRole(admission.AdmittedRole))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.RoleMismatch, "Admission role is missing or invalid.", false, "RoleMismatch");
        if (admission.Claims is null || admission.AdmittedClaims is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.ClaimNotGranted, "Admission claims are missing.", false, "ClaimNotGranted");
        if (!ValidClaims(admission.Claims) || !ValidClaims(admission.AdmittedClaims))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.ClaimNotGranted, "Admission claims are invalid.", false, "ClaimNotGranted");
        if (!ReplicationValidation.IsIdentifier(admission.AdmittedSessionId) ||
            !ReplicationValidation.IsProductId(admission.AdmittedProductId) ||
            !ReplicationValidation.IsReleaseId(admission.AdmittedGameReleaseId) ||
            admission.AdmittedConnectionGeneration == 0)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission identity is invalid.", false, "InvalidArgument");
        if (!string.Equals(admission.SessionId, admission.AdmittedSessionId, StringComparison.Ordinal) ||
            !string.Equals(admission.ProductId, admission.AdmittedProductId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission session identity is not bound.", false, "SessionMismatch");
        if (!string.Equals(admission.GameReleaseId, admission.AdmittedGameReleaseId, StringComparison.Ordinal))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Admission release identity is not bound.", false, "ReleaseMismatch");
        return ReplicationValidationResult.Accepted();
    }

    private static ReplicationValidationResult ValidateActualAdmissionFields(ReplicationAdmissionContext admission)
    {
        if (!ReplicationValidation.IsIdentifier(admission.SessionId) ||
            !ReplicationValidation.IsProductId(admission.ProductId) ||
            !ReplicationValidation.IsReleaseId(admission.GameReleaseId) ||
            string.IsNullOrWhiteSpace(admission.MessageId) ||
            admission.ConnectionGeneration == 0)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Actual admission identity is required.", false, "InvalidArgument");
        return ReplicationValidationResult.Accepted();
    }

    private static ReplicationValidationResult MapGateRejection(string? reason) => reason switch
    {
        "StaleConnectionGeneration" => ReplicationValidationResult.Rejected(ReplicationValidationCode.StaleConnectionGeneration, "Connection generation is stale.", false, "StaleConnectionGeneration"),
        "SessionMismatch" => ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Session does not match admission.", false, "SessionMismatch"),
        "ReleaseMismatch" => ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Release does not match admission.", false, "ReleaseMismatch"),
        "MessagePermissionDenied" => ReplicationValidationResult.Rejected(ReplicationValidationCode.MessagePermissionDenied, "Message is not permitted by the generated gate.", false, "MessagePermissionDenied"),
        "RoleMismatch" => ReplicationValidationResult.Rejected(ReplicationValidationCode.RoleMismatch, "Role does not match admission.", false, "RoleMismatch"),
        "ClaimNotGranted" => ReplicationValidationResult.Rejected(ReplicationValidationCode.ClaimNotGranted, "Claim is not granted by admission.", false, "ClaimNotGranted"),
        _ => ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Generated admission gate rejected the message.", false, "MessagePermissionDenied")
    };

    private static ReplicationValidationResult ValidateTransportPolicy(ReplicationEnvelopeTransportPolicy? policy)
    {
        if (policy is null || policy.MaxMessageBytes is < 1 or > 1_048_576 || policy.MaxFragmentBytes is < 1 or > 65_536 || policy.MaxFragmentBytes > policy.MaxMessageBytes || policy.AntiReplayWindow == 0 ||
            !Enum.IsDefined(typeof(ReplicationEnvelopeTransportPolicyAuthBinding), policy.AuthBinding) || !Enum.IsDefined(typeof(ReplicationEnvelopeTransportPolicyErrorClass), policy.ErrorClass))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Transport policy is invalid.", false, "ManifestMalformed");
        return ReplicationValidationResult.Accepted();
    }

    private static ReplicationValidationResult ValidateIntegrity(ReplicationEnvelopeIntegrity? integrity, string body)
    {
        if (integrity is null || !Enum.IsDefined(typeof(ReplicationEnvelopeIntegrityAlgorithm), integrity.Algorithm) || string.IsNullOrEmpty(integrity.Value))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope integrity metadata is invalid.", false, "ManifestMalformed");

        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string? expected = integrity.Algorithm switch
        {
            ReplicationEnvelopeIntegrityAlgorithm.None => integrity.Value == "none" ? "none" : null,
            ReplicationEnvelopeIntegrityAlgorithm.CRC32C when IsLowerHex(integrity.Value, 8) => ReplicationValidation.Crc32CHex(bodyBytes),
            ReplicationEnvelopeIntegrityAlgorithm.SHA256 when ReplicationValidation.IsHash256(integrity.Value) => ReplicationValidation.Sha256Hex(bodyBytes),
            ReplicationEnvelopeIntegrityAlgorithm.AEAD when IsAeadValue(integrity.Value) => integrity.Value,
            _ => null
        };

        if (expected is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope integrity value is invalid.", false, "ManifestMalformed");
        if (integrity.Algorithm is ReplicationEnvelopeIntegrityAlgorithm.CRC32C or ReplicationEnvelopeIntegrityAlgorithm.SHA256 &&
            !ReplicationValidation.ConstantTimeEquals(integrity.Value, expected))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.IntegrityMismatch, "Envelope integrity does not match the body.", false, "ManifestMalformed");
        return ReplicationValidationResult.Accepted();
    }

    private static ReplicationValidationResult ValidateBody(
        ReplicationEnvelopeMessageType messageType,
        ReplicationEnvelopeReliability reliability,
        StructuredJsonValue body,
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
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.MessagePermissionDenied, "Message type is not registered.", false, "MessagePermissionDenied");
        }

        ReplicationValidationResult shape = ValidateObjectShape(body, allowed, required);
        if (!shape.Succeeded) return shape;

        switch (messageType)
        {
            case ReplicationEnvelopeMessageType.Handshake:
                return RequiredString(body, "role", false);
            case ReplicationEnvelopeMessageType.FullSnapshot:
                if (schemaEpoch < 0 || !IsIdentifierProperty(body, "snapshotId") || !UIntProperty(body, "tickId", out _) || !UIntProperty(body, "schemaEpoch", out ulong epoch) || epoch != (ulong)schemaEpoch ||
                    !HashProperty(body, "mappingSetHash", mappingHash) || !ValidRevisionVector(body.GetRequiredProperty("sessionRevisionVector"), schemaEpoch))
                    return ReplicationValidationResult.Rejected(ReplicationValidationCode.SchemaMismatch, "FullSnapshot metadata does not match the expected contract.", true, "StaleEpoch");
                return ReplicationValidationResult.Accepted();
            case ReplicationEnvelopeMessageType.BaselineAck:
                return IsIdentifierProperty(body, "snapshotId") && UIntProperty(body, "confirmedRevision", out _)
                    ? ReplicationValidationResult.Accepted()
                    : ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "BaselineAck metadata is invalid.", false, "ManifestMalformed");
            case ReplicationEnvelopeMessageType.Delta:
                if (!IsIdentifierProperty(body, "baseSnapshotId") || !UIntProperty(body, "fromRevision", out ulong from) || !UIntProperty(body, "toRevision", out ulong to) ||
                    to <= from || !HashProperty(body, "mappingSetHash", mappingHash) || !UIntProperty(body, "confirmationSequence", out _) || !ValidTombstones(body.GetRequiredProperty("tombstones")))
                    return ReplicationValidationResult.Rejected(ReplicationValidationCode.StaleRevision, "Delta revision metadata is invalid.", true, "RevisionConflict");
                bool gap = false;
                if (body.TryGetProperty("gapDetected", out StructuredJsonValue? gapValue))
                {
                    if (gapValue is null || gapValue.Kind is not (StructuredJsonKind.True or StructuredJsonKind.False))
                        return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "gapDetected must be boolean.", false, "ManifestMalformed");
                    gap = gapValue.Kind == StructuredJsonKind.True;
                }
                string? reason = null;
                if (body.TryGetProperty("resyncReason", out StructuredJsonValue? reasonValue))
                {
                    if (reasonValue is null || reasonValue.Kind != StructuredJsonKind.String)
                        return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "resyncReason must be a string.", false, "ManifestMalformed");
                    reason = reasonValue.Text;
                    if (string.IsNullOrWhiteSpace(reason))
                        return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "resyncReason is invalid.", false, "ManifestMalformed");
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
                    !body.GetRequiredProperty("errorClass").IsStringIn("Retryable", "Rejectable", "Fatal"))
                    return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Error metadata is invalid.", false, "ManifestMalformed");
                return ReplicationValidationResult.Accepted();
            default:
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.MessagePermissionDenied, "Message type is not registered.", false, "MessagePermissionDenied");
        }
    }

    private static ReplicationValidationResult ValidateObjectShape(
        StructuredJsonValue body,
        IReadOnlyList<string> allowed,
        IReadOnlyList<string> required)
    {
        if (body.Properties is null)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body must be an object.", false, "ManifestMalformed");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (StructuredJsonProperty property in body.Properties)
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Envelope body contains an unknown or duplicate member.", false, "ManifestMalformed");
        foreach (string name in required)
            if (!body.TryGetProperty(name, out _))
                return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, "Required body member is missing: " + name, false, "ManifestMalformed");
        return ReplicationValidationResult.Accepted();
    }

    private static ReplicationValidationResult RequiredString(StructuredJsonValue body, string name, bool identifier)
    {
        if (!body.TryGetProperty(name, out StructuredJsonValue? value) || value is null || value.Kind != StructuredJsonKind.String)
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, name + " must be a string.", false, "ManifestMalformed");
        if (string.IsNullOrWhiteSpace(value.Text) || (identifier && !ReplicationValidation.IsIdentifier(value.Text)))
            return ReplicationValidationResult.Rejected(ReplicationValidationCode.Invalid, name + " is invalid.", false, "ManifestMalformed");
        return ReplicationValidationResult.Accepted();
    }

    private static bool IsIdentifierProperty(StructuredJsonValue body, string name) =>
        body.TryGetProperty(name, out StructuredJsonValue? value) && value is not null && value.Kind == StructuredJsonKind.String && ReplicationValidation.IsIdentifier(value.Text);

    private static bool HashProperty(StructuredJsonValue body, string name, string expected) =>
        body.TryGetProperty(name, out StructuredJsonValue? value) && value is not null && value.Kind == StructuredJsonKind.String &&
        ReplicationValidation.IsHash256(value.Text) && ReplicationValidation.ConstantTimeEquals(value.Text, expected);

    private static bool UIntProperty(StructuredJsonValue body, string name, out ulong value)
    {
        value = 0;
        return body.TryGetProperty(name, out StructuredJsonValue? element) && element is not null && element.TryGetUInt64(out value);
    }

    private static bool ValidRevisionVector(StructuredJsonValue value, int schemaEpoch)
    {
        if (value.Kind != StructuredJsonKind.Object || value.Properties is null || schemaEpoch < 0) return false;
        string[] required = { "tickId", "gameRevision", "voxelWorldRevision", "chunkRevisionSet", "replicationRevision", "configRevision", "schemaEpoch" };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (StructuredJsonProperty property in value.Properties)
            if (!names.Add(property.Name) || Array.IndexOf(required, property.Name) < 0) return false;
        foreach (string name in required)
            if (!value.TryGetProperty(name, out _)) return false;
        if (!UIntProperty(value, "tickId", out _) || !UIntProperty(value, "gameRevision", out _) || !UIntProperty(value, "voxelWorldRevision", out _) ||
            !UIntProperty(value, "replicationRevision", out _) || !UIntProperty(value, "configRevision", out _) || !UIntProperty(value, "schemaEpoch", out ulong epoch) || epoch != (ulong)schemaEpoch)
            return false;
        if (!value.TryGetProperty("chunkRevisionSet", out StructuredJsonValue? chunks) || chunks is null || chunks.Kind != StructuredJsonKind.Object || chunks.Properties is null) return false;
        var chunkNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (StructuredJsonProperty chunk in chunks.Properties)
            if (!chunkNames.Add(chunk.Name) || !ReplicationValidation.IsChunkId(chunk.Name) || !chunk.Value.TryGetUInt64(out _)) return false;
        return true;
    }

    private static bool ValidTombstones(StructuredJsonValue value)
    {
        if (value.Kind != StructuredJsonKind.Array || value.Items is null) return false;
        foreach (StructuredJsonValue tombstone in value.Items)
        {
            if (tombstone.Kind != StructuredJsonKind.Object || tombstone.Properties is null) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (StructuredJsonProperty property in tombstone.Properties)
                if (!names.Add(property.Name) || property.Name is not ("netEntityId" or "untilRevision")) return false;
            if (!IsNetIdentifierProperty(tombstone, "netEntityId") || !UIntProperty(tombstone, "untilRevision", out _)) return false;
        }
        return true;
    }

    private static bool IsNetIdentifierProperty(StructuredJsonValue body, string name) =>
        body.TryGetProperty(name, out StructuredJsonValue? value) && value is not null && value.Kind == StructuredJsonKind.String && ReplicationValidation.IsNetId(value.Text);

    private static bool IsRole(string? value) => value is "Server" or "Client" or "Replay";

    private static bool ValidClaims(IReadOnlyList<string> claims)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? claim in claims)
            if (!ReplicationValidation.IsIdentifier(claim) || !values.Add(claim)) return false;
        return true;
    }

    private static bool IsLowerHex(string value, int length)
    {
        if (value.Length != length) return false;
        foreach (char item in value)
            if (!((item >= '0' && item <= '9') || (item >= 'a' && item <= 'f'))) return false;
        return true;
    }

    private static bool IsAeadValue(string value)
    {
        if (value.Length is < 24 or > 256) return false;
        foreach (char item in value)
            if (!((item >= 'A' && item <= 'Z') || (item >= 'a' && item <= 'z') || (item >= '0' && item <= '9') || item is '+' or '/' or '=' or '_' or '-')) return false;
        return true;
    }
}

internal static class StructuredJsonValueExtensions
{
    internal static StructuredJsonValue GetRequiredProperty(this StructuredJsonValue value, string name)
    {
        if (value.TryGetProperty(name, out StructuredJsonValue? result) && result is not null) return result;
        return StructuredJsonValue.Null();
    }

    internal static bool IsStringIn(this StructuredJsonValue value, params string[] candidates) =>
        value.Kind == StructuredJsonKind.String && candidates.Any(candidate => candidate == value.Text);
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
