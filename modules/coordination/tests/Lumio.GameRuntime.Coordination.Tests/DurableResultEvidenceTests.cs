using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class DurableResultEvidenceTests
{
    [Fact]
    public void EvidenceFailurePreventsTerminalMarkerAndRevisionAdvance()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView next = PrepareNoSideEffectTests.Vector(2UL);
        var journal = new InMemoryTxnJournalPort(16);
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = PreparedRecord("txn-evidence-failure");
        TxnCommitResult result = new CommitIntentCoordinator(
            revisions,
            journal,
            new AppliedVoxel(next),
            new EcsCommandCommitExecutor(new AppliedEcs()),
            new FixedRevision(next),
            new RejectingEvidence()).Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Fatal, result.Status);
        Assert.Equal("EvidenceMissing", result.Failure!.GeneratedErrorId);
        Assert.DoesNotContain(journal.Records, entry => entry.RecordKind == TxnJournalRecordRecordKind.Committed);
        Assert.Equal(current, revisions.Read());
    }

    [Fact]
    public void EvidenceIsDurableBeforeCommittedMarkerIsAppended()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView next = PrepareNoSideEffectTests.Vector(2UL);
        var evidence = new InMemoryTxnResultEvidencePort();
        var journal = new ObservingJournal(new InMemoryTxnJournalPort(16), () => Assert.Equal(1, evidence.Count));
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = PreparedRecord("txn-evidence-order");
        TxnCommitResult result = new CommitIntentCoordinator(
            revisions,
            journal,
            new AppliedVoxel(next),
            new EcsCommandCommitExecutor(new AppliedEcs()),
            new FixedRevision(next),
            evidence).Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.Equal(next, revisions.Read());
    }

    [Fact]
    public void EvidenceBeforeMarkerCrashCanRecoverWithoutReapplyingParticipants()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView next = PrepareNoSideEffectTests.Vector(2UL);
        var inner = new InMemoryTxnJournalPort(16);
        var journal = new FailingTerminalJournal(inner);
        var evidence = new InMemoryTxnResultEvidencePort();
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = PreparedRecord("txn-evidence-before-marker");
        TxnCommitResult first = new CommitIntentCoordinator(
            revisions,
            journal,
            new AppliedVoxel(next),
            new EcsCommandCommitExecutor(new AppliedEcs()),
            new FixedRevision(next),
            evidence).Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Indeterminate, first.Status);
        Assert.Equal(1, evidence.Count);
        Assert.DoesNotContain(inner.Records, entry => entry.RecordKind == TxnJournalRecordRecordKind.Committed);

        TxnRecoveryResult recovered = new TxnRecoveryResolver(inner, revisions, evidence)
            .Recover(record, new AppliedQuery(next));

        Assert.Equal(TxnCommitStatus.Committed, recovered.Status);
        Assert.Equal(next, revisions.Read());
        Assert.Contains(inner.Records, entry => entry.RecordKind == TxnJournalRecordRecordKind.Committed);
    }

    private static TxnRecord PreparedRecord(string txnId)
    {
        TxnRecord record = new("session", txnId, 2UL, "command", PrepareNoSideEffectTests.Vector(1UL), 10UL, "digest");
        record.AttachPreparedDelta(PrepareNoSideEffectTests.Prepared(2UL), "voxel-token");
        Assert.True(record.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        return record;
    }

    private static CrossWorldPreparedTxn PreparedTxn(TxnRecord record) =>
        new(record, new ReservationLease(string.Concat("game-", record.TxnId)),
            new PreparedVoxelTokenLease(string.Concat("voxel-", record.TxnId), record.DeadlineTick));

    private sealed class AppliedVoxel : IVoxelWorldPort
    {
        private readonly SessionRevisionVectorView _revision;
        internal AppliedVoxel(SessionRevisionVectorView revision) => _revision = revision;
        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) => VoxelPrepareResult.Prepared("token", request.DeadlineTick);
        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) => VoxelCommitParticipantResult.Applied(_revision);
        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);
        public VoxelParticipantQueryResult Query(string sessionId, string txnId) => new(TxnParticipantState.Applied, true, null, _revision);
        public SessionRevisionVectorView ReadRevision() => _revision;
    }

    private sealed class AppliedEcs : IEcsCommandCommitPort
    {
        public EcsCommandPortResult Apply(Lumio.GameRuntime.Command.Command command, string? resolvedEntityId) => EcsCommandPortResult.Applied();
    }

    private sealed class FixedRevision : IEcsCommandCommitRevisionPort
    {
        private readonly SessionRevisionVectorView _revision;
        internal FixedRevision(SessionRevisionVectorView revision) => _revision = revision;
        public SessionRevisionVectorView? ReadResultRevision(TxnRecord record, CommandApplyReceipt receipt) => _revision;
    }

    private sealed class RejectingEvidence : ITxnResultEvidencePort
    {
        public TxnResultEvidenceWriteResult Write(in TxnResultEvidence evidence) =>
            new(TxnResultEvidenceWriteStatus.Rejected, "EvidenceMissing");
        public TxnResultEvidenceReadResult Read(string sessionId, string txnId) =>
            new(TxnResultEvidenceReadStatus.NotFound, null, "EvidenceMissing");
    }

    private sealed class ObservingJournal : ITxnJournalPort
    {
        private readonly InMemoryTxnJournalPort _inner;
        private readonly Action _onCommitted;
        internal ObservingJournal(InMemoryTxnJournalPort inner, Action onCommitted)
        {
            _inner = inner;
            _onCommitted = onCommitted;
        }
        public TxnJournalAppendResult Append(in TxnJournalRecord record)
        {
            if (record.RecordKind == TxnJournalRecordRecordKind.Committed) _onCommitted();
            return _inner.Append(in record);
        }
        public TxnJournalQueryResult Query(string sessionId, string txnId) => _inner.Query(sessionId, txnId);
        public TxnJournalTailResult ReadTail() => _inner.ReadTail();
    }

    private sealed class FailingTerminalJournal : ITxnJournalPort
    {
        private readonly InMemoryTxnJournalPort _inner;
        private bool _fail = true;
        internal FailingTerminalJournal(InMemoryTxnJournalPort inner) => _inner = inner;
        public TxnJournalAppendResult Append(in TxnJournalRecord record)
        {
            if (record.RecordKind == TxnJournalRecordRecordKind.Committed && _fail)
            {
                _fail = false;
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "PanicBoundary");
            }
            return _inner.Append(in record);
        }
        public TxnJournalQueryResult Query(string sessionId, string txnId) => _inner.Query(sessionId, txnId);
        public TxnJournalTailResult ReadTail() => _inner.ReadTail();
    }

    private sealed class AppliedQuery : ITxnParticipantQueryPort
    {
        private readonly SessionRevisionVectorView _revision;
        internal AppliedQuery(SessionRevisionVectorView revision) => _revision = revision;
        public TxnParticipantQueryResult Query(string sessionId, string txnId, TxnParticipantKind participant) =>
            TxnParticipantQueryResult.Applied(_revision);
    }
}
