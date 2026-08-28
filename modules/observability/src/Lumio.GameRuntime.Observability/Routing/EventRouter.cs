using System.Text;

namespace Lumio.GameRuntime.Observability;

internal sealed class EventRouter
{
    private readonly DiagnosticEventQueue _diagnosticQueue;
    private readonly IDurableEvidencePort _durableEvidence;

    internal EventRouter(DiagnosticEventQueue diagnosticQueue, IDurableEvidencePort durableEvidence)
    {
        _diagnosticQueue = diagnosticQueue;
        _durableEvidence = durableEvidence;
    }

    internal EventEnqueueResult Route(in RuntimeEventView value)
    {
        if (!value.IsWellFormed)
        {
            return EventEnqueueResult.Rejected("ManifestMalformed");
        }

        return value.Durability == "BestEffort"
            ? RouteBestEffort(in value)
            : RouteDurable(in value);
    }

    private EventEnqueueResult RouteBestEffort(in RuntimeEventView value)
    {
        var result = _diagnosticQueue.TryWrite(in value);
        return result.Status switch
        {
            DiagnosticWriteStatus.Accepted => EventEnqueueResult.Accepted(),
            DiagnosticWriteStatus.DroppedBestEffort => new EventEnqueueResult(EventEnqueueStatus.Sampled, null),
            DiagnosticWriteStatus.Closed => EventEnqueueResult.Rejected(result.GeneratedErrorId ?? "ManifestMalformed"),
            _ => EventEnqueueResult.Rejected(result.GeneratedErrorId ?? "ManifestMalformed")
        };
    }

    private EventEnqueueResult RouteDurable(in RuntimeEventView value)
    {
        var record = new DurableRecordView(
            value.EventId,
            value.Category,
            Encoding.UTF8.GetBytes(value.Message),
            value.Correlation);

        var result = _durableEvidence.Enqueue(in record);
        return result.Status switch
        {
            DurableEnqueueStatus.Accepted => EventEnqueueResult.Accepted(),
            // 满载的可靠 record 只能背压等重试,既不得改写成 success,
            // 也不得转入 Diagnostic queue 当 BestEffort 掉包(T05.S02/S06)。
            DurableEnqueueStatus.Backpressured =>
                new EventEnqueueResult(EventEnqueueStatus.Backpressured, result.GeneratedErrorId),
            _ => EventEnqueueResult.Rejected(result.GeneratedErrorId ?? "ManifestMalformed")
        };
    }
}
