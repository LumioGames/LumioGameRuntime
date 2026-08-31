using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Ingress;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;

namespace Lumio.GameRuntime.Simulation.Session;

public readonly record struct SimulationSessionOptions(
    string SessionId,
    ulong Seed,
    ulong InitialTickId,
    int IngressCapacity,
    long IngressBytes,
    int MaxOutputItems,
    long MaxOutputBytes)
{
    public static SimulationSessionOptions Default(string sessionId) => new(sessionId, 0, 1, 256, 1_048_576, 256, 1_048_576);

    public bool IsValid => SimulationValidation.IsIdentifier(SessionId) && IngressCapacity > 0 && IngressBytes > 0 && MaxOutputItems > 0 && MaxOutputBytes > 0;
}

public sealed class SimulationSession : IDisposable
{
    private readonly object _gate = new();
    private readonly SimulationSessionOptions _options;
    private readonly SimulationOwnerThread _owner;
    private readonly TickRunner _runner;
    private SimulationSessionState _state = SimulationSessionState.Created;
    private bool _disposed;

    internal SimulationSession(SimulationSessionOptions options, TickRunner? runner = null)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _owner = new SimulationOwnerThread();
        _runner = runner ?? new TickRunner(new TickRunnerOptions(options.Seed, options.InitialTickId, options.MaxOutputItems, options.MaxOutputBytes));
    }

    public string SessionId => _options.SessionId;

    public SimulationSessionState State
    {
        get { lock (_gate) return _state; }
    }

    public SessionEpoch Epoch => _owner.Epoch;

    public ulong CurrentTickId => _runner.NextTickId;

    public TickRunner Runner => _runner;

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

    public TickRunResult RunTick(in HostTickRequest request)
    {
        lock (_gate)
        {
            if (!_owner.Validate(request.Epoch)) return TickRunResult.Rejected(request.TickId, "WrongContext", "The request is not from the owner epoch.");
            if (_state != SimulationSessionState.Running) return TickRunResult.Rejected(request.TickId, "ContextClosing", $"Session is {_state}.");
            TickRunResult result = _runner.Run(in request);
            if (result.Status == TickRunStatus.Faulted || _runner.IsFaulted)
                _state = SimulationSessionState.Faulted;
            return result;
        }
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
}
