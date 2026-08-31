using System.Collections.Generic;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class CommitIntentOrderingTests
{
    private static readonly string[] ExpectedTrace =
    {
        "Journal.CommitIntent.Durable",
        "Voxel.Apply",
        "Journal.VoxelMarker.Durable",
        "ECS.Apply",
        "Journal.EcsMarker.Durable",
        "Journal.Committed.Durable",
        "Revision.Advance"
    };

    [Fact]
    public void DurableIntentPrecedesVoxelAndEcsApply()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        var revisions = new SessionRevisionVectorStore(current);
        var journal = new InMemoryTxnJournalPort(16);
        var voxel = new AppliedVoxelPort(current);
        PreparedGameDelta delta = PrepareNoSideEffectTests.Prepared(2UL);
        TxnRecord record = Record(delta);
        var leases = new CrossWorldPreparedTxn(record, new ReservationLease("game"), new PreparedVoxelTokenLease("voxel-token", 10UL));
        var coordinator = new CommitIntentCoordinator(revisions, journal, voxel, new EcsCommandCommitExecutor());

        TxnCommitResult result = coordinator.Commit(record, null, leases);

        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.Equal(ExpectedTrace, result.Trace);
    }

    [Fact]
    public void IntentBackpressureCallsNoParticipantAndLeavesPrepared()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        var revisions = new SessionRevisionVectorStore(current);
        var journal = new InMemoryTxnJournalPort(1);
        var filler = TxnJournalRecordFactory.Create("session", "runtime", 1UL, "other", Lumio.Gen.ContractTypes.TxnJournalRecordRecordKind.Prepare, "filler");
        journal.Append(in filler);
        var voxel = new AppliedVoxelPort(current);
        TxnRecord record = Record(PrepareNoSideEffectTests.Prepared(2UL));
        var coordinator = new CommitIntentCoordinator(revisions, journal, voxel, new EcsCommandCommitExecutor());
        TxnCommitResult result = coordinator.Commit(record);
        Assert.Equal(TxnCommitStatus.Retryable, result.Status);
        Assert.Equal(CrossWorldTxnState.Prepared, record.State);
        Assert.Equal(0, voxel.CommitCalls);
    }

    [Fact]
    public void JournalReplayWithFreshChainCursorIsIdempotent()
    {
        var journal = new InMemoryTxnJournalPort(4);
        TxnJournalRecord first = TxnJournalRecordFactory.Create(
            "session", "runtime", 1UL, "txn-replay",
            TxnJournalRecordRecordKind.CommitIntent, "txn-replay:commit-intent");
        TxnJournalRecord replay = TxnJournalRecordFactory.Create(
            "session", "runtime", 1UL, "txn-replay",
            TxnJournalRecordRecordKind.CommitIntent, "txn-replay:commit-intent");

        TxnJournalAppendResult firstResult = journal.Append(in first);
        TxnJournalAppendResult replayResult = journal.Append(in replay);

        Assert.True(firstResult.IsDurable);
        Assert.True(replayResult.IsDurable);
        Assert.True(replayResult.AlreadyPresent);
        Assert.False(journal.IsFatal);
        Assert.Equal(1, journal.Count);
    }

    private static TxnRecord Record(PreparedGameDelta delta)
    {
        var record = new TxnRecord("session", "txn", 2UL, "command", PrepareNoSideEffectTests.Vector(1UL), 10UL, "digest");
        record.AttachPreparedDelta(delta, "voxel-token");
        record.TryTransition(CrossWorldTxnState.Prepared);
        return record;
    }

    private sealed class AppliedVoxelPort : IVoxelWorldPort
    {
        private readonly SessionRevisionVectorView _revision;
        internal AppliedVoxelPort(SessionRevisionVectorView revision) => _revision = revision;
        public int CommitCalls { get; private set; }
        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) => VoxelPrepareResult.Prepared("token", request.DeadlineTick);
        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request)
        {
            CommitCalls++;
            return VoxelCommitParticipantResult.Applied(_revision);
        }
        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);
        public VoxelParticipantQueryResult Query(string sessionId, string txnId) => new(TxnParticipantState.Applied, true, null, _revision);
        public SessionRevisionVectorView ReadRevision() => _revision;
    }
}
