using System;
using System.Collections.Generic;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lumio.GameRuntime.Command.Tests")]

namespace Lumio.GameRuntime.Command;

public enum CommandModuleState { Created, Configured, Running, Draining, Closed, Faulted }

public readonly record struct CommandModuleResult(bool Succeeded, CommandModuleState State, string? GeneratedErrorId)
{
    public static CommandModuleResult Accepted(CommandModuleState state) => new(true, state, null);
    public static CommandModuleResult Rejected(CommandModuleState state, string errorId) => new(false, state, errorId);
}

/// <summary>Command merge/prepare/apply lifecycle; structural application belongs to WorldManager.</summary>
public sealed class CommandModule
{
    private readonly object _gate = new();
    private readonly CommandServices _services;
    private readonly CommandBufferMerger _merger;
    private readonly CommandPreflightValidator _preflight;
    private readonly EcsCommandCommitExecutor _executor;
    private readonly CommandBufferFactory _bufferFactory = new();
    private CommandModuleState _state = CommandModuleState.Created;
    private int _inFlight;
    private bool _applyInFlight;

    private CommandModule(CommandPreflightValidator preflight, EcsCommandCommitExecutor executor)
    {
        _merger = new CommandBufferMerger();
        _preflight = preflight;
        _executor = executor;
        _services = new CommandServices(this);
    }

    public static CommandModule Create(CommandPreflightValidator? preflight = null, EcsCommandCommitExecutor? executor = null) =>
        new(preflight ?? new CommandPreflightValidator(), executor ?? new EcsCommandCommitExecutor());

    public CommandModuleState State { get { lock (_gate) return _state; } }
    public CommandServices Services => _services;

    public BufferOpenResult OpenBuffer(in ProcessorInvocationKey key, in CommandBufferBudget budget)
    {
        if (!TryBegin(false, out CommandOperationLease operation)) return new BufferOpenResult(BufferOpenStatus.Rejected, null, CommandFailure.Rejected("ContextClosing", "Command module is not accepting buffers."));
        using (operation) return _bufferFactory.Open(in key, in budget);
    }
    public BufferOpenResult OpenBuffer(ProcessorInvocationKey key, CommandBufferBudget budget) => OpenBuffer(in key, in budget);
    public CommandMergeResult Merge(ulong tickId, IEnumerable<SealedCommandBuffer> buffers)
    {
        if (!TryBegin(false, out CommandOperationLease operation)) return CommandMergeResult.Rejected("ContextClosing");
        using (operation) return _merger.TryMergeResult(tickId, buffers);
    }
    public CommandPreflightResult Prepare(MergedCommandBatch batch)
    {
        if (!TryBegin(false, out CommandOperationLease operation)) return new CommandPreflightResult(CommandPreflightStatus.Rejected, null, CommandFailure.Rejected("ContextClosing", "Command module is not accepting prepare operations."));
        using (operation) return _preflight.TryPrepare(batch);
    }
    public CommandApplyReceipt Apply(PreparedGameDelta delta)
    {
        if (!TryBegin(true, out CommandOperationLease operation)) return new CommandApplyReceipt(CommandApplyStatus.InfrastructureFault, delta?.TickId ?? 0, delta?.CanonicalDigest.ToArray() ?? Array.Empty<byte>(), 0, "ContextClosing");
        using (operation) return _executor.Apply(delta, operation, this);
    }
    public CommandModuleResult Configure() => Transition(CommandModuleState.Created, CommandModuleState.Configured);
    public CommandModuleResult Start() => Transition(CommandModuleState.Configured, CommandModuleState.Running);
    public CommandModuleResult BeginDrain() => Transition(CommandModuleState.Running, CommandModuleState.Draining);
    public CommandModuleResult Close()
    {
        lock (_gate) { if (_state != CommandModuleState.Draining || _inFlight != 0) return CommandModuleResult.Rejected(_state, "ContextBusy"); _state = CommandModuleState.Closed; return CommandModuleResult.Accepted(_state); }
    }
    public CommandModuleResult Fault(string generatedErrorId)
    {
        if (string.IsNullOrWhiteSpace(generatedErrorId)) throw new ArgumentException("An error ID is required.", nameof(generatedErrorId));
        lock (_gate) { if (_state is CommandModuleState.Closed or CommandModuleState.Faulted) return CommandModuleResult.Rejected(_state, "InvalidArgument"); _state = CommandModuleState.Faulted; return CommandModuleResult.Accepted(_state); }
    }
    internal void CompleteOperation(bool permitsApply) { lock (_gate) { if (_inFlight <= 0) throw new InvalidOperationException("Command operation accounting underflowed."); _inFlight--; if (permitsApply) _applyInFlight = false; } }

    private CommandModuleResult Transition(CommandModuleState expected, CommandModuleState next)
    {
        lock (_gate) { if (_state != expected) return CommandModuleResult.Rejected(_state, "InvalidArgument"); _state = next; return CommandModuleResult.Accepted(_state); }
    }
    private bool TryBegin(bool apply, out CommandOperationLease operation)
    {
        lock (_gate)
        {
            bool admitted = apply ? _state == CommandModuleState.Running : _state is CommandModuleState.Configured or CommandModuleState.Running;
            if (!admitted || (apply && _applyInFlight)) { operation = null!; return false; }
            _inFlight++; if (apply) _applyInFlight = true; operation = new CommandOperationLease(this, apply); return true;
        }
    }
}
