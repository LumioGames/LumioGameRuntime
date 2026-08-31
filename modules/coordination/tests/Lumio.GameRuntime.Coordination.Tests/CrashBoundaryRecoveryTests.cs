using System.Collections.Generic;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class CrashBoundaryRecoveryTests
{
    [Fact]
    public void RecoveryNeverGuessesUnavailableParticipant()
    {
        TxnRecord record = IntentRecord("txn-unknown");
        TxnRecoveryResult result = new TxnRecoveryResolver().Recover(record, new QueryPort(false));
        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Contains(TxnParticipantState.Unknown, new[] { result.VoxelParticipant, result.EcsParticipant });
    }

    [Fact]
    public void RecoveryConvergesOnlyAfterBothParticipantsProveApplied()
    {
        TxnRecord record = IntentRecord("txn-applied");
        TxnRecoveryResult result = new TxnRecoveryResolver().Recover(record, new QueryPort(true));
        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.Equal(CrossWorldTxnState.Committed, record.State);
    }

    [Fact]
    public void DurableCommittedMarkerConvergesRecordAndParticipantMarkers()
    {
        TxnRecord record = new("session", "txn-marker", 2UL, "command",
            PrepareNoSideEffectTests.Vector(1UL), 10UL, "digest");
        var journal = new InMemoryTxnJournalPort(4);
        TxnJournalRecord marker = TxnJournalRecordFactory.Create(
            "session", "runtime", 2UL, "txn-marker",
            TxnJournalRecordRecordKind.Committed, "txn-marker:committed",
            TxnJournalRecordCommitState.Committed,
            TxnJournalRecordDurabilityState.Durable,
            "command");
        journal.Append(in marker);

        TxnRecoveryResult result = new TxnRecoveryResolver(journal)
            .Recover(record, new QueryPort(false));

        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.Equal(CrossWorldTxnState.Committed, record.State);
        Assert.Equal(TxnParticipantState.Applied, record.VoxelParticipant);
        Assert.Equal(TxnParticipantState.Applied, record.EcsParticipant);
    }

    [Fact]
    public void FatalJournalQueryDoesNotGuessThatTransactionIsAbortable()
    {
        TxnRecord record = new("session", "txn-journal-fatal", 2UL, "command",
            PrepareNoSideEffectTests.Vector(1UL), 10UL, "digest");

        TxnRecoveryResult result = new TxnRecoveryResolver(new FatalJournal())
            .Recover(record, new QueryPort(false));

        Assert.Equal(TxnCommitStatus.Fatal, result.Status);
        Assert.Equal(CrossWorldTxnState.Created, record.State);
    }

    [Fact]
    public void RevisionAdvanceFailureLeavesRecoveryIndeterminate()
    {
        TxnRecord record = IntentRecord("txn-revision-conflict");
        SessionRevisionVectorView incompatible = new(2UL, 2UL, 2UL,
            new Dictionary<string, ulong>(), 2UL, 1UL, 2UL);
        var queries = new QueryPort(true, incompatible);
        var revisions = new SessionRevisionVectorStore(PrepareNoSideEffectTests.Vector(1UL));

        TxnRecoveryResult result = new TxnRecoveryResolver(revisions: revisions).Recover(record, queries);

        Assert.Equal(TxnCommitStatus.Fatal, result.Status);
        Assert.Equal(CrossWorldTxnState.Indeterminate, record.State);
        Assert.Equal(1UL, revisions.Read().SchemaEpoch);
    }

    private static TxnRecord IntentRecord(string txnId)
    {
        PreparedGameDelta delta = PrepareNoSideEffectTests.Prepared(2UL);
        var record = new TxnRecord("session", txnId, 2UL, "command", PrepareNoSideEffectTests.Vector(1UL), 10UL, "digest");
        record.AttachPreparedDelta(delta, "voxel-token");
        record.TryTransition(CrossWorldTxnState.Prepared);
        record.TryTransition(CrossWorldTxnState.CommitIntent);
        return record;
    }

    private sealed class QueryPort : ITxnParticipantQueryPort
    {
        private readonly bool _available;
        private readonly SessionRevisionVectorView? _revision;

        internal QueryPort(bool available, SessionRevisionVectorView? revision = null)
        {
            _available = available;
            _revision = revision;
        }
        public TxnParticipantQueryResult Query(string sessionId, string txnId, TxnParticipantKind participant) =>
            _available ? TxnParticipantQueryResult.Applied(_revision) : TxnParticipantQueryResult.Unknown();
    }

    private sealed class FatalJournal : ITxnJournalPort
    {
        public TxnJournalAppendResult Append(in TxnJournalRecord record) =>
            new(TxnJournalAppendStatus.Fatal, 0UL, false, "PanicBoundary");

        public TxnJournalQueryResult Query(string sessionId, string txnId) =>
            new(TxnJournalQueryStatus.Fatal, new List<TxnJournalRecord>(), "PanicBoundary");
    }
}
