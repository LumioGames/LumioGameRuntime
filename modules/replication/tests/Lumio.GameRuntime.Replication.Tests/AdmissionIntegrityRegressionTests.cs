using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Replication;
using Lumio.GameRuntime.Replication.Validation;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class AdmissionIntegrityRegressionTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void AdmissionRequiresActualIdentityInsteadOfUsingEnvelopeValues()
    {
        const string body = "{\"role\":\"Client\"}";
        var envelope = CreateEnvelope(ReplicationEnvelopeMessageType.Handshake, body, Sha256Hex(body));
        var admission = new ReplicationAdmissionContext(
            "Client", new[] { "replicate" }, 1,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, admission, "session-1", "product", "release-1", Hash, 1);

        Assert.False(result.Succeeded);
        Assert.Equal(ReplicationValidationCode.Invalid, result.Code);
        Assert.Equal("InvalidArgument", result.GeneratedErrorId);
    }

    [Fact]
    public void IntegrityUsesCanonicalJsonBytesRatherThanRawBodyBytes()
    {
        const string nonCanonicalBody = " { \"role\" : \"Client\" } ";
        var envelope = CreateEnvelope(
            ReplicationEnvelopeMessageType.Handshake,
            nonCanonicalBody,
            Sha256Hex(nonCanonicalBody));
        var admission = CompleteAdmission("Handshake");

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, admission, "session-1", "product", "release-1", Hash, 1);

        Assert.Equal(ReplicationValidationCode.IntegrityMismatch, result.Code);
        Assert.Equal("ManifestMalformed", result.GeneratedErrorId);
    }

    [Fact]
    public void CanonicalDigestAcceptsReorderedMembersAndAsciiEscapedUnicode()
    {
        const string body = "{\"tombstones\":[],\"resyncReason\":\"\u00e9\",\"toRevision\":2,\"mappingSetHash\":\"" + Hash + "\",\"fromRevision\":1,\"gapDetected\":true,\"confirmationSequence\":1,\"baseSnapshotId\":\"snap-1\"}";
        const string canonical = "{\"baseSnapshotId\":\"snap-1\",\"confirmationSequence\":1,\"fromRevision\":1,\"gapDetected\":true,\"mappingSetHash\":\"" + Hash + "\",\"resyncReason\":\"\\u00e9\",\"toRevision\":2,\"tombstones\":[]}";
        var envelope = CreateEnvelope(ReplicationEnvelopeMessageType.Delta, body, Sha256Hex(canonical));

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, CompleteAdmission("Delta"), "session-1", "product", "release-1", Hash, 1);

        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void GeneratedGateRejectsStaleGenerationBeforeParsingMalformedBody()
    {
        const string body = "{not-json}";
        var envelope = CreateEnvelope(ReplicationEnvelopeMessageType.Delta, body, Sha256Hex(body));
        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", "Delta",
            "Client", new[] { "replicate" }, 2,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, admission, "session-1", "product", "release-1", Hash, 1);

        Assert.Equal(ReplicationValidationCode.StaleConnectionGeneration, result.Code);
        Assert.Equal("StaleConnectionGeneration", result.GeneratedErrorId);
    }

    [Fact]
    public void AuthorityRevisionStoreRejectsInvalidNextVectorsBeforeComparison()
    {
        var initial = new RevisionVector(1, 1, 1, 1, 1, 1, 1);
        var invalidChunks = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["c:0:0:0"] = 1,
            ["c:+1:0:0"] = 2,
        };
        var next = new RevisionVector(2, 2, 2, invalidChunks, 2, 2, 1);
        var store = new AuthorityRevisionStore(initial);

        RevisionAdvanceResult result = store.Advance(next);

        Assert.Equal(RevisionAdvanceStatus.Rejected, result.Status);
        Assert.NotNull(result.Failure);
        Assert.Equal("InvalidArgument", result.Failure!.GeneratedErrorId);
        Assert.True(store.Current.Equals(initial));
    }

    private static ReplicationAdmissionContext CompleteAdmission(string messageId) =>
        new(
            "session-1", "product", "release-1", messageId,
            "Client", new[] { "replicate" }, 1,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);

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

    private static string Sha256Hex(string value)
    {
        var builder = new StringBuilder(64);
        foreach (byte item in SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
