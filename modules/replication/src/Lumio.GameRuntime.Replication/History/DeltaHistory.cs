using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumio.GameRuntime.Replication.History;

public enum DeltaHistoryStatus
{
    Accepted,
    Duplicate,
    QueueFull,
    Invalid,
    RevisionConflict
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
    string MappingSetHash = "");

public sealed class DeltaChainResult
{
    internal DeltaChainResult(DeltaChainStatus status, IReadOnlyList<DeltaRecord> records)
    {
        Status = status;
        Records = records;
    }

    public DeltaChainStatus Status { get; }

    public IReadOnlyList<DeltaRecord> Records { get; }

    public bool RequiresResync => Status is not DeltaChainStatus.Complete;

    public bool RequiresFullResync => Status is DeltaChainStatus.UnknownBaseline or DeltaChainStatus.HistoryExhausted;

    public string ResyncReason => Status.ToString();
}

public sealed class DeltaHistory
{
    private readonly object _gate = new();
    private readonly ReplicationBudget _budget;
    private readonly List<DeltaRecord> _records = new();
    private long _bytes;

    public DeltaHistory(ReplicationBudget budget)
    {
        if (!budget.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));
        _budget = budget;
    }

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    public DeltaHistoryStatus Add(DeltaRecord record)
    {
        if (record is null || !ReplicationValidation.IsIdentifier(record.BaseSnapshotId) || record.ToRevision <= record.FromRevision || record.Bytes < 0 || (record.MappingSetHash.Length != 0 && !ReplicationValidation.IsHash256(record.MappingSetHash)))
            return DeltaHistoryStatus.Invalid;
        lock (_gate)
        {
            DeltaRecord? existing = _records.FirstOrDefault(value => value.BaseSnapshotId == record.BaseSnapshotId && value.Sequence == record.Sequence);
            if (existing is not null)
                return existing == record ? DeltaHistoryStatus.Duplicate : DeltaHistoryStatus.RevisionConflict;
            if (_records.Count >= _budget.HistoryWindow || record.Bytes > _budget.HistoryBytes - _bytes) return DeltaHistoryStatus.QueueFull;
            _records.Add(record);
            _bytes += record.Bytes;
            _records.Sort(Compare);
            return DeltaHistoryStatus.Accepted;
        }
    }

    public DeltaChainResult TryGetContiguous(string baseSnapshotId, ulong fromRevision, ulong toRevision)
    {
        if (!ReplicationValidation.IsIdentifier(baseSnapshotId) || toRevision < fromRevision)
            return new DeltaChainResult(DeltaChainStatus.Invalid, Array.Empty<DeltaRecord>());
        if (fromRevision == toRevision)
            return new DeltaChainResult(DeltaChainStatus.Complete, Array.Empty<DeltaRecord>());
        lock (_gate)
        {
            var selected = new List<DeltaRecord>();
            ulong cursor = fromRevision;
            while (cursor < toRevision)
            {
                DeltaRecord? next = null;
                foreach (DeltaRecord value in _records)
                {
                    if (value.BaseSnapshotId == baseSnapshotId && value.FromRevision == cursor && (next is null || value.Sequence < next.Sequence)) next = value;
                }
                if (next is null)
                {
                    // A known base with a missing revision link is a gap; an entirely
                    // unknown base is the distinct UnknownBaseline case.
                    bool knownBase = _records.Any(value => value.BaseSnapshotId == baseSnapshotId);
                    return new DeltaChainResult(!knownBase && selected.Count == 0 ? DeltaChainStatus.UnknownBaseline : DeltaChainStatus.Gap, selected);
                }
                // A link may not jump past the requested endpoint. Returning a
                // truncated chain would let callers apply state they did not ask for.
                if (next.ToRevision > toRevision)
                    return new DeltaChainResult(DeltaChainStatus.Gap, selected);
                selected.Add(next);
                cursor = next.ToRevision;
            }
            return new DeltaChainResult(cursor == toRevision ? DeltaChainStatus.Complete : DeltaChainStatus.Gap, selected);
        }
    }

    public DeltaHistoryStatus Append(DeltaRecord record) => Add(record);

    public DeltaChainResult TryBuildRepairRange(string baseSnapshotId, ulong fromRevision, ulong toRevision) =>
        TryGetContiguous(baseSnapshotId, fromRevision, toRevision);

    public DeltaChainResult TryBuildRepairRange(string baseSnapshotId, ulong fromRevision, ulong toRevision, string mappingSetHash)
    {
        if (!ReplicationValidation.IsHash256(mappingSetHash)) return new DeltaChainResult(DeltaChainStatus.Invalid, Array.Empty<DeltaRecord>());
        DeltaChainResult result = TryGetContiguous(baseSnapshotId, fromRevision, toRevision);
        if (result.Status != DeltaChainStatus.Complete) return result;
        foreach (DeltaRecord record in result.Records)
            if (!string.IsNullOrEmpty(record.MappingSetHash) && record.MappingSetHash != mappingSetHash)
                return new DeltaChainResult(DeltaChainStatus.Gap, Array.Empty<DeltaRecord>());
        return result;
    }

    public IReadOnlyList<DeltaRecord> Snapshot()
    {
        lock (_gate) return _records.ToArray();
    }

    private static int Compare(DeltaRecord left, DeltaRecord right)
    {
        int baseId = StringComparer.Ordinal.Compare(left.BaseSnapshotId, right.BaseSnapshotId);
        if (baseId != 0) return baseId;
        int from = left.FromRevision.CompareTo(right.FromRevision);
        return from != 0 ? from : left.Sequence.CompareTo(right.Sequence);
    }
}
