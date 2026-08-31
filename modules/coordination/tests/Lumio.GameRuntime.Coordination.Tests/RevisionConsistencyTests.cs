using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class RevisionConsistencyTests
{
    [Fact]
    public void NormalCommitWithMissingParticipantRevisionStaysIndeterminate()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        var revisions = new SessionRevisionVectorStore(current);
        var voxel = new RevisionVoxelPort(current, null);
        TxnRecord record = PreparedRecord("txn-missing-revision");
        var coordinator = new CommitIntentCoordinator(
            revisions,
            new InMemoryTxnJournalPort(),
            voxel,
            new EcsCommandCommitExecutor(new AppliedPort()));

        TxnCommitResult result = coordinator.Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Equal(CrossWorldTxnState.Indeterminate, record.State);
        Assert.Equal(current, revisions.Read());
        Assert.Null(record.ResultRevision);
    }

    [Fact]
    public void NormalCommitWithMissingEcsRevisionStaysIndeterminate()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView participantRevision = PrepareNoSideEffectTests.Vector(2UL);
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = PreparedRecord("txn-missing-ecs-revision");
        var coordinator = new CommitIntentCoordinator(
            revisions,
            new InMemoryTxnJournalPort(),
            new RevisionVoxelPort(current, participantRevision),
            new EcsCommandCommitExecutor(new AppliedPort()));

        TxnCommitResult result = coordinator.Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Equal(CrossWorldTxnState.Indeterminate, record.State);
        Assert.Equal(current, revisions.Read());
        Assert.Null(record.ResultRevision);
    }

    [Fact]
    public void RecoveryWithDisagreeingParticipantRevisionsStaysIndeterminate()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView voxelRevision = VectorAtTick(2UL, 2UL);
        SessionRevisionVectorView ecsRevision = VectorAtTick(3UL, 2UL);
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = IntentRecord("txn-revision-disagreement");
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntent(journal, record);

        TxnRecoveryResult result = new TxnRecoveryResolver(
                journal,
                revisions,
                new InMemoryTxnResultEvidencePort())
            .Recover(record, new QueryPort(voxelRevision, ecsRevision));

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Equal(CrossWorldTxnState.CommitIntent, record.State);
        Assert.Equal(current, revisions.Read());
        Assert.Null(record.ResultRevision);
        Assert.Equal("RevisionConflict", result.Failure!.GeneratedErrorId);
    }

    [Fact]
    public void NormalCommitWithDisagreeingParticipantRevisionsStaysIndeterminate()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView voxelRevision = VectorAtTick(2UL, 2UL);
        SessionRevisionVectorView ecsRevision = VectorAtTick(3UL, 2UL);
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = PreparedRecord("txn-normal-revision-disagreement");
        var coordinator = new CommitIntentCoordinator(
            revisions,
            new InMemoryTxnJournalPort(),
            new RevisionVoxelPort(current, voxelRevision),
            new EcsCommandCommitExecutor(new AppliedPort()),
            new FixedRevisionProvider(ecsRevision));

        TxnCommitResult result = coordinator.Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Equal(CrossWorldTxnState.Indeterminate, record.State);
        Assert.Equal(current, revisions.Read());
        Assert.Null(record.ResultRevision);
    }

    [Fact]
    public void FreshCommitWithStaleParticipantRevisionDoesNotWriteCommittedMarker()
    {
        SessionRevisionVectorView current = VectorAtTick(1UL, 2UL);
        var journal = new InMemoryTxnJournalPort();
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = PreparedRecord("txn-stale-participant-revision", current);
        var coordinator = new CommitIntentCoordinator(
            revisions,
            journal,
            new RevisionVoxelPort(current, current),
            new EcsCommandCommitExecutor(new AppliedPort()),
            new FixedRevisionProvider(current));

        TxnCommitResult result = coordinator.Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.DoesNotContain(journal.Records, entry =>
            entry.RecordKind == TxnJournalRecordRecordKind.Committed);
        Assert.Equal(current, revisions.Read());
    }

    [Fact]
    public void FreshCommitWithRegressingParticipantRevisionDoesNotWriteCommittedMarker()
    {
        SessionRevisionVectorView current = VectorAtTick(2UL, 2UL);
        SessionRevisionVectorView regressing = VectorAtTick(1UL, 2UL);
        var journal = new InMemoryTxnJournalPort();
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = PreparedRecord("txn-regressing-participant", current);
        var coordinator = new CommitIntentCoordinator(
            revisions,
            journal,
            new RevisionVoxelPort(current, regressing),
            new EcsCommandCommitExecutor(new AppliedPort()),
            new FixedRevisionProvider(regressing));

        TxnCommitResult result = coordinator.Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.DoesNotContain(journal.Records, entry => entry.RecordKind == TxnJournalRecordRecordKind.Committed);
        Assert.Equal(current, revisions.Read());
    }

    [Fact]
    public void FreshCommitWithWrongSchemaParticipantRevisionDoesNotWriteCommittedMarker()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView wrongSchema = new(2UL, 2UL, 2UL,
            new Dictionary<string, ulong>(), 2UL, 1UL, 99UL);
        var journal = new InMemoryTxnJournalPort();
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = PreparedRecord("txn-schema-participant");
        var coordinator = new CommitIntentCoordinator(
            revisions,
            journal,
            new RevisionVoxelPort(current, wrongSchema),
            new EcsCommandCommitExecutor(new AppliedPort()),
            new FixedRevisionProvider(wrongSchema));

        TxnCommitResult result = coordinator.Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Fatal, result.Status);
        Assert.DoesNotContain(journal.Records, entry => entry.RecordKind == TxnJournalRecordRecordKind.Committed);
        Assert.Equal(current, revisions.Read());
    }

    [Fact]
    public void CommittedMarkerRecoveryRestoresRevisionAndReplayIsIdempotent()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView committed = PrepareNoSideEffectTests.Vector(2UL);
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecord record = new(
            "session", "txn-marker-revision", 2UL, "command", current, 10UL, "digest");
        var journal = new InMemoryTxnJournalPort(4);
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence markerEvidence = new(
            record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision, committed);
        Assert.True(evidence.Write(in markerEvidence).IsDurable);
        TxnAuthorityTestData.AppendCommittedCertificate(journal, record, markerEvidence);
        var queries = new QueryPort(committed, committed);
        var resolver = new TxnRecoveryResolver(journal, revisions, evidence);

        TxnRecoveryResult first = resolver.Recover(record, queries);
        SessionRevisionVectorView afterFirst = revisions.Read();
        TxnRecoveryResult replay = resolver.Recover(record, queries);

        Assert.Equal(TxnCommitStatus.Committed, first.Status);
        Assert.Equal(committed, first.ResultRevision);
        Assert.Equal(committed, record.ResultRevision);
        Assert.Equal(committed, afterFirst);
        Assert.Equal(TxnCommitStatus.AlreadyCommitted, replay.Status);
        Assert.Equal(afterFirst, revisions.Read());
    }

    [Fact]
    public void CommittedRecordRecoveryRestoresRevisionWhenStoreWasRebuilt()
    {
        SessionRevisionVectorView expected = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView committed = PrepareNoSideEffectTests.Vector(2UL);
        TxnRecord record = PreparedRecord("txn-committed-store-rebuild");
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence recordEvidence = new(
            record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision, committed);
        Assert.True(evidence.Write(in recordEvidence).IsDurable);
        var journal = new InMemoryTxnJournalPort(4);
        TxnAuthorityTestData.AppendCommittedCertificate(journal, record, recordEvidence);
        TxnRecoveryResult published = new TxnRecoveryResolver(
                journal,
                new SessionRevisionVectorStore(expected),
                evidence)
            .Recover(record, new QueryPort(committed, committed));
        Assert.Equal(TxnCommitStatus.Committed, published.Status);

        var revisions = new SessionRevisionVectorStore(expected);
        TxnRecoveryResult result = new TxnRecoveryResolver(journal, revisions, evidence)
            .Recover(record, new QueryPort(committed, committed));

        Assert.Equal(TxnCommitStatus.AlreadyCommitted, result.Status);
        Assert.Equal(committed, revisions.Read());
    }

    [Fact]
    public void PreparedRecordRecoversFromDurableMarker()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView committed = PrepareNoSideEffectTests.Vector(2UL);
        var revisions = new SessionRevisionVectorStore(current);
        var journal = new InMemoryTxnJournalPort(4);
        TxnRecord record = PreparedRecord("txn-committed-no-revision");
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence noRevisionEvidence = new(
            record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision, committed);
        Assert.True(evidence.Write(in noRevisionEvidence).IsDurable);
        TxnAuthorityTestData.AppendCommittedCertificate(journal, record, noRevisionEvidence);

        TxnRecoveryResult result = new TxnRecoveryResolver(journal, revisions, evidence)
            .Recover(record, new QueryPort(committed, committed));

        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.Equal(committed, record.ResultRevision);
        Assert.Equal(committed, revisions.Read());
    }

    [Fact]
    public void IntentRecoveryWithStaleEqualParticipantRevisionDoesNotCommit()
    {
        SessionRevisionVectorView current = VectorAtTick(1UL, 2UL);
        TxnRecord record = PreparedRecord("txn-stale-recovery", current);
        TxnAuthorityTestData.MarkIntent(record);
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntent(journal, record);
        var revisions = new SessionRevisionVectorStore(current);
        TxnRecoveryResult result = new TxnRecoveryResolver(
                journal,
                revisions,
                new InMemoryTxnResultEvidencePort())
            .Recover(record, new QueryPort(current, current));

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Equal("RevisionConflict", result.Failure!.GeneratedErrorId);
        Assert.DoesNotContain(journal.Records, entry => entry.RecordKind == TxnJournalRecordRecordKind.Committed);
        Assert.Equal(current, revisions.Read());
        Assert.Null(record.ResultRevision);
    }

    [Fact]
    public void IntentRecoveryWithRegressingParticipantRevisionDoesNotCommit()
    {
        SessionRevisionVectorView current = VectorAtTick(2UL, 2UL);
        SessionRevisionVectorView regressing = VectorAtTick(1UL, 2UL);
        TxnRecord record = new TxnRecord("session", "txn-regressing-recovery", 2UL, "command", current, 10UL, "digest");
        record.AttachPreparedDelta(PrepareNoSideEffectTests.Prepared(2UL), "voxel-token");
        record.TryTransition(CrossWorldTxnState.Prepared);
        TxnAuthorityTestData.MarkIntent(record);
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntent(journal, record);
        TxnRecoveryResult result = new TxnRecoveryResolver(
                journal,
                new SessionRevisionVectorStore(current),
                new InMemoryTxnResultEvidencePort())
            .Recover(record, new QueryPort(regressing, regressing));

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Equal("RevisionConflict", result.Failure!.GeneratedErrorId);
        Assert.DoesNotContain(journal.Records, entry => entry.RecordKind == TxnJournalRecordRecordKind.Committed);
    }

    [Fact]
    public void IntentRecoveryWithWrongSchemaDoesNotCommit()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView wrongSchema = new(2UL, 2UL, 2UL,
            new Dictionary<string, ulong>(), 2UL, 1UL, 99UL);
        TxnRecord record = IntentRecord("txn-schema-recovery");
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntent(journal, record);
        TxnRecoveryResult result = new TxnRecoveryResolver(
                journal,
                new SessionRevisionVectorStore(current),
                new InMemoryTxnResultEvidencePort())
            .Recover(record, new QueryPort(wrongSchema, wrongSchema));

        Assert.Equal(TxnCommitStatus.Fatal, result.Status);
        Assert.Equal("InternalInvariant", result.Failure!.GeneratedErrorId);
        Assert.DoesNotContain(journal.Records, entry => entry.RecordKind == TxnJournalRecordRecordKind.Committed);
    }

    [Fact]
    public void CommittedMarkerRecoveryUsesEvidenceWhenParticipantQueriesAreUnavailable()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView committed = PrepareNoSideEffectTests.Vector(2UL);
        TxnRecord record = new("session", "txn-marker-no-query", 2UL, "command", current, 10UL, "digest");
        var journal = new InMemoryTxnJournalPort();
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence row = new(record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision, committed);
        Assert.True(evidence.Write(in row).IsDurable);
        TxnAuthorityTestData.AppendCommittedCertificate(journal, record, row);

        TxnRecoveryResult result = new TxnRecoveryResolver(journal,
            new SessionRevisionVectorStore(current), evidence)
            .Recover(record, new ThrowingQueryPort());

        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.Equal(committed, result.ResultRevision);
        Assert.Equal(committed, record.ResultRevision);
    }

    [Fact]
    public void CommittedMarkerRecoveryDoesNotRequireAQueryCapability()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView committed = PrepareNoSideEffectTests.Vector(2UL);
        TxnRecord record = new("session", "txn-marker-null-query", 2UL, "command", current, 10UL, "digest");
        var journal = new InMemoryTxnJournalPort();
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence row = new(record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision, committed);
        Assert.True(evidence.Write(in row).IsDurable);
        TxnAuthorityTestData.AppendCommittedCertificate(journal, record, row);

        TxnRecoveryResult result = new TxnRecoveryResolver(journal,
            new SessionRevisionVectorStore(current), evidence).Recover(record, null);

        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.Equal(committed, result.ResultRevision);
    }

    [Fact]
    public void CommittedMarkerWithoutEvidenceFailsClosed()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView committed = PrepareNoSideEffectTests.Vector(2UL);
        TxnRecord record = new("session", "txn-marker-missing-evidence", 2UL, "command", current, 10UL, "digest");
        var journal = new InMemoryTxnJournalPort();
        TxnResultEvidence missing = new(record, committed);
        TxnAuthorityTestData.AppendCommittedCertificate(journal, record, missing);

        TxnRecoveryResult result = new TxnRecoveryResolver(journal,
            new SessionRevisionVectorStore(current), new InMemoryTxnResultEvidencePort())
            .Recover(record, new ThrowingQueryPort());

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
        Assert.Equal("EvidenceMissing", result.Failure!.GeneratedErrorId);
        Assert.Equal(CrossWorldTxnState.Created, record.State);
    }

    [Fact]
    public void RevisionReservationBlocksACompetingAdvanceUntilReleased()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView next = PrepareNoSideEffectTests.Vector(2UL);
        var store = new SessionRevisionVectorStore(current);
        SessionCoordinationContext context = SessionCoordinationContext.For(store);
        Assert.True(context.TryEnter(Identity("txn-a", current), out TxnAuthorityOperation firstOperation, out _));
        RevisionReservationResult first = store.TryReserveStrict(current, next, firstOperation);
        Assert.True(first.Succeeded);

        Assert.False(context.TryEnter(Identity("txn-b", current), out _, out CoordinationFailure? competing));
        Assert.Equal(CoordinationFailureClass.Retryable, competing!.Class);
        Assert.Equal(current, store.Read());

        Assert.True(first.Reservation!.Commit().Succeeded);
        firstOperation.Dispose();
        Assert.Equal(next, store.Read());
    }

    [Fact]
    public void EqualityIsRejectedForANewTransactionAdvance()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        var store = new SessionRevisionVectorStore(current);
        SessionCoordinationContext context = SessionCoordinationContext.For(store);
        Assert.True(context.TryEnter(Identity("txn-equal", current), out TxnAuthorityOperation operation, out _));
        RevisionReservationResult result = store.TryReserveStrict(current, current, operation);
        operation.Dispose();

        Assert.False(result.Succeeded);
        Assert.Equal("RevisionConflict", result.Failure!.GeneratedErrorId);
    }

    [Fact]
    public void RevisionReservationSurvivesTerminalMarkerInterleaving()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView next = PrepareNoSideEffectTests.Vector(2UL);
        var revisions = new SessionRevisionVectorStore(current);
        var inner = new InMemoryTxnJournalPort();
        bool? interleaved = null;
        SessionCoordinationContext context = SessionCoordinationContext.For(revisions);
        var journal = new InterleavingJournal(inner, () =>
        {
            interleaved = context.TryEnter(Identity("txn-interleaved", current), out TxnAuthorityOperation nested, out _);
            if (interleaved.Value) nested.Dispose();
        });
        TxnRecord record = PreparedRecord("txn-marker-interleave");
        var coordinator = new CommitIntentCoordinator(
            revisions,
            journal,
            new RevisionVoxelPort(current, next),
            new EcsCommandCommitExecutor(new AppliedPort()),
            new FixedRevisionProvider(next),
            new InMemoryTxnResultEvidencePort());

        TxnCommitResult result = coordinator.Commit(PreparedTxn(record));

        Assert.Equal(TxnCommitStatus.Committed, result.Status);
        Assert.NotNull(interleaved);
        Assert.False(interleaved!.Value);
        Assert.Equal(next, revisions.Read());
    }

    [Fact]
    public void ReplayOfOlderDurableCommitIsIdempotentAfterLaterAdvance()
    {
        SessionRevisionVectorView expected = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView result = PrepareNoSideEffectTests.Vector(2UL);
        SessionRevisionVectorView later = PrepareNoSideEffectTests.Vector(3UL);
        TxnRecord record = PreparedRecord("txn-older-replay");
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence row = new(record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, expected, result);
        Assert.True(evidence.Write(in row).IsDurable);
        var journal = new InMemoryTxnJournalPort(4);
        TxnAuthorityTestData.AppendCommittedCertificate(journal, record, row);
        TxnRecoveryResult published = new TxnRecoveryResolver(
                journal,
                new SessionRevisionVectorStore(expected),
                evidence)
            .Recover(record, new ThrowingQueryPort());
        Assert.Equal(TxnCommitStatus.Committed, published.Status);

        var revisions = new SessionRevisionVectorStore(later);
        TxnRecoveryResult replay = new TxnRecoveryResolver(journal, revisions, evidence)
            .Recover(record, new ThrowingQueryPort());

        Assert.Equal(TxnCommitStatus.AlreadyCommitted, replay.Status);
        Assert.Equal(later, revisions.Read());
    }

    private static TxnRecord PreparedRecord(string txnId)
        => PreparedRecord(txnId, PrepareNoSideEffectTests.Vector(1UL));

    private static TxnRecord PreparedRecord(string txnId, SessionRevisionVectorView expected)
    {
        PreparedGameDelta delta = PrepareNoSideEffectTests.Prepared(2UL);
        var record = new TxnRecord("session", txnId, 2UL, "command", expected, 10UL, "digest");
        record.AttachPreparedDelta(delta, "voxel-token");
        record.TryTransition(CrossWorldTxnState.Prepared);
        return record;
    }

    private static TxnRecord IntentRecord(string txnId)
    {
        TxnRecord record = PreparedRecord(txnId);
        TxnAuthorityTestData.MarkIntent(record);
        return record;
    }

    private static TxnIdentity Identity(string txnId, SessionRevisionVectorView expected) =>
        new("session", "runtime", txnId, "command", 2UL, "digest", expected.CanonicalDigestHex);

    private static SessionRevisionVectorView VectorAtTick(ulong revision, ulong tick) =>
        new(tick, revision, revision, new Dictionary<string, ulong>(), revision, 1UL, 1UL);

    private static CrossWorldPreparedTxn PreparedTxn(TxnRecord record) =>
        new(record, new ReservationLease(string.Concat("game-", record.TxnId)),
            new PreparedVoxelTokenLease(string.Concat("voxel-", record.TxnId), record.DeadlineTick));

    private sealed class RevisionVoxelPort : IVoxelWorldPort
    {
        private readonly SessionRevisionVectorView _revision;
        private readonly SessionRevisionVectorView? _result;

        internal RevisionVoxelPort(SessionRevisionVectorView revision, SessionRevisionVectorView? result)
        {
            _revision = revision;
            _result = result;
        }

        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) =>
            VoxelPrepareResult.Prepared("voxel-token", request.DeadlineTick);

        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) =>
            VoxelCommitParticipantResult.Applied(_result);

        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);

        public VoxelParticipantQueryResult Query(string sessionId, string txnId) =>
            new(TxnParticipantState.Applied, true, null, _result);

        public SessionRevisionVectorView ReadRevision() => _revision;
    }

    private sealed class AppliedPort : IEcsCommandCommitPort
    {
        public EcsCommandPortResult Apply(Lumio.GameRuntime.Command.Command command, string? resolvedEntityId) => EcsCommandPortResult.Applied();
    }

    private sealed class QueryPort : ITxnParticipantQueryPort
    {
        private readonly SessionRevisionVectorView _voxel;
        private readonly SessionRevisionVectorView _ecs;

        internal QueryPort(SessionRevisionVectorView voxel, SessionRevisionVectorView ecs)
        {
            _voxel = voxel;
            _ecs = ecs;
        }

        public TxnParticipantQueryResult Query(string sessionId, string txnId, TxnParticipantKind participant) =>
            participant == TxnParticipantKind.VoxelCommit
                ? TxnParticipantQueryResult.Applied(_voxel)
                : TxnParticipantQueryResult.Applied(_ecs);
    }

    private sealed class FixedRevisionProvider : IEcsCommandCommitRevisionPort
    {
        private readonly SessionRevisionVectorView _revision;
        internal FixedRevisionProvider(SessionRevisionVectorView revision) => _revision = revision;
        public SessionRevisionVectorView? ReadResultRevision(TxnRecord record, CommandApplyReceipt receipt) => _revision;
    }

    private sealed class ThrowingQueryPort : ITxnParticipantQueryPort
    {
        public TxnParticipantQueryResult Query(string sessionId, string txnId, TxnParticipantKind participant) =>
            throw new InvalidOperationException("participant query must not be used for marker recovery");
    }

    private sealed class InterleavingJournal : ITxnJournalPort
    {
        private readonly InMemoryTxnJournalPort _inner;
        private readonly Action _onCommitted;

        internal InterleavingJournal(InMemoryTxnJournalPort inner, Action onCommitted)
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
}
