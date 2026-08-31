using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Small adapter-neutral storage used by the Foundation and conformance tests.
/// It intentionally makes no promise about a production ECS layout.
/// </summary>
[SuppressMessage("Design", "CA1512", Justification = "The production target also compiles for netstandard2.1, where the ThrowIf helper is unavailable.")]
internal sealed class ReferenceWorldStorageAdapter : IWorldStorageAdapter
{
    private sealed class EntityData
    {
        public readonly Dictionary<ComponentTypeId, Dictionary<ComponentFieldId, byte[]>> Components = new();
        public long CreationSequence;
    }

    private sealed class QueryData
    {
        public QuerySpec Spec;
    }

    private sealed class SnapshotData
    {
        public StorageSnapshotContext Context;
        public readonly Dictionary<LocalEntityId, EntityData> Entities = new();
        public LocalEntityId[] CreationOrder = Array.Empty<LocalEntityId>();
    }

    private readonly WorldId _worldId;
    private readonly int _capacity;
    private readonly int _maxSnapshotBytes;
    private readonly ComponentTypeRegistry _registry;
    private readonly Dictionary<LocalEntityId, EntityData> _entities = new();
    private readonly List<LocalEntityId> _creationOrder = new();
    private readonly Dictionary<uint, QueryData> _queries = new();
    private readonly Dictionary<ulong, SnapshotData> _snapshots = new();
    private readonly HashSet<StorageReadSnapshotHandle> _releasedSnapshots = new();
    private long _creationSequence;
    private uint _nextQueryHandle;
    private ulong _nextSnapshotHandle;
    private bool _disposed;

