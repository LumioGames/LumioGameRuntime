using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

internal sealed class EntityRecord
{
    internal EntityRecord(NetEntityId id, Type entityType, Component[] components)
    {
        Id = id;
        EntityType = entityType;
        Components = components;
        _byType = new Dictionary<Type, Component>();
        _byName = new Dictionary<string, Component>(StringComparer.Ordinal);
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            _byType[component.GetType()] = component;
            _byName[component.GetType().Name] = component;
        }
    }

    internal NetEntityId Id;
    internal Type EntityType;
    internal Component[] Components;
    private readonly Dictionary<Type, Component> _byType;
    private readonly Dictionary<string, Component> _byName;
    internal bool Appeared;
    internal bool Hydrated;
    internal ulong Revision = 1;
    internal bool AwakeCalled;
    internal bool PostAttributeCalled;
    internal bool Started;
    internal ulong CreatedTick;

    internal T Get<T>() where T : Component
    {
        if (_byType.TryGetValue(typeof(T), out Component? exact)) return (T)exact;
        for (int i = 0; i < Components.Length; i++)
            if (Components[i] is T match) return match;
        throw new InvalidOperationException("Component " + typeof(T).Name + " is not on entity " + Id.ToHex());
    }

    internal Component? Find(string componentName)
    {
        return _byName.TryGetValue(componentName, out Component? component) ? component : null;
    }

    internal System.Collections.Generic.IEnumerable<T> OfType<T>() where T : Component
    {
        for (int i = 0; i < Components.Length; i++)
            if (Components[i] is T match) yield return match;
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

    public World World { get; }
    public NetEntityId Id { get; }
    public T Get<T>() where T : Component => World.Get<T>(Id);
}

/// <summary>Deferred create order returned by <see cref="CommandBuffer.Create{T}"/>.</summary>
public sealed class EntityOrder
{
    internal EntityOrder(World world, Type entityType)
    {
        World = world;
        EntityType = entityType;
        Components = world.RentComponents(entityType);
        for (int i = 0; i < Components.Length; i++) Components[i].WorldInternal = world;
    }

    internal World World { get; }
    internal Type EntityType { get; }
    internal Component[] Components { get; }
    public NetEntityId AssignedId { get; internal set; }
    internal bool Issued;

    public T Get<T>() where T : Component
    {
        for (int i = 0; i < Components.Length; i++)
            if (Components[i] is T match) return match;
        throw new InvalidOperationException("Component " + typeof(T).Name + " is not on the create order.");
    }

    public Component? NamedComponent(string typeName)
    {
        for (int i = 0; i < Components.Length; i++)
            if (string.Equals(Components[i].GetType().Name, typeName, StringComparison.Ordinal)) return Components[i];
        return null;
    }
}

/// <summary>Structural command buffer. Creates are issued at commit.</summary>
public sealed class CommandBuffer
{
    private readonly World _world;
    internal CommandBuffer(World world) => _world = world;

    public EntityOrder Create<T>() where T : class => CreateFor(typeof(T));

    public EntityOrder CreateFor(Type type)
    {
        if (type is null) throw new ArgumentNullException(nameof(type));
        if (!_world.Registry.TryResolveEntityType(type.Name, out Type resolved)) resolved = type;
        var order = new EntityOrder(_world, resolved);
        _world.PendingCreates.Add(order);
        return order;
    }
}
