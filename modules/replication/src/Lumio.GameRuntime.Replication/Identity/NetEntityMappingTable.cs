using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Lifecycle;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct NetEntityId(string Value)
{
    public bool IsValid => ReplicationValidation.IsNetId(Value);

    public static NetEntityId Parse(string value)
    {
        if (!ReplicationValidation.IsNetId(value)) throw new ArgumentException("A lowercase 128-bit NetEntityId is required.", nameof(value));
        return new NetEntityId(value);
    }

    public static bool TryParse(string? value, out NetEntityId id)
    {
        if (ReplicationValidation.IsNetId(value)) { id = new NetEntityId(value!); return true; }
        id = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct MappingBindingResult(bool Succeeded, string? GeneratedErrorId, string? Detail)
{
    public static MappingBindingResult Accepted() => new(true, null, null);

    public static MappingBindingResult Rejected(string errorId, string detail) => new(false, errorId, detail);

    internal static MappingBindingResult Stale() =>
        Rejected("StaleConnectionGeneration", "The identity store token belongs to an older connection generation or lifecycle.");
}

/// <summary>Lifecycle state shared by generation-scoped identity stores.</summary>
public enum IdentityStoreState
{
    Active = 0,
    Invalidated = 1,
    Closed = 2,
}

/// <summary>
/// Narrow capability used by Wave 2 to fence retained identity-store references.
/// Tokens are issued by a store and become stale after Reset or Invalidate.
/// </summary>
public readonly struct IdentityStoreToken : IEquatable<IdentityStoreToken>
{
    private readonly object? _owner;

    public IdentityStoreToken(ulong generation)
    {
        _owner = null;
        Generation = generation;
        WorkEpoch = 0;
    }

    internal IdentityStoreToken(object owner, ulong generation, ulong workEpoch)
    {
        _owner = owner;
        Generation = generation;
        WorkEpoch = workEpoch;
    }

    public ulong Generation { get; }

    public ulong WorkEpoch { get; }

    public bool IsValid => _owner is not null && Generation != 0 && WorkEpoch != 0;

    internal bool HasOwner(object owner) => IsValid && ReferenceEquals(_owner, owner);

    public bool Equals(IdentityStoreToken other) =>
        Generation == other.Generation && WorkEpoch == other.WorkEpoch && ReferenceEquals(_owner, other._owner);

    public override bool Equals(object? obj) => obj is IdentityStoreToken other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_owner, Generation, WorkEpoch);

    public static bool operator ==(IdentityStoreToken left, IdentityStoreToken right) => left.Equals(right);

    public static bool operator !=(IdentityStoreToken left, IdentityStoreToken right) => !left.Equals(right);
}

public sealed class NetEntityMappingTable
{
    private readonly ReplicationStoreScope _scope;
    private readonly Dictionary<NetEntityId, LocalBinding> _byNet = new();
    private readonly Dictionary<string, NetEntityId> _byLocal = new(StringComparer.Ordinal);
    // This is the scope-owned canonical fence; the registry uses the same instance.
    private readonly Dictionary<NetEntityId, ulong> _tombstones;
    // Keep source ordering independently of the horizon so an expired
    // tombstone cannot admit an older same-generation lifecycle update.
    private readonly Dictionary<NetEntityId, LifecycleFence> _lifecycleFences = new();
    private readonly int _ownerThreadId;

    public NetEntityMappingTable() : this(new WorldId(0x11UL), 1)
    {
    }

    public NetEntityMappingTable(ulong initialGeneration)
        : this(new WorldId(0x11UL), initialGeneration)
    {
    }

    public NetEntityMappingTable(WorldId worldId)
        : this(worldId, 1)
    {
    }

    public NetEntityMappingTable(WorldId worldId, ulong initialGeneration)
        : this(new ReplicationStoreScope(initialGeneration), worldId)
    {
    }

    internal NetEntityMappingTable(ReplicationStoreScope scope)
        : this(scope, new WorldId(0x11UL))
    {
    }

    internal NetEntityMappingTable(ReplicationStoreScope scope, WorldId worldId)
    {
        if (worldId.IsDefault)
            throw new ArgumentOutOfRangeException(nameof(worldId), worldId, "A non-default WorldId is required.");
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _tombstones = scope.Tombstones;
        WorldId = worldId;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    public WorldId WorldId { get; }

    public int Count
    {
        get { lock (_scope.Gate) return _scope.State == IdentityStoreState.Active ? _byNet.Count : 0; }
    }

    public ulong Generation
    {
        get { lock (_scope.Gate) return _scope.ConnectionGeneration; }
    }

    public ulong WorkEpoch
    {
        get { lock (_scope.Gate) return _scope.WorkEpoch; }
    }

    public IdentityStoreState State
    {
        get { lock (_scope.Gate) return _scope.State; }
    }

    public bool IsInvalidated => State != IdentityStoreState.Active;

    public bool IsActive => State == IdentityStoreState.Active;

    public bool IsClosed => State == IdentityStoreState.Closed;

    public IdentityStoreToken CaptureToken()
    {
        lock (_scope.Gate) return _scope.CaptureLocked();
    }

    public IdentityStoreToken GetToken() => CaptureToken();

    public IdentityStoreToken CurrentToken => CaptureToken();

    public bool IsTokenCurrent(IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token);
    }

    internal MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId) =>
        BindCore(netEntityId, localEntityId, null, default, false);

    internal MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId, ulong currentRevision) =>
        BindCore(netEntityId, localEntityId, currentRevision, default, false);

    public MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId, IdentityStoreToken token) =>
        BindCore(netEntityId, localEntityId, null, token, true);

    public MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId, ulong currentRevision, IdentityStoreToken token) =>
        BindCore(netEntityId, localEntityId, currentRevision, token, true);

    internal MappingBindingResult Bind(string netEntityId, string localEntityId) =>
        NetEntityId.TryParse(netEntityId, out NetEntityId parsed)
            ? Bind(parsed, localEntityId)
            : MappingBindingResult.Rejected("ManifestMalformed", "NetEntityId is invalid.");

    internal MappingBindingResult Bind(string netEntityId, string localEntityId, ulong currentRevision) =>
        NetEntityId.TryParse(netEntityId, out NetEntityId parsed)
            ? Bind(parsed, localEntityId, currentRevision)
            : MappingBindingResult.Rejected("ManifestMalformed", "NetEntityId is invalid.");

    public MappingBindingResult Bind(string netEntityId, string localEntityId, IdentityStoreToken token)
    {
        lock (_scope.Gate)
        {
            if (!_scope.IsCurrentLocked(token)) return StaleToken(token);
        }
        return NetEntityId.TryParse(netEntityId, out NetEntityId parsed)
            ? Bind(parsed, localEntityId, token)
            : MappingBindingResult.Rejected("ManifestMalformed", "NetEntityId is invalid.");
    }

    public MappingBindingResult Bind(string netEntityId, string localEntityId, ulong currentRevision, IdentityStoreToken token)
    {
        lock (_scope.Gate)
        {
            if (!_scope.IsCurrentLocked(token)) return StaleToken(token);
        }
        return NetEntityId.TryParse(netEntityId, out NetEntityId parsed)
            ? Bind(parsed, localEntityId, currentRevision, token)
            : MappingBindingResult.Rejected("ManifestMalformed", "NetEntityId is invalid.");
    }

    internal MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity) =>
        BindGenerated(identity, null, default, false);

    internal MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity, ulong currentRevision) =>
        BindGenerated(identity, currentRevision, default, false);

    public MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity, IdentityStoreToken token) =>
        BindGenerated(identity, null, token, true);

    public MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity, ulong currentRevision, IdentityStoreToken token) =>
        BindGenerated(identity, currentRevision, token, true);

    internal MappingBindingResult TryBind(NetEntityId netEntityId, string localEntityId) => Bind(netEntityId, localEntityId);

    internal MappingBindingResult TryBind(NetEntityId netEntityId, string localEntityId, ulong currentRevision) => Bind(netEntityId, localEntityId, currentRevision);

    public bool TryBind(NetEntityId netEntityId, LocalEntityId localEntityId) =>
        TryBind(netEntityId, localEntityId, WorldId);

    public bool TryBind(NetEntityId netEntityId, LocalEntityId localEntityId, WorldId worldId)
    {
        if (worldId != WorldId || localEntityId.IsDefault || !IsOwnerThread()) return false;
        return Bind(netEntityId, localEntityId.ToString(), CaptureToken()).Succeeded;
    }

    internal bool DestroyAndTombstone(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonResult horizon) =>
        Remove(netEntityId, destroyRevision, horizon);

    public bool TryResolveLocal(NetEntityId netEntityId, ulong expectedGeneration, out string? localEntityId) =>
        TryResolveLocalCore(netEntityId, expectedGeneration, out localEntityId, default, false);

    public bool TryResolveLocal(NetEntityId netEntityId, ulong expectedGeneration, out string? localEntityId, IdentityStoreToken token) =>
        TryResolveLocalCore(netEntityId, expectedGeneration, out localEntityId, token, true);

    public bool TryResolveLocal(NetEntityId netEntityId, out LocalEntityId localEntityId)
    {
        localEntityId = default;
        lock (_scope.Gate)
        {
            if (_scope.State == IdentityStoreState.Active &&
                _byNet.TryGetValue(netEntityId, out LocalBinding? binding) &&
                LocalEntityId.TryParse(binding.LocalEntityId, out localEntityId))
                return true;
            localEntityId = default;
            return false;
        }
    }

    public bool TryResolveNet(LocalEntityId localEntityId, out NetEntityId netEntityId) =>
        TryResolveNet(localEntityId.ToString(), out netEntityId);

    public bool TryResolveNet(string localEntityId, out NetEntityId netEntityId)
    {
        lock (_scope.Gate)
        {
            if (_scope.State == IdentityStoreState.Active && localEntityId is not null && _byLocal.TryGetValue(localEntityId, out netEntityId)) return true;
            netEntityId = default;
            return false;
        }
    }

    public bool TryResolveNet(string localEntityId, out NetEntityId netEntityId, IdentityStoreToken token)
    {
        lock (_scope.Gate)
        {
            if (_scope.IsCurrentLocked(token) && localEntityId is not null && _byLocal.TryGetValue(localEntityId, out netEntityId)) return true;
            netEntityId = default;
            return false;
        }
    }

    internal bool Remove(NetEntityId netEntityId) => RemoveCore(netEntityId, ulong.MaxValue, default, false);

    /// <summary>Removes a mapping while retaining the identity through a direct horizon.</summary>
    internal bool Remove(NetEntityId netEntityId, ulong tombstoneUntilRevision) =>
        RemoveCore(netEntityId, tombstoneUntilRevision == 0 ? ulong.MaxValue : tombstoneUntilRevision, default, false);

    internal bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonResult horizon) =>
        Remove(netEntityId, destroyRevision, horizon, default, false);

    internal bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonInputs inputs) =>
        Remove(netEntityId, destroyRevision, TombstoneHorizonCalculator.Calculate(inputs), default, false);

    internal bool Remove(NetEntityId netEntityId, ulong destroyRevision, ulong tombstoneUntilRevision) =>
        Remove(netEntityId, destroyRevision, tombstoneUntilRevision, default, false);

    public bool Remove(NetEntityId netEntityId, IdentityStoreToken token) => RemoveCore(netEntityId, ulong.MaxValue, token, true);

    public bool Remove(NetEntityId netEntityId, ulong tombstoneUntilRevision, IdentityStoreToken token) =>
        RemoveCore(netEntityId, tombstoneUntilRevision == 0 ? ulong.MaxValue : tombstoneUntilRevision, token, true);

    public bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        Remove(netEntityId, destroyRevision, horizon, token, true);

    public bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonInputs inputs, IdentityStoreToken token) =>
        Remove(netEntityId, destroyRevision, TombstoneHorizonCalculator.Calculate(inputs), token, true);

    public bool Remove(NetEntityId netEntityId, ulong destroyRevision, ulong tombstoneUntilRevision, IdentityStoreToken token) =>
        Remove(netEntityId, destroyRevision, tombstoneUntilRevision, token, true);

    public bool IsTombstoned(NetEntityId netEntityId)
    {
        lock (_scope.Gate) return _scope.State == IdentityStoreState.Active && _tombstones.ContainsKey(netEntityId);
    }

    public bool IsTombstoned(NetEntityId netEntityId, ulong revision)
    {
        lock (_scope.Gate) return _scope.State == IdentityStoreState.Active && IsTombstonedLocked(netEntityId, revision);
    }

    public bool IsTombstoned(NetEntityId netEntityId, IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token) && _tombstones.ContainsKey(netEntityId);
    }

    public bool IsTombstoned(NetEntityId netEntityId, ulong revision, IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token) && IsTombstonedLocked(netEntityId, revision);
    }

    internal int CollectTombstones(ulong currentRevision, in TombstoneHorizonResult horizon) =>
        CollectTombstones(currentRevision, horizon, default, false);

    public int CollectTombstones(ulong currentRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        CollectTombstones(currentRevision, horizon, token, true);

    internal bool ReleaseTombstone(NetEntityId netEntityId, ulong currentRevision, in TombstoneHorizonResult horizon) =>
        ReleaseTombstone(netEntityId, currentRevision, horizon, default, false);

    public bool ReleaseTombstone(NetEntityId netEntityId, ulong currentRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        ReleaseTombstone(netEntityId, currentRevision, horizon, token, true);

    public IReadOnlyDictionary<NetEntityId, ulong> Tombstones
    {
        get
        {
            lock (_scope.Gate)
                return _scope.State == IdentityStoreState.Active
                    ? new Dictionary<NetEntityId, ulong>(_tombstones)
                    : new Dictionary<NetEntityId, ulong>();
        }
    }

    public IReadOnlyDictionary<NetEntityId, ulong> TombstonesSnapshot() => Tombstones;

    public IReadOnlyDictionary<NetEntityId, ulong> TombstonesSnapshot(IdentityStoreToken token) =>
        SnapshotTombstones(token);

    public IReadOnlyDictionary<NetEntityId, ulong> SnapshotTombstones(IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token) ? new Dictionary<NetEntityId, ulong>(_tombstones) : new Dictionary<NetEntityId, ulong>();
    }

    public IReadOnlyDictionary<NetEntityId, string> Snapshot()
    {
        lock (_scope.Gate)
        {
            if (_scope.State != IdentityStoreState.Active) return new Dictionary<NetEntityId, string>();
            var result = new Dictionary<NetEntityId, string>();
            foreach (KeyValuePair<NetEntityId, LocalBinding> item in _byNet) result.Add(item.Key, item.Value.LocalEntityId);
            return new ReadOnlyDictionary<NetEntityId, string>(result);
        }
    }

    public IReadOnlyDictionary<NetEntityId, string> Snapshot(IdentityStoreToken token)
    {
        lock (_scope.Gate)
        {
            if (!_scope.IsCurrentLocked(token)) return new Dictionary<NetEntityId, string>();
            var result = new Dictionary<NetEntityId, string>();
            foreach (KeyValuePair<NetEntityId, LocalBinding> item in _byNet) result.Add(item.Key, item.Value.LocalEntityId);
            return new ReadOnlyDictionary<NetEntityId, string>(result);
        }
    }

    public bool TrySnapshot(IdentityStoreToken token, out IReadOnlyDictionary<NetEntityId, string> snapshot)
    {
        lock (_scope.Gate)
        {
            if (!_scope.IsCurrentLocked(token))
            {
                snapshot = new Dictionary<NetEntityId, string>();
                return false;
            }

            var result = new Dictionary<NetEntityId, string>();
            foreach (KeyValuePair<NetEntityId, LocalBinding> item in _byNet) result.Add(item.Key, item.Value.LocalEntityId);
            snapshot = new ReadOnlyDictionary<NetEntityId, string>(result);
            return true;
        }
    }

    /// <summary>Clears state and advances to an explicit connection generation.</summary>
    internal bool Reset(ulong nextGeneration)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryAdvanceConnectionGenerationLocked(nextGeneration)) return false;
            ClearLocked();
            return true;
        }
    }

    /// <summary>Clears state and advances by one generation.</summary>
    internal bool Reset()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryAdvanceConnectionGenerationLocked()) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool ResetForGeneration(ulong nextGeneration) => Reset(nextGeneration);

    public bool ResetForGeneration(ulong nextGeneration, IdentityStoreToken expectedToken) => Reset(expectedToken, nextGeneration);

    public bool Reset(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryAdvanceConnectionGenerationLocked()) return false;
            ClearLocked();
            return true;
        }
    }

    public bool Reset(IdentityStoreToken expectedToken, ulong nextGeneration)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryAdvanceConnectionGenerationLocked(nextGeneration)) return false;
            ClearLocked();
            return true;
        }
    }

    /// <summary>Invalidates this store for the current lifecycle and clears all state.</summary>
    internal bool Invalidate()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryTransitionTerminalLocked(false)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Invalidate(ulong expectedGeneration)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || expectedGeneration != _scope.ConnectionGeneration || !_scope.TryTransitionTerminalLocked(false)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool Invalidate(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryTransitionTerminalLocked(false)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Close()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryTransitionTerminalLocked(true)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool Close(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryTransitionTerminalLocked(true)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Clear() => Reset();

    internal bool InvalidateForGeneration(ulong expectedGeneration) => Invalidate(expectedGeneration);

    public bool InvalidateForGeneration(IdentityStoreToken expectedToken) => Invalidate(expectedToken);

    internal IdentityStoreToken ResetAndGetToken(ulong nextGeneration) =>
        Reset(nextGeneration) ? CaptureToken() : default;

    private MappingBindingResult BindCore(NetEntityId netEntityId, string localEntityId, ulong? currentRevision, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread())
                return MappingBindingResult.Rejected("WrongContext", "Identity mutation requires the Simulation Owner Thread.");
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return tokenRequired ? StaleToken(token) : MappingBindingResult.Stale();
            return BindLocked(netEntityId, localEntityId, currentRevision);
        }
    }

    private MappingBindingResult BindLocked(NetEntityId netEntityId, string localEntityId, ulong? currentRevision, ulong? sourceRevision = null)
    {
        if (!netEntityId.IsValid || !TryParseLocal(localEntityId, out _, out ulong generation))
            return MappingBindingResult.Rejected("ManifestMalformed", "NetEntityId or LocalEntityId is invalid.");

        // A generated lifecycle fence carries stronger ordering information
        // than the expiry horizon. A legacy/direct bind has no source revision,
        // so it cannot prove that it is newer than that fence.
        if (sourceRevision is null && _lifecycleFences.ContainsKey(netEntityId))
            return MappingBindingResult.Rejected("RevisionConflict", "The identity is fenced by a newer lifecycle revision.");

        if (_byNet.ContainsKey(netEntityId) || _byLocal.ContainsKey(localEntityId))
            return MappingBindingResult.Rejected("InvalidArgument", "The mapping is already bound.");

        if (_tombstones.TryGetValue(netEntityId, out ulong until))
        {
            if (!currentRevision.HasValue || currentRevision.Value <= until)
                return MappingBindingResult.Rejected("SnapshotBaseMismatch", "The network identity is retained as a tombstone.");
            _tombstones.Remove(netEntityId);
        }

        _byNet.Add(netEntityId, new LocalBinding(localEntityId, generation, sourceRevision));
        _byLocal.Add(localEntityId, netEntityId);
        return MappingBindingResult.Accepted();
    }

    private MappingBindingResult BindGenerated(
        Lumio.Gen.ContractTypes.EntityIdentity? identity,
        ulong? currentRevision,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread())
                return MappingBindingResult.Rejected("WrongContext", "Identity mutation requires the Simulation Owner Thread.");
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return tokenRequired ? StaleToken(token) : MappingBindingResult.Stale();
            return BindGeneratedLocked(identity, currentRevision);
        }
    }

    private MappingBindingResult BindGeneratedLocked(Lumio.Gen.ContractTypes.EntityIdentity? identity, ulong? currentRevision)
    {
        Identity.EntityIdentityValidationResult validation = Identity.EntityIdentityValidator.Validate(identity!);
        if (!validation.Succeeded)
            return MappingBindingResult.Rejected(validation.GeneratedErrorId ?? "ManifestMalformed", validation.Detail ?? "Entity identity is invalid.");
        if (identity is null || !NetEntityId.TryParse(identity.NetEntityId, out NetEntityId netEntityId))
            return MappingBindingResult.Rejected("ManifestMalformed", "Entity identity is invalid.");
        if (identity.Namespace != Lumio.Gen.ContractTypes.EntityIdentityNamespace.Authoritative)
            return MappingBindingResult.Rejected("ManifestMalformed", "Only authoritative identities may create live mappings.");

        if (identity.Lifecycle == Lumio.Gen.ContractTypes.EntityIdentityLifecycle.Tombstoned ||
            identity.Lifecycle == Lumio.Gen.ContractTypes.EntityIdentityLifecycle.Destroyed)
        {
            if (_lifecycleFences.TryGetValue(netEntityId, out LifecycleFence existingFence) &&
                (identity.Generation < existingFence.Generation ||
                 identity.Generation == existingFence.Generation && IsStrictlyOlderSource(identity.SourceRevision, existingFence.SourceRevision)))
                return MappingBindingResult.Rejected("RevisionConflict", "A stale destroyed identity cannot replace a newer lifecycle fence.");
            if (_byNet.TryGetValue(netEntityId, out LocalBinding? current) &&
                (identity.Generation < current.Generation ||
                 identity.Generation == current.Generation && IsOlderSource(identity.SourceRevision, current.SourceRevision)))
                return MappingBindingResult.Rejected("RevisionConflict", "A stale destroyed identity cannot remove a newer live binding.");
            // A destroyed/tombstoned identity is retained as a non-live fence.
            ulong until = identity.TombstoneUntilRevision.GetValueOrDefault();
            if (!identity.TombstoneUntilRevision.HasValue || until == 0) until = ulong.MaxValue;
            if (identity.SourceRevision.HasValue && until <= identity.SourceRevision.Value) until = ulong.MaxValue;
            if (_byNet.Remove(netEntityId, out LocalBinding? binding)) _byLocal.Remove(binding.LocalEntityId);
            AddTombstoneLocked(netEntityId, until);
            _lifecycleFences[netEntityId] = new LifecycleFence(identity.Generation, identity.SourceRevision);
            return MappingBindingResult.Rejected("SnapshotBaseMismatch", "Destroyed or tombstoned identities cannot create live mappings.");
        }

        if (identity.Lifecycle != Lumio.Gen.ContractTypes.EntityIdentityLifecycle.Alive)
            return MappingBindingResult.Rejected("InvalidArgument", "Reserved identities cannot create live mappings.");

        if (identity.LocalEntityId is not null &&
            (!TryParseLocal(identity.LocalEntityId, out _, out ulong localGeneration) || localGeneration != identity.Generation))
            return MappingBindingResult.Rejected("InvalidArgument", "LocalEntityId generation does not match the generated identity.");

        if (identity.LocalEntityId is null)
            return MappingBindingResult.Rejected("ManifestMalformed", "Alive identities require a LocalEntityId for a live mapping.");
        if (_lifecycleFences.TryGetValue(netEntityId, out LifecycleFence fence) &&
            (identity.Generation < fence.Generation ||
             identity.Generation == fence.Generation && IsOlderOrEqualSource(identity.SourceRevision, fence.SourceRevision)))
        {
            // The horizon may be collectible even though the source-order
            // fence must remain. Remove only the expired horizon; never clear
            // the fence that rejects this delayed input.
            if (currentRevision.HasValue && _tombstones.TryGetValue(netEntityId, out ulong staleUntil) && currentRevision.Value > staleUntil)
                _tombstones.Remove(netEntityId);
            return MappingBindingResult.Rejected("RevisionConflict", "An older Alive identity cannot cross a newer lifecycle fence.");
        }
        if (_byNet.TryGetValue(netEntityId, out LocalBinding? live) &&
            (identity.Generation < live.Generation ||
             identity.Generation == live.Generation && IsOlderSource(identity.SourceRevision, live.SourceRevision)))
            return MappingBindingResult.Rejected("RevisionConflict", "An older Alive identity cannot replace a newer live binding.");
        MappingBindingResult result = BindLocked(netEntityId, identity.LocalEntityId, currentRevision, identity.SourceRevision);
        if (result.Succeeded) _lifecycleFences.Remove(netEntityId);
        return result;
    }

    private bool TryResolveLocalCore(NetEntityId netEntityId, ulong expectedGeneration, out string? localEntityId, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if ((tokenRequired ? _scope.IsCurrentLocked(token) : _scope.State == IdentityStoreState.Active) &&
                _byNet.TryGetValue(netEntityId, out LocalBinding? binding) && binding.Generation == expectedGeneration)
            {
                localEntityId = binding.LocalEntityId;
                return true;
            }

            localEntityId = null;
            return false;
        }
    }

    private bool RemoveCore(NetEntityId netEntityId, ulong tombstoneUntilRevision, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active) return false;
            if (!netEntityId.IsValid) return false;
            bool removed = _byNet.Remove(netEntityId, out LocalBinding? binding);
            if (removed) _byLocal.Remove(binding!.LocalEntityId);
            if (removed && binding!.SourceRevision.HasValue &&
                (!_lifecycleFences.TryGetValue(netEntityId, out LifecycleFence existingFence) ||
                 binding.Generation > existingFence.Generation ||
                 binding.Generation == existingFence.Generation && existingFence.SourceRevision.HasValue &&
                 binding.SourceRevision.Value > existingFence.SourceRevision.Value))
                _lifecycleFences[netEntityId] = new LifecycleFence(binding.Generation, binding.SourceRevision);
            if (_tombstones.TryGetValue(netEntityId, out ulong existing) && existing > tombstoneUntilRevision)
                tombstoneUntilRevision = existing;
            _tombstones[netEntityId] = tombstoneUntilRevision;
            return removed;
        }
    }

    private bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token, bool tokenRequired)
    {
        ulong until = horizon.Known && horizon.Horizon > destroyRevision ? horizon.Horizon : ulong.MaxValue;
        return RemoveCore(netEntityId, until, token, tokenRequired);
    }

    private bool Remove(NetEntityId netEntityId, ulong destroyRevision, ulong tombstoneUntilRevision, IdentityStoreToken token, bool tokenRequired)
    {
        ulong until = tombstoneUntilRevision > destroyRevision ? tombstoneUntilRevision : ulong.MaxValue;
        return RemoveCore(netEntityId, until, token, tokenRequired);
    }

    private int CollectTombstones(ulong currentRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return 0;
            if ((tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active) ||
                !horizon.Known || currentRevision <= horizon.Horizon) return 0;
            var ids = new List<NetEntityId>();
            foreach (KeyValuePair<NetEntityId, ulong> item in _tombstones)
                if (horizon.CanCollect(item.Value, currentRevision)) ids.Add(item.Key);
            foreach (NetEntityId id in ids) _tombstones.Remove(id);
            return ids.Count;
        }
    }

    private bool ReleaseTombstone(NetEntityId netEntityId, ulong currentRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread()) return false;
            if ((tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active) ||
                !_tombstones.TryGetValue(netEntityId, out ulong until) || !horizon.CanCollect(until, currentRevision)) return false;
            return _tombstones.Remove(netEntityId);
        }
    }

    private void AddTombstoneLocked(NetEntityId id, ulong untilRevision)
    {
        if (_tombstones.TryGetValue(id, out ulong existing) && existing > untilRevision) return;
        _tombstones[id] = untilRevision;
    }

    private bool IsTombstonedLocked(NetEntityId id, ulong revision) =>
        _tombstones.TryGetValue(id, out ulong until) && revision <= until;

    private void ClearLocked()
    {
        _byNet.Clear();
        _byLocal.Clear();
        _tombstones.Clear();
        _lifecycleFences.Clear();
    }

    internal void ClearContextLocked() => ClearLocked();

    private bool IsOwnerThread() =>
        Environment.CurrentManagedThreadId == _ownerThreadId;

    private MappingBindingResult StaleToken(IdentityStoreToken token) =>
        _scope.ClassifyLocked(token) == ReplicationTokenStatus.GenerationMismatch
            ? MappingBindingResult.Rejected("StaleConnectionGeneration", "The identity store token belongs to an older connection generation.")
            : MappingBindingResult.Rejected("FencingTokenStale", "The identity store token belongs to another lifecycle scope or work epoch.");

    private static bool TryParseLocal(string? value, out ulong index, out ulong generation)
    {
        index = 0;
        generation = 0;
        if (string.IsNullOrEmpty(value)) return false;
        int separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 || value.IndexOf(':', separator + 1) >= 0) return false;
        return ulong.TryParse(value.Substring(0, separator), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) &&
            ulong.TryParse(value.Substring(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out generation);
    }

    private static bool IsOlderSource(ulong? incoming, ulong? current)
    {
        if (current.HasValue && !incoming.HasValue) return true;
        return incoming.HasValue && current.HasValue && incoming.Value < current.Value;
    }

    private static bool IsOlderOrEqualSource(ulong? incoming, ulong? current)
    {
        if (!incoming.HasValue) return true;
        // An unknown stored revision is a conservative fence: a later input
        // cannot prove that it supersedes the unknown source ordering.
        if (!current.HasValue) return true;
        return incoming.Value <= current.Value;
    }

    private static bool IsStrictlyOlderSource(ulong? incoming, ulong? current)
    {
        if (!incoming.HasValue) return current.HasValue;
        if (!current.HasValue) return false;
        return incoming.Value < current.Value;
    }

    private sealed record LocalBinding(string LocalEntityId, ulong Generation, ulong? SourceRevision = null);

    private readonly record struct LifecycleFence(ulong Generation, ulong? SourceRevision);
}

