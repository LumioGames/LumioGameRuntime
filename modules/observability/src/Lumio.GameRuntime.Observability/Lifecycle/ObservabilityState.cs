namespace Lumio.GameRuntime.Observability;

public enum ObservabilityState
{
    Created,
    Configured,
    Running,
    Degraded,
    Flushing,
    Closed,
    Faulted
}
