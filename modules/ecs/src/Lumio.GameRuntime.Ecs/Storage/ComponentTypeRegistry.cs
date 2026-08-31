using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumio.GameRuntime.Ecs;

internal readonly record struct ComponentTypeHandle(
    WorldId WorldId,
    uint Value,
    EcsWorld.EcsWorldContext? Origin = null) : IComparable<ComponentTypeHandle>
{
    public bool IsDefault => WorldId.IsDefault || Value == 0U;

    public int CompareTo(ComponentTypeHandle other)
    {
        int world = WorldId.CompareTo(other.WorldId);
        return world == 0 ? Value.CompareTo(other.Value) : world;
    }
}

internal readonly record struct EntityTypeHandle(
    WorldId WorldId,
    uint Value,
    EcsWorld.EcsWorldContext? Origin = null)
{
    public bool IsDefault => WorldId.IsDefault || Value == 0U;
}

internal readonly record struct ComponentTypeRegistrationResult(
    bool Registered,
    ComponentTypeHandle Handle,
    StorageOperationResult Result)
{
    public ErrorIdentity? Error => Result.Error;
}

internal readonly record struct EntityTypeRegistrationResult(
    bool Registered,
    EntityTypeHandle Handle,
    StorageOperationResult Result)
{
    public ErrorIdentity? Error => Result.Error;
}

internal sealed class EntityTypeDefinition : IEquatable<EntityTypeDefinition>
{
    private readonly ComponentTypeHandle[] _declaredComponentTypes;
    private readonly ComponentTypeHandle[] _canonicalComponentTypes;

    public EntityTypeDefinition(string name)
        : this(name, Array.Empty<ComponentTypeHandle>(), EntityMode.CrossServer)
    {
    }

    public EntityTypeDefinition(
        string name,
        IEnumerable<ComponentTypeHandle> componentTypes,
        EntityMode defaultMode = EntityMode.CrossServer)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Entity type name is required.", nameof(name));
        if (defaultMode is not EntityMode.CrossServer and not EntityMode.Local)
            throw new ArgumentOutOfRangeException(nameof(defaultMode));
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(componentTypes);
#else
        if (componentTypes is null) throw new ArgumentNullException(nameof(componentTypes));
#endif

        ComponentTypeHandle[] supplied = componentTypes.ToArray();
        if (supplied.Length != supplied.Distinct().Count())
            throw new ArgumentException("Entity component handles must be unique.", nameof(componentTypes));
        if (supplied.Any(static value => value.IsDefault))
            throw new ArgumentException("Entity component handles must be non-default.", nameof(componentTypes));

        _declaredComponentTypes = supplied;
        _canonicalComponentTypes = supplied.OrderBy(static value => value).ToArray();
        Name = name;
        DefaultMode = defaultMode;
    }

    public string Name { get; }

    public EntityMode DefaultMode { get; }

    public ReadOnlyMemory<ComponentTypeHandle> ComponentTypes => _declaredComponentTypes;

    public ReadOnlyMemory<ComponentTypeHandle> CanonicalComponentTypes => _canonicalComponentTypes;

    public bool HasComponent(ComponentTypeHandle componentType)
    {
        for (int index = 0; index < _canonicalComponentTypes.Length; index++)
        {
            if (_canonicalComponentTypes[index].Equals(componentType)) return true;
        }

        return false;
    }

    public bool Equals(EntityTypeDefinition? other)
    {
        if (ReferenceEquals(this, other)) return true;
        return other is not null && StringComparer.Ordinal.Equals(Name, other.Name) &&
               DefaultMode == other.DefaultMode &&
               _canonicalComponentTypes.AsSpan().SequenceEqual(other._canonicalComponentTypes);
    }

    public override bool Equals(object? obj) => Equals(obj as EntityTypeDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(DefaultMode);
        for (int i = 0; i < _canonicalComponentTypes.Length; i++) hash.Add(_canonicalComponentTypes[i]);
        return hash.ToHashCode();
    }

    public override string ToString() => Name;
}

