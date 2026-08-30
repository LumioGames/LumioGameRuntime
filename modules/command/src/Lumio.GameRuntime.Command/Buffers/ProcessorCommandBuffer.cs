using System;
using System.Collections.Generic;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command;

/// <summary>A processor-owned, single-invocation command buffer.</summary>
public sealed class ProcessorCommandBuffer
{
    private readonly List<Command> _commands = new();
    private readonly CommandBufferBudget _budget;
    private readonly bool _mayEmitStructuralCommands;
    private CommandBufferState _state;
    private ulong _nextLocalSequence;
    private ulong _nextTokenSequence;
    private ulong _bytes;

    public ProcessorCommandBuffer(ProcessorInvocationKey key, CommandBufferBudget budget)
        : this(key.TickId, key.WorldId, key.ProcessorId, key.Phase, true, budget)
    {
        if (!key.IsValid) throw new ArgumentException("A valid processor invocation key is required.", nameof(key));
    }

    public ProcessorCommandBuffer(ProcessorInvocationKey key, bool mayEmitStructuralCommands, CommandBufferBudget budget)
        : this(key.TickId, key.WorldId, key.ProcessorId, key.Phase, mayEmitStructuralCommands, budget)
    {
        if (!key.IsValid) throw new ArgumentException("A valid processor invocation key is required.", nameof(key));
    }

    public ProcessorCommandBuffer(
        ulong tickId,
        string processorId,
        ProcessorDescriptorPhase phase,
        bool mayEmitStructuralCommands = true,
        CommandBufferBudget? budget = null,
        ulong bufferGeneration = 1UL)
    {
        if (string.IsNullOrWhiteSpace(processorId)) throw new ArgumentException("A processor ID is required.", nameof(processorId));
        if (budget is CommandBufferBudget selected && !selected.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));

