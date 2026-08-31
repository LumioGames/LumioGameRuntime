using Lumio.GameRuntime.Replication;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class RevisionStoreTests
{
    [Fact]
    public void AuthorityRevisionAdvancesOnlyAfterCommitAndRejectsRegressions()
    {
        var initial = new RevisionVector(1, 1, 1, 1, 1, 1, 1);
        var store = new AuthorityRevisionStore(initial);
        Assert.Equal(RevisionAdvanceStatus.Rejected, store.TryAdvance(new RevisionVector(2, 2, 2, 2, 2, 2, 1), committed: false).Status);
        Assert.Equal(RevisionAdvanceStatus.Advanced, store.TryAdvance(new RevisionVector(2, 2, 2, 2, 2, 2, 1)).Status);
        Assert.Equal(RevisionAdvanceStatus.AlreadyCurrent, store.TryAdvance(new RevisionVector(2, 2, 2, 2, 2, 2, 1)).Status);
        Assert.Equal(RevisionAdvanceStatus.Rejected, store.TryAdvance(initial).Status);
    }
}
