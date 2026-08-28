using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Channels;

namespace Lumio.GameRuntime.Observability;

internal sealed class DiagnosticEventQueue
{
    private readonly object _gate = new();
    private readonly DiagnosticQueueBudget _budget;
    private readonly Channel<RuntimeEventView> _channel;
    private int _count;
    private long _bytes;
    private long _droppedCount;
    private long _droppedBytes;
    private bool _closed;

    private DiagnosticEventQueue(DiagnosticQueueBudget budget)
    {
        if (!budget.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));

        _budget = budget;
        _channel = Channel.CreateBounded<RuntimeEventView>(new BoundedChannelOptions(budget.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    internal static DiagnosticEventQueue Create(DiagnosticQueueBudget budget) => new(budget);

    internal DiagnosticWriteResult TryWrite(in RuntimeEventView value)
    {
        if (!value.IsWellFormed)
        {
            return DiagnosticWriteResult.Rejected("ManifestMalformed");
        }

        var bytes = EstimateBytes(value);
        lock (_gate)
        {
            if (_closed)
            {
                return DiagnosticWriteResult.Closed();
            }

            if (_count >= _budget.Capacity || _bytes > _budget.MaxBytes - bytes || !_channel.Writer.TryWrite(value))
            {
                _droppedCount++;
                _droppedBytes += bytes;
                return DiagnosticWriteResult.DroppedBestEffort();
            }

            _count++;
            _bytes += bytes;
            return DiagnosticWriteResult.Accepted();
        }
    }

    internal IReadOnlyList<RuntimeEventView> ReadBatch(int maxItems)
    {
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);
#else
        if (maxItems <= 0) throw new ArgumentOutOfRangeException(nameof(maxItems));
#endif

        var values = new List<RuntimeEventView>(maxItems);
        while (values.Count < maxItems && _channel.Reader.TryRead(out RuntimeEventView value))
        {
            lock (_gate)
            {
                _count--;
                _bytes -= EstimateBytes(value);
            }

            values.Add(value);
        }

        return values;
    }

    internal int Count
    {
        get
        {
            lock (_gate) return _count;
        }
    }

    internal long DroppedCount
    {
        get
        {
            lock (_gate) return _droppedCount;
        }
    }

    internal DiagnosticDropSummary DropSummary
    {
        get
        {
            lock (_gate) return new DiagnosticDropSummary(_droppedCount, _droppedBytes, "QueueFull");
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _channel.Writer.TryComplete();
        }
    }

    private static long EstimateBytes(in RuntimeEventView value)
    {
        var characters = LengthOf(value.EventId) + LengthOf(value.Category) + LengthOf(value.Severity) +
            LengthOf(value.Message) + LengthOf(value.Durability) + LengthOf(value.Correlation.Scope) +
            LengthOf(value.Correlation.ProductId) + LengthOf(value.Correlation.GameReleaseId) +
            LengthOf(value.Correlation.SessionId) + LengthOf(value.Correlation.WorldId) +
            LengthOf(value.Correlation.TraceId) + LengthOf(value.Correlation.ProducerId);
        return checked(Encoding.UTF8.GetByteCount(new string('x', characters)) + sizeof(ulong));
    }

    private static int LengthOf(string? value) => value?.Length ?? 0;
}
