using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Coordination;

public enum TxnLookupStatus
{
    New,
    Duplicate,
    Conflict,
    Full
}

public readonly record struct TxnLookupResult(
    TxnLookupStatus Status,
    TxnRecord? Record,
    CoordinationFailure? Failure)
{
    public bool IsDuplicate => Status == TxnLookupStatus.Duplicate;
}

/// <summary>Bounded session transaction index; request digest conflicts are fatal.</summary>
public sealed class TxnIdempotencyIndex
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, TxnRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _requestDigests = new(StringComparer.Ordinal);

    public TxnIdempotencyIndex(int capacity = 4096)
    {
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
#else
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
#endif
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    public TxnLookupResult Register(TxnRecord record)
    {
        if (record is null)
        {
            return new TxnLookupResult(TxnLookupStatus.Conflict, null, CoordinationFailure.Rejected("InvalidArgument", "Transaction is required."));
        }

        lock (_gate)
        {
            if (_records.TryGetValue(record.TxnId, out TxnRecord? existing))
            {
                if (string.Equals(_requestDigests[record.TxnId], record.RequestDigest, StringComparison.Ordinal))
                    return new TxnLookupResult(TxnLookupStatus.Duplicate, existing, null);
                return new TxnLookupResult(
                    TxnLookupStatus.Conflict,
                    existing,
                    CoordinationFailure.Fatal("InvalidArgument", "A transaction ID was reused with a different request digest."));
            }

            if (_records.Count >= _capacity)
            {
                return new TxnLookupResult(
                    TxnLookupStatus.Full,
                    null,
                    CoordinationFailure.Retryable("QueueFull", "Transaction idempotency index is full."));
            }

            _records.Add(record.TxnId, record);
            _requestDigests.Add(record.TxnId, record.RequestDigest);
            return new TxnLookupResult(TxnLookupStatus.New, record, null);
        }
    }

    public bool TryGet(string txnId, out TxnRecord? record)
    {
        lock (_gate) return _records.TryGetValue(txnId, out record);
    }

    public IReadOnlyList<TxnRecord> Snapshot()
    {
        lock (_gate) return new List<TxnRecord>(_records.Values).AsReadOnly();
    }

    public TxnLookupResult Lookup(string txnId, string requestDigest)
    {
        if (string.IsNullOrWhiteSpace(txnId) || string.IsNullOrWhiteSpace(requestDigest))
            return new TxnLookupResult(TxnLookupStatus.Conflict, null, CoordinationFailure.Rejected("InvalidArgument", "Transaction ID and digest are required."));

        lock (_gate)
        {
            if (!_records.TryGetValue(txnId, out TxnRecord? record)) return new TxnLookupResult(TxnLookupStatus.New, null, null);
            return string.Equals(record.RequestDigest, requestDigest, StringComparison.Ordinal)
                ? new TxnLookupResult(TxnLookupStatus.Duplicate, record, null)
                : new TxnLookupResult(TxnLookupStatus.Conflict, record,
                    CoordinationFailure.Fatal("InvalidArgument", "A transaction ID was reused with a different request digest."));
        }
    }
}
