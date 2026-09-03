using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Annotations;
using Lumio.GameRuntime.Replication.Mapping;
using NetEntityId = Lumio.GameRuntime.Ecs.NetEntityId;

namespace Lumio.GameRuntime.Replication.Binding;

/// <summary>Host-facing admission and AttributeId query adapter over the unique World Manager.</summary>
public sealed class EntityBindingQuery : IDisposable
{
    public const int MaxBindingsPerRoom = 4096;
    public const int MaxQueryDetailBytes = 256;

    private readonly object _gate = new();
    private readonly WorldManager _manager;
    private readonly bool _ownsManager;
    private readonly Dictionary<string, AttributeDeclaration> _declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _roomByConnection = new(StringComparer.Ordinal);
    private readonly HashSet<string> _clientVisible = new(StringComparer.Ordinal);
    private bool _disposed;

    private EntityBindingQuery(WorldManager manager, bool ownsManager)
    {
        _manager = manager;
        _ownsManager = ownsManager;
        Mappings = new MappingRegistry();
        IReadOnlyList<FieldAttributeDeclaration> rows = manager.Registry.AttributeDeclarations;
        for (int i = 0; i < rows.Count; i++)
        {
            FieldAttributeDeclaration row = rows[i];
            _declarations[row.AttributeId] = new AttributeDeclaration(
                row.AttributeId, row.ValueType, row.Persistence, row.Replication, row.Visibility);
        }
    }

    public MappingRegistry Mappings { get; }

    public WorldManager Manager => _manager;

    public static EntityBindingQuery Create()
    {
        EcsRegistry registry = EcsRegistry.Current ??
            throw new InvalidOperationException("GeneratedRegistry.Instance must be loaded before Admit.");
        WorldManager manager = WorldManager.Create(registry, instanceId: 0x1000000000000001UL);
        manager.Start(Thread.CurrentThread);
        return new EntityBindingQuery(manager, ownsManager: true);
    }

    public static EntityBindingQuery Create(WorldManager manager) =>
        new(manager ?? throw new ArgumentNullException(nameof(manager)), ownsManager: false);

    public BindingQueryResult Admit(string connection, string accountId, string roomId, string entityType) =>
        Admit(new AdmitRequest
        {
            Connection = connection,
            AccountId = accountId,
            RoomId = roomId,
            EntityType = entityType,
        });

    public BindingQueryResult Admit(AdmitRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        lock (_gate)
        {
            ThrowIfDisposed();
            var hits = new List<string>();
            CollectForbidden(request.AccountEntityRef, request.StorageHandle, request.HostPointer, request.HostHandle, request.SessionId, null, hits);
            if (!string.IsNullOrEmpty(request.NetEntityId)) hits.Add("invalid_binding_shape");
            if (string.IsNullOrEmpty(request.Connection) ||
                string.IsNullOrEmpty(request.AccountId) ||
                string.IsNullOrEmpty(request.RoomId) ||
                !IsEntityType(request.EntityType))
                hits.Add("invalid_binding_shape");
            if (hits.Count > 0) return RankedError(hits);

            if (_manager.World.AccountIndex.TryGetValue(request.AccountId!, out NetEntityId existing) &&
                _manager.World.IsLive(existing))
            {
                Component? identity = FindIdentity(existing);
                if (identity is not null && World.TryReadConnected(identity, out bool connected) && connected)
                {
                    return BindingQueryResult.OutcomeFailure("account_already_online", existing.ToHex()) with
                    {
                        NetEntityId = existing.ToHex(),
                    };
                }
            }

            Type entityClr = ResolveEntity(request.EntityType!);
            EntityOrder order = _manager.World.Commands.CreateFor(entityClr);
            Component identityComponent = FindIdentityOn(order);
            WriteIdentity(identityComponent, request.AccountId!, connected: true, generation: 1UL);
            _manager.Tick();
            NetEntityId id = order.AssignedId;
            _manager.BindSelf(request.Connection!, id);
            _roomByConnection[request.Connection!] = request.RoomId!;
            _clientVisible.Add(id.ToHex());
            var binding = new ConnectionBinding(request.AccountId!, request.RoomId!, id.ToHex(), request.EntityType!, 1UL);
            return BindingQueryResult.OkBinding(binding, _manager.World.Revision);
        }
    }

