using System.Collections.Generic;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class DuplicateLostResultTests
{
    [Fact]
    public void DuplicateSameDigestReturnsOriginalAndConflictIsFatal()
    {
        var index = new TxnIdempotencyIndex(2);
        TxnRecord first = Record("txn", "digest-a");
        TxnRecord replay = Record("txn", "digest-a");
        TxnRecord conflict = Record("txn", "digest-b");

        Assert.Equal(TxnLookupStatus.New, index.Register(first).Status);
        TxnLookupResult duplicate = index.Register(replay);
        TxnLookupResult mismatch = index.Register(conflict);
        Assert.Equal(TxnLookupStatus.Duplicate, duplicate.Status);
        Assert.Same(first, duplicate.Record);
        Assert.Equal(TxnLookupStatus.Conflict, mismatch.Status);
        Assert.Equal(CoordinationFailureClass.Fatal, mismatch.Failure!.Class);
        Assert.Same(first, index.Lookup("txn", "digest-a").Record);
    }

    private static TxnRecord Record(string txnId, string digest) =>
        new("session", txnId, 1UL, "command", new SessionRevisionVectorView(1UL, 1UL, 1UL,
            new Dictionary<string, ulong>(), 1UL, 1UL, 1UL), 10UL, digest);
}
