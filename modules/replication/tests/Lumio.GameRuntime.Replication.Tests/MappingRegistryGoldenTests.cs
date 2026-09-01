using System;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.GeneratedContracts;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.Gen.CanonicalSerializer;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class MappingRegistryGoldenTests
{
    // Copied from architecture fixtures/valid/replication-mapping.json. Do not hand-edit generated contracts.
    private const string ValidMappingFixture =
        """
        {
          "mappingId": "health-authority-to-replica",
          "schemaVersion": 1,
          "source": { "entity": "player", "component": "HealthAuthority", "field": "current" },
          "target": { "entity": "player", "component": "HealthReplica", "field": "current" },
          "role": "ServerToClient",
          "owner": "AllClients",
          "visibility": "AOI",
          "delivery": { "reliability": "ReliableOnChange", "initial": true, "continuous": true, "quantization": "u16" },
          "lifecycle": "Continuous",
          "prediction": "Authoritative",
          "permission": "CanObserveHealth"
        }
        """;

    // Copied from architecture fixtures/invalid/replication-mapping-empty-field.json.
    private const string EmptyFieldFixture =
        """
        {
          "mappingId": "bad-mapping",
          "schemaVersion": 1,
          "source": { "entity": "player", "component": "HealthAuthority", "field": "" },
          "target": { "entity": "player", "component": "HealthReplica", "field": "current" },
          "role": "ServerToClient",
          "owner": "AllClients",
          "visibility": "AOI",
          "delivery": { "reliability": "ReliableOnChange", "initial": true, "continuous": true },
          "lifecycle": "Continuous",
          "prediction": "Authoritative"
        }
        """;

    private const string EmptyMappingSetJson = """{"digestDomain":"ReplicationMappingSetV1","mappings":[]}""";

    [Fact]
    public void MappingSetHashIsIndependentOfRegistrationOrder()
    {
        var first = new MappingRegistry();
        var second = new MappingRegistry();
        var a = MappingDescriptor.Create("mapping-actor-health", "Health", "current");
        var b = MappingDescriptor.Create("mapping-actor-transform", "Transform", "position");
        var c = MappingDescriptor.Create("mapping-voxel-chunk", "Voxel", "revision");
        Assert.True(first.Register(b).Succeeded);
        Assert.True(first.Register(c).Succeeded);
        Assert.True(first.Register(a).Succeeded);
        Assert.True(second.Register(a).Succeeded);
        Assert.True(second.Register(b).Succeeded);
        Assert.True(second.Register(c).Succeeded);
        Assert.Equal(first.View.MappingSetHash, second.View.MappingSetHash);
        Assert.Equal(PermutationGolden.Sha256, first.View.MappingSetHash);
        Assert.Equal(PermutationGolden.Sha256, second.View.MappingSetHash);
    }

    [Fact]
    public void ValidGeneratedFixtureLoadBindsMappingSetIdSchemaEpochAndHash()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(ValidMappingFixture);
        var registry = new MappingRegistry();
        MappingRegistrationResult result = registry.ValidateAndLoad(utf8);
        utf8[0] = (byte)'x';

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("health-authority-to-replica", registry.View.MappingSetId);
        Assert.Equal(GeneratedContractManifest.SchemaEpoch, registry.View.SchemaEpoch);
        Assert.Equal(Sha256Hex("""{"digestDomain":"ReplicationMappingSetV1","mappings":["health-authority-to-replica"]}"""), registry.View.MappingSetHash);
        Assert.Equal((byte)'{', registry.View.BoundInputBytes.Span[0]);
        MappingDescriptor mapping = Assert.Single(registry.View.Mappings);
        Assert.Equal(MappingRole.ServerToClient, mapping.Role);
        Assert.Equal(MappingOwner.AllClients, mapping.Owner);
        Assert.Equal(MappingVisibility.AOI, mapping.Visibility);
        Assert.Equal(MappingLifecycle.Continuous, mapping.Lifecycle);
        Assert.Equal(MappingPrediction.Authoritative, mapping.Prediction);
        Assert.Equal(MappingReliability.ReliableOnChange, mapping.Reliability);
        Assert.Equal("HealthAuthority", mapping.Source.Component);
        Assert.Equal("current", mapping.Source.Field);
        Assert.Equal("CanObserveHealth", mapping.Permission);
        Assert.Equal("u16", mapping.Quantization);
    }

    [Fact]
    public void EmptyGeneratedGoldenLoadBindsEmptyMappingSetHash()
    {
        var registry = new MappingRegistry();
        MappingRegistrationResult result = registry.ValidateAndLoad(Encoding.UTF8.GetBytes(EmptyMappingSetJson));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(EmptyGolden.Sha256, registry.View.MappingSetHash);
        Assert.Equal("a805f7c841f708981cc82a93047d7b0c8e6bf923f3dba18e179036741a6d2ea7", registry.View.MappingSetHash);
        Assert.Equal(GeneratedContractManifest.SchemaEpoch, registry.View.SchemaEpoch);
        Assert.Empty(registry.View.Mappings);
    }

    [Fact]
    public void UnsortedGeneratedPermutationGoldenLoadIsOrderIndependent()
    {
        const string unsorted =
            """{"digestDomain":"ReplicationMappingSetV1","mappings":["mapping-voxel-chunk","mapping-actor-transform","mapping-actor-health"]}""";
        var registry = new MappingRegistry();
        MappingRegistrationResult result = registry.ValidateAndLoad(Encoding.UTF8.GetBytes(unsorted));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(PermutationGolden.Sha256, registry.View.MappingSetHash);
        Assert.Equal(GeneratedContractManifest.SchemaEpoch, registry.View.SchemaEpoch);
    }

    [Fact]
    public void EmptyFieldUnknownMemberAndInvalidEnumsAreRejected()
    {
        var registry = new MappingRegistry();
        Assert.False(registry.ValidateAndLoad(Encoding.UTF8.GetBytes(EmptyFieldFixture)).Succeeded);
        Assert.False(registry.ValidateAndLoad(Encoding.UTF8.GetBytes(WithRole("NotARole"))).Succeeded);
        Assert.False(registry.ValidateAndLoad(Encoding.UTF8.GetBytes(WithVisibility("third-party-public"))).Succeeded);
        Assert.False(registry.ValidateAndLoad(Encoding.UTF8.GetBytes(WithLifecycle("Connected"))).Succeeded);
        Assert.False(registry.ValidateAndLoad(Encoding.UTF8.GetBytes(ValidMappingFixture.Replace("\"permission\": \"CanObserveHealth\"", "\"unknownRequired\": true, \"permission\": \"CanObserveHealth\"", StringComparison.Ordinal))).Succeeded);
        Assert.False(registry.ValidateAndLoad(Encoding.UTF8.GetBytes(ValidMappingFixture.Replace("\"role\": \"ServerToClient\",", "", StringComparison.Ordinal))).Succeeded);
        Assert.Empty(registry.View.Mappings);
    }

    private static string WithRole(string role) =>
        ValidMappingFixture.Replace("\"role\": \"ServerToClient\"", "\"role\": \"" + role + "\"", StringComparison.Ordinal);

    private static string WithVisibility(string visibility) =>
        ValidMappingFixture.Replace("\"visibility\": \"AOI\"", "\"visibility\": \"" + visibility + "\"", StringComparison.Ordinal);

    private static string WithLifecycle(string lifecycle) =>
        ValidMappingFixture.Replace("\"lifecycle\": \"Continuous\"", "\"lifecycle\": \"" + lifecycle + "\"", StringComparison.Ordinal);

    private static CanonicalGolden EmptyGolden =>
        Array.Find(CanonicalGoldens.All, value => value.Id == "replication-mapping-set-empty");

    private static CanonicalGolden PermutationGolden =>
        Array.Find(CanonicalGoldens.All, value => value.Id == "replication-mapping-set-permutation");

    private static string Sha256Hex(string canonical)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
