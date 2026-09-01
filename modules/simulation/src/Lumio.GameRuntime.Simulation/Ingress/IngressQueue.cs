using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Channels;

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

public enum LateInputAction
{
    ApplyNext,
    Reject,
    Resync
}

public enum IngressEnqueueStatus
{
    Accepted = 0,
    Duplicate = 1,
    QueueFull = 2,
    Invalid = 3,
    StaleGeneration = 4,
    RejectedLate = 5,
    Closed = 6,
    Backpressured = QueueFull,
    Rejected = 7
}

public readonly record struct OpaqueIngress
{
    private readonly byte[] _payload;

    public OpaqueIngress(string sessionId, ulong clientCommandSequence, ulong targetTickId, ulong generation, byte[] payload)
        : this(sessionId, clientCommandSequence, targetTickId, generation, payload, sessionId, IngressArrivalClass.CurrentTick)
    {
    }

    public OpaqueIngress(
        string sessionId,
        ulong clientCommandSequence,
        ulong targetTickId,
        ulong generation,
        byte[] payload,
        string commandId,
        IngressArrivalClass arrivalClass)
    {
        SessionId = sessionId;
        ClientCommandSequence = clientCommandSequence;
        TargetTickId = targetTickId;
        Generation = generation;
        CommandId = string.IsNullOrEmpty(commandId) ? sessionId : commandId;
        ArrivalClass = arrivalClass;
        _payload = payload is null ? Array.Empty<byte>() : (byte[])payload.Clone();
    }

    public string SessionId { get; }

    public string CommandId { get; }

    public ulong ClientCommandSequence { get; }

    public ulong TargetTickId { get; }

    public ulong Generation { get; }

    public IngressArrivalClass ArrivalClass { get; }

    public byte[] Payload => (byte[])(_payload ?? Array.Empty<byte>()).Clone();

    public int Length => _payload?.Length ?? 0;

    public OpaqueIngress Snapshot() =>
        new(SessionId, ClientCommandSequence, TargetTickId, Generation, _payload ?? Array.Empty<byte>(), CommandId, ArrivalClass);

    public OpaqueIngress WithArrivalClass(IngressArrivalClass arrivalClass) =>
        new(SessionId, ClientCommandSequence, TargetTickId, Generation, _payload ?? Array.Empty<byte>(), CommandId, arrivalClass);
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

public readonly record struct IngressEnqueueBatchResult(
    int AcceptedCount,
    int RejectedCount,
    int BackpressuredCount,
    bool IsPartial)
{
    public bool Succeeded => !IsPartial && RejectedCount == 0 && BackpressuredCount == 0;
}

public sealed class IngressQueue
{
    private readonly object _gate = new();
    private readonly IngressQueueOptions _options;
    private readonly Channel<OpaqueIngress> _channel;
    private readonly HashSet<IngressIdentity> _identities = new();
    private int _count;
    private long _bytes;
    private bool _closed;

    public IngressQueue(IngressQueueOptions options)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        // Channel FIFO is not canonical order; InputCanonicalizer re-sorts by frozen arrival class/sequence/command ID.
        _channel = CreateChannel(options.Capacity);
    }

    public IngressQueue(IngressBudget budget)
        : this((IngressQueueOptions)budget)
    {
    }

    public IngressQueue(int IngressQueueCapacity, long IngressQueueBytes)
        : this(new IngressBudget(IngressQueueCapacity, IngressQueueBytes))
    {
    }

    public int Count
    {
        get { lock (_gate) return _count; }
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

        if (!InputCanonicalizer.TryValidate(in value, _options.MaxBytes, out IngressEnqueueStatus validation))
            return validation;

        var identity = new IngressIdentity(value.SessionId, value.ClientCommandSequence, value.Generation);
        lock (_gate)
        {
            if (_closed) return IngressEnqueueStatus.Closed;
            if (_identities.Contains(identity)) return IngressEnqueueStatus.Duplicate;
            if (_count >= _options.Capacity || value.Length > _options.MaxBytes - _bytes)
                return IngressEnqueueStatus.Backpressured;

            OpaqueIngress snapshot = value.Snapshot();
            if (!_channel.Writer.TryWrite(snapshot))
                return IngressEnqueueStatus.Backpressured;

            _identities.Add(identity);
            _bytes += value.Length;
            _count++;
            return IngressEnqueueStatus.Accepted;
        }
    }

    public IngressEnqueueStatus TryEnqueue(in OpaqueIngress value, ulong currentTickId, ulong currentGeneration)
    {
        LateInputAction action = ClassifyLate(in value, currentTickId, currentGeneration);
        if (action == LateInputAction.Resync && value.Generation != currentGeneration)
            return IngressEnqueueStatus.StaleGeneration;
        if (action == LateInputAction.Reject) return IngressEnqueueStatus.RejectedLate;
        if (action == LateInputAction.Resync) return IngressEnqueueStatus.Rejected;

        IngressArrivalClass arrival = value.TargetTickId == currentTickId
            ? IngressArrivalClass.CurrentTick
            : IngressArrivalClass.NextTick;
        return TryEnqueue(value.WithArrivalClass(arrival));
    }

    public IngressEnqueueBatchResult TryEnqueueBatch(
        IReadOnlyList<OpaqueIngress> values,
        ulong currentTickId,
        ulong currentGeneration)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var accepted = 0;
        var rejected = 0;
        var backpressured = 0;
        for (var index = 0; index < values.Count; index++)
        {
            OpaqueIngress value = values[index];
            IngressEnqueueStatus status = TryEnqueue(in value, currentTickId, currentGeneration);
            switch (status)
            {
                case IngressEnqueueStatus.Accepted:
                case IngressEnqueueStatus.Duplicate:
                    accepted++;
                    break;
                case IngressEnqueueStatus.Backpressured:
                    backpressured++;
                    break;
                default:
                    rejected++;
                    break;
            }
        }

        bool isPartial = accepted > 0 && accepted < values.Count;
        return new IngressEnqueueBatchResult(accepted, rejected, backpressured, isPartial);
    }

    public IngressCaptureResult CaptureForTick(ulong tickId)
    {
        var captured = new List<OpaqueIngress>();
        var retained = new List<OpaqueIngress>();
        var rejectedLate = 0;
        lock (_gate)
        {
            DrainLocked(captured, retained, ref rejectedLate, tickId, null);
        }

        CanonicalInputBatch batch = InputCanonicalizer.Canonicalize(tickId, captured);
        return new IngressCaptureResult(true, batch, rejectedLate, rejectedLate == 0 ? null : "InvalidArgument");
    }

    public IngressCaptureResult CaptureForTick(ulong tickId, ulong currentGeneration)
    {
        var captured = new List<OpaqueIngress>();
        var retained = new List<OpaqueIngress>();
        var rejected = 0;
        lock (_gate)
        {
            DrainLocked(captured, retained, ref rejected, tickId, currentGeneration);
        }

        return new IngressCaptureResult(true, InputCanonicalizer.Canonicalize(tickId, captured), rejected, rejected == 0 ? null : "StaleConnectionGeneration");
    }

    public void Complete()
    {
        lock (_gate)
        {
            _closed = true;
            _channel.Writer.TryComplete();
        }
    }

    public IngressArrivalClass Classify(in OpaqueIngress value, ulong currentTickId)
    {
        if (value.TargetTickId < currentTickId) return IngressArrivalClass.Rejected;
        return value.TargetTickId == currentTickId ? IngressArrivalClass.CurrentTick : IngressArrivalClass.NextTick;
    }

    public LateInputAction ClassifyLate(in OpaqueIngress value, ulong currentTickId, ulong currentGeneration)
    {
        if (value.Generation != currentGeneration) return LateInputAction.Resync;
        if (value.TargetTickId < currentTickId) return LateInputAction.Reject;
        if (value.TargetTickId > currentTickId + 1UL) return LateInputAction.Resync;
        return LateInputAction.ApplyNext;
    }

    private void DrainLocked(
        List<OpaqueIngress> captured,
        List<OpaqueIngress> retained,
        ref int rejected,
        ulong tickId,
        ulong? currentGeneration)
    {
        while (_channel.Reader.TryRead(out OpaqueIngress item))
        {
            _count--;
            if (currentGeneration.HasValue)
            {
                if (item.Generation != currentGeneration.Value || item.TargetTickId < tickId)
                {
                    RemoveIdentity(item);
                    rejected++;
                    continue;
                }

                if (item.TargetTickId == tickId)
                {
                    captured.Add(item.Snapshot());
                    RemoveIdentity(item);
                }
                else retained.Add(item);
                continue;
            }

            if (item.TargetTickId == tickId)
            {
                captured.Add(item.Snapshot());
                RemoveIdentity(item);
            }
            else if (item.TargetTickId < tickId)
            {
                rejected++;
                RemoveIdentity(item);
            }
            else retained.Add(item);
        }

        for (var index = 0; index < retained.Count; index++)
        {
            OpaqueIngress item = retained[index];
            if (!_channel.Writer.TryWrite(item))
                throw new InvalidOperationException("Retained ingress could not be written back to the bounded channel.");
            _count++;
        }
    }

    private void RemoveIdentity(in OpaqueIngress item)
    {
        _identities.Remove(new IngressIdentity(item.SessionId, item.ClientCommandSequence, item.Generation));
        _bytes -= item.Length;
    }

    private static Channel<OpaqueIngress> CreateChannel(int capacity) =>
        Channel.CreateBounded<OpaqueIngress>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly record struct IngressIdentity(string SessionId, ulong Sequence, ulong Generation);
}
