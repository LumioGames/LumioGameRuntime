using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
        ReplicationValidationResult validation = ValidateAdmitted(
            new ReplicationEnvelopeValidator(), built.Snapshot!.ToEnvelope("trace-1"), mappings.View.MappingSetHash);

        Assert.True(validation.Succeeded, validation.Detail);
    }

    [Fact]
    public void UnknownBodyMemberAndGapWithoutReasonAreRejected()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var validator = new ReplicationEnvelopeValidator();
        var policy = new Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicy(65536, 4096, 32, Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicyAuthBinding.SessionAdmission, Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicyErrorClass.Rejectable);
        const string extraBody = "{\"baseSnapshotId\":\"snap-1\",\"confirmationSequence\":1,\"extra\":1,\"fromRevision\":1,\"gapDetected\":true,\"mappingSetHash\":\"" + hash + "\",\"toRevision\":2,\"tombstones\":[]}";
        var extraIntegrity = new Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrity(Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrityAlgorithm.SHA256, Sha256Hex(extraBody));
        var envelope = new Lumio.Gen.ContractTypes.ReplicationEnvelope("session-1", "product", "release-1", 1, 256, 1, Lumio.Gen.ContractTypes.ReplicationEnvelopeMessageType.Delta, Lumio.Gen.ContractTypes.ReplicationEnvelopeReliability.Reliable, extraIntegrity, "trace-1", policy,
            new Lumio.Gen.ContractTypes.OpaqueJson(extraBody));

        ReplicationValidationResult extra = ValidateAdmitted(validator, envelope, hash);
        Assert.False(extra.Succeeded);
        Assert.Equal(ReplicationValidationCode.Invalid, extra.Code);

        const string gapBody = "{\"baseSnapshotId\":\"snap-1\",\"confirmationSequence\":1,\"fromRevision\":1,\"gapDetected\":true,\"mappingSetHash\":\"" + hash + "\",\"toRevision\":2,\"tombstones\":[]}";
        var gapIntegrity = new Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrity(Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrityAlgorithm.SHA256, Sha256Hex(gapBody));
        var gapEnvelope = new Lumio.Gen.ContractTypes.ReplicationEnvelope("session-1", "product", "release-1", 1, 256, 1, Lumio.Gen.ContractTypes.ReplicationEnvelopeMessageType.Delta, Lumio.Gen.ContractTypes.ReplicationEnvelopeReliability.Reliable, gapIntegrity, "trace-1", policy,
            new Lumio.Gen.ContractTypes.OpaqueJson(gapBody));
        ReplicationValidationResult gap = ValidateAdmitted(validator, gapEnvelope, hash);
        Assert.Equal(ReplicationValidationCode.RequiresResync, gap.Code);
        Assert.True(gap.RequiresResync);
    }

    private static ReplicationValidationResult ValidateAdmitted(
        ReplicationEnvelopeValidator validator,
        Lumio.Gen.ContractTypes.ReplicationEnvelope envelope,
        string mappingSetHash)
    {
        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", envelope.MessageType.ToString(),
            "Client", new[] { "replicate" }, 1,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);
        return validator.ValidatePreQueue(
            envelope, admission, "session-1", "product", "release-1", mappingSetHash, 1);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
