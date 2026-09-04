using System;
using System.Linq;
using System.Reflection;
using System.Threading;
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

    private abstract class SamplePlayerEntity { }

    private sealed class TestRegistry : EcsRegistry
    {
        public override RegistrySide Side => RegistrySide.Server;
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
}
