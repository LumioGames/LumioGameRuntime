using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Lumio.GameRuntime.GeneratedContracts;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;

namespace Lumio.GameRuntime.Replication.History;

public enum BaselineStoreStatus
{
    Accepted,
    Duplicate,
    QueueFull,
    Invalid,
    RevisionConflict
}

public enum BaselineAckStatus
{
    Acknowledged,
    AlreadyAcknowledged,
    UnknownBaseline,
    RevisionConflict,
    Invalid
}

public sealed record BaselineRecord(
    string SnapshotId,
    ulong Revision,
    long Bytes,
    string MappingSetHash = "",
    int SchemaEpoch = 1,
    bool Acknowledged = false)
{
    public BaselineRecord(
        string snapshotId,
        ulong revision,
        long bytes,
        string mappingSetHash,
        int schemaEpoch,
        bool acknowledged,
        ulong sequence,
        string idempotencyKey)
        : this(snapshotId, revision, bytes, mappingSetHash, schemaEpoch, acknowledged)
    {
        Sequence = sequence;
        IdempotencyKey = idempotencyKey ?? string.Empty;
    }

    public ulong Sequence { get; init; }

    public string IdempotencyKey { get; init; } = string.Empty;

    internal FullSnapshotProjection? Projection { get; init; }
}

public sealed class BaselineStore
{
    private readonly ReplicationStoreScope _scope;
    private readonly ReplicationBudget _budget;
    private readonly Dictionary<string, BaselineRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BaselineRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _idempotencySizes = new(StringComparer.Ordinal);
    private readonly Queue<string> _idempotencyOrder = new();
    private long _idempotencyBytes;
    private long _bytes;

    public BaselineStore(ReplicationBudget budget) : this(budget, 1)
    {
    }

    public BaselineStore(ReplicationBudget budget, ulong initialGeneration)
        : this(budget, new ReplicationStoreScope(initialGeneration))
    {
    }

    internal BaselineStore(ReplicationBudget budget, ReplicationStoreScope scope)
    {
        if (!budget.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));
        _budget = budget;
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    public int Count
    {
        get { lock (_scope.Gate) return _scope.State == IdentityStoreState.Active ? _records.Count : 0; }
    }

    public long Bytes
    {
        get { lock (_scope.Gate) return _scope.State == IdentityStoreState.Active ? _bytes : 0; }
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

    public bool IsActive => State == IdentityStoreState.Active;

    public bool IsClosed => State == IdentityStoreState.Closed;

    public IdentityStoreToken CaptureToken()
    {
        lock (_scope.Gate) return _scope.CaptureLocked();
    }

    public bool IsTokenCurrent(IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token);
    }

    internal BaselineStoreStatus Add(BaselineRecord record) => AddCore(record, default, false);

    public BaselineStoreStatus Add(BaselineRecord record, IdentityStoreToken token) => AddCore(record, token, true);

    internal BaselineAckStatus Ack(string snapshotId, ulong confirmedRevision) =>
        AckCore(snapshotId, confirmedRevision, default, false);

    public BaselineAckStatus Ack(string snapshotId, ulong confirmedRevision, IdentityStoreToken token) =>
        AckCore(snapshotId, confirmedRevision, token, true);

    internal BaselineStoreStatus Stage(BaselineRecord record) => Add(record);

    public BaselineStoreStatus Stage(BaselineRecord record, IdentityStoreToken token) => Add(record, token);

    internal BaselineAckStatus Acknowledge(string snapshotId, ulong confirmedRevision) => Ack(snapshotId, confirmedRevision);

    public BaselineAckStatus Acknowledge(string snapshotId, ulong confirmedRevision, IdentityStoreToken token) =>
        Ack(snapshotId, confirmedRevision, token);

    public bool IsAcknowledged(string snapshotId) => IsAcknowledgedCore(snapshotId, default, false);

    public bool IsAcknowledged(string snapshotId, IdentityStoreToken token) => IsAcknowledgedCore(snapshotId, token, true);

    public bool TryGetAcknowledged(out BaselineRecord? record) =>
        TryGetAcknowledgedCore(default, false, out record);

    public bool TryGetAcknowledged(IdentityStoreToken token, out BaselineRecord? record) =>
        TryGetAcknowledgedCore(token, true, out record);

    /// <summary>Releases one baseline and returns whether storage was reclaimed.</summary>
    internal bool Release(string snapshotId) => ReleaseCore(snapshotId, default, false);

    public bool Release(string snapshotId, IdentityStoreToken token) => ReleaseCore(snapshotId, token, true);

    internal bool Expire(string snapshotId) => Release(snapshotId);

    public bool Expire(string snapshotId, IdentityStoreToken token) => Release(snapshotId, token);

