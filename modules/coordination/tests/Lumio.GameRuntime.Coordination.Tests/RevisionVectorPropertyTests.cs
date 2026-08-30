using System.Collections.Generic;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class RevisionVectorPropertyTests
{
    [Fact]
    public void CommittedRevisionsAdvanceMonotonicallyAndRegressionIsRejected()
    {
        var store = new SessionRevisionVectorStore(Vector(1UL, 1UL));
        RevisionAdvanceResult advanced = store.AdvanceCommitted(Vector(2UL, 2UL));
        RevisionAdvanceResult rejected = store.AdvanceCommitted(Vector(3UL, 1UL));
        RevisionAdvanceResult uncommitted = store.TryAdvance(Vector(3UL, 3UL), false);

        Assert.True(advanced.Succeeded);
        Assert.False(rejected.Succeeded);
        Assert.Equal("RevisionConflict", rejected.Failure!.GeneratedErrorId);
        Assert.False(uncommitted.Succeeded);
        Assert.Equal(Vector(2UL, 2UL), store.Read());
    }

    private static SessionRevisionVectorView Vector(ulong tick, ulong revision) =>
        new(tick, revision, revision, new Dictionary<string, ulong> { ["c:0:0:0"] = revision }, revision, 1UL, 1UL);
}
