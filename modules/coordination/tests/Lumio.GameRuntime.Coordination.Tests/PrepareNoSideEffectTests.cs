using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class PrepareNoSideEffectTests
{
    [Fact]
    public void RevisionConflictRejectsBeforeAnyReservation()
    {
        SessionRevisionVectorView current = Vector(1UL);
        var revisions = new SessionRevisionVectorStore(current);
        var game = new CountingGamePort();
        var voxel = new CountingVoxelPort(current);
        var coordinator = new TxnPrepareCoordinator(revisions, new CrossWorldCoordinator(), game, voxel);
        TxnPrepareRequest request = Request(expectedRevision: 0UL);

        TxnPrepareResult result = coordinator.Prepare(request);

        Assert.Equal(TxnPrepareStatus.Rejected, result.Status);
        Assert.Equal("RevisionConflict", result.Failure!.GeneratedErrorId);
        Assert.Equal(0, game.Calls);
        Assert.Equal(0, voxel.PrepareCalls);
        Assert.Equal(current, revisions.Read());
    }

    [Fact]
    public void PreparedLeasesReleaseExactlyOnce()
    {
        SessionRevisionVectorView current = Vector(1UL);
        var revisions = new SessionRevisionVectorStore(current);
        var coordinator = new TxnPrepareCoordinator(revisions, new CrossWorldCoordinator(), new CountingGamePort(), new CountingVoxelPort(current));
        TxnPrepareResult result = coordinator.Prepare(Request(expectedRevision: 1UL));
        Assert.True(result.IsPrepared);
        result.Prepared!.Dispose();
        result.Prepared.Dispose();
        Assert.Equal(1, result.Prepared.GameReservation.ReleaseCount);
        Assert.Equal(1, result.Prepared.VoxelReservation.ReleaseCount);
    }

    [Fact]
    public async Task ConcurrentDuplicatePreparePublishesOneLeasePair()
    {
        SessionRevisionVectorView current = Vector(1UL);
        var game = new CountingGamePort();
        var voxel = new CountingVoxelPort(current);
        var coordinator = new TxnPrepareCoordinator(
            new SessionRevisionVectorStore(current),
            new CrossWorldCoordinator(),
            game,
            voxel);
        TxnPrepareRequest request = Request(expectedRevision: 1UL);

        TxnPrepareResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => coordinator.Prepare(request))));

        Assert.All(results, result => Assert.True(result.IsPrepared));
        Assert.Single(results.Select(result => result.Prepared).Distinct());
        Assert.Equal(1, game.Calls);
        Assert.Equal(1, voxel.PrepareCalls);
    }

    [Fact]
    public void MalformedVoxelReservationIsReleasedBeforeReturningFailure()
    {
        SessionRevisionVectorView current = Vector(1UL);
        var game = new CountingGamePort();
        var malformed = new PreparedVoxelTokenLease("malformed-token", 10UL);
        var coordinator = new TxnPrepareCoordinator(
            new SessionRevisionVectorStore(current),
            new CrossWorldCoordinator(),
            game,
            new MalformedVoxelPort(malformed));

        TxnPrepareResult result = coordinator.Prepare(Request(expectedRevision: 1UL));

        Assert.False(result.IsPrepared);
        Assert.Equal(TxnPrepareStatus.Rejected, result.Status);
        Assert.Equal(1, malformed.ReleaseCount);
        Assert.Equal(1, game.Calls);
    }

    private static TxnPrepareRequest Request(ulong expectedRevision)
    {
        PreparedGameDelta delta = Prepared(2UL);
        return new TxnPrepareRequest("session", "txn", 2UL, "command", expectedRevision, expectedRevision,
            new Dictionary<string, ulong>(), 10UL, 1, delta, "digest");
    }

    internal static PreparedGameDelta Prepared(ulong tick)
    {
        var buffer = new ProcessorCommandBuffer(tick, "processor", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("entity");
        return new CommandPreflightValidator().Prepare(new CommandBufferMerger().Merge(tick, new[] { buffer.Seal() }));
    }

    internal static SessionRevisionVectorView Vector(ulong revision) =>
        new(1UL, revision, revision, new Dictionary<string, ulong>(), revision, 1UL, 1UL);

    private sealed class CountingGamePort : IGameReservationPort
    {
        public int Calls { get; private set; }

        public GameReservationResult Reserve(in GameReservationRequest request)
        {
            Calls++;
            return new GameReservationResult(GameReservationStatus.Reserved, new ReservationLease("game"), null);
        }
    }

    private sealed class CountingVoxelPort : IVoxelWorldPort
    {
        private readonly SessionRevisionVectorView _revision;
        internal CountingVoxelPort(SessionRevisionVectorView revision) => _revision = revision;
        public int PrepareCalls { get; private set; }
        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request)
        {
            PrepareCalls++;
            return VoxelPrepareResult.Prepared("voxel-token", request.DeadlineTick);
        }
        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) => VoxelCommitParticipantResult.Applied(_revision);
        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);
        public VoxelParticipantQueryResult Query(string sessionId, string txnId) => new(TxnParticipantState.NotStarted, true, null, null);
        public SessionRevisionVectorView ReadRevision() => _revision;
    }

    private sealed class MalformedVoxelPort : IVoxelWorldPort
    {
        private readonly PreparedVoxelTokenLease _lease;

        internal MalformedVoxelPort(PreparedVoxelTokenLease lease) => _lease = lease;

        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) =>
            new(VoxelPrepareStatus.Rejected, null, _lease,
                CoordinationFailure.Rejected("ManifestMalformed", "Malformed voxel result."));

        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) => VoxelCommitParticipantResult.Applied();

        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);

        public VoxelParticipantQueryResult Query(string sessionId, string txnId) =>
            new(TxnParticipantState.NotStarted, true, null, null);

        public SessionRevisionVectorView ReadRevision() => new(0UL, 0UL, 0UL,
            new Dictionary<string, ulong>(), 0UL, 0UL, 1UL);
    }
}
