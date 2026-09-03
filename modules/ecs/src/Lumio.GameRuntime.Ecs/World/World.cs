using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

/// <summary>The unique GameWorld held by a <see cref="WorldManager"/>.</summary>
public sealed class World : ISyncHost
{
    internal World(WorldManager manager, EcsRegistry registry, ulong instanceId, bool isServer)
    {
        Manager = manager;
        Registry = registry;
        InstanceId = instanceId;
        IsServer = isServer;
        Commands = new CommandBuffer(this);
        Entities = new Dictionary<NetEntityId, EntityRecord>();
        PendingCreates = new List<EntityOrder>();
        PendingDestroys = new List<NetEntityId>();
        Tombstones = new HashSet<NetEntityId>();
        Dirty = new List<DirtyEntry>();
        PendingRpcs = new List<ClientRpcRecord>();
        PendingCorrections = new List<FieldChange>();
        PendingHooks = new List<HookEntry>();
        AccountIndex = new Dictionary<string, NetEntityId>(StringComparer.Ordinal);
        CreationOrder = new List<NetEntityId>();
    }

    /// <summary>Owning manager.</summary>
    public WorldManager Manager { get; internal set; }

    /// <summary>Generated registry that described this world.</summary>
    public EcsRegistry Registry { get; }

    /// <summary>World instance id. Zero on a client until the welcome message is applied.</summary>
    public ulong InstanceId { get; internal set; }

    /// <summary>True when this world issues identities and is authoritative.</summary>
    public bool IsServer { get; }

    /// <summary>Current logic tick. Incremented at the end of <see cref="WorldManager.Tick"/>.</summary>
    public ulong Tick { get; internal set; }

    /// <summary>Revision incremented on every committed mutation.</summary>
    public ulong Revision { get; internal set; } = 1;

    private Entity? _self;

    /// <summary>This connection's bound entity. Set from the welcome message on the client; set at Admit on the server.</summary>
    public Entity Self => _self ?? throw new InvalidOperationException("World.Self is not bound.");

    internal void BindSelf(Entity entity) => _self = entity;

    /// <summary>Structural command buffer.</summary>
    public CommandBuffer Commands { get; }

    internal Dictionary<NetEntityId, EntityRecord> Entities { get; }
    internal List<EntityOrder> PendingCreates { get; }
    internal List<NetEntityId> PendingDestroys { get; }
    internal HashSet<NetEntityId> Tombstones { get; }
    internal List<DirtyEntry> Dirty { get; }
    internal List<ClientRpcRecord> PendingRpcs { get; }
    internal List<FieldChange> PendingCorrections { get; }
    internal List<HookEntry> PendingHooks { get; }
    internal Dictionary<string, NetEntityId> AccountIndex { get; }
    internal List<NetEntityId> CreationOrder { get; }
    internal ulong NextCounter = 1;
    internal ulong NextMessageId = 1;
    internal ulong NextRoomSequence = 1;
    internal string? PendingSaveSlot;
    internal bool ApplyingRemote;

    bool ISyncHost.IsServer => IsServer;
    bool ISyncHost.IsApplyingRemote => ApplyingRemote;
    WorldManager ISyncHost.Manager => Manager;
    World ISyncHost.World => this;

    /// <summary>Reads a component on <paramref name="id"/>.</summary>
    public T Get<T>(NetEntityId id) where T : Component
    {
        if (!Entities.TryGetValue(id, out EntityRecord? record) || record.Presence != Presence.Live)
            throw new InvalidOperationException("Entity " + id.ToHex() + " is not live.");
        return record.Get<T>();
    }

    /// <summary>Returns a live entity handle.</summary>
    public Entity Get(NetEntityId id)
    {
        if (!Entities.TryGetValue(id, out EntityRecord? record) || record.Presence != Presence.Live)
            throw new InvalidOperationException("Entity " + id.ToHex() + " is not live.");
        return new Entity(this, id);
    }

    /// <summary>True when <paramref name="id"/> is live.</summary>
    public bool IsLive(NetEntityId id) =>
        Entities.TryGetValue(id, out EntityRecord? record) && record.Presence == Presence.Live;

    /// <summary>Iterates live components of type <typeparamref name="T"/> in create order.</summary>
    public IEnumerable<T> Each<T>() where T : Component
    {
        for (int i = 0; i < CreationOrder.Count; i++)
        {
            if (!Entities.TryGetValue(CreationOrder[i], out EntityRecord? record)) continue;
            if (record.Presence != Presence.Live) continue;
            foreach (T component in record.OfType<T>())
                yield return component;
        }
    }

    /// <summary>The unique live component of type <typeparamref name="T"/> (WorldEntity components).</summary>
    public T Single<T>() where T : Component
    {
        T? found = null;
        foreach (T item in Each<T>())
        {
            if (found is not null)
                throw new InvalidOperationException("More than one " + typeof(T).Name + " is live.");
            found = item;
        }

        if (found is null)
            throw new InvalidOperationException("No live " + typeof(T).Name + " exists.");
        return found;
    }

