using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class R5RuntimeContractTests
{
    private static readonly string[] ObserverFields = { "Connected", "ConnectionGeneration", "DisconnectedAtTick", "ProjectedTick" };
    [Fact]
    public void ObserverComponentExposesOnlyRuntimeConnectionProjectionState()
    {
        Type type = typeof(ObserverComponent);
        string[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Select(static field => field.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ObserverFields,
            fields);
    }

    [Fact]
    public void SyncFieldsCarryClaimByWithoutHeapSlots()
    {
        Sync<string> field = new(Scope.Claim, Authority.Server, Notify.Remote, nameof(ObserverComponent.ProjectedTick));
        Assert.Equal(nameof(ObserverComponent.ProjectedTick), field.ClaimBy);
        Assert.DoesNotContain(typeof(Sync<string>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            static fieldInfo => fieldInfo.FieldType.Name.StartsWith("SyncSlot", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagerBindsObserverByNetEntityIdAndDoesNotAdvanceTick()
    {
        var registry = new TestRegistry();
        EcsRegistry.Current = registry;
        using WorldManager manager = WorldManager.Create(registry, 7UL);
        manager.Start(Thread.CurrentThread);
        EntityOrder order = manager.World.Commands.Create<SamplePlayerEntity>();
        manager.Tick();
        NetEntityId observer = order.AssignedId;
        Assert.NotEqual(default, observer);
        ulong before = manager.World.Tick;
        manager.Bind(observer);
        Assert.Equal(before, manager.World.Tick);
        Assert.True(manager.World.Get<ObserverComponent>(observer).Connected);
    }

    [Fact]
    public void SyncListMutationNotifiesItsBoundHost()
    {
        var host = new RecordingHost();
        var list = new SyncList<NetEntityId>(Scope.Owner, Authority.Owner);
        var owner = new ObserverComponent();
        list = list.Bound(host, owner, "IdentityComponent.friends");

        list.Add(new NetEntityId(7, 1));

        Assert.Equal(1, host.ContainerWrites);
        Assert.Equal("IdentityComponent.friends", host.LastContainer?.AttributeId);
    }

    [Fact]
    public void ClientRejectsWorldChangeBeforeWelcomeInSameBatch()
    {
        var registry = new TestRegistry { SideOverride = RegistrySide.Client };
        EcsRegistry.Current = registry;
        using WorldManager manager = WorldManager.Create(registry);
        manager.Start(Thread.CurrentThread);
        var id = new NetEntityId(9, 1);
        manager.Enqueue(new WorldChangeMessage(1, Array.Empty<CreateRecord>(), Array.Empty<FieldChange>(), Array.Empty<NetEntityId>(), Array.Empty<ClientRpcRecord>()));
        manager.Enqueue(new WelcomeMessage(9, id));

        Assert.Throws<InvalidOperationException>(() => manager.Tick());
    }

    [Fact]
    public void EntityIdIssuanceRejectsCounterExhaustion()
    {
        var registry = new TestRegistry();
        EcsRegistry.Current = registry;
        using WorldManager manager = WorldManager.Create(registry, 7UL);
        manager.Start(Thread.CurrentThread);
        typeof(World).GetField("NextCounter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager.World, ulong.MaxValue);
        manager.World.Commands.Create<SamplePlayerEntity>();

        Assert.Throws<InvalidOperationException>(() => manager.Tick());
    }

    [Fact]
    public void RuntimeControlsAreWorldMessagesAndNotWireMessages()
    {
        WorldMessage[] controls =
        {
            new AdmitConnectionMessage("conn-a", "acct-a", "room-a", "player"),
            new DisconnectConnectionMessage("conn-a"),
            new RebindConnectionMessage("conn-a", "acct-a", "room-a", "reconnect"),
        };

        Assert.All(controls, control =>
        {
            Assert.IsAssignableFrom<WorldMessage>(control);
            Assert.Throws<ArgumentException>(() => WireCodec.EncodePack(control));
        });
    }

    [Fact]
    public void ServerA2ControlsWithoutAdapterReturnExplicitQueries()
    {
        var registry = new TestRegistry();
        using WorldManager manager = WorldManager.Create(registry, 7UL);
        manager.Start(Thread.CurrentThread);
        manager.Enqueue(new ExpireEntityMessage("expire", "malformed"));
        manager.Enqueue(new ResolveBindingMessage("resolve", "room-a", "malformed"));
        manager.Enqueue(new AttributeQueryMessage("attribute", "server-authoritative", "room-a", "malformed", "IdentityComponent.name"));

        manager.Tick();

        WorldDrainResponse response = manager.DrainOutbox();
        Assert.Empty(response.Frames);
        Assert.Equal(3, response.Queries.Count);
        Assert.Collection(
            response.Queries,
            query =>
            {
                WorldControlRequestErrorResult error = Assert.IsType<WorldControlRequestErrorResult>(query);
                Assert.Equal("expire", error.RequestId);
                Assert.Equal("request_error", error.Outcome);
            },
            query =>
            {
                WorldControlRequestErrorResult error = Assert.IsType<WorldControlRequestErrorResult>(query);
                Assert.Equal("resolve", error.RequestId);
                Assert.Equal("request_error", error.Outcome);
            },
            query =>
            {
                WorldControlRequestErrorResult error = Assert.IsType<WorldControlRequestErrorResult>(query);
                Assert.Equal("attribute", error.RequestId);
                Assert.Equal("request_error", error.Outcome);
            });
    }

    [Fact]
    public async Task RuntimeControlRunsOnOwnerTickAndPreservesReturnedErrorConnection()
    {
        var registry = new TestRegistry();
        using WorldManager manager = WorldManager.Create(registry, 7UL);
        var adapter = new RecordingControlAdapter();
        manager.AttachControlAdapter(adapter);

        await Task.Run(() => manager.Enqueue(new AdmitConnectionMessage("conn-a", "acct-a", "room-a", "player")), TestContext.Current.CancellationToken);
        Assert.Empty(adapter.Handled);
        manager.Start(Thread.CurrentThread);
        manager.Tick();

        Assert.Single(adapter.Handled);
        Assert.Equal("conn-a", adapter.Handled[0].Connection);
        IReadOnlyList<WorldMessage> output = manager.DrainOutbox();
        ErrorMessage error = Assert.IsType<ErrorMessage>(Assert.Single(output, item => item is ErrorMessage));
        Assert.Equal("conn-a", error.Connection);
    }

    [Fact]
    public void RuntimeControlAdapterCanResolveProjectionConnection()
    {
        var registry = new TestRegistry();
        using WorldManager manager = WorldManager.Create(registry, 7UL);
        manager.Start(Thread.CurrentThread);
        var adapter = new RecordingControlAdapter();
        manager.AttachControlAdapter(adapter);
        Assert.Same(adapter, manager.DetachControlAdapter());
        Assert.Null(manager.DetachControlAdapter());
    }

    private abstract class SamplePlayerEntity { }

    private sealed class TestRegistry : EcsRegistry
    {
        public RegistrySide SideOverride { get; init; } = RegistrySide.Server;
        public override RegistrySide Side => SideOverride;
        public override Type WorldEntityType => typeof(SamplePlayerEntity);
        public override System.Collections.Generic.IReadOnlyList<Lumio.GameRuntime.Ecs.Annotations.FieldAttributeDeclaration> AttributeDeclarations => Array.Empty<Lumio.GameRuntime.Ecs.Annotations.FieldAttributeDeclaration>();
        public override Component[] CreateComponents(Type entityType) => new Component[] { new ObserverComponent() };
        public override string WireName(Type entityType) => "player";
        public override bool TryResolveEntityType(string name, out Type entityType)
        {
            entityType = typeof(SamplePlayerEntity);
            return string.Equals(name, nameof(SamplePlayerEntity), StringComparison.Ordinal);
        }
        public override bool IsEntityType(Type concrete, Type query) => concrete == query;
    }

    private sealed class RecordingHost : ISyncHost
    {
        public bool IsServer => false;
        public bool IsApplyingRemote => false;
        public WorldManager Manager => throw new NotSupportedException();
        public World World => throw new NotSupportedException();
        public int ContainerWrites { get; private set; }
        public ISyncContainer? LastContainer { get; private set; }
        public void OnLocalWrite(Component owner, ISyncField field, object? oldValue, object? newValue) { }
        public void OnContainerWrite(Component owner, ISyncContainer container, object? oldValue, object? newValue)
        {
            ContainerWrites++;
            LastContainer = container;
        }
    }

    private sealed class RecordingControlAdapter : IWorldControlAdapter
    {
        public List<WorldMessage> Handled { get; } = new();

        public bool TryHandle(WorldMessage message, out ErrorMessage? error)
        {
            Handled.Add(message);
            error = new ErrorMessage("runtime_failure", "rejected") { Connection = message.Connection };
            return false;
        }

        public bool TryResolveConnection(NetEntityId observerId, out string connection)
        {
            connection = "resolved-" + observerId.ToHex();
            return true;
        }
    }
}