    public ReferenceWorldStorageAdapter(
        WorldId worldId,
        int capacity,
        int maxSnapshotBytes = int.MaxValue)
    {
        if (worldId.IsDefault) throw new ArgumentOutOfRangeException(nameof(worldId));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxSnapshotBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes));
        _worldId = worldId;
        _registry = new ComponentTypeRegistry(worldId);
        _capacity = capacity;
        _maxSnapshotBytes = maxSnapshotBytes;
    }

    public int EntityCount => _entities.Count;

    public ErrorIdentity? LastError { get; private set; }

    public StorageOperationResult Register(ComponentTypeDefinition definition)
    {
        if (!EnsureOpen()) return Closed();
        StorageOperationResult result = _registry.Register(definition, out _);
        return Remember(result);
    }

    public StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components)
    {
        if (!EnsureOpen()) return Closed();
        if (entity.IsDefault) return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));
        if (_entities.ContainsKey(entity)) return Remember(StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration));
        if (_entities.Count >= _capacity) return Remember(StorageOperationResult.Rejected(EcsErrorCodes.CapacityExceeded));

        var data = new EntityData { CreationSequence = ++_creationSequence };
        ReadOnlySpan<ComponentTypeId> componentTypes = components.Components.Span;
        for (int i = 0; i < componentTypes.Length; i++)
        {
            ComponentTypeId componentType = componentTypes[i];
            if (!_registry.Contains(componentType))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent));
            if (data.Components.ContainsKey(componentType))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));
            data.Components.Add(componentType, new Dictionary<ComponentFieldId, byte[]>());
        }

        ReadOnlySpan<ComponentInitValue> values = components.Values.Span;
        for (int i = 0; i < values.Length; i++)
        {
            ComponentInitValue value = values[i];
            if (!TryGetField(value.ComponentType, value.Field, out ComponentFieldDefinition field))
                return Remember(StorageOperationResult.Rejected(LastError?.Code ?? EcsErrorCodes.UnknownField));
            if (value.CanonicalValue.Length != field.SizeBytes)
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));

            if (!data.Components.TryGetValue(value.ComponentType, out Dictionary<ComponentFieldId, byte[]>? component))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent));
            if (component.ContainsKey(value.Field))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration));
            component.Add(value.Field, value.CanonicalValue.ToArray());
        }

        _entities.Add(entity, data);
        _creationOrder.Add(entity);
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult Destroy(LocalEntityId entity)
    {
        if (!EnsureOpen()) return Closed();
        if (!_entities.Remove(entity)) return Remember(StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity));
        _creationOrder.Remove(entity);
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult CompileQuery(in QuerySpec spec, out StorageQueryHandle handle)
    {
        handle = default;
        if (!EnsureOpen()) return Closed();
        if (!spec.IsWellFormed) return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));
        ReadOnlySpan<ComponentTypeId> required = spec.Required.Span;
        for (int i = 0; i < required.Length; i++)
        {
            if (!_registry.Contains(required[i]))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent));
        }

        ReadOnlySpan<ComponentTypeId> excluded = spec.Excluded.Span;
        for (int i = 0; i < excluded.Length; i++)
        {
            if (!_registry.Contains(excluded[i]))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent));
        }

        ReadOnlySpan<ComponentFieldId> readSet = spec.ReadSet.Span;
        for (int i = 0; i < readSet.Length; i++)
        {
            if (!_registry.ContainsField(readSet[i]))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownField));
        }

        ReadOnlySpan<ComponentFieldId> writeSet = spec.WriteSet.Span;
        for (int i = 0; i < writeSet.Length; i++)
        {
            if (!_registry.ContainsField(writeSet[i]))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownField));
        }

        uint value = checked(++_nextQueryHandle);
        _queries.Add(value, new QueryData { Spec = CloneQuerySpec(in spec) });
        handle = new StorageQueryHandle(value);
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult EnumerateOrdered(
        StorageQueryHandle handle,
        Span<LocalEntityId> destination,
        out int written)
    {
        written = 0;
        if (!EnsureOpen()) return Closed();
        QuerySpec spec = QuerySpec.Empty;
        if (handle.Value != 0U)
        {
            if (!_queries.TryGetValue(handle.Value, out QueryData? query))
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));
            spec = query.Spec;
        }

        for (int i = 0; i < _creationOrder.Count; i++)
        {
            LocalEntityId entity = _creationOrder[i];
            if (!Matches(entity, spec)) continue;
            written++;
        }
        if (written > destination.Length)
        {
            written = 0;
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded));
        }

        int outputIndex = 0;
        for (int i = 0; i < _creationOrder.Count; i++)
        {
            LocalEntityId entity = _creationOrder[i];
            if (Matches(entity, spec)) destination[outputIndex++] = entity;
        }
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult ReadField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (!EnsureOpen()) return Closed();
        if (!TryGetField(componentType, field, out _))
            return Remember(StorageOperationResult.Rejected(LastError?.Code ?? EcsErrorCodes.UnknownField));
        if (!_entities.TryGetValue(entity, out EntityData? data))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity));
        if (!data.Components.TryGetValue(componentType, out Dictionary<ComponentFieldId, byte[]>? component) ||
            !component.TryGetValue(field, out byte[]? value))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownField));
        if (destination.Length < value.Length)
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded));
        value.AsSpan().CopyTo(destination);
        written = value.Length;
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult WriteExistingField(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue)
    {
        if (!EnsureOpen()) return Closed();
        if (!TryGetField(componentType, field, out ComponentFieldDefinition definition))
            return Remember(StorageOperationResult.Rejected(LastError?.Code ?? EcsErrorCodes.UnknownField));
        if (!_entities.TryGetValue(entity, out EntityData? data))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity));
        if (canonicalValue.Length != definition.SizeBytes)
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));
        if (!data.Components.TryGetValue(componentType, out Dictionary<ComponentFieldId, byte[]>? component) ||
            !component.ContainsKey(field))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownField));
        component[field] = canonicalValue.ToArray();
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult CaptureReadSnapshot(
        in StorageSnapshotContext context,
        out StorageReadSnapshotHandle handle)
    {
        handle = default;
        if (!EnsureOpen()) return Closed();
        if (!context.IsValid)
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));
        if (context.WorldId != _worldId)
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.CrossWorld));
        if (_nextSnapshotHandle == ulong.MaxValue)
            return Remember(StorageOperationResult.Fatal(EcsErrorCodes.InvalidState));

        long snapshotBytes = 0;
        foreach (EntityData entity in _entities.Values)
        {
            foreach (Dictionary<ComponentFieldId, byte[]> component in entity.Components.Values)
            {
                foreach (byte[] value in component.Values)
                {
                    snapshotBytes += value.Length;
                    if (snapshotBytes > _maxSnapshotBytes)
                        return Remember(StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded));
                }
            }
        }

        var snapshot = new SnapshotData
        {
            Context = context,
            CreationOrder = _creationOrder.ToArray()
        };
        foreach (KeyValuePair<LocalEntityId, EntityData> pair in _entities)
            snapshot.Entities.Add(pair.Key, CloneEntityData(pair.Value));

        ulong token = ++_nextSnapshotHandle;
        _snapshots.Add(token, snapshot);
        handle = new StorageReadSnapshotHandle(token, context);
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult EnumerateSnapshotOrdered(
        StorageReadSnapshotHandle handle,
        Span<LocalEntityId> destination,
        out int written)
    {
        written = 0;
        if (!EnsureOpen()) return Closed();
        if (!TryGetSnapshot(handle, out SnapshotData? snapshot))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased));
        if (snapshot.CreationOrder.Length > destination.Length)
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded));
        snapshot.CreationOrder.AsSpan().CopyTo(destination);
        written = snapshot.CreationOrder.Length;
        return Remember(StorageOperationResult.Accepted());
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
        if (!EnsureOpen()) return Closed();
        if (!TryGetSnapshot(handle, out SnapshotData? snapshot))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased));
        if (!snapshot.Entities.TryGetValue(entity, out EntityData? data))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity));
        if (!data.Components.TryGetValue(componentType, out Dictionary<ComponentFieldId, byte[]>? component) ||
            !component.TryGetValue(field, out byte[]? value))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.UnknownField));
        if (destination.Length < value.Length)
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded));
        value.AsSpan().CopyTo(destination);
        written = value.Length;
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle)
    {
        if (!EnsureOpen()) return Closed();
        if (_releasedSnapshots.Contains(handle))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.SnapshotDoubleRelease));
        if (!TryGetSnapshot(handle, out _) || !_snapshots.Remove(handle.Value))
            return Remember(StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased));
        _releasedSnapshots.Add(handle);
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult ValidateIntegrity()
    {
        if (!EnsureOpen()) return Closed();
        if (_entities.Count > _capacity || _creationOrder.Count != _entities.Count)
            return Remember(StorageOperationResult.Fatal(EcsErrorCodes.InvalidState));
        for (int i = 0; i < _creationOrder.Count; i++)
        {
            if (!_entities.ContainsKey(_creationOrder[i]))
                return Remember(StorageOperationResult.Fatal(EcsErrorCodes.InvalidState));
        }
        return Remember(StorageOperationResult.Accepted());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entities.Clear();
        _creationOrder.Clear();
        _queries.Clear();
        _snapshots.Clear();
        _releasedSnapshots.Clear();
    }

    private bool Matches(LocalEntityId entity, in QuerySpec spec)
    {
        if (!_entities.TryGetValue(entity, out EntityData? data)) return false;
        ReadOnlySpan<ComponentTypeId> required = spec.Required.Span;
        for (int i = 0; i < required.Length; i++)
        {
            if (!data.Components.ContainsKey(required[i])) return false;
        }
        ReadOnlySpan<ComponentTypeId> excluded = spec.Excluded.Span;
        for (int i = 0; i < excluded.Length; i++)
        {
            if (data.Components.ContainsKey(excluded[i])) return false;
        }
        return true;
    }

    private static QuerySpec CloneQuerySpec(in QuerySpec spec) => new(
        spec.Required.ToArray(),
        spec.Excluded.ToArray(),
        spec.ReadSet.ToArray(),
        spec.WriteSet.ToArray());

    private static EntityData CloneEntityData(EntityData source)
    {
        var clone = new EntityData { CreationSequence = source.CreationSequence };
        foreach (KeyValuePair<ComponentTypeId, Dictionary<ComponentFieldId, byte[]>> component in source.Components)
        {
            var fields = new Dictionary<ComponentFieldId, byte[]>(component.Value.Count);
            foreach (KeyValuePair<ComponentFieldId, byte[]> field in component.Value)
                fields.Add(field.Key, field.Value.ToArray());
            clone.Components.Add(component.Key, fields);
        }
        return clone;
    }

    private bool TryGetSnapshot(StorageReadSnapshotHandle handle, out SnapshotData snapshot)
    {
        if (!handle.IsDefault &&
            handle.Context.WorldId == _worldId &&
            _snapshots.TryGetValue(handle.Value, out snapshot!) &&
            snapshot.Context == handle.Context)
        {
            return true;
        }
        snapshot = null!;
        return false;
    }

    private bool TryGetField(ComponentTypeId componentType, ComponentFieldId field, out ComponentFieldDefinition definition)
    {
        if (!_registry.TryGet(componentType, out ComponentTypeDefinition? component))
        {
            LastError = new ErrorIdentity(EcsErrorCodes.UnknownComponent);
            definition = default;
            return false;
        }
        if (!component.TryGetField(field, out definition))
        {
            LastError = new ErrorIdentity(EcsErrorCodes.UnknownField);
            return false;
        }
        LastError = null;
        return true;
    }

    private bool EnsureOpen()
    {
        if (!_disposed) return true;
        LastError = new ErrorIdentity(EcsErrorCodes.WorldDisposed);
        return false;
    }

    private StorageOperationResult Closed() => Remember(StorageOperationResult.Rejected(EcsErrorCodes.WorldDisposed));

    private StorageOperationResult Remember(StorageOperationResult result)
    {
        if (result.Error is null) LastError = null;
        else LastError = result.Error;
        return result;
    }
}
