using System;
using Lumio.GameRuntime.Simulation.Tick;

namespace Lumio.GameRuntime.Simulation;

public class FailStopController
{
    private readonly object _gate = new();
    private PhaseFailureRecord? _firstFailure;

    public bool IsFaulted
    {
        get { lock (_gate) return _firstFailure is not null; }
    }

    public PhaseFailureRecord? FirstFailure
    {
        get { lock (_gate) return _firstFailure; }
    }

    public bool FailStop(PhaseFailureRecord failure)
    {
        if (failure is null) throw new ArgumentNullException(nameof(failure));
        lock (_gate)
        {
            if (_firstFailure is not null) return false;
            _firstFailure = failure;
            return true;
        }
    }
}
