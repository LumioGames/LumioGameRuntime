using System;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class WorldIsolationTests
{
    [Fact]
    public void WorldFollowsExactLifecycle()
    {
        using var world = new EcsWorld(new EcsWorldCreateRequest(
            new WorldId(1), new EcsBudget(4, 32, 32, 4096)));

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
    }

    [Fact]
    public void SameLocalIdInDifferentWorldsRequiresMatchingWorldContext()
    {
        using var first = NewWorld(10);
        using var second = NewWorld(11);
        EntityTypeDefinition type = new("Player");

        EntityCreateResult firstResult = first.CreateEntityForCommit(new EntityCreateRequest(type));
        EntityCreateResult secondResult = second.CreateEntityForCommit(new EntityCreateRequest(type));

        Assert.True(firstResult.Created);
        Assert.True(secondResult.Created);
        Assert.Equal(firstResult.Entity, secondResult.Entity);
        Assert.Equal(
            StorageOperationStatus.Accepted,
            first.ValidateEntityContext(first.WorldId, firstResult.Entity).Status);
        StorageOperationResult crossWorld = second.ValidateEntityContext(first.WorldId, firstResult.Entity);
        Assert.Equal(StorageOperationStatus.Rejected, crossWorld.Status);
        Assert.Equal(EcsErrorCodes.CrossWorld, crossWorld.Error?.Code);
        Assert.Equal(
            StorageOperationStatus.Accepted,
            second.ValidateEntityContext(second.WorldId, secondResult.Entity).Status);
    }

    [Fact]
    public void NonOwnerStructuralWriteFaultsWorldBeforeStorage()
    {
        using var world = NewWorld(20);
        var completed = new ManualResetEventSlim(false);
        EntityCreateResult result = default;
        var thread = new Thread(() =>
        {
            result = world.CreateEntityForCommit(new EntityCreateRequest(new EntityTypeDefinition("Player")));
            completed.Set();
        });

        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        thread.Join();

        Assert.False(result.Created);
        Assert.Equal(StorageOperationStatus.Fatal, result.Result.Status);
        Assert.Equal(EcsErrorCodes.OwnerThreadViolation, result.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
        Assert.Equal(0, world.ActiveEntityCount);
    }

    [Fact]
    public void DisposedWorldRejectsOldEntityAndFurtherLifecycleCalls()
    {
        var world = NewWorld(30);
        EntityCreateResult created = world.CreateEntityForCommit(new EntityCreateRequest(new EntityTypeDefinition("Player")));
        Assert.True(created.Created);
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);

        Assert.False(world.TryResolve(created.Entity, out _));
        EntityDestroyResult destroyed = world.DestroyEntityForCommit(created.Entity);
        Assert.False(destroyed.Destroyed);
        Assert.Equal(StorageOperationStatus.Rejected, destroyed.Result.Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, destroyed.Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected, world.Start().Status);
        world.Dispose();
    }

    [Fact]
    public void NullSchemaRegistrationIsRejectedAsInvalidArgument()
    {
        using var world = new EcsWorld(new EcsWorldCreateRequest(
            new WorldId(31), new EcsBudget(4, 32, 32, 4096)));
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);

        StorageOperationResult result = world.RegisterTypes(null!);

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(EcsErrorCodes.InvalidArgument, result.Error?.Code);
        Assert.Equal(EcsWorldState.Registering, world.State);
    }

    [Fact]
    public void NullComponentRegistrationIsRejectedAsInvalidArgument()
    {
        using var world = new EcsWorld(new EcsWorldCreateRequest(
            new WorldId(32), new EcsBudget(4, 32, 32, 4096)));
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);

        StorageOperationResult result = world.RegisterComponentType(null!);

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(EcsErrorCodes.InvalidArgument, result.Error?.Code);
        Assert.Equal(EcsWorldState.Registering, world.State);
    }

    [Fact]
    public void ContextValidationReportsFaultedAndDisposedStates()
    {
        using var world = NewWorld(33);
        LocalEntityId entity = new(1, 1);

        Assert.Equal(StorageOperationStatus.Accepted, world.Fault(new ErrorIdentity("InjectedFault")).Status);
        StorageOperationResult faulted = world.ValidateEntityContext(world.WorldId, entity);
        Assert.Equal(StorageOperationStatus.Rejected, faulted.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, faulted.Error?.Code);

        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);
        StorageOperationResult disposed = world.ValidateEntityContext(world.WorldId, entity);
        Assert.Equal(StorageOperationStatus.Rejected, disposed.Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, disposed.Error?.Code);
    }

    private static EcsWorld NewWorld(ulong id)
    {
        var world = new EcsWorld(new EcsWorldCreateRequest(
            new WorldId(id), new EcsBudget(4, 32, 32, 4096)));
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        return world;
    }
}
