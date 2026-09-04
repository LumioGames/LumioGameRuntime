using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

/// <summary>The single dense-array GameWorld owned by a WorldManager.</summary>
public sealed class World : ISyncHost
{
    internal World(WorldManager manager, EcsRegistry registry, ulong instanceId, bool isServer)
    {
        Manager = manager;
        Registry = registry;
        InstanceId = instanceId;
        IsServer = isServer;
        Commands = new CommandBuffer(this);
        Entities = new List<EntityRecord?> { null };
        PendingCreates = new List<EntityOrder>();
        PendingDestroys = new List<NetEntityId>();
        Dirty = new List<DirtyEntry>();
        PendingRpcs = new List<ClientRpcRecord>();
        PendingCorrections = new List<FieldChange>();
        PendingHooks = new List<HookEntry>();
        DestroyedThisTick = new List<NetEntityId>();
        AccountIndex = new Dictionary<string, NetEntityId>(StringComparer.Ordinal);
        CreationOrder = new List<NetEntityId>();
        ComponentPools = new Dictionary<Type, Stack<Component[]>>();
    }

    public WorldManager Manager { get; internal set; }
    public EcsRegistry Registry { get; }
    public ulong InstanceId { get; internal set; }
    public bool IsServer { get; }
    public ulong Tick { get; internal set; }
    public ulong Revision { get; internal set; } = 1;

    private Entity? _self;
    public Entity Self => _self ?? throw new InvalidOperationException("World.Self is not bound.");
    internal void BindSelf(Entity entity) => _self = entity;

    public CommandBuffer Commands { get; }
    public IReadOnlyList<NetEntityId> IssuedIds => CreationOrder;

    internal List<EntityRecord?> Entities { get; }
    internal List<EntityOrder> PendingCreates { get; }
    internal List<NetEntityId> PendingDestroys { get; }
    internal List<DirtyEntry> Dirty { get; }
    internal List<ClientRpcRecord> PendingRpcs { get; }
    internal List<FieldChange> PendingCorrections { get; }
    internal List<HookEntry> PendingHooks { get; }
    internal List<NetEntityId> DestroyedThisTick { get; }
    internal Dictionary<string, NetEntityId> AccountIndex { get; }
    internal List<NetEntityId> CreationOrder { get; }
    private Dictionary<Type, Stack<Component[]>> ComponentPools { get; }
    internal ulong NextCounter = 1;
    internal ulong NextMessageId = 1;
    internal ulong NextRoomSequence = 1;
    internal string? PendingSaveSlot;
    internal bool ApplyingRemote;

    bool ISyncHost.IsServer => IsServer;
    bool ISyncHost.IsApplyingRemote => ApplyingRemote;
    WorldManager ISyncHost.Manager => Manager;
    World ISyncHost.World => this;

    internal EntityRecord? Record(NetEntityId id)
    {
        if (id.InstanceId != InstanceId || id.Counter == 0 || id.Counter >= (ulong)Entities.Count) return null;
        return Entities[(int)id.Counter];
    }

    public T Get<T>(NetEntityId id) where T : Component => Record(id)?.Get<T>() ??
        throw new InvalidOperationException("Entity " + id.ToHex() + " is not live.");

    public Entity Get(NetEntityId id) => Record(id) is not null
        ? new Entity(this, id)
        : throw new InvalidOperationException("Entity " + id.ToHex() + " is not live.");

    public bool IsLive(NetEntityId id) => Record(id) is not null;

    /// <summary>Tombstones are derived from the issuer counter and dense live slots.</summary>
    public bool IsTombstoned(NetEntityId id) =>
        id.InstanceId == InstanceId && id.Counter > 0 && id.Counter < NextCounter && Record(id) is null;

    public bool TryGetAccount(string accountId, out NetEntityId id)
    {
        if (string.IsNullOrEmpty(accountId)) { id = default; return false; }
        return AccountIndex.TryGetValue(accountId, out id) && IsLive(id);
    }

    public Component? NamedComponent(NetEntityId id, string typeName) => Record(id)?.Find(typeName);