internal readonly struct ComponentFieldDefinition : IEquatable<ComponentFieldDefinition>
{
    public ComponentFieldDefinition(ComponentFieldId id, int sizeBytes)
    {
        if (id.IsDefault) throw new ArgumentOutOfRangeException(nameof(id));
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
#else
        if (sizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
#endif
        Id = id;
        SizeBytes = sizeBytes;
    }

    public ComponentFieldId Id { get; }

    public int SizeBytes { get; }

    public static bool operator ==(ComponentFieldDefinition left, ComponentFieldDefinition right) => left.Equals(right);

    public static bool operator !=(ComponentFieldDefinition left, ComponentFieldDefinition right) => !left.Equals(right);

    public bool Equals(ComponentFieldDefinition other) => Id == other.Id && SizeBytes == other.SizeBytes;

    public override bool Equals(object? obj) => obj is ComponentFieldDefinition other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, SizeBytes);
}

internal sealed class ComponentTypeDefinition : IEquatable<ComponentTypeDefinition>
{
    private readonly ComponentFieldDefinition[] _fields;

    public ComponentTypeDefinition(
        ComponentTypeId id,
        string name,
        IEnumerable<ComponentFieldDefinition> fields)
    {
        if (id.IsDefault) throw new ArgumentOutOfRangeException(nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Component name is required.", nameof(name));
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(fields);
#else
        if (fields is null) throw new ArgumentNullException(nameof(fields));
#endif

        _fields = fields.OrderBy(static field => field.Id.Value).ToArray();
        if (_fields.Length != _fields.Select(static field => field.Id).Distinct().Count())
            throw new ArgumentException("Component field ids must be unique.", nameof(fields));
        Id = id;
        Name = name;
    }

    public ComponentTypeId Id { get; }

    public string Name { get; }

    public ReadOnlyMemory<ComponentFieldDefinition> Fields => _fields;

    public bool TryGetField(ComponentFieldId field, out ComponentFieldDefinition definition)
    {
        for (int index = 0; index < _fields.Length; index++)
        {
            if (_fields[index].Id == field)
            {
                definition = _fields[index];
                return true;
            }
        }

        definition = default;
        return false;
    }

    public bool Equals(ComponentTypeDefinition? other)
    {
        if (ReferenceEquals(this, other)) return true;
        return other is not null && Id == other.Id && StringComparer.Ordinal.Equals(Name, other.Name) &&
               _fields.AsSpan().SequenceEqual(other._fields);
    }

    public override bool Equals(object? obj) => Equals(obj as ComponentTypeDefinition);

    public override int GetHashCode() => HashCode.Combine(Id, Name);
}

/// <summary>World-local registry for validated component schemas.</summary>
internal sealed class ComponentTypeRegistry
{
    private readonly Dictionary<ComponentTypeId, ComponentTypeDefinition> _definitions = new();
    private readonly Dictionary<ComponentTypeHandle, ComponentTypeDefinition> _definitionsByHandle = new();
    private readonly WorldId _worldId;
    private readonly EcsWorld.EcsWorldContext? _origin;
    private uint _nextHandle;

    public ComponentTypeRegistry(WorldId worldId, EcsWorld.EcsWorldContext? origin = null)
    {
        if (worldId.IsDefault) throw new ArgumentOutOfRangeException(nameof(worldId));
        _worldId = worldId;
        _origin = origin;
    }

    public int Count => _definitions.Count;

    public StorageOperationResult Register(ComponentTypeDefinition definition, out ComponentTypeHandle handle)
    {
        handle = default;
        if (definition is null)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        if (_definitions.ContainsKey(definition.Id))
            return StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration);
        if (_nextHandle == uint.MaxValue)
            return StorageOperationResult.Fatal(EcsErrorCodes.InvalidState);
        handle = new ComponentTypeHandle(_worldId, ++_nextHandle, _origin);
        _definitions.Add(definition.Id, definition);
        _definitionsByHandle.Add(handle, definition);
        return StorageOperationResult.Accepted();
    }

    internal bool CanRegister(ComponentTypeDefinition definition) =>
        definition is not null && !_definitions.ContainsKey(definition.Id);

    public bool TryGet(ComponentTypeId id, out ComponentTypeDefinition definition) =>
        _definitions.TryGetValue(id, out definition!);

    public bool TryGet(ComponentTypeHandle handle, out ComponentTypeDefinition definition)
    {
        if (handle.WorldId == _worldId && _definitionsByHandle.TryGetValue(handle, out definition!))
            return true;
        definition = null!;
        return false;
    }

    public bool Contains(ComponentTypeId id) => _definitions.ContainsKey(id);

    internal bool ContainsField(ComponentFieldId id)
    {
        foreach (ComponentTypeDefinition definition in _definitions.Values)
        {
            if (definition.TryGetField(id, out _)) return true;
        }

        return false;
    }
}
