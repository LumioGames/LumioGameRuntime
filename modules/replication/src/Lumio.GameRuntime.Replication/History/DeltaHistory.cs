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

public enum DeltaHistoryStatus
{
    Accepted,
    Duplicate,
    QueueFull,
    Invalid,
    RevisionConflict
}

public enum DeltaAckStatus
{
    Acknowledged,
    AlreadyAcknowledged,
    UnknownHistory,
    RevisionConflict,
    Invalid
}

public enum DeltaChainStatus
{
    Complete,
    Gap,
    UnknownBaseline,
    HistoryExhausted,
    Invalid
}

public sealed record DeltaRecord(
    string BaseSnapshotId,
    ulong FromRevision,
    ulong ToRevision,
    ulong Sequence,
    long Bytes,
    string MappingSetHash = "",
    int SchemaEpoch = 1)
{
    public DeltaRecord(
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        ulong sequence,
        long bytes,
        string mappingSetHash,
        int schemaEpoch,
        string idempotencyKey)
        : this(baseSnapshotId, fromRevision, toRevision, sequence, bytes, mappingSetHash, schemaEpoch)
    {
        IdempotencyKey = idempotencyKey ?? string.Empty;
    }

    public string IdempotencyKey { get; init; } = string.Empty;

    internal DeltaProjection? Projection { get; init; }
}

public sealed class DeltaChainResult
{
    internal DeltaChainResult(DeltaChainStatus status, IReadOnlyList<DeltaRecord> records)
    {
        Status = status;
        Records = new ReadOnlyCollection<DeltaRecord>(records.ToArray());
    }

    public DeltaChainStatus Status { get; }

    public IReadOnlyList<DeltaRecord> Records { get; }

    public bool RequiresResync => Status is not DeltaChainStatus.Complete;

    public bool RequiresFullResync => Status is DeltaChainStatus.UnknownBaseline or DeltaChainStatus.HistoryExhausted;

    public string ResyncReason => Status.ToString();
}

public sealed class DeltaHistory
{
    private readonly ReplicationStoreScope _scope;
    private readonly ReplicationBudget _budget;
    private readonly List<DeltaRecord> _records = new();
    private readonly Dictionary<string, AckCursor> _acknowledged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BaselineCursor> _baselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeltaRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _idempotencySizes = new(StringComparer.Ordinal);
    private readonly Queue<string> _idempotencyOrder = new();
    private long _idempotencyBytes;
    private long _bytes;
    private bool _implicitBaselineAdmissionSealed;

    public DeltaHistory(ReplicationBudget budget) : this(budget, 1)
    {
    }

    public DeltaHistory(ReplicationBudget budget, ulong initialGeneration)
        : this(budget, new ReplicationStoreScope(initialGeneration))
    {
    }

    internal DeltaHistory(ReplicationBudget budget, ReplicationStoreScope scope)
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

    internal DeltaHistoryStatus Add(DeltaRecord record) => AddCore(record, default, false);

    public DeltaHistoryStatus Add(DeltaRecord record, IdentityStoreToken token) => AddCore(record, token, true);

