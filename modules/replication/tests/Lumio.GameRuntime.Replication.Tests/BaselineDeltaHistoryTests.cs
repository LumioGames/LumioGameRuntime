using System;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Mapping;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class BaselineDeltaHistoryTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void BaselineAckIsIndependentAndIdempotent()
    {
        var store = new BaselineStore(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken token = store.CaptureToken();
        Assert.Equal(BaselineStoreStatus.Accepted, store.Add(new BaselineRecord("snap-1", 10, 100, Hash), token));
        Assert.Equal(BaselineAckStatus.Acknowledged, store.Ack("snap-1", 10, token));
        Assert.Equal(BaselineAckStatus.AlreadyAcknowledged, store.Ack("snap-1", 10, token));
        Assert.True(store.IsAcknowledged("snap-1"));
    }

    [Fact]
    public void MissingLinkProducesGapAndRequiresResync()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken oldToken = history.CaptureToken();
        Assert.True(history.ResetForBaseline("snap-1", 0, 9, oldToken));
        IdentityStoreToken token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-1", 10, 11, 2, 10, Hash), token));
        var result = history.TryGetContiguous("snap-1", 9, 11);
        Assert.Equal(DeltaChainStatus.Gap, result.Status);
        Assert.True(result.RequiresResync);
    }
}
