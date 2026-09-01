using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.GeneratedContracts;

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
    private readonly CommandBufferMerger _merger;
    private readonly CommandPreflightValidator _preflight;
    private readonly EcsCommandCommitExecutor _executor;
    private readonly EcsWorld? _world;
    private readonly bool _prepareFromWorld;
    private readonly CommandBufferFactory _bufferFactory = new();
    private int _inFlight;
    private bool _applyInFlight;

    private CommandModule(
        CommandBufferMerger merger,
        CommandPreflightValidator preflight,
        EcsCommandCommitExecutor executor,
        EcsWorld? world,
        bool prepareFromWorld)
    {
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _world = world;
        _prepareFromWorld = prepareFromWorld;
        _services = new CommandServices(this);
    }

    public static CommandModule Create(
        CommandPreflightValidator? preflight = null,
        EcsCommandCommitExecutor? executor = null,
        EcsWorld? world = null)
    {
        bool prepareFromWorld = preflight is null && world is not null;
        CommandPreflightValidator resolvedPreflight = preflight ??
            (world is null
                ? new CommandPreflightValidator()
                : new CommandPreflightValidator(CommandPreflightOptions.FromWorld(world)));
        EcsCommandCommitExecutor resolvedExecutor = executor ??
            (world is null
                ? new EcsCommandCommitExecutor()
                : new EcsCommandCommitExecutor(
                    new EcsWorldCommandCommitPort(world),
                    errorId => world.FaultFromParticipant(errorId)));
        return new CommandModule(new CommandBufferMerger(), resolvedPreflight, resolvedExecutor, world, prepareFromWorld);
    }

    public CommandModuleState State
    {
        get { lock (_gate) return _state; }
    }

    public CommandServices Services => _services;

    public BufferOpenResult OpenBuffer(in ProcessorInvocationKey key, in CommandBufferBudget budget)
    {
        if (!TryBeginOperation(permitsApply: false, out CommandOperationLease operation))
            return new BufferOpenResult(BufferOpenStatus.Rejected, null,
                CommandFailure.Rejected("ContextClosing", "Command module is not accepting buffers."));

        using (operation)
        {
            return _bufferFactory.Open(in key, in budget);
        }
    }

    public BufferOpenResult OpenBuffer(ProcessorInvocationKey key, CommandBufferBudget budget) => OpenBuffer(in key, in budget);

    public CommandMergeResult Merge(ulong tickId, IEnumerable<SealedCommandBuffer> buffers)
    {
        if (!TryBeginOperation(permitsApply: false, out CommandOperationLease operation))
            return CommandMergeResult.Rejected("ContextClosing");

        using (operation)
        {
            return _merger.TryMergeResult(tickId, buffers);
        }
    }

    public CommandPreflightResult Prepare(MergedCommandBatch batch)
    {
        if (!TryBeginOperation(permitsApply: false, out CommandOperationLease operation))
            return new CommandPreflightResult(
                CommandPreflightStatus.Rejected,
                null,
                CommandFailure.Rejected("ContextClosing", "Command module is not accepting prepare operations."));

        using (operation)
        {
            if (_prepareFromWorld && _world is not null)
            {
                CommandPrepareResult prepared = _preflight.Prepare(in batch, BindWorldPrepareContext(batch));
                return new CommandPreflightResult(prepared.Status, prepared.Delta, prepared.Failure);
            }

            return _preflight.TryPrepare(batch);
        }
    }

    public CommandApplyReceipt Apply(PreparedGameDelta delta)
    {
        if (!TryBeginOperation(permitsApply: true, out CommandOperationLease operation))
            return RejectedApply(delta, "ContextClosing");

        using (operation)
        {
            return _executor.Apply(delta, operation, this);
        }
    }

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
            if (_inFlight != 0) return CommandModuleResult.Rejected(_state, "ContextBusy");
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

    internal void CompleteOperation(bool permitsApply)
    {
        lock (_gate)
        {
            if (_inFlight <= 0) throw new InvalidOperationException("Command operation accounting underflowed.");
            _inFlight--;
            if (permitsApply) _applyInFlight = false;
        }
    }

    private bool TryBeginOperation(bool permitsApply, out CommandOperationLease operation)
    {
        lock (_gate)
        {
            bool admitted = permitsApply
                ? _state == CommandModuleState.Running
                : _state is CommandModuleState.Configured or CommandModuleState.Running;
            if (!admitted || (permitsApply && _applyInFlight))
            {
                operation = null!;
                return false;
            }

            _inFlight = checked(_inFlight + 1);
            if (permitsApply) _applyInFlight = true;
            operation = new CommandOperationLease(this, permitsApply);
            return true;
        }
    }

    private CommandPrepareContext BindWorldPrepareContext(MergedCommandBatch batch)
    {
        EcsWorld world = _world!;
        int availableSlots = world.Budget.MaxEntities - world.ActiveEntityCount;
        return new CommandPrepareContext(
            batch.TickId,
            batch.WorldId,
            GeneratedContractManifest.SchemaEpoch,
            CommandBufferBudget.Unlimited,
            availableSlots <= 0 ? 0UL : (ulong)availableSlots,
            (ulong)world.Budget.MaxChangeEntries,
            new EcsWorldCommandValidationContext(world));
    }

    private static CommandApplyReceipt RejectedApply(PreparedGameDelta? delta, string errorId) =>
        new(
            CommandApplyStatus.InfrastructureFault,
            delta?.TickId ?? 0UL,
            delta?.CanonicalDigest.ToArray() ?? Array.Empty<byte>(),
            0,
            errorId);
}