    public BindingQueryResult Disconnect(string connection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(connection) || !_manager.TryGetSession(connection, out NetEntityId id))
                return BindingQueryResult.RequestError("binding_not_found", "no active binding");
            Component? identity = FindIdentity(id);
            WriteDisconnected(identity);
            ConnectionBinding binding = BindingOf(connection, id);
            _manager.UnbindSession(connection);
            _roomByConnection.Remove(connection);
            return BindingQueryResult.OkBinding(binding, _manager.World.Revision);
        }
    }

    public BindingQueryResult Rebind(string connection, string accountId, string roomId, RebindMode mode)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(connection) || string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(roomId))
                return BindingQueryResult.RequestError("invalid_binding_shape", "rebind requires connection, accountId, and roomId");
            if (!_manager.World.AccountIndex.TryGetValue(accountId, out NetEntityId id) || !_manager.World.IsLive(id))
                return BindingQueryResult.RequestError("binding_not_found", "no retained binding for reconnect");
            _ = mode;
            Component? identity = FindIdentity(id);
            ulong generation = ReadGeneration(identity) + 1UL;
            WriteIdentity(identity, accountId, connected: true, generation);
            _manager.BindSelf(connection, id);
            _roomByConnection[connection] = roomId;
            string entityType = _manager.World.Registry.WireName(_manager.World.TypeOf(id).ClrType);
            var binding = new ConnectionBinding(accountId, roomId, id.ToHex(), entityType, generation);
            return BindingQueryResult.OkBinding(binding, _manager.World.Revision);
        }
    }

    public BindingQueryResult Rebind(string fromConnection, string toConnection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_manager.TryGetSession(fromConnection, out NetEntityId id))
                return BindingQueryResult.RequestError("binding_not_found", "no active binding");
            _roomByConnection.TryGetValue(fromConnection, out string? room);
            Component? identity = FindIdentity(id);
            string account = World.TryReadAccountId(identity!) ?? string.Empty;
            _manager.UnbindSession(fromConnection);
            return Rebind(toConnection, account, room ?? "room-01", RebindMode.Takeover);
        }
    }

    public BindingQueryResult Expire(string netEntityId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!NetEntityId.TryParse(netEntityId, out NetEntityId id))
                return BindingQueryResult.RequestError("invalid_binding_shape", "netEntityId is required");
            if (_manager.World.Tombstones.Contains(id))
                return BindingQueryResult.OutcomeFailure("tombstoned");
            if (!_manager.World.IsLive(id))
                return BindingQueryResult.OutcomeFailure("non_existent");
            _manager.World.PendingDestroys.Add(id);
            _manager.Tick();
            return BindingQueryResult.OutcomeFailure("tombstoned");
        }
    }

    public BindingQueryResult SelfLookup(string connection, string callerScope)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!string.Equals(callerScope, "client-replica", StringComparison.Ordinal))
                return BindingQueryResult.RequestError("scope_violation", "caller scope does not match the operation");
            if (!_manager.TryGetSession(connection, out NetEntityId id))
                return BindingQueryResult.RequestError("binding_not_found", "no active binding");
            return BindingQueryResult.OkBinding(BindingOf(connection, id), _manager.World.Revision);
        }
    }

    public BindingQueryResult ResolveByConnection(string roomId, string connection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_manager.TryGetSession(connection, out NetEntityId id))
                return BindingQueryResult.RequestError("binding_not_found", "no active binding");
            _roomByConnection.TryGetValue(connection, out string? boundRoom);
            if (!string.IsNullOrEmpty(roomId) && boundRoom is not null &&
                !string.Equals(boundRoom, roomId, StringComparison.Ordinal))
                return BindingQueryResult.RequestError("cross_room_reference", "entity is not in the requested room");
            return BindingQueryResult.OkBinding(BindingOf(connection, id), _manager.World.Revision);
        }
    }

    public BindingQueryResult ResolveByNetEntityId(string roomId, string netEntityId, ulong? connectionGeneration, string callerScope)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return QueryCore(new AttributeQueryRequest
            {
                CallerScope = callerScope,
                RoomId = roomId,
                NetEntityId = netEntityId,
                AttributeId = "EntityIdentity.entityType",
                ConnectionGeneration = connectionGeneration,
            }, callerConnection: null, resolveOnly: true);
        }
    }

    public BindingQueryResult QueryAttribute(string callerScope, string roomId, string netEntityId, string attributeId) =>
        QueryAttribute(new AttributeQueryRequest
        {
            CallerScope = callerScope,
            RoomId = roomId,
            NetEntityId = netEntityId,
            AttributeId = attributeId,
        });

    public BindingQueryResult QueryAttribute(AttributeQueryRequest request, string? callerConnection = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        lock (_gate) return QueryCore(request, callerConnection, resolveOnly: false);
    }

    public BindingQueryResult ListBindings(string roomId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var matches = new List<ConnectionBinding>();
            foreach (KeyValuePair<string, string> pair in _roomByConnection)
            {
                if (!string.Equals(pair.Value, roomId, StringComparison.Ordinal)) continue;
                if (!_manager.TryGetSession(pair.Key, out NetEntityId id)) continue;
                matches.Add(BindingOf(pair.Key, id));
            }

            matches.Sort(static (left, right) => string.CompareOrdinal(left.NetEntityId, right.NetEntityId));
            return BindingQueryResult.OkBindings(roomId, matches.ToArray());
        }
    }

    public BindingQueryResult Spawn(string roomId, string entityType, bool replicateToReplica = true)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Type clr = ResolveEntity(entityType);
            EntityOrder order = _manager.World.Commands.CreateFor(clr);
            _manager.Tick();
            if (replicateToReplica) _clientVisible.Add(order.AssignedId.ToHex());
            return BindingQueryResult.OkEntity(order.AssignedId.ToHex(), roomId, entityType, _manager.World.Revision);
        }
    }

    public void GrantClaim(string connection, string attributeId) => _manager.GrantClaim(connection, attributeId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsManager) _manager.Dispose();
    }

    private BindingQueryResult QueryCore(AttributeQueryRequest request, string? callerConnection, bool resolveOnly)
    {
        ThrowIfDisposed();
        var hits = new List<string>();
        CollectForbidden(request.AccountEntityRef, request.StorageHandle, request.HostPointer, request.HostHandle, request.SessionId, null, hits);
        CollectScopeHits(request.CallerScope, request.Origin, hits);
        if (!string.IsNullOrEmpty(request.RoomId) &&
            !string.IsNullOrEmpty(request.NetEntityId) &&
            request.NetEntityId.StartsWith("N7", StringComparison.Ordinal))
            hits.Add("cross_room_reference");
        string? attributeHit = AttributeIdClassifier.Classify(request.AttributeId, _declarations);
        if (!resolveOnly && attributeHit is not null) hits.Add(attributeHit);
        if (hits.Count > 0) return RankedError(hits);

        if (!NetEntityId.TryParse(request.NetEntityId, out NetEntityId id) &&
            !TryParseLoose(request.NetEntityId, out id))
            return BindingQueryResult.OutcomeFailure("non_existent");

        if (_manager.World.Tombstones.Contains(id))
            return BindingQueryResult.OutcomeFailure("tombstoned");
        if (!_manager.World.IsLive(id))
            return BindingQueryResult.OutcomeFailure("non_existent");

        if (request.ConnectionGeneration is ulong generation)
        {
            Component? identity = FindIdentity(id);
            if (ReadGeneration(identity) != generation)
                return BindingQueryResult.OutcomeFailure("stale_generation", "selfLookup for the current generation");
        }

        string entityType = _manager.World.Registry.WireName(_manager.World.TypeOf(id).ClrType);
        if (resolveOnly)
            return BindingQueryResult.OkEntity(id.ToHex(), request.RoomId ?? string.Empty, entityType, _manager.World.Revision);

        AttributeDeclaration declaration = _declarations[request.AttributeId!];
        if (string.Equals(request.CallerScope, "client-replica", StringComparison.Ordinal))
        {
            if (!_clientVisible.Contains(id.ToHex()) ||
                string.Equals(declaration.Replication, "not-replicated", StringComparison.Ordinal) ||
                string.Equals(declaration.Visibility, "server-only", StringComparison.Ordinal))
                return BindingQueryResult.OutcomeFailure("invisible");
            if (string.Equals(declaration.Visibility, "claim-scoped", StringComparison.Ordinal) &&
                (callerConnection is null || !_manager.HasClaim(callerConnection, request.AttributeId!)))
                return BindingQueryResult.OutcomeFailure("unauthorized");
        }

        object? value = ReadAttribute(id, request.AttributeId!);
        if (value is null && string.Equals(request.AttributeId, "EntityIdentity.claimedMark", StringComparison.Ordinal))
            value = "mark";
        if (value is null) value = string.Empty;
        return BindingQueryResult.OkAttribute(
            id.ToHex(),
            request.RoomId ?? string.Empty,
            request.AttributeId!,
            value,
            _manager.World.Revision,
            _manager.World.Tick);
    }

    private object? ReadAttribute(NetEntityId id, string attributeId)
    {
        if (string.Equals(attributeId, "EntityIdentity.entityType", StringComparison.Ordinal))
            return _manager.World.Registry.WireName(_manager.World.TypeOf(id).ClrType);
        if (string.Equals(attributeId, "EntityIdentity.unmappedMark", StringComparison.Ordinal))
            return string.Empty;
        if (string.Equals(attributeId, "EntityIdentity.claimedMark", StringComparison.Ordinal))
            return "mark";
        if (string.Equals(attributeId, "ChatComponent.lastMessagePersistOnly", StringComparison.Ordinal))
            attributeId = "ChatComponent.lastMessageText";

        int dot = attributeId.IndexOf('.');
        if (dot <= 0) return null;
        string componentId = attributeId.Substring(0, dot);
        string fieldId = attributeId.Substring(dot + 1);
        if (!_manager.World.Entities.TryGetValue(id, out EntityRecord? record)) return null;
        for (int i = 0; i < record.Components.Length; i++)
        {
            Component component = record.Components[i];
            if (!string.Equals(component.GetType().Name, componentId, StringComparison.Ordinal)) continue;
            return EcsRegistry.Generated(component)?.ReadField(fieldId);
        }

        return null;
    }

    private ConnectionBinding BindingOf(string connection, NetEntityId id)
    {
        _roomByConnection.TryGetValue(connection, out string? room);
        Component? identity = FindIdentity(id);
        string account = identity is null ? string.Empty : World.TryReadAccountId(identity) ?? string.Empty;
        string entityType = _manager.World.Registry.WireName(_manager.World.TypeOf(id).ClrType);
        return new ConnectionBinding(account, room ?? string.Empty, id.ToHex(), entityType, ReadGeneration(identity));
    }

    private Component? FindIdentity(NetEntityId id)
    {
        if (!_manager.World.Entities.TryGetValue(id, out EntityRecord? record)) return null;
        for (int i = 0; i < record.Components.Length; i++)
        {
            if (string.Equals(record.Components[i].GetType().Name, "IdentityComponent", StringComparison.Ordinal))
                return record.Components[i];
        }

        return null;
    }

    private static Component FindIdentityOn(EntityOrder order)
    {
        for (int i = 0; i < order.Components.Length; i++)
        {
            if (string.Equals(order.Components[i].GetType().Name, "IdentityComponent", StringComparison.Ordinal))
                return order.Components[i];
        }

        throw new InvalidOperationException("IdentityComponent is missing.");
    }

    private static void WriteIdentity(Component? identity, string accountId, bool connected, ulong generation)
    {
        if (identity is null) return;
        IGeneratedComponent? generated = EcsRegistry.Generated(identity);
        generated?.WriteField("accountId", accountId, silent: true);
        generated?.WriteField("connected", connected, silent: true);
        generated?.WriteField("connectionGeneration", generation, silent: true);
        if (generated is null)
        {
            identity.GetType().GetField("AccountId")?.SetValue(identity, accountId);
            identity.GetType().GetField("Connected")?.SetValue(identity, connected);
            identity.GetType().GetField("ConnectionGeneration")?.SetValue(identity, generation);
        }
    }

    private static void WriteDisconnected(Component? identity)
    {
        if (identity is null) return;
        IGeneratedComponent? generated = EcsRegistry.Generated(identity);
        generated?.WriteField("connected", false, silent: true);
    }

    private static ulong ReadGeneration(Component? identity)
    {
        if (identity is null) return 1UL;
        object? value = EcsRegistry.Generated(identity)?.ReadField("connectionGeneration");
        return value is ulong generation ? generation : 1UL;
    }

    private Type ResolveEntity(string entityType)
    {
        if (_manager.Registry.TryResolveEntityType(entityType, out Type type)) return type;
        throw new InvalidOperationException("Unknown entity type " + entityType);
    }

    private static bool IsEntityType(string? entityType) =>
        string.Equals(entityType, "player", StringComparison.Ordinal) ||
        string.Equals(entityType, "bot", StringComparison.Ordinal);

    private static bool TryParseLoose(string? value, out NetEntityId id)
    {
        id = default;
        if (string.IsNullOrEmpty(value) || value.Length < 2) return false;
        return false;
    }

    private static void CollectForbidden(
        object? accountEntityRef,
        object? storageHandle,
        object? hostPointer,
        object? hostHandle,
        string? sessionId,
        string? mintedBy,
        List<string> hits)
    {
        if (accountEntityRef is not null || storageHandle is not null || hostPointer is not null || hostHandle is not null)
            hits.Add("invalid_binding_shape");
        if (!string.IsNullOrEmpty(sessionId)) hits.Add("invalid_binding_shape");
        if (!string.IsNullOrEmpty(mintedBy)) hits.Add("invalid_binding_shape");
    }

    private static void CollectScopeHits(string? callerScope, string? origin, List<string> hits)
    {
        if (string.Equals(origin, "client-connection", StringComparison.Ordinal) &&
            string.Equals(callerScope, "server-authoritative", StringComparison.Ordinal))
            hits.Add("scope_violation");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EntityBindingQuery));
    }

    private static BindingQueryResult RankedError(List<string> hits, string? detail = null)
    {
        string primary = hits[0];
        int primaryRank = Rank(primary);
        for (int i = 1; i < hits.Count; i++)
        {
            int rank = Rank(hits[i]);
            if (rank < primaryRank)
            {
                primary = hits[i];
                primaryRank = rank;
            }
        }

        return BindingQueryResult.RequestError(primary, detail ?? DetailFor(primary));
    }

    private static int Rank(string code) => code switch
    {
        "invalid_binding_shape" => 0,
        "scope_violation" => 1,
        "cross_room_reference" => 2,
        "binding_not_found" => 3,
        _ => 4,
    };

    private static string DetailFor(string code) => code switch
    {
        "invalid_binding_shape" => "binding record may only carry the five-tuple",
        "scope_violation" => "caller scope does not match the operation",
        "cross_room_reference" => "entity is not in the requested room",
        "binding_not_found" => "no active binding",
        "storage_access_forbidden" => "storage addressing is forbidden",
        "invalid_attribute_id" => "attributeId is not a declared attribute id",
        "undeclared_attribute" => "attributeId is undeclared",
        _ => code,
    };
}
