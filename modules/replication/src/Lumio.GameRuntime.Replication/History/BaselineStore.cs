using System;
using System.Collections.Generic;

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
    bool Acknowledged = false);

public sealed class BaselineStore
{
    private readonly object _gate = new();
    private readonly ReplicationBudget _budget;
    private readonly Dictionary<string, BaselineRecord> _records = new(StringComparer.Ordinal);
    private long _bytes;

    public BaselineStore(ReplicationBudget budget)
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

    public BaselineStoreStatus Add(BaselineRecord record)
    {
        if (record is null || !ReplicationValidation.IsIdentifier(record.SnapshotId) || record.Bytes < 0 || record.SchemaEpoch < 0 || (record.MappingSetHash.Length != 0 && !ReplicationValidation.IsHash256(record.MappingSetHash))) return BaselineStoreStatus.Invalid;
        lock (_gate)
        {
            if (_records.TryGetValue(record.SnapshotId, out BaselineRecord? existing))
                return existing == record ? BaselineStoreStatus.Duplicate : BaselineStoreStatus.RevisionConflict;
            if (_records.Count >= _budget.HistoryWindow || record.Bytes > _budget.HistoryBytes - _bytes) return BaselineStoreStatus.QueueFull;
            _records.Add(record.SnapshotId, record);
            _bytes += record.Bytes;
            return BaselineStoreStatus.Accepted;
        }
    }

    public BaselineAckStatus Ack(string snapshotId, ulong confirmedRevision)
    {
        if (!ReplicationValidation.IsIdentifier(snapshotId)) return BaselineAckStatus.Invalid;
        lock (_gate)
        {
            if (!_records.TryGetValue(snapshotId, out BaselineRecord? record)) return BaselineAckStatus.UnknownBaseline;
            if (record.Revision != confirmedRevision) return BaselineAckStatus.RevisionConflict;
            if (record.Acknowledged) return BaselineAckStatus.AlreadyAcknowledged;
            _records[snapshotId] = record with { Acknowledged = true };
            return BaselineAckStatus.Acknowledged;
        }
    }

    public BaselineStoreStatus Stage(BaselineRecord record) => Add(record);

    public BaselineAckStatus Acknowledge(string snapshotId, ulong confirmedRevision) => Ack(snapshotId, confirmedRevision);

    public bool IsAcknowledged(string snapshotId)
    {
        lock (_gate) return _records.TryGetValue(snapshotId, out BaselineRecord? value) && value.Acknowledged;
    }

    public bool TryGet(string snapshotId, out BaselineRecord? record)
    {
        lock (_gate) return _records.TryGetValue(snapshotId, out record);
    }

    public IReadOnlyList<BaselineRecord> Snapshot()
    {
        lock (_gate)
        {
            var values = new List<BaselineRecord>(_records.Values);
            values.Sort((left, right) => StringComparer.Ordinal.Compare(left.SnapshotId, right.SnapshotId));
            return values;
        }
    }
}
