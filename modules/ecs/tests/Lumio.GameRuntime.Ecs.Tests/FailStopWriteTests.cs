using System;
using System.Collections.Generic;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class FailStopWriteTests
{
    [Fact]
    public void FailureAfterExistingFieldWriteFaultsWorldWithoutFieldRollback()
    {
        using var harness = Fixtures.WorldWithPostWriteFailure();
        StorageOperationResult result = WriteExistingFieldForTest(harness, Fixtures.ValidWrite(harness.Entity));

        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Contains(result.Error!.Value.Code, Catalog.StableErrorIds);
        Assert.NotEqual("changeset append failed after field write", result.Error.Value.Code);
        Assert.Equal(EcsErrorCodes.PostWriteFailure, result.Error.Value.Code);
        Assert.Equal(EcsWorldState.Faulted, harness.World.State);
        Assert.Equal(Fixtures.AfterBytes, harness.StorageBytes(Fixtures.ValidWrite(harness.Entity)));
        Assert.Equal(0, harness.StorageUndoCalls);
        Assert.Equal(1, harness.Storage.WriteCalls);
        Assert.True(harness.World.FirstFault.HasValue);
        EcsFaultEvidence evidence = harness.World.FirstFault.Value;
        Assert.Equal(Fixtures.Tick, evidence.Context.TickId);
        Assert.Equal(Fixtures.Processor, evidence.Context.ProcessorId);
        Assert.Equal(harness.Entity, evidence.Context.Entity);
        Assert.Equal(Fixtures.PositionId, evidence.Context.ComponentType);
        Assert.Equal(Fixtures.PositionField, evidence.Context.Field);
        Assert.Equal(Fixtures.HashFor(harness.Entity), evidence.Context.EvidenceIdentity);
        Assert.Equal(1, evidence.PartialChangeCount);
        Assert.Equal(evidence, Assert.Single(harness.Sink.Published));
    }

    [Fact]
    public void OwnerThreadViolationFaultsWriteBeforeStorageWhileWorkerMayReadSnapshot()
    {
        using var harness = Fixtures.WorldWithPostWriteFailure();
        Assert.Equal(StorageOperationStatus.Accepted, harness.World.CaptureReadSnapshot(
            new SnapshotId(1),
            new Revision(1),
            out StorageReadSnapshotHandle snapshot).Status);
        int writesBefore = harness.Storage.WriteCalls;
        StorageOperationResult snapshotRead = default;
        StorageOperationResult write = default;
        int snapshotWritten = -1;
        var snapshotBytes = new byte[] { 0, 0, 0, 0 };
        var worker = new Thread(() =>
        {
            snapshotRead = harness.World.ReadSnapshotField(
                snapshot,
                harness.Entity,
                Fixtures.PositionId,
                Fixtures.PositionField,
                snapshotBytes,
                out snapshotWritten);
            write = WriteExistingFieldForTest(harness, Fixtures.ValidWrite(harness.Entity));
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));

        Assert.Equal(StorageOperationStatus.Accepted, snapshotRead.Status);
        Assert.Equal(4, snapshotWritten);
        Assert.Equal(Fixtures.BeforeBytes, snapshotBytes);
        Assert.Equal(StorageOperationStatus.Fatal, write.Status);
        Assert.Equal(EcsErrorCodes.OwnerThreadViolation, write.Error?.Code);
        Assert.Contains(write.Error!.Value.Code, Catalog.StableErrorIds);
        Assert.Equal(EcsWorldState.Faulted, harness.World.State);
        Assert.Equal(writesBefore, harness.Storage.WriteCalls);
        Assert.Equal(0, harness.StorageUndoCalls);
        Assert.Equal(Fixtures.BeforeBytes, harness.StorageBytes(Fixtures.ValidWrite(harness.Entity)));
    }

    [Fact]
    public void InjectedTokenProviderIsCapturedAtStartAndGuardsWritesWithoutWallClock()
    {
        var tokens = new ScriptedOwnerThreadTokenProvider(7);
        using var harness = Fixtures.WorldWithPostWriteFailure(tokens);
        Assert.Equal(7, harness.World.OwnerThreadId);

        tokens.CurrentToken = 8;
        int writesBefore = harness.Storage.WriteCalls;
        StorageOperationResult result = WriteExistingFieldForTest(harness, Fixtures.ValidWrite(harness.Entity));

        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Equal(EcsErrorCodes.OwnerThreadViolation, result.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, harness.World.State);
        Assert.Equal(writesBefore, harness.Storage.WriteCalls);
        Assert.Equal(0, harness.StorageUndoCalls);
    }

    [Fact]
    public void AdapterExceptionBecomesGeneratedFatalAndPublishesDurableEvidence()
    {
        using var harness = Fixtures.WorldWithPostWriteFailure(throwOnWrite: true);
        StorageOperationResult result = WriteExistingFieldForTest(harness, Fixtures.ValidWrite(harness.Entity));

        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Contains(result.Error!.Value.Code, Catalog.StableErrorIds);
        Assert.NotEqual("adapter exploded after field write", result.Error.Value.Code);
        Assert.Equal(EcsErrorCodes.PostWriteFailure, result.Error.Value.Code);
        Assert.Equal(EcsWorldState.Faulted, harness.World.State);
        Assert.Equal(Fixtures.AfterBytes, harness.StorageBytes(Fixtures.ValidWrite(harness.Entity)));
        Assert.Equal(0, harness.StorageUndoCalls);
        Assert.Equal(Fixtures.HashFor(harness.Entity), Assert.Single(harness.Sink.Published).Context.EvidenceIdentity);
    }

    [Fact]
    public void FaultedWorldDoesNotCatchAndContinueOnLaterWrites()
    {
        using var harness = Fixtures.WorldWithPostWriteFailure();
        Assert.Equal(StorageOperationStatus.Fatal,
            WriteExistingFieldForTest(harness, Fixtures.ValidWrite(harness.Entity)).Status);
        int writesAfterFault = harness.Storage.WriteCalls;

        StorageOperationResult later = WriteExistingFieldForTest(harness, Fixtures.ValidWrite(harness.Entity));

        Assert.Equal(StorageOperationStatus.Rejected, later.Status);
        Assert.Equal(EcsErrorCodes.WorldFaulted, later.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, harness.World.State);
        Assert.Equal(writesAfterFault, harness.Storage.WriteCalls);
        Assert.Equal(0, harness.StorageUndoCalls);
    }

    [Fact]
    public void OwnerThreadGuardUsesInjectedTokenProvider()
    {
        var tokens = new ScriptedOwnerThreadTokenProvider(42);
        var guard = new OwnerThreadGuard(tokens);

        Assert.Equal(StorageOperationStatus.Accepted, guard.BindCurrentThread().Status);
        Assert.Equal(42, guard.OwnerThreadId);
        Assert.Equal(StorageOperationStatus.Accepted, guard.ValidateCurrentThread().Status);

        tokens.CurrentToken = 99;
        StorageOperationResult result = guard.ValidateCurrentThread();

        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(EcsErrorCodes.OwnerThreadViolation, result.Error?.Code);
    }

    [Fact]
    public void FailStopControllerMapsAdapterExceptionToGeneratedIdentityNotExceptionText()
    {
        var sink = new RecordingFailureSink();
        var controller = new EcsFailStopController(sink);
        var exception = new InvalidOperationException("not-a-stable-error-code");
        EcsWorldState state = EcsWorldState.Running;

        StorageOperationResult result = controller.CaptureAdapterFailure(
            StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure),
            ref state,
            new FailureContext(
                new WorldId(1522),
                Fixtures.Tick,
                Fixtures.Processor,
                new LocalEntityId(1, 1),
                Fixtures.PositionId,
                Fixtures.PositionField,
                "WriteExistingField",
                Fixtures.HashFor(new LocalEntityId(1, 1))),
            partialChangeCount: 1,
            exception);

        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Equal(EcsErrorCodes.PostWriteFailure, result.Error?.Code);
        Assert.NotEqual(exception.Message, result.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, state);
        Assert.Equal(Fixtures.HashFor(new LocalEntityId(1, 1)), Assert.Single(sink.Published).Context.EvidenceIdentity);
        Assert.False(controller.CaptureAdapterFailure(
            StorageOperationResult.Fatal(EcsErrorCodes.InvalidState),
            ref state,
            new FailureContext(
                new WorldId(1522),
                Fixtures.Tick,
                Fixtures.Processor,
                new LocalEntityId(1, 1),
                Fixtures.PositionId,
                Fixtures.PositionField,
                "WriteExistingField"),
            0,
            exception).IsSuccess);
        Assert.Equal(EcsErrorCodes.PostWriteFailure, controller.First!.Value.Error.Code);
        Assert.Equal(EcsErrorCodes.PostWriteFailure, Assert.Single(sink.Published).Error.Code);
    }

    private static StorageOperationResult WriteExistingFieldForTest(
        WorldHarness harness,
        in EcsFieldWrite write) =>
        harness.World.WriteExistingField(in write, harness.ChangeSet);

    private static class Fixtures
    {
        public static readonly byte[] BeforeBytes = { 1, 2, 3, 4 };
        public static readonly byte[] AfterBytes = { 9, 8, 7, 6 };
        public static readonly TickId Tick = new(41);
        public static readonly ProcessorId Processor = new(7);
        public static readonly ComponentTypeId PositionId = new(10);
        public static readonly ComponentFieldId PositionField = new(1);
        public static string HashFor(LocalEntityId entity) => EcsFailStopController.Hash(
            Tick,
            Processor,
            entity,
            PositionId,
            PositionField);

        public static EcsFieldWrite ValidWrite(LocalEntityId entity) => new(
            entity,
            PositionId,
            PositionField,
            AfterBytes,
            new EcsOperationEvidence(Tick, Processor, HashFor(entity)));

        public static WorldHarness WorldWithPostWriteFailure(
            IOwnerThreadTokenProvider? tokens = null,
            bool throwOnWrite = false)
        {
            var worldId = new WorldId(1530);
            var budget = new EcsBudget(4, 32, 32, 4096);
            var request = new EcsWorldCreateRequest(worldId, budget);
            var storage = new InstrumentedStorageAdapter(worldId, budget.MaxEntities, budget.MaxSnapshotBytes)
            {
                ThrowAfterWrite = throwOnWrite
            };
            var sink = new RecordingFailureSink();
            var world = new EcsWorld(
                in request,
                storage,
                new EntitySlotTable(budget.MaxEntities),
                tokens ?? ManagedOwnerThreadTokenProvider.Instance,
                sink);
            Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
            ComponentTypeRegistrationResult component = EcsTestRegistration.Register(
                world,
                new ComponentTypeDefinition(
                    PositionId,
                    "Position",
                    new[] { new ComponentFieldDefinition(PositionField, 4) }));
            Assert.True(component.Registered);
            EntityTypeRegistrationResult entityType = world.RegisterEntityType(
                new EntityTypeDefinition("Player", new[] { component.Handle }));
            Assert.True(entityType.Registered);
            Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
            Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);
            EntityCreateResult created = world.CreateEntityForCommit(
                world.Context,
                new EntityCreateRequest(
                    entityType.Handle,
                    new ComponentInitBatch(new[]
                    {
                        new ComponentInitValue(PositionId, PositionField, BeforeBytes)
                    })));
            Assert.True(created.Created);
            return new WorldHarness(
                world,
                storage,
                sink,
                created.Entity,
                new ThrowingChangeSetAppend());
        }
    }

    private sealed class WorldHarness : IDisposable
    {
        public WorldHarness(
            EcsWorld world,
            InstrumentedStorageAdapter storage,
            RecordingFailureSink sink,
            LocalEntityId entity,
            IEcsChangeSetAppend changeSet)
        {
            World = world;
            Storage = storage;
            Sink = sink;
            Entity = entity;
            ChangeSet = changeSet;
        }

        public EcsWorld World { get; }
        public InstrumentedStorageAdapter Storage { get; }
        public RecordingFailureSink Sink { get; }
        public LocalEntityId Entity { get; }
        public IEcsChangeSetAppend ChangeSet { get; }
        public int StorageUndoCalls => Storage.UndoCalls;

        public byte[] StorageBytes(in EcsFieldWrite write) =>
            Storage.ReadBytes(write.Entity, write.ComponentType, write.Field);

        public void Dispose() => World.ForceCleanup();
    }

    private sealed class ScriptedOwnerThreadTokenProvider : IOwnerThreadTokenProvider
    {
        public ScriptedOwnerThreadTokenProvider(int token) => CurrentToken = token;

        public int CurrentToken { get; set; }
    }

    private sealed class RecordingFailureSink : IEcsDurableFailureSink
    {
        public List<EcsFaultEvidence> Published { get; } = new();

        public void Publish(in EcsFaultEvidence evidence) => Published.Add(evidence);
    }

    private sealed class ThrowingChangeSetAppend : IEcsChangeSetAppend
    {
        public StorageOperationResult TryAppend(in ChangeEntry entry) =>
            throw new InvalidOperationException("changeset append failed after field write");
    }

    private sealed class InstrumentedStorageAdapter : IWorldStorageAdapter
    {
        private readonly ReferenceWorldStorageAdapter _inner;

        public InstrumentedStorageAdapter(WorldId worldId, int maxEntities, int maxSnapshotBytes)
        {
            _inner = new ReferenceWorldStorageAdapter(worldId, maxEntities, maxSnapshotBytes);
        }

        public int WriteCalls { get; private set; }

        public int UndoCalls { get; private set; }

        public bool ThrowAfterWrite { get; init; }

        public byte[] ReadBytes(LocalEntityId entity, ComponentTypeId componentType, ComponentFieldId field)
        {
            var destination = new byte[4];
            StorageOperationResult result = _inner.ReadField(entity, componentType, field, destination, out int written);
            if (!result.IsSuccess || written != 4)
                throw new InvalidOperationException("storage bytes are unavailable.");
            return destination;
        }

        public void UndoField(
            LocalEntityId entity,
            ComponentTypeId componentType,
            ComponentFieldId field,
            ReadOnlySpan<byte> canonicalValue)
        {
            UndoCalls++;
            _inner.WriteExistingField(entity, componentType, field, canonicalValue);
        }

        public StorageOperationResult Register(ComponentTypeDefinition definition) => _inner.Register(definition);

        public StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components) =>
            _inner.Create(entity, in components);

        public StorageOperationResult Destroy(LocalEntityId entity) => _inner.Destroy(entity);

        public StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle) =>
            _inner.CompileQuery(in spec, out handle);

        public StorageOperationResult EnumerateOrdered(
            StorageQueryHandle handle,
            Span<LocalEntityId> destination,
            out int written) =>
            _inner.EnumerateOrdered(handle, destination, out written);

        public StorageOperationResult ReadField(
            LocalEntityId entity,
            ComponentTypeId componentType,
            ComponentFieldId field,
            Span<byte> destination,
            out int written) =>
            _inner.ReadField(entity, componentType, field, destination, out written);

        public StorageOperationResult WriteExistingField(
            LocalEntityId entity,
            ComponentTypeId componentType,
            ComponentFieldId field,
            ReadOnlySpan<byte> canonicalValue)
        {
            WriteCalls++;
            StorageOperationResult result = _inner.WriteExistingField(entity, componentType, field, canonicalValue);
            if (ThrowAfterWrite)
                throw new InvalidOperationException("adapter exploded after field write");
            return result;
        }

        public StorageOperationResult CaptureReadSnapshot(
            in StorageSnapshotContext context,
            out StorageReadSnapshotHandle handle) =>
            _inner.CaptureReadSnapshot(in context, out handle);

        public StorageOperationResult EnumerateSnapshotOrdered(
            StorageReadSnapshotHandle handle,
            Span<LocalEntityId> destination,
            out int written) =>
            _inner.EnumerateSnapshotOrdered(handle, destination, out written);

        public StorageOperationResult ReadSnapshotField(
            StorageReadSnapshotHandle handle,
            LocalEntityId entity,
            ComponentTypeId componentType,
            ComponentFieldId field,
            Span<byte> destination,
            out int written) =>
            _inner.ReadSnapshotField(handle, entity, componentType, field, destination, out written);

        public StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle) =>
            _inner.ReleaseReadSnapshot(handle);

        public StorageOperationResult ValidateIntegrity() => _inner.ValidateIntegrity();

        public void Dispose() => _inner.Dispose();
    }
}
