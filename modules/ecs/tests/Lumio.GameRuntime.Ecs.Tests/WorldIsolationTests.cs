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
        using var module = new EcsModule();
        EcsWorld world = CreateWorld(module, 1);

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
        using var firstModule = new EcsModule();
        using var secondModule = new EcsModule();
        EcsWorld first = NewWorld(firstModule, 10, out EntityTypeHandle firstType);
        EcsWorld second = NewWorld(secondModule, 11, out EntityTypeHandle secondType);

        EntityCreateResult firstResult = first.CreateEntityForCommit(first.Context, new EntityCreateRequest(firstType));
        EntityCreateResult secondResult = second.CreateEntityForCommit(second.Context, new EntityCreateRequest(secondType));

        Assert.True(firstResult.Created);
        Assert.True(secondResult.Created);
        Assert.Equal(firstResult.Entity, secondResult.Entity);
        Assert.Equal(
            StorageOperationStatus.Accepted,
            first.ValidateEntityContext(first, firstResult.Entity).Status);
        StorageOperationResult crossWorld = second.ValidateEntityContext(first, firstResult.Entity);
        Assert.Equal(StorageOperationStatus.Rejected, crossWorld.Status);
        Assert.Equal(EcsErrorCodes.CrossWorld, crossWorld.Error?.Code);
        Assert.Equal(
            StorageOperationStatus.Accepted,
            second.ValidateEntityContext(second, secondResult.Entity).Status);
    }

    [Fact]
    public void NonOwnerStructuralWriteFaultsWorldBeforeStorage()
    {
        using var module = new EcsModule();
        EcsWorld world = NewWorld(module, 20, out EntityTypeHandle entityType);
        var completed = new ManualResetEventSlim(false);
        EntityCreateResult result = default;
        var thread = new Thread(() =>
        {
            result = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));
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
        using var module = new EcsModule();
        EcsWorld world = NewWorld(module, 30, out EntityTypeHandle entityType);
        EntityCreateResult created = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));
        Assert.True(created.Created);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginDrain().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);

        Assert.False(world.TryResolve(world, created.Entity, out _));
        EntityDestroyResult destroyed = world.DestroyEntityForCommit(created.Target);
        Assert.False(destroyed.Destroyed);
        Assert.Equal(StorageOperationStatus.Rejected, destroyed.Result.Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, destroyed.Error?.Code);
        Assert.Equal(StorageOperationStatus.Rejected, world.Start().Status);
    }

    [Fact]
    public void NullEntityTypeRegistrationIsRejectedAsInvalidArgument()
    {
        using var module = new EcsModule();
        EcsWorld world = CreateWorld(module, 31);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);

        EntityTypeRegistrationResult result = world.RegisterEntityType(null!);

        Assert.Equal(StorageOperationStatus.Rejected, result.Result.Status);
        Assert.Equal(EcsErrorCodes.InvalidArgument, result.Error?.Code);
        Assert.Equal(EcsWorldState.Registering, world.State);
    }

    [Fact]
    public void NullComponentRegistrationIsRejectedAsInvalidArgument()
    {
        using var module = new EcsModule();
        EcsWorld world = CreateWorld(module, 32);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);

        ComponentTypeRegistrationResult result = EcsTestRegistration.Register(world, null);

        Assert.Equal(StorageOperationStatus.Rejected, result.Result.Status);
        Assert.Equal(EcsErrorCodes.InvalidArgument, result.Error?.Code);
        Assert.Equal(EcsWorldState.Registering, world.State);
    }

    [Fact]
    public void ContextValidationReportsFaultedAndDisposedStates()
    {
        using var module = new EcsModule();
        EcsWorld world = NewWorld(module, 33, out _);
        LocalEntityId entity = new(1, 1);

        Assert.Equal(StorageOperationStatus.Accepted, world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);
        StorageOperationResult faulted = world.ValidateEntityContext(world, entity);
        Assert.Equal(StorageOperationStatus.Rejected, faulted.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, faulted.Error?.Code);

        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);
        StorageOperationResult disposed = world.ValidateEntityContext(world, entity);
        Assert.Equal(StorageOperationStatus.Rejected, disposed.Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, disposed.Error?.Code);
    }

    private static EcsWorld NewWorld(
        EcsModule module,
        ulong id,
        out EntityTypeHandle entityType)
    {
        EcsWorld world = CreateWorld(module, id);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        EntityTypeRegistrationResult registration = world.RegisterEntityType(new EntityTypeDefinition("Player"));
        Assert.True(registration.Registered);
        entityType = registration.Handle;
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        return world;
    }

    private static EcsWorld CreateWorld(EcsModule module, ulong id)
    {
        var request = new EcsWorldCreateRequest(
            new WorldId(id),
            new EcsBudget(4, 32, 32, 4096));
        return Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
    }
}