    private DeltaHistoryStatus AddCore(DeltaRecord record, IdentityStoreToken token, bool tokenRequired)
    {
        if (record is null || !ReplicationValidation.IsIdentifier(record.BaseSnapshotId) || record.ToRevision <= record.FromRevision || record.Bytes < 0 ||
            record.SchemaEpoch != GeneratedContractManifest.SchemaEpoch ||
            !ReplicationValidation.IsHash256(record.MappingSetHash))
            return DeltaHistoryStatus.Invalid;
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return DeltaHistoryStatus.Invalid;
            if (!string.IsNullOrEmpty(record.IdempotencyKey) &&
                _idempotency.TryGetValue(record.IdempotencyKey, out DeltaRecord? keyed))
                return SameAuthoritativePayload(keyed, record)
                    ? DeltaHistoryStatus.Duplicate
                    : DeltaHistoryStatus.RevisionConflict;
            if (_acknowledged.TryGetValue(record.BaseSnapshotId, out AckCursor cursor) && record.Sequence <= cursor.Sequence)
                return DeltaHistoryStatus.RevisionConflict;
            DeltaRecord? existing = _records.FirstOrDefault(value => value.BaseSnapshotId == record.BaseSnapshotId && value.Sequence == record.Sequence);
            if (existing is not null)
                return SamePayload(existing, record) ? DeltaHistoryStatus.Duplicate : DeltaHistoryStatus.RevisionConflict;
            long idempotencySize = 0;
            if (!string.IsNullOrEmpty(record.IdempotencyKey))
            {
                // A keyed record is authoritative only when its replay outcome
                // can be retained for the complete configured budget.
                if (!TryGetIdempotencySize(record, out idempotencySize) ||
                    !CanRememberIdempotencyLocked(record.IdempotencyKey, idempotencySize))
                    return DeltaHistoryStatus.QueueFull;
            }
            // After bounded cursor eviction, an unknown ID cannot be distinguished
            // from a replay. A reset or explicit baseline registration reopens it.
            if (!IsKnownBaselineLocked(record.BaseSnapshotId) && _implicitBaselineAdmissionSealed)
                return DeltaHistoryStatus.RevisionConflict;
            if (_records.Count >= _budget.HistoryWindow || record.Bytes > _budget.HistoryBytes - _bytes) return DeltaHistoryStatus.QueueFull;
            if (record.Bytes > long.MaxValue - _bytes) return DeltaHistoryStatus.QueueFull;
            long admittedBytes = _bytes + record.Bytes;

            // A directly appended history starts at sequence one. Contexts register
            // the full-snapshot sequence so the first delta can follow it.
            if (!_baselines.TryGetValue(record.BaseSnapshotId, out BaselineCursor baseline))
                _baselines[record.BaseSnapshotId] = new BaselineCursor(0, record.FromRevision, false, record.Sequence == 1);
            else if (!baseline.Explicit && !baseline.StartKnown && baseline.Sequence == 0 && record.Sequence == 1)
                _baselines[record.BaseSnapshotId] = baseline with { Revision = record.FromRevision, StartKnown = true };
            _records.Add(record);
            _bytes = admittedBytes;
            _records.Sort(Compare);
            if (!string.IsNullOrEmpty(record.IdempotencyKey) && !RememberIdempotencyLocked(record, idempotencySize))
            {
                _records.Remove(record);
                _bytes -= record.Bytes;
                return DeltaHistoryStatus.QueueFull;
            }
            return DeltaHistoryStatus.Accepted;
        }
    }

    /// <summary>Associates a base snapshot with its authoritative sequence and revision.</summary>
    internal void RegisterBaseline(string baseSnapshotId, ulong baselineSequence, ulong baselineRevision)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId)) return;
        lock (_scope.Gate)
        {
            if (_scope.State != IdentityStoreState.Active) return;
            _baselines[baseSnapshotId] = new BaselineCursor(baselineSequence, baselineRevision, true, true);
        }
    }

    public DeltaChainResult TryGetContiguous(string baseSnapshotId, ulong fromRevision, ulong toRevision) =>
        TryGetContiguousCore(baseSnapshotId, fromRevision, toRevision, default, false);

    public DeltaChainResult TryGetContiguous(string baseSnapshotId, ulong fromRevision, ulong toRevision, IdentityStoreToken token) =>
        TryGetContiguousCore(baseSnapshotId, fromRevision, toRevision, token, true);

    private DeltaChainResult TryGetContiguousCore(
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId) || toRevision < fromRevision)
            return new DeltaChainResult(DeltaChainStatus.Invalid, Array.Empty<DeltaRecord>());
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return new DeltaChainResult(DeltaChainStatus.Invalid, Array.Empty<DeltaRecord>());
            if (!IsKnownBaselineLocked(baseSnapshotId))
                return new DeltaChainResult(DeltaChainStatus.UnknownBaseline, Array.Empty<DeltaRecord>());

            if (fromRevision == toRevision)
            {
                DeltaChainStatus emptyStatus = CanSatisfyEmptyRangeLocked(baseSnapshotId, fromRevision);
                return new DeltaChainResult(emptyStatus, Array.Empty<DeltaRecord>());
            }

            if (_acknowledged.TryGetValue(baseSnapshotId, out AckCursor acknowledged) && fromRevision < acknowledged.Revision)
                return new DeltaChainResult(DeltaChainStatus.HistoryExhausted, Array.Empty<DeltaRecord>());

            if (!TryGetExpectedStartLocked(baseSnapshotId, fromRevision, out ulong expectedSequence))
                return new DeltaChainResult(DeltaChainStatus.Gap, Array.Empty<DeltaRecord>());

            var selected = new List<DeltaRecord>();
            ulong cursor = fromRevision;
            while (cursor < toRevision)
            {
                DeltaRecord? next = _records.FirstOrDefault(value =>
                    value.BaseSnapshotId == baseSnapshotId &&
                    value.FromRevision == cursor &&
                    value.Sequence == expectedSequence);
                if (next is null)
                {
                    bool hasRevisionLink = _records.Any(value => value.BaseSnapshotId == baseSnapshotId && value.FromRevision == cursor);
                    bool hasRetainedHistory = _records.Any(value => value.BaseSnapshotId == baseSnapshotId);
                    return new DeltaChainResult(hasRevisionLink || hasRetainedHistory ? DeltaChainStatus.Gap : DeltaChainStatus.HistoryExhausted, selected);
                }
                if (next.ToRevision > toRevision)
                    return new DeltaChainResult(DeltaChainStatus.Gap, selected);
                selected.Add(next);
                cursor = next.ToRevision;
                if (expectedSequence == ulong.MaxValue && cursor < toRevision)
                    return new DeltaChainResult(DeltaChainStatus.Gap, selected);
                expectedSequence++;
            }

            return new DeltaChainResult(cursor == toRevision ? DeltaChainStatus.Complete : DeltaChainStatus.Gap, selected);
        }
    }

    internal DeltaHistoryStatus Append(DeltaRecord record) => Add(record);

    public DeltaHistoryStatus Append(DeltaRecord record, IdentityStoreToken token) => Add(record, token);

    /// <summary>Releases acknowledged records while retaining the acknowledgement cursor.</summary>
    internal DeltaAckStatus Acknowledge(string baseSnapshotId, ulong confirmedSequence, ulong confirmedRevision) =>
        AcknowledgeCore(baseSnapshotId, confirmedSequence, confirmedRevision, default, false);

    public DeltaAckStatus Acknowledge(
        string baseSnapshotId,
        ulong confirmedSequence,
        ulong confirmedRevision,
        IdentityStoreToken token) =>
        AcknowledgeCore(baseSnapshotId, confirmedSequence, confirmedRevision, token, true);

    private DeltaAckStatus AcknowledgeCore(
        string baseSnapshotId,
        ulong confirmedSequence,
        ulong? confirmedRevision,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId)) return DeltaAckStatus.Invalid;
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return DeltaAckStatus.Invalid;
            if (!IsKnownBaselineLocked(baseSnapshotId)) return DeltaAckStatus.UnknownHistory;

            bool hadPrevious = _acknowledged.TryGetValue(baseSnapshotId, out AckCursor previous);
            if (hadPrevious)
            {
                if (confirmedSequence < previous.Sequence || confirmedRevision.HasValue && confirmedRevision.Value < previous.Revision)
                    return DeltaAckStatus.RevisionConflict;
                if (confirmedSequence <= previous.Sequence && (!confirmedRevision.HasValue || confirmedRevision.Value <= previous.Revision))
                    return DeltaAckStatus.AlreadyAcknowledged;
            }

            DeltaRecord? target = _records.FirstOrDefault(value =>
                value.BaseSnapshotId == baseSnapshotId &&
                value.Sequence == confirmedSequence &&
                (!confirmedRevision.HasValue || value.ToRevision == confirmedRevision.Value));
            if (target is null) return DeltaAckStatus.UnknownHistory;
            ulong targetRevision = target.ToRevision;
            ulong sequenceCursor = hadPrevious ? previous.Sequence : _baselines[baseSnapshotId].Sequence;
            if (sequenceCursor == ulong.MaxValue) return DeltaAckStatus.UnknownHistory;
            ulong expectedSequence = sequenceCursor + 1;
            ulong cursor = hadPrevious ? previous.Revision : _baselines[baseSnapshotId].Revision;

            DeltaRecord[] path = _records
                .Where(value => value.BaseSnapshotId == baseSnapshotId && value.Sequence >= expectedSequence && value.Sequence <= confirmedSequence)
                .OrderBy(value => value.Sequence)
                .ToArray();
            if (path.Length == 0 || path[^1].Sequence != confirmedSequence) return DeltaAckStatus.UnknownHistory;
            foreach (DeltaRecord value in path)
            {
                if (value.Sequence != expectedSequence || value.FromRevision != cursor || value.ToRevision > targetRevision)
                    return DeltaAckStatus.UnknownHistory;
                cursor = value.ToRevision;
                if (expectedSequence == ulong.MaxValue && value.Sequence != confirmedSequence)
                    return DeltaAckStatus.UnknownHistory;
                expectedSequence++;
            }
            if (cursor != targetRevision) return DeltaAckStatus.UnknownHistory;

            var pathSequences = new HashSet<ulong>(path.Select(value => value.Sequence));
            int removed = RemoveLocked(value => value.BaseSnapshotId == baseSnapshotId && pathSequences.Contains(value.Sequence));
            if (removed != path.Length) return DeltaAckStatus.UnknownHistory;
            if (!hadPrevious && _acknowledged.Count >= _budget.HistoryWindow)
            {
                string evicted = _acknowledged.Keys.OrderBy(value => value, StringComparer.Ordinal).First();
                _acknowledged.Remove(evicted);
                _baselines.Remove(evicted);
                RemoveLocked(value => value.BaseSnapshotId == evicted);
                _implicitBaselineAdmissionSealed = true;
            }
            _acknowledged[baseSnapshotId] = new AckCursor(confirmedSequence, targetRevision);
            return DeltaAckStatus.Acknowledged;
        }
    }

    public DeltaChainResult TryGetContiguous(string baseSnapshotId, ulong fromRevision, ulong toRevision, int schemaEpoch)
    {
        if (schemaEpoch != GeneratedContractManifest.SchemaEpoch)
            return new DeltaChainResult(DeltaChainStatus.Invalid, Array.Empty<DeltaRecord>());
        DeltaChainResult result = TryGetContiguous(baseSnapshotId, fromRevision, toRevision);
        if (result.Status != DeltaChainStatus.Complete) return result;
        foreach (DeltaRecord record in result.Records)
            if (record.SchemaEpoch != schemaEpoch) return new DeltaChainResult(DeltaChainStatus.Gap, Array.Empty<DeltaRecord>());
        return result;
    }

    internal DeltaAckStatus Ack(string baseSnapshotId, ulong confirmedSequence, ulong confirmedRevision) =>
        Acknowledge(baseSnapshotId, confirmedSequence, confirmedRevision);

    public DeltaAckStatus Ack(string baseSnapshotId, ulong confirmedSequence, ulong confirmedRevision, IdentityStoreToken token) =>
        Acknowledge(baseSnapshotId, confirmedSequence, confirmedRevision, token);

    internal DeltaAckStatus Acknowledge(string baseSnapshotId, ulong confirmedSequence) =>
        AcknowledgeCore(baseSnapshotId, confirmedSequence, null, default, false);

    public DeltaAckStatus Acknowledge(string baseSnapshotId, ulong confirmedSequence, IdentityStoreToken token) =>
        AcknowledgeCore(baseSnapshotId, confirmedSequence, null, token, true);

    internal DeltaAckStatus Ack(string baseSnapshotId, ulong confirmedSequence) =>
        AcknowledgeCore(baseSnapshotId, confirmedSequence, null, default, false);

    public DeltaAckStatus Ack(string baseSnapshotId, ulong confirmedSequence, IdentityStoreToken token) =>
        AcknowledgeCore(baseSnapshotId, confirmedSequence, null, token, true);

    internal int ReleaseThrough(string baseSnapshotId, ulong confirmedRevision)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId)) return 0;
        lock (_scope.Gate)
        {
            if (_scope.State != IdentityStoreState.Active) return 0;
            DeltaRecord[] released = _records
                .Where(value => value.BaseSnapshotId == baseSnapshotId && value.ToRevision <= confirmedRevision)
                .ToArray();
            int removed = RemoveLocked(value => value.BaseSnapshotId == baseSnapshotId && value.ToRevision <= confirmedRevision);
            ForgetReleasedIdentitiesLocked(released);
            return removed;
        }
    }

    internal int Release(string baseSnapshotId)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId)) return 0;
        lock (_scope.Gate)
        {
            if (_scope.State != IdentityStoreState.Active) return 0;
            _acknowledged.Remove(baseSnapshotId);
            _baselines.Remove(baseSnapshotId);
            int removed = RemoveLocked(value => value.BaseSnapshotId == baseSnapshotId);
            RemoveIdempotencyForBaseLocked(baseSnapshotId);
            return removed;
        }
    }

    internal int Expire(ulong beforeRevision)
    {
        lock (_scope.Gate)
        {
            if (_scope.State != IdentityStoreState.Active) return 0;
            DeltaRecord[] expired = _records.Where(value => value.ToRevision < beforeRevision).ToArray();
            int removed = RemoveLocked(value => value.ToRevision < beforeRevision);
            ForgetReleasedIdentitiesLocked(expired);
            return removed;
        }
    }

    /// <summary>Clears same-generation history without reopening a terminal store.</summary>
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

    /// <summary>A new baseline invalidates every delta chain for this connection.</summary>
    internal void ResetForBaseline(string baseSnapshotId)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId)) return;
        Reset();
    }

    internal void ResetForBaseline(string baseSnapshotId, ulong baselineSequence, ulong baselineRevision)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId)) return;
        lock (_scope.Gate)
        {
            if (_scope.State != IdentityStoreState.Active) return;
            ClearLocked();
            _baselines[baseSnapshotId] = new BaselineCursor(baselineSequence, baselineRevision, true, true);
        }
    }

    public bool ResetForBaseline(string baseSnapshotId, IdentityStoreToken token) =>
        ResetForBaselineCore(baseSnapshotId, 0, 0, false, token);

    public bool ResetForBaseline(
        string baseSnapshotId,
        ulong baselineSequence,
        ulong baselineRevision,
        IdentityStoreToken token) =>
        ResetForBaselineCore(baseSnapshotId, baselineSequence, baselineRevision, true, token);

    private bool ResetForBaselineCore(
        string baseSnapshotId,
        ulong baselineSequence,
        ulong baselineRevision,
        bool registerBaseline,
        IdentityStoreToken token)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId)) return false;
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone ||
                !_scope.IsCurrentLocked(token) || !_scope.TryAdvanceWorkEpochLocked()) return false;
            ClearLocked();
            if (registerBaseline)
                _baselines[baseSnapshotId] = new BaselineCursor(baselineSequence, baselineRevision, true, true);
            return true;
        }
    }

    public DeltaChainResult TryBuildRepairRange(string baseSnapshotId, ulong fromRevision, ulong toRevision) =>
        TryGetContiguous(baseSnapshotId, fromRevision, toRevision);

    public DeltaChainResult TryBuildRepairRange(string baseSnapshotId, ulong fromRevision, ulong toRevision, string mappingSetHash)
    {
        if (!ReplicationValidation.IsHash256(mappingSetHash)) return new DeltaChainResult(DeltaChainStatus.Invalid, Array.Empty<DeltaRecord>());
        DeltaChainResult result = TryGetContiguous(baseSnapshotId, fromRevision, toRevision);
        if (result.Status != DeltaChainStatus.Complete) return result;
        foreach (DeltaRecord record in result.Records)
            if (record.MappingSetHash != mappingSetHash)
                return new DeltaChainResult(DeltaChainStatus.Gap, Array.Empty<DeltaRecord>());
        return result;
    }

    public DeltaChainResult TryBuildRepairRange(string baseSnapshotId, ulong fromRevision, ulong toRevision, string mappingSetHash, int schemaEpoch)
    {
        if (!ReplicationValidation.IsHash256(mappingSetHash) || schemaEpoch != GeneratedContractManifest.SchemaEpoch)
            return new DeltaChainResult(DeltaChainStatus.Invalid, Array.Empty<DeltaRecord>());
        DeltaChainResult result = TryGetContiguous(baseSnapshotId, fromRevision, toRevision, schemaEpoch);
        if (result.Status != DeltaChainStatus.Complete) return result;
        foreach (DeltaRecord record in result.Records)
            if (record.MappingSetHash != mappingSetHash)
                return new DeltaChainResult(DeltaChainStatus.Gap, Array.Empty<DeltaRecord>());
        return result;
    }

    public IReadOnlyList<DeltaRecord> Snapshot() => SnapshotCore(default, false);

    public IReadOnlyList<DeltaRecord> Snapshot(IdentityStoreToken token) => SnapshotCore(token, true);

    private IReadOnlyList<DeltaRecord> SnapshotCore(IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active)
                return Array.Empty<DeltaRecord>();
            return new ReadOnlyCollection<DeltaRecord>(_records.ToArray());
        }
    }

    private bool IsKnownBaselineLocked(string baseSnapshotId) =>
        _baselines.ContainsKey(baseSnapshotId) ||
        _acknowledged.ContainsKey(baseSnapshotId) ||
        _records.Any(value => value.BaseSnapshotId == baseSnapshotId);

    private DeltaChainStatus CanSatisfyEmptyRangeLocked(string baseSnapshotId, ulong revision)
    {
        BaselineCursor baseline = _baselines[baseSnapshotId];
        if (_acknowledged.TryGetValue(baseSnapshotId, out AckCursor acknowledged))
        {
            if (revision < acknowledged.Revision) return DeltaChainStatus.HistoryExhausted;
            if (revision == acknowledged.Revision) return DeltaChainStatus.Complete;
            return ReachabilityStatusLocked(baseSnapshotId, acknowledged.Revision, acknowledged.Sequence, revision);
        }
        if (!baseline.StartKnown)
            return _records.Any(value => value.BaseSnapshotId == baseSnapshotId)
                ? DeltaChainStatus.Gap
                : DeltaChainStatus.HistoryExhausted;
        if (revision == baseline.Revision) return DeltaChainStatus.Complete;
        if (revision < baseline.Revision) return DeltaChainStatus.Gap;
        return ReachabilityStatusLocked(baseSnapshotId, baseline.Revision, baseline.Sequence, revision);
    }

    private DeltaChainStatus ReachabilityStatusLocked(string baseSnapshotId, ulong fromRevision, ulong baselineSequence, ulong toRevision)
    {
        ulong expectedSequence = IncrementOrFail(baselineSequence);
        if (expectedSequence == 0) return DeltaChainStatus.Gap;
        ulong cursor = fromRevision;
        while (cursor < toRevision)
        {
            DeltaRecord? next = _records.FirstOrDefault(value =>
                value.BaseSnapshotId == baseSnapshotId &&
                value.FromRevision == cursor &&
                value.Sequence == expectedSequence);
            if (next is null)
            {
                bool hasRevisionLink = _records.Any(value => value.BaseSnapshotId == baseSnapshotId && value.FromRevision == cursor);
                bool hasRetainedHistory = _records.Any(value => value.BaseSnapshotId == baseSnapshotId);
                return hasRevisionLink || hasRetainedHistory ? DeltaChainStatus.Gap : DeltaChainStatus.HistoryExhausted;
            }
            if (next.ToRevision > toRevision) return DeltaChainStatus.Gap;
            cursor = next.ToRevision;
            if (expectedSequence == ulong.MaxValue && cursor < toRevision) return DeltaChainStatus.Gap;
            expectedSequence++;
        }
        return cursor == toRevision ? DeltaChainStatus.Complete : DeltaChainStatus.Gap;
    }

    private bool TryGetExpectedStartLocked(string baseSnapshotId, ulong fromRevision, out ulong expectedSequence)
    {
        ulong cursorRevision;
        ulong cursorSequence;
        if (_acknowledged.TryGetValue(baseSnapshotId, out AckCursor acknowledged))
        {
            cursorRevision = acknowledged.Revision;
            cursorSequence = acknowledged.Sequence;
        }
        else
        {
            BaselineCursor baseline = _baselines[baseSnapshotId];
            if (!baseline.StartKnown)
            {
                expectedSequence = 0;
                return false;
            }

            cursorRevision = baseline.Revision;
            cursorSequence = baseline.Sequence;
        }

        if (fromRevision < cursorRevision)
        {
            expectedSequence = 0;
            return false;
        }

        while (cursorRevision < fromRevision)
        {
            if (cursorSequence == ulong.MaxValue)
            {
                expectedSequence = 0;
                return false;
            }

            ulong nextSequence = cursorSequence + 1;
            DeltaRecord? next = _records.FirstOrDefault(value =>
                value.BaseSnapshotId == baseSnapshotId &&
                value.FromRevision == cursorRevision &&
                value.Sequence == nextSequence);
            if (next is null || next.ToRevision > fromRevision)
            {
                expectedSequence = 0;
                return false;
            }

            cursorRevision = next.ToRevision;
            cursorSequence = next.Sequence;
        }

        if (cursorRevision == fromRevision && cursorSequence != ulong.MaxValue)
        {
            expectedSequence = cursorSequence + 1;
            return true;
        }

        expectedSequence = 0;
        return false;
    }

    private static ulong IncrementOrFail(ulong value) => value == ulong.MaxValue ? 0 : value + 1;

    private static bool SamePayload(DeltaRecord left, DeltaRecord right) =>
        left.BaseSnapshotId == right.BaseSnapshotId &&
        left.FromRevision == right.FromRevision &&
        left.ToRevision == right.ToRevision &&
        left.Sequence == right.Sequence &&
        left.Bytes == right.Bytes &&
        left.MappingSetHash == right.MappingSetHash &&
        left.SchemaEpoch == right.SchemaEpoch &&
        SameProjection(left.Projection, right.Projection) &&
        (string.IsNullOrEmpty(left.IdempotencyKey) || string.IsNullOrEmpty(right.IdempotencyKey) ||
            left.IdempotencyKey == right.IdempotencyKey);

    private static bool SameAuthoritativePayload(DeltaRecord left, DeltaRecord right) =>
        left.BaseSnapshotId == right.BaseSnapshotId &&
        left.FromRevision == right.FromRevision &&
        left.ToRevision == right.ToRevision &&
        left.Bytes == right.Bytes &&
        left.MappingSetHash == right.MappingSetHash &&
        left.SchemaEpoch == right.SchemaEpoch &&
        SameProjection(left.Projection, right.Projection);

    private static bool SameProjection(DeltaProjection? left, DeltaProjection? right) =>
        left is null || right is null ||
        left.SessionId == right.SessionId &&
        left.ProductId == right.ProductId &&
        left.GameReleaseId == right.GameReleaseId &&
        left.BaseSnapshotId == right.BaseSnapshotId &&
        left.FromRevision == right.FromRevision &&
        left.ToRevision == right.ToRevision &&
        left.ConfirmationSequence == right.ConfirmationSequence &&
        left.MappingSetHash == right.MappingSetHash &&
        left.BodyJson == right.BodyJson;

    internal bool TryGetByIdempotencyKey(string idempotencyKey, out DeltaRecord? record)
    {
        lock (_scope.Gate)
        {
            if (_scope.State == IdentityStoreState.Active && !string.IsNullOrEmpty(idempotencyKey) &&
                _idempotency.TryGetValue(idempotencyKey, out record))
                return true;
        }

        record = null;
        return false;
    }

    private int RemoveLocked(Func<DeltaRecord, bool> predicate)
    {
        var removed = 0;
        for (var index = _records.Count - 1; index >= 0; index--)
        {
            if (!predicate(_records[index])) continue;
            _bytes -= _records[index].Bytes;
            _records.RemoveAt(index);
            removed++;
        }

        return removed;
    }

    private void ClearLocked()
    {
        _records.Clear();
        _acknowledged.Clear();
        _baselines.Clear();
        _idempotency.Clear();
        _idempotencySizes.Clear();
        _idempotencyOrder.Clear();
        _idempotencyBytes = 0;
        _bytes = 0;
        _implicitBaselineAdmissionSealed = false;
    }

    private bool CanRememberIdempotencyLocked(string key, long size) =>
        !string.IsNullOrEmpty(key) && !_idempotency.ContainsKey(key) &&
        _idempotency.Count < _budget.HistoryWindow && size >= 0 && size <= _budget.HistoryBytes &&
        _idempotencyBytes <= _budget.HistoryBytes - size;

    private bool RememberIdempotencyLocked(DeltaRecord record, long size)
    {
        if (!CanRememberIdempotencyLocked(record.IdempotencyKey, size)) return false;
        long admittedBytes = _idempotencyBytes + size;
        // Keep the oldest durable replay outcomes instead of evicting an
        // identity that may still be retried after its stream record is ACKed.
        _idempotency[record.IdempotencyKey] = record;
        _idempotencySizes[record.IdempotencyKey] = size;
        _idempotencyOrder.Enqueue(record.IdempotencyKey);
        _idempotencyBytes = admittedBytes;
        return true;
    }

    private static bool TryGetIdempotencySize(DeltaRecord record, out long size)
    {
        size = 0;
        long keyBytes = Encoding.UTF8.GetByteCount(record.IdempotencyKey);
        if (record.Bytes > long.MaxValue - keyBytes) return false;
        size = record.Bytes + keyBytes;
        return true;
    }

    private void RemoveIdempotencyForBaseLocked(string baseSnapshotId)
    {
        string[] keys = _idempotency
            .Where(item => item.Value.BaseSnapshotId == baseSnapshotId)
            .Select(item => item.Key)
            .ToArray();
        foreach (string key in keys) RemoveIdempotencyLocked(key);
        if (keys.Length > 0)
        {
            string[] retained = _idempotencyOrder
                .Where(key => _idempotency.ContainsKey(key))
                .ToArray();
            _idempotencyOrder.Clear();
            foreach (string key in retained) _idempotencyOrder.Enqueue(key);
        }
    }

    private void ForgetReleasedIdentitiesLocked(IReadOnlyList<DeltaRecord> released)
    {
        foreach (DeltaRecord record in released)
        {
            if (string.IsNullOrEmpty(record.IdempotencyKey) ||
                _records.Any(value => value.IdempotencyKey == record.IdempotencyKey))
                continue;
            RemoveIdempotencyLocked(record.IdempotencyKey);
        }

        if (released.Count > 0)
        {
            string[] retained = _idempotencyOrder
                .Where(key => _idempotency.ContainsKey(key))
                .ToArray();
            _idempotencyOrder.Clear();
            foreach (string key in retained) _idempotencyOrder.Enqueue(key);
        }
    }

    private void RemoveIdempotencyLocked(string key)
    {
        _idempotency.Remove(key);
        if (_idempotencySizes.Remove(key, out long size)) _idempotencyBytes -= size;
    }

    internal void ClearContextLocked() => ClearLocked();

    internal void ResetForBaselineContextLocked(string baseSnapshotId, ulong baselineSequence, ulong baselineRevision)
    {
        ClearLocked();
        _baselines[baseSnapshotId] = new BaselineCursor(baselineSequence, baselineRevision, true, true);
    }

    private static int Compare(DeltaRecord left, DeltaRecord right)
    {
        int baseId = StringComparer.Ordinal.Compare(left.BaseSnapshotId, right.BaseSnapshotId);
        if (baseId != 0) return baseId;
        int from = left.FromRevision.CompareTo(right.FromRevision);
        return from != 0 ? from : left.Sequence.CompareTo(right.Sequence);
    }

    private readonly record struct BaselineCursor(ulong Sequence, ulong Revision, bool Explicit, bool StartKnown);

    private readonly record struct AckCursor(ulong Sequence, ulong Revision);
}

