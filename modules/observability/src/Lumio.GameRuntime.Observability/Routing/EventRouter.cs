namespace Lumio.GameRuntime.Observability;

internal sealed class EventRouter
{
    private readonly DiagnosticEventQueue _diagnosticQueue;

    internal EventRouter(DiagnosticEventQueue diagnosticQueue)
    {
        _diagnosticQueue = diagnosticQueue;
    }

    internal EventEnqueueResult Route(in RuntimeEventView value)
    {
        if (!value.IsWellFormed)
        {
            return EventEnqueueResult.Rejected("ManifestMalformed");
        }

        if (value.Durability != "BestEffort")
        {
            return new EventEnqueueResult(EventEnqueueStatus.Backpressured, "QueueFull");
        }

        var result = _diagnosticQueue.TryWrite(in value);
        return result.Status switch
        {
            DiagnosticWriteStatus.Accepted => EventEnqueueResult.Accepted(),
            DiagnosticWriteStatus.DroppedBestEffort => new EventEnqueueResult(EventEnqueueStatus.Sampled, null),
            DiagnosticWriteStatus.Closed => EventEnqueueResult.Rejected(result.GeneratedErrorId ?? "ManifestMalformed"),
            _ => EventEnqueueResult.Rejected(result.GeneratedErrorId ?? "ManifestMalformed")
        };
    }
}
