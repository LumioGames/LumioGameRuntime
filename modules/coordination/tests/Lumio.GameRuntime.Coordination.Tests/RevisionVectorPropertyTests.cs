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

    [Theory]
    [InlineData("game")]
    [InlineData("voxel")]
    [InlineData("replication")]
    [InlineData("config")]
    public void DomainRegressionIsRejectedAndStoreUnchanged(string domain)
    {
        SessionRevisionVectorView current = Distinct(2UL);
        SessionRevisionVectorView next = Regress(current, domain, 3UL);
        var store = new SessionRevisionVectorStore(current);
        SessionCoordinationContext context = SessionCoordinationContext.For(store);
        Assert.True(context.TryEnter(Identity("txn-domain-" + domain, 3UL, current),
            out TxnAuthorityOperation operation, out _));
        SessionRevisionVectorView before = store.Read();

        RevisionReservationResult rejected = store.TryReserveStrict(current, next, operation);
        operation.Dispose();

        Assert.False(rejected.Succeeded);
        Assert.Equal(RevisionReservationStatus.Rejected, rejected.Status);
        Assert.Equal(CoordinationFailureClass.Rejected, rejected.Failure!.Class);
        Assert.Equal("RevisionConflict", rejected.Failure.GeneratedErrorId);
        Assert.Equal(before, store.Read());
    }

    [Fact]
    public void SchemaEpochSwitchIsFatalAndStoreUnchanged()
    {
        SessionRevisionVectorView current = Distinct(2UL);
        SessionRevisionVectorView next = new(
            3UL,
            current.GameRevision,
            current.VoxelWorldRevision,
            current.ChunkRevisionSet,
            current.ReplicationRevision,
            current.ConfigRevision,
            current.SchemaEpoch + 1UL);
        var store = new SessionRevisionVectorStore(current);
        SessionCoordinationContext context = SessionCoordinationContext.For(store);
        Assert.True(context.TryEnter(Identity("txn-epoch", 3UL, current),
            out TxnAuthorityOperation operation, out _));
        SessionRevisionVectorView before = store.Read();

        RevisionReservationResult rejected = store.TryReserveStrict(current, next, operation);
        operation.Dispose();

        Assert.False(rejected.Succeeded);
        Assert.Equal(RevisionReservationStatus.Fatal, rejected.Status);
        Assert.Equal(CoordinationFailureClass.Fatal, rejected.Failure!.Class);
        Assert.Equal("InternalInvariant", rejected.Failure.GeneratedErrorId);
        Assert.Equal(before, store.Read());
    }

    [Fact]
    public void UncommittedReservationDoesNotAdvanceStore()
    {
        SessionRevisionVectorView current = Vector(1UL, 1UL);
        SessionRevisionVectorView next = Vector(2UL, 2UL);
        var store = new SessionRevisionVectorStore(current);
        SessionCoordinationContext context = SessionCoordinationContext.For(store);
        Assert.True(context.TryEnter(Identity("txn-hold", 2UL, current),
            out TxnAuthorityOperation operation, out _));

        RevisionReservationResult reserved = store.TryReserveStrict(current, next, operation);
        Assert.True(reserved.Succeeded);
        Assert.Equal(current, store.Read());
        reserved.Reservation!.Release();
        operation.Dispose();
        Assert.Equal(current, store.Read());
    }

    [Fact]
    public void AdvanceWithoutAuthorityIsRejectedAndStoreUnchanged()
    {
        SessionRevisionVectorView current = Vector(1UL, 1UL);
        SessionRevisionVectorView next = Vector(2UL, 2UL);
        var store = new SessionRevisionVectorStore(current);
        SessionCoordinationContext context = SessionCoordinationContext.For(store);
        Assert.True(context.TryEnter(Identity("txn-no-auth", 2UL, current),
            out TxnAuthorityOperation operation, out _));
        operation.Dispose();

        RevisionReservationResult rejected = store.TryReserveStrict(current, next, operation);

        Assert.False(rejected.Succeeded);
        Assert.Equal(RevisionReservationStatus.Rejected, rejected.Status);
        Assert.Equal("InvalidArgument", rejected.Failure!.GeneratedErrorId);
        Assert.Equal(current, store.Read());
    }

    [Fact]
    public void RevisionVectorUtf8DigestMatchesGoldenHex()
    {
        SessionRevisionVectorView vector = new(1UL, 1UL, 1UL, new Dictionary<string, ulong>(), 1UL, 1UL, 1UL);
        Assert.Equal("c1fdfcb726e471de7683dba38b229a0877b8f32a08ce6059da09679925906540", vector.CanonicalDigestHex);
        Assert.Equal("LittleEndian", Lumio.Gen.CanonicalSerializer.LumioBinForm.ByteOrder);
        Assert.NotEqual(Lumio.Gen.CanonicalSerializer.LumioBinForm.FormId, "utf8-field-layout");
    }

    private static TxnIdentity Identity(string txnId, ulong tick, SessionRevisionVectorView expected) =>
        new("session", "runtime", txnId, "command", tick, "digest", expected.CanonicalDigestHex);

    private static SessionRevisionVectorView Vector(ulong tick, ulong revision) =>
        new(tick, revision, revision, new Dictionary<string, ulong> { ["c:0:0:0"] = revision }, revision, 1UL, 1UL);

    private static SessionRevisionVectorView Distinct(ulong tick) =>
        new(tick, 10UL, 20UL, new Dictionary<string, ulong> { ["c:0:0:0"] = 20UL }, 30UL, 40UL, 1UL);

    private static SessionRevisionVectorView Regress(SessionRevisionVectorView current, string domain, ulong tick)
    {
        ulong game = current.GameRevision;
        ulong voxel = current.VoxelWorldRevision;
        ulong replication = current.ReplicationRevision;
        ulong config = current.ConfigRevision;
        switch (domain)
        {
            case "game":
                game--;
                break;
            case "voxel":
                voxel--;
                break;
            case "replication":
                replication--;
                break;
            default:
                config--;
                break;
        }

        return new(tick, game, voxel, current.ChunkRevisionSet, replication, config, current.SchemaEpoch);
    }
}
