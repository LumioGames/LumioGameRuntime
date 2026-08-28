namespace Lumio.GameRuntime.Observability;

public sealed class ObservabilityServices
{
    internal ObservabilityServices(
        IRuntimeEventPort events,
        IMetricPort metrics,
        ITracePort traces)
    {
        Events = events;
        Metrics = metrics;
        Traces = traces;
    }

    public IRuntimeEventPort Events { get; }

    public IMetricPort Metrics { get; }

    public ITracePort Traces { get; }
}
