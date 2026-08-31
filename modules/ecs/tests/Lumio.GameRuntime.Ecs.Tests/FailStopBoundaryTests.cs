using System;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class FailStopBoundaryTests
{
    [Fact]
    public void GenerationOverflowFaultsWorldAndRejectsLaterWork()
    {
        var storage = new ScriptedStorageAdapter();
        var slots = new EntitySlotTable(maxSlots: 1, initialGeneration: uint.MaxValue);
        EcsWorld world = NewWorld(400, storage, slots);
        try
        {
            EntityTypeHandle entityType = StartWithEntityType(world);
            EntityCreateResult first = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));
            Assert.True(first.Created);
            Assert.True(world.DestroyEntityForCommit(first.Target).Destroyed);

            EntityCreateResult overflow = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));
            EntityCreateResult later = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));

            Assert.False(overflow.Created);
            Assert.Equal(StorageOperationStatus.Fatal, overflow.Result.Status);
            Assert.Equal(EcsWorldState.Faulted, world.State);
            Assert.False(later.Created);
            Assert.Equal(StorageOperationStatus.Rejected, later.Result.Status);
            Assert.Equal(EcsErrorCodes.WorldFaulted, later.Error?.Code);
        }
        finally
        {
            world.ForceCleanup();
        }
    }

    [Fact]
    public void FatalAdapterRegistrationFaultsWorld()
    {
        var storage = new ScriptedStorageAdapter
        {
            RegisterResult = StorageOperationResult.Fatal(EcsErrorCodes.InvalidState)
        };
        EcsWorld world = NewWorld(401, storage);
        try
        {
            Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);

            ComponentTypeRegistrationResult registration = EcsTestRegistration.Register(world, PositionComponent());
            EntityTypeRegistrationResult later = world.RegisterEntityType(new EntityTypeDefinition("Later"));

            Assert.False(registration.Registered);
            Assert.Equal(StorageOperationStatus.Fatal, registration.Result.Status);
            Assert.Equal(EcsWorldState.Faulted, world.State);
            Assert.False(later.Registered);
            Assert.Equal(StorageOperationStatus.Rejected, later.Result.Status);
            Assert.Equal(EcsErrorCodes.WorldFaulted, later.Error?.Code);
            Assert.True(world.FirstFault.HasValue);
            Assert.Equal(new ComponentTypeId(10), world.FirstFault.Value.Context.ComponentType);
            Assert.Equal(0, world.FirstFault.Value.PartialChangeCount);
        }
        finally
        {
            world.ForceCleanup();
        }
    }

    [Fact]
    public void FatalIntegrityValidationFaultsWorld()
    {
        var storage = new ScriptedStorageAdapter
        {
            IntegrityResult = StorageOperationResult.Fatal(EcsErrorCodes.InvalidState)
        };
        EcsWorld world = NewWorld(402, storage);
        try
        {
            Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);

            StorageOperationResult ready = world.MarkReady();

            Assert.Equal(StorageOperationStatus.Fatal, ready.Status);
            Assert.Equal(EcsWorldState.Faulted, world.State);
            Assert.Equal(StorageOperationStatus.Rejected, world.Start().Status);
            Assert.Equal(EcsErrorCodes.WorldFaulted, world.Start().Error?.Code);
        }
        finally
        {
            world.ForceCleanup();
        }
    }

    [Fact]
    public void IndeterminatePostWriteCreateNeverReportsPartialSuccess()
    {
        var storage = new ScriptedStorageAdapter
        {
            CreateResult = new StorageOperationResult(
                StorageOperationStatus.Indeterminate,
                new ErrorIdentity(EcsErrorCodes.InvalidState)),
            RecordCreateBeforeResult = true
        };
        EcsWorld world = NewWorld(403, storage);
        try
        {
            EntityTypeHandle entityType = StartWithEntityType(world);

            EntityCreateResult created = world.CreateEntityForCommit(world.Context, new EntityCreateRequest(entityType));
            var destination = new LocalEntityId[1];
            StorageOperationResult enumeration = world.EnumerateActiveEntities(
                world,
                destination,
                out int written);

            Assert.False(created.Created);
            Assert.Equal(StorageOperationStatus.Fatal, created.Result.Status);
            Assert.Equal(EcsWorldState.Faulted, world.State);
            Assert.Equal(1, storage.StoredEntityCount);
            Assert.Equal(0, world.ActiveEntityCount);
            Assert.Equal(StorageOperationStatus.Rejected, enumeration.Status);
            Assert.Equal(0, written);
            Assert.Equal(default, destination[0]);
            Assert.True(world.FirstFault.HasValue);
            Assert.Equal(created.Entity, world.FirstFault.Value.Context.Entity);
            Assert.Equal("Create", world.FirstFault.Value.Context.Operation);
            Assert.Equal(1, world.FirstFault.Value.PartialChangeCount);
        }
        finally
        {
            world.ForceCleanup();
        }
    }

    [Fact]
    public void PostWriteCreateFailureCapturesImmutableOperationEvidence()
    {
        var storage = new ScriptedStorageAdapter
        {
            RecordCreateBeforeResult = true,
            ThrowAfterCreate = true
        };
        EcsWorld world = NewWorld(404, storage);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        ComponentTypeRegistrationResult component = EcsTestRegistration.Register(world, PositionComponent());
        Assert.True(component.Registered);
        EntityTypeRegistrationResult entityType = world.RegisterEntityType(new EntityTypeDefinition(
            "Player",
            new[] { component.Handle }));
        Assert.True(entityType.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        Type? operationEvidenceType = typeof(EcsWorld).Assembly.GetType(
            "Lumio.GameRuntime.Ecs.EcsOperationEvidence");
        Assert.NotNull(operationEvidenceType);
        ConstructorInfo operationEvidenceConstructor = Assert.Single(operationEvidenceType.GetConstructors());
        object operationEvidence = operationEvidenceConstructor.Invoke(new object?[]
        {
            new TickId(41),
            new ProcessorId(7),
            "create-evidence-41"
        });
        ComponentInitBatch initialValues = new(new[]
        {
            new ComponentInitValue(
                new ComponentTypeId(10),
                new ComponentFieldId(1),
                new byte[] { 1, 2, 3, 4 })
        });
        ConstructorInfo requestConstructor = Assert.Single(
            typeof(EntityCreateRequest).GetConstructors(),
            static constructor => constructor.GetParameters().Length == 3);
        object request = requestConstructor.Invoke(new object?[]
        {
            entityType.Handle,
            initialValues,
            operationEvidence
        });
        MethodInfo create = Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            static method => method.Name == "CreateEntityForCommit");

        EntityCreateResult result = Assert.IsType<EntityCreateResult>(
            create.Invoke(world, new[] { world.Context, request }));

        Assert.False(result.Created);
        Assert.Equal(StorageOperationStatus.Fatal, result.Result.Status);
        Assert.True(world.FirstFault.HasValue);
        EcsFaultEvidence captured = world.FirstFault.Value;
        Assert.Equal(new TickId(41), captured.Context.TickId);
        PropertyInfo? processor = typeof(FailureContext).GetProperty("ProcessorId");
        PropertyInfo? evidenceIdentity = typeof(FailureContext).GetProperty("EvidenceIdentity");
        Assert.NotNull(processor);
        Assert.NotNull(evidenceIdentity);
        Assert.Equal(new ProcessorId(7), processor.GetValue(captured.Context));
        Assert.Equal("create-evidence-41", evidenceIdentity.GetValue(captured.Context));
        Assert.Equal(result.Entity, captured.Context.Entity);
        Assert.Equal(new ComponentTypeId(10), captured.Context.ComponentType);
        Assert.Equal(new ComponentFieldId(1), captured.Context.Field);
        Assert.Equal("Create", captured.Context.Operation);
        Assert.Equal(1, captured.PartialChangeCount);

        world.ForceCleanup();

        Assert.Equal(captured, world.FirstFault);
    }

    [Fact]
    public void PostWriteDestroyFailureCapturesEntityAndPartialChangeEvidence()
    {
        var storage = new ScriptedStorageAdapter
        {
            RecordCreateBeforeResult = true,
            ThrowAfterDestroy = true
        };
        EcsWorld world = NewWorld(405, storage);
        EntityTypeHandle entityType = StartWithEntityType(world);
        EntityCreateResult created = world.CreateEntityForCommit(
            world.Context,
            new EntityCreateRequest(entityType));
        Assert.True(created.Created);

        EntityDestroyResult destroyed = world.DestroyEntityForCommit(created.Target);

        Assert.False(destroyed.Destroyed);
        Assert.Equal(StorageOperationStatus.Fatal, destroyed.Result.Status);
        Assert.True(world.FirstFault.HasValue);
        Assert.Equal(created.Entity, world.FirstFault.Value.Context.Entity);
        Assert.Equal("Destroy", world.FirstFault.Value.Context.Operation);
        Assert.Equal(1, world.FirstFault.Value.PartialChangeCount);
    }

    [Fact]
    public void FatalAndIndeterminateResultsUseOneWorldBoundary()
    {
        Assert.Single(
            typeof(EcsWorld).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            static method => method.Name == "CompleteBoundary");
    }

    private static EcsWorld NewWorld(
        ulong id,
        IWorldStorageAdapter storage,
        EntitySlotTable? slots = null)
    {
        var request = new EcsWorldCreateRequest(
            new WorldId(id),
            new EcsBudget(slots?.Capacity ?? 4, 32, 32, 4096));
        if (slots is null) return new EcsWorld(in request, storage);

        ConstructorInfo constructor = Assert.Single(
            typeof(EcsWorld).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic),
            static candidate => candidate.GetParameters().Length == 3);
        return Assert.IsType<EcsWorld>(constructor.Invoke(new object?[] { request, storage, slots }));
    }

    private static EntityTypeHandle StartWithEntityType(EcsWorld world)
    {
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        EntityTypeRegistrationResult registration = world.RegisterEntityType(new EntityTypeDefinition("Player"));
        Assert.True(registration.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
        return registration.Handle;
    }

    private static ComponentTypeDefinition PositionComponent() => new(
        new ComponentTypeId(10),
        "Position",
        new[] { new ComponentFieldDefinition(new ComponentFieldId(1), 4) });

    private sealed class ScriptedStorageAdapter : IWorldStorageAdapter
    {
        public StorageOperationResult RegisterResult { get; init; } = StorageOperationResult.Accepted();
        public StorageOperationResult IntegrityResult { get; init; } = StorageOperationResult.Accepted();
        public StorageOperationResult CreateResult { get; init; } = StorageOperationResult.Accepted();
        public bool RecordCreateBeforeResult { get; init; }
        public bool ThrowAfterCreate { get; init; }
        public bool ThrowAfterDestroy { get; init; }
        public int StoredEntityCount { get; private set; }

        public StorageOperationResult Register(ComponentTypeDefinition definition) => RegisterResult;

        public StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components)
        {
            if (RecordCreateBeforeResult) StoredEntityCount++;
            if (ThrowAfterCreate) throw new InvalidOperationException("create failed after storage mutation");
            return CreateResult;
        }

        public StorageOperationResult Destroy(LocalEntityId entity)
        {
            if (StoredEntityCount > 0) StoredEntityCount--;
            if (ThrowAfterDestroy) throw new InvalidOperationException("destroy failed after storage mutation");
            return StorageOperationResult.Accepted();
        }

        public StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle)
        {
            handle = default;
            return StorageOperationResult.Accepted();
        }

        public StorageOperationResult EnumerateOrdered(
            StorageQueryHandle handle,
            Span<LocalEntityId> destination,
            out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }

        public StorageOperationResult ReadField(
            LocalEntityId entity,
            ComponentTypeId componentType,
            ComponentFieldId field,
            Span<byte> destination,
            out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }

        public StorageOperationResult WriteExistingField(
            LocalEntityId entity,
            ComponentTypeId componentType,
            ComponentFieldId field,
            ReadOnlySpan<byte> canonicalValue) => StorageOperationResult.Accepted();

        public StorageOperationResult CaptureReadSnapshot(
            in StorageSnapshotContext context,
            out StorageReadSnapshotHandle handle)
        {
            handle = new StorageReadSnapshotHandle(1, context);
            return StorageOperationResult.Accepted();
        }

        public StorageOperationResult EnumerateSnapshotOrdered(
            StorageReadSnapshotHandle handle,
            Span<LocalEntityId> destination,
            out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }

        public StorageOperationResult ReadSnapshotField(
            StorageReadSnapshotHandle handle,
            LocalEntityId entity,
            ComponentTypeId componentType,
            ComponentFieldId field,
            Span<byte> destination,
            out int written)
        {
            written = 0;
            return StorageOperationResult.Accepted();
        }

        public StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle) =>
            StorageOperationResult.Accepted();

        public StorageOperationResult ValidateIntegrity() => IntegrityResult;

        public void Dispose()
        {
        }
    }
}
