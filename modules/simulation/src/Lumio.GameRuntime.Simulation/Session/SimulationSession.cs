using System;
using Lumio.GameRuntime.Coordination;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;

namespace Lumio.GameRuntime.Simulation.Session;

/// <summary>Forwards Revision/Txn reads to Coordination. The session never caches a mutable copy.</summary>
internal interface ICoordinationReadPort
{
    SessionRevisionVectorView ReadRevision();

    TxnResolutionResult QueryTxn(string txnId);
}

public readonly record struct SimulationSessionOptions(
    string SessionId,
    ulong Seed,
    ulong InitialTickId,
    int IngressCapacity,
    long IngressBytes,
    int MaxOutputItems,
    long MaxOutputBytes)
{
    public static SimulationSessionOptions Default(string sessionId) =>
        new(sessionId, 0, 1, 256, 1_048_576, 256, 1_048_576) { WorldId = new WorldId(1UL) };

    public WorldId WorldId { get; init; }

    public bool IsValid => SimulationValidation.IsIdentifier(SessionId) && IngressCapacity > 0 && IngressBytes > 0 && MaxOutputItems > 0 && MaxOutputBytes > 0;
}

public sealed class SimulationSession : IRuntimeSession
{
    private readonly object _gate = new();
    private readonly SimulationSessionOptions _options;
    private readonly SimulationOwnerThread _owner;
    private readonly TickRunner _runner;
    private readonly WorldId _worldId;
    private readonly ICoordinationReadPort? _coordination;
    private SimulationSessionState _state = SimulationSessionState.Created;
    private bool _disposed;
    private bool _tickInFlight;
    private bool _disposeRequested;

