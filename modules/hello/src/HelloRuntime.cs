using System;
using System.Collections.Generic;
using System.Text;

namespace Lumio.GameRuntime.Hello;

/// <summary>Authoritative hello world runtime: validates ingress commands, commits them on tick, exposes the bounded log.</summary>
/// <remarks>
/// Single-threaded calling model: hosts must serialize Enqueue/Tick/Snapshot the same way they serialize a simulation tick.
/// Non-goal: this type never echoes input. Every value it returns is an authoritative committed record produced by
/// <see cref="Tick"/>; nothing leaves the runtime before a tick commits it.
/// </remarks>
public sealed class HelloRuntime
{
    /// <summary>Maximum number of validated commands waiting to be consumed by a tick.</summary>
    public const int IngressQueueCapacity = 64;

    /// <summary>Maximum number of committed records retained in the hello log of a snapshot.</summary>
    public const int HelloLogCapacity = 32;

    /// <summary>Maximum UTF-8 byte length of a payload (contract limits.maxPayloadBytes).</summary>
    public const int MaxPayloadBytes = 4096;

    private readonly Queue<HelloInputCommand> _pending = new(IngressQueueCapacity);
    private readonly Queue<HelloRecord> _helloLog = new(HelloLogCapacity + 1);
    private readonly Dictionary<string, ulong> _maxSequenceBySender = new();
    private ulong _tickId;
    private ulong _revision;

    /// <summary>Validates a command and admits it to the ingress queue for the next tick.</summary>
    /// <param name="command">Command to validate and enqueue.</param>
    /// <returns><see langword="null"/> when accepted; otherwise the rejection reason.</returns>
    public HelloRuntimeError? Enqueue(HelloInputCommand command)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        if (command.Sender is not ("browser" or "bot"))
        {
            return new HelloRuntimeError(HelloRuntimeErrorCode.UnknownRole);
        }

        if (command.Kind != "hello")
        {
            return new HelloRuntimeError(HelloRuntimeErrorCode.BadEnvelope);
        }

        if (string.IsNullOrEmpty(command.Payload) || Encoding.UTF8.GetByteCount(command.Payload) > MaxPayloadBytes)
        {
            return new HelloRuntimeError(HelloRuntimeErrorCode.BadEnvelope);
        }

        if (command.PayloadSha256 != HelloWireHash.PayloadSha256Hex(command.Payload))
        {
            return new HelloRuntimeError(HelloRuntimeErrorCode.BadPayloadHash);
        }

        // The sequence is reserved at enqueue time: anything at or below the highest value this
        // runtime has already seen (queued or committed) for the sender is a duplicate. This
        // implies the wire rule "reject sequence <= committed maximum" and also covers in-flight
        // duplicates that would otherwise commit twice within one tick.
        if (command.Sequence <= _maxSequenceBySender.GetValueOrDefault(command.Sender))
        {
            return new HelloRuntimeError(HelloRuntimeErrorCode.DuplicateSequence);
        }

        if (_pending.Count >= IngressQueueCapacity)
        {
            return new HelloRuntimeError(HelloRuntimeErrorCode.QueueFull);
        }

        _maxSequenceBySender[command.Sender] = command.Sequence;
        _pending.Enqueue(command);
        return null;
    }

    /// <summary>Commits every queued command at the given tick timestamp (tick-on-demand).</summary>
    /// <param name="committedAtMs">UTC epoch milliseconds to stamp on each committed record.</param>
    /// <returns>Deltas for the records committed by this tick, in queue order; empty when the queue was empty.</returns>
    public HelloDelta[] Tick(long committedAtMs)
    {
        if (_pending.Count == 0)
        {
            return Array.Empty<HelloDelta>();
        }

        _tickId++;
        HelloDelta[] deltas = new HelloDelta[_pending.Count];
        int index = 0;
        while (_pending.Count > 0)
        {
            HelloInputCommand command = _pending.Dequeue();
            _revision++;
            HelloRecord record = new(
                command.Sender,
                command.Sequence,
                command.Kind,
                command.Payload,
                command.PayloadSha256,
                _tickId,
                _revision,
                command.SentAtMs,
                committedAtMs);
            _helloLog.Enqueue(record);
            if (_helloLog.Count > HelloLogCapacity)
            {
                _helloLog.Dequeue();
            }

            deltas[index++] = new HelloDelta(
                record.Sender,
                record.Sequence,
                record.Kind,
                record.Payload,
                record.PayloadSha256,
                record.TickId,
                record.Revision,
                record.OriginSentAtMs,
                record.CommittedAtMs,
                command.Sequence);
        }

        return deltas;
    }

    /// <summary>Returns the current authoritative state as an immutable snapshot.</summary>
    public HelloFullSnapshot Snapshot() => new(_tickId, _revision, _helloLog.ToArray());
}