public sealed class DeltaHistoryView
{
    private readonly DeltaHistory _store;

    internal DeltaHistoryView(DeltaHistory store) => _store = store;

    public int Count => _store.Count;

    public long Bytes => _store.Bytes;

    public ulong Generation => _store.Generation;

    public ulong WorkEpoch => _store.WorkEpoch;

    public IdentityStoreState State => _store.State;

    public bool IsActive => _store.IsActive;

    public bool IsClosed => _store.IsClosed;

    public DeltaChainResult TryGetContiguous(string baseSnapshotId, ulong fromRevision, ulong toRevision) =>
        _store.TryGetContiguous(baseSnapshotId, fromRevision, toRevision);

    public DeltaChainResult TryGetContiguous(string baseSnapshotId, ulong fromRevision, ulong toRevision, int schemaEpoch) =>
        _store.TryGetContiguous(baseSnapshotId, fromRevision, toRevision, schemaEpoch);

    public DeltaChainResult TryBuildRepairRange(string baseSnapshotId, ulong fromRevision, ulong toRevision) =>
        _store.TryBuildRepairRange(baseSnapshotId, fromRevision, toRevision);

    public DeltaChainResult TryBuildRepairRange(string baseSnapshotId, ulong fromRevision, ulong toRevision, string mappingSetHash) =>
        _store.TryBuildRepairRange(baseSnapshotId, fromRevision, toRevision, mappingSetHash);

    public DeltaChainResult TryBuildRepairRange(
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        string mappingSetHash,
        int schemaEpoch) =>
        _store.TryBuildRepairRange(baseSnapshotId, fromRevision, toRevision, mappingSetHash, schemaEpoch);

    public IReadOnlyList<DeltaRecord> Snapshot() => _store.Snapshot();
}
