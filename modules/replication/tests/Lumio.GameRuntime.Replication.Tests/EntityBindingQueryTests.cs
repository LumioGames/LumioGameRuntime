using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Replication.Mapping;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class EntityBindingQueryTests
{
    private const string FrozenBlob = "cadae092160bf5bc2c39fe1823a44cafa51594e5";
    private const string EntityTypeId = "EntityIdentity.entityType";
    private const string PersistOnlyId = "ChatComponent.lastMessagePersistOnly";
    private const string ClaimedId = "EntityIdentity.claimedMark";

    [Fact]
    public void BindingRecordExposesExactlyTheFiveTuple()
    {
        string[] names = typeof(ConnectionBinding)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "AccountId", "ConnectionGeneration", "EntityType", "NetEntityId", "RoomId" },
            names);
    }

    [Fact]
    public void VendoredContractBlobMatchesFrozenC2()
    {
        string path = Path.Combine(
            FindRepoRoot(),
            "modules",
            "replication",
            "contracts",
            "entity-binding-and-query-v1.json");
        Assert.True(File.Exists(path), path);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(FrozenBlob, GitBlobSha1(bytes));
    }

    [Fact]
    public void CreateConsumesMappingRegistryIdentitiesAndTwoEcsWorlds()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();

        Assert.NotNull(sut.Mappings);
        Assert.True(sut.Mappings.TryGet("mapping-entity-identity-entity-type", out MappingDescriptor? mapping));
        Assert.Equal("EntityIdentity", mapping!.Source.Component);
        Assert.Equal("entityType", mapping.Source.Field);
        Assert.NotNull(sut.Identities);
        Assert.NotNull(sut.Tombstones);
        Assert.NotEqual(sut.AuthoritativeWorld.WorldId, sut.ReplicaWorld.WorldId);
        Assert.Equal(EcsWorldState.Running, sut.AuthoritativeWorld.State);
        Assert.Equal(EcsWorldState.Running, sut.ReplicaWorld.State);
        Assert.Equal(Environment.CurrentManagedThreadId, sut.AuthoritativeWorld.OwnerThreadId);
    }

    [Fact]
    public void SelfLookupAfterAdmission()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));

        BindingQueryResult result = sut.SelfLookup("C1", "client-replica");

        AssertOk(result);
        ConnectionBinding binding = AssertBinding(result, "acct-07", "room-01", "N1", "player", 1);
        Assert.Equal(binding, result.Binding);
    }

    [Fact]
    public void ServerResolvesAdmittedConnection()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));

        BindingQueryResult result = sut.ResolveByConnection("room-01", "C1");

        AssertOk(result);
        AssertBinding(result, "acct-07", "room-01", "N1", "player", 1);
        Assert.True(result.AuthoritativeRevision.GetValueOrDefault() >= 1UL);
    }

    [Fact]
    public void ServerAuthoritativeAttributeRead()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));

        BindingQueryResult result = Query(sut, "N1", EntityTypeId, "server-authoritative");

        AssertOk(result);
        Assert.Equal("N1", result.NetEntityId);
        Assert.Equal("room-01", result.RoomId);
        Assert.Equal(EntityTypeId, result.AttributeId);
        Assert.Equal("player", result.Value);
        Assert.True(result.ObservedRevision.GetValueOrDefault() >= 1UL);
        Assert.True(result.ObservedTick.GetValueOrDefault() >= 1UL);
    }

    [Fact]
    public void ClientReplicaReadOfReplicatedVisibleAttribute()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));

        BindingQueryResult result = Query(sut, "N1", EntityTypeId, "client-replica", "C1");

        AssertOk(result);
        Assert.Equal("player", result.Value);
        Assert.True(result.ObservedRevision.GetValueOrDefault() >= 1UL);
        Assert.True(result.ObservedTick.GetValueOrDefault() >= 1UL);

        BindingQueryResult persist = Query(sut, "N1", PersistOnlyId, "client-replica", "C1");
        Assert.Equal("invisible", persist.Outcome);
        Assert.Null(persist.Value);
    }

    [Fact]
    public void RebindPreservesEntityAndIncrementsGeneration()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        AssertOk(sut.Rebind("C1", "C1-next"));

        BindingQueryResult current = sut.SelfLookup("C1-next", "client-replica");
        AssertOk(current);
        AssertBinding(current, "acct-07", "room-01", "N1", "player", 2);

        BindingQueryResult stale = Query(
            sut, "N1", EntityTypeId, "server-authoritative", connectionGeneration: 1);
        Assert.Equal("stale_generation", stale.Outcome);
        Assert.Null(stale.Code);
        Assert.Null(stale.Value);

        BindingQueryResult missingOld = sut.SelfLookup("C1", "client-replica");
        AssertRequestError(missingOld, "binding_not_found");
    }

    [Fact]
    public void OutcomeNonExistent()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));

        BindingQueryResult query = Query(sut, "N9", EntityTypeId, "server-authoritative");
        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", "N9", null, "server-authoritative");

        Assert.Equal("non_existent", query.Outcome);
        Assert.Equal("non_existent", resolve.Outcome);
        Assert.Null(query.Value);
        Assert.Null(resolve.NetEntityId);
        Assert.Null(query.Code);
    }

    [Fact]
    public void OutcomeStaleGeneration()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        AssertOk(sut.Rebind("C1", "C1-b"));
        AssertOk(sut.Rebind("C1-b", "C1-c"));

        BindingQueryResult result = Query(
            sut, "N1", EntityTypeId, "server-authoritative", connectionGeneration: 2);

        Assert.Equal("stale_generation", result.Outcome);
        Assert.Null(result.Value);
        Assert.Contains("selfLookup", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeInvisible()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        AssertOk(sut.Spawn("room-01", "N5", "player", replicateToReplica: false));

        BindingQueryResult missingReplica = Query(sut, "N5", EntityTypeId, "client-replica", "C1");
        BindingQueryResult persistOnly = Query(sut, "N1", PersistOnlyId, "client-replica", "C1");

        Assert.Equal("invisible", missingReplica.Outcome);
        Assert.Equal("invisible", persistOnly.Outcome);
        Assert.Null(missingReplica.Value);
        Assert.Null(persistOnly.Value);
        Assert.Null(missingReplica.Code);
    }

    [Fact]
    public void OutcomeUnauthorized()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));

        BindingQueryResult result = Query(sut, "N1", ClaimedId, "client-replica", "C1");

        Assert.Equal("unauthorized", result.Outcome);
        Assert.Null(result.Value);
        Assert.Null(result.Code);

        sut.GrantClaim("C1", ClaimedId);
        BindingQueryResult allowed = Query(sut, "N1", ClaimedId, "client-replica", "C1");
        AssertOk(allowed);
        Assert.Equal("mark", allowed.Value);
    }

    [Fact]
    public void OutcomeTombstoned()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        AssertOk(sut.Destroy("room-01", "N1"));

        BindingQueryResult query = Query(sut, "N1", EntityTypeId, "server-authoritative");
        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", "N1", null, "client-replica");

        Assert.Equal("tombstoned", query.Outcome);
        Assert.Equal("tombstoned", resolve.Outcome);
        Assert.Null(query.Value);
        Assert.NotEqual("N2", query.NetEntityId);
    }

    [Fact]
    public void DestroyedEntityDoesNotResolveToReplacement()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        AssertOk(sut.Destroy("room-01", "N1"));
        AssertOk(Admit(sut, connection: "C2", net: "N2"));

        BindingQueryResult destroyed = sut.ResolveByNetEntityId("room-01", "N1", null, "server-authoritative");
        BindingQueryResult replacement = sut.ResolveByNetEntityId("room-01", "N2", null, "server-authoritative");

        Assert.Equal("tombstoned", destroyed.Outcome);
        AssertOk(replacement);
        Assert.Equal("N2", replacement.NetEntityId);
        Assert.NotEqual("N1", replacement.NetEntityId);
    }

    [Fact]
    public void ForgottenTombstoneBecomesNonExistent()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        AssertOk(sut.Destroy("room-01", "N1"));
        AssertOk(sut.Forget("room-01", "N1"));

        BindingQueryResult result = Query(sut, "N1", EntityTypeId, "server-authoritative");
        Assert.Equal("non_existent", result.Outcome);
    }

    [Fact]
    public void SecondLiveBindInAnotherRoomIsRejected()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));

        BindingQueryResult second = Admit(sut, connection: "C-other", room: "room-02", net: "N8");
        AssertRequestError(second, "cross_room_reference");
        BindingQueryResult original = sut.SelfLookup("C1", "client-replica");
        AssertBinding(original, "acct-07", "room-01", "N1", "player", 1);
    }

    [Fact]
    public async Task OffThreadServerAuthoritativeReadDoesNotReturnStorage()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));

        BindingQueryResult result = await Task.Run(
            () => Query(sut, "N1", EntityTypeId, "server-authoritative"));

        Assert.NotEqual("ok", result.Outcome);
        Assert.Null(result.Value);
        AssertRequestError(result, "scope_violation");
    }

    [Fact]
    public void ArbitraryPropertyNameLookup()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        BindingQueryResult result = Query(sut, "N1", "last message text", "client-replica", "C1");
        AssertRequestError(result, "invalid_attribute_id");
    }

    [Fact]
    public void SqlExpressionPayload()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        BindingQueryResult result = Query(sut, "N1", "SELECT * FROM entities", "client-replica", "C1");
        AssertRequestError(result, "invalid_attribute_id");
    }

    [Fact]
    public void StoragePathAddressing()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        BindingQueryResult result = Query(
            sut, "N1", "Storage.tables.entity_row(42)", "server-authoritative");
        AssertRequestError(result, "storage_access_forbidden");
    }

    [Fact]
    public void UndeclaredAttributeId()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        BindingQueryResult result = Query(sut, "N1", "ChatComponent.notDeclared", "client-replica", "C1");
        AssertRequestError(result, "undeclared_attribute");
    }

    [Fact]
    public void CrossRoomResolution()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut, connection: "C7", account: "acct-77", room: "room-02", net: "N7"));

        BindingQueryResult result = Query(sut, "N7", EntityTypeId, "client-replica", "C7", roomId: "room-01");
        AssertRequestError(result, "cross_room_reference");
    }

    [Fact]
    public void ClientReadsPersistOnlyAttribute()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        BindingQueryResult result = Query(sut, "N1", PersistOnlyId, "client-replica", "C1");
        Assert.Equal("invisible", result.Outcome);
        Assert.Null(result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public void BindingRecordCarriesAccountEntityReference()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult result = sut.Bind(
            "C1",
            new BindingRecordRequest
            {
                AccountId = "acct-07",
                RoomId = "room-01",
                NetEntityId = "N1",
                EntityType = "player",
                ConnectionGeneration = 1,
                AccountEntityRef = "<live object handle>",
            });
        AssertRequestError(result, "invalid_binding_shape");
    }

    [Fact]
    public void ClientInvokesServerAuthoritativeScope()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        BindingQueryResult result = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = "N1",
                AttributeId = EntityTypeId,
                Origin = "client-connection",
            },
            "C1");
        AssertRequestError(result, "scope_violation");
    }

    [Fact]
    public void StoragePathSeparatorAddressing()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        BindingQueryResult result = Query(sut, "N1", "ecs/tables/entity_row", "server-authoritative");
        AssertRequestError(result, "storage_access_forbidden");
    }

    [Fact]
    public void MultiViolationShapeOutranksScopeAndStorage()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        AssertOk(Admit(sut));
        BindingQueryResult result = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = "N1",
                AttributeId = "Storage.tables.entity_row(42)",
                Origin = "client-connection",
                AccountEntityRef = "<live object handle>",
            },
            "C1");

        AssertRequestError(result, "invalid_binding_shape");
        Assert.Contains("scope_violation", result.Detail, StringComparison.Ordinal);
        Assert.Contains("storage_access_forbidden", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfLookupWithoutBindingIsBindingNotFound()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult result = sut.SelfLookup("missing", "client-replica");
        AssertRequestError(result, "binding_not_found");
    }

    private static BindingQueryResult Admit(
        EntityBindingQuery sut,
        string connection = "C1",
        string account = "acct-07",
        string room = "room-01",
        string net = "N1",
        string type = "player",
        ulong generation = 1) =>
        sut.Bind(
            connection,
            new BindingRecordRequest
            {
                AccountId = account,
                RoomId = room,
                NetEntityId = net,
                EntityType = type,
                ConnectionGeneration = generation,
            });

    private static BindingQueryResult Query(
        EntityBindingQuery sut,
        string netEntityId,
        string attributeId,
        string callerScope,
        string? callerConnection = null,
        ulong? connectionGeneration = null,
        string roomId = "room-01") =>
        sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = callerScope,
                RoomId = roomId,
                NetEntityId = netEntityId,
                AttributeId = attributeId,
                ConnectionGeneration = connectionGeneration,
            },
            callerConnection);

    private static void AssertOk(BindingQueryResult result)
    {
        Assert.Equal("ok", result.Outcome);
        Assert.Null(result.Code);
    }

    private static void AssertRequestError(BindingQueryResult result, string code)
    {
        Assert.Equal("request_error", result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.False(string.IsNullOrEmpty(result.Detail));
        Assert.Null(result.Value);
    }

    private static ConnectionBinding AssertBinding(
        BindingQueryResult result,
        string accountId,
        string roomId,
        string netEntityId,
        string entityType,
        ulong generation)
    {
        Assert.True(result.Binding.HasValue);
        ConnectionBinding binding = result.Binding.Value;
        Assert.Equal(accountId, binding.AccountId);
        Assert.Equal(roomId, binding.RoomId);
        Assert.Equal(netEntityId, binding.NetEntityId);
        Assert.Equal(entityType, binding.EntityType);
        Assert.Equal(generation, binding.ConnectionGeneration);
        return binding;
    }

    private static string GitBlobSha1(byte[] content)
    {
        byte[] header = Encoding.UTF8.GetBytes("blob " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0");
#pragma warning disable CA5350 // git hash-object identity is SHA-1
        using var sha = SHA1.Create();
#pragma warning restore CA5350
        sha.TransformBlock(header, 0, header.Length, null, 0);
        sha.TransformFinalBlock(content, 0, content.Length);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string FindRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(current, "modules")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Repository root was not found from " + AppContext.BaseDirectory);
    }
}
