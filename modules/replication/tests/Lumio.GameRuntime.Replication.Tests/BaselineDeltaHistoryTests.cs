using System;
using Lumio.GameRuntime.Replication.History;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class BaselineDeltaHistoryTests
{
    [Fact]
    public void BaselineAckIsIndependentAndIdempotent()
    {
        var store = new BaselineStore(new ReplicationBudget(4, 4096, 8, 4096));
        Assert.Equal(BaselineStoreStatus.Accepted, store.Add(new BaselineRecord("snap-1", 10, 100)));
        Assert.Equal(BaselineAckStatus.Acknowledged, store.Ack("snap-1", 10));
        Assert.Equal(BaselineAckStatus.AlreadyAcknowledged, store.Ack("snap-1", 10));
        Assert.True(store.IsAcknowledged("snap-1"));
    }

    [Fact]
    public void MissingLinkProducesGapAndRequiresResync()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));
        history.Add(new DeltaRecord("snap-1", 10, 11, 2, 10));
        var result = history.TryGetContiguous("snap-1", 9, 11);
        Assert.Equal(DeltaChainStatus.Gap, result.Status);
        Assert.True(result.RequiresResync);
    }
}
