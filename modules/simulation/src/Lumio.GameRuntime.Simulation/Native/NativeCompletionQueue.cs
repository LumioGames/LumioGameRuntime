using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Simulation.Native;

public enum NativeCompletionStatus
{
    Accepted,
    Duplicate,
    QueueFull,
    Invalid,
    StaleGeneration,
    Closed
}

public readonly record struct NativeCompletion
{
    private readonly byte[] _payload;

    public NativeCompletion(string jobId, string token, ulong generation, byte[] payload)
    {
        JobId = jobId;
        Token = token;
        Generation = generation;
        _payload = payload is null ? Array.Empty<byte>() : (byte[])payload.Clone();
    }

    public string JobId { get; }

    public string Token { get; }

    public ulong Generation { get; }

    public ReadOnlyMemory<byte> Payload => (byte[])(_payload ?? Array.Empty<byte>()).Clone();

    public int Length => _payload?.Length ?? 0;

    public NativeCompletion Snapshot() => new(JobId, Token, Generation, _payload ?? Array.Empty<byte>());
}

public sealed class NativeCompletionQueue
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly Queue<NativeCompletion> _items = new();
    private readonly HashSet<NativeIdentity> _identities = new();
    private long _bytes;
    private bool _closed;

    public NativeCompletionQueue(int capacity, long maxBytes)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _capacity = capacity;
        _maxBytes = maxBytes;
    }

    public NativeCompletionQueue(NativeCompletionBudget budget)
        : this(budget.Capacity, budget.MaxBytes)
    {
    }

    public bool StopDispatchSignal { get; private set; }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public NativeCompletionStatus TryPublish(in NativeCompletion completion, ulong currentGeneration)
    {
        if (_closed) return NativeCompletionStatus.Closed;
        if (!SimulationValidation.IsIdentifier(completion.JobId) || !SimulationValidation.IsIdentifier(completion.Token) || completion.Length <= 0)
            return NativeCompletionStatus.Invalid;
        if (completion.Generation != currentGeneration) return NativeCompletionStatus.StaleGeneration;
        var identity = new NativeIdentity(completion.JobId, completion.Token, completion.Generation);
        lock (_gate)
        {
            if (_closed) return NativeCompletionStatus.Closed;
            if (_identities.Contains(identity)) return NativeCompletionStatus.Duplicate;
            if (_items.Count >= _capacity || completion.Length > _maxBytes - _bytes)
            {
                StopDispatchSignal = true;
                return NativeCompletionStatus.QueueFull;
            }

            _items.Enqueue(completion.Snapshot());
            _identities.Add(identity);
            _bytes += completion.Length;
            return NativeCompletionStatus.Accepted;
        }
    }

    public NativeCompletionBatch DrainAtBarrier(ulong currentGeneration)
    {
        var values = new List<NativeCompletion>();
        lock (_gate)
        {
            while (_items.Count > 0)
            {
                NativeCompletion item = _items.Dequeue();
                _identities.Remove(new NativeIdentity(item.JobId, item.Token, item.Generation));
                _bytes -= item.Length;
                if (item.Generation == currentGeneration) values.Add(item);
            }

            StopDispatchSignal = false;
        }

        return NativeCompletionMerger.Merge(values, currentGeneration);
    }

    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    public NativeCompletionBatch Drain(ulong currentGeneration) => DrainAtBarrier(currentGeneration);

    public void Complete()
    {
        lock (_gate) _closed = true;
    }

    private readonly record struct NativeIdentity(string JobId, string Token, ulong Generation);
}
