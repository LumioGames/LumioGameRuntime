using System;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Entity occupancy mode. CS entities occupy a <see cref="NetEntityId"/>; Local entities do not.</summary>
public enum Mode
{
    /// <summary>Cross-server entity: present on both ends, occupies a network identity.</summary>
    CS = 0,

    /// <summary>Local-only entity: no network identity, not replicated, not persisted.</summary>
    Local = 1,
}

/// <summary>Marks a type whose members may be scanned into the generated registry and C-2 table.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class EcsComponentAttribute : Attribute
{
}

/// <summary>Marks a field or property that enters the world snapshot. Unmarked members are not persisted.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PersistAttribute : Attribute
{
}

/// <summary>Declares an entity type as an abstract class. Exactly one type may set <see cref="World"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class EntityTypeAttribute : Attribute
{
    /// <summary>Declares a CS or Local entity type.</summary>
    public EntityTypeAttribute(Mode mode)
    {
        Mode = mode;
    }

    /// <summary>Occupancy mode.</summary>
    public Mode Mode { get; }

    /// <summary>When true, this is the unique world singleton type.</summary>
    public bool World { get; set; }
}

/// <summary>Attaches a component type to an entity type. Inherited through C# inheritance.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class HasAttribute : Attribute
{
    /// <summary>Declares that the entity type includes <paramref name="componentType"/>.</summary>
    public HasAttribute(Type componentType)
    {
        ComponentType = componentType ?? throw new ArgumentNullException(nameof(componentType));
    }

    /// <summary>Component CLR type.</summary>
    public Type ComponentType { get; }
}

/// <summary>Declares a logical child entity spawned with the parent.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class ChildAttribute : Attribute
{
    /// <summary>Declares a named child entity type.</summary>
    public ChildAttribute(string name, Type entityType)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        EntityType = entityType ?? throw new ArgumentNullException(nameof(entityType));
    }

    /// <summary>Child slot name.</summary>
    public string Name { get; }

    /// <summary>Child entity type.</summary>
    public Type EntityType { get; }
}

/// <summary>Client-to-server intent. The method body runs on the server during ApplyInputs.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ServerRpcAttribute : Attribute
{
}

/// <summary>Server-to-client one-shot notification (event). Not stored, not replayed.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ClientRpcAttribute : Attribute
{
    /// <summary>Declares a client RPC broadcast at <paramref name="scope"/>.</summary>
    public ClientRpcAttribute(Scope scope)
    {
        Scope = scope;
    }

    /// <summary>Who receives the event.</summary>
    public Scope Scope { get; }
}
