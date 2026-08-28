using System;
using Lumio.GameRuntime.Observability;
using Xunit;

namespace Lumio.GameRuntime.Observability.Tests;

public sealed class DiagnosticBackpressureTests
{
    [Fact]
    public void BestEffortEventIsDroppedWithMetricWhenQueueIsFull()
    {
        var queue = DiagnosticEventQueue.Create(new DiagnosticQueueBudget(2, 4096));
        var first = Event(1);
        var second = Event(2);
        var third = Event(3);

        Assert.Equal(DiagnosticWriteStatus.Accepted, queue.TryWrite(in first).Status);
        Assert.Equal(DiagnosticWriteStatus.Accepted, queue.TryWrite(in second).Status);
        Assert.Equal(DiagnosticWriteStatus.DroppedBestEffort, queue.TryWrite(in third).Status);
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.DroppedCount);
        Assert.Equal("QueueFull", queue.DropSummary.Reason);
        Assert.Equal(1, queue.DropSummary.DroppedCount);
    }

    [Fact]
    public void CompletingQueueRejectsNewEventsWithoutDroppingAcceptedItems()
    {
        var queue = DiagnosticEventQueue.Create(new DiagnosticQueueBudget(2, 4096));
        var value = Event(1);

        Assert.Equal(DiagnosticWriteStatus.Accepted, queue.TryWrite(in value).Status);
        queue.Complete();

        Assert.Equal(DiagnosticWriteStatus.Closed, queue.TryWrite(in value).Status);
        Assert.Single(queue.ReadBatch(2));
        Assert.Equal(0, queue.DroppedCount);
    }

    private static RuntimeEventView Event(int id) => new(
        $"event-{id}",
        "Diagnostic",
        "Info",
        DateTimeOffset.UnixEpoch,
        new CorrelationView("Session", "product", "release", "session", "world", "trace", "producer", (ulong)id),
        "message",
        "BestEffort");
}
