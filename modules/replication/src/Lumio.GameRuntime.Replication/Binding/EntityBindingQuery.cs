using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Annotations;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;

namespace Lumio.GameRuntime.Replication.Binding;

public sealed class EntityBindingQuery : IDisposable
{
    public const int MaxBindingsPerRoom = 4096;
    public const int MaxQueryDetailBytes = 256;

    private const string EntityTypeAttribute = "EntityIdentity.entityType";
    private const string ClaimedAttribute = "EntityIdentity.claimedMark";
    private const string UnmappedMarkAttribute = "EntityIdentity.unmappedMark";
    private const string PersistOnlyAttribute = "ChatComponent.lastMessagePersistOnly";
    private const string LastMessageTextAttribute = "ChatComponent.lastMessageText";
    private const string LastMessageTickAttribute = "ChatComponent.lastMessageTick";

    private readonly object _gate = new();
    private readonly EcsModule _authoritativeModule;
    private readonly EcsModule _replicaModule;
    private readonly Dictionary<string, ConnectionBinding> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _liveConnectionByAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Occupancy> _entities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttributeDeclaration> _declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<AttributeKey, object> _values = new();
    private readonly Dictionary<string, HashSet<string>> _claims = new(StringComparer.Ordinal);
    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _retainedByAccount = new(StringComparer.Ordinal);
    private uint _nextLocalIndex = 1;
    private ulong _nextIdentitySequence = 1;
    private bool _disposed;

    private EntityBindingQuery(
        EcsModule authoritativeModule,
        EcsModule replicaModule,
        EcsWorld authoritativeWorld,
        EcsWorld replicaWorld,
        MappingRegistry mappings,
        NetEntityMappingTable identities,
        TombstoneRegistry tombstones)
    {
        _authoritativeModule = authoritativeModule;
        _replicaModule = replicaModule;
        AuthoritativeWorld = authoritativeWorld;
        ReplicaWorld = replicaWorld;
        Mappings = mappings;
        Identities = identities;
        Tombstones = tombstones;
        DeclareDefaults();
    }

    public EcsWorld AuthoritativeWorld { get; }

    public EcsWorld ReplicaWorld { get; }

    public MappingRegistry Mappings { get; }

    public NetEntityMappingTable Identities { get; }

    public TombstoneRegistry Tombstones { get; }

    public static EntityBindingQuery Create() => Create(identityTable: null);

    public static EntityBindingQuery Create(IdentityTableSnapshot? identityTable)
    {
        var authoritativeModule = new EcsModule();
        var replicaModule = new EcsModule();
        try
        {
            var budget = new EcsBudget(128, 128, 128, 4096);
            EcsWorldCreateResult authoritative = authoritativeModule.CreateWorld(new EcsWorldCreateRequest(new WorldId(1), budget));
            EcsWorldCreateResult replica = replicaModule.CreateWorld(new EcsWorldCreateRequest(new WorldId(2), budget));
            if (!authoritative.Created || authoritative.World is null || !replica.Created || replica.World is null)
                throw new InvalidOperationException("Failed to create authoritative and replica worlds.");

            StartWorld(authoritative.World);
            StartWorld(replica.World);

            var mappings = new MappingRegistry();
            MappingRegistrationResult entityTypeMapping = mappings.Register(
                MappingDescriptor.Create("mapping-entity-identity-entity-type", "EntityIdentity", "entityType"));
            MappingRegistrationResult claimedMapping = mappings.Register(
                MappingDescriptor.Create("mapping-entity-identity-claimed-mark", "EntityIdentity", "claimedMark"));
            if (!entityTypeMapping.Succeeded || !claimedMapping.Succeeded)
                throw new InvalidOperationException("Failed to register replicated attribute mappings.");

            var identityScope = new ReplicationStoreScope(1);
            var query = new EntityBindingQuery(
                authoritativeModule,
                replicaModule,
                authoritative.World,
                replica.World,
                mappings,
                new NetEntityMappingTable(identityScope, authoritative.World.WorldId),
                new TombstoneRegistry(identityScope));
            query.RestoreIdentityTable(identityTable);
            return query;
        }
        catch
        {
            authoritativeModule.Dispose();
            replicaModule.Dispose();
            throw;
        }
    }

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
            CollectForbidden(
                request.AccountEntityRef,
                request.StorageHandle,
                request.HostPointer,
                request.HostHandle,
                request.SessionId,
                hits);
            if (!string.IsNullOrEmpty(request.NetEntityId))
                hits.Add("invalid_binding_shape");
            if (string.IsNullOrEmpty(request.Connection) ||
                string.IsNullOrEmpty(request.AccountId) ||
                string.IsNullOrEmpty(request.RoomId) ||
                !IsEntityType(request.EntityType))
                hits.Add("invalid_binding_shape");
            CollectAccountConflict(request.AccountId, request.Connection, request.RoomId, restoringNetEntityId: null, hits);
            if (hits.Count > 0) return RankedError(hits);

