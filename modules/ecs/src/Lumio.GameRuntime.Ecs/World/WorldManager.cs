using System;
using System.Collections.Generic;
using System.Threading;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Single owner of the authoritative World and its per-observer projection.</summary>
public sealed class WorldManager : IDisposable
{
    private readonly object _inboxLock = new();
    private readonly Queue<WorldMessage> _inbox = new();
    private readonly List<WorldMessage> _outbox = new();
    private readonly Dictionary<NetEntityId, int> _initialCreateCursors = new();
    private Thread? _ownerThread;
    private bool _started;
    private bool _disposed;
    private NetEntityId _pendingWelcomeSelf;
    private ulong _pendingWelcomeGeneration;

    private WorldManager(EcsRegistry registry, ulong instanceId, bool isServer)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        EcsRegistry.Current = registry;
        World = new World(this, registry, instanceId, isServer);
    }

    public EcsRegistry Registry { get; }
    public World World { get; private set; }
    public Thread? OwnerThread => _ownerThread;
    public bool IsOwnerThread => _started && _ownerThread is not null && Environment.CurrentManagedThreadId == _ownerThread.ManagedThreadId;
    public ISnapshotSink? SnapshotSink { get; set; }

    /// <summary>Maximum number of initial create records per observer pack; zero means unlimited.</summary>
    public int CreatesPerPack { get; set; }

    public static WorldManager Create(EcsRegistry registry, ulong? instanceId = null)
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (registry.Side == RegistrySide.Server)
        {
            if (!instanceId.HasValue) throw new ArgumentException("Server WorldManager.Create requires instanceId.", nameof(instanceId));
            var manager = new WorldManager(registry, instanceId.Value, true);
            manager.SpawnWorldEntity();
            return manager;
        }

        if (instanceId.HasValue) throw new ArgumentException("Client WorldManager.Create does not take instanceId.", nameof(instanceId));
        return new WorldManager(registry, 0UL, false);
    }

    public static WorldManager CreateFromSnapshot(ReadOnlyMemory<byte> snapshot)
    {
        EcsRegistry registry = EcsRegistry.Current ?? throw new InvalidOperationException("CreateFromSnapshot requires a generated registry.");
        WorldSnapshotCodec.SnapshotHeader header = WorldSnapshotCodec.Read(snapshot, out List<WorldSnapshotCodec.SnapshotEntity> entities);
        var manager = new WorldManager(registry, header.InstanceId, registry.Side == RegistrySide.Server);
        manager.World.NextCounter = header.NextCounter;
        manager.World.Tick = header.Tick;
        manager.World.NextMessageId = header.NextMessageId;
        manager.World.NextRoomSequence = header.NextRoomSequence;
        for (int i = 0; i < entities.Count; i++)
        {
            WorldSnapshotCodec.SnapshotEntity row = entities[i];
            if (!registry.TryResolveEntityType(row.TypeName, out Type entityType))
                throw new InvalidOperationException("Snapshot entity type is unknown: " + row.TypeName);
            NetEntityId id = new(header.InstanceId, row.Counter);
            EntityRecord record = manager.World.Attach(id, entityType, manager.World.RentComponents(entityType), bind: true);
            var reader = new WorldSnapshotCodec.SnapshotReader(row.Fields);
            for (int c = 0; c < record.Components.Length; c++) EcsRegistry.Generated(record.Components[c])?.RestorePersist(reader);
            record.Hydrated = true;
            record.Appeared = true;
            record.AwakeCalled = true;
            record.PostAttributeCalled = true;
            record.Started = true;
            for (int c = 0; c < record.Components.Length; c++) record.Components[c].OnHydrate();
        }
        manager.World.RebuildAccountIndex();
        return manager;
    }

    public void Start(Thread ownerThread)
    {
        ThrowIfDisposed();
        _ownerThread = ownerThread ?? throw new ArgumentNullException(nameof(ownerThread));
        _started = true;
    }

    public void Enqueue(WorldMessage message)
    {
        ThrowIfDisposed();
        if (message is null) throw new ArgumentNullException(nameof(message));
        lock (_inboxLock) _inbox.Enqueue(message);
    }

    public void Tick()
    {
        ThrowIfDisposed();
        EnsureOwner();
        var batch = new List<WorldMessage>();
        lock (_inboxLock) while (_inbox.Count > 0) batch.Add(_inbox.Dequeue());
        if (World.IsServer)
        {
            ApplyInputs(batch);
            CommitCreates();
            Project();
            ConsumeSave();
            World.Tick++;
            ClearTickEphemera();
            return;
        }

        ApplyClientBatch(batch);
        World.Tick++;
        ClearTickEphemera();
    }

    public IReadOnlyList<WorldMessage> DrainOutbox()
    {
        EnsureOwner();
        WorldMessage[] copy = _outbox.ToArray();
        _outbox.Clear();
        return copy;
    }

    public byte[] CaptureSnapshot()
    {
        EnsureOwner();
        if (!World.IsServer) throw new InvalidOperationException("Client worlds do not capture snapshots.");
        CommitCreates();
        return WorldSnapshotCodec.Capture(World);
    }

    /// <summary>Binds an observer entity without advancing logical time.</summary>
    public void Bind(NetEntityId observerId)
    {
        EnsureOwner();
        ObserverComponent observer = World.Get<ObserverComponent>(observerId);
        observer.Connected = true;
        observer.ConnectionGeneration = observer.ConnectionGeneration == 0 ? 1 : observer.ConnectionGeneration + 1;
        observer.DisconnectedAtTick = 0;
        observer.ProjectedTick = 0;
        _initialCreateCursors.Remove(observerId);
    }

    /// <summary>Marks an observer disconnected without destroying its entity.</summary>
    public void Unbind(NetEntityId observerId)
    {
        EnsureOwner();
        ObserverComponent observer = World.Get<ObserverComponent>(observerId);
        observer.Connected = false;
        observer.DisconnectedAtTick = World.Tick;
        observer.ProjectedTick = 0;
        _initialCreateCursors.Remove(observerId);
    }

    internal void EnqueueOwnerWrite(NetEntityId entity, ISyncField field, object? value)
    {
        string text = value as string ?? value?.ToString() ?? string.Empty;
        PostOutbound(new InputCommandMessage(WireCodec.FieldWrite, entity, WireCodec.EncodeFieldWrite(entity, ComponentName(field), FieldName(field), text)));
    }

    internal void EnqueueOwnerWrite(NetEntityId entity, ISyncContainer container, object? value)
    {
        string text = WireCodec.ContainerText(container);
        PostOutbound(new InputCommandMessage(WireCodec.FieldWrite, entity, WireCodec.EncodeFieldWrite(entity, ComponentName(container), FieldName(container), text)));
    }

    internal void EnqueueServerRpc(NetEntityId entity, string componentId, string method, object?[] args)
    {
        string mapping = string.Equals(componentId, "ChatComponent", StringComparison.Ordinal) && string.Equals(method, "SendMessage", StringComparison.Ordinal)
            ? WireCodec.ChatInput : WireCodec.ServerRpc;
        byte[] payload = mapping == WireCodec.ChatInput
            ? WireCodec.EncodeUtf8(args.Length == 0 ? string.Empty : args[0]?.ToString() ?? string.Empty)
            : WireCodec.EncodeServerRpc(componentId, method, args);
        PostOutbound(new InputCommandMessage(mapping, entity, payload));
    }

    public void Dispose() => _disposed = true;

    private void SpawnWorldEntity()
    {
        Type type = Registry.WorldEntityType;
        EntityRecord record = World.Attach(World.IssueId(), type, World.RentComponents(type), bind: true);
        Appear(record, false);
    }

    private void ApplyInputs(List<WorldMessage> batch)
    {
        var inputs = new List<InputCommandMessage>();
        for (int i = 0; i < batch.Count; i++) if (batch[i] is InputCommandMessage input) inputs.Add(input);
        inputs.Sort(static (a, b) => a.Sender.CompareTo(b.Sender));
        for (int i = 0; i < inputs.Count; i++)
        {
            InputCommandMessage input = inputs[i];
            for (int c = 0; c < input.Commands.Count; c++)
                ApplyInput(new InputCommandMessage(input.Commands[c].MappingId, input.Sender, input.Commands[c].Payload, input.Connection));
        }
    }

    private void ApplyInput(InputCommandMessage input)
    {
        if (input.MappingId == WireCodec.FieldWrite) { ApplyFieldWrite(input); return; }
        if (input.MappingId == WireCodec.ChatInput)
        {
            if (!WireCodec.TryReadUtf8Payload(input.Payload.Span, out string text) || !World.IsLive(input.Sender)) return;
            Component? chat = World.NamedComponent(input.Sender, "ChatComponent");
            if (chat is null) return;
            chat.Rpc = new RpcContext(input.Sender, World.Tick);
            EcsRegistry.Generated(chat)?.DispatchServerRpc("SendMessage", new object[] { text });
            return;
        }
        if (input.MappingId == WireCodec.ServerRpc && WireCodec.TryDecodeServerRpc(input.Payload.Span, out string componentId, out string method, out string[] arguments))
        {
            Component? component = World.NamedComponent(input.Sender, componentId);
            if (component is null) return;
            component.Rpc = new RpcContext(input.Sender, World.Tick);
            EcsRegistry.Generated(component)?.DispatchServerRpc(method, arguments);
        }
    }

    private void ApplyFieldWrite(InputCommandMessage input)
    {
        if (!WireCodec.TryDecodeFieldWrite(input.Payload.Span, out NetEntityId target, out string componentId, out string fieldId, out string value)) return;
        if (target != input.Sender) { QueueCorrection(input.Sender, target, componentId, fieldId); return; }
        Component? component = World.NamedComponent(target, componentId);
        if (component is null) return;
        if (!TryGetSyncField(component, fieldId, out ISyncField field))
        {
            object? containerValue = EcsRegistry.Generated(component)?.ReadField(fieldId);
            if (containerValue is not ISyncContainer container || container.Authority != Authority.Owner)
            {
                if (containerValue is ISyncContainer) QueueCorrection(input.Sender, target, componentId, fieldId);
                return;
            }
            object? oldContainer = container.BoxedValue;
            try { container.AssignFromRemote(WireCodec.ParseContainerText(container, value)); }
            catch (FormatException) { QueueCorrection(input.Sender, target, componentId, fieldId); return; }
            World.Dirty.Add(new DirtyEntry(target, container, oldContainer, container.BoxedValue, ChangeReason.Sync, true));
            World.Revision++;
            return;
        }
        if (field.Authority != Authority.Owner) { QueueCorrection(input.Sender, target, componentId, fieldId); return; }
        IGeneratedComponent? generated = EcsRegistry.Generated(component);
        bool accepted = generated is null || generated.DispatchClientWrite(new SyncWrite(field, value));
        if (!accepted) { QueueCorrection(input.Sender, target, componentId, fieldId); return; }
        object? old = field.BoxedValue;
        generated?.WriteField(fieldId, ConvertValue(field.ValueType, value), silent: true);
        object? applied = EcsRegistry.Generated(component)?.ReadField(fieldId) ?? value;
        World.Dirty.Add(new DirtyEntry(target, field, old, applied, ChangeReason.Sync, true));
        World.Revision++;
    }

    private void QueueCorrection(NetEntityId observerId, NetEntityId entity, string componentId, string fieldId)
    {
        Component? component = World.NamedComponent(entity, componentId);
        if (component is null || !TryGetSyncField(component, fieldId, out ISyncField field)) return;
        World.PendingCorrections.Add(new FieldChange(entity, componentId, fieldId, field.BoxedValue, ChangeReason.Correction, observerId));
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
            EntityRecord record = World.Attach(id, order.EntityType, order.Components, true);
            record.CreatedTick = World.Tick;
            Appear(record, false);
            World.Revision++;
        }
        World.PendingCreates.Clear();
        for (int i = 0; i < World.PendingDestroys.Count; i++) Destroy(World.PendingDestroys[i]);
        World.PendingDestroys.Clear();
    }

    private void Appear(EntityRecord record, bool postAttribute)
    {
        if (record.Appeared) return;
        for (int i = 0; i < record.Components.Length; i++) record.Components[i].Awake();
        record.AwakeCalled = true;
        if (postAttribute)
        {
            for (int i = 0; i < record.Components.Length; i++) EcsRegistry.Generated(record.Components[i])?.InvokePostAttribute();
            record.PostAttributeCalled = true;
        }
        for (int i = 0; i < record.Components.Length; i++) { record.Components[i].OnEnable(); record.Components[i].Start(); }
        record.Started = true;
        record.Appeared = true;
    }

    private void Destroy(NetEntityId id)
    {
        EntityRecord? record = World.Record(id);
        if (record is null) return;
        for (int i = 0; i < record.Components.Length; i++) { record.Components[i].OnDisable(); record.Components[i].OnDestroy(); }
        World.DestroyedThisTick.Add(id);
        World.Detach(id);
    }

    private void Project()
    {
        var stampedRpcs = new List<ClientRpcRecord>(World.PendingRpcs.Count);
        for (int i = 0; i < World.PendingRpcs.Count; i++)
        {
            ClientRpcRecord rpc = World.PendingRpcs[i];
            stampedRpcs.Add(new ClientRpcRecord(rpc.Target, rpc.ComponentId, rpc.Method, rpc.Args, World.NextMessageId++, World.NextRoomSequence++, rpc.Sender, World.Tick, rpc.Scope));
        }

        foreach (ObserverComponent observer in World.Each<ObserverComponent>())
        {
            if (!observer.Connected) continue;
            NetEntityId observerId = observer.Entity;
            var rpcs = new List<ClientRpcRecord>(stampedRpcs.Count);
            for (int r = 0; r < stampedRpcs.Count; r++)
                if (Visible(stampedRpcs[r].Scope, null, null, stampedRpcs[r].Target, observerId)) rpcs.Add(stampedRpcs[r]);
            bool initial = !_initialCreateCursors.TryGetValue(observerId, out int marker) || marker >= 0;
            var creates = new List<CreateRecord>();
            int cursor = initial && marker >= 0 ? marker : 0;
            for (int i = 0; i < World.CreationOrder.Count; i++)
            {
                EntityRecord? record = World.Record(World.CreationOrder[i]);
                if (record is null || !record.Appeared) continue;
                if (!initial && record.CreatedTick <= observer.ProjectedTick) continue;
                if (!initial || i >= cursor)
                {
                    creates.Add(ToCreateRecord(record, observerId));
                    if (CreatesPerPack > 0 && initial && creates.Count >= CreatesPerPack) { cursor = i + 1; break; }
                }
            }

            bool fullComplete = !initial || CreatesPerPack <= 0 || cursor >= World.CreationOrder.Count;
            if (initial && CreatesPerPack > 0 && !fullComplete) _initialCreateCursors[observerId] = cursor;
            else if (initial) _initialCreateCursors[observerId] = -1;

            var fields = new List<FieldChange>();
            if (!initial)
            {
                for (int i = 0; i < World.Dirty.Count; i++)
                {
                    DirtyEntry dirty = World.Dirty[i];
                    if (dirty.SuppressWriterEcho && dirty.Entity == observerId) continue;
                    if (!World.IsLive(dirty.Entity)) continue;
                    if (dirty.Field is not null)
                    {
                        if (!Visible(dirty.Field, dirty.Entity, observerId)) continue;
                        fields.Add(new FieldChange(dirty.Entity, ComponentName(dirty.Field), FieldName(dirty.Field), dirty.NewValue, ChangeReason.Sync));
                    }
                    else if (dirty.Container is not null)
                    {
                        if (!Visible(dirty.Container, dirty.Container.Owner, dirty.Entity, observerId)) continue;
                        fields.Add(new FieldChange(dirty.Entity, ComponentName(dirty.Container), FieldName(dirty.Container), dirty.NewValue, ChangeReason.Sync));
                    }
                }
                for (int i = 0; i < World.PendingCorrections.Count; i++)
                {
                    FieldChange correction = World.PendingCorrections[i];
                    if (correction.ObserverId == observerId) fields.Add(correction);
                }
            }

            var destroys = new List<NetEntityId>(World.DestroyedThisTick);
            if (initial) _outbox.Add(new WelcomeMessage(World.InstanceId, observerId, observer.ConnectionGeneration, observerId.ToHex()));
            _outbox.Add(new WorldChangeMessage(World.Tick, creates, fields, destroys, rpcs, observerId: observerId));
            if (fullComplete) observer.ProjectedTick = World.Tick;
        }
        FireLocalHooks();
    }

    private CreateRecord ToCreateRecord(EntityRecord record, NetEntityId observerId)
    {
        var fields = new List<FieldValue>();
        for (int i = 0; i < record.Components.Length; i++) CollectVisibleFields(record.Components[i], observerId, record.Id, fields);
        return new CreateRecord(Registry.WireName(record.EntityType), record.Id, fields);
    }

    private void CollectVisibleFields(Component component, NetEntityId observerId, NetEntityId entityId, List<FieldValue> fields)
    {
        IGeneratedComponent? generated = EcsRegistry.Generated(component);
        if (generated is null) return;
        generated.CaptureSync(new VisibleFieldCollector(component, observerId, entityId, this, fields));
    }

    private bool Visible(ISyncField field, NetEntityId entityId, NetEntityId observerId)
    {
        return Visible(field.Scope, field.ClaimBy, field.Owner, entityId, observerId);
    }

    private bool Visible(ISyncContainer container, Component owner, NetEntityId entityId, NetEntityId observerId)
    {
        return Visible(container.Scope, container.ClaimBy, owner, entityId, observerId);
    }

    private bool Visible(Scope scope, string? claimBy, Component? owner, NetEntityId entityId, NetEntityId observerId)
    {
        if (scope == Scope.Room || scope == Scope.Aoi) return true;
        if (scope == Scope.Owner) return entityId == observerId;
        if (scope != Scope.Claim || string.IsNullOrEmpty(claimBy)) return false;
        if (owner is null) return false;
        Component? component = World.NamedComponent(entityId, owner.GetType().Name);
        object? claims = component is null ? null : EcsRegistry.Generated(component)?.ReadField(claimBy);
        return claims is SyncList<NetEntityId> list && list.Contains(observerId);
    }

    private void FireLocalHooks()
    {
        for (int i = 0; i < World.PendingHooks.Count; i++)
        {
            HookEntry hook = World.PendingHooks[i];
            EcsRegistry.Generated(hook.Owner)?.InvokeFieldChanging(hook.Ordinal, hook.OldValue, hook.NewValue, hook.Reason);
            EcsRegistry.Generated(hook.Owner)?.InvokeFieldChanged(hook.Ordinal, hook.OldValue, hook.NewValue, hook.Reason);
        }
    }

    private void ApplyClientBatch(List<WorldMessage> batch)
    {
        bool welcomeSeen = false;
        World.ApplyingRemote = true;
        try
        {
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch[i] is WelcomeMessage welcome)
                {
                    welcomeSeen = true;
                    World.InstanceId = welcome.InstanceId;
                    World.BindSelf(new Entity(World, welcome.Self));
                    _pendingWelcomeSelf = welcome.Self;
                    _pendingWelcomeGeneration = welcome.ConnectionGeneration;
                    continue;
                }
                if (batch[i] is ConnectionSupersededMessage superseded &&
                    World.NamedComponent(superseded.NetEntityId, nameof(ObserverComponent)) is ObserverComponent supersededObserver)
                {
                    supersededObserver.Connected = false;
                    supersededObserver.ConnectionGeneration = superseded.NewConnectionGeneration;
                    supersededObserver.DisconnectedAtTick = World.Tick;
                    continue;
                }
                if (batch[i] is WorldChangeMessage change)
                {
                    if (!welcomeSeen && World.InstanceId == 0) throw new InvalidOperationException("Welcome must be applied before WorldChange.");
                    ApplyWorldChange(change);
                }
            }
        }
        finally { World.ApplyingRemote = false; }
        if (!_pendingWelcomeSelf.IsDefault && World.IsLive(_pendingWelcomeSelf) &&
            World.NamedComponent(_pendingWelcomeSelf, nameof(ObserverComponent)) is ObserverComponent observer)
        {
            observer.Connected = true;
            observer.ConnectionGeneration = _pendingWelcomeGeneration;
            observer.DisconnectedAtTick = 0;
        }
    }

    private void ApplyWorldChange(WorldChangeMessage change)
    {
        for (int i = 0; i < change.Creates.Count; i++)
        {
            CreateRecord create = change.Creates[i];
            if (World.IsLive(create.NetEntityId)) continue;
            if (World.IsTombstoned(create.NetEntityId)) continue;
            if (!Registry.TryResolveEntityType(create.EntityType, out Type entityType)) throw new InvalidOperationException("Unknown entity type " + create.EntityType);
            EntityRecord record = World.Attach(create.NetEntityId, entityType, World.RentComponents(entityType), true);
            for (int c = 0; c < record.Components.Length; c++) record.Components[c].Awake();
            record.AwakeCalled = true;
            for (int f = 0; f < create.Fields.Count; f++)
            {
                FieldValue value = create.Fields[f];
                Component? component = World.NamedComponent(create.NetEntityId, value.ComponentId);
                if (component is not null)
                {
                    object? decoded = value.Value;
                    if (TryGetSyncField(component, value.FieldId, out ISyncField sync))
                        decoded = ConvertValue(sync.ValueType, value.Value);
                    else if (EcsRegistry.Generated(component)?.ReadField(value.FieldId) is ISyncContainer container)
                    {
                        container.AssignFromRemote(value.Value);
                        continue;
                    }
                    EcsRegistry.Generated(component)?.WriteField(value.FieldId, decoded, true);
                }
            }
            for (int c = 0; c < record.Components.Length; c++) EcsRegistry.Generated(record.Components[c])?.InvokePostAttribute();
            record.PostAttributeCalled = true;
            for (int c = 0; c < record.Components.Length; c++) { record.Components[c].OnEnable(); record.Components[c].Start(); }
            record.Started = true;
            record.Appeared = true;
        }

        var hooks = new List<HookEntry>();
        for (int i = 0; i < change.Fields.Count; i++)
        {
            FieldChange value = change.Fields[i];
            Component? component = World.NamedComponent(value.NetEntityId, value.ComponentId);
            if (component is null) continue;
            if (!TryGetSyncField(component, value.FieldId, out ISyncField field))
            {
                if (EcsRegistry.Generated(component)?.ReadField(value.FieldId) is ISyncContainer container)
                    container.AssignFromRemote(value.Value);
                continue;
            }
            object? old = field.BoxedValue;
            EcsRegistry.Generated(component)?.WriteField(value.FieldId, ConvertValue(field.ValueType, value.Value), true);
            if (value.Reason == ChangeReason.Correction || !Equals(old, value.Value)) hooks.Add(new HookEntry(component, field.Ordinal, old, value.Value, value.Reason));
        }
        for (int i = 0; i < change.Destroys.Count; i++) Destroy(change.Destroys[i]);
        for (int i = 0; i < hooks.Count; i++)
        {
            HookEntry hook = hooks[i];
            EcsRegistry.Generated(hook.Owner)?.InvokeFieldChanging(hook.Ordinal, hook.OldValue, hook.NewValue, hook.Reason);
            EcsRegistry.Generated(hook.Owner)?.InvokeFieldChanged(hook.Ordinal, hook.OldValue, hook.NewValue, hook.Reason);
        }
        for (int i = 0; i < change.Rpcs.Count; i++)
        {
            ClientRpcRecord rpc = change.Rpcs[i];
            Component? component = World.NamedComponent(rpc.Target, rpc.ComponentId);
            if (component is null) continue;
            component.Rpc = new RpcContext(rpc.Sender, rpc.AppliedTick);
            EcsRegistry.Generated(component)?.DispatchClientRpc(rpc.Method, new List<object?>(rpc.Args).ToArray());
        }
        World.Tick = Math.Max(World.Tick, change.Tick);
    }

    private void ConsumeSave()
    {
        if (World.PendingSaveSlot is null) return;
        string slot = World.PendingSaveSlot;
        World.PendingSaveSlot = null;
        byte[] bytes = WorldSnapshotCodec.Capture(World);
        SnapshotSink?.Write(slot, bytes);
    }

    private void ClearTickEphemera()
    {
        World.Dirty.Clear();
        World.PendingRpcs.Clear();
        World.PendingCorrections.Clear();
        World.PendingHooks.Clear();
        World.DestroyedThisTick.Clear();
    }

    private static bool TryGetSyncField(Component component, string fieldId, out ISyncField field)
    {
        if (component is IGeneratedSyncMetadata metadata && metadata.TryGetSyncField(fieldId, out field)) return true;
        field = null!;
        return false;
    }

    private static object? ConvertValue(Type type, object? value)
    {
        if (value is null || type.IsInstanceOfType(value)) return value;
        if (type == typeof(string)) return value.ToString() ?? string.Empty;
        if (type == typeof(ulong) && ulong.TryParse(value.ToString(), out ulong u)) return u;
        if (type == typeof(bool) && bool.TryParse(value.ToString(), out bool b)) return b;
        if (type == typeof(int) && int.TryParse(value.ToString(), out int i)) return i;
        return value;
    }

    private static string ComponentName(ISyncField field)
    {
        int dot = field.AttributeId.IndexOf('.');
        return dot > 0 ? field.AttributeId.Substring(0, dot) : field.Owner.GetType().Name;
    }

    private static string FieldName(ISyncField field)
    {
        int dot = field.AttributeId.IndexOf('.');
        return dot >= 0 ? field.AttributeId.Substring(dot + 1) : field.AttributeId;
    }

    private static string ComponentName(ISyncContainer container)
    {
        int dot = container.AttributeId.IndexOf('.');
        return dot > 0 ? container.AttributeId.Substring(0, dot) : container.Owner.GetType().Name;
    }

    private static string FieldName(ISyncContainer container)
    {
        int dot = container.AttributeId.IndexOf('.');
        return dot >= 0 ? container.AttributeId.Substring(dot + 1) : container.AttributeId;
    }

    private void PostOutbound(WorldMessage message)
    {
        if (World.IsServer) { Enqueue(message); return; }
        EnsureOwner();
        _outbox.Add(message);
    }

    private void EnsureOwner()
    {
        if (!_started || _ownerThread is null) throw new InvalidOperationException("WorldManager.Start must run before this operation.");
        if (Environment.CurrentManagedThreadId != _ownerThread.ManagedThreadId) throw new InvalidOperationException("WorldManager entry called from a non-owner thread.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorldManager));
    }

    private sealed class VisibleFieldCollector : IPersistWriter, IContainerFieldWriter
    {
        private readonly Component _component;
        private readonly NetEntityId _observer;
        private readonly NetEntityId _entity;
        private readonly WorldManager _manager;
        private readonly List<FieldValue> _fields;
        internal VisibleFieldCollector(Component component, NetEntityId observer, NetEntityId entity, WorldManager manager, List<FieldValue> fields)
        { _component = component; _observer = observer; _entity = entity; _manager = manager; _fields = fields; }
        public void WriteString(string attributeId, string? value) => Add(attributeId, value);
        public void WriteUInt64(string attributeId, ulong value) => Add(attributeId, value);
        public void WriteBoolean(string attributeId, bool value) => Add(attributeId, value);
        public void WriteContainer(string attributeId, object value) => Add(attributeId, value);
        private void Add(string attributeId, object? value)
        {
            string fieldName = FieldName(attributeId);
            if (TryGetSyncField(_component, fieldName, out ISyncField field))
            {
                if (!_manager.Visible(field, _entity, _observer)) return;
            }
            else if (value is not ISyncContainer container || !_manager.Visible(container, _component, _entity, _observer))
            {
                return;
            }

            int dot = attributeId.IndexOf('.');
            _fields.Add(new FieldValue(dot > 0 ? attributeId.Substring(0, dot) : _component.GetType().Name, dot >= 0 ? attributeId.Substring(dot + 1) : attributeId, value));
        }
        private static string FieldName(string id) { int dot = id.IndexOf('.'); return dot >= 0 ? id.Substring(dot + 1) : id; }
    }
}
