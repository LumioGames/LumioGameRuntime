using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class OwnerAndLifecycleTests
{
    [Fact]
    public void BootstrapOnOneThreadCanStartOnTheSimulationThread()
    {
        using var module = new EcsModule();
        EcsWorld world = CreateWorld(module, 300);
        StorageOperationResult begin = default;
        EntityTypeRegistrationResult registration = default;
        StorageOperationResult ready = default;
        var bootstrap = new Thread(() =>
        {
            begin = world.BeginRegistration();
            registration = world.RegisterEntityType(new EntityTypeDefinition("Player"));
            ready = world.MarkReady();
        });

        bootstrap.Start();
        bootstrap.Join();

        Assert.Equal(StorageOperationStatus.Accepted, begin.Status);
        Assert.True(registration.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, ready.Status);
        Assert.Equal(0, world.OwnerThreadId);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        Assert.Equal(Environment.CurrentManagedThreadId, world.OwnerThreadId);
        Assert.True(world.CreateEntityForCommit(world.Context, new EntityCreateRequest(registration.Handle)).Created);
    }

    [Fact]
    public void RunningWorldCannotDisposeWithoutDraining()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 301, out _);

        StorageOperationResult direct = world.DisposeWorld();

        Assert.Equal(StorageOperationStatus.Rejected, direct.Status);
        Assert.Equal(EcsWorldState.Running, world.State);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginDrain().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);
        Assert.Equal(EcsWorldState.Disposed, world.State);
    }

    [Fact]
    public void PostStartNonOwnerAccessUsesGeneratedStableErrorAndFaultsWorld()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 302, out EntityTypeHandle entityType);
        EntityCreateResult result = default;
        var nonOwner = new Thread(() =>
            result = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType)));

        nonOwner.Start();
        nonOwner.Join();

        Assert.False(result.Created);
        Assert.Equal(StorageOperationStatus.Fatal, result.Result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains(result.Error.Value.Code, Catalog.StableErrorIds);
        Assert.Equal(EcsErrorCodes.OwnerThreadViolation, result.Error.Value.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
    }

    [Fact]
    public void ModuleOwnsNonPublicForceCleanup()
    {
        var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 303, out _);
        MethodInfo forceCleanup = Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            static method => method.Name == "ForceCleanup");

        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(EcsWorld)));
        Assert.DoesNotContain(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method == forceCleanup);

        module.Dispose();

        Assert.Equal(EcsWorldState.Disposed, world.State);
    }

    [Fact]
    public void NonOwnerLiveReadsAreRejectedWhileSnapshotReadsRemainAvailable()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 304, out EntityTypeHandle entityType);
        EntityCreateResult created = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));
        Assert.True(created.Created);
        Assert.Equal(StorageOperationStatus.Accepted, world.CaptureReadSnapshot(
            new SnapshotId(1),
            new Revision(1),
            out StorageReadSnapshotHandle snapshot).Status);
        StorageOperationResult validation = default;
        StorageOperationResult enumeration = default;
        StorageOperationResult snapshotEnumeration = default;
        bool resolved = true;
        EntityLifecycleState resolvedState = EntityLifecycleState.Alive;
        int liveWritten = -1;
        int snapshotWritten = -1;
        var liveEntities = new LocalEntityId[1];
        var snapshotEntities = new LocalEntityId[1];
        var reader = new Thread(() =>
        {
            validation = world.ValidateEntityContext(world, created.Entity);
            resolved = world.TryResolve(world, created.Entity, out resolvedState);
            enumeration = world.EnumerateActiveEntities(world, liveEntities, out liveWritten);
            snapshotEnumeration = world.EnumerateSnapshotEntities(snapshot, snapshotEntities, out snapshotWritten);
        });

        reader.Start();
        reader.Join();

        Assert.Equal(StorageOperationStatus.Rejected, validation.Status);
        Assert.Equal(EcsErrorCodes.OwnerThreadViolation, validation.Error?.Code);
        Assert.False(resolved);
        Assert.Equal(EntityLifecycleState.Destroyed, resolvedState);
        Assert.Equal(StorageOperationStatus.Rejected, enumeration.Status);
        Assert.Equal(EcsErrorCodes.OwnerThreadViolation, enumeration.Error?.Code);
        Assert.Equal(0, liveWritten);
        Assert.Equal(default, liveEntities[0]);
        Assert.Equal(StorageOperationStatus.Accepted, snapshotEnumeration.Status);
        Assert.Equal(1, snapshotWritten);
        Assert.Equal(created.Entity, snapshotEntities[0]);
        Assert.Equal(EcsWorldState.Running, world.State);
    }

    [Fact]
    public void FaultedWorldRejectsLaterMutationBeforeCheckingOwner()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 305, out EntityTypeHandle entityType);
        Assert.Equal(StorageOperationStatus.Accepted,
            world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);
        EntityCreateResult later = default;
        var nonOwner = new Thread(() =>
            later = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType)));

        nonOwner.Start();
        nonOwner.Join();

        Assert.False(later.Created);
        Assert.Equal(StorageOperationStatus.Rejected, later.Result.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, later.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
    }

    private static EcsWorld NewRunningWorld(
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
        EcsWorldCreateResult result = module.CreateWorld(in request);
        return Assert.IsType<EcsWorld>(result.World);
    }
}
