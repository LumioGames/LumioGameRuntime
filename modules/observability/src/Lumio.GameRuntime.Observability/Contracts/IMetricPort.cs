using System;

namespace Lumio.GameRuntime.Observability;

public interface IMetricPort
{
    MetricRecordResult Record(in MetricSampleView sample);
    MetricSnapshot CaptureSnapshot();
}

public readonly record struct MetricSampleView(
    string MetricId,
    double Value,
    CorrelationView Correlation)
{
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(MetricId) &&
        !double.IsNaN(Value) &&
        !double.IsInfinity(Value) &&
        Correlation.IsComplete;
}

public enum MetricRecordStatus
{
    Accepted,
    Rejected,
    Backpressured
}

public readonly record struct MetricRecordResult(MetricRecordStatus Status, string? GeneratedErrorId)
{
    public bool IsAccepted => Status == MetricRecordStatus.Accepted;

    public static MetricRecordResult Accepted() => new(MetricRecordStatus.Accepted, null);

    public static MetricRecordResult Rejected(string generatedErrorId) =>
        new(MetricRecordStatus.Rejected, generatedErrorId);
}

public readonly record struct MetricSnapshot(long SampleCount, DateTimeOffset CapturedAt);
