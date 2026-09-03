using System;
using System.Collections.Generic;
using System.Threading;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Unique owner of one GameWorld. Server <see cref="Create"/> requires an instance id;
/// client <see cref="Create"/> omits it. The only cross-thread entry is <see cref="Enqueue"/>.
/// </summary>
public sealed class WorldManager : IDisposable
{
    private readonly object _inboxLock = new();
    private readonly Queue<WorldMessage> _inbox = new();
    private readonly List<WorldMessage> _outbox = new();
    private readonly Dictionary<string, NetEntityId> _session = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _claims = new(StringComparer.Ordinal);
    private Thread? _ownerThread;
    private bool _started;
    private bool _disposed;

    private WorldManager(EcsRegistry registry, ulong instanceId, bool isServer)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        EcsRegistry.Current = registry;
        World = CreateWorld(this, registry, instanceId, isServer);
    }

    /// <summary>Generated registry used to build this world.</summary>
    public EcsRegistry Registry { get; }

    /// <summary>The unique GameWorld.</summary>
    public World World { get; private set; }

    /// <summary>Owner thread recorded at <see cref="Start"/>.</summary>
    public Thread? OwnerThread => _ownerThread;

    /// <summary>Optional host sink that receives snapshot bytes from <see cref="WorldSaveComponent.Save"/>.</summary>
    public ISnapshotSink? SnapshotSink { get; set; }

    /// <summary>
    /// Creates a world. Server registries must pass <paramref name="instanceId"/>;
    /// client registries must omit it. The registry carries the side; there is no mode parameter.
    /// </summary>
    public static WorldManager Create(EcsRegistry registry, ulong? instanceId = null)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (registry.Side == RegistrySide.Server)
        {
            if (!instanceId.HasValue)
                throw new ArgumentException("Server WorldManager.Create requires instanceId.", nameof(instanceId));
            var manager = new WorldManager(registry, instanceId.Value, isServer: true);
            manager.SpawnWorldEntity();
            return manager;
        }

        if (instanceId.HasValue)
            throw new ArgumentException("Client WorldManager.Create does not take instanceId.", nameof(instanceId));
        return new WorldManager(registry, instanceId: 0UL, isServer: false);
    }

    /// <summary>Restores a new world from snapshot bytes. Only <c>OnHydrate</c> runs.</summary>
    public static WorldManager CreateFromSnapshot(ReadOnlyMemory<byte> snapshot)
    {
        EcsRegistry registry = EcsRegistry.Current ??
            throw new InvalidOperationException("CreateFromSnapshot requires a generated registry in this process.");
        WorldSnapshotCodec.SnapshotHeader header = WorldSnapshotCodec.Read(snapshot, out List<WorldSnapshotCodec.SnapshotEntity> entities, out List<ulong> tombstones);
        var manager = new WorldManager(registry, header.InstanceId, isServer: registry.Side == RegistrySide.Server);
        manager.World.NextCounter = header.NextCounter;
        manager.World.Tick = header.Tick;
        manager.World.NextMessageId = header.NextMessageId;
        manager.World.NextRoomSequence = header.NextRoomSequence;
        for (int i = 0; i < entities.Count; i++)
        {
            WorldSnapshotCodec.SnapshotEntity row = entities[i];
            if (!registry.TryResolveEntityType(row.TypeName, out Type entityType))
                throw new InvalidOperationException("Snapshot entity type is unknown: " + row.TypeName);
            var id = new NetEntityId(header.InstanceId, row.Counter);
            Component[] components = registry.CreateComponents(entityType);
            EntityRecord record = manager.World.Attach(id, entityType, components, bind: true);
            var reader = new WorldSnapshotCodec.SnapshotReader(row.Fields);
            for (int c = 0; c < record.Components.Length; c++)
                EcsRegistry.Generated(record.Components[c])?.RestorePersist(reader);
            record.Hydrated = true;
            record.Appeared = true;
            for (int c = 0; c < record.Components.Length; c++)
                record.Components[c].OnHydrate();
        }

        for (int i = 0; i < tombstones.Count; i++)
        {
            var id = new NetEntityId(header.InstanceId, tombstones[i]);
            manager.World.Tombstones.Add(id);
        }

        return manager;
    }

    /// <summary>The single CreateWorld path. Called from <see cref="Create"/> and <see cref="CreateFromSnapshot"/>.</summary>
    private static World CreateWorld(WorldManager manager, EcsRegistry registry, ulong instanceId, bool isServer) =>
        new(manager, registry, instanceId, isServer);

    /// <summary>Records the owner thread. After this, only that thread may <see cref="Tick"/>.</summary>
    public void Start(Thread ownerThread)
    {
        ThrowIfDisposed();
        _ownerThread = ownerThread ?? throw new ArgumentNullException(nameof(ownerThread));
        _started = true;
    }

    /// <summary>The only legal cross-thread entry. Network threads enqueue; they never touch storage.</summary>
    public void Enqueue(WorldMessage message)
    {
        ThrowIfDisposed();
        if (message is null) throw new ArgumentNullException(nameof(message));
        lock (_inboxLock) _inbox.Enqueue(message);
    }

    /// <summary>Advances one logic tick on the owner thread: ApplyInputs → commit → appearance → projection.</summary>
    public void Tick()
    {
        ThrowIfDisposed();
        EnsureOwner();
        List<WorldMessage> batch;
        lock (_inboxLock)
        {
            batch = new List<WorldMessage>(_inbox.Count);
            while (_inbox.Count > 0) batch.Add(_inbox.Dequeue());
        }

        if (World.IsServer)
        {
            ApplyInputs(batch);
            CommitCreates();
            StampAndProject();
            ConsumeSave();
            World.Tick++;
            ClearTickEphemera();
        }
        else
        {
            ApplyClientBatch(batch);
            World.Tick++;
            ClearTickEphemera();
        }
    }

    /// <summary>Drains the server outbox (welcome + world-change packs) for loopback or the host.</summary>
    public IReadOnlyList<WorldMessage> DrainOutbox()
    {
        EnsureOwner();
        WorldMessage[] copy = _outbox.ToArray();
        _outbox.Clear();
        return copy;
    }

    /// <summary>Captures persist fields + identity table + issuer + WorldEntity + tick.</summary>
    public byte[] CaptureSnapshot()
    {
        EnsureOwner();
        if (!World.IsServer)
            throw new InvalidOperationException("Client worlds do not capture world snapshots.");
        CommitCreates();
        return WorldSnapshotCodec.Capture(World);
    }

    /// <summary>Binds a host connection to an entity (host session table).</summary>
    public void BindSelf(string connection, NetEntityId id)
    {
        EnsureOwner();
        _session[connection] = id;
        World.BindSelf(new Entity(World, id));
    }

    /// <summary>Looks up the host session binding.</summary>
    public bool TryGetSession(string connection, out NetEntityId id) =>
        _session.TryGetValue(connection, out id);

    /// <summary>Removes a host session binding.</summary>
    public void UnbindSession(string connection) => _session.Remove(connection);

    /// <summary>Grants a C-2 claim to a connection (derived index, rebuildable).</summary>
    public void GrantClaim(string connection, string attributeId)
    {
        if (!_claims.TryGetValue(connection, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _claims[connection] = set;
        }

        set.Add(attributeId);
    }

    /// <summary>True when <paramref name="connection"/> holds <paramref name="attributeId"/>.</summary>
    public bool HasClaim(string connection, string attributeId) =>
        _claims.TryGetValue(connection, out HashSet<string>? set) && set.Contains(attributeId);

    internal void EnqueueOwnerWrite(NetEntityId entity, ISyncField field, object? value)
    {
        string text = value as string ?? value?.ToString() ?? string.Empty;
        byte[] payload = WireCodec.EncodeFieldWrite(entity, ComponentName(field), FieldName(field), text);
        PostOutbound(new InputCommandMessage(WireCodec.FieldWrite, entity, payload, connection: null));
    }

    internal void EnqueueServerRpc(NetEntityId entity, string componentId, string method, object?[] args)
    {
        string argument = args.Length > 0 ? args[0]?.ToString() ?? string.Empty : string.Empty;
        WorldMessage message = string.Equals(componentId, "ChatComponent", StringComparison.Ordinal) &&
            string.Equals(method, "SendMessage", StringComparison.Ordinal)
            ? new InputCommandMessage(WireCodec.ChatInput, entity, WireCodec.EncodeUtf8(argument))
            : new InputCommandMessage(WireCodec.ServerRpc, entity, WireCodec.EncodeServerRpc(componentId, method, argument));
        PostOutbound(message);
    }

    private void PostOutbound(WorldMessage message)
    {
        if (World.IsServer)
        {
            Enqueue(message);
            return;
        }

        EnsureOwner();
        _outbox.Add(message);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private void SpawnWorldEntity()
    {
        Type worldType = Registry.WorldEntityType;
        Component[] components = Registry.CreateComponents(worldType);
        NetEntityId id = World.IssueId();
        EntityRecord record = World.Attach(id, worldType, components, bind: true);
        Appear(record, postAttribute: false);
    }

    private void ApplyInputs(List<WorldMessage> batch)
    {
        var inputs = new List<InputCommandMessage>();
        for (int i = 0; i < batch.Count; i++)
        {
            if (batch[i] is InputCommandMessage input) inputs.Add(input);
        }

        inputs.Sort(static (left, right) => left.Sender.CompareTo(right.Sender));
        for (int i = 0; i < inputs.Count; i++)
            ApplyInput(inputs[i]);
    }

    private void ApplyInput(InputCommandMessage input)
    {
        if (string.Equals(input.MappingId, WireCodec.FieldWrite, StringComparison.Ordinal))
        {
            ApplyFieldWrite(input);
            return;
        }

        if (string.Equals(input.MappingId, WireCodec.ChatInput, StringComparison.Ordinal))
        {
            if (!WireCodec.TryReadUtf8Payload(input.Payload.Span, out string text)) return;
            if (!World.IsLive(input.Sender)) return;
            Component? chat = FindComponent(input.Sender, "ChatComponent");
            if (chat is null) return;
            chat.Rpc = new RpcContext(input.Sender, World.Tick);
            IGeneratedComponent? generated = EcsRegistry.Generated(chat);
            if (generated is not null) generated.DispatchServerRpc("SendMessage", new object[] { text });
            else TryDispatchSendMessage(chat, text);
            return;
        }

        if (string.Equals(input.MappingId, WireCodec.ServerRpc, StringComparison.Ordinal))
        {
            if (!WireCodec.TryDecodeServerRpc(input.Payload.Span, out string componentId, out string method, out string argument))
                return;
            Component? component = FindComponent(input.Sender, componentId);
            if (component is null) return;
            component.Rpc = new RpcContext(input.Sender, World.Tick);
            EcsRegistry.Generated(component)?.DispatchServerRpc(method, new object[] { argument });
        }
    }

    private void ApplyFieldWrite(InputCommandMessage input)
    {
        if (!WireCodec.TryDecodeFieldWrite(input.Payload.Span, out ulong counter, out string componentId, out string fieldId, out string value))
            return;
        var target = new NetEntityId(World.InstanceId, counter);
        if (target != input.Sender)
        {
            PushCorrection(input.Sender, componentId, fieldId);
            return;
        }

        Component? component = FindComponent(target, componentId);
        if (component is null) return;
        IGeneratedComponent? generated = EcsRegistry.Generated(component);
        ISyncField? field = FindSyncField(component, fieldId);
        if (field is null) return;
        if (field.Authority != Authority.Owner)
        {
            PushCorrection(target, componentId, fieldId);
            return;
        }

        var write = new SyncWrite(field, value);
        bool accept = generated is null || generated.DispatchClientWrite(in write);
        if (!accept)
        {
            PushCorrection(target, componentId, fieldId);
            return;
        }

        object? old = field.BoxedValue;
        generated?.WriteField(fieldId, value, silent: false);
        if (generated is null)
            field.AssignFromRemote(value);
        World.Dirty.Add(new DirtyEntry(target, field, old, value, ChangeReason.Sync));
        World.PendingHooks.Add(new HookEntry(component, field.Ordinal, old, value, ChangeReason.Sync));
        World.Revision++;
    }

    private void PushCorrection(NetEntityId target, string componentId, string fieldId)
    {
        Component? component = FindComponent(target, componentId);
        if (component is null) return;
        ISyncField? field = FindSyncField(component, fieldId);
        object? value = field?.BoxedValue;
        World.PendingCorrections.Add(new FieldChange(target, componentId, fieldId, value, ChangeReason.Correction));
    }

    private void CommitCreates()
    {
        for (int i = 0; i < World.PendingCreates.Count; i++)
        {
            EntityOrder order = World.PendingCreates[i];
            if (order.Issued) continue;
            NetEntityId id = World.IssueId();
            order.AssignedId = id;
            order.Issued = true;
            EntityRecord record = World.Attach(id, order.EntityType, order.Components, bind: true);
            Appear(record, postAttribute: false);
            World.Revision++;
        }

        World.PendingCreates.Clear();

        for (int i = 0; i < World.PendingDestroys.Count; i++)
            Destroy(World.PendingDestroys[i]);
        World.PendingDestroys.Clear();
    }

    private static void Appear(EntityRecord record, bool postAttribute)
    {
        if (record.Appeared) return;
        for (int i = 0; i < record.Components.Length; i++)
            record.Components[i].Awake();
        record.Lifecycle.Add("Awake");
        if (postAttribute)
        {
            for (int i = 0; i < record.Components.Length; i++)
                EcsRegistry.Generated(record.Components[i])?.InvokePostAttribute();
            record.Lifecycle.Add("PostAttribute");
        }

        for (int i = 0; i < record.Components.Length; i++)
        {
            record.Components[i].OnEnable();
            record.Components[i].Start();
        }
        record.Lifecycle.Add("Start");

        record.Appeared = true;
    }

    private void Destroy(NetEntityId id)
    {
        if (!World.Entities.TryGetValue(id, out EntityRecord? record))
        {
            World.Tombstones.Add(id);
            return;
        }

        for (int i = 0; i < record.Components.Length; i++)
        {
            record.Components[i].OnDisable();
            record.Components[i].OnDestroy();
        }

        record.Presence = Presence.Tombstoned;
        World.Tombstones.Add(id);
        World.Entities.Remove(id);
        World.CreationOrder.Remove(id);
    }

    private void StampAndProject()
    {
        var creates = new List<CreateRecord>();
        for (int i = 0; i < World.CreationOrder.Count; i++)
        {
            NetEntityId id = World.CreationOrder[i];
            if (!World.Entities.TryGetValue(id, out EntityRecord? record) || record.Presence != Presence.Live) continue;
            if (record.Revision == 0) continue;
            creates.Add(ToCreateRecord(record));
        }

        // Recipients already have entities; send only new creates this tick via Dirty of kind create.
        // For the slice, emit a full live census on the first pack and deltas afterwards.
        var fields = new List<FieldChange>();
        for (int i = 0; i < World.Dirty.Count; i++)
        {
            DirtyEntry dirty = World.Dirty[i];
            fields.Add(new FieldChange(
                dirty.Entity,
                ComponentName(dirty.Field),
                FieldName(dirty.Field),
                dirty.NewValue,
                ChangeReason.Sync));
        }

        for (int i = 0; i < World.PendingCorrections.Count; i++)
            fields.Add(World.PendingCorrections[i]);

        var rpcs = new List<ClientRpcRecord>(World.PendingRpcs.Count);
        for (int i = 0; i < World.PendingRpcs.Count; i++)
        {
            ClientRpcRecord rpc = World.PendingRpcs[i];
            rpcs.Add(new ClientRpcRecord(
                rpc.Target,
                rpc.ComponentId,
                rpc.Method,
                rpc.Args,
                World.NextMessageId++,
                World.NextRoomSequence++,
                rpc.Sender,
                World.Tick));
        }

        foreach (KeyValuePair<string, NetEntityId> pair in _session)
        {
            if (!HasWelcome(pair.Key))
                _outbox.Add(new WelcomeMessage(World.InstanceId, pair.Value, pair.Key));
        }

        var newCreates = new List<CreateRecord>();
        for (int i = 0; i < World.CreationOrder.Count; i++)
        {
            if (!World.Entities.TryGetValue(World.CreationOrder[i], out EntityRecord? record)) continue;
            if (!record.Appeared) continue;
            if (record.Hydrated) continue;
            if (IsKnownToClients(record.Id)) continue;
            newCreates.Add(ToCreateRecord(record));
            MarkKnown(record.Id);
        }

        _outbox.Add(new WorldChangeMessage(World.Tick, newCreates, fields, Array.Empty<NetEntityId>(), rpcs));
        FireLocalHooks();
    }

    private readonly HashSet<NetEntityId> _knownToClients = new();
    private readonly HashSet<string> _welcomed = new(StringComparer.Ordinal);

    private bool HasWelcome(string connection)
    {
        if (_welcomed.Contains(connection)) return true;
        _welcomed.Add(connection);
        return false;
    }

    private bool IsKnownToClients(NetEntityId id) => _knownToClients.Contains(id);

    private void MarkKnown(NetEntityId id) => _knownToClients.Add(id);

    private void FireLocalHooks()
    {
        for (int i = 0; i < World.PendingHooks.Count; i++)
        {
            HookEntry hook = World.PendingHooks[i];
            IGeneratedComponent? generated = EcsRegistry.Generated(hook.Owner);
            generated?.InvokeFieldChanging(hook.Ordinal, hook.OldValue, hook.NewValue, hook.Reason);
            generated?.InvokeFieldChanged(hook.Ordinal, hook.OldValue, hook.NewValue, hook.Reason);
        }
    }

    private void ApplyClientBatch(List<WorldMessage> batch)
    {
        var changes = new List<WorldChangeMessage>();
        for (int i = 0; i < batch.Count; i++)
        {
            if (batch[i] is WelcomeMessage welcome)
            {
                World.InstanceId = welcome.InstanceId;
                World.BindSelf(new Entity(World, welcome.Self));
                if (!string.IsNullOrEmpty(welcome.Connection))
                    _session[welcome.Connection] = welcome.Self;
                continue;
            }

            if (batch[i] is WorldChangeMessage change) changes.Add(change);
        }

        World.ApplyingRemote = true;
        try
        {
            for (int i = 0; i < changes.Count; i++)
                ApplyWorldChange(changes[i]);
        }
        finally
        {
            World.ApplyingRemote = false;
        }
    }

    private void ApplyWorldChange(WorldChangeMessage change)
    {
        var created = new List<EntityRecord>();
        for (int i = 0; i < change.Creates.Count; i++)
        {
            CreateRecord create = change.Creates[i];
            if (World.Entities.ContainsKey(create.NetEntityId)) continue;
            if (!Registry.TryResolveEntityType(create.EntityType, out Type entityType))
                throw new InvalidOperationException("Unknown entity type " + create.EntityType);
            Component[] components = Registry.CreateComponents(entityType);
            EntityRecord record = World.Attach(create.NetEntityId, entityType, components, bind: true);
            for (int c = 0; c < record.Components.Length; c++)
                record.Components[c].Awake();
            record.Lifecycle.Add("Awake");
            for (int f = 0; f < create.Fields.Count; f++)
            {
                FieldValue field = create.Fields[f];
                Component? component = FindComponent(create.NetEntityId, field.ComponentId);
                EcsRegistry.Generated(component!)?.WriteField(field.FieldId, field.Value, silent: true);
            }

            for (int c = 0; c < record.Components.Length; c++)
                EcsRegistry.Generated(record.Components[c])?.InvokePostAttribute();
            record.Lifecycle.Add("PostAttribute");

            for (int c = 0; c < record.Components.Length; c++)
            {
                record.Components[c].OnEnable();
                record.Components[c].Start();
            }
            record.Lifecycle.Add("Start");

            record.Appeared = true;
            created.Add(record);
        }

        var pendingHooks = new List<HookEntry>();
        for (int i = 0; i < change.Fields.Count; i++)
        {
            FieldChange field = change.Fields[i];
            Component? component = FindComponent(field.NetEntityId, field.ComponentId);
            if (component is null) continue;
            ISyncField? sync = FindSyncField(component, field.FieldId);
            object? old = sync?.BoxedValue;
            EcsRegistry.Generated(component)?.WriteField(field.FieldId, field.Value, silent: true);
            if (sync is not null)
                pendingHooks.Add(new HookEntry(component, sync.Ordinal, old, field.Value, field.Reason));
        }

        for (int i = 0; i < change.Destroys.Count; i++)
            Destroy(change.Destroys[i]);

        for (int i = 0; i < pendingHooks.Count; i++)
        {
            HookEntry hook = pendingHooks[i];
            IGeneratedComponent? generated = EcsRegistry.Generated(hook.Owner);
            generated?.InvokeFieldChanging(hook.Ordinal, hook.OldValue, hook.NewValue, hook.Reason);
            generated?.InvokeFieldChanged(hook.Ordinal, hook.OldValue, hook.NewValue, hook.Reason);
        }

        for (int i = 0; i < change.Rpcs.Count; i++)
        {
            ClientRpcRecord rpc = change.Rpcs[i];
            Component? component = FindComponent(rpc.Target, rpc.ComponentId);
            if (component is null) continue;
            component.Rpc = new RpcContext(rpc.Sender, rpc.AppliedTick);
            object?[] args = new object?[rpc.Args.Count];
            for (int a = 0; a < rpc.Args.Count; a++) args[a] = rpc.Args[a];
            EcsRegistry.Generated(component)?.DispatchClientRpc(rpc.Method, args);
        }

        World.Tick = change.Tick;
    }

    private CreateRecord ToCreateRecord(EntityRecord record)
    {
        var fields = new List<FieldValue>();
        for (int i = 0; i < record.Components.Length; i++)
        {
            Component component = record.Components[i];
            CollectVisibleFields(component, fields);
        }

        return new CreateRecord(Registry.WireName(record.EntityType), record.Id, fields);
    }

    private static void CollectVisibleFields(Component component, List<FieldValue> fields)
    {
        IGeneratedComponent? generated = EcsRegistry.Generated(component);
        if (generated is null) return;
        generated.CaptureSync(new VisibleFieldCollector(component, fields));
    }

    private void ConsumeSave()
    {
        if (World.PendingSaveSlot is null) return;
        string slot = World.PendingSaveSlot;
        World.PendingSaveSlot = null;
        byte[] bytes = WorldSnapshotCodec.Capture(World);
        SnapshotSink?.Write(slot, bytes);
        _outbox.Add(new SnapshotReadyMessage(slot, bytes));
    }

    private void ClearTickEphemera()
    {
        World.Dirty.Clear();
        World.PendingRpcs.Clear();
        World.PendingCorrections.Clear();
        World.PendingHooks.Clear();
    }

    private Component? FindComponent(NetEntityId id, string componentId)
    {
        if (!World.Entities.TryGetValue(id, out EntityRecord? record) || record.Presence != Presence.Live)
            return null;
        for (int i = 0; i < record.Components.Length; i++)
        {
            if (string.Equals(record.Components[i].GetType().Name, componentId, StringComparison.Ordinal))
                return record.Components[i];
        }

        return null;
    }

    private static ISyncField? FindSyncField(Component component, string fieldId)
    {
        System.Reflection.FieldInfo[] fields = component.GetType().GetFields();
        for (int i = 0; i < fields.Length; i++)
        {
            if (!string.Equals(Camel(fields[i].Name), fieldId, StringComparison.Ordinal) &&
                !string.Equals(fields[i].Name, fieldId, StringComparison.OrdinalIgnoreCase))
                continue;
            object? value = fields[i].GetValue(component);
            if (value is ISyncField sync) return sync;
            if (value is not null)
            {
                System.Reflection.PropertyInfo? identity = value.GetType().GetProperty("Identity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (identity?.GetValue(value) is ISyncField boxed) return boxed;
            }
        }

        return null;
    }

    private static void TryDispatchSendMessage(Component chat, string text)
    {
        System.Reflection.MethodInfo? method = chat.GetType().GetMethod("SendMessage", new[] { typeof(string) });
        method?.Invoke(chat, new object[] { text });
    }

    private static string ComponentName(ISyncField field)
    {
        string id = field.AttributeId;
        int dot = id.IndexOf('.');
        return dot <= 0 ? field.Owner.GetType().Name : id.Substring(0, dot);
    }

    private static string FieldName(ISyncField field)
    {
        string id = field.AttributeId;
        int dot = id.IndexOf('.');
        return dot < 0 ? id : id.Substring(dot + 1);
    }

    private static string Camel(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (char.IsLower(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private void EnsureOwner()
    {
        if (!_started || _ownerThread is null)
            throw new InvalidOperationException("WorldManager.Start must run before Tick.");
        if (!ReferenceEquals(Thread.CurrentThread, _ownerThread) &&
            Environment.CurrentManagedThreadId != _ownerThread.ManagedThreadId)
            throw new InvalidOperationException("WorldManager entry called from a non-owner thread.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorldManager));
    }

    private sealed class VisibleFieldCollector : PersistWriter
    {
        private readonly Component _component;
        private readonly List<FieldValue> _fields;

        internal VisibleFieldCollector(Component component, List<FieldValue> fields)
        {
            _component = component;
            _fields = fields;
        }

        public void WriteString(string attributeId, string? value) => Add(attributeId, value);
        public void WriteUInt64(string attributeId, ulong value) => Add(attributeId, value);
        public void WriteBoolean(string attributeId, bool value) => Add(attributeId, value);

        private void Add(string attributeId, object? value)
        {
            int dot = attributeId.IndexOf('.');
            string component = dot <= 0 ? _component.GetType().Name : attributeId.Substring(0, dot);
            string field = dot < 0 ? attributeId : attributeId.Substring(dot + 1);
            _fields.Add(new FieldValue(component, field, value));
        }
    }
}

/// <summary>Outbox notice that a snapshot was produced. Hosts write the bytes; Runtime does not.</summary>
public sealed class SnapshotReadyMessage : WorldMessage
{
    /// <summary>Creates a snapshot-ready notice.</summary>
    public SnapshotReadyMessage(string slot, byte[] bytes)
    {
        Slot = slot;
        Bytes = bytes;
    }

    /// <summary>Save slot name.</summary>
    public string Slot { get; }

    /// <summary>Snapshot payload.</summary>
    public byte[] Bytes { get; }
}