    internal SimulationSession(
        SimulationSessionOptions options,
        TickExecutorComposition? composition = null,
        TickRunner? runner = null,
        ICoordinationReadPort? coordination = null)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _worldId = options.WorldId.IsDefault ? new WorldId(1UL) : options.WorldId;
        _owner = new SimulationOwnerThread();
        _coordination = coordination;
        var runnerOptions = new TickRunnerOptions(options.Seed, options.InitialTickId, options.MaxOutputItems, options.MaxOutputBytes)
        {
            IngressCapacity = options.IngressCapacity,
            MaxIngressBytes = options.IngressBytes,
            SessionId = options.SessionId
        };
        _runner = runner ?? (composition is null
            ? new TickRunner(runnerOptions)
            : TickRunner.FromComposition(runnerOptions, composition));
    }

    public string SessionId => _options.SessionId;

    public WorldId WorldId => _worldId;

    public SimulationSessionState State
    {
        get { lock (_gate) return _state; }
    }

    public SessionEpoch Epoch => _owner.Epoch;

    public ulong CurrentTickId => _runner.NextTickId;

    internal TickRunner Runner => _runner;

    public LifecycleResult Initialize(SessionEpoch epoch)
    {
        return Transition(epoch, SimulationSessionState.Created, SimulationSessionState.Initialized);
    }

    public LifecycleResult Prime(SessionEpoch epoch) => Transition(epoch, SimulationSessionState.Initialized, SimulationSessionState.Ready);

    public LifecycleResult Start(SessionEpoch epoch) => Transition(epoch, SimulationSessionState.Ready, SimulationSessionState.Running);

    public LifecycleResult Pause(SessionEpoch epoch) => Transition(epoch, SimulationSessionState.Running, SimulationSessionState.Paused);

    public LifecycleResult Resume(SessionEpoch epoch) => Transition(epoch, SimulationSessionState.Paused, SimulationSessionState.Running);

    public LifecycleResult Drain(SessionEpoch epoch)
    {
        lock (_gate)
        {
            if (!_owner.Validate(epoch) || _state is not (SimulationSessionState.Running or SimulationSessionState.Paused))
                return LifecycleResult.Rejected(_state, "WrongContext");
            _state = SimulationSessionState.Draining;
            return LifecycleResult.Accepted(_state);
        }
    }

    TickRunResult IRuntimeSession.RunTick(in TickInput input)
    {
        HostTickRequest request = input.Request;
        return RunTick(in request);
    }

    public TickRunResult RunTick(in HostTickRequest request)
    {
        lock (_gate)
        {
            if (_state == SimulationSessionState.Disposed)
                return TickRunResult.Rejected(request.TickId, "ContextClosing", $"Session is {_state}.");
            if (!_owner.IsOwner)
            {
                _state = SimulationSessionState.Faulted;
                var ownerFailure = new PhaseFailureRecord(
                    request.TickId,
                    TickPhase.IngressCapture,
                    null,
                    "WrongContext",
                    "Tick execution belongs to the owner thread.",
                    false);
                return TickRunResult.Faulted(request.TickId, string.Empty, ownerFailure);
            }
            if (!_owner.Validate(request.Epoch)) return TickRunResult.Rejected(request.TickId, "WrongContext", "The request is not from the owner epoch.");
            bool faultedReplay = _state == SimulationSessionState.Faulted && _runner.IsFaulted;
            if (_state != SimulationSessionState.Running && !faultedReplay)
                return TickRunResult.Rejected(request.TickId, "ContextClosing", $"Session is {_state}.");
            if (_tickInFlight) return TickRunResult.Rejected(request.TickId, "WrongContext", "run_tick is not reentrant.");
            _tickInFlight = true;
            try
            {
                TickRunResult result = _runner.Run(in request, IsTickLifecycleValid);
                if (result.Status is TickRunStatus.Faulted or TickRunStatus.PostCommitFaulted || _runner.IsFaulted)
                    _state = SimulationSessionState.Faulted;
                return result;
            }
            finally
            {
                _tickInFlight = false;
                if (_disposeRequested)
                {
                    _disposeRequested = false;
                    _disposed = true;
                    _state = SimulationSessionState.Disposed;
                }
            }
        }
    }

    public SessionRevisionVectorView ReadRevision()
    {
        ICoordinationReadPort port = _coordination ?? throw new InvalidOperationException("Coordination read port is not bound.");
        return port.ReadRevision();
    }

    public TxnResolutionResult QueryTxn(string txnId)
    {
        ICoordinationReadPort port = _coordination ?? throw new InvalidOperationException("Coordination read port is not bound.");
        return port.QueryTxn(txnId);
    }

    public LifecycleResult MarkSnapshotted(SessionEpoch epoch)
    {
        return Transition(epoch, SimulationSessionState.Draining, SimulationSessionState.Snapshotted);
    }

    public LifecycleResult DisposeSession(SessionEpoch epoch)
    {
        lock (_gate)
        {
            if (!_owner.Validate(epoch)) return LifecycleResult.Rejected(_state, "WrongContext");
            if (_state is not (SimulationSessionState.Snapshotted or SimulationSessionState.Faulted or SimulationSessionState.Draining))
                return LifecycleResult.Rejected(_state, "InvalidArgument");
            _state = SimulationSessionState.Disposed;
            _disposed = true;
            return LifecycleResult.Accepted(_state);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_owner.IsOwner) return;
            if (_tickInFlight)
            {
                _disposeRequested = true;
                return;
            }
            _disposed = true;
            _state = SimulationSessionState.Disposed;
        }
    }

    internal void Fault()
    {
        lock (_gate)
        {
            if (_state != SimulationSessionState.Disposed) _state = SimulationSessionState.Faulted;
        }
    }

    private LifecycleResult Transition(SessionEpoch epoch, SimulationSessionState expected, SimulationSessionState next)
    {
        lock (_gate)
        {
            if (!_owner.Validate(epoch) || _state != expected) return LifecycleResult.Rejected(_state, "WrongContext");
            _state = next;
            return LifecycleResult.Accepted(_state);
        }
    }

    private bool IsTickLifecycleValid()
    {
        lock (_gate)
            return _tickInFlight && !_disposeRequested && !_disposed && _state == SimulationSessionState.Running;
    }
}
