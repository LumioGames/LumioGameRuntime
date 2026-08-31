using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Identity;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Validation;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ReviewFixTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void IntegrityDigestCoversTheActualBodyBytes()
    {
        var envelope = CreateEnvelope(
            ReplicationEnvelopeMessageType.FullSnapshot,
            "{\"snapshotId\":\"snap-1\",\"tickId\":1,\"sessionRevisionVector\":{\"tickId\":1,\"gameRevision\":1,\"voxelWorldRevision\":1,\"chunkRevisionSet\":{\"c:0:0:0\":1},\"replicationRevision\":1,\"configRevision\":1,\"schemaEpoch\":1},\"schemaEpoch\":1,\"mappingSetHash\":\"" + Hash + "\"}",
            Sha256Hex("placeholder"));

        ReplicationValidationResult result = ValidateAdmitted(new ReplicationEnvelopeValidator(), envelope);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void DeltaRejectsARevisionWithTheWrongSchemaEpoch()
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        var result = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 2, 1,
            new RevisionVector(2, 2, 2, 2, 2, 2, 2), mappings.View,
            Array.Empty<Lumio.GameRuntime.Replication.Mapping.TombstoneView>());

        Assert.Equal(ProjectionStatus.Rejected, result.Status);
        Assert.Equal("StaleEpoch", result.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void ActivationRequiresAnAcknowledgedBaseline()
    {
        var context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);

        ReplicationContextTransitionResult result = context.Activate();

        Assert.False(result.Succeeded);
        Assert.Equal(ReplicationContextState.AwaitingBaselineAck, result.State);
    }

    [Fact]
    public void DeltaCannotBeBuiltBeforeActiveState()
    {
        var context = CreateContext();
        var result = context.BuildDelta(
            "snap-1", 1, 2, 1,
            new RevisionVector(2, 2, 2, 2, 2, 2, 1),
            Array.Empty<Lumio.GameRuntime.Replication.Mapping.TombstoneView>());

        Assert.Equal(ProjectionStatus.Rejected, result.Status);
    }

    [Fact]
    public void RemovedNetworkIdentityCannotBeImmediatelyReused()
    {
        var table = new Lumio.GameRuntime.Replication.Mapping.NetEntityMappingTable();
        var id = Lumio.GameRuntime.Replication.Mapping.NetEntityId.Parse("00000000000000010000000000000001");
        var token = table.CaptureToken();
        Assert.True(table.Bind(id, "1:1", token).Succeeded);
        Assert.True(table.Remove(id, token));

        Assert.False(table.Bind(id, "1:2", token).Succeeded);
    }

    [Fact]
    public void HorizonBoundNetworkIdentityReportsItsTombstoneWithoutARevisionArgument()
    {
        var table = new Lumio.GameRuntime.Replication.Mapping.NetEntityMappingTable();
        var id = Lumio.GameRuntime.Replication.Mapping.NetEntityId.Parse("00000000000000010000000000000001");
        var token = table.CaptureToken();
        Assert.True(table.Bind(id, "1:1", token).Succeeded);
        Assert.True(table.Remove(id, 5, new Lumio.GameRuntime.Replication.Mapping.TombstoneHorizonResult(true, 10), token));

        Assert.True(table.IsTombstoned(id));
        Assert.True(table.IsTombstoned(id, 10));
    }

    [Fact]
    public void TwoArgumentHorizonCheckRequiresStrictlyPastDestroyRevision()
    {
        var horizon = new Lumio.GameRuntime.Replication.Mapping.TombstoneHorizonResult(true, 5);

        Assert.False(horizon.CanCollect(5, 10));
        Assert.False(horizon.CanCollect(6, 10));
    }

    [Fact]
    public void ProjectionBatchValidatesDigestCountsItAndReturnsCopies()
    {
        byte[] payload = { 1, 2, 3 };
        string payloadHash = Sha256Hex(payload);
        int serializedBytes = sizeof(uint) +
            sizeof(uint) + Encoding.UTF8.GetByteCount("mapping-a") +
            sizeof(uint) + payload.Length +
            sizeof(uint) + Encoding.UTF8.GetByteCount(payloadHash);
        var batch = new ProjectionBatch(1, serializedBytes - 1);

        Assert.Equal(ProjectionBatchStatus.QueueFull,
            batch.Add(new ProjectionBlock("mapping-a", payload, payloadHash)));

        var accepted = new ProjectionBatch(1, serializedBytes);
        Assert.Equal(ProjectionBatchStatus.Accepted,
            accepted.Add(new ProjectionBlock("mapping-a", payload, payloadHash)));
        Assert.Equal(serializedBytes, accepted.Bytes);
        payload[0] = 99;
        IReadOnlyList<ProjectionBlock> view = accepted.Blocks;
        view[0].Payload[0] = 88;
        Assert.Equal(1, accepted.Blocks[0].Payload[0]);
    }

    [Fact]
    public void PreQueueAdmissionContextAndSchedulerArePresent()
    {
        Assert.Contains(typeof(ReplicationEnvelopeValidator).GetMethods(), method => method.Name == "ValidatePreQueue");
        Assert.NotNull(typeof(ReplicationEnvelopeValidator).Assembly.GetType(
            "Lumio.GameRuntime.Replication.ReplicationScheduler"));
    }

    [Fact]
    public void GeneratedGateRejectsStaleGenerationBeforeMalformedBodyParsing()
    {
        var envelope = CreateEnvelope(ReplicationEnvelopeMessageType.Delta, "{not-json}", Sha256Hex("{not-json}"));
        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", "Delta",
            "Client", new[] { "replicate" }, 2,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 3);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, admission, "session-1", "product", "release-1", Hash, 1);

        Assert.Equal(ReplicationValidationCode.StaleConnectionGeneration, result.Code);
        Assert.Equal("StaleConnectionGeneration", result.GeneratedErrorId);
    }

    [Fact]
    public void EnvelopeValidationFailsClosedWithoutAdmissionContext()
    {
        string body = "{\"role\":\"Client\"}";
        var envelope = CreateEnvelope(ReplicationEnvelopeMessageType.Handshake, body, Sha256Hex(body));

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidateEnvelope(
            envelope, "session-1", "product", "release-1", Hash, 1);

        Assert.Equal(ReplicationValidationCode.ClaimNotGranted, result.Code);
        Assert.Equal("ClaimNotGranted", result.GeneratedErrorId);
    }

    [Fact]
    public void PreQueueRejectsBodyBytesOverTheTransportBudget()
    {
        string body = "{\"role\":\"" + new string('a', 80) + "\"}";
        var envelope = new ReplicationEnvelope(
            "session-1", "product", "release-1", 1, 64, 1,
            ReplicationEnvelopeMessageType.Handshake, ReplicationEnvelopeReliability.Reliable,
            new ReplicationEnvelopeIntegrity(ReplicationEnvelopeIntegrityAlgorithm.SHA256, Sha256Hex(body)),
            "trace-1",
            new ReplicationEnvelopeTransportPolicy(
                64, 64, 32,
                ReplicationEnvelopeTransportPolicyAuthBinding.SessionAdmission,
                ReplicationEnvelopeTransportPolicyErrorClass.Rejectable),
            new OpaqueJson(body));

        ReplicationValidationResult result = ValidateAdmitted(new ReplicationEnvelopeValidator(), envelope);

        Assert.Equal(ReplicationValidationCode.Invalid, result.Code);
        Assert.Equal("CapacityExceeded", result.GeneratedErrorId);
    }

    [Fact]
    public void GeneratedGateRejectsClaimsOutsideHandshakeAdmission()
    {
        string body = "{\"role\":\"Client\"}";
        var envelope = CreateEnvelope(ReplicationEnvelopeMessageType.Handshake, body, Sha256Hex(body));
        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", "Handshake",
            "Client", new[] { "admin" }, 3,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 3);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, admission, "session-1", "product", "release-1", Hash, 1);

        Assert.Equal(ReplicationValidationCode.ClaimNotGranted, result.Code);
        Assert.Equal("ClaimNotGranted", result.GeneratedErrorId);
    }

    [Fact]
    public void GeneratedGateAcceptsAnAdmittedEnvelopeBeforeBodyValidation()
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        FullSnapshotProjectionResult built = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-1",
            new RevisionVector(1, 1, 1, 1, 1, 1, 1), mappings.View);
        Assert.True(built.Succeeded);
        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", "FullSnapshot",
            "Client", new[] { "replicate" }, 1,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            built.Snapshot!.ToEnvelope("trace-1"), admission,
            "session-1", "product", "release-1", mappings.View.MappingSetHash, 1);

        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void DirectGateInputRejectsNullClaimsBeforeBodyParsing()
    {
        var envelope = CreateEnvelope(ReplicationEnvelopeMessageType.Handshake, "{not-json}", Sha256Hex("{not-json}"));
        var gateInput = new Lumio.Gen.ProtocolPermissionValidator.GateInput(
            "session-1", "product", "release-1", "Handshake", "Client", null!, 1,
            "session-1", "product", "release-1", "Client", null!, 1);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, gateInput, "session-1", "product", "release-1", Hash, 1);

        Assert.Equal(ReplicationValidationCode.ClaimNotGranted, result.Code);
        Assert.Equal("ClaimNotGranted", result.GeneratedErrorId);
    }

    [Fact]
    public void AdmissionMessageIdMustDescribeTheEnvelopeMessage()
    {
        string body = "{\"baseSnapshotId\":\"snap-1\",\"fromRevision\":1,\"toRevision\":2,\"mappingSetHash\":\"" + Hash + "\",\"confirmationSequence\":1,\"tombstones\":[]}";
        var envelope = CreateEnvelope(ReplicationEnvelopeMessageType.Delta, body, Sha256Hex(body));
        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", "Handshake", "Client", new[] { "replicate" }, 1,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, admission, "session-1", "product", "release-1", Hash, 1);

        Assert.Equal(ReplicationValidationCode.MessagePermissionDenied, result.Code);
        Assert.Equal("MessagePermissionDenied", result.GeneratedErrorId);
    }

    [Fact]
    public void UnknownDeltaAckDoesNotEvictRetainedAckCursor()
    {
        var history = new DeltaHistory(new ReplicationBudget(1, 4096, 8, 4096));
        var token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-1", 1, 2, 1, 10, Hash), token));
        Assert.Equal(DeltaAckStatus.Acknowledged, history.Acknowledge("snap-1", 1, 2, token));

        Assert.Equal(DeltaAckStatus.UnknownHistory, history.Acknowledge("snap-unknown", 1, 2, token));
        Assert.Equal(DeltaHistoryStatus.RevisionConflict,
            history.Add(new DeltaRecord("snap-1", 1, 2, 1, 10, Hash), token));
    }

    [Fact]
    public void SequenceOnlyDeltaAckDoesNotPoisonRevisionBearingAck()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));
        var token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-1", 1, 2, 1, 10, Hash), token));
        Assert.Equal(DeltaAckStatus.Acknowledged, history.Acknowledge("snap-1", 1, token));
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-1", 2, 3, 2, 10, Hash), token));

        Assert.Equal(DeltaAckStatus.Acknowledged,
            history.Acknowledge("snap-1", 2, 3, token));
    }

    [Fact]
    public void DeltaAckCannotJumpToAnUnknownSequence()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));
        var token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-1", 1, 2, 1, 10, Hash), token));

        Assert.Equal(DeltaAckStatus.UnknownHistory,
            history.Acknowledge("snap-1", 99, 2, token));
        Assert.Equal(1, history.Count);
        Assert.Equal(DeltaAckStatus.Acknowledged,
            history.Acknowledge("snap-1", 1, 2, token));
    }

    [Fact]
    public void OversizedBaselineDoesNotEvictTheAcknowledgedBaseline()
    {
        var store = new BaselineStore(new ReplicationBudget(1, 100, 8, 4096));
        var token = store.CaptureToken();
        Assert.Equal(BaselineStoreStatus.Accepted,
            store.Add(new BaselineRecord("snap-1", 1, 50, Hash), token));
        Assert.Equal(BaselineAckStatus.Acknowledged, store.Ack("snap-1", 1, token));

        Assert.Equal(BaselineStoreStatus.QueueFull,
            store.Add(new BaselineRecord("snap-2", 2, 101, Hash), token));
        Assert.True(store.IsAcknowledged("snap-1"));
    }

    [Fact]
    public void RejectedBaselineReplacementDoesNotPartiallyEvictHistory()
    {
        var store = new BaselineStore(new ReplicationBudget(2, 150, 8, 4096));
        var token = store.CaptureToken();
        Assert.Equal(BaselineStoreStatus.Accepted,
            store.Add(new BaselineRecord("snap-1", 1, 50, Hash), token));
        Assert.Equal(BaselineAckStatus.Acknowledged, store.Ack("snap-1", 1, token));
        Assert.Equal(BaselineStoreStatus.Accepted,
            store.Add(new BaselineRecord("snap-2", 2, 90, Hash), token));

        Assert.Equal(BaselineStoreStatus.QueueFull,
            store.Add(new BaselineRecord("snap-3", 3, 70, Hash), token));
        Assert.True(store.IsAcknowledged("snap-1"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void AcknowledgedBaselineEnablesDeltaAndAckReleasesHistory()
    {
        var context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult full = context.BuildFullSnapshot(
            "snap-1", new RevisionVector(1, 1, 1, 1, 1, 1, 1));
        Assert.True(full.Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged,
            context.AckBaseline("snap-1", full.Snapshot!.Revision.ReplicationRevision));
        Assert.True(context.Activate().Succeeded);

        DeltaProjectionResult delta = context.BuildDelta(
            "snap-1", 1, 2, 1, new RevisionVector(2, 2, 2, 2, 2, 2, 1),
            Array.Empty<Lumio.GameRuntime.Replication.Mapping.TombstoneView>());
        Assert.True(delta.Succeeded);
        Assert.True(context.Deltas.Count > 0);
        Assert.Equal(BaselineAckStatus.Acknowledged,
            context.AckDelta("snap-1", delta.Delta!.Sequence, 2));
        Assert.Equal(0, context.Deltas.Count);
    }

    [Fact]
    public void ResyncResetsHistoryAndCanResumeOnTheSameConnection()
    {
        var context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult first = context.BuildFullSnapshot(
            "snap-1", new RevisionVector(1, 1, 1, 1, 1, 1, 1));
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged,
            context.AckBaseline("snap-1", first.Snapshot!.Revision.ReplicationRevision));
        Assert.True(context.Activate().Succeeded);
        Assert.True(context.BuildDelta("snap-1", 1, 2, 1,
            new RevisionVector(2, 2, 2, 2, 2, 2, 1),
            Array.Empty<Lumio.GameRuntime.Replication.Mapping.TombstoneView>()).Succeeded);
        Assert.True(context.BeginResync().Succeeded);
        Assert.Equal(0, context.Deltas.Count);
        FullSnapshotProjectionResult replacement = context.BuildFullSnapshot(
            "snap-2", new RevisionVector(3, 3, 3, 3, 3, 3, 1));
        Assert.True(replacement.Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged,
            context.AckBaseline("snap-2", replacement.Snapshot!.Revision.ReplicationRevision));
        Assert.True(context.CompleteResync().Succeeded);
        Assert.True(context.BuildDelta("snap-2", 3, 4, 2,
            new RevisionVector(4, 4, 4, 4, 4, 4, 1),
            Array.Empty<Lumio.GameRuntime.Replication.Mapping.TombstoneView>()).Succeeded);
    }

    [Fact]
    public void AnOlderAcknowledgedBaselineCannotActivateAReplacementSnapshot()
    {
        var context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult first = context.BuildFullSnapshot(
            "snap-1", new RevisionVector(1, 1, 1, 1, 1, 1, 1));
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged,
            context.AckBaseline("snap-1", first.Snapshot!.Revision.ReplicationRevision));
        Assert.True(context.Activate().Succeeded);
        Assert.True(context.BeginResync().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-2",
            new RevisionVector(2, 2, 2, 2, 2, 2, 1)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);

        ReplicationContextTransitionResult activation = context.Activate();

        Assert.False(activation.Succeeded);
        Assert.Equal(ReplicationContextState.AwaitingBaselineAck, activation.State);
    }

    [Fact]
    public void StructuredValidationRejectsDuplicateTypesAndMalformedNumbers()
    {
        var validator = new ReplicationEnvelopeValidator();
        string duplicate = "{\"baseSnapshotId\":\"snap-1\",\"fromRevision\":1,\"toRevision\":2,\"mappingSetHash\":\"" + Hash + "\",\"confirmationSequence\":1,\"tombstones\":[],\"fromRevision\":1}";
        string wrongType = "{\"baseSnapshotId\":\"snap-1\",\"fromRevision\":\"1\",\"toRevision\":2,\"mappingSetHash\":\"" + Hash + "\",\"confirmationSequence\":1,\"tombstones\":[]}";

        Assert.Equal(ReplicationValidationCode.Invalid,
            ValidateAdmitted(validator,
                CreateEnvelope(ReplicationEnvelopeMessageType.Delta, duplicate, Sha256Hex(duplicate))).Code);
        Assert.NotEqual(ReplicationValidationCode.Accepted,
            ValidateAdmitted(validator,
                CreateEnvelope(ReplicationEnvelopeMessageType.Delta, wrongType, Sha256Hex(wrongType))).Code);
    }

    [Fact]
    public void RevisionVectorRejectsNonCanonicalChunkIds()
    {
        var chunks = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["c:+1:0:0"] = 1
        };
        var revision = new RevisionVector(1, 1, 1, chunks, 1, 1, 1);

        Assert.False(revision.IsValid);
    }

    [Fact]
    public void ProjectionRejectsProductAndReleaseIdsOutsideGeneratedPatterns()
    {
        var mappings = MappingSetView.Empty;
        var revision = new RevisionVector(1, 1, 1, 1, 1, 1, 1);
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));

        FullSnapshotProjectionResult productResult = projection.BuildFullSnapshot(
            "session-1", new string('p', 33), "release-1", "snap-1", revision, mappings);
        FullSnapshotProjectionResult releaseResult = projection.BuildFullSnapshot(
            "session-1", "product", "release:1", "snap-1", revision, mappings);

        Assert.Equal(ProjectionStatus.Rejected, productResult.Status);
        Assert.Equal(ProjectionStatus.Rejected, releaseResult.Status);
    }

    private static ReplicationContext CreateContext()
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        return new ReplicationContext("session-1", "product", "release-1", mappings.View,
            new ReplicationBudget(8, 4096, 8, 4096));
    }

    private static ReplicationValidationResult ValidateAdmitted(
        ReplicationEnvelopeValidator validator,
        ReplicationEnvelope envelope)
    {
        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", envelope.MessageType.ToString(),
            "Client", new[] { "replicate" }, 1,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);
        return validator.ValidatePreQueue(
            envelope, admission, "session-1", "product", "release-1", Hash, 1);
    }

    private static ReplicationEnvelope CreateEnvelope(
        ReplicationEnvelopeMessageType messageType,
        string body,
        string digest)
    {
        var policy = new ReplicationEnvelopeTransportPolicy(
            65536, 4096, 32,
            ReplicationEnvelopeTransportPolicyAuthBinding.SessionAdmission,
            ReplicationEnvelopeTransportPolicyErrorClass.Rejectable);
        return new ReplicationEnvelope(
            "session-1", "product", "release-1", 1, 256, 1,
            messageType, ReplicationEnvelopeReliability.Reliable,
            new ReplicationEnvelopeIntegrity(ReplicationEnvelopeIntegrityAlgorithm.SHA256, digest),
            "trace-1", policy, new OpaqueJson(body));
    }

    private static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));

    private static string Sha256Hex(byte[] value)
    {
        var builder = new StringBuilder();
        foreach (byte item in SHA256.HashData(value)) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
