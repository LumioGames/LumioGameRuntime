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
    private const string UnregisteredHex = "ffffffffffffffffffffffffffffffff";

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
    public void HostPublicSurfaceExposesAdmitDisconnectRebindExpireAndQueries()
    {
        string[] names = typeof(EntityBindingQuery)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("Admit", names);
        Assert.Contains("Disconnect", names);
        Assert.Contains("Rebind", names);
        Assert.Contains("Expire", names);
        Assert.Contains("SelfLookup", names);
        Assert.Contains("ResolveByConnection", names);
        Assert.Contains("ResolveByNetEntityId", names);
        Assert.Contains("QueryAttribute", names);
        Assert.Contains("ListBindings", names);

        MethodInfo fourArgAdmit = typeof(EntityBindingQuery)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Single(static method =>
                method.Name == "Admit" &&
                method.GetParameters().Length == 4 &&
                method.GetParameters().All(static parameter => parameter.ParameterType == typeof(string)));
        Assert.Equal(new[] { "connection", "accountId", "roomId", "entityType" },
            fourArgAdmit.GetParameters().Select(static parameter => parameter.Name).ToArray());
        Assert.DoesNotContain(fourArgAdmit.GetParameters(), static parameter =>
            parameter.Name is "netEntityId" or "NetEntityId");
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

    [Fact(DisplayName = "self_lookup_after_admission")]
    public void SelfLookupAfterAdmission()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult result = sut.SelfLookup("C1", "client-replica");

        AssertOk(result);
        ConnectionBinding binding = AssertBinding(result, "acct-07", "room-01", admitted.NetEntityId, "player", 1);
        Assert.Equal(binding, result.Binding);
    }

    [Fact(DisplayName = "server_resolves_admitted_connection")]
    public void ServerResolvesAdmittedConnection()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult result = sut.ResolveByConnection("room-01", "C1");

        AssertOk(result);
        AssertBinding(result, "acct-07", "room-01", admitted.NetEntityId, "player", 1);
        Assert.True(result.AuthoritativeRevision.GetValueOrDefault() >= 1UL);
    }

    [Fact(DisplayName = "server_authoritative_attribute_read")]
    public void ServerAuthoritativeAttributeRead()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult result = Query(sut, admitted.NetEntityId, EntityTypeId, "server-authoritative");

        AssertOk(result);
        Assert.Equal(admitted.NetEntityId, result.NetEntityId);
        Assert.Equal("room-01", result.RoomId);
        Assert.Equal(EntityTypeId, result.AttributeId);
        Assert.Equal("player", result.Value);
        Assert.True(result.ObservedRevision.GetValueOrDefault() >= 1UL);
        Assert.True(result.ObservedTick.GetValueOrDefault() >= 1UL);
    }

    [Fact(DisplayName = "client_replica_read_of_replicated_visible_attribute")]
    public void ClientReplicaReadOfReplicatedVisibleAttribute()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult result = Query(sut, admitted.NetEntityId, EntityTypeId, "client-replica", "C1");

        AssertOk(result);
        Assert.Equal("player", result.Value);
        Assert.True(result.ObservedRevision.GetValueOrDefault() >= 1UL);
        Assert.True(result.ObservedTick.GetValueOrDefault() >= 1UL);

        BindingQueryResult persist = Query(sut, admitted.NetEntityId, PersistOnlyId, "client-replica", "C1");
        Assert.Equal("invisible", persist.Outcome);
        Assert.Null(persist.Value);
    }

    [Fact(DisplayName = "rebind_preserves_entity_and_increments_generation")]
    public void RebindPreservesEntityAndIncrementsGeneration()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        AssertOk(sut.Rebind("C1", "C1-next"));

        BindingQueryResult current = sut.SelfLookup("C1-next", "client-replica");
        AssertOk(current);
        AssertBinding(current, "acct-07", "room-01", admitted.NetEntityId, "player", 2);

        BindingQueryResult stale = Query(
            sut, admitted.NetEntityId, EntityTypeId, "server-authoritative", connectionGeneration: 1);
        Assert.Equal("stale_generation", stale.Outcome);
        Assert.Null(stale.Code);
        Assert.Null(stale.Value);

        BindingQueryResult missingOld = sut.SelfLookup("C1", "client-replica");
        AssertRequestError(missingOld, "binding_not_found");
    }

    [Fact(DisplayName = "outcome_non_existent")]
    public void OutcomeNonExistent()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        Admit(sut);

        BindingQueryResult query = Query(sut, "N9", EntityTypeId, "server-authoritative");
        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", "N9", null, "server-authoritative");

        Assert.Equal("non_existent", query.Outcome);
        Assert.Equal("non_existent", resolve.Outcome);
        Assert.Null(query.Value);
        Assert.Null(resolve.NetEntityId);
        Assert.Null(query.Code);
    }

    [Fact(DisplayName = "outcome_stale_generation")]
    public void OutcomeStaleGeneration()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        AssertOk(sut.Rebind("C1", "C1-b"));
        AssertOk(sut.Rebind("C1-b", "C1-c"));

        BindingQueryResult result = Query(
            sut, admitted.NetEntityId, EntityTypeId, "server-authoritative", connectionGeneration: 2);

        Assert.Equal("stale_generation", result.Outcome);
        Assert.Null(result.Value);
        Assert.Contains("selfLookup", result.Detail, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "outcome_invisible")]
    public void OutcomeInvisible()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult spawned = sut.Spawn("room-01", "player", replicateToReplica: false);
        AssertOk(spawned);
        string n5 = spawned.NetEntityId!;

        BindingQueryResult missingReplica = Query(sut, n5, EntityTypeId, "client-replica", "C1");
        BindingQueryResult persistOnly = Query(sut, admitted.NetEntityId, PersistOnlyId, "client-replica", "C1");

        Assert.Equal("invisible", missingReplica.Outcome);
        Assert.Equal("invisible", persistOnly.Outcome);
        Assert.Null(missingReplica.Value);
        Assert.Null(persistOnly.Value);
        Assert.Null(missingReplica.Code);
    }

    [Fact(DisplayName = "outcome_unauthorized")]
    public void OutcomeUnauthorized()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult result = Query(sut, admitted.NetEntityId, ClaimedId, "client-replica", "C1");

        Assert.Equal("unauthorized", result.Outcome);
        Assert.Null(result.Value);
        Assert.Null(result.Code);

        sut.GrantClaim("C1", ClaimedId);
        BindingQueryResult allowed = Query(sut, admitted.NetEntityId, ClaimedId, "client-replica", "C1");
        AssertOk(allowed);
        Assert.Equal("mark", allowed.Value);
    }

    [Fact(DisplayName = "outcome_tombstoned")]
    public void OutcomeTombstoned()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        AssertOk(sut.Expire(admitted.NetEntityId));

        BindingQueryResult query = Query(sut, admitted.NetEntityId, EntityTypeId, "server-authoritative");
        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", admitted.NetEntityId, null, "client-replica");

        Assert.Equal("tombstoned", query.Outcome);
        Assert.Equal("tombstoned", resolve.Outcome);
        Assert.Null(query.Value);
        Assert.NotEqual("N2", query.NetEntityId);
    }

    [Fact(DisplayName = "arbitrary_property_name_lookup")]
    public void ArbitraryPropertyNameLookup()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult result = Query(sut, admitted.NetEntityId, "last message text", "client-replica", "C1");
        AssertRequestError(result, "invalid_attribute_id");
    }

    [Fact(DisplayName = "sql_expression_payload")]
    public void SqlExpressionPayload()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult result = Query(sut, admitted.NetEntityId, "SELECT * FROM entities", "client-replica", "C1");
        AssertRequestError(result, "invalid_attribute_id");
    }

    [Fact(DisplayName = "storage_path_addressing")]
    public void StoragePathAddressing()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult result = Query(
            sut, admitted.NetEntityId, "Storage.tables.entity_row(42)", "server-authoritative");
        AssertRequestError(result, "storage_access_forbidden");
    }

    [Fact(DisplayName = "undeclared_attribute_id")]
    public void UndeclaredAttributeId()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult result = Query(sut, admitted.NetEntityId, "ChatComponent.notDeclared", "client-replica", "C1");
        AssertRequestError(result, "undeclared_attribute");
        Assert.NotEqual("unauthorized", result.Code);
        Assert.NotEqual("unauthorized", result.Outcome);
    }

    [Fact(DisplayName = "cross_room_resolution")]
    public void CrossRoomResolution()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding n7 = Admit(sut, connection: "C7", account: "acct-77", room: "room-02");

        BindingQueryResult result = Query(sut, n7.NetEntityId, EntityTypeId, "client-replica", "C7", roomId: "room-01");
        AssertRequestError(result, "cross_room_reference");
    }

    [Fact(DisplayName = "client_reads_persist_only_attribute")]
    public void ClientReadsPersistOnlyAttribute()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult result = Query(sut, admitted.NetEntityId, PersistOnlyId, "client-replica", "C1");
        Assert.Equal("invisible", result.Outcome);
        Assert.Null(result.Code);
        Assert.Null(result.Value);
    }

    [Fact(DisplayName = "binding_record_carries_account_entity_reference")]
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

    [Fact(DisplayName = "client_invokes_server_authoritative_scope")]
    public void ClientInvokesServerAuthoritativeScope()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult result = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = admitted.NetEntityId,
                AttributeId = EntityTypeId,
                Origin = "client-connection",
            },
            "C1");
        AssertRequestError(result, "scope_violation");
    }

    [Fact(DisplayName = "storage_path_separator_addressing")]
    public void StoragePathSeparatorAddressing()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult result = Query(sut, admitted.NetEntityId, "ecs/tables/entity_row", "server-authoritative");
        AssertRequestError(result, "storage_access_forbidden");
    }

    [Fact(DisplayName = "multi_violation_shape_outranks_scope_and_storage")]
    public void MultiViolationShapeOutranksScopeAndStorage()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        BindingQueryResult result = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = admitted.NetEntityId,
                AttributeId = "Storage.tables.entity_row(42)",
                Origin = "client-connection",
                AccountEntityRef = "<live object handle>",
            },
            "C1");

        AssertRequestError(result, "invalid_binding_shape");
        Assert.Contains("scope_violation", result.Detail, StringComparison.Ordinal);
        Assert.Contains("storage_access_forbidden", result.Detail, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "runtime_issues_net_entity_id_on_admission")]
    public void RuntimeIssuesNetEntityIdOnAdmission()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult result = sut.Admit("C1", "acct-07", "room-01", "player");

        AssertOk(result);
        ConnectionBinding binding = AssertBinding(result, "acct-07", "room-01", result.Binding!.Value.NetEntityId, "player", 1);
        Assert.True(NetEntityId.TryParse(binding.NetEntityId, out NetEntityId parsed));
        Assert.True(sut.Identities.TryResolveLocal(parsed, out _));
        Assert.NotEqual("host-minted-N1", binding.NetEntityId);
        Assert.NotEqual("101", binding.NetEntityId);
        Assert.NotEqual("N1", binding.NetEntityId);
    }

    [Fact(DisplayName = "host_minted_net_entity_id")]
    public void HostMintedNetEntityId()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult result = sut.Bind(
            "C1",
            new BindingRecordRequest
            {
                AccountId = "acct-07",
                RoomId = "room-01",
                NetEntityId = "host-minted-N1",
                EntityType = "player",
                ConnectionGeneration = 1,
                MintedBy = "host",
            });

        AssertRequestError(result, "invalid_binding_shape");
        Assert.Null(result.Binding);

        BindingQueryResult decimalMint = sut.Bind(
            "C-decimal",
            new BindingRecordRequest
            {
                AccountId = "acct-07",
                RoomId = "room-01",
                NetEntityId = "101",
                EntityType = "player",
                ConnectionGeneration = 1,
            });
        AssertRequestError(decimalMint, "invalid_binding_shape");
        Assert.Null(decimalMint.Binding);
        AssertRequestError(sut.SelfLookup("C1", "client-replica"), "binding_not_found");
        AssertRequestError(sut.SelfLookup("C-decimal", "client-replica"), "binding_not_found");
    }

    [Fact(DisplayName = "binding_record_carries_session_id")]
    public void BindingRecordCarriesSessionId()
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
                SessionId = "sess-9",
            });
        AssertRequestError(result, "invalid_binding_shape");
        Assert.Null(result.Binding);
    }

    [Fact(DisplayName = "query_undeclared_account_id")]
    public void QueryUndeclaredAccountId()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult result = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = "N1",
                AttributeId = "EntityIdentity.accountId",
            });
        AssertRequestError(result, "undeclared_attribute");
        Assert.NotEqual("unauthorized", result.Code);
        Assert.NotEqual("unauthorized", result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public void BindRestoreRejectsWhenAnotherConnectionOrAccountOccupiesId()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult steal = sut.Bind(
            "C-other",
            new BindingRecordRequest
            {
                AccountId = "acct-other",
                RoomId = "room-01",
                NetEntityId = admitted.NetEntityId,
                EntityType = "player",
                ConnectionGeneration = 1,
            });

        AssertRequestError(steal, "invalid_binding_shape");
        Assert.Null(steal.Binding);
        AssertBinding(sut.SelfLookup("C1", "client-replica"), "acct-07", "room-01", admitted.NetEntityId, "player", 1);
        AssertRequestError(sut.SelfLookup("C-other", "client-replica"), "binding_not_found");
    }

    [Fact]
    public void AdmitIssuesRuntimeNetEntityIdAndRejectsHostSuppliedId()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        Assert.True(NetEntityId.TryParse(admitted.NetEntityId, out NetEntityId parsed));
        Assert.True(sut.Identities.TryResolveLocal(parsed, out _));
        Assert.Equal(1UL, admitted.ConnectionGeneration);

        BindingQueryResult hostSupplied = sut.Admit(
            new AdmitRequest
            {
                Connection = "C-host",
                AccountId = "acct-host",
                RoomId = "room-01",
                EntityType = "player",
                NetEntityId = admitted.NetEntityId,
            });
        AssertRequestError(hostSupplied, "invalid_binding_shape");
        Assert.Null(hostSupplied.Binding);
    }

    [Fact]
    public void BindRestoreOnlyRejectsUnregisteredNetEntityId()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult result = sut.Bind(
            "C1",
            new BindingRecordRequest
            {
                AccountId = "acct-07",
                RoomId = "room-01",
                NetEntityId = UnregisteredHex,
                EntityType = "player",
                ConnectionGeneration = 1,
            });
        AssertRequestError(result, "invalid_binding_shape");
        Assert.Null(result.Binding);
    }

    [Fact]
    public void BindRestoresIssuedIdentityAndRejectsSessionOrHostHandle()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        AssertOk(sut.Disconnect("C1"));

        BindingQueryResult restored = sut.Bind(
            "C-restore",
            new BindingRecordRequest
            {
                AccountId = admitted.AccountId,
                RoomId = admitted.RoomId,
                NetEntityId = admitted.NetEntityId,
                EntityType = admitted.EntityType,
                ConnectionGeneration = admitted.ConnectionGeneration,
            });
        AssertOk(restored);
        AssertBinding(restored, admitted.AccountId, admitted.RoomId, admitted.NetEntityId, admitted.EntityType, admitted.ConnectionGeneration);

        BindingQueryResult session = sut.Bind(
            "C-session",
            new BindingRecordRequest
            {
                AccountId = admitted.AccountId,
                RoomId = admitted.RoomId,
                NetEntityId = admitted.NetEntityId,
                EntityType = admitted.EntityType,
                ConnectionGeneration = 1,
                SessionId = "sess-1",
            });
        AssertRequestError(session, "invalid_binding_shape");

        BindingQueryResult handle = sut.Admit(
            new AdmitRequest
            {
                Connection = "C-handle",
                AccountId = "acct-handle",
                RoomId = "room-01",
                EntityType = "bot",
                HostHandle = new object(),
            });
        AssertRequestError(handle, "invalid_binding_shape");
    }

    [Fact]
    public void QueryAttributeRejectsSessionIdAndDoesNotMapUndeclaredToUnauthorized()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult session = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = admitted.NetEntityId,
                AttributeId = EntityTypeId,
                SessionId = "sess-1",
            });
        AssertRequestError(session, "invalid_binding_shape");

        BindingQueryResult accountId = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = admitted.NetEntityId,
                AttributeId = "EntityIdentity.accountId",
            });
        AssertRequestError(accountId, "undeclared_attribute");
        Assert.NotEqual("unauthorized", accountId.Code);
        Assert.NotEqual("unauthorized", accountId.Outcome);
    }

    [Fact]
    public void DisconnectThenReconnectPreservesNetEntityIdAndIncrementsGeneration()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        AssertOk(sut.Disconnect("C1"));

        BindingQueryResult missing = sut.SelfLookup("C1", "client-replica");
        AssertRequestError(missing, "binding_not_found");
        BindingQueryResult stillThere = sut.ResolveByNetEntityId("room-01", admitted.NetEntityId, null, "server-authoritative");
        AssertOk(stillThere);

        BindingQueryResult reconnected = sut.Rebind("C2", "acct-07", "room-01", RebindMode.Reconnect);
        AssertOk(reconnected);
        AssertBinding(reconnected, "acct-07", "room-01", admitted.NetEntityId, "player", 2);
    }

    [Fact]
    public void TakeoverRebindSupersedesLiveConnection()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult takeover = sut.Rebind("C2", "acct-07", "room-01", RebindMode.Takeover);
        AssertOk(takeover);
        AssertBinding(takeover, "acct-07", "room-01", admitted.NetEntityId, "player", 2);
        AssertRequestError(sut.SelfLookup("C1", "client-replica"), "binding_not_found");
        AssertOk(sut.SelfLookup("C2", "client-replica"));
    }

    [Fact]
    public void ExpireTombstonesAndNeverReusesNetEntityId()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding first = Admit(sut);
        AssertOk(sut.Expire(first.NetEntityId));
        Assert.True(NetEntityId.TryParse(first.NetEntityId, out NetEntityId parsed));
        Assert.True(sut.Tombstones.Contains(parsed));
        Assert.True(sut.Identities.IsTombstoned(parsed));

        BindingQueryResult query = Query(sut, first.NetEntityId, EntityTypeId, "server-authoritative");
        Assert.Equal("tombstoned", query.Outcome);

        ConnectionBinding second = Admit(sut, connection: "C2", account: "acct-08");
        Assert.NotEqual(first.NetEntityId, second.NetEntityId);

        BindingQueryResult revive = sut.Bind(
            "C-revive",
            new BindingRecordRequest
            {
                AccountId = "acct-revive",
                RoomId = "room-01",
                NetEntityId = first.NetEntityId,
                EntityType = "player",
                ConnectionGeneration = 1,
            });
        Assert.Equal("tombstoned", revive.Outcome);
        Assert.Null(revive.Code);
        Assert.Null(revive.Binding);
    }

    [Fact]
    public void IdentityTableTombstoneSurvivesProcessRestoreAndIsNeverReused()
    {
        ConnectionBinding first;
        IdentityTableSnapshot snapshot;
        using (EntityBindingQuery processA = EntityBindingQuery.Create())
        {
            first = Admit(processA);
            AssertOk(processA.Expire(first.NetEntityId));
            snapshot = processA.CaptureIdentityTable();
            Assert.Contains(snapshot.Records, record =>
                record.NetEntityId == first.NetEntityId && record.Tombstoned);
        }

        using EntityBindingQuery processB = EntityBindingQuery.Create(snapshot);
        ConnectionBinding second = Admit(processB, connection: "C2", account: "acct-08");
        Assert.NotEqual(first.NetEntityId, second.NetEntityId);

        BindingQueryResult query = Query(processB, first.NetEntityId, EntityTypeId, "server-authoritative");
        Assert.Equal("tombstoned", query.Outcome);

        BindingQueryResult bind = processB.Bind(
            "C-reuse",
            new BindingRecordRequest
            {
                AccountId = "acct-reuse",
                RoomId = "room-01",
                NetEntityId = first.NetEntityId,
                EntityType = "player",
                ConnectionGeneration = 1,
            });
        Assert.NotEqual("ok", bind.Outcome);
        Assert.Null(bind.Binding);
    }

    [Fact]
    public void ListBindingsReturnsActiveRoomMembersOnly()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding first = Admit(sut);
        ConnectionBinding second = Admit(sut, connection: "C2", account: "acct-08");
        Admit(sut, connection: "C3", account: "acct-09", room: "room-02");

        BindingQueryResult listed = sut.ListBindings("room-01");
        AssertOk(listed);
        Assert.NotNull(listed.Bindings);
        Assert.Equal(2, listed.Bindings!.Length);
        Assert.Contains(listed.Bindings, item => item.NetEntityId == first.NetEntityId);
        Assert.Contains(listed.Bindings, item => item.NetEntityId == second.NetEntityId);

        AssertOk(sut.Disconnect("C2"));
        BindingQueryResult afterDisconnect = sut.ListBindings("room-01");
        AssertOk(afterDisconnect);
        Assert.Single(afterDisconnect.Bindings!);
        Assert.Equal(first.NetEntityId, afterDisconnect.Bindings![0].NetEntityId);
    }

    [Fact]
    public void DestroyedEntityDoesNotResolveToReplacement()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding first = Admit(sut);
        AssertOk(sut.Expire(first.NetEntityId));
        ConnectionBinding replacement = Admit(sut, connection: "C2", account: "acct-08");

        BindingQueryResult destroyed = sut.ResolveByNetEntityId("room-01", first.NetEntityId, null, "server-authoritative");
        BindingQueryResult live = sut.ResolveByNetEntityId("room-01", replacement.NetEntityId, null, "server-authoritative");

        Assert.Equal("tombstoned", destroyed.Outcome);
        AssertOk(live);
        Assert.Equal(replacement.NetEntityId, live.NetEntityId);
        Assert.NotEqual(first.NetEntityId, live.NetEntityId);
    }

    [Fact]
    public void ForgottenTombstoneBecomesNonExistent()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        AssertOk(sut.Expire(admitted.NetEntityId));
        AssertOk(sut.Forget(admitted.RoomId, admitted.NetEntityId));

        BindingQueryResult result = Query(sut, admitted.NetEntityId, EntityTypeId, "server-authoritative");
        Assert.Equal("non_existent", result.Outcome);
    }

    [Fact]
    public void SecondLiveBindInAnotherRoomIsRejected()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding first = Admit(sut);

        BindingQueryResult second = sut.Admit("C-other", "acct-07", "room-02", "player");
        AssertRequestError(second, "cross_room_reference");
        BindingQueryResult original = sut.SelfLookup("C1", "client-replica");
        AssertBinding(original, "acct-07", "room-01", first.NetEntityId, "player", 1);
    }

    [Fact]
    public async Task OffThreadServerAuthoritativeReadDoesNotReturnStorage()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);

        BindingQueryResult result = await Task.Run(
            () => Query(sut, admitted.NetEntityId, EntityTypeId, "server-authoritative"));

        Assert.NotEqual("ok", result.Outcome);
        Assert.Null(result.Value);
        AssertRequestError(result, "scope_violation");
    }

    [Fact]
    public void SelfLookupWithoutBindingIsBindingNotFound()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult result = sut.SelfLookup("missing", "client-replica");
        AssertRequestError(result, "binding_not_found");
    }

    [Fact]
    public void ClientReplicaDoesNotLeakUnreplicatedForeignRoomOccupancy()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        Admit(sut);
        BindingQueryResult spawned = sut.Spawn("room-02", "player", replicateToReplica: false);
        AssertOk(spawned);
        string n7 = spawned.NetEntityId!;

        BindingQueryResult query = Query(sut, n7, EntityTypeId, "client-replica", "C1", roomId: "room-01");
        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", n7, null, "client-replica");

        Assert.True(query.Outcome == "non_existent" || query.Outcome == "invisible", query.Outcome);
        Assert.Null(query.Code);
        Assert.Null(query.Value);
        Assert.True(resolve.Outcome == "non_existent" || resolve.Outcome == "invisible", resolve.Outcome);
        Assert.Null(resolve.Code);
        Assert.NotEqual("cross_room_reference", query.Code);
        Assert.NotEqual("cross_room_reference", resolve.Code);
    }

    [Fact]
    public void ClientReplicaDoesNotLeakUnreplicatedForeignTombstone()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        Admit(sut);
        BindingQueryResult spawned = sut.Spawn("room-02", "player", replicateToReplica: false);
        AssertOk(spawned);
        string n7 = spawned.NetEntityId!;
        AssertOk(sut.Expire(n7));

        BindingQueryResult query = Query(sut, n7, EntityTypeId, "client-replica", "C1", roomId: "room-01");
        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", n7, null, "client-replica");

        Assert.True(query.Outcome == "non_existent" || query.Outcome == "invisible", query.Outcome);
        Assert.Null(query.Code);
        Assert.Null(query.Value);
        Assert.True(resolve.Outcome == "non_existent" || resolve.Outcome == "invisible", resolve.Outcome);
        Assert.Null(resolve.Code);
        Assert.NotEqual("cross_room_reference", query.Code);
        Assert.NotEqual("cross_room_reference", resolve.Code);
    }

    [Fact]
    public void DestroyedIdCannotBeRevivedBySpawnOrBind()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        AssertOk(sut.Expire(admitted.NetEntityId));

        BindingQueryResult spawn = sut.Spawn("room-01", admitted.NetEntityId, "player");
        BindingQueryResult bind = sut.Bind(
            "C-revive",
            new BindingRecordRequest
            {
                AccountId = "acct-revive",
                RoomId = "room-01",
                NetEntityId = admitted.NetEntityId,
                EntityType = "player",
                ConnectionGeneration = 1,
            });
        BindingQueryResult query = Query(sut, admitted.NetEntityId, EntityTypeId, "server-authoritative");
        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", admitted.NetEntityId, null, "server-authoritative");

        Assert.Equal("tombstoned", spawn.Outcome);
        Assert.Null(spawn.Code);
        Assert.Equal("tombstoned", bind.Outcome);
        Assert.Null(bind.Code);
        Assert.Equal("tombstoned", query.Outcome);
        Assert.Equal("tombstoned", resolve.Outcome);
        Assert.Null(query.Value);
    }

    [Fact]
    public void ForgottenIdCannotBeReusedByBindOrSpawn()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        AssertOk(sut.Expire(admitted.NetEntityId));
        AssertOk(sut.Forget(admitted.RoomId, admitted.NetEntityId));

        BindingQueryResult query = Query(sut, admitted.NetEntityId, EntityTypeId, "server-authoritative");
        BindingQueryResult bind = sut.Bind(
            "C-reuse",
            new BindingRecordRequest
            {
                AccountId = "acct-reuse",
                RoomId = "room-01",
                NetEntityId = admitted.NetEntityId,
                EntityType = "player",
                ConnectionGeneration = 1,
            });
        BindingQueryResult spawn = sut.Spawn("room-01", admitted.NetEntityId, "player");
        BindingQueryResult after = Query(sut, admitted.NetEntityId, EntityTypeId, "server-authoritative");
        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", admitted.NetEntityId, null, "server-authoritative");

        Assert.Equal("non_existent", query.Outcome);
        Assert.NotEqual("ok", bind.Outcome);
        Assert.Null(bind.Binding);
        Assert.NotEqual("ok", spawn.Outcome);
        Assert.Equal("non_existent", after.Outcome);
        Assert.Equal("non_existent", resolve.Outcome);
        Assert.Null(after.Value);
    }

    [Fact]
    public void DestroyedHexNetEntityIdIsRecordedInTombstoneRegistry()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        Assert.True(NetEntityId.TryParse(admitted.NetEntityId, out NetEntityId id));
        Assert.True(sut.Identities.TryResolveLocal(id, out _));

        BindingQueryResult expire = sut.Expire(admitted.NetEntityId);
        AssertOk(expire);
        Assert.True(sut.Tombstones.Contains(id));
        Assert.True(sut.Identities.IsTombstoned(id));

        BindingQueryResult resolve = sut.ResolveByNetEntityId("room-01", admitted.NetEntityId, null, "server-authoritative");
        Assert.Equal("tombstoned", resolve.Outcome);
        Assert.Null(resolve.Code);

        BindingQueryResult revive = sut.Spawn("room-01", admitted.NetEntityId, "player");
        Assert.NotEqual("ok", revive.Outcome);
        Assert.True(sut.Tombstones.Contains(id));
    }

    [Fact]
    public void ClientReplicaUnmappedDeclaredAttributeIsInvisible()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding admitted = Admit(sut);
        Assert.True(sut.Mappings.TryGet("mapping-entity-identity-entity-type", out MappingDescriptor? mapped));
        Assert.Equal("entityType", mapped!.Source.Field);
        Assert.False(HasSourceMapping(sut, "EntityIdentity", "unmappedMark"));

        BindingQueryResult result = Query(sut, admitted.NetEntityId, "EntityIdentity.unmappedMark", "client-replica", "C1");

        Assert.Equal("invisible", result.Outcome);
        Assert.Null(result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public void AdmitIssuesDistinctNetEntityIds()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        ConnectionBinding first = Admit(sut);
        ConnectionBinding second = Admit(sut, connection: "C2", account: "acct-08");
        Assert.NotEqual(first.NetEntityId, second.NetEntityId);
        Assert.True(NetEntityId.TryParse(first.NetEntityId, out _));
        Assert.True(NetEntityId.TryParse(second.NetEntityId, out _));
    }

    private static ConnectionBinding Admit(
        EntityBindingQuery sut,
        string connection = "C1",
        string account = "acct-07",
        string room = "room-01",
        string type = "player")
    {
        BindingQueryResult result = sut.Admit(connection, account, room, type);
        AssertOk(result);
        Assert.True(result.Binding.HasValue);
        return result.Binding.Value;
    }

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

    private static bool HasSourceMapping(EntityBindingQuery sut, string component, string field)
    {
        foreach (MappingDescriptor mapping in sut.Mappings.View.Mappings)
        {
            if (string.Equals(mapping.Source.Component, component, StringComparison.Ordinal) &&
                string.Equals(mapping.Source.Field, field, StringComparison.Ordinal))
                return true;
        }

        return false;
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
