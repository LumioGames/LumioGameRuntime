namespace Lumio.GameRuntime.Coordination;

public enum CoordinatorState
{
    Created,
    Ready,
    Running,
    Draining,
    Disposed,
    Faulted
}

public readonly record struct CoordinationLifecycleResult(
    bool Succeeded,
    CoordinatorState State,
    CoordinationFailure? Failure);
