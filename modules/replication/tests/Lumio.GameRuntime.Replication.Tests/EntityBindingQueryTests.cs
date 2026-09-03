using System;
using System.Threading;
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
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult first = sut.Admit("C1", "acct-07", "room-01", "player");
        Assert.Equal("ok", first.Outcome);
        Assert.True(first.Binding.HasValue);
        Assert.True(NetEntityId.TryParse(first.Binding.Value.NetEntityId, out NetEntityId id));
        Assert.Equal(0x1000000000000001UL, id.InstanceId);
        Assert.True(id.Counter >= 1UL);

        BindingQueryResult second = sut.Admit("C2", "acct-07", "room-01", "player");
        Assert.Equal("account_already_online", second.Outcome);
        Assert.Equal(first.Binding.Value.NetEntityId, second.NetEntityId);
    }

    [Fact]
    public void ShapeErrorIsNotAccountAlreadyOnline()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
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
    public void QueryAttributeReadsChatTextAfterInput()
    {
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult admitted = sut.Admit("C1", "acct-07", "room-01", "player");
        NetEntityId id = NetEntityId.Parse(admitted.Binding!.Value.NetEntityId);
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
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult admitted = sut.Admit("C1", "acct-07", "room-01", "player");
        string net = admitted.Binding!.Value.NetEntityId;

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
        using EntityBindingQuery sut = EntityBindingQuery.Create();
        BindingQueryResult admitted = sut.Admit("C1", "acct-07", "room-01", "player");
        string net = admitted.Binding!.Value.NetEntityId;
        BindingQueryResult rebound = sut.Rebind("C1", "C1-next");
        Assert.Equal("ok", rebound.Outcome);
        Assert.Equal(net, rebound.Binding!.Value.NetEntityId);
        Assert.Equal(2UL, rebound.Binding.Value.ConnectionGeneration);

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
        Assert.Equal("tombstoned", expired.Outcome);
        BindingQueryResult again = sut.QueryAttribute("server-authoritative", "room-01", net, "EntityIdentity.entityType");
        Assert.Equal("tombstoned", again.Outcome);
    }

    [Fact]
    public void CreateFromSnapshotRebuildsAccountIndexAndAdmitRebinds()
    {
        using EntityBindingQuery original = EntityBindingQuery.Create();
        BindingQueryResult first = original.Admit("C1", "acct-07", "room-01", "player");
        Assert.Equal("ok", first.Outcome);
        NetEntityId id = NetEntityId.Parse(first.Binding!.Value.NetEntityId);
        byte[] snapshot = original.Manager.CaptureSnapshot();

        EcsRegistry.Current = GeneratedRegistry.Instance;
        WorldManager restored = WorldManager.CreateFromSnapshot(snapshot);
        restored.Start(Thread.CurrentThread);
        using EntityBindingQuery query = EntityBindingQuery.Create(restored);
        BindingQueryResult again = query.Admit("C2", "acct-07", "room-01", "player");
        Assert.Equal("ok", again.Outcome);
        Assert.Equal(id.ToHex(), again.Binding!.Value.NetEntityId);
        Assert.True(restored.World.TryGetAccount("acct-07", out NetEntityId indexed));
        Assert.Equal(id, indexed);
    }

    private static byte[] Encode(string text)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] bytes = new byte[4 + utf8.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, bytes, 4, utf8.Length);
        return bytes;
    }
}
