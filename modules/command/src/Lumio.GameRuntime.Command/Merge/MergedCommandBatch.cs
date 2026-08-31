using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Lumio.GameRuntime.Command;

/// <summary>Stable, immutable result of merging sealed processor buffers.</summary>
public sealed class MergedCommandBatch
{
    private readonly ReadOnlyCollection<Command> _commands;
    private readonly ReadOnlyCollection<SealedCommandBuffer> _buffers;
    private readonly byte[] _canonicalDigest;
    private CommandBufferState _state;

    internal MergedCommandBatch(ulong tickId, IEnumerable<SealedCommandBuffer> buffers, IEnumerable<Command> commands)
    {
        TickId = tickId;
        _buffers = new List<SealedCommandBuffer>(buffers).AsReadOnly();
        _commands = new List<Command>(commands).AsReadOnly();
        _canonicalDigest = CommandCanonical.Digest(_commands, _buffers.Count == 0 ? "default" : _buffers[0].WorldId).ToArray();
        _state = CommandBufferState.Merged;
    }

    public ulong TickId { get; }

    public string WorldId => _buffers.Count == 0 ? "default" : _buffers[0].WorldId;

    public IReadOnlyList<Command> Commands => _commands;

    public IReadOnlyList<SealedCommandBuffer> Buffers => _buffers;

    public CommandBufferState State => _state;

    public ReadOnlyMemory<byte> CanonicalDigest => _canonicalDigest;

    public ReadOnlyMemory<byte> CanonicalBytes
    {
        get
        {
            var bytes = new List<byte>();
            foreach (Command command in _commands) bytes.AddRange(command.CanonicalBytes.ToArray());
            return bytes.ToArray();
        }
    }

    public string IdempotencyKey => string.Concat(TickId.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", CanonicalDigestHex);

    public string CanonicalDigestHex => CommandHashing.ToHex(_canonicalDigest);

    public bool IsEmpty => _commands.Count == 0;

    public void MarkPrepared()
    {
        if (_state != CommandBufferState.Merged) throw new InvalidOperationException("Batch is not merged.");
        _state = CommandBufferState.Prepared;
        foreach (SealedCommandBuffer buffer in _buffers) buffer.MarkPrepared();
    }

    public void MarkApplied()
    {
        if (_state != CommandBufferState.Prepared) throw new InvalidOperationException("Batch is not prepared.");
        _state = CommandBufferState.Applied;
        foreach (SealedCommandBuffer buffer in _buffers) buffer.MarkApplied();
    }

    public void MarkFaulted()
    {
        _state = CommandBufferState.Faulted;
        foreach (SealedCommandBuffer buffer in _buffers) buffer.MarkFaulted();
    }

    public CommandBufferTransitionResult Fault(string errorId = "PanicBoundary")
    {
        _state = CommandBufferState.Faulted;
        return new CommandBufferTransitionResult(false, _state, errorId);
    }
}