    public IEnumerable<T> Each<T>() where T : Component
    {
        for (int i = 0; i < CreationOrder.Count; i++)
        {
            EntityRecord? record = Record(CreationOrder[i]);
            if (record is null) continue;
            foreach (T component in record.OfType<T>()) yield return component;
        }
    }

    public T Single<T>() where T : Component
    {
        T? found = null;
        foreach (T item in Each<T>())
        {
            if (found is not null) throw new InvalidOperationException("More than one " + typeof(T).Name + " is live.");
            found = item;
        }
        return found ?? throw new InvalidOperationException("No live " + typeof(T).Name + " exists.");
    }

    public IReadOnlyList<string> LifecycleOf(NetEntityId id)
    {
        EntityRecord? record = Record(id);
        if (record is null) return Array.Empty<string>();
        var result = new List<string>(3);
        if (record.AwakeCalled) result.Add("Awake");
        if (record.PostAttributeCalled) result.Add("PostAttribute");
        if (record.Started) result.Add("Start");
        return result;
    }

    public EntityTypeRef TypeOf(NetEntityId id) => Record(id) is EntityRecord record
        ? new EntityTypeRef(record.EntityType, Registry)
        : throw new InvalidOperationException("Entity " + id.ToHex() + " is not live.");

    public void QueueDestroy(NetEntityId id) => PendingDestroys.Add(id);
    internal void RequestSave(string slot) => PendingSaveSlot = slot;

    internal NetEntityId IssueId()
    {
        if (!IsServer) throw new InvalidOperationException("Client worlds do not issue NetEntityId.");
        if (NextCounter == 0 || NextCounter == ulong.MaxValue)
            throw new InvalidOperationException("NetEntityId counter is exhausted.");
        return new NetEntityId(InstanceId, NextCounter++);
    }

