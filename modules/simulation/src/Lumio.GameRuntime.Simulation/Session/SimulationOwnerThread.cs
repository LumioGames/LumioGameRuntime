using System;
using System.Threading;

namespace Lumio.GameRuntime.Simulation.Session;

/// <summary>Owner token for a single authoritative World. It never exposes a scheduler or a wall clock.</summary>
public sealed class SimulationOwnerThread
{
    private readonly int _managedThreadId;
    private readonly object _gate = new();
    private ulong _generation = 1;

    public SimulationOwnerThread()
    {
        _managedThreadId = Environment.CurrentManagedThreadId;
    }

    public int ManagedThreadId => _managedThreadId;

    public SessionEpoch Epoch
    {
        get { lock (_gate) return new SessionEpoch(_generation); }
    }

    public bool IsOwner => Environment.CurrentManagedThreadId == _managedThreadId;

    public bool Validate(SessionEpoch epoch) => IsOwner && epoch.Value == Epoch.Value;

    public SessionEpoch AdvanceEpoch()
    {
        if (!IsOwner) throw new InvalidOperationException("Only the simulation owner may advance the epoch.");
        lock (_gate)
        {
            checked { _generation++; }
            return new SessionEpoch(_generation);
        }
    }
}
