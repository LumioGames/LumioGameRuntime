using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Annotations;
using MappingRegistry = Lumio.GameRuntime.Replication.Mapping.MappingRegistry;

namespace Lumio.GameRuntime.Replication.Binding;

/// <summary>Admission and typed AttributeId queries over the single World Manager.</summary>
public sealed class EntityBindingQuery : IDisposable
{
    public const int MaxBindingsPerRoom = 4096;
    public const int MaxQueryDetailBytes = 256;
    private static readonly Regex AttributeIdPattern = new("^[A-Z][A-Za-z0-9]*\\.[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly WorldManager _manager;
    private readonly Dictionary<string, AttributeDeclaration> _declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NetEntityId> _connectionToEntity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _roomByConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingAdmission> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    private EntityBindingQuery(WorldManager manager)
    {
        _manager = manager;
        Mappings = new MappingRegistry();
        IReadOnlyList<FieldAttributeDeclaration> rows = manager.Registry.AttributeDeclarations;
        for (int i = 0; i < rows.Count; i++)
        {
            FieldAttributeDeclaration row = rows[i];
            _declarations[row.AttributeId] = new AttributeDeclaration(row.AttributeId, row.ValueType, row.Persistence, row.Replication, row.Visibility);
        }
    }

    public MappingRegistry Mappings { get; }
    public WorldManager Manager => _manager;

    public static EntityBindingQuery Create(WorldManager manager) => new(manager ?? throw new ArgumentNullException(nameof(manager)));

    public BindingQueryResult Admit(string connection, string accountId, string roomId, string entityType) => Admit(new AdmitRequest { Connection = connection, AccountId = accountId, RoomId = roomId, EntityType = entityType });

    public BindingQueryResult Admit(AdmitRequest request)
    {
            if (request is null) throw new ArgumentNullException(nameof(request));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_manager.IsOwnerThread) return BindingQueryResult.RequestError("owner_thread_required", "world mutation must run on the WorldManager owner thread");
            if (!ValidAdmission(request)) return BindingQueryResult.RequestError("invalid_binding_shape", "admission requires connection, account, room and entity type");
            if (string.Equals(request.EntityType, "bot", StringComparison.Ordinal))
                return BindingQueryResult.OutcomeFailure("bot_namespace_admission_forbidden");
            foreach (KeyValuePair<string, PendingAdmission> pending in _pending)
            {
                if (string.Equals(pending.Value.AccountId, request.AccountId, StringComparison.Ordinal))
                    return BindingQueryResult.OutcomeFailure("account_already_online");
                if (string.Equals(pending.Key, request.Connection, StringComparison.Ordinal))
                    return BindingQueryResult.OutcomeFailure("account_already_online");
            }
            if (_manager.World.TryGetAccount(request.AccountId!, out NetEntityId existing))
            {
                ObserverComponent observer = _manager.World.Get<ObserverComponent>(existing);
                if (observer.Connected) return BindingQueryResult.OutcomeFailure("account_already_online", existing.ToHex());
                _manager.Bind(existing);
                _connectionToEntity[request.Connection!] = existing;
                _roomByConnection[request.Connection!] = request.RoomId!;
                return new BindingQueryResult("accepted");
            }

            Type entityType = ResolveEntity(request.EntityType!);
            EntityOrder order = _manager.World.Commands.CreateFor(entityType);
            Component? identity = order.NamedComponent("IdentityComponent");
            Component? observerComponent = order.NamedComponent(nameof(ObserverComponent));
            if (identity is null || observerComponent is not ObserverComponent newObserver)
                return BindingQueryResult.RequestError("invalid_binding_shape", "entity type is not bindable");
            if (EcsRegistry.Generated(identity) is not IGeneratedComponent generated)
                return BindingQueryResult.RequestError("invalid_binding_shape", "identity component is not generated");
            generated.WriteField("accountId", request.AccountId!, silent: true);
            newObserver.Connected = true;
            newObserver.ConnectionGeneration = 1;
            _pending[request.Connection!] = new PendingAdmission(request.AccountId!, request.RoomId!, request.Connection!, order);
            return new BindingQueryResult("accepted");
        }
    }

    public BindingQueryResult Disconnect(string connection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_manager.IsOwnerThread) return BindingQueryResult.RequestError("owner_thread_required", "world mutation must run on the WorldManager owner thread");
            SynchronizePending();
            if (!_connectionToEntity.TryGetValue(connection, out NetEntityId id)) return BindingQueryResult.RequestError("binding_not_found", "no active binding");
            _manager.Unbind(id);
            _connectionToEntity.Remove(connection);
            _roomByConnection.Remove(connection);
            return BindingQueryResult.OutcomeFailure("accepted");
        }
    }

    public BindingQueryResult Rebind(string connection, string accountId, string roomId, RebindMode mode)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_manager.IsOwnerThread) return BindingQueryResult.RequestError("owner_thread_required", "world mutation must run on the WorldManager owner thread");
            SynchronizePending();
            if (!_manager.World.TryGetAccount(accountId, out NetEntityId id) || !_manager.World.IsLive(id)) return BindingQueryResult.RequestError("binding_not_found", "no retained binding");
            _ = mode;
            _manager.Bind(id);
            _connectionToEntity[connection] = id;
            _roomByConnection[connection] = roomId;
            return BindingQueryResult.OutcomeFailure("accepted");
        }
    }

    public BindingQueryResult Rebind(string fromConnection, string toConnection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_manager.IsOwnerThread) return BindingQueryResult.RequestError("owner_thread_required", "world mutation must run on the WorldManager owner thread");
            SynchronizePending();
            if (!_connectionToEntity.TryGetValue(fromConnection, out NetEntityId id)) return BindingQueryResult.RequestError("binding_not_found", "no active binding");
            _roomByConnection.TryGetValue(fromConnection, out string? room);
            _manager.Bind(id);
            _connectionToEntity.Remove(fromConnection);
            _connectionToEntity[toConnection] = id;
            _roomByConnection.Remove(fromConnection);
            _roomByConnection[toConnection] = room ?? string.Empty;
            return BindingQueryResult.OutcomeFailure("accepted");
        }
    }

    public BindingQueryResult Expire(string netEntityId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_manager.IsOwnerThread) return BindingQueryResult.RequestError("owner_thread_required", "world mutation must run on the WorldManager owner thread");
            SynchronizePending();
            if (!NetEntityId.TryParse(netEntityId, out NetEntityId id)) return BindingQueryResult.RequestError("invalid_binding_shape", "netEntityId is required");
            if (_manager.World.IsTombstoned(id)) return BindingQueryResult.OutcomeFailure("tombstoned");
            if (!_manager.World.IsLive(id)) return BindingQueryResult.OutcomeFailure("non_existent");
            _manager.World.QueueDestroy(id);
            return BindingQueryResult.OutcomeFailure("accepted");
        }
    }

    public BindingQueryResult SelfLookup(string connection, string callerScope)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SynchronizePending();
            if (callerScope != "client-replica") return BindingQueryResult.RequestError("scope_violation", "caller scope does not match operation");
            if (!_connectionToEntity.TryGetValue(connection, out NetEntityId id)) return BindingQueryResult.RequestError("binding_not_found", "no active binding");
            return BindingQueryResult.OkBinding(BindingOf(connection, id), _manager.World.Revision);
        }
    }

    public BindingQueryResult ResolveByConnection(string roomId, string connection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SynchronizePending();
            if (!_connectionToEntity.TryGetValue(connection, out NetEntityId id)) return BindingQueryResult.RequestError("binding_not_found", "no active binding");
            if (_roomByConnection.TryGetValue(connection, out string? room) && room != roomId) return BindingQueryResult.RequestError("cross_room_reference", "entity is not in requested room");
            return BindingQueryResult.OkBinding(BindingOf(connection, id), _manager.World.Revision);
        }
    }

    public BindingQueryResult ResolveByNetEntityId(string roomId, string netEntityId, ulong? generation, string callerScope)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return QueryCore(new AttributeQueryRequest { CallerScope = callerScope, RoomId = roomId, NetEntityId = netEntityId, AttributeId = "EntityIdentity.entityType", ConnectionGeneration = generation }, null, true);
        }
    }

    public BindingQueryResult QueryAttribute(string callerScope, string roomId, string netEntityId, string attributeId) => QueryAttribute(new AttributeQueryRequest { CallerScope = callerScope, RoomId = roomId, NetEntityId = netEntityId, AttributeId = attributeId });

    public BindingQueryResult QueryAttribute(AttributeQueryRequest request, string? callerConnection = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        lock (_gate) { ThrowIfDisposed(); SynchronizePending(); return QueryCore(request, callerConnection, false); }
    }

    public BindingQueryResult Spawn(string roomId, string entityType, bool replicateToReplica = true)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_manager.IsOwnerThread) return BindingQueryResult.RequestError("owner_thread_required", "world mutation must run on the WorldManager owner thread");
            Type type = ResolveEntity(entityType);
            EntityOrder order = _manager.World.Commands.CreateFor(type);
            _ = replicateToReplica;
            return BindingQueryResult.OkEntity(string.Empty, roomId, entityType, _manager.World.Revision);
        }
    }

    internal bool TryResolveConnection(string connection, out NetEntityId id)
    {
        lock (_gate) { SynchronizePending(); return _connectionToEntity.TryGetValue(connection, out id); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    private BindingQueryResult QueryCore(AttributeQueryRequest request, string? callerConnection, bool resolveOnly)
    {
        if (string.Equals(request.CallerScope, "server-authoritative", StringComparison.Ordinal) &&
            request.Origin is not null && !string.Equals(request.Origin, "server", StringComparison.Ordinal))
            return BindingQueryResult.RequestError("scope_violation", "server-authoritative reads are server-side only");
        if (!NetEntityId.TryParse(request.NetEntityId, out NetEntityId id)) return BindingQueryResult.RequestError("invalid_binding_shape", "netEntityId must be 128-bit hex");
        if (_manager.World.IsTombstoned(id)) return BindingQueryResult.OutcomeFailure("tombstoned");
        if (!_manager.World.IsLive(id)) return BindingQueryResult.OutcomeFailure("non_existent");
        if (!string.IsNullOrEmpty(request.RoomId))
        {
            foreach (KeyValuePair<string, string> pair in _roomByConnection)
            {
                if (_connectionToEntity.TryGetValue(pair.Key, out NetEntityId bound) && bound == id &&
                    !string.Equals(pair.Value, request.RoomId, StringComparison.Ordinal))
                    return BindingQueryResult.RequestError("cross_room_reference", "entity is not in the requested room");
            }
        }
        if (request.ConnectionGeneration.HasValue && _manager.World.NamedComponent(id, nameof(ObserverComponent)) is ObserverComponent observer && observer.ConnectionGeneration != request.ConnectionGeneration.Value) return BindingQueryResult.OutcomeFailure("stale_generation");
        if (resolveOnly) return BindingQueryResult.OkEntity(id.ToHex(), request.RoomId ?? string.Empty, _manager.World.Registry.WireName(_manager.World.TypeOf(id).ClrType), _manager.World.Revision);
        string attributeId = request.AttributeId ?? string.Empty;
        if (attributeId.StartsWith("Storage.", StringComparison.OrdinalIgnoreCase) || attributeId.Contains('/') || attributeId.Contains('\\'))
            return BindingQueryResult.RequestError("storage_access_forbidden", "storage paths are not valid attribute ids");
        if (attributeId.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || !AttributeIdPattern.IsMatch(attributeId)) return BindingQueryResult.RequestError("invalid_attribute_id", "attributeId must be Component.field");
        if (!_declarations.TryGetValue(attributeId, out AttributeDeclaration declaration)) return BindingQueryResult.RequestError("undeclared_attribute", "attribute is not declared");
        if (request.CallerScope == "client-replica" && declaration.Replication != "replicated") return BindingQueryResult.OutcomeFailure("invisible");
        int dot = attributeId.IndexOf('.');
        string componentName = attributeId.Substring(0, dot);
        string fieldName = attributeId.Substring(dot + 1);
        Component? component = _manager.World.NamedComponent(id, componentName);
        if (component is null) return BindingQueryResult.OutcomeFailure("undeclared_attribute");
        if (!TryGetSyncField(component, fieldName, out ISyncField field))
        {
            object? privateValue = EcsRegistry.Generated(component)?.ReadField(fieldName);
            return request.CallerScope == "server-authoritative" && privateValue is not null ? BindingQueryResult.OkAttribute(id.ToHex(), request.RoomId ?? string.Empty, attributeId, privateValue, _manager.World.Revision, _manager.World.Tick) : BindingQueryResult.OutcomeFailure("invisible");
        }
        if (request.CallerScope == "client-replica" && field.Scope == Scope.Owner && (callerConnection is null || !_connectionToEntity.TryGetValue(callerConnection, out NetEntityId owner) || owner != id)) return BindingQueryResult.OutcomeFailure("invisible");
        if (request.CallerScope == "client-replica" && field.Scope == Scope.Claim)
        {
            if (callerConnection is null || !_connectionToEntity.ContainsKey(callerConnection)) return BindingQueryResult.OutcomeFailure("unauthorized");
            Component? ownerComponent = _manager.World.NamedComponent(id, componentName);
            object? claims = ownerComponent is null ? null : EcsRegistry.Generated(ownerComponent)?.ReadField(field.ClaimBy ?? string.Empty);
            if (claims is not SyncList<NetEntityId> claimList || !_connectionToEntity.TryGetValue(callerConnection, out NetEntityId claimant) || !claimList.Contains(claimant))
                return BindingQueryResult.OutcomeFailure("unauthorized");
        }
        return BindingQueryResult.OkAttribute(id.ToHex(), request.RoomId ?? string.Empty, attributeId, field.BoxedValue!, _manager.World.Revision, _manager.World.Tick);
    }

    private ConnectionBinding BindingOf(string connection, NetEntityId id)
    {
        Component? identity = _manager.World.NamedComponent(id, "IdentityComponent");
        string account = World.TryReadAccountId(identity!) ?? string.Empty;
        ObserverComponent observer = _manager.World.Get<ObserverComponent>(id);
        return new ConnectionBinding(account, _roomByConnection.TryGetValue(connection, out string? room) ? room : string.Empty, id.ToHex(), _manager.World.Registry.WireName(_manager.World.TypeOf(id).ClrType), observer.ConnectionGeneration);
    }

    private void SynchronizePending()
    {
        var stale = new List<string>();
        foreach (KeyValuePair<string, NetEntityId> pair in _connectionToEntity)
            if (!_manager.World.IsLive(pair.Value)) stale.Add(pair.Key);
        for (int i = 0; i < stale.Count; i++)
        {
            _connectionToEntity.Remove(stale[i]);
            _roomByConnection.Remove(stale[i]);
        }

        if (_pending.Count == 0) return;
        var ready = new List<string>();
        foreach (KeyValuePair<string, PendingAdmission> pair in _pending)
        {
            if (pair.Value.Order.AssignedId.IsDefault) continue;
            NetEntityId id = pair.Value.Order.AssignedId;
            if (!_manager.World.IsLive(id)) continue;
            _connectionToEntity[pair.Key] = id;
            _roomByConnection[pair.Key] = pair.Value.RoomId;
            ready.Add(pair.Key);
        }
        for (int i = 0; i < ready.Count; i++) _pending.Remove(ready[i]);
    }

    private static bool ValidAdmission(AdmitRequest request) =>
        !string.IsNullOrEmpty(request.Connection) && !string.IsNullOrEmpty(request.AccountId) && !string.IsNullOrEmpty(request.RoomId) && IsEntityType(request.EntityType) && request.AccountEntityRef is null && request.StorageHandle is null && request.HostPointer is null && request.HostHandle is null && string.IsNullOrEmpty(request.NetEntityId) && string.IsNullOrEmpty(request.SessionId);

    private Type ResolveEntity(string name)
    {
        if (_manager.Registry.TryResolveEntityType(name, out Type type)) return type;
        throw new InvalidOperationException("Unknown entity type " + name);
    }

    private static bool IsEntityType(string? value) => value == "player" || value == "bot";
    private static bool TryGetSyncField(Component component, string fieldId, out ISyncField field)
    {
        if (component is IGeneratedSyncMetadata metadata && metadata.TryGetSyncField(fieldId, out field)) return true;
        field = null!;
        return false;
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(EntityBindingQuery)); }

    private readonly record struct PendingAdmission(string AccountId, string RoomId, string Connection, EntityOrder Order);
}
