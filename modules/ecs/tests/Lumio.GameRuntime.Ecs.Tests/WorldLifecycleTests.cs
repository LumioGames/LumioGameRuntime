using System;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class WorldLifecycleTests
{
    [Fact]
    public void WorldAcceptsOnlyTheCanonicalLifecycle()
    {
        using var module = new EcsModule();
        EcsWorld world = CreateWorld(module, 1520);

        Assert.Equal(EcsWorldState.Created, world.State);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        Assert.Equal(EcsWorldState.Registering, world.State);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(EcsWorldState.Ready, world.State);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        Assert.Equal(EcsWorldState.Running, world.State);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginDrain().Status);
        Assert.Equal(EcsWorldState.Draining, world.State);
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);
        Assert.Equal(EcsWorldState.Disposed, world.State);
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);
        Assert.Equal(EcsWorldState.Disposed, world.State);
    }

    [Theory]
    [InlineData(EcsWorldState.Created)]
    [InlineData(EcsWorldState.Registering)]
    [InlineData(EcsWorldState.Ready)]
    [InlineData(EcsWorldState.Running)]
    [InlineData(EcsWorldState.Draining)]
    public void ActiveStateCanEnterFaulted(EcsWorldState active)
    {
        using var module = new EcsModule();
        EcsWorld world = WorldAt(module, 1521 + (ulong)active, active);

        StorageOperationResult fault = world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState));

        Assert.Equal(StorageOperationStatus.Accepted, fault.Status);
        Assert.Equal(EcsWorldState.Faulted, world.State);
        Assert.True(world.FirstFault.HasValue);
        Assert.Equal(EcsErrorCodes.InvalidState, world.FirstFault.Value.Error.Code);
    }

    [Theory]
    [InlineData(EcsWorldState.Created, LifecycleOp.MarkReady, "InternalInvariant")]
    [InlineData(EcsWorldState.Created, LifecycleOp.Start, "InternalInvariant")]
    [InlineData(EcsWorldState.Created, LifecycleOp.BeginDrain, "InternalInvariant")]
    [InlineData(EcsWorldState.Created, LifecycleOp.DisposeWorld, "InternalInvariant")]
    [InlineData(EcsWorldState.Registering, LifecycleOp.BeginRegistration, "InternalInvariant")]
    [InlineData(EcsWorldState.Registering, LifecycleOp.Start, "InternalInvariant")]
    [InlineData(EcsWorldState.Registering, LifecycleOp.BeginDrain, "InternalInvariant")]
    [InlineData(EcsWorldState.Registering, LifecycleOp.DisposeWorld, "InternalInvariant")]
    [InlineData(EcsWorldState.Ready, LifecycleOp.BeginRegistration, "InternalInvariant")]
    [InlineData(EcsWorldState.Ready, LifecycleOp.MarkReady, "InternalInvariant")]
    [InlineData(EcsWorldState.Ready, LifecycleOp.BeginDrain, "InternalInvariant")]
    [InlineData(EcsWorldState.Ready, LifecycleOp.DisposeWorld, "InternalInvariant")]
    [InlineData(EcsWorldState.Running, LifecycleOp.BeginRegistration, "InternalInvariant")]
    [InlineData(EcsWorldState.Running, LifecycleOp.MarkReady, "InternalInvariant")]
    [InlineData(EcsWorldState.Running, LifecycleOp.Start, "InternalInvariant")]
    [InlineData(EcsWorldState.Running, LifecycleOp.DisposeWorld, "InternalInvariant")]
    [InlineData(EcsWorldState.Draining, LifecycleOp.BeginRegistration, "ContextClosing")]
    [InlineData(EcsWorldState.Draining, LifecycleOp.MarkReady, "ContextClosing")]
    [InlineData(EcsWorldState.Draining, LifecycleOp.Start, "ContextClosing")]
    [InlineData(EcsWorldState.Draining, LifecycleOp.BeginDrain, "ContextClosing")]
    [InlineData(EcsWorldState.Faulted, LifecycleOp.BeginRegistration, "InternalInvariant")]
    [InlineData(EcsWorldState.Faulted, LifecycleOp.MarkReady, "InternalInvariant")]
    [InlineData(EcsWorldState.Faulted, LifecycleOp.Start, "InternalInvariant")]
    [InlineData(EcsWorldState.Faulted, LifecycleOp.BeginDrain, "InternalInvariant")]
    [InlineData(EcsWorldState.Faulted, LifecycleOp.Create, "InternalInvariant")]
    [InlineData(EcsWorldState.Disposed, LifecycleOp.BeginRegistration, "ContextDestroyed")]
    [InlineData(EcsWorldState.Disposed, LifecycleOp.MarkReady, "ContextDestroyed")]
    [InlineData(EcsWorldState.Disposed, LifecycleOp.Start, "ContextDestroyed")]
    [InlineData(EcsWorldState.Disposed, LifecycleOp.BeginDrain, "ContextDestroyed")]
    [InlineData(EcsWorldState.Disposed, LifecycleOp.Fault, "ContextDestroyed")]
    [InlineData(EcsWorldState.Disposed, LifecycleOp.Create, "ContextDestroyed")]
    public void IllegalTransitionIsRejected(EcsWorldState state, LifecycleOp operation, string code)
    {
        using var module = new EcsModule();
        EcsWorld world = WorldAt(module, 1540 + (ulong)state * 10 + (ulong)operation, state);

        StorageOperationResult result = Invoke(world, operation);

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(state, world.State);
    }

    [Fact]
    public void FaultedWorldAllowsEvidenceCaptureAndDisposeOnly()
    {
        using var module = new EcsModule();
        EcsWorld world = WorldAt(module, 1600, EcsWorldState.Running);
        Assert.Equal(StorageOperationStatus.Accepted,
            world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);
        Assert.True(world.FirstFault.HasValue);
        EcsFaultEvidence first = world.FirstFault.Value;

        StorageOperationResult laterFault = world.Fault(new ErrorIdentity(EcsErrorCodes.PostWriteFailure));
        StorageOperationResult create = world.CreateEntityForCommit(
            world.Context,
            new EntityCreateRequest(default)).Result;
        StorageOperationResult start = world.Start();
        StorageOperationResult drain = world.BeginDrain();

        Assert.Equal(StorageOperationStatus.Accepted, laterFault.Status);
        Assert.Equal(first, world.FirstFault);
        Assert.Equal(StorageOperationStatus.Rejected, create.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, create.Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected, start.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, start.Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected, drain.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, drain.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);
        Assert.Equal(EcsWorldState.Disposed, world.State);
        Assert.Equal(first, world.FirstFault);
    }

    [Fact]
    public void DisposedWorldRejectsAllOperations()
    {
        using var module = new EcsModule();
        EcsWorld world = WorldAt(module, 1601, EcsWorldState.Disposed);
        var destination = new LocalEntityId[1];

        Assert.Equal(StorageOperationStatus.Rejected, world.BeginRegistration().Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, world.BeginRegistration().Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected, world.MarkReady().Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, world.MarkReady().Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected, world.Start().Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, world.Start().Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected, world.BeginDrain().Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, world.BeginDrain().Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected,
            world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed,
            world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected,
            world.CreateEntityForCommit(world.Context, new EntityCreateRequest(default)).Result.Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed,
            world.CreateEntityForCommit(world.Context, new EntityCreateRequest(default)).Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected,
            world.EnumerateActiveEntities(world, destination, out int written).Status);
        Assert.Equal(0, written);
        Assert.Equal(EcsErrorCodes.WorldDisposed,
            world.ValidateEntityContext(world, new LocalEntityId(1, 1)).Error?.Code);
        Assert.Equal(EcsWorldState.Disposed, world.State);
    }

    public enum LifecycleOp
    {
        BeginRegistration,
        MarkReady,
        Start,
        BeginDrain,
        DisposeWorld,
        Fault,
        Create
    }

    private static StorageOperationResult Invoke(EcsWorld world, LifecycleOp operation) => operation switch
    {
        LifecycleOp.BeginRegistration => world.BeginRegistration(),
        LifecycleOp.MarkReady => world.MarkReady(),
        LifecycleOp.Start => world.Start(),
        LifecycleOp.BeginDrain => world.BeginDrain(),
        LifecycleOp.DisposeWorld => world.DisposeWorld(),
        LifecycleOp.Fault => world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)),
        LifecycleOp.Create => world.CreateEntityForCommit(world.Context, new EntityCreateRequest(default)).Result,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static EcsWorld WorldAt(EcsModule module, ulong id, EcsWorldState target)
    {
        EcsWorld world = CreateWorld(module, id);
        if (target == EcsWorldState.Created) return world;
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        if (target == EcsWorldState.Registering) return world;
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        if (target == EcsWorldState.Ready) return world;
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        if (target == EcsWorldState.Running) return world;
        if (target == EcsWorldState.Faulted)
        {
            Assert.Equal(StorageOperationStatus.Accepted,
                world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);
            return world;
        }

        Assert.Equal(StorageOperationStatus.Accepted, world.BeginDrain().Status);
        if (target == EcsWorldState.Draining) return world;
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);
        Assert.Equal(EcsWorldState.Disposed, world.State);
        return world;
    }

    private static EcsWorld CreateWorld(EcsModule module, ulong id)
    {
        var request = new EcsWorldCreateRequest(new WorldId(id), new EcsBudget(4, 32, 32, 4096));
        return Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
    }
}
