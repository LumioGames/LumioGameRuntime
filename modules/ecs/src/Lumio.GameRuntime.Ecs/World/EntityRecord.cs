using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

internal enum Presence
{
    Live = 0,
    Tombstoned = 1,
}

internal sealed class EntityRecord
{
    internal EntityRecord(NetEntityId id, Type entityType, Component[] components)
    {
        Id = id;
        EntityType = entityType;
        Components = components;
        Presence = Presence.Live;
        Appeared = false;
        Lifecycle = new List<string>();
    }

    internal List<string> Lifecycle;

    internal NetEntityId Id;
    internal Type EntityType;
    internal Component[] Components;
    internal Presence Presence;
    internal bool Appeared;
    internal bool Hydrated;
    internal ulong Revision = 1;

    internal T Get<T>() where T : Component
    {
        for (int i = 0; i < Components.Length; i++)
        {
            if (Components[i] is T match) return match;
        }

        throw new InvalidOperationException("Component " + typeof(T).Name + " is not on entity " + Id.ToHex());
    }

    internal bool TryGet(Type type, out Component component)
    {
        for (int i = 0; i < Components.Length; i++)
        {
            if (type.IsInstanceOfType(Components[i]))
            {
                component = Components[i];
                return true;
            }
        }

        component = null!;
        return false;
    }

    internal IEnumerable<T> OfType<T>() where T : Component
    {
        for (int i = 0; i < Components.Length; i++)
        {
            if (Components[i] is T match) yield return match;
        }
    }
}

/// <summary>Public entity handle. <see cref="World.Self"/> is one of these.</summary>
public sealed class Entity
{
    internal Entity(World world, NetEntityId id)
    {
        World = world;
        Id = id;
    }

    /// <summary>Owning world.</summary>
    public World World { get; }

    /// <summary>Network identity.</summary>
    public NetEntityId Id { get; }

    /// <summary>Reads a component on this entity.</summary>
    public T Get<T>() where T : Component => World.Get<T>(Id);
}

/// <summary>Deferred create order returned by <see cref="CommandBuffer.Create{T}"/>.</summary>
public sealed class EntityOrder
{
    internal EntityOrder(World world, Type entityType)
    {
        World = world;
        EntityType = entityType;
        Components = world.Registry.CreateComponents(entityType);
        for (int i = 0; i < Components.Length; i++)
            Components[i].WorldInternal = world;
    }

    internal World World { get; }
    internal Type EntityType { get; }
    internal Component[] Components { get; }
    internal NetEntityId AssignedId;
    internal bool Issued;

    /// <summary>Reads a component on the not-yet-committed entity so birth values can be set.</summary>
    public T Get<T>() where T : Component
    {
        for (int i = 0; i < Components.Length; i++)
        {
            if (Components[i] is T match) return match;
        }

        throw new InvalidOperationException("Component " + typeof(T).Name + " is not on the create order.");
    }
}

/// <summary>Structural command buffer. Creates are issued at commit.</summary>
public sealed class CommandBuffer
{
    private readonly World _world;

    internal CommandBuffer(World world) => _world = world;

    /// <summary>Queues a template copy of <typeparamref name="T"/>. The identity is issued at commit.</summary>
    public EntityOrder Create<T>() where T : class => CreateFor(typeof(T));

    /// <summary>Queues a template copy of <paramref name="type"/>.</summary>
    public EntityOrder CreateFor(Type type)
    {
        if (type is null) throw new ArgumentNullException(nameof(type));
        if (!_world.Registry.TryResolveEntityType(type.Name, out Type resolved))
            resolved = type;
        var order = new EntityOrder(_world, resolved);
        _world.PendingCreates.Add(order);
        return order;
    }
}
