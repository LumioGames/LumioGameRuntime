using System.Collections.Generic;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class RevisionVectorPropertyTests
{
    [Fact]
    public void CommittedRevisionsAdvanceMonotonicallyAndRegressionIsRejected()
    {
        SessionRevisionVectorView current = Vector(1UL, 1UL);
        SessionRevisionVectorView next = Vector(2UL, 2UL);
        var store = new SessionRevisionVectorStore(current);
        SessionCoordinationContext context = SessionCoordinationContext.For(store);
        Assert.True(context.TryEnter(Identity("txn-advance", 2UL, current),
            out TxnAuthorityOperation first, out _));
        RevisionReservationResult reserved = store.TryReserveStrict(current, next, first);
        Assert.True(reserved.Succeeded);
        RevisionAdvanceResult advanced = reserved.Reservation!.Commit();
        first.Dispose();

        Assert.True(context.TryEnter(Identity("txn-regress", 3UL, next),
            out TxnAuthorityOperation second, out _));
        RevisionReservationResult rejected = store.TryReserveStrict(next, Vector(3UL, 1UL), second);
        second.Dispose();

        Assert.True(advanced.Succeeded);
        Assert.False(rejected.Succeeded);
        Assert.Equal("RevisionConflict", rejected.Failure!.GeneratedErrorId);
        Assert.Equal(Vector(2UL, 2UL), store.Read());
    }

    private static TxnIdentity Identity(string txnId, ulong tick, SessionRevisionVectorView expected) =>
        new("session", "runtime", txnId, "command", tick, "digest", expected.CanonicalDigestHex);

    private static SessionRevisionVectorView Vector(ulong tick, ulong revision) =>
        new(tick, revision, revision, new Dictionary<string, ulong> { ["c:0:0:0"] = revision }, revision, 1UL, 1UL);
}
