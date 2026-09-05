using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Samples.Username;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class EntityBindingQueryTests
{
    public EntityBindingQueryTests()
    {
        EcsRegistry.Current = GeneratedRegistry.Instance;
    }

    [Fact]
    public void AdmitIssues128BitIdAndSecondConnectionIsAlreadyOnline()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        BindingQueryResult first = sut.Admit("C1", "acct-07", "room-01", "player");
        Assert.Equal("accepted", first.Outcome);
        sut.Manager.Tick();
        BindingQueryResult resolved = sut.ResolveByConnection("room-01", "C1");
        Assert.True(resolved.Binding.HasValue);
        Assert.True(NetEntityId.TryParse(resolved.Binding.Value.NetEntityId, out NetEntityId id));
        Assert.Equal(0x1000000000000001UL, id.InstanceId);
        Assert.True(id.Counter >= 1UL);

        BindingQueryResult second = sut.Admit("C2", "acct-07", "room-01", "player");
        Assert.Equal("account_already_online", second.Outcome);
        Assert.Null(second.Binding);
    }

    [Fact]
    public void ShapeErrorIsNotAccountAlreadyOnline()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        BindingQueryResult result = sut.Admit(new AdmitRequest
        {
            Connection = "C1",
            AccountId = "acct-07",
            RoomId = "room-01",
            EntityType = "player",
            AccountEntityRef = new object(),
        });
        Assert.Equal("request_error", result.Outcome);
        Assert.Equal("invalid_binding_shape", result.Code);
    }

    [Fact]
    public void BotNamespaceCannotBeAdmittedThroughAccountBinding()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        BindingQueryResult result = sut.Admit("C1", "acct-bot", "room-01", "bot");
        Assert.Equal("bot_namespace_admission_forbidden", result.Outcome);
        Assert.Null(result.Binding);
    }

    [Fact]
    public async Task AdmissionFromNonOwnerThreadIsRejectedBeforeWorldMutation()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        BindingQueryResult result = await Task.Run(() => sut.Admit("C1", "acct-thread", "room-01", "player"));

        Assert.Equal("request_error", result.Outcome);
        Assert.Equal("owner_thread_required", result.Code);
        Assert.False(sut.Manager.World.TryGetAccount("acct-thread", out _));
    }

    [Fact]
    public void EnqueuedAdmissionRunsOnOwnerTickAndAddressesWelcomeToConnection()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();

        Exception? enqueueError = null;
        Thread worker = new(() =>
        {
            try { sut.Manager.Enqueue(new AdmitConnectionMessage("C1", "acct-queued", "room-01", "player")); }
            catch (Exception error) { enqueueError = error; }
        });
        worker.Start();
        worker.Join();
        Assert.Null(enqueueError);

        sut.Manager.Tick();

        IReadOnlyList<WorldMessage> outbox = sut.Manager.DrainOutbox();
        WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(Assert.Single(outbox, static message => message is WelcomeMessage));
        Assert.Equal("C1", welcome.Connection);
        Assert.Contains(outbox, static message => message is WorldChangeMessage);
        Assert.Equal("ok", sut.ResolveByConnection("room-01", "C1").Outcome);
    }

    [Fact]
    public void EnqueuedDuplicateAdmissionProducesConnectionScopedError()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        sut.Manager.Enqueue(new AdmitConnectionMessage("C1", "acct-duplicate", "room-01", "player"));
        sut.Manager.Tick();
        WelcomeMessage firstWelcome = Assert.IsType<WelcomeMessage>(Assert.Single(sut.Manager.DrainOutbox(), static message => message is WelcomeMessage));

        sut.Manager.Enqueue(new AdmitConnectionMessage("C2", "acct-duplicate", "room-01", "player"));
        sut.Manager.Tick();

        ErrorMessage error = Assert.IsType<ErrorMessage>(Assert.Single(sut.Manager.DrainOutbox(), static message => message is ErrorMessage));
        Assert.Equal("C2", error.Connection);
        Assert.Equal("runtime_failure", error.Code);
        Assert.Contains("account_already_online", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(firstWelcome.Self.ToHex(), error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EnqueuedDisconnectRemovesBindingOnNextOwnerTick()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        sut.Manager.Enqueue(new AdmitConnectionMessage("C1", "acct-disconnect", "room-01", "player"));
        sut.Manager.Tick();
        sut.Manager.DrainOutbox();

        sut.Manager.Enqueue(new DisconnectConnectionMessage("C1"));
        sut.Manager.Tick();

        BindingQueryResult resolved = sut.ResolveByConnection("room-01", "C1");
        Assert.Equal("binding_not_found", resolved.Code);
    }

    [Fact]
    public void EnqueuedTakeoverPreservesEntityAndWelcomesNewGeneration()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        sut.Manager.Enqueue(new AdmitConnectionMessage("C1", "acct-takeover-queued", "room-01", "player"));
        sut.Manager.Tick();
        IReadOnlyList<WorldMessage> firstOutbox = sut.Manager.DrainOutbox();
        string netEntityId = Assert.Single(firstOutbox, static message => message is WelcomeMessage) is WelcomeMessage firstWelcome
            ? firstWelcome.Self.ToHex()
            : throw new Xunit.Sdk.XunitException("admission did not produce Welcome");

        sut.Manager.Enqueue(new RebindConnectionMessage("C2", "acct-takeover-queued", "room-01", "takeover"));
        sut.Manager.Tick();

        IReadOnlyList<WorldMessage> outbox = sut.Manager.DrainOutbox();
        ConnectionSupersededMessage superseded = Assert.IsType<ConnectionSupersededMessage>(Assert.Single(outbox, static message => message is ConnectionSupersededMessage));
        Assert.Equal("C1", superseded.Connection);
        Assert.Equal(2UL, superseded.NewConnectionGeneration);
        WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(Assert.Single(outbox, static message => message is WelcomeMessage));
        Assert.Equal("C2", welcome.Connection);
        Assert.Equal(netEntityId, welcome.Self.ToHex());
        Assert.Equal(2UL, welcome.ConnectionGeneration);
        Assert.Equal("binding_not_found", sut.ResolveByConnection("room-01", "C1").Code);
        Assert.Equal("ok", sut.ResolveByConnection("room-01", "C2").Outcome);
    }

    [Fact]
    public void DisposingQueryDetachesManagerControlAdapter()
    {
        EntityBindingQuery sut = TestBindingFactory.Create();
        sut.Dispose();

        Assert.Null(sut.Manager.DetachControlAdapter());
    }

    [Fact]
    public void QueryAttributeReadsChatTextAfterInput()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        BindingQueryResult admitted = sut.Admit("C1", "acct-07", "room-01", "player");
        sut.Manager.Tick();
        NetEntityId id = NetEntityId.Parse(sut.ResolveByConnection("room-01", "C1").Binding!.Value.NetEntityId);
        sut.Manager.Enqueue(new InputCommandMessage("chat.input", id, Encode("hello"), "C1"));
        sut.Manager.Tick();

        BindingQueryResult query = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = id.ToHex(),
                AttributeId = "ChatComponent.lastMessageText",
            });
        Assert.Equal("ok", query.Outcome);
        Assert.Equal("hello", query.Value);
    }

    [Fact]
    public void InvalidAttributeIdAndUndeclaredAndPersistOnlyInvisible()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        BindingQueryResult admitted = sut.Admit("C1", "acct-07", "room-01", "player");
        sut.Manager.Tick();
        string net = sut.ResolveByConnection("room-01", "C1").Binding!.Value.NetEntityId;

        BindingQueryResult sql = sut.QueryAttribute("client-replica", "room-01", net, "SELECT * FROM entities");
        Assert.Equal("invalid_attribute_id", sql.Code);

        BindingQueryResult undeclared = sut.QueryAttribute("client-replica", "room-01", net, "ChatComponent.notDeclared");
        Assert.Equal("undeclared_attribute", undeclared.Code);

        BindingQueryResult persist = sut.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "client-replica",
                RoomId = "room-01",
                NetEntityId = net,
                AttributeId = "ChatComponent.lastMessageText",
            }, "C1");
        Assert.Equal("invisible", persist.Outcome);

        BindingQueryResult account = sut.QueryAttribute("server-authoritative", "room-01", net, "EntityIdentity.accountId");
        Assert.Equal("undeclared_attribute", account.Code);
    }

    [Fact]
    public void RebindIncrementsGenerationAndExpireTombstones()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        BindingQueryResult admitted = sut.Admit("C1", "acct-07", "room-01", "player");
        sut.Manager.Tick();
        string net = sut.ResolveByConnection("room-01", "C1").Binding!.Value.NetEntityId;
        BindingQueryResult rebound = sut.Rebind("C1", "C1-next");
        Assert.Equal("accepted", rebound.Outcome);
        BindingQueryResult reboundBinding = sut.ResolveByConnection("room-01", "C1-next");
        Assert.Equal(net, reboundBinding.Binding!.Value.NetEntityId);
        Assert.Equal(2UL, reboundBinding.Binding.Value.ConnectionGeneration);

        BindingQueryResult stale = sut.QueryAttribute(new AttributeQueryRequest
        {
            CallerScope = "server-authoritative",
            RoomId = "room-01",
            NetEntityId = net,
            AttributeId = "EntityIdentity.entityType",
            ConnectionGeneration = 1,
        });
        Assert.Equal("stale_generation", stale.Outcome);

        BindingQueryResult expired = sut.Expire(net);
        Assert.Equal("accepted", expired.Outcome);
        sut.Manager.Tick();
        BindingQueryResult again = sut.QueryAttribute("server-authoritative", "room-01", net, "EntityIdentity.entityType");
        Assert.Equal("tombstoned", again.Outcome);
    }

    [Fact]
    public void TakeoverSupersedesThePreviousConnection()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        Assert.Equal("accepted", sut.Admit("C1", "acct-takeover", "room-01", "player").Outcome);
        sut.Manager.Tick();

        BindingQueryResult takeover = sut.Rebind("C2", "acct-takeover", "room-01", RebindMode.Takeover);

        Assert.Equal("accepted", takeover.Outcome);
        Assert.Equal("binding_not_found", sut.ResolveByConnection("room-01", "C1").Code);
        Assert.Equal("ok", sut.ResolveByConnection("room-01", "C2").Outcome);
        Assert.Contains(sut.Manager.DrainOutbox(), static message => message is ConnectionSupersededMessage);
    }

    [Fact]
    public void CreateFromSnapshotRebuildsAccountIndexAndAdmitRebinds()
    {
        using EntityBindingQuery original = TestBindingFactory.Create();
        BindingQueryResult first = original.Admit("C1", "acct-07", "room-01", "player");
        Assert.Equal("accepted", first.Outcome);
        original.Manager.Tick();
        NetEntityId id = NetEntityId.Parse(original.ResolveByConnection("room-01", "C1").Binding!.Value.NetEntityId);
        byte[] snapshot = original.Manager.CaptureSnapshot();

        EcsRegistry.Current = GeneratedRegistry.Instance;
        WorldManager restored = WorldManager.CreateFromSnapshot(snapshot);
        restored.Start(Thread.CurrentThread);
        using EntityBindingQuery query = EntityBindingQuery.Create(restored);
        BindingQueryResult again = query.Admit("C2", "acct-07", "room-01", "player");
        Assert.Equal("accepted", again.Outcome);
        Assert.Equal(id.ToHex(), query.ResolveByConnection("room-01", "C2").Binding!.Value.NetEntityId);
        Assert.True(restored.World.TryGetAccount("acct-07", out NetEntityId indexed));
        Assert.Equal(id, indexed);
    }

    [Fact]
    public void OwnerThreadControlsAreImmutableInternalWorldMessagesAndNotWireFrames()
    {
        WorldMessage[] controls =
        {
            new ExpireEntityMessage("expire-1", "00000000000000010000000000000001"),
            new ResolveBindingMessage("resolve-1", "room-01", "00000000000000010000000000000001"),
            new AttributeQueryMessage("attribute-1", "server-authoritative", "room-01", "00000000000000010000000000000001", "IdentityComponent.name"),
        };

        Assert.All(controls, control => Assert.Throws<ArgumentException>(() => WireCodec.EncodePack(control)));
        Assert.All(controls, control => Assert.All(control.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly), property => Assert.False(property.CanWrite)));
    }

    [Fact]
    public void EnqueuedQueriesRunOnOwnerTickAndDrainSeparatelyFromC1Frames()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        Assert.Equal("accepted", sut.Admit("C1", "acct-a2", "room-01", "player").Outcome);
        sut.Manager.Tick();
        _ = sut.Manager.DrainOutbox();
        NetEntityId id = NetEntityId.Parse(sut.ResolveByConnection("room-01", "C1").Binding!.Value.NetEntityId);

        sut.Manager.Enqueue(new ResolveBindingMessage("resolve-1", "room-01", id.ToHex(), connection: "C1"));
        sut.Manager.Enqueue(new AttributeQueryMessage("attribute-1", "server-authoritative", "room-01", id.ToHex(), "IdentityComponent.name"));

        Assert.Empty(sut.Manager.DrainOutbox().Queries);
        sut.Manager.Tick();

        WorldDrainResponse response = sut.Manager.DrainOutbox();
        IReadOnlyList<WorldMessage> frames = response.Frames;
        IReadOnlyList<WorldMessage> queries = response.Queries;
        Assert.DoesNotContain(frames, static message => message is ResolveBindingResult or AttributeQueryResult);
        ResolveBindingResult resolve = Assert.IsType<ResolveBindingResult>(Assert.Single(queries, static message => message is ResolveBindingResult));
        AttributeQueryResult attribute = Assert.IsType<AttributeQueryResult>(Assert.Single(queries, static message => message is AttributeQueryResult));
        Assert.Equal("resolve-1", resolve.RequestId);
        Assert.Equal("ok", resolve.Outcome);
        Assert.Equal(id.ToHex(), resolve.Binding!.Value.NetEntityId);
        Assert.Equal("attribute-1", attribute.RequestId);
        Assert.Equal("ok", attribute.Outcome);
        Assert.Equal(sut.Manager.World.Revision, attribute.ObservedRevision);
        Assert.Equal(sut.Manager.World.Tick - 1, attribute.ObservedTick);
    }

    [Fact]
    public void ExpiryIsOwnerThreadOrderedAndRepeatedExpiryReturnsTombstoned()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        Assert.Equal("accepted", sut.Admit("C1", "acct-expire", "room-01", "player").Outcome);
        sut.Manager.Tick();
        _ = sut.Manager.DrainOutbox();
        NetEntityId id = NetEntityId.Parse(sut.ResolveByConnection("room-01", "C1").Binding!.Value.NetEntityId);

        sut.Manager.Enqueue(new ExpireEntityMessage("expire-1", id.ToHex(), "C1"));
        Assert.True(sut.Manager.World.IsLive(id));
        sut.Manager.Tick();
        WorldDrainResponse firstResponse = sut.Manager.DrainOutbox();
        ExpireEntityResult first = Assert.IsType<ExpireEntityResult>(Assert.Single(firstResponse.Queries));
        Assert.Equal("expire-1", first.RequestId);
        Assert.Equal("accepted", first.Outcome);
        Assert.Contains(firstResponse.Frames, message => message is WorldChangeMessage change && change.Destroys.Any(destroyed => destroyed == id));

        sut.Manager.Enqueue(new ExpireEntityMessage("expire-2", id.ToHex()));
        sut.Manager.Tick();
        ExpireEntityResult second = Assert.IsType<ExpireEntityResult>(Assert.Single(sut.Manager.DrainOutbox().Queries));
        Assert.Equal("tombstoned", second.Outcome);
    }

    [Fact]
    public void QueryControlsReturnCorrelationAndClosedOutcomeMatrix()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        Assert.Equal("accepted", sut.Admit("C1", "acct-query", "room-01", "player").Outcome);
        sut.Manager.Tick();
        _ = sut.Manager.DrainOutbox();
        NetEntityId id = NetEntityId.Parse(sut.ResolveByConnection("room-01", "C1").Binding!.Value.NetEntityId);

        sut.Manager.Enqueue(new ResolveBindingMessage("bad", "room-01", "malformed"));
        sut.Manager.Enqueue(new ResolveBindingMessage("cross", "room-02", id.ToHex()));
        sut.Manager.Enqueue(new ResolveBindingMessage("stale", "room-01", id.ToHex(), connectionGeneration: 99));
        sut.Manager.Enqueue(new AttributeQueryMessage("invalid", "server-authoritative", "room-01", id.ToHex(), "SELECT *"));
        sut.Manager.Enqueue(new AttributeQueryMessage("invisible", "client-replica", "room-01", id.ToHex(), "ChatComponent.lastMessageText", connection: "C1"));
        sut.Manager.Enqueue(new AttributeQueryMessage("unauthorized", "client-replica", "room-01", id.ToHex(), "IdentityComponent.realName", connection: "unknown"));
        sut.Manager.Enqueue(new AttributeQueryMessage("missing", "server-authoritative", "room-01", new NetEntityId(id.InstanceId, id.Counter + 100).ToHex(), "IdentityComponent.name"));
        sut.Manager.Enqueue(new ExpireEntityMessage("expire", id.ToHex()));

        sut.Manager.Tick();

        IReadOnlyList<WorldMessage> queries = sut.Manager.DrainOutbox().Queries;
        Assert.Equal(8, queries.Count);
        Assert.Equal("request_error", Assert.IsType<ResolveBindingResult>(queries[0]).Outcome);
        Assert.Equal("invalid_binding_shape", Assert.IsType<ResolveBindingResult>(queries[0]).Code);
        Assert.Equal("request_error", Assert.IsType<ResolveBindingResult>(queries[1]).Outcome);
        Assert.Equal("cross_room_reference", Assert.IsType<ResolveBindingResult>(queries[1]).Code);
        Assert.Equal("stale_generation", Assert.IsType<ResolveBindingResult>(queries[2]).Outcome);
        Assert.Equal("request_error", Assert.IsType<AttributeQueryResult>(queries[3]).Outcome);
        Assert.Equal("invisible", Assert.IsType<AttributeQueryResult>(queries[4]).Outcome);
        Assert.Equal("unauthorized", Assert.IsType<AttributeQueryResult>(queries[5]).Outcome);
        Assert.Equal("non_existent", Assert.IsType<AttributeQueryResult>(queries[6]).Outcome);
        Assert.Equal("accepted", Assert.IsType<ExpireEntityResult>(queries[7]).Outcome);

        sut.Manager.DrainOutbox();
        sut.Manager.Enqueue(new AttributeQueryMessage("after", "server-authoritative", "room-01", id.ToHex(), "IdentityComponent.name"));
        sut.Manager.Tick();
        AttributeQueryResult after = Assert.IsType<AttributeQueryResult>(Assert.Single(sut.Manager.DrainOutbox().Queries));
        Assert.Equal("tombstoned", after.Outcome);
    }

    [Fact]
    public void RepeatedExpiryIntentInOneTickReturnsTombstonedForTheSecondRequest()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        Assert.Equal("accepted", sut.Admit("C1", "acct-same-tick", "room-01", "player").Outcome);
        sut.Manager.Tick();
        _ = sut.Manager.DrainOutbox();
        NetEntityId id = NetEntityId.Parse(sut.ResolveByConnection("room-01", "C1").Binding!.Value.NetEntityId);

        sut.Manager.Enqueue(new ExpireEntityMessage("expire-first", id.ToHex()));
        sut.Manager.Enqueue(new ExpireEntityMessage("expire-second", id.ToHex()));
        sut.Manager.Tick();

        IReadOnlyList<WorldMessage> queries = sut.Manager.DrainOutbox().Queries;
        Assert.Equal(new[] { "accepted", "tombstoned" }, queries.Cast<ExpireEntityResult>().Select(static result => result.Outcome));
    }

    [Fact]
    public void ClientA2ControlsAreNotDroppedAndProduceExplicitRequestErrors()
    {
        var registry = new ClientTestRegistry();
        EcsRegistry.Current = registry;
        using WorldManager manager = WorldManager.Create(registry);
        manager.Start(Thread.CurrentThread);
        manager.Enqueue(new ExpireEntityMessage("client-expire", "malformed"));
        manager.Enqueue(new ResolveBindingMessage("client-resolve", "room-01", "malformed"));

        manager.Tick();

        IReadOnlyList<WorldMessage> queries = manager.DrainOutbox().Queries;
        Assert.Equal(2, queries.Count);
        Assert.All(queries, static query => Assert.Equal("request_error", Assert.IsType<WorldControlRequestErrorResult>(query).Outcome));
    }

    [Fact]
    public void ResolveRetainedBindingPreservesItsRoomAfterDisconnect()
    {
        using EntityBindingQuery sut = TestBindingFactory.Create();
        Assert.Equal("accepted", sut.Admit("C1", "acct-retained", "room-retained", "player").Outcome);
        sut.Manager.Tick();
        _ = sut.Manager.DrainOutbox();
        NetEntityId id = NetEntityId.Parse(sut.ResolveByConnection("room-retained", "C1").Binding!.Value.NetEntityId);
        sut.Manager.Enqueue(new DisconnectConnectionMessage("C1"));
        sut.Manager.Tick();
        _ = sut.Manager.DrainOutbox();

        sut.Manager.Enqueue(new ResolveBindingMessage("retained", "room-retained", id.ToHex()));
        sut.Manager.Tick();

        ResolveBindingResult result = Assert.IsType<ResolveBindingResult>(Assert.Single(sut.Manager.DrainOutbox().Queries));
        Assert.Equal("ok", result.Outcome);
        Assert.Equal("room-retained", result.Binding!.Value.RoomId);
    }

    [Fact]
    public void ResultConstructorsRejectOpenOrMismatchedOutcomeShapes()
    {
        Assert.Throws<ArgumentException>(() => new ExpireEntityResult("r", "accepted", "unexpected", "detail"));
        Assert.Throws<ArgumentException>(() => new ResolveBindingResult("r", "ok"));
        Assert.Throws<ArgumentException>(() => new AttributeQueryResult("r", "request_error"));
        Assert.Throws<ArgumentException>(() => new AttributeQueryResult("r", "tombstoned", code: "unexpected"));
    }

    private static byte[] Encode(string text)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] bytes = new byte[4 + utf8.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, bytes, 4, utf8.Length);
        return bytes;
    }

    private abstract class ClientEntity
    {
    }

    private sealed class ClientTestRegistry : EcsRegistry
    {
        public override RegistrySide Side => RegistrySide.Client;
        public override Type WorldEntityType => typeof(ClientEntity);
        public override IReadOnlyList<Lumio.GameRuntime.Ecs.Annotations.FieldAttributeDeclaration> AttributeDeclarations => Array.Empty<Lumio.GameRuntime.Ecs.Annotations.FieldAttributeDeclaration>();
        public override Component[] CreateComponents(Type entityType) => Array.Empty<Component>();
        public override string WireName(Type entityType) => "client";
        public override bool TryResolveEntityType(string name, out Type entityType)
        {
            entityType = typeof(ClientEntity);
            return string.Equals(name, "client", StringComparison.Ordinal);
        }
        public override bool IsEntityType(Type concrete, Type query) => concrete == query;
    }
}
