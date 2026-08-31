using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class WorldBoundaryTests
{
    [Fact]
    public void WorldConstructionIsControlledByTheModule()
    {
        Assert.Empty(typeof(EcsWorld).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void ModuleRejectsDuplicateWorldIds()
    {
        using var module = new EcsModule();
        var request = new EcsWorldCreateRequest(new WorldId(100), new EcsBudget(4, 32, 32, 4096));

        EcsWorldCreateResult first = module.CreateWorld(in request);
        EcsWorldCreateResult duplicate = module.CreateWorld(in request);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Null(duplicate.World);
        Assert.Equal(EcsErrorCodes.DuplicateRegistration, duplicate.Error?.Code);
    }

    [Fact]
    public void ConcurrentFactoryCallsCannotBypassDuplicateWorldIdCheck()
    {
        const int workerCount = 8;
        for (int round = 0; round < 32; round++)
        {
            using var module = new EcsModule();
            var request = new EcsWorldCreateRequest(
                new WorldId(1000UL + checked((ulong)round)),
                new EcsBudget(4, 32, 32, 4096));
            var results = new EcsWorldCreateResult[workerCount];
            var failures = new Exception?[workerCount];
            using var start = new Barrier(workerCount);
            var workers = new Thread[workerCount];
            for (int index = 0; index < workers.Length; index++)
            {
                int workerIndex = index;
                workers[index] = new Thread(() =>
                {
                    try
                    {
                        if (!start.SignalAndWait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("Concurrent factory workers did not reach the barrier.");
                        results[workerIndex] = module.CreateWorld(in request);
                    }
                    catch (Exception exception)
                    {
                        failures[workerIndex] = exception;
                    }
                });
                workers[index].Start();
            }
            foreach (Thread worker in workers) worker.Join();

            Assert.DoesNotContain(failures, static failure => failure is not null);
            Assert.Equal(1, results.Count(static result => result.Created));
            Assert.Equal(workerCount - 1, results.Count(static result =>
                !result.Created && result.Error?.Code == EcsErrorCodes.DuplicateRegistration));
        }
    }

    [Fact]
    public void PublicResolveAlwaysRequiresWorldContext()
    {
        MethodInfo[] publicResolveMethods = typeof(EcsWorld).GetMethods(
            BindingFlags.Instance | BindingFlags.Public).Where(
                static method => method.Name.Contains("Resolve", StringComparison.Ordinal)).ToArray();

        Assert.DoesNotContain(publicResolveMethods, static method =>
        {
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length > 0 && parameters[0].ParameterType == typeof(LocalEntityId);
        });
    }

    [Fact]
    public void CrossWorldResolveRejectsBeforePublishingState()
    {
        using var firstModule = new EcsModule();
        using var secondModule = new EcsModule();
        EcsWorld first = NewRunningWorld(firstModule, 101, out EntityTypeHandle firstType);
        EcsWorld second = NewRunningWorld(secondModule, 102, out _);
        EntityCreateResult created = first.CreateEntityForCommit(first.Context,
            new EntityCreateRequest(firstType));
        Assert.True(created.Created);

        bool resolved = second.TryResolve(first, created.Entity, out EntityLifecycleState state);
        StorageOperationResult validation = second.ValidateEntityContext(first, created.Entity);

        Assert.False(resolved);
        Assert.Equal(EntityLifecycleState.Destroyed, state);
        Assert.Equal(StorageOperationStatus.Rejected, validation.Status);
        Assert.Equal(EcsErrorCodes.CrossWorld, validation.Error?.Code);
    }

    [Fact]
    public void FaultedWorldRejectsEnumerationAndResolve()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 103, out EntityTypeHandle entityType);
        EntityCreateResult created = world.CreateEntityForCommit(world.Context,
            new EntityCreateRequest(entityType));
        Assert.True(created.Created);
        Assert.Equal(StorageOperationStatus.Accepted,
            world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);
        var destination = new LocalEntityId[1];

        StorageOperationResult enumeration = Enumerate(world, world, destination, out int written);
        StorageOperationResult resolution = world.ValidateEntityContext(world, created.Entity);

        Assert.Equal(StorageOperationStatus.Rejected, enumeration.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, enumeration.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(default, destination[0]);
        Assert.Equal(StorageOperationStatus.Rejected, resolution.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, resolution.Error?.Code);
    }

    [Fact]
    public void DisposedWorldRejectsEnumerationAndResolve()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 104, out EntityTypeHandle entityType);
        EntityCreateResult created = world.CreateEntityForCommit(world.Context,
            new EntityCreateRequest(entityType));
        Assert.True(created.Created);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginDrain().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);
        var destination = new LocalEntityId[1];

        StorageOperationResult enumeration = Enumerate(world, world, destination, out int written);
        StorageOperationResult resolution = world.ValidateEntityContext(world, created.Entity);

        Assert.Equal(StorageOperationStatus.Rejected, enumeration.Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, enumeration.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(default, destination[0]);
        Assert.Equal(StorageOperationStatus.Rejected, resolution.Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, resolution.Error?.Code);
    }

    private static StorageOperationResult Enumerate(
        EcsWorld world,
        EcsWorld contextWorld,
        LocalEntityId[] destination,
        out int written)
    {
        MethodInfo method = Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            static candidate => candidate.Name == "EnumerateActiveEntities" && candidate.GetParameters().Length == 3);
        object?[] arguments = { contextWorld, destination, 0 };
        object? returnValue = method.Invoke(world, arguments);
        written = Assert.IsType<int>(arguments[2]);
        return Assert.IsType<StorageOperationResult>(returnValue);
    }

    private static EcsWorld NewRunningWorld(
        EcsModule module,
        ulong id,
        out EntityTypeHandle entityType)
    {
        var request = new EcsWorldCreateRequest(new WorldId(id), new EcsBudget(4, 32, 32, 4096));
        EcsWorldCreateResult created = module.CreateWorld(in request);
        EcsWorld world = Assert.IsType<EcsWorld>(created.World);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        EntityTypeRegistrationResult registration = world.RegisterEntityType(new EntityTypeDefinition("Player"));
        Assert.True(registration.Registered);
        entityType = registration.Handle;
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        return world;
    }
}
