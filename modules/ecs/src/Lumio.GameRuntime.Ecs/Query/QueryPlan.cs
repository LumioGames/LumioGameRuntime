using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

internal interface IEcsQueryViewHost
{
    WorldId WorldId { get; }

    TickId TickId { get; }

    uint Epoch { get; }

    EcsBudget Budget { get; }

    bool IsKnownComponent(ComponentTypeId componentType);

    bool IsKnownField(ComponentTypeId componentType, ComponentFieldId field);

    StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle);

    StorageOperationResult EnumerateOrdered(
        StorageQueryHandle handle,
        Span<LocalEntityId> destination,
        out int written);

    StorageOperationResult ReadField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written);

    StorageOperationResult WriteExistingField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue);
}

internal sealed class QueryPlan
{
    internal QueryPlan(WorldId worldId, in QuerySpec spec, StorageQueryHandle handle)
    {
        WorldId = worldId;
        Spec = spec;
        Handle = handle;
    }

    public WorldId WorldId { get; }

    public QuerySpec Spec { get; }

    public StorageQueryHandle Handle { get; }

    public static StorageOperationResult TryCompile(
        IWorldStorageAdapter storage,
        WorldId worldId,
        in QuerySpec spec,
        out QueryPlan? plan)
    {
        plan = null;
        if (storage is null) return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        StorageOperationResult compiled = storage.CompileQuery(in spec, out StorageQueryHandle handle);
        if (!compiled.IsSuccess) return compiled;
        plan = new QueryPlan(worldId, in spec, handle);
        return compiled;
    }
}

internal sealed class EcsQuerySession : IEcsQueryViewHost, IDisposable
{
    private readonly IWorldStorageAdapter _storage;
    private readonly List<ComponentTypeDefinition> _definitions = new();
    private readonly bool _ownsStorage;
    private bool _disposed;

    public EcsQuerySession(
        WorldId worldId,
        IWorldStorageAdapter storage,
        EcsBudget budget,
        TickId tickId,
        bool ownsStorage = true)
    {
        if (worldId.IsDefault) throw new ArgumentOutOfRangeException(nameof(worldId));
        if (!budget.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(storage);
#else
        if (storage is null) throw new ArgumentNullException(nameof(storage));
#endif
        WorldId = worldId;
        _storage = storage;
        Budget = budget;
        TickId = tickId;
        Epoch = 1;
        _ownsStorage = ownsStorage;
    }

    public WorldId WorldId { get; }

    public TickId TickId { get; private set; }

    public uint Epoch { get; private set; }

    public EcsBudget Budget { get; }

    public void AdvanceEpoch() => Epoch++;

    public void AdvanceTick()
    {
        TickId = new TickId(TickId.Value + 1UL);
        Epoch++;
    }

    public StorageOperationResult Register(ComponentTypeDefinition definition)
    {
        StorageOperationResult result = _storage.Register(definition);
        if (result.IsSuccess) _definitions.Add(definition);
        return result;
    }

    public EcsReadView OpenRead(in QuerySpec spec) => new(this, in spec);

    public EcsWriteView OpenWrite(in QuerySpec spec, ChangeSetBuilder builder) => new(this, in spec, builder);

    public StorageOperationResult TryQuery(in QuerySpec spec, in QueryBudget budget, out QueryBatch? batch)
    {
        batch = null;
        if (!budget.IsValid) return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        int limit = budget.MaxEntities;
        if (limit > Budget.MaxQueryResults) limit = Budget.MaxQueryResults;
        StorageOperationResult compiled = QueryPlan.TryCompile(_storage, WorldId, in spec, out QueryPlan? plan);
        if (!compiled.IsSuccess || plan is null) return compiled;
        var destination = new LocalEntityId[limit];
        StorageOperationResult enumerated = _storage.EnumerateOrdered(plan.Handle, destination, out int written);
        if (!enumerated.IsSuccess) return enumerated;
        Array.Sort(destination, 0, written);
        var entities = new LocalEntityId[written];
        Array.Copy(destination, entities, written);
        batch = new QueryBatch(WorldId, TickId, Epoch, spec, entities);
        return StorageOperationResult.Accepted();
    }

    public bool IsKnownComponent(ComponentTypeId componentType)
    {
        for (int index = 0; index < _definitions.Count; index++)
        {
            if (_definitions[index].Id == componentType) return true;
        }

        return false;
    }

    public bool IsKnownField(ComponentTypeId componentType, ComponentFieldId field)
    {
        for (int index = 0; index < _definitions.Count; index++)
        {
            if (_definitions[index].Id == componentType)
                return _definitions[index].TryGetField(field, out _);
        }

        return false;
    }

    public StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle) =>
        _storage.CompileQuery(in spec, out handle);

    public StorageOperationResult EnumerateOrdered(
        StorageQueryHandle handle,
        Span<LocalEntityId> destination,
        out int written) =>
        _storage.EnumerateOrdered(handle, destination, out written);

    public StorageOperationResult ReadField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written) =>
        _storage.ReadField(entity, componentType, field, destination, out written);

    public StorageOperationResult WriteExistingField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue) =>
        _storage.WriteExistingField(entity, componentType, field, canonicalValue);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsStorage) _storage.Dispose();
    }
}
