using System;
using System.Collections.Generic;
using System.Threading.Channels;

namespace Lumio.GameRuntime.Simulation.Native;

public enum NativeCompletionStatus
{
    Accepted,
    Duplicate,
    QueueFull,
    Invalid,
    StaleGeneration,
    Closed,
    Faulted
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
    private readonly Channel<NativeCompletion> _channel;
    private readonly HashSet<NativeIdentity> _identities = new();
    private int _count;
    private long _bytes;
    private bool _closed;
    private bool _faulted;
    private string? _generatedErrorId;

    public NativeCompletionQueue(int capacity, long maxBytes)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _capacity = capacity;
        _maxBytes = maxBytes;
        _channel = Channel.CreateBounded<NativeCompletion>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public NativeCompletionQueue(NativeCompletionBudget budget)
        : this(budget.Capacity, budget.MaxBytes)
    {
    }

    public NativeCompletionQueue(int NativeCompletionQueueCapacity)
        : this(new NativeCompletionBudget(NativeCompletionQueueCapacity))
    {
    }

    public bool StopDispatchSignal { get; private set; }

    public bool AdmissionStopped
    {
        get { lock (_gate) return _faulted || StopDispatchSignal; }
    }

    public bool IsFaulted
    {
        get { lock (_gate) return _faulted; }
    }

    public string? GeneratedErrorId
    {
        get { lock (_gate) return _generatedErrorId; }
    }

    public int Count
    {
        get { lock (_gate) return _count; }
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
            if (_faulted)
            {
                StopDispatchSignal = true;
                return NativeCompletionStatus.Faulted;
            }

            if (_identities.Contains(identity)) return NativeCompletionStatus.Duplicate;
            // Reliable completions cannot be dropped: overflow faults and stops admission.
            if (_count >= _capacity || completion.Length > _maxBytes - _bytes || !_channel.Writer.TryWrite(completion.Snapshot()))
                return FaultLocked();

            _identities.Add(identity);
            _bytes += completion.Length;
            _count++;
            return NativeCompletionStatus.Accepted;
        }
    }

    public NativeCompletionBatch DrainAtBarrier(ulong currentGeneration)
    {
        var values = new List<NativeCompletion>();
        lock (_gate)
        {
            while (_channel.Reader.TryRead(out NativeCompletion item))
            {
                _count--;
                _identities.Remove(new NativeIdentity(item.JobId, item.Token, item.Generation));
                _bytes -= item.Length;
                if (item.Generation == currentGeneration) values.Add(item);
            }

            StopDispatchSignal = _faulted;
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
        lock (_gate)
        {
            _closed = true;
            _channel.Writer.TryComplete();
        }
    }

    private NativeCompletionStatus FaultLocked()
    {
        _faulted = true;
        StopDispatchSignal = true;
        _generatedErrorId = SimulationValidation.IsStableErrorId("QueueFull") ? "QueueFull" : "InternalInvariant";
        return NativeCompletionStatus.Faulted;
    }

    private readonly record struct NativeIdentity(string JobId, string Token, ulong Generation);
}
