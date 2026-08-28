using System;
using Lumio.GameRuntime.Observability;
using Xunit;

namespace Lumio.GameRuntime.Observability.Tests;

public sealed class DurableRouteFailureTests
{
    [Fact]
    public void DurableQueueFullReturnsBackpressureAndNeverBestEffortDrop()
    {
        var router = new DurableEvidenceRouter(1);
        var first = Record("key-1");
        var second = Record("key-2");

        var accepted = router.Enqueue(in first);
        var backpressured = router.Enqueue(in second);
        var retry = router.Enqueue(in first);

        Assert.Equal(DurableEnqueueStatus.Accepted, accepted.Status);
        Assert.Equal(DurableEnqueueStatus.Backpressured, backpressured.Status);
        Assert.Equal("QueueFull", backpressured.GeneratedErrorId);
        Assert.Equal(DurableEnqueueStatus.Accepted, retry.Status);
        Assert.True(retry.AlreadyPresent);
        Assert.Equal(accepted.RecordSequence, retry.RecordSequence);
        Assert.Equal(DurableQueryStatus.Found, router.Query("key-1").Status);
    }

    [Fact]
    public void DurableRouteIsClosedExplicitly()
    {
        var router = new DurableEvidenceRouter(1);
        router.Complete();
        var value = Record("key-1");

        Assert.Equal(DurableEnqueueStatus.Closed, router.Enqueue(in value).Status);
        Assert.Equal(DurableQueryStatus.Closed, router.Query("key-1").Status);
    }

    private static DurableRecordView Record(string key) => new(
        key,
        "TxnJournal",
        new byte[] { 1, 2, 3 },
        new CorrelationView("Txn", "product", "release", "session", "world", "trace", "producer", 1UL));
}