    internal int Expire(ulong beforeRevision)
    {
        lock (_scope.Gate)
        {
            if (_scope.State != IdentityStoreState.Active) return 0;
            var ids = _records.Values
                .Where(value => !value.Acknowledged && value.Revision < beforeRevision)
                .Select(value => value.SnapshotId)
                .ToArray();
            var removed = 0;
            foreach (string id in ids)
                if (ReleaseLocked(id)) removed++;
            return removed;
        }
    }

    /// <summary>Reclaims acknowledged baselines older than the selected acknowledgement.</summary>
    internal int ReleaseObsolete(string snapshotId)
    {
        if (!ReplicationValidation.IsIdentifier(snapshotId)) return 0;
        lock (_scope.Gate)
        {
            return _scope.State == IdentityStoreState.Active ? ReleaseObsoleteLocked(snapshotId) : 0;
        }
    }

    /// <summary>Clears same-generation baseline state without reopening a terminal store.</summary>
    internal void Reset()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode == ReplicationStoreScopeMode.Standalone && _scope.TryAdvanceWorkEpochLocked()) ClearLocked();
        }
    }

    public bool Reset(IdentityStoreToken token)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(token) || !_scope.TryAdvanceWorkEpochLocked()) return false;
            ClearLocked();
            return true;
        }
    }

    internal void Clear() => Reset();

    internal bool ResetForGeneration(ulong nextGeneration)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryAdvanceConnectionGenerationLocked(nextGeneration)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool ResetForGeneration(ulong nextGeneration, IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryAdvanceConnectionGenerationLocked(nextGeneration)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Invalidate()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryTransitionTerminalLocked(false)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool Invalidate(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryTransitionTerminalLocked(false)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Close()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryTransitionTerminalLocked(true)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool Close(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryTransitionTerminalLocked(true)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool TryGet(string snapshotId, out BaselineRecord? record) =>
        TryGetCore(snapshotId, default, false, out record);

    public bool TryGet(string snapshotId, out BaselineRecord? record, IdentityStoreToken token) =>
        TryGetCore(snapshotId, token, true, out record);

    public IReadOnlyList<BaselineRecord> Snapshot() => SnapshotCore(default, false);

    public IReadOnlyList<BaselineRecord> Snapshot(IdentityStoreToken token) => SnapshotCore(token, true);

    private BaselineStoreStatus AddCore(BaselineRecord record, IdentityStoreToken token, bool tokenRequired)
    {
        if (record is null || !ReplicationValidation.IsIdentifier(record.SnapshotId) || record.Bytes < 0 || record.SchemaEpoch < 0 ||
            record.SchemaEpoch != GeneratedContractManifest.SchemaEpoch || record.MappingSetHash is null ||
            !ReplicationValidation.IsHash256(record.MappingSetHash) || record.Acknowledged) return BaselineStoreStatus.Invalid;
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return BaselineStoreStatus.Invalid;
            if (record.Bytes > _budget.HistoryBytes) return BaselineStoreStatus.QueueFull;
            if (!string.IsNullOrEmpty(record.IdempotencyKey))
            {
                if (_idempotency.TryGetValue(record.IdempotencyKey, out BaselineRecord? keyed))
                    return SamePayload(keyed, record) ? BaselineStoreStatus.Duplicate : BaselineStoreStatus.RevisionConflict;
            }
            if (_records.TryGetValue(record.SnapshotId, out BaselineRecord? existing))
                return SamePayload(existing, record) ? BaselineStoreStatus.Duplicate : BaselineStoreStatus.RevisionConflict;
            long idempotencySize = 0;
            if (!string.IsNullOrEmpty(record.IdempotencyKey))
            {
                // A keyed record is authoritative only when its replay outcome
                // can be retained for the complete configured budget.
                if (!TryGetIdempotencySize(record, out idempotencySize) ||
                    !CanRememberIdempotencyLocked(record.IdempotencyKey, idempotencySize))
                    return BaselineStoreStatus.QueueFull;
            }
            if (_records.Count >= _budget.HistoryWindow || record.Bytes > _budget.HistoryBytes - _bytes)
            {
                // Acknowledged snapshots are replaceable. Evict them in a stable
                // order so a long-lived connection can stage its next baseline.
                BaselineRecord[] replaceable = _records.Values
                    .Where(value => value.Acknowledged)
                    .OrderBy(value => value.Revision)
                    .ThenBy(value => value.SnapshotId, StringComparer.Ordinal)
                    .ToArray();
                var planned = new List<BaselineRecord>();
                int projectedCount = _records.Count;
                long projectedBytes = _bytes;
                foreach (BaselineRecord old in replaceable)
                {
                    if (projectedCount < _budget.HistoryWindow && record.Bytes <= _budget.HistoryBytes - projectedBytes) break;
                    planned.Add(old);
                    projectedCount--;
                    projectedBytes -= old.Bytes;
                }

                if (projectedCount >= _budget.HistoryWindow || record.Bytes > _budget.HistoryBytes - projectedBytes)
                    return BaselineStoreStatus.QueueFull;
                foreach (BaselineRecord old in planned)
                    if (_records.Remove(old.SnapshotId)) _bytes -= old.Bytes;
            }
            if (_records.Count >= _budget.HistoryWindow || record.Bytes > _budget.HistoryBytes - _bytes) return BaselineStoreStatus.QueueFull;
            if (record.Bytes > long.MaxValue - _bytes) return BaselineStoreStatus.QueueFull;
            long admittedBytes = _bytes + record.Bytes;
            _records.Add(record.SnapshotId, record);
            _bytes = admittedBytes;
            if (!string.IsNullOrEmpty(record.IdempotencyKey) && !RememberIdempotencyLocked(record, idempotencySize))
            {
                _records.Remove(record.SnapshotId);
                _bytes -= record.Bytes;
                return BaselineStoreStatus.QueueFull;
            }
            return BaselineStoreStatus.Accepted;
        }
    }

    private BaselineAckStatus AckCore(string snapshotId, ulong confirmedRevision, IdentityStoreToken token, bool tokenRequired)
    {
        if (!ReplicationValidation.IsIdentifier(snapshotId)) return BaselineAckStatus.Invalid;
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return BaselineAckStatus.Invalid;
            if (!_records.TryGetValue(snapshotId, out BaselineRecord? record)) return BaselineAckStatus.UnknownBaseline;
            if (record.Revision != confirmedRevision) return BaselineAckStatus.RevisionConflict;
            if (record.Acknowledged) return BaselineAckStatus.AlreadyAcknowledged;
            _records[snapshotId] = record with { Acknowledged = true };
            ReleaseObsoleteLocked(snapshotId);
            return BaselineAckStatus.Acknowledged;
        }
    }

    private bool IsAcknowledgedCore(string snapshotId, IdentityStoreToken token, bool tokenRequired)
    {
        if (!ReplicationValidation.IsIdentifier(snapshotId)) return false;
        lock (_scope.Gate)
        {
            return (tokenRequired ? _scope.IsCurrentLocked(token) : _scope.State == IdentityStoreState.Active) &&
                _records.TryGetValue(snapshotId, out BaselineRecord? value) && value.Acknowledged;
        }
    }

    private bool TryGetAcknowledgedCore(IdentityStoreToken token, bool tokenRequired, out BaselineRecord? record)
    {
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
            {
                record = null;
                return false;
            }
            BaselineRecord? selected = _records.Values
                .Where(value => value.Acknowledged)
                .OrderByDescending(value => value.Revision)
                .ThenByDescending(value => value.SnapshotId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (selected is not null)
            {
                record = selected;
                return true;
            }
        }

        record = null;
        return false;
    }

    private bool ReleaseCore(string snapshotId, IdentityStoreToken token, bool tokenRequired)
    {
        if (!ReplicationValidation.IsIdentifier(snapshotId)) return false;
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active) return false;
            return ReleaseLocked(snapshotId);
        }
    }

    private bool TryGetCore(string snapshotId, IdentityStoreToken token, bool tokenRequired, out BaselineRecord? record)
    {
        if (!ReplicationValidation.IsIdentifier(snapshotId))
        {
            record = null;
            return false;
        }
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
            {
                record = null;
                return false;
            }
            return _records.TryGetValue(snapshotId, out record);
        }
    }

    private IReadOnlyList<BaselineRecord> SnapshotCore(IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return Array.Empty<BaselineRecord>();
            var values = new List<BaselineRecord>(_records.Values);
            values.Sort((left, right) => StringComparer.Ordinal.Compare(left.SnapshotId, right.SnapshotId));
            return new ReadOnlyCollection<BaselineRecord>(values);
        }
    }

    private int ReleaseObsoleteLocked(string snapshotId)
    {
        if (!_records.TryGetValue(snapshotId, out BaselineRecord? selected) || !selected.Acknowledged) return 0;
        var removed = 0;
        var ids = new List<string>();
        foreach (string id in _records.Keys)
            if (id != snapshotId && _records[id].Acknowledged && _records[id].Revision < selected.Revision) ids.Add(id);
        foreach (string id in ids)
        {
            if (_records.Remove(id, out BaselineRecord? record))
            {
                _bytes -= record.Bytes;
                removed++;
            }
        }

        return removed;
    }

    private static bool SamePayload(BaselineRecord left, BaselineRecord right) =>
        left.SnapshotId == right.SnapshotId &&
        left.Revision == right.Revision &&
        left.Bytes == right.Bytes &&
        left.MappingSetHash == right.MappingSetHash &&
        left.SchemaEpoch == right.SchemaEpoch &&
        SameProjection(left.Projection, right.Projection) &&
        (string.IsNullOrEmpty(left.IdempotencyKey) || string.IsNullOrEmpty(right.IdempotencyKey) ||
            left.IdempotencyKey == right.IdempotencyKey);

    private static bool SameProjection(FullSnapshotProjection? left, FullSnapshotProjection? right) =>
        left is null || right is null ||
        left.SessionId == right.SessionId &&
        left.ProductId == right.ProductId &&
        left.GameReleaseId == right.GameReleaseId &&
        left.SnapshotId == right.SnapshotId &&
        left.MappingSetHash == right.MappingSetHash &&
        left.BodyJson == right.BodyJson;

    internal bool TryGetByIdempotencyKey(string idempotencyKey, out BaselineRecord? record)
    {
        lock (_scope.Gate)
        {
            if (_scope.State == IdentityStoreState.Active && !string.IsNullOrEmpty(idempotencyKey))
            {
                return _idempotency.TryGetValue(idempotencyKey, out record);
            }
        }

        record = null;
        return false;
    }

    private bool ReleaseLocked(string snapshotId)
    {
        if (!_records.Remove(snapshotId, out BaselineRecord? record)) return false;
        _bytes -= record.Bytes;
        if (!string.IsNullOrEmpty(record.IdempotencyKey))
        {
            RemoveIdempotencyLocked(record.IdempotencyKey);
            CompactIdempotencyOrderLocked();
        }
        return true;
    }

    private void ClearLocked()
    {
        _records.Clear();
        _idempotency.Clear();
        _idempotencySizes.Clear();
        _idempotencyOrder.Clear();
        _idempotencyBytes = 0;
        _bytes = 0;
    }

    private bool CanRememberIdempotencyLocked(string key, long size) =>
        !string.IsNullOrEmpty(key) && !_idempotency.ContainsKey(key) &&
        _idempotency.Count < _budget.HistoryWindow && size >= 0 && size <= _budget.HistoryBytes &&
        _idempotencyBytes <= _budget.HistoryBytes - size;

    private bool RememberIdempotencyLocked(BaselineRecord record, long size)
    {
        if (!CanRememberIdempotencyLocked(record.IdempotencyKey, size)) return false;
        long admittedBytes = _idempotencyBytes + size;
        // Retained identities are durable replay outcomes. Keep them stable;
        // a newer request must not evict a result that can still be retried.
        _idempotency[record.IdempotencyKey] = record;
        _idempotencySizes[record.IdempotencyKey] = size;
        _idempotencyOrder.Enqueue(record.IdempotencyKey);
        _idempotencyBytes = admittedBytes;
        return true;
    }

    private static bool TryGetIdempotencySize(BaselineRecord record, out long size)
    {
        size = 0;
        long keyBytes = Encoding.UTF8.GetByteCount(record.IdempotencyKey);
        if (record.Bytes > long.MaxValue - keyBytes) return false;
        size = record.Bytes + keyBytes;
        return true;
    }

    private void RemoveIdempotencyLocked(string key)
    {
        _idempotency.Remove(key);
        if (_idempotencySizes.Remove(key, out long size)) _idempotencyBytes -= size;
    }

    private void CompactIdempotencyOrderLocked()
    {
        string[] retained = _idempotencyOrder
            .Where(key => _idempotency.ContainsKey(key))
            .ToArray();
        _idempotencyOrder.Clear();
        foreach (string key in retained) _idempotencyOrder.Enqueue(key);
    }

    internal void ClearContextLocked() => ClearLocked();
}

public sealed class BaselineStoreView
{
    private readonly BaselineStore _store;

    internal BaselineStoreView(BaselineStore store) => _store = store;

    public int Count => _store.Count;

    public long Bytes => _store.Bytes;

    public ulong Generation => _store.Generation;

    public ulong WorkEpoch => _store.WorkEpoch;

    public IdentityStoreState State => _store.State;

    public bool IsActive => _store.IsActive;

    public bool IsClosed => _store.IsClosed;

    public bool IsAcknowledged(string snapshotId) => _store.IsAcknowledged(snapshotId);

    public bool TryGetAcknowledged(out BaselineRecord? record) => _store.TryGetAcknowledged(out record);

    public bool TryGet(string snapshotId, out BaselineRecord? record) => _store.TryGet(snapshotId, out record);

    public IReadOnlyList<BaselineRecord> Snapshot() => _store.Snapshot();
}
