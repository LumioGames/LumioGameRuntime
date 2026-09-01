using System.Collections.Generic;
using Lumio.Gen.CanonicalSerializer;
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

    [Fact]
    public void DuplicateKeyIsTxnIdAndRequestDigestIgnoringOtherIdentityFields()
    {
        SessionRevisionVectorView expected = Vector();
        SessionRevisionVectorView otherExpected = new(1UL, 2UL, 1UL, new Dictionary<string, ulong>(), 1UL, 1UL, 1UL);
        const string digest = "2013b6c1dee602db6b55973dde708d4110b0e81348bae28cae10ef0201f85bb7";
        var first = new TxnRecord("session", "txn-key", 1UL, "command-a", expected, 10UL, digest);
        var replay = new TxnRecord("session", "txn-key", 1UL, "command-b", otherExpected, 11UL, digest);
        var index = new TxnIdempotencyIndex();

        Assert.Equal(TxnLookupStatus.New, index.Register(first).Status);
        TxnLookupResult duplicate = index.Register(replay);

        Assert.Equal(TxnLookupStatus.Duplicate, duplicate.Status);
        Assert.Same(first, duplicate.Record);
        Assert.Equal(digest, first.RequestDigest);
    }

    [Fact]
    public void LostCallerQueryResultReturnsOriginalIndexState()
    {
        SessionRevisionVectorView expected = Vector();
        const string digest = "2013b6c1dee602db6b55973dde708d4110b0e81348bae28cae10ef0201f85bb7";
        var coordinator = new CrossWorldCoordinator();
        _ = coordinator.Begin(new TxnRequest("session", "txn-lost", 1UL, "command", expected, 10UL, digest));

        TxnCommitResult queried = coordinator.QueryResult(new TxnId("txn-lost"));

        Assert.NotNull(queried.Record);
        Assert.Equal("txn-lost", queried.Record!.TxnId);
        Assert.Equal(digest, queried.Record.RequestDigest);
        Assert.Equal(CrossWorldTxnState.Created, queried.Record.State);
        Assert.Same(queried.Record, coordinator.QueryResult(new TxnId("txn-lost")).Record);
    }

    [Fact]
    public void RequestDigestUtf8LayoutHasGoldenHexAndIsNotLumioBinV1()
    {
        SessionRevisionVectorView expected = Vector();
        Assert.Equal("c1fdfcb726e471de7683dba38b229a0877b8f32a08ce6059da09679925906540", expected.CanonicalDigestHex);
        string digest = TxnRequestDigest.HashHex(
            "session", "runtime", "txn-a", "command-a", 1UL, expected.CanonicalDigestHex);
        Assert.Equal("e0f90796b3534231f37efcf902a68178520e4423100f565888680964acd5666c", digest);
        Assert.Equal(64, digest.Length);
        Assert.NotEqual(LumioBinForm.FormId, "utf8-field-layout");
        Assert.Equal("LittleEndian", LumioBinForm.ByteOrder);

        string otherCommand = TxnRequestDigest.HashHex(
            "session", "runtime", "txn-a", "command-b", 1UL, expected.CanonicalDigestHex);
        Assert.Equal("05d595188bce06496d57ad0c9981f55dfafbcca89c9b479f75900ba771d36560", otherCommand);
        Assert.NotEqual(digest, otherCommand);

        var index = new TxnIdempotencyIndex();
        TxnRecord first = Record("txn-hashed", digest);
        TxnRecord replay = new(
            "session", "txn-hashed", 1UL, "command-b", expected, 10UL, digest);
        Assert.Equal(TxnLookupStatus.New, index.Register(first).Status);
        Assert.Equal(TxnLookupStatus.Duplicate, index.Register(replay).Status);
        Assert.Equal(TxnLookupStatus.Conflict, index.Register(Record("txn-hashed", otherCommand)).Status);
    }

    private static TxnRecord Record(string txnId, string digest) =>
        new("session", txnId, 1UL, "command", Vector(), 10UL, digest);

    private static SessionRevisionVectorView Vector() =>
        new(1UL, 1UL, 1UL, new Dictionary<string, ulong>(), 1UL, 1UL, 1UL);
}
