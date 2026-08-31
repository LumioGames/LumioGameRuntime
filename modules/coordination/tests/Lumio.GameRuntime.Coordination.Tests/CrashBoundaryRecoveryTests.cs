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
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntent(journal, record);
        TxnRecoveryResult result = new TxnRecoveryResolver(
            journal,
            new SessionRevisionVectorStore(record.ExpectedRevision),
            new InMemoryTxnResultEvidencePort()).Recover(record, new QueryPort(false));
        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Contains(TxnParticipantState.Unknown, new[] { result.VoxelParticipant, result.EcsParticipant });
    }

    [Fact]
    public void RecoveryConvergesOnlyAfterBothParticipantsProveApplied()
    {
        TxnRecord record = IntentRecord("txn-applied");
        SessionRevisionVectorView revision = PrepareNoSideEffectTests.Vector(2UL);
        var revisions = new SessionRevisionVectorStore(PrepareNoSideEffectTests.Vector(1UL));
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntent(journal, record);
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence row = new(record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision, revision);
        Assert.True(evidence.Write(in row).IsDurable);
        TxnRecoveryResult result = new TxnRecoveryResolver(journal, revisions, evidence)
            .Recover(record, new QueryPort(true, revision));
        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.Equal(CrossWorldTxnState.Committed, record.State);
    }

    [Fact]
    public void DurableCommittedMarkerConvergesRecordAndParticipantMarkers()
    {
        TxnRecord record = new("session", "txn-marker", 2UL, "command",
            PrepareNoSideEffectTests.Vector(1UL), 10UL, "digest");
        var journal = new InMemoryTxnJournalPort(4);

        SessionRevisionVectorView revision = PrepareNoSideEffectTests.Vector(2UL);
        var revisions = new SessionRevisionVectorStore(PrepareNoSideEffectTests.Vector(1UL));
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence resultEvidence = new(
            record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision, revision);
        Assert.True(evidence.Write(in resultEvidence).IsDurable);
        TxnAuthorityTestData.AppendCommittedCertificate(journal, record, resultEvidence);
        TxnRecoveryResult result = new TxnRecoveryResolver(journal, revisions, evidence)
            .Recover(record, new QueryPort(true, revision));

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

        TxnRecoveryResult result = new TxnRecoveryResolver(
            new FatalJournal(),
            new SessionRevisionVectorStore(record.ExpectedRevision),
            new InMemoryTxnResultEvidencePort())
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
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntent(journal, record);

        TxnRecoveryResult result = new TxnRecoveryResolver(
            journal,
            revisions,
            new InMemoryTxnResultEvidencePort()).Recover(record, queries);

        Assert.Equal(TxnCommitStatus.Fatal, result.Status);
        Assert.Equal(CrossWorldTxnState.CommitIntent, record.State);
        Assert.Equal(1UL, revisions.Read().SchemaEpoch);
    }

    private static TxnRecord IntentRecord(string txnId)
    {
        PreparedGameDelta delta = PrepareNoSideEffectTests.Prepared(2UL);
        var record = new TxnRecord("session", txnId, 2UL, "command", PrepareNoSideEffectTests.Vector(1UL), 10UL, "digest");
        record.AttachPreparedDelta(delta, "voxel-token");
        record.TryTransition(CrossWorldTxnState.Prepared);
        TxnAuthorityTestData.MarkIntent(record);
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
