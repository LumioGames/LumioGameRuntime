using System;
using System.Collections.Generic;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class PreparedBoundaryTests
{
    [Fact]
    public void NullValidationContextRejectsUnknownComponentType()
    {
        CommandPreflightResult result = new CommandPreflightValidator().TryPrepare(UnknownComponentBatch(11UL));
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Null(result.Delta);
    }

    [Fact]
    public void UnknownComponentTypeIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady();
        AssertRejectedWithoutMutation(harness, UnknownComponentBatch(12UL), harness.ActiveEntityCount);
    }

    [Fact]
    public void UnknownFieldIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady(seedEntity: true);
        var buffer = new ProcessorCommandBuffer(13UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Write(harness.SeededEntityId, CommandEcsHarness.ComponentName, "missing-field", CommandEcsHarness.FieldBytes).IsAccepted);
        AssertRejectedWithoutMutation(harness, Merge(13UL, buffer), harness.ActiveEntityCount);
    }

    [Fact]
    public void StaleEntityIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady();
        var buffer = new ProcessorCommandBuffer(14UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Destroy("9:9").IsAccepted);
        AssertRejectedWithoutMutation(harness, Merge(14UL, buffer), harness.ActiveEntityCount);
    }

    [Fact]
    public void InvalidDeferredTargetIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady();
        var buffer = new ProcessorCommandBuffer(15UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        DeferredEntityToken token = buffer.AllocateDeferredEntity();
        Assert.True(buffer.Writer.Write(token, CommandEcsHarness.ComponentName, CommandEcsHarness.FieldId, CommandEcsHarness.FieldBytes).IsAccepted);
        AssertRejectedWithoutMutation(harness, Merge(15UL, buffer), harness.ActiveEntityCount);
    }

    [Fact]
    public void CreateWriteDestroyConflictIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady(seedEntity: true);
        var first = new ProcessorCommandBuffer(16UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        var second = new ProcessorCommandBuffer(16UL, "processor-b", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(first.Writer.Write(harness.SeededEntityId, CommandEcsHarness.ComponentName, CommandEcsHarness.FieldId, CommandEcsHarness.FieldBytes).IsAccepted);
        Assert.True(second.Writer.Write(harness.SeededEntityId, CommandEcsHarness.ComponentName, CommandEcsHarness.FieldId, CommandEcsHarness.FieldBytes).IsAccepted);
        AssertRejectedWithoutMutation(
            harness,
            new CommandBufferMerger().Merge(16UL, new[] { first.Seal(), second.Seal() }),
            harness.ActiveEntityCount);
    }

    [Fact]
    public void PermissionDenyIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady(seedEntity: true);
        var buffer = new ProcessorCommandBuffer(17UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Write(harness.SeededEntityId, CommandEcsHarness.ComponentName, CommandEcsHarness.FieldId, CommandEcsHarness.FieldBytes).IsAccepted);
        CommandModule module = RunningModule(
            harness,
            new CommandPreflightValidator(new CommandPreflightOptions
            {
                Context = new DenyWriteValidationContext()
            }));
        int activeBefore = harness.ActiveEntityCount;
        CommandPreflightResult result = PrepareThenMaybeApply(module, Merge(17UL, buffer));
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Null(result.Delta);
        Assert.Equal("MessagePermissionDenied", result.Failure?.GeneratedErrorId);
        Assert.Equal(0, harness.Storage.MutationCalls);
        Assert.Equal(activeBefore, harness.ActiveEntityCount);
    }

    [Fact]
    public void MaxBytesBudgetIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady(seedEntity: true);
        var buffer = new ProcessorCommandBuffer(18UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Write(harness.SeededEntityId, CommandEcsHarness.ComponentName, CommandEcsHarness.FieldId, CommandEcsHarness.FieldBytes).IsAccepted);
        CommandModule module = RunningModule(
            harness,
            new CommandPreflightValidator(new CommandPreflightOptions
            {
                MaxBytes = 1UL,
                Context = new EcsWorldCommandValidationContext(harness.World)
            }));
        int activeBefore = harness.ActiveEntityCount;
        CommandPreflightResult result = PrepareThenMaybeApply(module, Merge(18UL, buffer));
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Null(result.Delta);
        Assert.Equal(0, harness.Storage.MutationCalls);
        Assert.Equal(activeBefore, harness.ActiveEntityCount);
    }

    [Fact]
    public void EntitySlotExhaustionIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady(maxEntities: 1, seedEntity: true);
        var buffer = new ProcessorCommandBuffer(19UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Create(CommandEcsHarness.EntityTypeName, out _).IsAccepted);
        AssertRejectedWithoutMutation(harness, Merge(19UL, buffer), harness.ActiveEntityCount);
    }

    [Fact]
    public void ChangeEntryBudgetIsRejectedWithZeroMutations()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady(seedEntity: true);
        var buffer = new ProcessorCommandBuffer(20UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Write(harness.SeededEntityId, CommandEcsHarness.ComponentName, CommandEcsHarness.FieldId, CommandEcsHarness.FieldBytes).IsAccepted);
        CommandModule module = RunningModule(
            harness,
            new CommandPreflightValidator(new CommandPreflightOptions
            {
                AvailableChangeEntries = 0UL,
                Context = new EcsWorldCommandValidationContext(harness.World)
            }));
        int activeBefore = harness.ActiveEntityCount;
        CommandPreflightResult result = PrepareThenMaybeApply(module, Merge(20UL, buffer));
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Null(result.Delta);
        Assert.Equal(0, harness.Storage.MutationCalls);
        Assert.Equal(activeBefore, harness.ActiveEntityCount);
    }

    [Fact]
    public void PreparedGameDeltaCreateIsNotAPublicBypass()
    {
        Assert.DoesNotContain(
            typeof(PreparedGameDelta).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => string.Equals(method.Name, "Create", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidTargetIsRejectedBeforeApply()
    {
        var buffer = new ProcessorCommandBuffer(1UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("missing");
        CommandPreflightResult result = new CommandPreflightValidator(new CommandPreflightOptions
        {
            Context = new MissingEntityContext()
        }).TryPrepare(new CommandBufferMerger().Merge(1UL, new[] { buffer.Seal() }));
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Null(result.Delta);
    }

    [Fact]
    public void ValidPreparedDeltaCanBeAppliedIdempotently()
    {
        var buffer = new ProcessorCommandBuffer(2UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("entity-a");
        MergedCommandBatch merged = new CommandBufferMerger().Merge(2UL, new[] { buffer.Seal() });
        PreparedGameDelta prepared = new CommandPreflightValidator(AllowAllOptions()).Prepare(merged);
        CommandModule module = RunningModule(new AppliedPort());
        CommandApplyReceipt first = module.Apply(prepared);
        CommandApplyReceipt second = module.Apply(prepared);
        Assert.Equal(CommandApplyStatus.Applied, first.Status);
        Assert.Equal(CommandApplyStatus.AlreadyApplied, second.Status);
        Assert.Equal(first.CanonicalDigest.ToArray(), second.CanonicalDigest.ToArray());
        Assert.DoesNotContain(Enum.GetNames<CommandApplyStatus>(), name => name.Contains("Reject", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateWriteDestroyResolvesDeferredTargetInStableApplyOrder()
    {
        var buffer = new ProcessorCommandBuffer(5UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Create("avatar", out DeferredEntityToken token);
        buffer.Writer.Write(token, "avatar", "health", new byte[] { 100 });
        buffer.Writer.Destroy(token);
        PreparedGameDelta prepared = new CommandPreflightValidator(AllowAllOptions()).Prepare(new CommandBufferMerger().Merge(5UL, new[] { buffer.Seal() }));
        var port = new CapturingPort();
        CommandApplyReceipt receipt = RunningModule(port).Apply(prepared);
        Assert.Equal(CommandApplyStatus.Applied, receipt.Status);
        Assert.Equal(3, port.Calls);
        Assert.Equal(3, port.ResolvedTargets.Count);
        Assert.Null(port.ResolvedTargets[0]);
        Assert.Equal("entity-created", port.ResolvedTargets[1]);
        Assert.Equal("entity-created", port.ResolvedTargets[2]);
    }

    private static void AssertRejectedWithoutMutation(CommandEcsHarness harness, MergedCommandBatch batch, int activeBefore)
    {
        CommandModule module = RunningModule(harness);
        CommandPreflightResult result = PrepareThenMaybeApply(module, batch);
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Null(result.Delta);
        Assert.Equal(0, harness.Storage.MutationCalls);
        Assert.Equal(activeBefore, harness.ActiveEntityCount);
    }

    private static CommandPreflightResult PrepareThenMaybeApply(CommandModule module, MergedCommandBatch batch)
    {
        CommandPreflightResult result = module.Prepare(batch);
        if (result.Delta is not null)
            module.Apply(result.Delta);
        return result;
    }

    private static MergedCommandBatch UnknownComponentBatch(ulong tick)
    {
        var buffer = new ProcessorCommandBuffer(tick, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Write("1:1", "unknownType", "1", CommandEcsHarness.FieldBytes).IsAccepted);
        return Merge(tick, buffer);
    }

    private static MergedCommandBatch Merge(ulong tick, ProcessorCommandBuffer buffer) =>
        new CommandBufferMerger().Merge(tick, new[] { buffer.Seal() });

    private static CommandPreflightOptions AllowAllOptions() => new()
    {
        Context = AllowAllCommandValidationContext.Instance
    };

    private sealed class MissingEntityContext : ICommandValidationContext
    {
        public bool IsKnownComponent(string componentType) => true;

        public bool IsKnownField(string componentType, string fieldName) => true;

        public bool EntityExists(string entityId) => false;

        public bool CanWrite(string processorId, Command command) => true;
    }

    private sealed class DenyWriteValidationContext : ICommandValidationContext
    {
        public bool IsKnownComponent(string componentType) => true;

        public bool IsKnownField(string componentType, string fieldName) => true;

        public bool EntityExists(string entityId) => true;

        public bool CanWrite(string processorId, Command command) => false;
    }

    private sealed class CapturingPort : IEcsCommandCommitPort
    {
        public int Calls { get; private set; }
        public List<string?> ResolvedTargets { get; } = new();
        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId)
        {
            Calls++;
            ResolvedTargets.Add(resolvedEntityId);
            return command.Kind == CommandKind.Create ? EcsCommandPortResult.Applied("entity-created") : EcsCommandPortResult.Applied();
        }
    }

    private sealed class AppliedPort : IEcsCommandCommitPort
    {
        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId) => EcsCommandPortResult.Applied();
    }

    private static CommandModule RunningModule(IEcsCommandCommitPort port)
    {
        CommandModule module = CommandModule.Create(
            preflight: new CommandPreflightValidator(AllowAllOptions()),
            executor: new EcsCommandCommitExecutor(port));
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);
        return module;
    }

    private static CommandModule RunningModule(CommandEcsHarness harness, CommandPreflightValidator? preflight = null)
    {
        CommandModule module = CommandModule.Create(preflight: preflight, world: harness.World);
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);
        return module;
    }
}

internal sealed class AllowAllCommandValidationContext : ICommandValidationContext
{
    internal static readonly AllowAllCommandValidationContext Instance = new();

    public bool IsKnownComponent(string componentType) => true;

    public bool IsKnownField(string componentType, string fieldName) => true;

    public bool EntityExists(string entityId) => true;

    public bool CanWrite(string processorId, Command command) => true;
}

internal sealed class CommandEcsHarness : IDisposable
{
    internal const string ComponentName = "avatar";
    internal const string EntityTypeName = "avatar";
    internal const string FieldId = "1";
    internal static readonly byte[] FieldBytes = { 1, 2, 3, 4 };
    internal static readonly ComponentTypeId ComponentType = new(10);
    internal static readonly ComponentFieldId Field = new(1);

    private CommandEcsHarness(
        EcsWorld world,
        InstrumentedCommandStorageAdapter storage,
        LocalEntityId? seededEntity)
    {
        World = world;
        Storage = storage;
        SeededEntity = seededEntity;
    }

    public EcsWorld World { get; }

    public InstrumentedCommandStorageAdapter Storage { get; }

    public LocalEntityId? SeededEntity { get; }

    public string SeededEntityId => SeededEntity?.ToString()
        ?? throw new InvalidOperationException("The harness was not seeded with an entity.");

    public int ActiveEntityCount => World.ActiveEntityCount;

    public static CommandEcsHarness CreateReady(
        int maxEntities = 4,
        bool seedEntity = false,
        int throwAfterSuccessfulMutations = int.MaxValue)
    {
        var worldId = new WorldId(1570);
        var budget = new EcsBudget(maxEntities, 32, 32, 4096);
        var request = new EcsWorldCreateRequest(worldId, budget);
        var storage = new InstrumentedCommandStorageAdapter(worldId, budget.MaxEntities, budget.MaxSnapshotBytes)
        {
            ThrowAfterSuccessfulMutations = throwAfterSuccessfulMutations
        };
        var world = new EcsWorld(in request, storage);
        Assert.Equal(StorageOperationStatus.Accepted, world.BeginRegistration().Status);
        ComponentTypeRegistrationResult component = RegisterComponent(
            world,
            new ComponentTypeDefinition(
                ComponentType,
                ComponentName,
                new[] { new ComponentFieldDefinition(Field, 4) }));
        Assert.True(component.Registered);
        EntityTypeRegistrationResult entityType = world.RegisterEntityType(
            new EntityTypeDefinition(EntityTypeName, new[] { component.Handle }));
        Assert.True(entityType.Registered);
        Assert.Equal(StorageOperationStatus.Accepted, world.MarkReady().Status);
        Assert.Equal(StorageOperationStatus.Accepted, world.Start().Status);

        LocalEntityId? seeded = null;
        if (seedEntity)
        {
            EntityCreateResult created = world.CreateEntityForCommit(
                world.Context,
                new EntityCreateRequest(entityType.Handle));
            Assert.True(created.Created);
            storage.ResetCounts();
            seeded = created.Entity;
        }

        return new CommandEcsHarness(world, storage, seeded);
    }

    public void Dispose() => World.ForceCleanup();

    private static ComponentTypeRegistrationResult RegisterComponent(EcsWorld world, ComponentTypeDefinition definition)
    {
        FieldInfo capabilityField = Array.Find(
            typeof(EcsWorld).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(EcsWorld.ComponentRegistrationCapability)) ??
            throw new InvalidOperationException("World component-registration capability is missing.");
        var capability = (EcsWorld.ComponentRegistrationCapability)(capabilityField.GetValue(world) ??
            throw new InvalidOperationException("World component-registration capability is null."));
        return world.RegisterComponentType(capability, definition);
    }
}

internal sealed class InstrumentedCommandStorageAdapter : IWorldStorageAdapter
{
    private readonly ReferenceWorldStorageAdapter _inner;

    public InstrumentedCommandStorageAdapter(WorldId worldId, int maxEntities, int maxSnapshotBytes)
    {
        _inner = new ReferenceWorldStorageAdapter(worldId, maxEntities, maxSnapshotBytes);
    }

    public int CreateCalls { get; private set; }

    public int WriteCalls { get; private set; }

    public int DestroyCalls { get; private set; }

    public int UndoCalls { get; private set; }

    public int MutationCalls => CreateCalls + WriteCalls + DestroyCalls;

    public int ThrowAfterSuccessfulMutations { get; init; } = int.MaxValue;

    public void ResetCounts()
    {
        CreateCalls = 0;
        WriteCalls = 0;
        DestroyCalls = 0;
        UndoCalls = 0;
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

    public StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components)
    {
        CreateCalls++;
        StorageOperationResult result = _inner.Create(entity, in components);
        ThrowIfBudgetExhausted();
        return result;
    }

    public StorageOperationResult Destroy(LocalEntityId entity)
    {
        DestroyCalls++;
        StorageOperationResult result = _inner.Destroy(entity);
        ThrowIfBudgetExhausted();
        return result;
    }

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
        ThrowIfBudgetExhausted();
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

    private void ThrowIfBudgetExhausted()
    {
        if (MutationCalls >= ThrowAfterSuccessfulMutations)
            throw new InvalidOperationException("storage failed after successful apply");
    }
}
