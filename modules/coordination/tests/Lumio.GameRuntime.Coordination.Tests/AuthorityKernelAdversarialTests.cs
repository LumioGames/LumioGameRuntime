using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class AuthorityKernelAdversarialTests
{
    [Fact]
    public void RecoveryRequiresExactDurableIntentInsteadOfLocalState()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = IntentRecord("txn-no-journal", 2UL, expected);
        var revisions = new SessionRevisionVectorStore(expected);
        var evidence = Evidence(record, result);
        var queries = new CountingQueryPort(result);

        TxnRecoveryResult recovered = new TxnRecoveryResolver(revisions: revisions, evidence: evidence)
            .Recover(record, queries);

        Assert.False(recovered.Succeeded);
        Assert.Equal(expected, revisions.Read());
        Assert.Equal(0, queries.Calls);
        Assert.Null(record.ResultRevision);
    }

    [Fact]
    public void EmptyJournalDoesNotUpgradeLocalIntentOrCallParticipants()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = IntentRecord("txn-no-intent-marker", 2UL, expected);
        var revisions = new SessionRevisionVectorStore(expected);
        var journal = new InMemoryTxnJournalPort();
        var queries = new CountingQueryPort(result);

        TxnRecoveryResult recovered = new TxnRecoveryResolver(journal, revisions, Evidence(record, result))
            .Recover(record, queries);

        Assert.False(recovered.Succeeded);
        Assert.Equal(expected, revisions.Read());
        Assert.Equal(0, queries.Calls);
        Assert.Empty(journal.Records);
    }

    [Fact]
    public void PublicSurfaceCannotFabricateTransactionOrRevisionAuthority()
    {
        string[] recordMethods = PublicDeclaredMethods(typeof(TxnRecord));
        string[] coordinatorMethods = PublicDeclaredMethods(typeof(CrossWorldCoordinator));
        string[] revisionMethods = PublicDeclaredMethods(typeof(SessionRevisionVectorStore));

        Assert.DoesNotContain("TryTransition", recordMethods);
        Assert.DoesNotContain("Transition", recordMethods);
        Assert.DoesNotContain("MarkCommitIntentPersisted", recordMethods);
        Assert.DoesNotContain("MarkParticipant", recordMethods);
        Assert.DoesNotContain("MarkPrepared", coordinatorMethods);
        Assert.DoesNotContain("MarkCommitIntent", coordinatorMethods);
        Assert.DoesNotContain("MarkCommitted", coordinatorMethods);
        Assert.DoesNotContain("Transition", coordinatorMethods);
        Assert.DoesNotContain("StopAccepting", coordinatorMethods);
        Assert.DoesNotContain("ResumeAccepting", coordinatorMethods);
        Assert.DoesNotContain("TryAdvance", revisionMethods);
        Assert.DoesNotContain("Advance", revisionMethods);
        Assert.DoesNotContain("AdvanceCommitted", revisionMethods);
        Assert.DoesNotContain("TryReserveStrict", revisionMethods);
        Assert.DoesNotContain("ReserveStrict", revisionMethods);
        Assert.DoesNotContain("RestoreCommitted", revisionMethods);
        Assert.DoesNotContain("Dispose", revisionMethods);
        Assert.Null(typeof(CrossWorldPreparedTxn).GetProperty(
            "GameReservation",
            BindingFlags.Public | BindingFlags.Instance));
    }

    [Theory]
    [InlineData("released")]
    [InlineData("expired")]
    [InlineData("disposed")]
    public void InactivePreparedLeaseCannotAppendIntentOrApply(string mode)
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = PreparedRecord(string.Concat("txn-inactive-", mode), 2UL, expected);
        var game = new ReservationLease("game", 10UL);
        var voxelLease = new PreparedVoxelTokenLease("voxel-token", 10UL);
        var prepared = new CrossWorldPreparedTxn(record, game, voxelLease);
        switch (mode)
        {
            case "released":
                game.Release();
                break;
            case "expired":
                game.Expire();
                break;
            case "disposed":
                prepared.Dispose();
                break;
        }

        var journal = new InMemoryTxnJournalPort();
        var voxel = new CountingVoxelPort(result);
        var ecs = new CountingEcsPort();
        TxnCommitResult committed = Coordinator(expected, result, journal, voxel, ecs, Evidence(record, result))
            .Commit(prepared);

        Assert.False(committed.Succeeded);
        Assert.Empty(journal.Records);
        Assert.Equal(0, voxel.CommitCalls);
        Assert.Equal(0, ecs.Calls);
    }

    [Fact]
    public void AbortedIntentAndPrefixTerminalMarkerAreNotCommitAuthority()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = PreparedRecord("txn-aborted-intent", 2UL, expected);
        var journal = new InMemoryTxnJournalPort();
        Append(journal, record, TxnJournalRecordRecordKind.CommitIntent, "txn-aborted-intent:commit-intent",
            TxnJournalRecordCommitState.Aborted, "aborted-intent");
        Append(journal, record, TxnJournalRecordRecordKind.Committed, "txn-aborted-intent:committed:stale",
            TxnJournalRecordCommitState.Committed, "wrong-terminal-payload");
        var revisions = new SessionRevisionVectorStore(expected);

        TxnRecoveryResult recovered = new TxnRecoveryResolver(journal, revisions, Evidence(record, result))
            .Recover(record, new CountingQueryPort(result));

        Assert.False(recovered.Succeeded);
        Assert.Equal(expected, revisions.Read());
        Assert.Equal(CrossWorldTxnState.Prepared, record.State);
    }

    [Fact]
    public void ResultRevisionTickMustEqualTransactionTick()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView wrongTick = Vector(99UL, 2UL);
        TxnRecord record = PreparedRecord("txn-wrong-tick", 2UL, expected);
        var journal = new InMemoryTxnJournalPort();
        var evidence = new InMemoryTxnResultEvidencePort();
        var revisions = new SessionRevisionVectorStore(expected);

        TxnCommitResult committed = Coordinator(expected, wrongTick, journal,
            new CountingVoxelPort(wrongTick), new CountingEcsPort(), evidence)
            .Commit(new CrossWorldPreparedTxn(record, new ReservationLease("game"),
                new PreparedVoxelTokenLease("voxel-token", record.DeadlineTick)));

        Assert.False(committed.Succeeded);
        Assert.Equal(expected, revisions.Read());
        Assert.DoesNotContain(journal.Records, row => row.RecordKind == TxnJournalRecordRecordKind.Committed);
        Assert.False(evidence.Read(EvidenceIdentity(record)).IsFound);
    }

    [Fact]
    public void EvidenceConstructorRejectsResultFromAnotherTick()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView wrongTick = Vector(99UL, 2UL);

        Assert.Throws<ArgumentException>(() => new TxnResultEvidence(
            "session", "txn-evidence-tick", "command", 2UL, "digest", expected, wrongTick));
    }

    [Fact]
    public void RecoveryValidatesFailedParticipantBeforeRevisionRestore()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = IntentRecord("txn-failed-marker", 2UL, expected);
        Assert.True(record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Failed).Succeeded);
        var journal = new InMemoryTxnJournalPort();
        Append(journal, record, TxnJournalRecordRecordKind.Committed, "txn-failed-marker:committed",
            TxnJournalRecordCommitState.Committed, "terminal");
        var revisions = new SessionRevisionVectorStore(expected);

        TxnRecoveryResult recovered = new TxnRecoveryResolver(journal, revisions, Evidence(record, result))
            .Recover(record, new CountingQueryPort(result));

        Assert.False(recovered.Succeeded);
        Assert.Equal(expected, revisions.Read());
        Assert.Null(record.ResultRevision);
        Assert.Equal(TxnParticipantState.Failed, record.VoxelParticipant);
    }

    [Fact]
    public void ReentrantJournalCallbackCannotRunNestedCommit()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = PreparedRecord("txn-reentrant", 2UL, expected);
        var inner = new InMemoryTxnJournalPort();
        var journal = new ReentrantJournalPort(inner);
        var voxel = new CountingVoxelPort(result);
        var ecs = new CountingEcsPort();
        CommitIntentCoordinator coordinator = Coordinator(expected, result, journal, voxel, ecs, Evidence(record, result));
        var prepared = new CrossWorldPreparedTxn(
            record,
            new ReservationLease("game"),
            new PreparedVoxelTokenLease("voxel-token", record.DeadlineTick));
        journal.Callback = () => coordinator.Commit(prepared);

        TxnCommitResult outer = coordinator.Commit(prepared);

        Assert.NotNull(journal.Nested);
        Assert.False(journal.Nested!.Value.Succeeded);
        Assert.True(voxel.CommitCalls <= 1);
        Assert.True(ecs.Calls <= 1);
        Assert.False(outer.Status == TxnCommitStatus.Fatal && record.State == CrossWorldTxnState.Committed);
    }

    [Fact]
    public void ReleaseCallbackFailureStillReleasesBothLeases()
    {
        int gameReleases = 0;
        int voxelReleases = 0;
        TxnRecord record = PreparedRecord("txn-release-containment", 2UL, Vector(1UL, 1UL));
        var prepared = new CrossWorldPreparedTxn(
            record,
            new ReservationLease("game", () => gameReleases++),
            new PreparedVoxelTokenLease("voxel", 10UL, () =>
            {
                voxelReleases++;
                throw new InvalidOperationException("release failed");
            }));

        Exception? failure = Record.Exception(prepared.Dispose);

        Assert.Null(failure);
        Assert.Equal(1, voxelReleases);
        Assert.Equal(1, gameReleases);
        Assert.Equal(ReservationLeaseState.Released, prepared.GameReservation.State);
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("expire")]
    [InlineData("dispose")]
    public void DuplicatePrepareNeverReturnsTerminalOrReleasedCapability(string terminalAction)
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        var coordinator = new TxnPrepareCoordinator(
            new SessionRevisionVectorStore(expected),
            new CrossWorldCoordinator(),
            new SuccessfulGamePort(),
            new CountingVoxelPort(Vector(2UL, 2UL)));
        TxnPrepareRequest request = PrepareRequest(string.Concat("txn-duplicate-", terminalAction), 2UL, expected);
        TxnPrepareResult first = coordinator.Prepare(request);
        Assert.True(first.IsPrepared);
        switch (terminalAction)
        {
            case "abort":
                first.Prepared!.Abort();
                break;
            case "expire":
                first.Prepared!.Expire();
                break;
            case "dispose":
                first.Prepared!.Dispose();
                break;
        }

        TxnPrepareResult duplicate = coordinator.Prepare(request);

        Assert.False(duplicate.IsPrepared);
        Assert.Null(duplicate.Prepared);
    }

    [Fact]
    public void JournalTailIsPublicAndAllowsFreshCoordinatorToContinueChain()
    {
        Assert.NotNull(typeof(ITxnJournalPort).GetMethod("ReadTail", BindingFlags.Public | BindingFlags.Instance));

        SessionRevisionVectorView firstExpected = Vector(1UL, 1UL);
        SessionRevisionVectorView firstResult = Vector(2UL, 2UL);
        SessionRevisionVectorView secondResult = Vector(3UL, 3UL);
        var revisions = new SessionRevisionVectorStore(firstExpected);
        var inner = new InMemoryTxnJournalPort();
        ITxnJournalPort publicOnly = new PublicOnlyJournalPort(inner);
        TxnRecord first = PreparedRecord("txn-tail-one", 2UL, firstExpected);
        TxnRecord second = PreparedRecord("txn-tail-two", 3UL, firstResult);

        TxnCommitResult firstCommit = Coordinator(revisions, firstResult, publicOnly, Evidence(first, firstResult))
            .Commit(new CrossWorldPreparedTxn(first, new ReservationLease("game-one"),
                new PreparedVoxelTokenLease("voxel-one", first.DeadlineTick)));
        TxnCommitResult secondCommit = Coordinator(revisions, secondResult, publicOnly, Evidence(second, secondResult))
            .Commit(new CrossWorldPreparedTxn(second, new ReservationLease("game-two"),
                new PreparedVoxelTokenLease("voxel-two", second.DeadlineTick)));

        Assert.True(firstCommit.Succeeded);
        Assert.True(secondCommit.Succeeded);
        Assert.False(inner.IsFatal);
        Assert.Equal(8, inner.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(value => (ulong)value), inner.Records.Select(row => row.RecordSeq));
    }

    [Fact]
    public void DelimiterCollisionCannotShareRevisionReservation()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        string firstTupleKey = string.Concat("a:b", ":", "c", ":", "d");
        string secondTupleKey = string.Concat("a", ":", "b:c", ":", "d");
        Assert.Equal(firstTupleKey, secondTupleKey);

        var first = new TxnIdentity("a:b", "runtime", "c", "command", 2UL, "d", expected.CanonicalDigestHex);
        var second = new TxnIdentity("a", "runtime", "b:c", "command", 2UL, "d", expected.CanonicalDigestHex);

        Assert.NotEqual(first.DigestHex, second.DigestHex);
        Assert.False(first.CanonicalBytes.Span.SequenceEqual(second.CanonicalBytes.Span));
    }

    [Fact]
    public void ConvenienceCommitConstructorFailsClosedWithoutExplicitEvidence()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = PreparedRecord("txn-no-evidence-default", 2UL, expected);
        var journal = new InMemoryTxnJournalPort();
        var coordinator = new CommitIntentCoordinator(
            new SessionRevisionVectorStore(expected),
            journal,
            new CountingVoxelPort(result),
            new EcsCommandCommitExecutor(new CountingEcsPort()),
            new FixedRevisionPort(result));

        TxnCommitResult committed = coordinator.Commit(new CrossWorldPreparedTxn(
            record,
            new ReservationLease("game"),
            new PreparedVoxelTokenLease("voxel-token", record.DeadlineTick)));

        Assert.False(committed.Succeeded);
        Assert.DoesNotContain(journal.Records, row => row.RecordKind == TxnJournalRecordRecordKind.Committed);
    }

    [Fact]
    public void PrefixTerminalBesideExactIntentCannotTriggerParticipantRecovery()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = IntentRecord("txn-prefix-terminal", 2UL, expected);
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntent(journal, record);
        TxnJournalTailResult tail = journal.ReadTail();
        TxnJournalRecord prefix = TxnJournalRecordFactory.Create(
            record.SessionId,
            record.GameReleaseId,
            record.TickId,
            record.TxnId,
            TxnJournalRecordRecordKind.Committed,
            string.Concat(TxnJournalAuthority.Key(TxnIdentity.From(record), TxnJournalStage.Committed), ".stale"),
            TxnJournalRecordCommitState.Committed,
            TxnJournalRecordDurabilityState.Durable,
            record.CommandId,
            tail.RecordSequence + 1UL,
            tail.Checksum,
            System.Text.Encoding.UTF8.GetBytes("stale-terminal"));
        Assert.True(journal.Append(in prefix).IsDurable);
        var queries = new CountingQueryPort(result);
        var revisions = new SessionRevisionVectorStore(expected);

        TxnRecoveryResult recovered = new TxnRecoveryResolver(journal, revisions, Evidence(record, result))
            .Recover(record, queries);

        Assert.False(recovered.Succeeded);
        Assert.Equal(0, queries.Calls);
        Assert.Equal(expected, revisions.Read());
        Assert.Equal(CrossWorldTxnState.CommitIntent, record.State);
    }

    [Fact]
    public void RetryAfterTerminalAppendFailureCompletesWithoutReapplyingParticipants()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = PreparedRecord("txn-terminal-retry", 2UL, expected);
        var inner = new InMemoryTxnJournalPort();
        var journal = new FailOnceTerminalJournal(inner);
        var voxel = new CountingVoxelPort(result);
        var ecs = new CountingEcsPort();
        CommitIntentCoordinator coordinator = Coordinator(expected, result, journal, voxel, ecs, Evidence(record, result));
        var prepared = new CrossWorldPreparedTxn(
            record,
            new ReservationLease("game"),
            new PreparedVoxelTokenLease("voxel-token", record.DeadlineTick));

        TxnCommitResult first = coordinator.Commit(prepared);
        TxnCommitResult retry = coordinator.Commit(prepared);

        Assert.Equal(TxnCommitStatus.Indeterminate, first.Status);
        Assert.True(retry.Succeeded);
        Assert.Equal(1, voxel.CommitCalls);
        Assert.Equal(1, ecs.Calls);
        Assert.Equal(result, retry.ResultRevision);
    }

    [Fact]
    public void DuplicateIndexComparesFullIdentityNotOnlyRequestDigest()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        var first = new TxnRecord("session", "txn-full-identity", 2UL, "command-a", expected, 10UL, "same-digest");
        var second = new TxnRecord("session", "txn-full-identity", 2UL, "command-b", expected, 10UL, "same-digest");
        var index = new TxnIdempotencyIndex();

        Assert.Equal(TxnLookupStatus.New, index.Register(first).Status);
        TxnLookupResult duplicate = index.Register(second);

        Assert.Equal(TxnLookupStatus.Conflict, duplicate.Status);
        Assert.Equal("InvalidArgument", duplicate.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void AbortReportsContainedReleaseCallbackFailure()
    {
        int gameReleases = 0;
        int voxelReleases = 0;
        TxnRecord record = PreparedRecord("txn-abort-release-failure", 2UL, Vector(1UL, 1UL));
        var prepared = new CrossWorldPreparedTxn(
            record,
            new ReservationLease("game", () => gameReleases++),
            new PreparedVoxelTokenLease("voxel", 10UL, () =>
            {
                voxelReleases++;
                throw new InvalidOperationException("release failed");
            }));

        TxnTransitionResult aborted = prepared.Abort();

        Assert.False(aborted.Succeeded);
        Assert.Equal("PanicBoundary", aborted.Failure?.GeneratedErrorId);
        Assert.Equal(1, gameReleases);
        Assert.Equal(1, voxelReleases);
        Assert.Equal(CrossWorldTxnState.Aborted, record.State);
    }

    [Fact]
    public void CoordinationModuleRejectsPrepareBeforeStartWithoutParticipantCalls()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        var game = new CountingGamePort();
        var voxel = new CountingVoxelPort(result);
        CoordinationModule module = CoordinationModule.Create(
            expected,
            game,
            voxel,
            new EcsCommandCommitExecutor(new CountingEcsPort()),
            new InMemoryTxnJournalPort(),
            (_, _) => result,
            new InMemoryTxnResultEvidencePort());

        TxnPrepareResult prepared = module.Services.PrepareTxn(
            PrepareRequest("txn-before-start", 2UL, expected));

        Assert.False(prepared.IsPrepared);
        Assert.Equal("ContextClosing", prepared.Failure?.GeneratedErrorId);
        Assert.Equal(0, game.Calls);
        Assert.Equal(0, voxel.PrepareCalls);
    }

    [Fact]
    public void ReentrantPrepareCallbackCannotAcquireSecondLeasePair()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        var voxel = new CountingVoxelPort(Vector(2UL, 2UL));
        var game = new ReentrantGamePort();
        var coordinator = new TxnPrepareCoordinator(
            new SessionRevisionVectorStore(expected),
            new CrossWorldCoordinator(),
            game,
            voxel);
        TxnPrepareRequest request = PrepareRequest("txn-reentrant-prepare", 2UL, expected);
        game.Callback = () => coordinator.Prepare(request);

        TxnPrepareResult outer = coordinator.Prepare(request);

        Assert.True(outer.IsPrepared);
        Assert.NotNull(game.Nested);
        Assert.Equal(TxnPrepareStatus.Retryable, game.Nested!.Value.Status);
        Assert.Equal(1, game.Calls);
        Assert.Equal(1, voxel.PrepareCalls);
    }

    [Fact]
    public void StaleJournalTailIsRetryableAndDoesNotPoisonStream()
    {
        var journal = new InMemoryTxnJournalPort();
        TxnJournalTailResult stale = journal.ReadTail();
        TxnJournalRecord first = TxnJournalRecordFactory.Create(
            "session", "runtime", 1UL, "txn-tail-first",
            TxnJournalRecordRecordKind.Prepare, "first", recordSeq: 1UL,
            previousHash: stale.Checksum);
        Assert.True(journal.Append(in first).IsDurable);
        TxnJournalRecord competing = TxnJournalRecordFactory.Create(
            "session", "runtime", 1UL, "txn-tail-second",
            TxnJournalRecordRecordKind.Prepare, "second", recordSeq: 1UL,
            previousHash: stale.Checksum);

        TxnJournalAppendResult rejected = journal.Append(in competing);

        Assert.Equal(TxnJournalAppendStatus.Backpressured, rejected.Status);
        Assert.Equal("RevisionConflict", rejected.GeneratedErrorId);
        Assert.False(journal.IsFatal);
        Assert.Single(journal.Records);
    }

    [Fact]
    public void RecoveryQueriesOnlyParticipantMissingDurableMarker()
    {
        SessionRevisionVectorView expected = Vector(1UL, 1UL);
        SessionRevisionVectorView result = Vector(2UL, 2UL);
        TxnRecord record = IntentRecord("txn-query-missing-only", 2UL, expected);
        var journal = new InMemoryTxnJournalPort();
        TxnAuthorityTestData.AppendIntentAndVoxelMarker(journal, record, result);
        var queries = new ParticipantCountingQueryPort(result);

        TxnRecoveryResult recovered = new TxnRecoveryResolver(
                journal,
                new SessionRevisionVectorStore(expected),
                new InMemoryTxnResultEvidencePort())
            .Recover(record, queries);

        Assert.True(recovered.Succeeded);
        Assert.Equal(0, queries.VoxelCalls);
        Assert.Equal(1, queries.EcsCalls);
    }

    private static string[] PublicDeclaredMethods(Type type) => type
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Select(method => method.Name)
        .ToArray();

    private static CommitIntentCoordinator Coordinator(
        SessionRevisionVectorView expected,
        SessionRevisionVectorView result,
        ITxnJournalPort journal,
        CountingVoxelPort voxel,
        CountingEcsPort ecs,
        ITxnResultEvidencePort evidence) =>
        Coordinator(new SessionRevisionVectorStore(expected), result, journal, evidence, voxel, ecs);

    private static CommitIntentCoordinator Coordinator(
        SessionRevisionVectorStore revisions,
        SessionRevisionVectorView result,
        ITxnJournalPort journal,
        ITxnResultEvidencePort evidence,
        CountingVoxelPort? voxel = null,
        CountingEcsPort? ecs = null) =>
        new(revisions, journal, voxel ?? new CountingVoxelPort(result),
            new EcsCommandCommitExecutor(ecs ?? new CountingEcsPort()),
            new FixedRevisionPort(result), evidence);

    private static TxnRecord PreparedRecord(string txnId, ulong tick, SessionRevisionVectorView expected)
    {
        TxnRecord record = new("session", txnId, tick, "command", expected, tick + 10UL, "digest");
        record.AttachPreparedDelta(PrepareNoSideEffectTests.Prepared(tick), "voxel-token");
        Assert.True(record.TryTransition(CrossWorldTxnState.Prepared).Succeeded);
        return record;
    }

    private static TxnRecord IntentRecord(string txnId, ulong tick, SessionRevisionVectorView expected)
    {
        TxnRecord record = PreparedRecord(txnId, tick, expected);
        TxnAuthorityTestData.MarkIntent(record);
        return record;
    }

    private static TxnPrepareRequest PrepareRequest(string txnId, ulong tick, SessionRevisionVectorView expected) =>
        new("session", txnId, tick, "command", expected.GameRevision, expected.VoxelWorldRevision,
            expected.ChunkRevisionSet, tick + 10UL, (int)expected.SchemaEpoch,
            PrepareNoSideEffectTests.Prepared(tick), "digest");

    private static SessionRevisionVectorView Vector(ulong tick, ulong revision) =>
        new(tick, revision, revision, new Dictionary<string, ulong>(), revision, 1UL, 1UL);

    private static InMemoryTxnResultEvidencePort Evidence(TxnRecord record, SessionRevisionVectorView result)
    {
        var evidence = new InMemoryTxnResultEvidencePort();
        TxnResultEvidence row = new(record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision, result, record.GameReleaseId);
        Assert.True(evidence.Write(in row).IsDurable);
        return evidence;
    }

    private static TxnResultEvidenceIdentity EvidenceIdentity(TxnRecord record) =>
        new(record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision.CanonicalDigestHex, record.GameReleaseId);

    private static void Append(
        InMemoryTxnJournalPort journal,
        TxnRecord record,
        TxnJournalRecordRecordKind kind,
        string key,
        TxnJournalRecordCommitState state,
        string payload)
    {
        TxnJournalRecord row = TxnJournalRecordFactory.Create(
            record.SessionId, record.GameReleaseId, record.TickId, record.TxnId, kind, key, state,
            TxnJournalRecordDurabilityState.Durable, record.CommandId,
            payload: System.Text.Encoding.UTF8.GetBytes(payload));
        Assert.True(journal.Append(in row).IsDurable);
    }

    private sealed class FixedRevisionPort : IEcsCommandCommitRevisionPort
    {
        private readonly SessionRevisionVectorView _result;

        internal FixedRevisionPort(SessionRevisionVectorView result) => _result = result;

        public SessionRevisionVectorView? ReadResultRevision(TxnRecord record, CommandApplyReceipt receipt) => _result;
    }

    private sealed class CountingVoxelPort : IVoxelWorldPort
    {
        private readonly SessionRevisionVectorView _result;

        internal CountingVoxelPort(SessionRevisionVectorView result) => _result = result;

        public int PrepareCalls { get; private set; }

        public int CommitCalls { get; private set; }

        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request)
        {
            PrepareCalls++;
            return VoxelPrepareResult.Prepared("voxel-token", request.DeadlineTick);
        }

        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request)
        {
            CommitCalls++;
            return VoxelCommitParticipantResult.Applied(_result);
        }

        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);

        public VoxelParticipantQueryResult Query(string sessionId, string txnId) =>
            new(TxnParticipantState.Applied, true, null, _result);

        public SessionRevisionVectorView ReadRevision() => _result;
    }

    private sealed class CountingEcsPort : IEcsCommandCommitPort
    {
        public int Calls { get; private set; }

        public EcsCommandPortResult Apply(Lumio.GameRuntime.Command.Command command, string? resolvedEntityId)
        {
            Calls++;
            return EcsCommandPortResult.Applied();
        }
    }

    private sealed class CountingQueryPort : ITxnParticipantQueryPort
    {
        private readonly SessionRevisionVectorView _result;

        internal CountingQueryPort(SessionRevisionVectorView result) => _result = result;

        public int Calls { get; private set; }

        public TxnParticipantQueryResult Query(string sessionId, string txnId, TxnParticipantKind participant)
        {
            Calls++;
            return TxnParticipantQueryResult.Applied(_result);
        }
    }

    private sealed class ParticipantCountingQueryPort : ITxnParticipantQueryPort
    {
        private readonly SessionRevisionVectorView _result;

        internal ParticipantCountingQueryPort(SessionRevisionVectorView result) => _result = result;

        internal int VoxelCalls { get; private set; }

        internal int EcsCalls { get; private set; }

        public TxnParticipantQueryResult Query(string sessionId, string txnId, TxnParticipantKind participant)
        {
            if (participant == TxnParticipantKind.VoxelCommit) VoxelCalls++;
            else EcsCalls++;
            return TxnParticipantQueryResult.Applied(_result);
        }
    }

    private sealed class SuccessfulGamePort : IGameReservationPort
    {
        public GameReservationResult Reserve(in GameReservationRequest request) =>
            new(GameReservationStatus.Reserved, new ReservationLease(string.Concat("game-", request.TxnId)), null);
    }

    private sealed class CountingGamePort : IGameReservationPort
    {
        public int Calls { get; private set; }

        public GameReservationResult Reserve(in GameReservationRequest request)
        {
            Calls++;
            return new GameReservationResult(
                GameReservationStatus.Reserved,
                new ReservationLease(string.Concat("game-", request.TxnId)),
                null);
        }
    }

    private sealed class ReentrantGamePort : IGameReservationPort
    {
        private bool _inside;

        internal Func<TxnPrepareResult>? Callback { get; set; }

        internal TxnPrepareResult? Nested { get; private set; }

        public int Calls { get; private set; }

        public GameReservationResult Reserve(in GameReservationRequest request)
        {
            Calls++;
            if (!_inside && Callback is not null)
            {
                _inside = true;
                Nested = Callback();
            }
            return new GameReservationResult(
                GameReservationStatus.Reserved,
                new ReservationLease(string.Concat("game-", request.TxnId)),
                null);
        }
    }

    private sealed class ReentrantJournalPort : ITxnJournalPort
    {
        private readonly InMemoryTxnJournalPort _inner;
        private bool _inside;

        internal ReentrantJournalPort(InMemoryTxnJournalPort inner) => _inner = inner;

        internal Func<TxnCommitResult>? Callback { get; set; }

        internal TxnCommitResult? Nested { get; private set; }

        public TxnJournalAppendResult Append(in TxnJournalRecord record)
        {
            if (!_inside && Callback is not null)
            {
                _inside = true;
                Nested = Callback();
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "PanicBoundary");
            }

            return _inner.Append(in record);
        }

        public TxnJournalQueryResult Query(string sessionId, string txnId) => _inner.Query(sessionId, txnId);

        public TxnJournalTailResult ReadTail() => _inner.ReadTail();
    }

    private sealed class PublicOnlyJournalPort : ITxnJournalPort
    {
        private readonly InMemoryTxnJournalPort _inner;

        internal PublicOnlyJournalPort(InMemoryTxnJournalPort inner) => _inner = inner;

        public TxnJournalAppendResult Append(in TxnJournalRecord record) => _inner.Append(in record);

        public TxnJournalQueryResult Query(string sessionId, string txnId) => _inner.Query(sessionId, txnId);

        public TxnJournalTailResult ReadTail() => _inner.ReadTail();
    }

    private sealed class FailOnceTerminalJournal : ITxnJournalPort
    {
        private readonly InMemoryTxnJournalPort _inner;
        private bool _fail = true;

        internal FailOnceTerminalJournal(InMemoryTxnJournalPort inner) => _inner = inner;

        public TxnJournalAppendResult Append(in TxnJournalRecord record)
        {
            if (_fail && record.RecordKind == TxnJournalRecordRecordKind.Committed)
            {
                _fail = false;
                return new TxnJournalAppendResult(TxnJournalAppendStatus.Fatal, 0UL, false, "PanicBoundary");
            }
            return _inner.Append(in record);
        }

        public TxnJournalQueryResult Query(string sessionId, string txnId) =>
            _inner.Query(sessionId, txnId);

        public TxnJournalTailResult ReadTail() => _inner.ReadTail();
    }
}