    /// <summary>Lifecycle probe used by appearance-order tests (Awake → PostAttribute → Start).</summary>
    public IReadOnlyList<string> LifecycleOf(NetEntityId id)
    {
        if (!Entities.TryGetValue(id, out EntityRecord? record))
            return Array.Empty<string>();
        return record.Lifecycle;
    }

    /// <summary>Type handle for <paramref name="id"/>. Subtypes return true from <see cref="EntityTypeRef.Is{T}"/>.</summary>
    public EntityTypeRef TypeOf(NetEntityId id)
    {
        if (!Entities.TryGetValue(id, out EntityRecord? record) || record.Presence != Presence.Live)
            throw new InvalidOperationException("Entity " + id.ToHex() + " is not live.");
        return new EntityTypeRef(record.EntityType, Registry);
    }

    internal void RequestSave(string slot) => PendingSaveSlot = slot;

    internal NetEntityId IssueId()
    {
        if (!IsServer)
            throw new InvalidOperationException("Client worlds do not issue NetEntityId.");
        ulong counter = NextCounter++;
        return new NetEntityId(InstanceId, counter);
    }

    internal EntityRecord Attach(NetEntityId id, Type entityType, Component[] components, bool bind)
    {
        var record = new EntityRecord(id, entityType, components);
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            component.WorldInternal = this;
            component.EntityId = id;
            component.Record = record;
            if (bind) Registry.BindComponent(component, this);
        }

        Entities[id] = record;
        CreationOrder.Add(id);
        IndexAccount(record);
        return record;
    }

    internal void IndexAccount(EntityRecord record)
    {
        if (record.TryGet(typeof(object), out _))
        {
            // keep analyzer quiet; real path below
        }

        for (int i = 0; i < record.Components.Length; i++)
        {
            Component component = record.Components[i];
            string? accountId = TryReadAccountId(component);
            if (!string.IsNullOrEmpty(accountId))
                AccountIndex[accountId] = record.Id;
        }
    }

    internal static string? TryReadAccountId(Component component)
    {
        IGeneratedComponent? generated = EcsRegistry.Generated(component);
        if (generated is not null)
        {
            object? value = generated.ReadField("accountId");
            return value as string;
        }

        System.Reflection.FieldInfo? field = component.GetType().GetField("AccountId");
        return field?.GetValue(component) as string;
    }

    internal static bool TryReadConnected(Component component, out bool connected)
    {
        IGeneratedComponent? generated = EcsRegistry.Generated(component);
        if (generated is not null)
        {
            object? value = generated.ReadField("connected");
            if (value is bool flag)
            {
                connected = flag;
                return true;
            }
        }

        System.Reflection.FieldInfo? field = component.GetType().GetField("Connected");
        if (field is not null && field.FieldType == typeof(bool))
        {
            connected = (bool)(field.GetValue(component) ?? false);
            return true;
        }

        connected = false;
        return false;
    }

    void ISyncHost.OnLocalWrite(Component owner, ISyncField field, object? oldValue, object? newValue)
    {
        if (ApplyingRemote) return;
        Dirty.Add(new DirtyEntry(owner.EntityId, field, oldValue, newValue, ChangeReason.Local));
        if (field.Notify == Notify.All)
            PendingHooks.Add(new HookEntry(owner, field.Ordinal, oldValue, newValue, ChangeReason.Local));

        if (!IsServer && field.Authority == Authority.Owner)
            Manager.EnqueueOwnerWrite(owner.EntityId, field, newValue);
    }

    internal void EnqueueClientRpc(Component owner, string method, object?[] args)
    {
        var record = new ClientRpcRecord(
            owner.EntityId,
            owner.GetType().Name,
            method,
            args,
            0,
            0,
            owner.EntityId,
            Tick);
        PendingRpcs.Add(record);
    }

    internal void EnqueueServerRpc(Component owner, string method, object?[] args)
    {
        Manager.EnqueueServerRpc(owner.EntityId, owner.GetType().Name, method, args);
    }
}

internal readonly struct DirtyEntry
{
    internal DirtyEntry(NetEntityId entity, ISyncField field, object? oldValue, object? newValue, ChangeReason reason)
    {
        Entity = entity;
        Field = field;
        OldValue = oldValue;
        NewValue = newValue;
        Reason = reason;
    }

    internal readonly NetEntityId Entity;
    internal readonly ISyncField Field;
    internal readonly object? OldValue;
    internal readonly object? NewValue;
    internal readonly ChangeReason Reason;
}

internal readonly struct HookEntry
{
    internal HookEntry(Component owner, int ordinal, object? oldValue, object? newValue, ChangeReason reason)
    {
        Owner = owner;
        Ordinal = ordinal;
        OldValue = oldValue;
        NewValue = newValue;
        Reason = reason;
    }

    internal readonly Component Owner;
    internal readonly int Ordinal;
    internal readonly object? OldValue;
    internal readonly object? NewValue;
    internal readonly ChangeReason Reason;
}
