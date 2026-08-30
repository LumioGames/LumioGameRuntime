using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Command;

/// <summary>Immutable view handed from a processor to the merge barrier.</summary>
public sealed class SealedCommandBuffer
{
    private readonly ProcessorCommandBuffer _owner;
    private readonly IReadOnlyList<Command> _commands;

    internal SealedCommandBuffer(ProcessorCommandBuffer owner, IEnumerable<Command> commands, ulong bytes)
    {
        _owner = owner;
        var copy = new List<Command>(commands);
        _commands = copy.AsReadOnly();
        Bytes = bytes;
    }

    public ulong TickId => _owner.TickId;

    public string ProcessorId => _owner.ProcessorId;

    public string WorldId => _owner.WorldId;

    public ulong BufferGeneration => _owner.BufferGeneration;

    public Lumio.Gen.ContractTypes.ProcessorDescriptorPhase Phase => _owner.Phase;

    public bool MayEmitStructuralCommands => _owner.MayEmitStructuralCommands;

    public CommandBufferState State => _owner.State;

    public IReadOnlyList<Command> Commands => _commands;

    public ulong Bytes { get; }

    public ReadOnlyMemory<byte> CanonicalDigest => CommandCanonical.Digest(_commands, WorldId);

    public bool IsSealed => State == CommandBufferState.Sealed;

    internal void MarkMerged() => _owner.MarkMerged();

    internal void MarkPrepared() => _owner.MarkPrepared();

    internal void MarkApplied() => _owner.MarkApplied();

    internal void MarkFaulted() => _owner.MarkFaulted();
}
