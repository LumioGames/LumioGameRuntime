using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Replication.Lifecycle;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct TombstoneView(NetEntityId NetEntityId, ulong UntilRevision);

public sealed class TombstoneRegistry
{
    private readonly ReplicationStoreScope _scope;
    // This is the scope-owned canonical fence shared with the mapping table.
    private readonly Dictionary<NetEntityId, ulong> _values;

    public TombstoneRegistry() : this(1)
    {
    }

    public TombstoneRegistry(ulong initialGeneration)
        : this(new ReplicationStoreScope(initialGeneration))
    {
    }

    internal TombstoneRegistry(ReplicationStoreScope scope)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _values = scope.Tombstones;
    }

    public int Count
    {
        get { lock (_scope.Gate) return _scope.State == IdentityStoreState.Active ? _values.Count : 0; }
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

    public IdentityStoreToken CaptureToken()
    {
        lock (_scope.Gate) return _scope.CaptureLocked();
    }

    public IdentityStoreToken GetToken() => CaptureToken();

    public IdentityStoreToken CurrentToken => CaptureToken();

    public bool IsActive => State == IdentityStoreState.Active;

    public bool IsClosed => State == IdentityStoreState.Closed;

    public bool IsTokenCurrent(IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token);
    }

    internal bool Add(NetEntityId id, ulong untilRevision) => AddCore(id, untilRevision, default, false);

    public bool Add(NetEntityId id, ulong untilRevision, IdentityStoreToken token) => AddCore(id, untilRevision, token, true);

    /// <summary>
    /// Records a destroy fence. Unknown or understated horizons are retained
    /// forever rather than being clamped to the destroy revision.
    /// </summary>
    internal bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonResult horizon) =>
        Add(id, destroyRevision, horizon, default, false);

    internal bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonInputs inputs) =>
        Add(id, destroyRevision, TombstoneHorizonCalculator.Calculate(inputs), default, false);

    public bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        Add(id, destroyRevision, horizon, token, true);

    public bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonInputs inputs, IdentityStoreToken token) =>
        Add(id, destroyRevision, TombstoneHorizonCalculator.Calculate(inputs), token, true);

    internal bool Add(NetEntityId id, ulong destroyRevision, ulong tombstoneUntilRevision) =>
        AddCore(id, tombstoneUntilRevision > destroyRevision ? tombstoneUntilRevision : ulong.MaxValue, default, false);

    public bool Add(NetEntityId id, ulong destroyRevision, ulong tombstoneUntilRevision, IdentityStoreToken token) =>
        AddCore(id, tombstoneUntilRevision > destroyRevision ? tombstoneUntilRevision : ulong.MaxValue, token, true);

    public bool Contains(NetEntityId id, ulong revision)
    {
        lock (_scope.Gate) return _scope.State == IdentityStoreState.Active && ContainsLocked(id, revision);
    }

    public bool Contains(NetEntityId id, ulong revision, IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token) && ContainsLocked(id, revision);
    }

    public bool Contains(NetEntityId id)
    {
        lock (_scope.Gate) return _scope.State == IdentityStoreState.Active && _values.ContainsKey(id);
    }

    public bool Contains(NetEntityId id, IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token) && _values.ContainsKey(id);
    }

    internal int Collect(ulong revision, in TombstoneHorizonResult horizon) =>
        Collect(revision, horizon, default, false);

    public int Collect(ulong revision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        Collect(revision, horizon, token, true);

    public bool CanCollect(NetEntityId id, in TombstoneHorizonInputs inputs, ulong currentRevision) =>
        CanCollect(id, TombstoneHorizonCalculator.Calculate(inputs), currentRevision, default, false);

    public bool CanCollect(NetEntityId id, in TombstoneHorizonInputs inputs, ulong currentRevision, IdentityStoreToken token) =>
        CanCollect(id, TombstoneHorizonCalculator.Calculate(inputs), currentRevision, token, true);

    public bool CanCollect(NetEntityId id, in TombstoneHorizonResult horizon, ulong currentRevision) =>
        CanCollect(id, horizon, currentRevision, default, false);

    public bool CanCollect(NetEntityId id, in TombstoneHorizonResult horizon, ulong currentRevision, IdentityStoreToken token) =>
        CanCollect(id, horizon, currentRevision, token, true);

    internal bool Remove(NetEntityId id)
    {
        lock (_scope.Gate) return _scope.State == IdentityStoreState.Active && _values.Remove(id);
    }

    public bool Remove(NetEntityId id, IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token) && _values.Remove(id);
    }

    public IReadOnlyDictionary<NetEntityId, ulong> Snapshot()
    {
        lock (_scope.Gate) return _scope.State == IdentityStoreState.Active ? new Dictionary<NetEntityId, ulong>(_values) : new Dictionary<NetEntityId, ulong>();
    }

    public IReadOnlyDictionary<NetEntityId, ulong> Snapshot(IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token) ? new Dictionary<NetEntityId, ulong>(_values) : new Dictionary<NetEntityId, ulong>();
    }

    public bool TrySnapshot(IdentityStoreToken token, out IReadOnlyDictionary<NetEntityId, ulong> snapshot)
    {
        lock (_scope.Gate)
        {
            if (!_scope.IsCurrentLocked(token))
            {
                snapshot = new Dictionary<NetEntityId, ulong>();
                return false;
            }

            snapshot = new Dictionary<NetEntityId, ulong>(_values);
            return true;
        }
    }

    internal bool Reset(ulong nextGeneration)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryAdvanceConnectionGenerationLocked(nextGeneration)) return false;
            _values.Clear();
            return true;
        }
    }

    internal bool Reset()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryAdvanceConnectionGenerationLocked()) return false;
            _values.Clear();
            return true;
        }
    }

    internal bool ResetForGeneration(ulong nextGeneration) => Reset(nextGeneration);

    public bool ResetForGeneration(ulong nextGeneration, IdentityStoreToken expectedToken) => Reset(expectedToken, nextGeneration);

    public bool Reset(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryAdvanceConnectionGenerationLocked()) return false;
            _values.Clear();
            return true;
        }
    }

    public bool Reset(IdentityStoreToken expectedToken, ulong nextGeneration)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryAdvanceConnectionGenerationLocked(nextGeneration)) return false;
            _values.Clear();
            return true;
        }
    }

    internal bool Invalidate()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryTransitionTerminalLocked(false)) return false;
            _values.Clear();
            return true;
        }
    }

    internal bool Invalidate(ulong expectedGeneration)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || expectedGeneration != _scope.ConnectionGeneration || !_scope.TryTransitionTerminalLocked(false)) return false;
            _values.Clear();
            return true;
        }
    }

    public bool Invalidate(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryTransitionTerminalLocked(false)) return false;
            _values.Clear();
            return true;
        }
    }

    internal bool Close()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryTransitionTerminalLocked(true)) return false;
            _values.Clear();
            return true;
        }
    }

    public bool Close(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryTransitionTerminalLocked(true)) return false;
            _values.Clear();
            return true;
        }
    }

    internal bool Clear() => Reset();

    internal bool InvalidateForGeneration(ulong expectedGeneration) => Invalidate(expectedGeneration);

    public bool InvalidateForGeneration(IdentityStoreToken expectedToken) => Invalidate(expectedToken);

    internal IdentityStoreToken ResetAndGetToken(ulong nextGeneration) =>
        Reset(nextGeneration) ? CaptureToken() : default;

    private bool AddCore(NetEntityId id, ulong untilRevision, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active) return false;
            if (!id.IsValid) return false;
            if (_values.TryGetValue(id, out ulong existing) && untilRevision < existing) return false;
            _values[id] = untilRevision;
            return true;
        }
    }

    private bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token, bool tokenRequired)
    {
        // Invalid and unknown horizons use MaxValue as a conservative fence.
        ulong until = horizon.Known && horizon.Horizon > destroyRevision ? horizon.Horizon : ulong.MaxValue;
        return AddCore(id, until, token, tokenRequired);
    }

    private int Collect(ulong revision, in TombstoneHorizonResult horizon, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if ((tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active) ||
                !horizon.Known || revision <= horizon.Horizon) return 0;
            var ids = new List<NetEntityId>();
            foreach (KeyValuePair<NetEntityId, ulong> item in _values)
                if (horizon.CanCollect(item.Value, revision)) ids.Add(item.Key);
            foreach (NetEntityId id in ids) _values.Remove(id);
            return ids.Count;
        }
    }

    private bool CanCollect(NetEntityId id, in TombstoneHorizonResult horizon, ulong currentRevision, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            return (tokenRequired ? _scope.IsCurrentLocked(token) : _scope.State == IdentityStoreState.Active) &&
                _values.TryGetValue(id, out ulong until) && horizon.CanCollect(until, currentRevision);
        }
    }

    private bool ContainsLocked(NetEntityId id, ulong revision) =>
        _values.TryGetValue(id, out ulong until) && revision <= until;

    internal void ClearContextLocked() => _values.Clear();
}

public sealed class TombstoneRegistryView
{
    private readonly TombstoneRegistry _store;

    internal TombstoneRegistryView(TombstoneRegistry store) => _store = store;

    public int Count => _store.Count;

    public ulong Generation => _store.Generation;

    public ulong WorkEpoch => _store.WorkEpoch;

    public IdentityStoreState State => _store.State;

    public bool IsActive => _store.IsActive;

    public bool IsClosed => _store.IsClosed;

    public bool Contains(NetEntityId id, ulong revision) => _store.Contains(id, revision);

    public bool Contains(NetEntityId id) => _store.Contains(id);

    public bool CanCollect(NetEntityId id, in TombstoneHorizonInputs inputs, ulong currentRevision) =>
        _store.CanCollect(id, inputs, currentRevision);

    public bool CanCollect(NetEntityId id, in TombstoneHorizonResult horizon, ulong currentRevision) =>
        _store.CanCollect(id, horizon, currentRevision);

    public IReadOnlyDictionary<NetEntityId, ulong> Snapshot() => _store.Snapshot();
}
