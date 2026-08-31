using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Lumio.GameRuntime.Simulation.Ingress;

public readonly record struct IngressQueueOptions(int Capacity, long MaxBytes)
{
    public bool IsValid => Capacity > 0 && MaxBytes > 0;
}

public enum IngressArrivalClass
{
    CurrentTick,
    NextTick,
    Rejected
}

public enum IngressEnqueueStatus
{
    Accepted,
    Duplicate,
    QueueFull,
    Invalid,
    StaleGeneration,
    RejectedLate,
    Closed
}

public readonly record struct OpaqueIngress
{
    private readonly byte[] _payload;

    public OpaqueIngress(string sessionId, ulong clientCommandSequence, ulong targetTickId, ulong generation, byte[] payload)
    {
        SessionId = sessionId;
        ClientCommandSequence = clientCommandSequence;
        TargetTickId = targetTickId;
        Generation = generation;
        _payload = payload is null ? Array.Empty<byte>() : (byte[])payload.Clone();
    }

    public string SessionId { get; }

    public ulong ClientCommandSequence { get; }

    public ulong TargetTickId { get; }

    public ulong Generation { get; }

    public byte[] Payload => (byte[])(_payload ?? Array.Empty<byte>()).Clone();

    public int Length => _payload?.Length ?? 0;

    public OpaqueIngress Snapshot() => new(SessionId, ClientCommandSequence, TargetTickId, Generation, _payload ?? Array.Empty<byte>());
}

public sealed class CanonicalInputBatch
{
    internal CanonicalInputBatch(ulong tickId, IList<OpaqueIngress> items, string canonicalHashHex)
    {
        TickId = tickId;
        var copies = new List<OpaqueIngress>(items.Count);
        foreach (OpaqueIngress item in items) copies.Add(item.Snapshot());
        Items = new ReadOnlyCollection<OpaqueIngress>(copies);
        CanonicalHashHex = canonicalHashHex;
    }

    public ulong TickId { get; }

    public IReadOnlyList<OpaqueIngress> Items { get; }

    public string CanonicalHashHex { get; }
}

public readonly record struct IngressCaptureResult(
    bool Succeeded,
    CanonicalInputBatch? Batch,
    int RejectedLateCount,
    string? GeneratedErrorId);

public sealed class IngressQueue
{
    private readonly object _gate = new();
    private readonly IngressQueueOptions _options;
    private readonly Queue<OpaqueIngress> _items = new();
    private readonly HashSet<IngressIdentity> _identities = new();
    private long _bytes;
    private bool _closed;

    public IngressQueue(IngressQueueOptions options)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
    }

    public IngressQueue(IngressBudget budget)
        : this((IngressQueueOptions)budget)
    {
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    public IngressEnqueueStatus TryEnqueue(OpaqueIngress value) => TryEnqueue(in value);

    public IngressEnqueueStatus TryEnqueue(in OpaqueIngress value)
    {
        lock (_gate)
        {
            if (_closed) return IngressEnqueueStatus.Closed;
        }
        if (!SimulationValidation.IsIdentifier(value.SessionId) || value.Payload is null || value.Length <= 0)
            return IngressEnqueueStatus.Invalid;
        var identity = new IngressIdentity(value.SessionId, value.ClientCommandSequence, value.Generation);
        lock (_gate)
        {
            if (_closed) return IngressEnqueueStatus.Closed;
            if (_identities.Contains(identity)) return IngressEnqueueStatus.Duplicate;
            if (_items.Count >= _options.Capacity || value.Length > _options.MaxBytes - _bytes)
                return IngressEnqueueStatus.QueueFull;
            _items.Enqueue(value.Snapshot());
            _identities.Add(identity);
            _bytes += value.Length;
            return IngressEnqueueStatus.Accepted;
        }
    }

    public IngressEnqueueStatus TryEnqueue(in OpaqueIngress value, ulong currentTickId, ulong currentGeneration)
    {
        if (value.Generation != currentGeneration) return IngressEnqueueStatus.StaleGeneration;
        if (value.TargetTickId < currentTickId) return IngressEnqueueStatus.RejectedLate;
        return TryEnqueue(in value);
    }

    public IngressCaptureResult CaptureForTick(ulong tickId)
    {
        var captured = new List<OpaqueIngress>();
        var retained = new Queue<OpaqueIngress>();
        var rejectedLate = 0;
        lock (_gate)
        {
            while (_items.Count > 0)
            {
                OpaqueIngress item = _items.Dequeue();
                if (item.TargetTickId == tickId)
                {
                    captured.Add(item.Snapshot());
                    RemoveIdentity(item);
                }
                else if (item.TargetTickId < tickId)
                {
                    rejectedLate++;
                    RemoveIdentity(item);
                }
                else
                {
                    retained.Enqueue(item);
                }
            }

            while (retained.Count > 0) _items.Enqueue(retained.Dequeue());
        }

        CanonicalInputBatch batch = InputCanonicalizer.Canonicalize(tickId, captured);
        return new IngressCaptureResult(true, batch, rejectedLate, rejectedLate == 0 ? null : "InvalidArgument");
    }

    public IngressCaptureResult CaptureForTick(ulong tickId, ulong currentGeneration)
    {
        var captured = new List<OpaqueIngress>();
        var retained = new Queue<OpaqueIngress>();
        var rejected = 0;
        lock (_gate)
        {
            while (_items.Count > 0)
            {
                OpaqueIngress item = _items.Dequeue();
                if (item.Generation != currentGeneration || item.TargetTickId < tickId)
                {
                    RemoveIdentity(item);
                    rejected++;
                }
                else if (item.TargetTickId == tickId)
                {
                    captured.Add(item.Snapshot());
                    RemoveIdentity(item);
                }
                else retained.Enqueue(item);
            }
            while (retained.Count > 0) _items.Enqueue(retained.Dequeue());
        }

        return new IngressCaptureResult(true, InputCanonicalizer.Canonicalize(tickId, captured), rejected, rejected == 0 ? null : "StaleConnectionGeneration");
    }

    public void Complete()
    {
        lock (_gate) _closed = true;
    }

    public IngressArrivalClass Classify(in OpaqueIngress value, ulong currentTickId)
    {
        if (value.TargetTickId < currentTickId) return IngressArrivalClass.Rejected;
        return value.TargetTickId == currentTickId ? IngressArrivalClass.CurrentTick : IngressArrivalClass.NextTick;
    }

    private void RemoveIdentity(in OpaqueIngress item)
    {
        _identities.Remove(new IngressIdentity(item.SessionId, item.ClientCommandSequence, item.Generation));
        _bytes -= item.Length;
    }

    private readonly record struct IngressIdentity(string SessionId, ulong Sequence, ulong Generation);
}
