using System;

namespace Lumio.GameRuntime.Observability;

public interface IRuntimeEventPort
{
    EventEnqueueResult Emit(in RuntimeEventView value);
}

public readonly record struct CorrelationView(
    string Scope,
    string ProductId,
    string GameReleaseId,
    string SessionId,
    string WorldId,
    string TraceId,
    string ProducerId,
    ulong EventSeq)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Scope) &&
        !string.IsNullOrWhiteSpace(ProductId) &&
        !string.IsNullOrWhiteSpace(GameReleaseId) &&
        !string.IsNullOrWhiteSpace(TraceId) &&
        !string.IsNullOrWhiteSpace(ProducerId) &&
        EventSeq != 0;
}

public readonly record struct RuntimeEventView(
    string EventId,
    string Category,
    string Severity,
    DateTimeOffset Timestamp,
    CorrelationView Correlation,
    string Message,
    string Durability)
{
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(EventId) &&
        !string.IsNullOrWhiteSpace(Category) &&
        !string.IsNullOrWhiteSpace(Severity) &&
        !string.IsNullOrWhiteSpace(Message) &&
        !string.IsNullOrWhiteSpace(Durability) &&
        Message.Length <= 8192 &&
        Correlation.IsComplete;
}

public enum EventEnqueueStatus
{
    Accepted,
    Sampled,
    Rejected,
    Backpressured
}

public readonly record struct EventEnqueueResult(EventEnqueueStatus Status, string? GeneratedErrorId)
{
    public bool IsAccepted => Status is EventEnqueueStatus.Accepted or EventEnqueueStatus.Sampled;

    public static EventEnqueueResult Accepted() => new(EventEnqueueStatus.Accepted, null);

    public static EventEnqueueResult Rejected(string generatedErrorId) =>
        new(EventEnqueueStatus.Rejected, generatedErrorId);
}