public sealed class NetEntityMappingView
{
    private readonly NetEntityMappingTable _store;

    internal NetEntityMappingView(NetEntityMappingTable store) => _store = store;

    public int Count => _store.Count;

    public ulong Generation => _store.Generation;

    public ulong WorkEpoch => _store.WorkEpoch;

    public IdentityStoreState State => _store.State;

    public bool IsInvalidated => _store.IsInvalidated;

    public bool IsActive => _store.IsActive;

    public bool IsClosed => _store.IsClosed;

    public bool TryResolveLocal(NetEntityId netEntityId, ulong expectedGeneration, out string? localEntityId) =>
        _store.TryResolveLocal(netEntityId, expectedGeneration, out localEntityId);

    public bool TryResolveNet(string localEntityId, out NetEntityId netEntityId) =>
        _store.TryResolveNet(localEntityId, out netEntityId);

    public bool IsTombstoned(NetEntityId netEntityId) => _store.IsTombstoned(netEntityId);

    public bool IsTombstoned(NetEntityId netEntityId, ulong revision) => _store.IsTombstoned(netEntityId, revision);

    public IReadOnlyDictionary<NetEntityId, ulong> Tombstones => _store.Tombstones;

    public IReadOnlyDictionary<NetEntityId, ulong> TombstonesSnapshot() => _store.TombstonesSnapshot();

    public IReadOnlyDictionary<NetEntityId, string> Snapshot() => _store.Snapshot();
}
