using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumio.GameRuntime.Ecs;

public sealed class EntityTypeDefinition : IEquatable<EntityTypeDefinition>
{
    private readonly ComponentTypeId[] _componentTypes;

    public EntityTypeDefinition(string name)
        : this(name, Array.Empty<ComponentTypeId>(), EntityMode.CrossServer)
    {
    }

    public EntityTypeDefinition(
        string name,
        IEnumerable<ComponentTypeId> componentTypes,
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

        ComponentTypeId[] supplied = componentTypes.ToArray();
        if (supplied.Length != supplied.Distinct().Count())
            throw new ArgumentException("Entity component type ids must be unique.", nameof(componentTypes));
        if (supplied.Any(static value => value.IsDefault))
            throw new ArgumentException("Entity component type ids must be non-zero.", nameof(componentTypes));

        _componentTypes = supplied.OrderBy(static value => value.Value).ToArray();
        Name = name;
        DefaultMode = defaultMode;
    }

    public string Name { get; }

    public EntityMode DefaultMode { get; }

    public ReadOnlyMemory<ComponentTypeId> ComponentTypes => _componentTypes;

    public bool HasComponent(ComponentTypeId componentType) =>
        Array.BinarySearch(_componentTypes, componentType) >= 0;

    public bool Equals(EntityTypeDefinition? other)
    {
        if (ReferenceEquals(this, other)) return true;
        return other is not null && StringComparer.Ordinal.Equals(Name, other.Name) &&
               DefaultMode == other.DefaultMode && _componentTypes.AsSpan().SequenceEqual(other._componentTypes);
    }

    public override bool Equals(object? obj) => Equals(obj as EntityTypeDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(DefaultMode);
        for (int i = 0; i < _componentTypes.Length; i++) hash.Add(_componentTypes[i]);
        return hash.ToHashCode();
    }

    public override string ToString() => Name;
}

public readonly struct ComponentFieldDefinition : IEquatable<ComponentFieldDefinition>
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

public sealed class ComponentTypeDefinition : IEquatable<ComponentTypeDefinition>
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

public sealed class GeneratedComponentSchemaView
{
    private readonly ComponentTypeDefinition[] _components;
    private readonly IReadOnlyList<ComponentTypeDefinition> _componentView;

    public GeneratedComponentSchemaView(SchemaEpoch schemaEpoch, IEnumerable<ComponentTypeDefinition> components)
    {
        if (schemaEpoch.Value == 0U) throw new ArgumentOutOfRangeException(nameof(schemaEpoch));
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(components);
#else
        if (components is null) throw new ArgumentNullException(nameof(components));
#endif
        _components = components.ToArray();
        if (_components.Any(static component => component is null))
            throw new ArgumentException("Component schema entries cannot be null.", nameof(components));
        if (_components.Length != _components.Select(static component => component.Id).Distinct().Count())
            throw new ArgumentException("Component type ids must be unique.", nameof(components));
        SchemaEpoch = schemaEpoch;
        _componentView = Array.AsReadOnly(_components);
    }

    public SchemaEpoch SchemaEpoch { get; }

    public IReadOnlyList<ComponentTypeDefinition> Components => _componentView;
}

/// <summary>World-local registry for validated component schemas.</summary>
public sealed class ComponentTypeRegistry
{
    private readonly Dictionary<ComponentTypeId, ComponentTypeDefinition> _definitions = new();

    public int Count => _definitions.Count;

    public StorageOperationResult Register(ComponentTypeDefinition definition)
    {
        if (definition is null)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        if (_definitions.ContainsKey(definition.Id))
            return StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration);
        _definitions.Add(definition.Id, definition);
        return StorageOperationResult.Accepted();
    }

    internal bool CanRegister(ComponentTypeDefinition definition) =>
        definition is not null && !_definitions.ContainsKey(definition.Id);

    internal bool CanRegister(GeneratedComponentSchemaView schema)
    {
        if (schema is null) return false;
        var seen = new HashSet<ComponentTypeId>();
        for (int i = 0; i < schema.Components.Count; i++)
        {
            ComponentTypeDefinition definition = schema.Components[i];
            if (definition is null || !seen.Add(definition.Id) || _definitions.ContainsKey(definition.Id)) return false;
        }
        return true;
    }

    public StorageOperationResult Register(GeneratedComponentSchemaView schema)
    {
        if (schema is null)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

        var pending = new List<ComponentTypeDefinition>(schema.Components.Count);
        var seen = new HashSet<ComponentTypeId>();
        for (int i = 0; i < schema.Components.Count; i++)
        {
            ComponentTypeDefinition definition = schema.Components[i];
            if (!seen.Add(definition.Id) || _definitions.ContainsKey(definition.Id))
                return StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration);
            pending.Add(definition);
        }

        for (int i = 0; i < pending.Count; i++) _definitions.Add(pending[i].Id, pending[i]);
        return StorageOperationResult.Accepted();
    }

    public bool TryGet(ComponentTypeId id, out ComponentTypeDefinition definition) =>
        _definitions.TryGetValue(id, out definition!);

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