        TickId = tickId;
        WorldId = "default";
        BufferGeneration = bufferGeneration;
        ProcessorId = processorId;
        Phase = phase;
        _mayEmitStructuralCommands = mayEmitStructuralCommands;
        _budget = budget ?? CommandBufferBudget.Unlimited;
        _state = CommandBufferState.Open;
        Writer = new CommandBufferWriter(this);
    }

    public ProcessorCommandBuffer(
        ulong tickId,
        string worldId,
        string processorId,
        ProcessorDescriptorPhase phase,
        bool mayEmitStructuralCommands = true,
        CommandBufferBudget? budget = null,
        ulong bufferGeneration = 1UL)
        : this(tickId, processorId, phase, mayEmitStructuralCommands, budget, bufferGeneration)
    {
        if (string.IsNullOrWhiteSpace(worldId) || !CommandValidation.IsIdentifier(worldId))
            throw new ArgumentException("A valid world ID is required.", nameof(worldId));
        WorldId = worldId;
    }

    public ProcessorCommandBuffer(
        string worldId,
        ulong tickId,
        string processorId,
        ProcessorDescriptorPhase phase,
        bool mayEmitStructuralCommands = true,
        CommandBufferBudget? budget = null,
        ulong bufferGeneration = 1UL)
        : this(tickId, worldId, processorId, phase, mayEmitStructuralCommands, budget, bufferGeneration)
    {
    }

    public ProcessorCommandBuffer(
        ulong tickId,
        string processorId,
        string phase,
        bool mayEmitStructuralCommands = true,
        CommandBufferBudget? budget = null,
        ulong bufferGeneration = 1UL)
        : this(tickId, processorId, new CommandSortKey(phase, processorId, 1UL).Phase, mayEmitStructuralCommands, budget, bufferGeneration)
    {
    }

    public ulong TickId { get; }

    public string ProcessorId { get; }

    public string WorldId { get; private set; }

    public ulong BufferGeneration { get; }

    public ProcessorInvocationKey InvocationKey => new(TickId, WorldId, Phase, ProcessorId);

    public ProcessorDescriptorPhase Phase { get; }

    public bool MayEmitStructuralCommands => _mayEmitStructuralCommands;

    public CommandBufferState State => _state;

    public CommandBufferWriter Writer { get; }

    public IReadOnlyList<Command> Commands => _commands.AsReadOnly();

    public CommandBudgetUsage Usage => new((ulong)_commands.Count, _bytes);

    public CommandAppendResult Append(CommandKind kind, string? targetEntityId = null, string? componentType = null,
        string? fieldName = null, ReadOnlyMemory<byte> payload = default, DeferredEntityToken? deferredTarget = null,
        string? commandId = null, ulong? estimatedBytes = null)
    {
        if (_state != CommandBufferState.Open) return CommandAppendResult.Rejected("InvalidArgument");
        if (kind is CommandKind.Create or CommandKind.Destroy &&
            (!_mayEmitStructuralCommands || !CommandValidation.IsStructuralPhase(Phase)))
        {
            return CommandAppendResult.Rejected("MessagePermissionDenied");
        }

        if (targetEntityId is not null && deferredTarget is not null)
        {
            return CommandAppendResult.Rejected("InvalidArgument");
        }

        ulong sequence;
        try
        {
            sequence = checked(_nextLocalSequence + 1UL);
        }
        catch (OverflowException)
        {
            _state = CommandBufferState.Faulted;
            return new CommandAppendResult(CommandAppendStatus.Fatal, 0UL, "InternalInvariant");
        }

        if (deferredTarget is DeferredEntityToken token &&
            (token.TickId != TickId || !string.Equals(token.WorldId, WorldId, StringComparison.Ordinal) ||
             (token.BufferGeneration != 0UL && token.BufferGeneration != BufferGeneration)))
        {
            return CommandAppendResult.Rejected("WrongContext");
        }

        Command command;
        try
        {
            command = new Command(
                kind,
                new CommandSortKey(Phase, ProcessorId, sequence),
                targetEntityId,
                componentType,
                fieldName,
                payload,
                deferredTarget,
                commandId,
                estimatedBytes);
        }
        catch (ArgumentException)
        {
            return CommandAppendResult.Rejected("InvalidArgument");
        }
        catch (OverflowException)
        {
            return CommandAppendResult.Rejected("CapacityExceeded");
        }

        if (command.EstimatedBytes == 0UL || !_budget.TryAdd((ulong)_commands.Count, _bytes, command.EstimatedBytes))
        {
            return CommandAppendResult.Rejected("BudgetExceeded");
        }

        _commands.Add(command);
        _nextLocalSequence = sequence;
        _bytes = checked(_bytes + command.EstimatedBytes);
        return CommandAppendResult.Accepted(sequence);
    }

    public CommandAppendResult Append(Command command)
    {
        if (command is null) return CommandAppendResult.Rejected("InvalidArgument");
        if (command.SortKey.Phase != Phase || !string.Equals(command.SortKey.ProcessorId, ProcessorId, StringComparison.Ordinal))
        {
            return CommandAppendResult.Rejected("WrongContext");
        }

        ulong expectedSequence;
        try
        {
            expectedSequence = checked(_nextLocalSequence + 1UL);
        }
        catch (OverflowException)
        {
            return CommandAppendResult.Rejected("CapacityExceeded");
        }

        if (command.SortKey.LocalSequence != expectedSequence)
        {
            return CommandAppendResult.Rejected("InvalidArgument");
        }

        return Append(
            command.Kind,
            command.TargetEntityId,
            command.ComponentType,
            command.FieldName,
            command.Payload,
            command.DeferredTarget,
            command.CommandId,
            command.EstimatedBytes);
    }

    public CommandAppendResult Append(in StructuralCommand command) => Append((Command)command);

    public DeferredEntityToken AllocateDeferredEntity()
    {
        if (_state != CommandBufferState.Open) throw new InvalidOperationException("Deferred entities can only be allocated while the buffer is open.");

        ulong sequence;
        try { sequence = checked(_nextTokenSequence + 1UL); }
        catch (OverflowException) { throw new InvalidOperationException("Command sequence exhausted."); }
        _nextTokenSequence = sequence;
        return new DeferredEntityToken(TickId, WorldId, ProcessorId, BufferGeneration, sequence);
    }

    internal void RollbackDeferredEntity(DeferredEntityToken token)
    {
        if (_state == CommandBufferState.Open && token.ProcessorId == ProcessorId &&
            token.TickId == TickId && token.LocalSequence == _nextTokenSequence && _nextTokenSequence > 0UL)
        {
            _nextTokenSequence--;
        }
    }

    public CommandBufferTransitionResult TrySeal(out SealedCommandBuffer? sealedBuffer)
    {
        sealedBuffer = null;
        if (_state != CommandBufferState.Open)
        {
            return CommandBufferTransitionResult.Failure(_state, "InvalidArgument");
        }

        _state = CommandBufferState.Sealed;
        sealedBuffer = new SealedCommandBuffer(this, _commands, _bytes);
        return CommandBufferTransitionResult.Success(_state);
    }

    public CommandBufferTransitionResult TryDiscard()
    {
        if (_state is CommandBufferState.Open or CommandBufferState.Sealed or CommandBufferState.Merged)
        {
            _state = CommandBufferState.Discarded;
            return CommandBufferTransitionResult.Success(_state);
        }

        return CommandBufferTransitionResult.Failure(_state, "InvalidArgument");
    }

    public CommandBufferTransitionResult Fault(string generatedErrorId = "PanicBoundary")
    {
        if (string.IsNullOrWhiteSpace(generatedErrorId)) generatedErrorId = "PanicBoundary";
        _state = CommandBufferState.Faulted;
        return new CommandBufferTransitionResult(false, _state, generatedErrorId);
    }

    public SealedCommandBuffer Seal()
    {
        CommandBufferTransitionResult result = TrySeal(out SealedCommandBuffer? sealedBuffer);
        if (!result.Succeeded || sealedBuffer is null) throw new InvalidOperationException(result.GeneratedErrorId);
        return sealedBuffer;
    }

    internal void MarkMerged()
    {
        if (_state != CommandBufferState.Sealed) throw new InvalidOperationException("Buffer is not sealed.");
        _state = CommandBufferState.Merged;
    }

    internal void MarkPrepared()
    {
        if (_state != CommandBufferState.Merged) throw new InvalidOperationException("Buffer is not merged.");
        _state = CommandBufferState.Prepared;
    }

    internal void MarkApplied()
    {
        if (_state != CommandBufferState.Prepared) throw new InvalidOperationException("Buffer is not prepared.");
        _state = CommandBufferState.Applied;
    }

    internal void MarkFaulted() => _state = CommandBufferState.Faulted;

    internal void MarkDiscarded()
    {
        TryDiscard();
    }
}
