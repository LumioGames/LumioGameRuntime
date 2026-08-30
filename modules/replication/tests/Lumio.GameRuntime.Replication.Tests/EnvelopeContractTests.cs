using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Validation;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class EnvelopeContractTests
{
    [Fact]
    public void ProjectionEnvelopePassesGeneratedMetadataValidation()
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        var revision = new RevisionVector(4, 8, 2, 1, 3, 1, 1);
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));

        FullSnapshotProjectionResult built = projection.BuildFullSnapshot("session-1", "product", "release-1", "snap-4", revision, mappings.View);
        Assert.True(built.Succeeded);
        ReplicationValidationResult validation = new ReplicationEnvelopeValidator().ValidateEnvelope(
            built.Snapshot!.ToEnvelope("trace-1"), "session-1", "product", "release-1", mappings.View.MappingSetHash, 1);

        Assert.True(validation.Succeeded, validation.Detail);
    }

    [Fact]
    public void UnknownBodyMemberAndGapWithoutReasonAreRejected()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var validator = new ReplicationEnvelopeValidator();
        var policy = new Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicy(65536, 4096, 32, Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicyAuthBinding.SessionAdmission, Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicyErrorClass.Rejectable);
        var integrity = new Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrity(Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrityAlgorithm.SHA256, hash);
        var envelope = new Lumio.Gen.ContractTypes.ReplicationEnvelope("session-1", "product", "release-1", 1, 256, 1, Lumio.Gen.ContractTypes.ReplicationEnvelopeMessageType.Delta, Lumio.Gen.ContractTypes.ReplicationEnvelopeReliability.Reliable, integrity, "trace-1", policy,
            new Lumio.Gen.ContractTypes.OpaqueJson("{\"baseSnapshotId\":\"snap-1\",\"fromRevision\":1,\"toRevision\":2,\"mappingSetHash\":\"" + hash + "\",\"confirmationSequence\":1,\"tombstones\":[],\"gapDetected\":true,\"extra\":1}"));

        ReplicationValidationResult extra = validator.ValidateEnvelope(envelope, "session-1", "product", "release-1", hash, 1);
        Assert.False(extra.Succeeded);
        Assert.Equal(ReplicationValidationCode.Invalid, extra.Code);

        var gapEnvelope = new Lumio.Gen.ContractTypes.ReplicationEnvelope("session-1", "product", "release-1", 1, 256, 1, Lumio.Gen.ContractTypes.ReplicationEnvelopeMessageType.Delta, Lumio.Gen.ContractTypes.ReplicationEnvelopeReliability.Reliable, integrity, "trace-1", policy,
            new Lumio.Gen.ContractTypes.OpaqueJson("{\"baseSnapshotId\":\"snap-1\",\"fromRevision\":1,\"toRevision\":2,\"mappingSetHash\":\"" + hash + "\",\"confirmationSequence\":1,\"tombstones\":[],\"gapDetected\":true}"));
        ReplicationValidationResult gap = validator.ValidateEnvelope(gapEnvelope, "session-1", "product", "release-1", hash, 1);
        Assert.Equal(ReplicationValidationCode.RequiresResync, gap.Code);
        Assert.True(gap.RequiresResync);
    }
}