    internal EntityRecord Attach(NetEntityId id, Type entityType, Component[] components, bool bind)
    {
        if (id.Counter > int.MaxValue) throw new InvalidOperationException("Entity counter exceeds dense storage capacity.");
        while (Entities.Count <= (int)id.Counter) Entities.Add(null);
        var record = new EntityRecord(id, entityType, components);
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            component.WorldInternal = this;
            component.EntityId = id;
            component.Record = record;
            if (bind) Registry.BindComponent(component, this);
        }
        Entities[(int)id.Counter] = record;
        if (!CreationOrder.Contains(id)) CreationOrder.Add(id);
        if (id.Counter >= NextCounter && id.Counter < ulong.MaxValue) NextCounter = id.Counter + 1UL;
        IndexAccount(record);
        return record;
    }

    internal void Detach(NetEntityId id)
    {
        EntityRecord? record = id.Counter < (ulong)Entities.Count ? Entities[(int)id.Counter] : null;
        if (record is not null)
        {
            Entities[(int)id.Counter] = null;
            ResetAndPool(record.EntityType, record.Components);
        }
        foreach (KeyValuePair<string, NetEntityId> pair in new List<KeyValuePair<string, NetEntityId>>(AccountIndex))
            if (pair.Value == id) AccountIndex.Remove(pair.Key);
    }

    internal Component[] RentComponents(Type entityType)
    {
        if (ComponentPools.TryGetValue(entityType, out Stack<Component[]>? pool) && pool.Count > 0)
            return pool.Pop();
        return Registry.CreateComponents(entityType);
    }

    private void ResetAndPool(Type entityType, Component[] components)
    {
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component is ObserverComponent observer)
            {
                observer.Connected = false;
                observer.ConnectionGeneration = 0;
                observer.DisconnectedAtTick = 0;
                observer.ProjectedTick = 0;
            }
            IGeneratedComponent? generated = EcsRegistry.Generated(component);
            if (generated is null) continue;
            string prefix = component.GetType().Name + ".";
            for (int r = 0; r < Registry.AttributeDeclarations.Count; r++)
            {
                string id = Registry.AttributeDeclarations[r].AttributeId;
                if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string fieldName = id.Substring(prefix.Length);
                object? current = generated.ReadField(fieldName);
                if (current is ISyncContainer container) { container.ResetForReuse(); continue; }
                generated.WriteField(fieldName, current switch
                {
                    string => string.Empty,
                    bool => false,
                    ulong => 0UL,
                    uint => 0U,
                    int => 0,
                    _ => null
                }, silent: true);
            }
        }

        if (!ComponentPools.TryGetValue(entityType, out Stack<Component[]>? pool))
            ComponentPools[entityType] = pool = new Stack<Component[]>();
        pool.Push(components);
    }

    internal void RebuildAccountIndex()
    {
        AccountIndex.Clear();
        for (int i = 0; i < CreationOrder.Count; i++)
        {
            EntityRecord? record = Record(CreationOrder[i]);
            if (record is not null) IndexAccount(record);
        }
    }

    internal void IndexAccount(EntityRecord record)
    {
        for (int i = 0; i < record.Components.Length; i++)
        {
            IGeneratedComponent? generated = EcsRegistry.Generated(record.Components[i]);
            if (generated?.ReadField("accountId") is string account && account.Length > 0)
                AccountIndex[account] = record.Id;
        }
    }

    public static string? TryReadAccountId(Component component) =>
        EcsRegistry.Generated(component)?.ReadField("accountId") as string;

    void ISyncHost.OnLocalWrite(Component owner, ISyncField field, object? oldValue, object? newValue)
    {
        if (ApplyingRemote) return;
        Dirty.Add(new DirtyEntry(owner.EntityId, field, oldValue, newValue, ChangeReason.Local));
        if (field.Notify == Notify.All)
            PendingHooks.Add(new HookEntry(owner, field.Ordinal, oldValue, newValue, ChangeReason.Local));
        if (!IsServer && field.Authority == Authority.Owner)
            Manager.EnqueueOwnerWrite(owner.EntityId, field, newValue);
    }

    void ISyncHost.OnContainerWrite(Component owner, ISyncContainer container, object? oldValue, object? newValue)
    {
        if (ApplyingRemote) return;
        Dirty.Add(new DirtyEntry(owner.EntityId, container, oldValue, newValue, ChangeReason.Local));
        if (!IsServer && container.Authority == Authority.Owner)
            Manager.EnqueueOwnerWrite(owner.EntityId, container, newValue);
    }

    internal void EnqueueClientRpc(Component owner, string method, Scope scope, object?[] args) =>
        PendingRpcs.Add(new ClientRpcRecord(owner.EntityId, owner.GetType().Name, method, args, 0, 0, owner.EntityId, Tick, scope));

    internal void EnqueueServerRpc(Component owner, string method, object?[] args) =>
        Manager.EnqueueServerRpc(owner.EntityId, owner.GetType().Name, method, args);
}

internal readonly struct DirtyEntry
{
    internal DirtyEntry(NetEntityId entity, ISyncField field, object? oldValue, object? newValue, ChangeReason reason, bool suppressWriterEcho = false)
    { Entity = entity; Field = field; Container = null; OldValue = oldValue; NewValue = newValue; Reason = reason; SuppressWriterEcho = suppressWriterEcho; }
    internal DirtyEntry(NetEntityId entity, ISyncContainer container, object? oldValue, object? newValue, ChangeReason reason, bool suppressWriterEcho = false)
    { Entity = entity; Field = null; Container = container; OldValue = oldValue; NewValue = newValue; Reason = reason; SuppressWriterEcho = suppressWriterEcho; }
    internal readonly NetEntityId Entity;
    internal readonly ISyncField? Field;
    internal readonly ISyncContainer? Container;
    internal readonly object? OldValue;
    internal readonly object? NewValue;
    internal readonly ChangeReason Reason;
    internal readonly bool SuppressWriterEcho;
}

internal readonly struct HookEntry
{
    internal HookEntry(Component owner, int ordinal, object? oldValue, object? newValue, ChangeReason reason)
    {
        Owner = owner; Ordinal = ordinal; OldValue = oldValue; NewValue = newValue; Reason = reason;
    }
    internal readonly Component Owner;
    internal readonly int Ordinal;
    internal readonly object? OldValue;
    internal readonly object? NewValue;
    internal readonly ChangeReason Reason;
}
