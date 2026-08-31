using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class FollowupBoundaryTests
{
    [Fact]
    public void CommitDestroyRequiresTheOwningWorldIncarnation()
    {
        using var firstModule = new EcsModule();
        using var secondModule = new EcsModule();
        EcsWorld first = NewRunningWorld(firstModule, 700, out EntityTypeHandle firstType);
        EcsWorld second = NewRunningWorld(secondModule, 700, out EntityTypeHandle secondType);
        EntityCreateResult firstCreated = first.CreateEntityForCommit(first.Context, new EntityCreateRequest(firstType));
        EntityCreateResult secondCreated = second.CreateEntityForCommit(second.Context, new EntityCreateRequest(secondType));
        Assert.True(firstCreated.Created);
        Assert.True(secondCreated.Created);
        Assert.Equal(firstCreated.Entity, secondCreated.Entity);

        MethodInfo destroy = Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            static method => method.Name == "DestroyEntityForCommit" && method.GetParameters().Length == 1);
        ParameterInfo[] parameters = destroy.GetParameters();
        Type targetType = Assert.Single(
            typeof(EcsWorld).Assembly.GetTypes(),
            static type => type.Name == "WorldEntityTarget");
        Assert.Equal(targetType, parameters[0].ParameterType);

        PropertyInfo targetProperty = Assert.Single(
            typeof(EntityCreateResult).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static property => property.Name == "Target");
        object? value = destroy.Invoke(second, new[] { targetProperty.GetValue(firstCreated) });
        EntityDestroyResult wrongContext = Assert.IsType<EntityDestroyResult>(value);

        Assert.False(wrongContext.Destroyed);
        Assert.Equal(EcsErrorCodes.CrossWorld, wrongContext.Error?.Code);
        Assert.Equal(1, second.ActiveEntityCount);
        Assert.Equal(
            StorageOperationStatus.Accepted,
            second.ValidateEntityContext(second, secondCreated.Entity).Status);
    }

    [Fact]
    public void EntityTypeHandleCannotCrossSameWorldIdIncarnation()
    {
        using var firstModule = new EcsModule();
        using var secondModule = new EcsModule();
        EcsWorld first = NewRunningWorld(firstModule, 785, out EntityTypeHandle firstType);
        EcsWorld second = NewRunningWorld(secondModule, 785, out _);

        EntityCreateResult result = second.CreateEntityForCommit(
            second.Context,
            new EntityCreateRequest(firstType));

        Assert.False(result.Created);
        Assert.Equal(EcsErrorCodes.InvalidType, result.Error?.Code);
        Assert.Equal(0, second.ActiveEntityCount);
    }

    [Fact]
    public void WorldAndEntityCapabilitiesHaveNoFriendCallableMintingSurface()
    {
        Type[] capabilities =
        {
            Assert.Single(
                typeof(EcsWorld).Assembly.GetTypes(),
                static type => type.Name == "EcsWorldContext"),
            Assert.Single(
                typeof(EcsWorld).Assembly.GetTypes(),
                static type => type.Name == "WorldEntityTarget")
        };

        foreach (Type capability in capabilities)
        {
            Assert.Equal(typeof(EcsWorld), capability.DeclaringType);
            ConstructorInfo constructor = Assert.Single(
                capability.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.True(constructor.IsFamilyAndAssembly);
            Assert.Empty(capability.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }
    }

    [Fact]
    public void EntityTypeMembershipDoesNotTreatAContextlessHandleAsRegistered()
    {
        using var module = new EcsModule();
        EcsWorld world = CreateWorld(module, 787);
        var registered = new ComponentTypeHandle(world.WorldId, 1, world.Context);
        var forged = new ComponentTypeHandle(new WorldId(787), 1);
        var definition = new EntityTypeDefinition("ContextBound", new[] { registered });

        Assert.False(definition.HasComponent(forged));
    }

    [Fact]
    public void SnapshotHandleCannotCrossSameWorldIdIncarnation()
    {
        using var firstModule = new EcsModule();
        using var secondModule = new EcsModule();
        EcsWorld first = NewRunningWorld(firstModule, 786, out _);
        EcsWorld second = NewRunningWorld(secondModule, 786, out _);
        Assert.Equal(StorageOperationStatus.Accepted,
            first.CaptureReadSnapshot(new SnapshotId(1), new Revision(1), out StorageReadSnapshotHandle firstSnapshot).Status);
        Assert.Equal(StorageOperationStatus.Accepted,
            second.CaptureReadSnapshot(new SnapshotId(1), new Revision(1), out _).Status);
        var destination = new LocalEntityId[] { new(6, 6) };

        StorageOperationResult result = second.EnumerateSnapshotEntities(firstSnapshot, destination, out int written);

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(EcsErrorCodes.CrossWorld, result.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(new LocalEntityId(6, 6), destination[0]);
    }

    [Fact]
    public void PublicLiveReadsRequireTheOwningWorldIncarnation()
    {
        using var firstModule = new EcsModule();
        using var secondModule = new EcsModule();
        EcsWorld first = NewRunningWorld(firstModule, 788, out EntityTypeHandle firstType);
        EcsWorld second = NewRunningWorld(secondModule, 788, out EntityTypeHandle secondType);
        EntityCreateResult firstCreated = first.CreateEntityForCommit(
            first.Context,
            new EntityCreateRequest(firstType));
        EntityCreateResult secondCreated = second.CreateEntityForCommit(
            second.Context,
            new EntityCreateRequest(secondType));
        Assert.True(firstCreated.Created);
        Assert.True(secondCreated.Created);

        MethodInfo resolve = Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            static method => method.Name == "TryResolve" && method.GetParameters().Length == 3 &&
                method.GetParameters()[0].ParameterType == typeof(EcsWorld));
        MethodInfo enumerate = Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            static method => method.Name == "EnumerateActiveEntities" && method.GetParameters().Length == 3 &&
                method.GetParameters()[0].ParameterType == typeof(EcsWorld));

        object?[] resolveArgs = { first, firstCreated.Entity, EntityLifecycleState.Destroyed };
        bool resolved = Assert.IsType<bool>(resolve.Invoke(second, resolveArgs));
        var destination = new LocalEntityId[] { new(8, 8) };
        object?[] enumerateArgs = { first, destination, 0 };
        StorageOperationResult enumeration = Assert.IsType<StorageOperationResult>(enumerate.Invoke(second, enumerateArgs));

        Assert.False(resolved);
        Assert.Equal(StorageOperationStatus.Rejected, enumeration.Status);
        Assert.Equal(EcsErrorCodes.CrossWorld, enumeration.Error?.Code);
        Assert.Equal(0, Assert.IsType<int>(enumerateArgs[2]));
        Assert.Equal(new LocalEntityId(8, 8), destination[0]);
    }

    [Fact]
    public void FaultedWorldPublishesAnEmptyCountAndNoLiveReadState()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 701, out EntityTypeHandle entityType);
        EntityCreateResult created = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));
        Assert.True(created.Created);
        Assert.Equal(StorageOperationStatus.Accepted,
            world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);

        var destination = new LocalEntityId[] { new(99, 99) };
        StorageOperationResult enumeration = world.EnumerateActiveEntities(
            world,
            destination,
            out int written);

        Assert.Equal(0, world.ActiveEntityCount);
        Assert.Equal(StorageOperationStatus.Rejected, enumeration.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, enumeration.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(new LocalEntityId(99, 99), destination[0]);
        Assert.False(world.TryResolve(world, created.Entity, out EntityLifecycleState state));
        Assert.Equal(EntityLifecycleState.Destroyed, state);
    }

    [Fact]
    public void DisposedWorldPublishesAnEmptyCountAndNoLiveReadState()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 702, out EntityTypeHandle entityType);
        EntityCreateResult created = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));
        Assert.True(created.Created);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginDrain().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.DisposeWorld().Status);

        var destination = new LocalEntityId[] { new(88, 88) };
        StorageOperationResult enumeration = world.EnumerateActiveEntities(
            world,
            destination,
            out int written);

        Assert.Equal(0, world.ActiveEntityCount);
        Assert.Equal(StorageOperationStatus.Rejected, enumeration.Status);
        Assert.Equal(EcsErrorCodes.WorldDisposed, enumeration.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(new LocalEntityId(88, 88), destination[0]);
        Assert.False(world.TryResolve(world, created.Entity, out EntityLifecycleState state));
        Assert.Equal(EntityLifecycleState.Destroyed, state);
    }

    [Fact]
    public void IntegrityCallbackCannotResurrectAWorldFaultedDuringTheTransition()
    {
        var storage = new ReentrantStorageAdapter { FaultOnIntegrity = true };
        EcsWorld world = NewWorld(703, storage);
        storage.World = world;

        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        StorageOperationResult ready = world.MarkReady();

        Assert.Equal(StorageOperationStatus.Accepted, storage.CallbackResult.Status);
        Assert.Equal(StorageOperationStatus.Rejected, ready.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, ready.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
    }

    [Fact]
    public void CreateDoesNotLeaveStorageStateAfterAReentrantFault()
    {
        var storage = new ReentrantStorageAdapter { FaultOnCreate = true };
        EcsWorld world = NewRunningWorld(storage, 705, out EntityTypeHandle entityType);
        storage.World = world;

        EntityCreateResult result = world.CreateEntityForCommit(
            world.Context,
            new EntityCreateRequest(entityType));

        Assert.False(result.Created);
        Assert.Equal(EcsErrorCodes.WorldFaulted, result.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
        Assert.Equal(0, world.ActiveEntityCount);
        Assert.Equal(0, storage.StoredEntityCount);
    }

    [Fact]
    public void PreStartCrossThreadFaultIsAccepted()
    {
        using var module = new EcsModule();
        EcsWorld world = CreateWorld(module, 706);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        StorageOperationResult fault = default;

        var faultThread = new Thread(() =>
            fault = world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)));
        faultThread.Start();
        faultThread.Join();

        Assert.Equal(StorageOperationStatus.Accepted, fault.Status);
        Assert.Equal(EcsWorldState.Faulted, world.State);
    }

    [Fact]
    public void RunningNonOwnerPublicFaultIsRejectedWithoutChangingState()
    {
        using var module = new EcsModule();
        EcsWorld world = NewRunningWorld(module, 707, out _);
        StorageOperationResult fault = default;

        var faultThread = new Thread(() =>
            fault = world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)));
        faultThread.Start();
        faultThread.Join();

        Assert.Equal(StorageOperationStatus.Rejected, fault.Status);
        Assert.Equal("WrongContext", fault.Error?.Code);
        Assert.Equal(EcsWorldState.Running, world.State);
        Assert.Null(world.FirstFault);
    }

    [Fact]
    public void WorkerSnapshotAdapterExceptionStillEntersInternalFailStop()
    {
        var storage = new ThrowingSnapshotAdapter { ThrowOnEnumeration = true };
        EcsWorld world = StartWorldWithSnapshot(storage, 708, out StorageReadSnapshotHandle snapshot);
        StorageOperationResult result = default;
        int written = -1;

        var readThread = new Thread(() =>
        {
            var destination = new LocalEntityId[1];
            result = world.EnumerateSnapshotEntities(snapshot, destination, out written);
        });
        readThread.Start();
        readThread.Join();

        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Equal(EcsWorldState.Faulted, world.State);
        Assert.NotNull(world.FirstFault);
        Assert.Equal(0, written);
    }

    [Fact]
    public void ConcurrentStartAndFaultExposeOnlyValidAtomicOrderings()
    {
        for (int round = 0; round < 64; round++)
        {
            using var module = new EcsModule();
            EcsWorld world = CreateWorld(module, checked((ulong)(710 + round)));
            Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
            Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);

            using var startGate = new Barrier(2);
            Exception? startFailure = null;
            Exception? faultFailure = null;
            StorageOperationResult start = default;
            StorageOperationResult fault = default;
            var startThread = new Thread(() =>
            {
                try
                {
                    startGate.SignalAndWait(TimeSpan.FromSeconds(5));
                    start = world.Start();
                }
                catch (Exception exception)
                {
                    startFailure = exception;
                }
            });
            var faultThread = new Thread(() =>
            {
                try
                {
                    startGate.SignalAndWait(TimeSpan.FromSeconds(5));
                    fault = world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState));
                }
                catch (Exception exception)
                {
                    faultFailure = exception;
                }
            });

            startThread.Start();
            faultThread.Start();
            startThread.Join();
            faultThread.Join();

            Assert.Null(startFailure);
            Assert.Null(faultFailure);
            bool faultWon =
                fault.Status == StorageOperationStatus.Accepted &&
                start.Status == StorageOperationStatus.Rejected &&
                world.State == EcsWorldState.Faulted;
            bool startWon =
                start.Status == StorageOperationStatus.Accepted &&
                fault.Status == StorageOperationStatus.Rejected &&
                fault.Error?.Code == "WrongContext" &&
                world.State == EcsWorldState.Running;
            Assert.True(faultWon || startWon);
        }
    }

    [Fact]
    public void ForceCleanupExposesDrainBeforeDisposal()
    {
        var storage = new ReentrantStorageAdapter();
        EcsWorld world = NewWorld(779, storage);
        storage.World = world;
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);

        world.ForceCleanup();

        Assert.Equal(EcsWorldState.Draining, storage.StateObservedDuringDispose);
        Assert.Equal(EcsWorldState.Disposed, world.State);
    }

    [Fact]
    public void SnapshotEnumerationAdapterExceptionEntersTheCommonFailStopBoundary()
    {
        var storage = new ThrowingSnapshotAdapter
        {
            ThrowOnEnumeration = true,
            WriteBeforeThrow = true
        };
        EcsWorld world = StartWorldWithSnapshot(storage, 780, out StorageReadSnapshotHandle snapshot);
        var destination = new LocalEntityId[] { new(7, 7) };

        StorageOperationResult result = world.EnumerateSnapshotEntities(snapshot, destination, out int written);

        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Equal(EcsErrorCodes.PostWriteFailure, result.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
        Assert.Equal(0, written);
        Assert.Equal(new LocalEntityId(7, 7), destination[0]);
    }

    [Fact]
    public void SnapshotReadAdapterExceptionEntersTheCommonFailStopBoundary()
    {
        var storage = new ThrowingSnapshotAdapter
        {
            ThrowOnRead = true,
            WriteBeforeThrow = true
        };
        EcsWorld world = StartWorldWithSnapshot(storage, 781, out StorageReadSnapshotHandle snapshot);
        var destination = new byte[] { 7, 7, 7, 7 };

        StorageOperationResult result = world.ReadSnapshotField(
            snapshot,
            new LocalEntityId(1, 1),
            new ComponentTypeId(10),
            new ComponentFieldId(1),
            destination,
            out int written);

        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Equal(EcsErrorCodes.PostWriteFailure, result.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
        Assert.Equal(0, written);
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, destination);
        Assert.True(world.FirstFault.HasValue);
        Assert.Equal(new LocalEntityId(1, 1), world.FirstFault.Value.Context.Entity);
        Assert.Equal(new ComponentTypeId(10), world.FirstFault.Value.Context.ComponentType);
        Assert.Equal(new ComponentFieldId(1), world.FirstFault.Value.Context.Field);
    }

    [Fact]
    public void SnapshotReleaseAdapterExceptionEntersTheCommonFailStopBoundary()
    {
        var storage = new ThrowingSnapshotAdapter { ThrowOnRelease = true };
        EcsWorld world = StartWorldWithSnapshot(storage, 782, out StorageReadSnapshotHandle snapshot);

        StorageOperationResult result = world.ReleaseReadSnapshot(snapshot);

        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Equal(EcsErrorCodes.PostWriteFailure, result.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.State);
    }

    [Fact]
    public void FaultedSnapshotLeaseCanBeReleasedExactlyOnceWithoutReopeningReads()
    {
        var storage = new ThrowingSnapshotAdapter();
        EcsWorld world = StartWorldWithSnapshot(storage, 789, out StorageReadSnapshotHandle snapshot);
        Assert.Equal(StorageOperationStatus.Accepted,
            world.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState)).Status);
        var destination = new LocalEntityId[1];

        StorageOperationResult readBeforeRelease =
            world.EnumerateSnapshotEntities(snapshot, destination, out int writtenBeforeRelease);
        StorageOperationResult firstRelease = world.ReleaseReadSnapshot(snapshot);
        StorageOperationResult secondRelease = world.ReleaseReadSnapshot(snapshot);
        StorageOperationResult readAfterRelease =
            world.EnumerateSnapshotEntities(snapshot, destination, out int writtenAfterRelease);

        Assert.Equal(StorageOperationStatus.Rejected, readBeforeRelease.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, readBeforeRelease.Error?.Code);
        Assert.Equal(0, writtenBeforeRelease);
        Assert.Equal(StorageOperationStatus.Accepted, firstRelease.Status);
        Assert.Equal(StorageOperationStatus.Rejected, secondRelease.Status);
        Assert.Equal("HandleDoubleRelease", secondRelease.Error?.Code);
        Assert.Equal(1, storage.ReleaseCallCount);
        Assert.Equal(StorageOperationStatus.Rejected, readAfterRelease.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, readAfterRelease.Error?.Code);
        Assert.Equal(0, writtenAfterRelease);
    }

    [Fact]
    public void SnapshotEnumerationDoesNotPublishAfterAReentrantFault()
    {
        var storage = new ReentrantSnapshotAdapter { WriteBeforeFault = true };
        EcsWorld world = StartWorldWithSnapshot(storage, 783, out StorageReadSnapshotHandle snapshot);
        storage.World = world;
        var destination = new LocalEntityId[] { new(8, 8) };

        StorageOperationResult result = world.EnumerateSnapshotEntities(snapshot, destination, out int written);

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, result.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(new LocalEntityId(8, 8), destination[0]);
    }

    [Fact]
    public void SnapshotReadDoesNotPublishAfterAReentrantFault()
    {
        var storage = new ReentrantSnapshotAdapter { WriteBeforeFault = true };
        EcsWorld world = StartWorldWithSnapshot(storage, 784, out StorageReadSnapshotHandle snapshot);
        storage.World = world;
        var destination = new byte[] { 8, 8, 8, 8 };

        StorageOperationResult result = world.ReadSnapshotField(
            snapshot,
            new LocalEntityId(1, 1),
            new ComponentTypeId(10),
            new ComponentFieldId(1),
            destination,
            out int written);

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, result.Error?.Code);
        Assert.Equal(0, written);
        Assert.Equal(new byte[] { 8, 8, 8, 8 }, destination);
    }

    private static EcsWorld StartWorldWithSnapshot(
        IWorldStorageAdapter storage,
        ulong id,
        out StorageReadSnapshotHandle snapshot)
    {
        EcsWorld world = NewWorld(id, storage);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        EntityTypeRegistrationResult registration = world.RegisterEntityType(new EntityTypeDefinition("Player"));
        Assert.True(registration.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        Assert.Equal(StorageOperationStatus.Accepted,
            world.CaptureReadSnapshot(new SnapshotId(1), new Revision(1), out snapshot).Status);
        return world;
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

    private static EcsWorld NewRunningWorld(
        ReentrantStorageAdapter storage,
        ulong id,
        out EntityTypeHandle entityType)
    {
        EcsWorld world = NewWorld(id, storage);
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
        var request = new EcsWorldCreateRequest(new WorldId(id), new EcsBudget(4, 32, 32, 4096));
        return Assert.IsType<EcsWorld>(module.CreateWorld(in request).World);
    }

    private static EcsWorld NewWorld(ulong id, IWorldStorageAdapter storage)
    {
        var request = new EcsWorldCreateRequest(new WorldId(id), new EcsBudget(4, 32, 32, 4096));
        ConstructorInfo constructor = Assert.Single(
            typeof(EcsWorld).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic),
            static candidate => candidate.GetParameters().Length == 2);
        return Assert.IsType<EcsWorld>(constructor.Invoke(new object?[] { request, storage }));
    }

    private sealed class ReentrantStorageAdapter : IWorldStorageAdapter
    {
        public EcsWorld? World { get; set; }
        public bool FaultOnIntegrity { get; init; }
        public bool FaultOnCreate { get; init; }
        public StorageOperationResult CallbackResult { get; private set; }
        public EcsWorldState? StateObservedDuringDispose { get; private set; }
        public int StoredEntityCount { get; private set; }

        public StorageOperationResult Register(ComponentTypeDefinition definition) => StorageOperationResult.Accepted();
        public StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components)
        {
            if (FaultOnCreate) World!.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState));
            StoredEntityCount++;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult Destroy(LocalEntityId entity)
        {
            if (StoredEntityCount > 0) StoredEntityCount--;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle)
        {
            handle = default;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult EnumerateOrdered(StorageQueryHandle handle, Span<LocalEntityId> destination, out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReadField(LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, Span<byte> destination, out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult WriteExistingField(LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, ReadOnlySpan<byte> canonicalValue) => StorageOperationResult.Accepted();
        public StorageOperationResult CaptureReadSnapshot(in StorageSnapshotContext context, out StorageReadSnapshotHandle handle)
        {
            handle = new StorageReadSnapshotHandle(1, context);
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult EnumerateSnapshotOrdered(StorageReadSnapshotHandle handle, Span<LocalEntityId> destination, out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReadSnapshotField(StorageReadSnapshotHandle handle, LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, Span<byte> destination, out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle) => StorageOperationResult.Accepted();
        public StorageOperationResult ValidateIntegrity()
        {
            CallbackResult = FaultOnIntegrity
                ? World!.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState))
                : StorageOperationResult.Accepted();
            return StorageOperationResult.Accepted();
        }
        public void Dispose()
        {
            StateObservedDuringDispose = World?.State;
        }
    }

    private sealed class ThrowingSnapshotAdapter : IWorldStorageAdapter
    {
        public bool ThrowOnEnumeration { get; init; }
        public bool ThrowOnRead { get; init; }
        public bool ThrowOnRelease { get; init; }
        public bool WriteBeforeThrow { get; init; }
        public int ReleaseCallCount { get; private set; }

        public StorageOperationResult Register(ComponentTypeDefinition definition) => StorageOperationResult.Accepted();
        public StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components) => StorageOperationResult.Accepted();
        public StorageOperationResult Destroy(LocalEntityId entity) => StorageOperationResult.Accepted();
        public StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle)
        {
            handle = default;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult EnumerateOrdered(StorageQueryHandle handle, Span<LocalEntityId> destination, out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReadField(LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, Span<byte> destination, out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult WriteExistingField(LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, ReadOnlySpan<byte> canonicalValue) => StorageOperationResult.Accepted();
        public StorageOperationResult CaptureReadSnapshot(in StorageSnapshotContext context, out StorageReadSnapshotHandle handle)
        {
            handle = new StorageReadSnapshotHandle(1, context);
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult EnumerateSnapshotOrdered(StorageReadSnapshotHandle handle, Span<LocalEntityId> destination, out int written)
        {
            written = 0;
            if (ThrowOnEnumeration)
            {
                if (WriteBeforeThrow && destination.Length > 0) destination[0] = new LocalEntityId(1, 1);
                written = 1;
                throw new InvalidOperationException("enumeration adapter failure");
            }
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReadSnapshotField(StorageReadSnapshotHandle handle, LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, Span<byte> destination, out int written)
        {
            written = 0;
            if (ThrowOnRead)
            {
                if (WriteBeforeThrow && destination.Length > 0) destination[0] = 1;
                written = 1;
                throw new InvalidOperationException("read adapter failure");
            }
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle)
        {
            ReleaseCallCount++;
            if (ThrowOnRelease) throw new InvalidOperationException("release adapter failure");
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ValidateIntegrity() => StorageOperationResult.Accepted();
        public void Dispose()
        {
        }
    }

    private sealed class ReentrantSnapshotAdapter : IWorldStorageAdapter
    {
        public EcsWorld? World { get; set; }
        public bool WriteBeforeFault { get; init; }

        public StorageOperationResult Register(ComponentTypeDefinition definition) => StorageOperationResult.Accepted();
        public StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components) => StorageOperationResult.Accepted();
        public StorageOperationResult Destroy(LocalEntityId entity) => StorageOperationResult.Accepted();
        public StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle)
        {
            handle = default;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult EnumerateOrdered(StorageQueryHandle handle, Span<LocalEntityId> destination, out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReadField(LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, Span<byte> destination, out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult WriteExistingField(LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, ReadOnlySpan<byte> canonicalValue) => StorageOperationResult.Accepted();
        public StorageOperationResult CaptureReadSnapshot(in StorageSnapshotContext context, out StorageReadSnapshotHandle handle)
        {
            handle = new StorageReadSnapshotHandle(1, context);
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult EnumerateSnapshotOrdered(StorageReadSnapshotHandle handle, Span<LocalEntityId> destination, out int written)
        {
            written = 1;
            if (WriteBeforeFault && destination.Length > 0) destination[0] = new LocalEntityId(1, 1);
            World!.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState));
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReadSnapshotField(StorageReadSnapshotHandle handle, LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field, Span<byte> destination, out int written)
        {
            written = 1;
            if (WriteBeforeFault && destination.Length > 0) destination[0] = 1;
            World!.Fault(new ErrorIdentity(EcsErrorCodes.InvalidState));
            return StorageOperationResult.Accepted();
        }
        public StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle) => StorageOperationResult.Accepted();
        public StorageOperationResult ValidateIntegrity() => StorageOperationResult.Accepted();
        public void Dispose()
        {
        }
    }
}