            string roomId = request.RoomId!;
            if (CountLiveBindings(roomId) >= MaxBindingsPerRoom &&
                !_byConnection.ContainsKey(request.Connection!))
            {
                hits.Add("invalid_binding_shape");
                return RankedError(hits, "room binding capacity exceeded");
            }

            string netEntityId = AllocateNetEntityIdLocked();
            var binding = new ConnectionBinding(request.AccountId!, roomId, netEntityId, request.EntityType!, 1UL);
            AttachBinding(request.Connection!, binding);
            Occupancy occupancy = EnsureLiveEntity(netEntityId, roomId, request.EntityType!, 1UL, replicateToReplica: true);
            return BindingQueryResult.OkBinding(binding, occupancy.Revision);
        }
    }

    public BindingQueryResult Disconnect(string connection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(connection) || !_byConnection.TryGetValue(connection, out ConnectionBinding binding))
                return BindingQueryResult.RequestError("binding_not_found", "no active binding");

            _byConnection.Remove(connection);
            if (_liveConnectionByAccount.TryGetValue(binding.AccountId, out string? live) &&
                string.Equals(live, connection, StringComparison.Ordinal))
                _liveConnectionByAccount.Remove(binding.AccountId);
            _retainedByAccount[binding.AccountId] = binding.NetEntityId;
            _claims.Remove(connection);
            return BindingQueryResult.OkBinding(binding, _entities[binding.NetEntityId].Revision);
        }
    }

    public BindingQueryResult Rebind(string connection, string accountId, string roomId, RebindMode mode)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(connection) || string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(roomId))
                return BindingQueryResult.RequestError("invalid_binding_shape", "rebind requires connection, accountId, and roomId");

            if (mode == RebindMode.Takeover &&
                _liveConnectionByAccount.TryGetValue(accountId, out string? liveConnection) &&
                _byConnection.TryGetValue(liveConnection, out ConnectionBinding live))
            {
                if (!string.Equals(live.RoomId, roomId, StringComparison.Ordinal))
                    return BindingQueryResult.RequestError("cross_room_reference", "entity is not in the requested room");
                return RebindLocked(liveConnection, connection);
            }

            if (!_retainedByAccount.TryGetValue(accountId, out string? netEntityId) ||
                !_entities.TryGetValue(netEntityId, out Occupancy? occupancy) ||
                occupancy.Presence != OccupancyState.Live)
                return BindingQueryResult.RequestError("binding_not_found", "no retained binding for reconnect");
            if (!string.Equals(occupancy.RoomId, roomId, StringComparison.Ordinal))
                return BindingQueryResult.RequestError("cross_room_reference", "entity is not in the requested room");

            ulong nextGeneration = checked(occupancy.ConnectionGeneration + 1UL);
            occupancy.ConnectionGeneration = nextGeneration;
            occupancy.Revision++;
            occupancy.Tick++;
            var updated = new ConnectionBinding(accountId, roomId, netEntityId, occupancy.EntityType, nextGeneration);
            AttachBinding(connection, updated);
            _retainedByAccount.Remove(accountId);
            return BindingQueryResult.OkBinding(updated, occupancy.Revision);
        }
    }

    public BindingQueryResult Expire(string netEntityId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(netEntityId))
                return BindingQueryResult.RequestError("invalid_binding_shape", "netEntityId is required");
            if (!_entities.TryGetValue(netEntityId, out Occupancy? occupancy))
                return IsRetired(netEntityId)
                    ? BindingQueryResult.OutcomeFailure("tombstoned")
                    : BindingQueryResult.OutcomeFailure("non_existent");
            return DestroyLocked(occupancy.RoomId, netEntityId);
        }
    }

    public BindingQueryResult ListBindings(string roomId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(roomId))
                return BindingQueryResult.RequestError("invalid_binding_shape", "roomId is required");

            var matches = new List<ConnectionBinding>();
            foreach (ConnectionBinding binding in _byConnection.Values)
            {
                if (string.Equals(binding.RoomId, roomId, StringComparison.Ordinal))
                    matches.Add(binding);
            }

            matches.Sort(static (left, right) => string.CompareOrdinal(left.NetEntityId, right.NetEntityId));
            return BindingQueryResult.OkBindings(roomId, matches.ToArray());
        }
    }

    public IdentityTableSnapshot CaptureIdentityTable()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var records = new List<IssuedNetEntity>();
            foreach (string id in _issued)
            {
                string roomId = string.Empty;
                string entityType = "player";
                bool tombstoned = IsRetired(id);
                if (_entities.TryGetValue(id, out Occupancy? occupancy))
                {
                    roomId = occupancy.RoomId;
                    entityType = occupancy.EntityType;
                    tombstoned = occupancy.Presence == OccupancyState.Tombstoned || tombstoned;
                }

                records.Add(new IssuedNetEntity(id, roomId, entityType, tombstoned));
            }

            records.Sort(static (left, right) => string.CompareOrdinal(left.NetEntityId, right.NetEntityId));
            return new IdentityTableSnapshot(records);
        }
    }

    public BindingQueryResult Bind(string connection, BindingRecordRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        lock (_gate)
        {
            ThrowIfDisposed();
            var hits = new List<string>();
            CollectForbidden(
                request.AccountEntityRef,
                request.StorageHandle,
                request.HostPointer,
                request.HostHandle,
                request.SessionId,
                hits);
            if (string.IsNullOrEmpty(connection) || !IsValidBindingShape(request))
            {
                hits.Add("invalid_binding_shape");
                return RankedError(hits);
            }

            if (hits.Count > 0) return RankedError(hits);

            string connectionId = connection;
            string accountId = request.AccountId!;
            string roomId = request.RoomId!;
            string netEntityId = request.NetEntityId!;
            string entityType = request.EntityType!;
            ulong generation = request.ConnectionGeneration ?? 1UL;

            if (NetEntityId.TryParse(netEntityId, out _))
            {
                if (IsRetired(netEntityId))
                    return BindingQueryResult.OutcomeFailure("tombstoned");
                if (!_issued.Contains(netEntityId))
                {
                    hits.Add("invalid_binding_shape");
                    return RankedError(hits, "netEntityId is not registered in the identity table");
                }
            }

            CollectAccountConflict(accountId, connectionId, roomId, netEntityId, hits);
            if (hits.Count > 0) return RankedError(hits, "account already has an active room binding");

            if (CountLiveBindings(roomId) >= MaxBindingsPerRoom &&
                !_byConnection.ContainsKey(connectionId))
            {
                hits.Add("invalid_binding_shape");
                return RankedError(hits, "room binding capacity exceeded");
            }

            if (IsRetired(netEntityId))
                return BindingQueryResult.OutcomeFailure("tombstoned");

            var binding = new ConnectionBinding(accountId, roomId, netEntityId, entityType, generation);
            AttachBinding(connectionId, binding);
            Occupancy occupancy = EnsureLiveEntity(netEntityId, roomId, entityType, generation, replicateToReplica: true);
            return BindingQueryResult.OkBinding(binding, occupancy.Revision);
        }
    }

    public BindingQueryResult Rebind(string fromConnection, string toConnection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(fromConnection) || string.IsNullOrEmpty(toConnection))
                return BindingQueryResult.RequestError("invalid_binding_shape", "connection refs are required");
            return RebindLocked(fromConnection, toConnection);
        }
    }

    public BindingQueryResult SelfLookup(string connection, string callerScope)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var hits = new List<string>();
            if (!string.Equals(callerScope, "client-replica", StringComparison.Ordinal))
                hits.Add("scope_violation");
            if (!_byConnection.TryGetValue(connection ?? string.Empty, out ConnectionBinding found))
                hits.Add("binding_not_found");
            if (hits.Count > 0) return RankedError(hits);
            return BindingQueryResult.OkBinding(found);
        }
    }

    public BindingQueryResult ResolveByConnection(string roomId, string connection)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!IsOwnerThread(AuthoritativeWorld))
                return BindingQueryResult.RequestError("scope_violation", "server resolve requires Simulation Owner Thread");
            if (!_byConnection.TryGetValue(connection ?? string.Empty, out ConnectionBinding binding) ||
                !string.Equals(binding.RoomId, roomId, StringComparison.Ordinal))
                return BindingQueryResult.RequestError("binding_not_found", "no active binding in this room");
            ProbeAuthoritativeStorage(_entities[binding.NetEntityId].LocalId);
            return BindingQueryResult.OkBinding(binding, _entities[binding.NetEntityId].Revision);
        }
    }

    public BindingQueryResult ResolveByNetEntityId(
        string roomId,
        string netEntityId,
        ulong? connectionGeneration,
        string callerScope)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var hits = new List<string>();
            CollectScopeHits(callerScope, origin: null, hits);
            if (RequiresAuthoritativeStorage(callerScope) && !IsOwnerThread(AuthoritativeWorld))
                hits.Add("scope_violation");
            if (IsClientReplica(callerScope) && !IsOwnerThread(ReplicaWorld))
                hits.Add("scope_violation");
            CollectCrossRoomHit(roomId, netEntityId, callerScope, hits);
            if (hits.Count > 0) return RankedError(hits);
            return ResolveOccupancy(roomId, netEntityId, connectionGeneration, callerScope);
        }
    }

    public BindingQueryResult QueryAttribute(AttributeQueryRequest request, string? callerConnection = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        lock (_gate)
        {
            ThrowIfDisposed();
            var hits = new List<string>();
            CollectForbidden(
                request.AccountEntityRef,
                request.StorageHandle,
                request.HostPointer,
                request.HostHandle,
                request.SessionId,
                hits);
            CollectScopeHits(request.CallerScope, request.Origin, hits);
            if (RequiresAuthoritativeStorage(request.CallerScope) && !IsOwnerThread(AuthoritativeWorld))
                hits.Add("scope_violation");
            if (IsClientReplica(request.CallerScope) && !IsOwnerThread(ReplicaWorld))
                hits.Add("scope_violation");

            bool mayReadOccupancy =
                (RequiresAuthoritativeStorage(request.CallerScope) && IsOwnerThread(AuthoritativeWorld)) ||
                (IsClientReplica(request.CallerScope) && IsOwnerThread(ReplicaWorld));
            if (mayReadOccupancy)
                CollectCrossRoomHit(request.RoomId, request.NetEntityId, request.CallerScope, hits);

            string? attributeHit = AttributeIdClassifier.Classify(request.AttributeId, _declarations);
            if (attributeHit is not null) hits.Add(attributeHit);
            if (hits.Count > 0) return RankedError(hits);

            BindingQueryResult occupancy = ResolveOccupancy(
                request.RoomId!,
                request.NetEntityId!,
                request.ConnectionGeneration,
                request.CallerScope!);
            if (occupancy.Outcome != "ok") return occupancy;

            Occupancy entity = _entities[request.NetEntityId!];
            AttributeDeclaration declaration = _declarations[request.AttributeId!];
            if (IsClientReplica(request.CallerScope))
            {
                if (!entity.InReplica ||
                    !IsMappedReplicated(declaration) ||
                    string.Equals(declaration.Visibility, "server-only", StringComparison.Ordinal))
                    return BindingQueryResult.OutcomeFailure("invisible");
                if (string.Equals(declaration.Visibility, "claim-scoped", StringComparison.Ordinal) &&
                    !HasClaim(callerConnection, request.AttributeId!))
                    return BindingQueryResult.OutcomeFailure("unauthorized");
            }

            if (!_values.TryGetValue(new AttributeKey(entity.RoomId, entity.NetEntityId, request.AttributeId!), out object? value))
                return BindingQueryResult.OutcomeFailure("non_existent");

            return BindingQueryResult.OkAttribute(
                entity.NetEntityId,
                entity.RoomId,
                request.AttributeId!,
                value,
                entity.Revision,
                entity.Tick);
        }
    }

    public BindingQueryResult Spawn(string roomId, string entityType, bool replicateToReplica = true)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(roomId) || !IsEntityType(entityType))
                return BindingQueryResult.RequestError("invalid_binding_shape", "spawn requires room and entityType");
            string netEntityId = AllocateNetEntityIdLocked();
            Occupancy occupancy = EnsureLiveEntity(netEntityId, roomId, entityType, connectionGeneration: 0, replicateToReplica);
            occupancy.InReplica = replicateToReplica;
            return BindingQueryResult.OkEntity(netEntityId, roomId, entityType, occupancy.Revision);
        }
    }

    public BindingQueryResult Spawn(string roomId, string netEntityId, string entityType, bool replicateToReplica = true)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(netEntityId) || !IsEntityType(entityType))
                return BindingQueryResult.RequestError("invalid_binding_shape", "spawn requires room, netEntityId, and entityType");
            if (IsRetired(netEntityId))
                return BindingQueryResult.OutcomeFailure("tombstoned");
            if (NetEntityId.TryParse(netEntityId, out _) && !_issued.Contains(netEntityId))
                return BindingQueryResult.RequestError("invalid_binding_shape", "netEntityId is not registered in the identity table");
            Occupancy occupancy = EnsureLiveEntity(netEntityId, roomId, entityType, connectionGeneration: 0, replicateToReplica);
            occupancy.InReplica = replicateToReplica;
            return BindingQueryResult.OkEntity(netEntityId, roomId, entityType, occupancy.Revision);
        }
    }

    public BindingQueryResult Destroy(string roomId, string netEntityId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return DestroyLocked(roomId, netEntityId);
        }
    }

    public BindingQueryResult Forget(string roomId, string netEntityId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string id = netEntityId ?? string.Empty;
            if (!_entities.TryGetValue(id, out Occupancy? occupancy) ||
                !string.Equals(occupancy.RoomId, roomId, StringComparison.Ordinal))
                return BindingQueryResult.OutcomeFailure("non_existent");

            string entityType = occupancy.EntityType;
            _retired.Add(id);
            _entities.Remove(id);
            ClearValues(roomId, id);
            if (NetEntityId.TryParse(id, out NetEntityId parsed))
            {
                IdentityStoreToken token = Identities.CaptureToken();
                Identities.Remove(parsed, token);
            }

            return BindingQueryResult.OkEntity(id, roomId, entityType, occupancy.Revision);
        }
    }

    public void GrantClaim(string connection, string attributeId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_claims.TryGetValue(connection, out HashSet<string>? held))
            {
                held = new HashSet<string>(StringComparer.Ordinal);
                _claims[connection] = held;
            }

            held.Add(attributeId);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _authoritativeModule.Dispose();
        _replicaModule.Dispose();
    }

    private BindingQueryResult ResolveOccupancy(
        string roomId,
        string netEntityId,
        ulong? connectionGeneration,
        string callerScope)
    {
        if (!_entities.TryGetValue(netEntityId, out Occupancy? occupancy))
            return BindingQueryResult.OutcomeFailure("non_existent");
        bool replicaUnknown = IsClientReplica(callerScope) && !occupancy.InReplica;
        if (!string.Equals(occupancy.RoomId, roomId, StringComparison.Ordinal))
        {
            if (replicaUnknown)
                return BindingQueryResult.OutcomeFailure("non_existent");
            return BindingQueryResult.RequestError("cross_room_reference", "entity is not in the requested room");
        }

        if (RequiresAuthoritativeStorage(callerScope))
            ProbeAuthoritativeStorage(occupancy.LocalId);

        if (occupancy.Presence == OccupancyState.Tombstoned)
            return BindingQueryResult.OutcomeFailure("tombstoned");
        if (connectionGeneration.HasValue && connectionGeneration.Value < occupancy.ConnectionGeneration)
            return BindingQueryResult.OutcomeFailure("stale_generation", "generation is stale; retry after selfLookup");
        if (IsClientReplica(callerScope) && !occupancy.InReplica)
            return BindingQueryResult.OutcomeFailure("invisible");

        return BindingQueryResult.OkEntity(occupancy.NetEntityId, occupancy.RoomId, occupancy.EntityType, occupancy.Revision);
    }

    private Occupancy EnsureLiveEntity(
        string netEntityId,
        string roomId,
        string entityType,
        ulong connectionGeneration,
        bool replicateToReplica)
    {
        if (_entities.TryGetValue(netEntityId, out Occupancy? occupancy))
        {
            if (occupancy.Presence == OccupancyState.Tombstoned || _retired.Contains(netEntityId))
                return occupancy;
            occupancy.Presence = OccupancyState.Live;
            occupancy.RoomId = roomId;
            occupancy.EntityType = entityType;
            occupancy.ConnectionGeneration = connectionGeneration;
            occupancy.InReplica = occupancy.InReplica || replicateToReplica;
            occupancy.Revision++;
            occupancy.Tick++;
            SeedAttributes(occupancy);
            return occupancy;
        }

        occupancy = new Occupancy(
            netEntityId,
            roomId,
            entityType,
            connectionGeneration,
            new LocalEntityId(_nextLocalIndex++, 1),
            replicateToReplica);
        _entities[netEntityId] = occupancy;
        SeedAttributes(occupancy);
        if (NetEntityId.TryParse(netEntityId, out NetEntityId parsed))
            Identities.TryBind(parsed, occupancy.LocalId, AuthoritativeWorld.WorldId);
        return occupancy;
    }

    private void SeedAttributes(Occupancy occupancy)
    {
        PutValue(occupancy, EntityTypeAttribute, occupancy.EntityType);
        PutValue(occupancy, ClaimedAttribute, "mark");
        PutValue(occupancy, PersistOnlyAttribute, "persisted");
        PutValue(occupancy, LastMessageTextAttribute, string.Empty);
        PutValue(occupancy, LastMessageTickAttribute, 0UL);
    }

    private void PutValue(Occupancy occupancy, string attributeId, object value) =>
        _values[new AttributeKey(occupancy.RoomId, occupancy.NetEntityId, attributeId)] = value;

    private void ClearValues(string roomId, string netEntityId)
    {
        var keys = new List<AttributeKey>();
        foreach (AttributeKey key in _values.Keys)
        {
            if (string.Equals(key.RoomId, roomId, StringComparison.Ordinal) &&
                string.Equals(key.NetEntityId, netEntityId, StringComparison.Ordinal))
                keys.Add(key);
        }

        for (int i = 0; i < keys.Count; i++) _values.Remove(keys[i]);
    }

    private void RetireLiveBinding(string netEntityId)
    {
        string? connection = null;
        foreach (KeyValuePair<string, ConnectionBinding> pair in _byConnection)
        {
            if (string.Equals(pair.Value.NetEntityId, netEntityId, StringComparison.Ordinal))
            {
                connection = pair.Key;
                break;
            }
        }

        if (connection is null) return;
        ConnectionBinding binding = _byConnection[connection];
        _byConnection.Remove(connection);
        if (_liveConnectionByAccount.TryGetValue(binding.AccountId, out string? live) &&
            string.Equals(live, connection, StringComparison.Ordinal))
            _liveConnectionByAccount.Remove(binding.AccountId);
        _retainedByAccount.Remove(binding.AccountId);
        _claims.Remove(connection);
    }

    private int CountLiveBindings(string roomId)
    {
        int count = 0;
        foreach (ConnectionBinding binding in _byConnection.Values)
        {
            if (string.Equals(binding.RoomId, roomId, StringComparison.Ordinal)) count++;
        }

        return count;
    }

    private void RestoreIdentityTable(IdentityTableSnapshot? snapshot)
    {
        if (snapshot is null) return;
        for (int i = 0; i < snapshot.Records.Count; i++)
        {
            IssuedNetEntity record = snapshot.Records[i];
            if (string.IsNullOrEmpty(record.NetEntityId)) continue;
            _issued.Add(record.NetEntityId);
            NoteIssuedSequence(record.NetEntityId);
            if (!record.Tombstoned) continue;

            string roomId = string.IsNullOrEmpty(record.RoomId) ? "room-01" : record.RoomId;
            string entityType = IsEntityType(record.EntityType) ? record.EntityType : "player";
            var occupancy = new Occupancy(
                record.NetEntityId,
                roomId,
                entityType,
                connectionGeneration: 1,
                new LocalEntityId(_nextLocalIndex++, 1),
                inReplica: true);
            occupancy.Presence = OccupancyState.Tombstoned;
            _entities[record.NetEntityId] = occupancy;
            _retired.Add(record.NetEntityId);
            if (NetEntityId.TryParse(record.NetEntityId, out NetEntityId parsed))
                Tombstones.Add(parsed, occupancy.Revision, Identities.CaptureToken());
        }
    }

    private string AllocateNetEntityIdLocked()
    {
        while (true)
        {
            ulong sequence = _nextIdentitySequence;
            if (_nextIdentitySequence < ulong.MaxValue) _nextIdentitySequence++;
            string id = sequence.ToString("x32", CultureInfo.InvariantCulture);
            if (_issued.Add(id)) return id;
            if (sequence == ulong.MaxValue)
                throw new InvalidOperationException("NetEntityId space is exhausted.");
        }
    }

    private void NoteIssuedSequence(string netEntityId)
    {
        if (!ulong.TryParse(netEntityId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
            return;
        if (value >= _nextIdentitySequence && value < ulong.MaxValue)
            _nextIdentitySequence = value + 1UL;
    }

    private void AttachBinding(string connection, ConnectionBinding binding)
    {
        _byConnection[connection] = binding;
        _liveConnectionByAccount[binding.AccountId] = connection;
        _retainedByAccount.Remove(binding.AccountId);
    }

    private void CollectAccountConflict(
        string? accountId,
        string? connection,
        string? roomId,
        string? restoringNetEntityId,
        List<string> hits)
    {
        if (string.IsNullOrEmpty(accountId)) return;
        if (_liveConnectionByAccount.TryGetValue(accountId, out string? liveConnection) &&
            _byConnection.TryGetValue(liveConnection, out ConnectionBinding live) &&
            !string.Equals(liveConnection, connection, StringComparison.Ordinal))
        {
            hits.Add(string.Equals(live.RoomId, roomId, StringComparison.Ordinal)
                ? "invalid_binding_shape"
                : "cross_room_reference");
            return;
        }

        if (_retainedByAccount.TryGetValue(accountId, out string? retainedId) &&
            _entities.TryGetValue(retainedId, out Occupancy? occupancy) &&
            occupancy.Presence == OccupancyState.Live &&
            !string.Equals(retainedId, restoringNetEntityId, StringComparison.Ordinal))
        {
            hits.Add(string.Equals(occupancy.RoomId, roomId, StringComparison.Ordinal)
                ? "invalid_binding_shape"
                : "cross_room_reference");
        }
    }

    private BindingQueryResult RebindLocked(string fromConnection, string toConnection)
    {
        if (!_byConnection.TryGetValue(fromConnection, out ConnectionBinding current))
            return BindingQueryResult.RequestError("binding_not_found", "no active binding");

        ulong nextGeneration = checked(current.ConnectionGeneration + 1UL);
        var updated = current with { ConnectionGeneration = nextGeneration };
        _byConnection.Remove(fromConnection);
        _byConnection[toConnection] = updated;
        _liveConnectionByAccount[current.AccountId] = toConnection;
        _retainedByAccount.Remove(current.AccountId);
        if (_claims.TryGetValue(fromConnection, out HashSet<string>? claims))
        {
            _claims.Remove(fromConnection);
            _claims[toConnection] = claims;
        }

        Occupancy occupancy = _entities[current.NetEntityId];
        occupancy.ConnectionGeneration = nextGeneration;
        occupancy.Revision++;
        occupancy.Tick++;
        return BindingQueryResult.OkBinding(updated, occupancy.Revision);
    }

    private BindingQueryResult DestroyLocked(string roomId, string netEntityId)
    {
        string id = netEntityId ?? string.Empty;
        if (!_entities.TryGetValue(id, out Occupancy? occupancy) ||
            occupancy.Presence != OccupancyState.Live ||
            !string.Equals(occupancy.RoomId, roomId, StringComparison.Ordinal))
            return BindingQueryResult.OutcomeFailure("non_existent");

        occupancy.Presence = OccupancyState.Tombstoned;
        occupancy.Revision++;
        _retired.Add(id);
        _issued.Add(id);
        RetireLiveBinding(id);
        if (NetEntityId.TryParse(id, out NetEntityId parsed))
        {
            IdentityStoreToken token = Identities.CaptureToken();
            Identities.Remove(parsed, token);
            Tombstones.Add(parsed, occupancy.Revision, token);
        }

        return BindingQueryResult.OkEntity(id, roomId, occupancy.EntityType, occupancy.Revision);
    }

    private void CollectCrossRoomHit(string? roomId, string? netEntityId, string? callerScope, List<string> hits)
    {
        if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(netEntityId)) return;
        if (!_entities.TryGetValue(netEntityId, out Occupancy? occupancy)) return;
        if (IsClientReplica(callerScope) && !occupancy.InReplica)
            return;
        if (!string.Equals(occupancy.RoomId, roomId, StringComparison.Ordinal))
            hits.Add("cross_room_reference");
    }

    private static void CollectForbidden(
        object? accountEntityRef,
        object? storageHandle,
        object? hostPointer,
        object? hostHandle,
        string? sessionId,
        List<string> hits)
    {
        if (accountEntityRef is not null ||
            storageHandle is not null ||
            hostPointer is not null ||
            hostHandle is not null ||
            !string.IsNullOrEmpty(sessionId))
            hits.Add("invalid_binding_shape");
    }

    private static void CollectScopeHits(string? callerScope, string? origin, List<string> hits)
    {
        bool server = string.Equals(callerScope, "server-authoritative", StringComparison.Ordinal);
        bool client = string.Equals(callerScope, "client-replica", StringComparison.Ordinal);
        if (!server && !client) hits.Add("scope_violation");
        if (server && string.Equals(origin, "client-connection", StringComparison.Ordinal))
            hits.Add("scope_violation");
    }

    private static bool IsValidBindingShape(BindingRecordRequest request) =>
        !string.IsNullOrEmpty(request.AccountId) &&
        !string.IsNullOrEmpty(request.RoomId) &&
        !string.IsNullOrEmpty(request.NetEntityId) &&
        IsEntityType(request.EntityType) &&
        request.ConnectionGeneration.GetValueOrDefault(1UL) >= 1UL;

    private static bool IsEntityType(string? entityType) =>
        string.Equals(entityType, "player", StringComparison.Ordinal) ||
        string.Equals(entityType, "bot", StringComparison.Ordinal);

    private static bool RequiresAuthoritativeStorage(string? callerScope) =>
        string.Equals(callerScope, "server-authoritative", StringComparison.Ordinal);

    private static bool IsClientReplica(string? callerScope) =>
        string.Equals(callerScope, "client-replica", StringComparison.Ordinal);

    private bool HasClaim(string? connection, string attributeId) =>
        connection is not null &&
        _claims.TryGetValue(connection, out HashSet<string>? held) &&
        held.Contains(attributeId);

    private bool IsRetired(string netEntityId)
    {
        if (_retired.Contains(netEntityId)) return true;
        if (_entities.TryGetValue(netEntityId, out Occupancy? occupancy) &&
            occupancy.Presence == OccupancyState.Tombstoned)
            return true;
        return NetEntityId.TryParse(netEntityId, out NetEntityId parsed) && Tombstones.Contains(parsed);
    }

    private bool IsMappedReplicated(AttributeDeclaration declaration)
    {
        if (!string.Equals(declaration.Replication, "replicated", StringComparison.Ordinal))
            return false;
        int dot = declaration.AttributeId.IndexOf('.');
        if (dot <= 0) return false;
        string component = declaration.AttributeId.Substring(0, dot);
        string field = declaration.AttributeId.Substring(dot + 1);
        foreach (MappingDescriptor mapping in Mappings.View.Mappings)
        {
            if (string.Equals(mapping.Source.Component, component, StringComparison.Ordinal) &&
                string.Equals(mapping.Source.Field, field, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void ProbeAuthoritativeStorage(LocalEntityId localId) =>
        AuthoritativeWorld.TryResolve(AuthoritativeWorld, localId, out _);

    private static bool IsOwnerThread(EcsWorld world) =>
        world.OwnerThreadId != 0 && world.OwnerThreadId == Environment.CurrentManagedThreadId;

    private static void StartWorld(EcsWorld world)
    {
        if (world.BeginRegistration().Status != StorageOperationStatus.Accepted ||
            world.MarkReady().Status != StorageOperationStatus.Accepted ||
            world.Start().Status != StorageOperationStatus.Accepted)
            throw new InvalidOperationException("World failed to enter Running on the owner thread.");
    }

    private void DeclareDefaults()
    {
        IReadOnlyList<FieldAttributeDeclaration> rows = AttributeDeclarationCatalog.LoadEmbedded();
        for (int i = 0; i < rows.Count; i++)
        {
            FieldAttributeDeclaration row = rows[i];
            Declare(new AttributeDeclaration(row.AttributeId, row.ValueType, row.Persistence, row.Replication, row.Visibility));
        }
    }

    private void Declare(AttributeDeclaration declaration) => _declarations[declaration.AttributeId] = declaration;

    private void ThrowIfDisposed()
    {
#if NET10_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed) throw new ObjectDisposedException(nameof(EntityBindingQuery));
#endif
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

        var others = new List<string>();
        for (int i = 0; i < hits.Count; i++)
        {
            if (!string.Equals(hits[i], primary, StringComparison.Ordinal) && !others.Contains(hits[i]))
                others.Add(hits[i]);
        }

        string text = detail ?? DetailFor(primary);
        if (others.Count > 0) text = text + "; " + string.Join("; ", others.ToArray());
        return BindingQueryResult.RequestError(primary, ClipDetail(text));
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

    private static string ClipDetail(string detail)
    {
        if (Encoding.UTF8.GetByteCount(detail) <= MaxQueryDetailBytes) return detail;
        byte[] bytes = Encoding.UTF8.GetBytes(detail);
        int length = MaxQueryDetailBytes;
        while (length > 0 && (bytes[length - 1] & 0xC0) == 0x80) length--;
        if (length > 0 && (bytes[length - 1] & 0xC0) == 0xC0) length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private enum OccupancyState
    {
        Live,
        Tombstoned,
    }

    private sealed class Occupancy
    {
        public Occupancy(
            string netEntityId,
            string roomId,
            string entityType,
            ulong connectionGeneration,
            LocalEntityId localId,
            bool inReplica)
        {
            NetEntityId = netEntityId;
            RoomId = roomId;
            EntityType = entityType;
            ConnectionGeneration = connectionGeneration;
            LocalId = localId;
            InReplica = inReplica;
            Presence = OccupancyState.Live;
            Revision = 1;
            Tick = 1;
        }

        public string NetEntityId { get; }
        public string RoomId { get; set; }
        public string EntityType { get; set; }
        public ulong ConnectionGeneration { get; set; }
        public LocalEntityId LocalId { get; }
        public bool InReplica { get; set; }
        public OccupancyState Presence { get; set; }
        public ulong Revision { get; set; }
        public ulong Tick { get; set; }
    }

    private readonly struct AttributeKey : IEquatable<AttributeKey>
    {
        public AttributeKey(string roomId, string netEntityId, string attributeId)
        {
            RoomId = roomId;
            NetEntityId = netEntityId;
            AttributeId = attributeId;
        }

        public string RoomId { get; }
        public string NetEntityId { get; }
        public string AttributeId { get; }

        public bool Equals(AttributeKey other) =>
            string.Equals(RoomId, other.RoomId, StringComparison.Ordinal) &&
            string.Equals(NetEntityId, other.NetEntityId, StringComparison.Ordinal) &&
            string.Equals(AttributeId, other.AttributeId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is AttributeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(RoomId);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(NetEntityId);
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(AttributeId);
            }
        }
    }
}
