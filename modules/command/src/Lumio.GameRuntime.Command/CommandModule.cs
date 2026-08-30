using System;
using System.Collections.Generic;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lumio.GameRuntime.Command.Tests")]

namespace Lumio.GameRuntime.Command;

public enum CommandModuleState
{
    Created,
    Configured,
    Running,
    Draining,
    Closed,
    Faulted
}

public readonly record struct CommandModuleResult(
    bool Succeeded,
    CommandModuleState State,
    string? GeneratedErrorId)
{
    public static CommandModuleResult Accepted(CommandModuleState state) => new(true, state, null);

    public static CommandModuleResult Rejected(CommandModuleState state, string errorId) => new(false, state, errorId);
}

public sealed class CommandModule
{
    private readonly object _gate = new();
    private CommandModuleState _state = CommandModuleState.Created;
    private readonly CommandServices _services;

    private CommandModule(CommandBufferMerger merger, CommandPreflightValidator preflight, EcsCommandCommitExecutor executor)
    {
        _services = new CommandServices(merger, preflight, executor);
    }

    public static CommandModule Create(
        CommandPreflightValidator? preflight = null,
        EcsCommandCommitExecutor? executor = null) =>
        new(new CommandBufferMerger(), preflight ?? new CommandPreflightValidator(), executor ?? new EcsCommandCommitExecutor());

    public CommandModuleState State
    {
        get { lock (_gate) return _state; }
    }

    public CommandServices Services => _services;

    public BufferOpenResult OpenBuffer(in ProcessorInvocationKey key, in CommandBufferBudget budget)
    {
        lock (_gate)
        {
            if (_state is not (CommandModuleState.Configured or CommandModuleState.Running))
            {
                return new BufferOpenResult(BufferOpenStatus.Rejected, null,
                    CommandFailure.Rejected("ContextClosing", "Command module is not accepting buffers."));
            }
        }

        return _services.BufferFactory.Open(in key, in budget);
    }

    public BufferOpenResult OpenBuffer(ProcessorInvocationKey key, CommandBufferBudget budget) => OpenBuffer(in key, in budget);

    public CommandMergeResult Merge(ulong tickId, IEnumerable<SealedCommandBuffer> buffers)
    {
        lock (_gate)
        {
            if (_state is not (CommandModuleState.Configured or CommandModuleState.Running))
                return CommandMergeResult.Rejected("ContextClosing");
        }

        return _services.Merger.TryMergeResult(tickId, buffers);
    }

    public CommandPreflightResult Prepare(MergedCommandBatch batch) => _services.Preflight.TryPrepare(batch);

    public CommandApplyReceipt Apply(PreparedGameDelta delta) => _services.Executor.Apply(delta);

    public CommandModuleResult Configure()
    {
        lock (_gate)
        {
            if (_state != CommandModuleState.Created) return CommandModuleResult.Rejected(_state, "InvalidArgument");
            _state = CommandModuleState.Configured;
            return CommandModuleResult.Accepted(_state);
        }
    }

    public CommandModuleResult Start()
    {
        lock (_gate)
        {
            if (_state != CommandModuleState.Configured) return CommandModuleResult.Rejected(_state, "InvalidArgument");
            _state = CommandModuleState.Running;
            return CommandModuleResult.Accepted(_state);
        }
    }

    public CommandModuleResult BeginDrain()
    {
        lock (_gate)
        {
            if (_state != CommandModuleState.Running) return CommandModuleResult.Rejected(_state, "InvalidArgument");
            _state = CommandModuleState.Draining;
            return CommandModuleResult.Accepted(_state);
        }
    }

    public CommandModuleResult Close()
    {
        lock (_gate)
        {
            if (_state != CommandModuleState.Draining) return CommandModuleResult.Rejected(_state, "InvalidArgument");
            _state = CommandModuleState.Closed;
            return CommandModuleResult.Accepted(_state);
        }
    }

    public CommandModuleResult Fault(string generatedErrorId)
    {
        if (string.IsNullOrWhiteSpace(generatedErrorId)) throw new ArgumentException("An error ID is required.", nameof(generatedErrorId));
        lock (_gate)
        {
            if (_state is CommandModuleState.Closed or CommandModuleState.Faulted)
                return CommandModuleResult.Rejected(_state, "InvalidArgument");
            _state = CommandModuleState.Faulted;
            return CommandModuleResult.Accepted(_state);
        }
    }
}
