using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Observability;

internal sealed class DurableEvidenceRouter : IDurableEvidencePort
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, DurableRecordView> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _recordSequences = new(StringComparer.Ordinal);
    private ulong _nextRecordSequence;
    private bool _closed;

    internal DurableEvidenceRouter(int capacity)
    {
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
#else
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
#endif
        _capacity = capacity;
    }

    public DurableEnqueueResult Enqueue(in DurableRecordView record)
    {
        if (!record.IsWellFormed)
        {
            return new DurableEnqueueResult(DurableEnqueueStatus.Rejected, 0UL, false, "ManifestMalformed");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return new DurableEnqueueResult(DurableEnqueueStatus.Closed, 0UL, false, "ManifestMalformed");
            }

            if (_records.TryGetValue(record.IdempotencyKey, out DurableRecordView existing))
            {
                return new DurableEnqueueResult(
                    DurableEnqueueStatus.Accepted,
                    _recordSequences[existing.IdempotencyKey],
                    true,
                    null);
            }

            if (_records.Count >= _capacity)
            {
                return new DurableEnqueueResult(DurableEnqueueStatus.Backpressured, 0UL, false, "QueueFull");
            }

            var copy = record with { Payload = record.Payload.ToArray() };
            _records.Add(copy.IdempotencyKey, copy);
            _nextRecordSequence = checked(_nextRecordSequence + 1UL);
            _recordSequences.Add(copy.IdempotencyKey, _nextRecordSequence);
            return new DurableEnqueueResult(DurableEnqueueStatus.Accepted, _nextRecordSequence, false, null);
        }
    }

    public DurableQueryResult Query(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new DurableQueryResult(DurableQueryStatus.Rejected, null, "ManifestMalformed");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return new DurableQueryResult(DurableQueryStatus.Closed, null, "ManifestMalformed");
            }

            return _records.TryGetValue(idempotencyKey, out DurableRecordView record)
                ? new DurableQueryResult(DurableQueryStatus.Found, record with { Payload = record.Payload.ToArray() }, null)
                : new DurableQueryResult(DurableQueryStatus.NotFound, null, null);
        }
    }

    internal void Complete()
    {
        lock (_gate) _closed = true;
    }

}
