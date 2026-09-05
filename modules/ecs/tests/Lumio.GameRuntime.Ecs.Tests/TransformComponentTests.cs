using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class TransformComponentTests
{
    [Fact]
    public void GeneratedEntityContainsLogicAndClientModelComponents()
    {
        var registry = new TransformRegistry();
        using WorldManager manager = WorldManager.Create(registry, 21UL);
        manager.Start(Thread.CurrentThread);

        EntityOrder order = manager.World.Commands.Create<TransformEntity>();
        manager.Tick();

        Assert.IsType<LogicTransform>(manager.World.Get<LogicTransform>(order.AssignedId));
        Assert.IsType<ModelTransform>(manager.World.Get<ModelTransform>(order.AssignedId));
    }

    [Fact]
    public void LogicUsesYUpAndHamiltonParentRotation()
    {
        var registry = new TransformRegistry();
        using WorldManager manager = WorldManager.Create(registry, 22UL);
        manager.Start(Thread.CurrentThread);
        EntityOrder parentOrder = manager.World.Commands.Create<TransformEntity>();
        EntityOrder childOrder = manager.World.Commands.Create<TransformEntity>();
        manager.Tick();

        LogicTransform parent = parentOrder.Get<LogicTransform>();
        LogicTransform child = childOrder.Get<LogicTransform>();
        TransformController controller = manager.World.RegisterTransformController(parent.Entity, "movement");
        using (parent.BeginWrite(controller))
        {
            parent.SetWorldPosition(new Vector3(2, 0, 3));
            parent.SetWorldEulerDegrees(new Vector3(0, 90, 0));
        }

        TransformController childController = manager.World.RegisterTransformController(child.Entity, "movement");
        using (child.BeginWrite(childController)) child.SetParent(parent, ParentPoseMode.KeepWorld);
        Assert.Equal(new Vector3(2, 0, 3), parent.WorldPosition);
        Assert.Equal(Vector3.UnitX, Vector3.Round(parent.Forward));
        using (parent.BeginWrite(controller)) parent.LookAt(parent.WorldPosition + Vector3.UnitZ);
        Assert.Equal(Vector3.UnitZ, Vector3.Round(parent.Forward));
    }

    [Fact]
    public void ParentChangeKeepsWorldAndDestroyUnparentsChild()
    {
        var registry = new TransformRegistry();
        using WorldManager manager = WorldManager.Create(registry, 23UL);
        manager.Start(Thread.CurrentThread);
        EntityOrder parentOrder = manager.World.Commands.Create<TransformEntity>();
        EntityOrder childOrder = manager.World.Commands.Create<TransformEntity>();
        manager.Tick();

        LogicTransform parent = parentOrder.Get<LogicTransform>();
        LogicTransform child = childOrder.Get<LogicTransform>();
        TransformController parentController = manager.World.RegisterTransformController(parent.Entity, "movement");
        TransformController childController = manager.World.RegisterTransformController(child.Entity, "movement");
        using (parent.BeginWrite(parentController)) parent.SetWorldPosition(new Vector3(5, 0, 0));
        using (child.BeginWrite(childController)) child.SetWorldPosition(new Vector3(6, 0, 0));
        using (child.BeginWrite(childController)) child.SetParent(parent, ParentPoseMode.KeepWorld);
        Assert.Equal(new Vector3(6, 0, 0), child.WorldPosition);
        Assert.Single(parent.Children);

        using (parent.BeginWrite(parentController)) parent.TeleportWorld(new Vector3(20, 0, 0), new TransformTeleportId(9));
        Assert.Equal(new TransformTeleportId(9), child.LastTeleport);

        manager.World.QueueDestroy(parent.Entity);
        manager.Tick();
        Assert.Null(child.Parent);
        Assert.Equal(new Vector3(21, 0, 0), child.WorldPosition);
    }

    [Fact]
    public void LogicRejectsUnauthorizedWritesBeforeMutation()
    {
        var registry = new TransformRegistry();
        using WorldManager manager = WorldManager.Create(registry, 24UL);
        manager.Start(Thread.CurrentThread);
        EntityOrder order = manager.World.Commands.Create<TransformEntity>();
        manager.Tick();
        LogicTransform logic = order.Get<LogicTransform>();
        TransformController owner = manager.World.RegisterTransformController(logic.Entity, "movement");
        using (logic.BeginWrite(owner)) logic.SetWorldPosition(new Vector3(1, 0, 0));
        TransformController other = TransformController.Unbound("other");

        Assert.Throws<TransformWriteException>(() =>
        {
            using (logic.BeginWrite(other)) logic.SetWorldPosition(new Vector3(9, 0, 0));
        });
        Assert.Equal(new Vector3(1, 0, 0), logic.WorldPosition);
    }

    [Fact]
    public void ModelFirstSamplePositionsAndTeleportDoesNotReplayOldHistory()
    {
        var registry = new TransformRegistry { SideOverride = RegistrySide.Client };
        using WorldManager manager = WorldManager.Create(registry);
        manager.Start(Thread.CurrentThread);
        EntityOrder order = manager.World.Commands.Create<TransformEntity>();
        manager.Tick();
        ModelTransform model = order.Get<ModelTransform>();
        NetEntityId id = order.AssignedId;

        model.PushSample(new TransformSample(id, 1, 0, new Pose(new Vector3(10, 0, 0), Quaternion.Identity), TransformSampleReference.World));
        model.UpdatePresentation(0);
        Assert.Equal(new Vector3(10, 0, 0), model.WorldPosition);

        var teleport = new TransformTeleportId(7);
        model.PushSample(new TransformSample(id, 2, 1, new Pose(new Vector3(100, 0, 0), Quaternion.Identity), TransformSampleReference.World, teleport));
        model.UpdatePresentation(1);
        Assert.Equal(new Vector3(100, 0, 0), model.WorldPosition);
        model.PushSample(new TransformSample(id, 1, 0.5, new Pose(new Vector3(1, 0, 0), Quaternion.Identity), TransformSampleReference.World));
        model.UpdatePresentation(2);
        Assert.Equal(new Vector3(100, 0, 0), model.WorldPosition);
    }

    [Fact]
    public void InvalidMathInputsAreRejectedWithoutPartialWrite()
    {
        var registry = new TransformRegistry();
        using WorldManager manager = WorldManager.Create(registry, 25UL);
        manager.Start(Thread.CurrentThread);
        EntityOrder order = manager.World.Commands.Create<TransformEntity>();
        manager.Tick();
        LogicTransform logic = order.Get<LogicTransform>();
        TransformController controller = manager.World.RegisterTransformController(logic.Entity, "movement");

        using (logic.BeginWrite(controller))
        {
            Assert.Throws<ArgumentException>(() => logic.SetLocalPose(new Pose(new Vector3(float.NaN, 0, 0), Quaternion.Identity)));
            Assert.Throws<ArgumentException>(() => logic.SetLocalRotation(new Quaternion(0, 0, 0, 0)));
            Assert.Equal(Vector3.Zero, logic.LocalPosition);
        }
    }

    [Fact]
    public void LogicPersistsWhileModelPresentationStateIsExcluded()
    {
        var registry = new TransformRegistry();
        using WorldManager manager = WorldManager.Create(registry, 26UL);
        manager.Start(Thread.CurrentThread);
        EntityOrder order = manager.World.Commands.Create<TransformEntity>();
        manager.Tick();
        LogicTransform logic = order.Get<LogicTransform>();
        TransformController controller = manager.World.RegisterTransformController(logic.Entity, "movement");
        using (logic.BeginWrite(controller)) logic.SetWorldPose(new Pose(new Vector3(4, 0, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f)));
        order.Get<ModelTransform>().PushSample(new TransformSample(order.AssignedId, 1, 0, new Pose(new Vector3(90, 0, 0), Quaternion.Identity), TransformSampleReference.World));

        byte[] snapshot = manager.CaptureSnapshot();
        EcsRegistry.Current = registry;
        using WorldManager restored = WorldManager.CreateFromSnapshot(snapshot);
        Assert.Equal(new Vector3(4, 0, 5), restored.World.Get<LogicTransform>(order.AssignedId).WorldPosition);
        Assert.Equal(Vector3.Zero, restored.World.Get<ModelTransform>(order.AssignedId).WorldPosition);
    }

    [Fact]
    public void ModelInterpolatesCompleteSamplesWithoutWritingLogic()
    {
        var registry = new TransformRegistry { SideOverride = RegistrySide.Client };
        using WorldManager manager = WorldManager.Create(registry);
        manager.Start(Thread.CurrentThread);
        EntityOrder order = manager.World.Commands.Create<TransformEntity>();
        manager.Tick();
        ModelTransform model = order.Get<ModelTransform>();
        model.PushSample(new TransformSample(order.AssignedId, 1, 0, new Pose(Vector3.Zero, Quaternion.Identity), TransformSampleReference.World));
        model.PushSample(new TransformSample(order.AssignedId, 2, 1, new Pose(new Vector3(10, 0, 0), Quaternion.Identity), TransformSampleReference.World));
        model.UpdatePresentation(0.5);
        Assert.Equal(new Vector3(5, 0, 0), model.WorldPosition);
        Assert.Equal(Vector3.Zero, model.Parent is null ? Vector3.Zero : model.Parent.WorldPosition);
    }

    [Fact]
    public void ClientWorldCreatesOneModelOnTheOriginalEntity()
    {
        var registry = new TransformRegistry { SideOverride = RegistrySide.Client };
        using WorldManager manager = WorldManager.Create(registry);
        manager.Start(Thread.CurrentThread);
        NetEntityId id = new(27, 1);
        manager.Enqueue(new WelcomeMessage(27, id));
        manager.Enqueue(new WorldChangeMessage(1, new[] { new CreateRecord("transform", id, Array.Empty<FieldValue>()) }, Array.Empty<FieldChange>(), Array.Empty<NetEntityId>(), Array.Empty<ClientRpcRecord>()));
        manager.Tick();

        Assert.Single(manager.World.Each<ModelTransform>());
        Assert.Same(manager.World.Get<LogicTransform>(id).World, manager.World.Get<ModelTransform>(id).World);
    }

    [EntityType(Mode.CS)]
    [Has(typeof(LogicTransform))]
    [Has(typeof(ModelTransform))]
    private abstract class TransformEntity
    {
    }

    private sealed class TransformRegistry : EcsRegistry
    {
        public RegistrySide SideOverride { get; init; } = RegistrySide.Server;
        public override RegistrySide Side => SideOverride;
        public override Type WorldEntityType => typeof(TransformEntity);
        public override IReadOnlyList<Lumio.GameRuntime.Ecs.Annotations.FieldAttributeDeclaration> AttributeDeclarations => Array.Empty<Lumio.GameRuntime.Ecs.Annotations.FieldAttributeDeclaration>();
        public override Component[] CreateComponents(Type entityType) => new Component[] { new LogicTransform(), new ModelTransform() };
        public override string WireName(Type entityType) => "transform";
        public override bool TryResolveEntityType(string name, out Type entityType)
        {
            entityType = typeof(TransformEntity);
            return string.Equals(name, nameof(TransformEntity), StringComparison.Ordinal) || string.Equals(name, "transform", StringComparison.Ordinal);
        }
        public override bool IsEntityType(Type concrete, Type query) => concrete == query;
    }
}
