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
public sealed class ReferenceWorldStorageAdapter : IWorldStorageAdapter
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

    private readonly int _capacity;
    private readonly ComponentTypeRegistry _registry = new();
    private readonly Dictionary<LocalEntityId, EntityData> _entities = new();
    private readonly List<LocalEntityId> _creationOrder = new();
    private readonly Dictionary<uint, QueryData> _queries = new();
    private readonly HashSet<ulong> _snapshots = new();
    private long _creationSequence;
    private uint _nextQueryHandle;
    private ulong _nextSnapshotHandle;
    private bool _disposed;

    public ReferenceWorldStorageAdapter(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int EntityCount => _entities.Count;

    public ErrorIdentity? LastError { get; private set; }

    public StorageOperationResult Register(in GeneratedComponentSchemaView schema)
    {
        if (!EnsureOpen()) return Closed();
        StorageOperationResult result = _registry.Register(schema);
        return Remember(result);
    }

    public StorageOperationResult Create(LocalEntityId entity, in ComponentInitBatch components)
    {
        if (!EnsureOpen()) return Closed();
        if (entity.IsDefault) return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));
        if (_entities.ContainsKey(entity)) return Remember(StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration));
        if (_entities.Count >= _capacity) return Remember(StorageOperationResult.Rejected(EcsErrorCodes.CapacityExceeded));

        var data = new EntityData { CreationSequence = ++_creationSequence };
        ReadOnlySpan<ComponentInitValue> values = components.Values.Span;
        for (int i = 0; i < values.Length; i++)
        {
            ComponentInitValue value = values[i];
            if (!TryGetField(value.ComponentType, value.Field, out ComponentFieldDefinition field))
                return Remember(StorageOperationResult.Rejected(LastError?.Code ?? EcsErrorCodes.UnknownField));
            if (value.CanonicalValue.Length != field.SizeBytes)
                return Remember(StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument));

            if (!data.Components.TryGetValue(value.ComponentType, out Dictionary<ComponentFieldId, byte[]>? component))
            {
                component = new Dictionary<ComponentFieldId, byte[]>();
                data.Components.Add(value.ComponentType, component);
            }
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

    public StorageOperationResult CaptureReadSnapshot(out StorageReadSnapshotHandle handle)
    {
        handle = default;
        if (!EnsureOpen()) return Closed();
        ulong value = checked(++_nextSnapshotHandle);
        _snapshots.Add(value);
        handle = new StorageReadSnapshotHandle(value);
        return Remember(StorageOperationResult.Accepted());
    }

    public StorageOperationResult ReleaseReadSnapshot(StorageReadSnapshotHandle handle)
    {
        if (!EnsureOpen()) return Closed();
        return Remember(_snapshots.Remove(handle.Value)
            ? StorageOperationResult.Accepted()
            : StorageOperationResult.Rejected(EcsErrorCodes.SnapshotReleased));
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
