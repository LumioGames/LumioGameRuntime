namespace Lumio.GameRuntime.Simulation.Session;

public enum SimulationSessionState
{
    Created,
    Initialized,
    Ready,
    Running,
    Paused,
    Draining,
    Snapshotted,
    Disposed,
    Faulted
}

public readonly record struct SessionEpoch(ulong Value)
{
    public bool IsValid => Value > 0;
}

public readonly record struct LifecycleResult(
    bool Succeeded,
    SimulationSessionState State,
    string? GeneratedErrorId)
{
    public static LifecycleResult Accepted(SimulationSessionState state) => new(true, state, null);

    public static LifecycleResult Rejected(SimulationSessionState state, string errorId) => new(false, state, errorId);
}
