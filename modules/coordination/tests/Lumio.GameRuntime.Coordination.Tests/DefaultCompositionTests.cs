using System.Collections.Generic;
using System.Reflection;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class DefaultCompositionTests
{
    [Fact]
    public void DefaultCommandCompositionCannotReportAppliedSuccess()
    {
        PreparedGameDelta delta = PrepareNoSideEffectTests.Prepared(2UL);
        CommandModule module = CommandModule.Create();
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);

        CommandApplyReceipt result = module.Apply(delta);

        Assert.Equal(CommandApplyStatus.InfrastructureFault, result.Status);
        Assert.False(result.IsApplied);
        Assert.Equal("CapabilityMissing", result.GeneratedErrorId);
    }

    [Fact]
    public void ExplicitCommandParticipantRetainsAppliedSuccessPath()
    {
        PreparedGameDelta delta = PrepareNoSideEffectTests.Prepared(2UL);
        CommandModule module = CommandModule.Create(
            executor: new EcsCommandCommitExecutor(new ConfiguredEcsPort()));
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);

        CommandApplyReceipt result = module.Apply(delta);

        Assert.Equal(CommandApplyStatus.Applied, result.Status);
    }

    [Fact]
    public void DefaultCoordinationCompositionCannotPrepareOrAdvanceRevision()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        CoordinationModule module = CoordinationModule.Create(current);
        module.Configure();
        module.Start();
        PreparedGameDelta delta = PrepareNoSideEffectTests.Prepared(2UL);
        TxnPrepareRequest request = new(
            "session", "txn-default", 2UL, "command", 1UL, 1UL,
            new Dictionary<string, ulong>(), 10UL, 1, delta, "digest");

        TxnPrepareResult result = module.Services.PrepareTxn(request);

        Assert.NotEqual(TxnPrepareStatus.Prepared, result.Status);
        Assert.Equal(current, module.Revisions.Read());
    }

    [Fact]
    public void DefaultCoordinationCompositionCannotCommitPreparedTransaction()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        CoordinationModule module = CoordinationModule.Create(current);
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);
        PreparedGameDelta delta = PrepareNoSideEffectTests.Prepared(2UL);
        var record = new TxnRecord("session", "txn-default-commit", 2UL, "command", current, 10UL, "digest");
        record.AttachPreparedDelta(delta, "voxel-token");
        record.TryTransition(CrossWorldTxnState.Prepared);
        using var prepared = new CrossWorldPreparedTxn(
            record,
            new ReservationLease("game"),
            new PreparedVoxelTokenLease("voxel-token", 10UL));

        TxnCommitResult result = module.Services.CommitTxn(prepared);

        Assert.NotEqual(TxnCommitStatus.Committed, result.Status);
        Assert.NotEqual(TxnCommitStatus.AlreadyCommitted, result.Status);
        Assert.Equal(current, module.Revisions.Read());
    }

    [Fact]
    public void ManualCommitMarkerCannotBypassParticipantResults()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        var revisions = new SessionRevisionVectorStore(current);
        var coordinator = new CrossWorldCoordinator(revisions);
        TxnBeginResult begin = coordinator.Begin(new TxnRequest(
            "session", "txn-manual-commit", 1UL, "command", current, 10UL, "digest"));

        MethodInfo? result = typeof(CrossWorldCoordinator).GetMethod(
            "MarkCommitted",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.Null(result);
        Assert.Equal(CrossWorldTxnState.Created, begin.Record!.State);
        Assert.Equal(current, revisions.Read());
    }

    [Fact]
    public void ResolveDoesNotReportCommittedWithoutVerifiedRevision()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        var coordinator = new CrossWorldCoordinator(new SessionRevisionVectorStore(current));
        TxnBeginResult begin = coordinator.Begin(new TxnRequest(
            "session", "txn-unverified-resolution", 1UL, "command", current, 10UL, "digest"));
        begin.Record!.TryTransition(CrossWorldTxnState.Prepared);
        TxnAuthorityTestData.MarkIntent(begin.Record);
        begin.Record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Applied);
        begin.Record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Applied);
        begin.Record.TryTransition(CrossWorldTxnState.Indeterminate);

        TxnResolutionResult result = coordinator.ResolveTxn(begin.Record.TxnId);

        Assert.Equal(TxnCommitStatus.Indeterminate, result.Status);
    }

    [Fact]
    public void ManualParticipantMarkersStillCannotFabricateCommitRevision()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        var coordinator = new CrossWorldCoordinator(new SessionRevisionVectorStore(current));
        TxnBeginResult begin = coordinator.Begin(new TxnRequest(
            "session", "txn-forged-result", 1UL, "command", current, 10UL, "digest"));
        begin.Record!.TryTransition(CrossWorldTxnState.Prepared);
        TxnAuthorityTestData.MarkIntent(begin.Record);
        begin.Record.MarkParticipant(TxnParticipantKind.VoxelCommit, TxnParticipantState.Applied);
        begin.Record.MarkParticipant(TxnParticipantKind.EcsCommandBufferCommit, TxnParticipantState.Applied);

        MethodInfo? result = typeof(CrossWorldCoordinator).GetMethod(
            "MarkCommitted",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.Null(result);
        Assert.Equal(CrossWorldTxnState.CommitIntent, begin.Record.State);
    }

    [Fact]
    public void ExplicitParticipantsRetainConfiguredSuccessPath()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView resultRevision = PrepareNoSideEffectTests.Vector(2UL);
        var voxel = new ConfiguredVoxelPort(resultRevision);
        var module = CoordinationModule.Create(
            current,
            new ConfiguredGamePort(),
            voxel,
            new EcsCommandCommitExecutor(new ConfiguredEcsPort()),
            new InMemoryTxnJournalPort(),
            (record, receipt) => resultRevision,
            new InMemoryTxnResultEvidencePort());
        module.Configure();
        module.Start();
        TxnPrepareResult prepared = module.Services.PrepareTxn(new TxnPrepareRequest(
            "session", "txn-configured", 2UL, "command", 1UL, 1UL,
            new Dictionary<string, ulong>(), 10UL, 1,
            PrepareNoSideEffectTests.Prepared(2UL), "digest"));

        Assert.True(prepared.IsPrepared);
        TxnCommitResult committed = module.Services.CommitTxn(prepared.Prepared!);
        Assert.Equal(TxnCommitStatus.Committed, committed.Status);
        Assert.Equal(resultRevision, module.Revisions.Read());
    }

    [Fact]
    public void ExplicitCompositionWithoutResultEvidenceFailsClosed()
    {
        SessionRevisionVectorView current = PrepareNoSideEffectTests.Vector(1UL);
        SessionRevisionVectorView resultRevision = PrepareNoSideEffectTests.Vector(2UL);
        CoordinationModule module = CoordinationModule.Create(
            current,
            new ConfiguredGamePort(),
            new ConfiguredVoxelPort(resultRevision),
            new EcsCommandCommitExecutor(new ConfiguredEcsPort()),
            new InMemoryTxnJournalPort(),
            (record, receipt) => resultRevision);
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);
        TxnPrepareResult prepared = module.Services.PrepareTxn(new TxnPrepareRequest(
            "session", "txn-no-evidence", 2UL, "command", 1UL, 1UL,
            new Dictionary<string, ulong>(), 10UL, 1,
            PrepareNoSideEffectTests.Prepared(2UL), "digest-no-evidence"));

        Assert.True(prepared.IsPrepared);
        TxnCommitResult result = module.Services.CommitTxn(prepared.Prepared!);

        Assert.NotEqual(TxnCommitStatus.Committed, result.Status);
        Assert.NotEqual(TxnCommitStatus.AlreadyCommitted, result.Status);
        Assert.Equal("EvidenceMissing", result.Failure?.GeneratedErrorId);
        Assert.Equal(current, module.Revisions.Read());
    }

    private sealed class ConfiguredGamePort : IGameReservationPort
    {
        public GameReservationResult Reserve(in GameReservationRequest request) =>
            new(GameReservationStatus.Reserved, new ReservationLease(request.TxnId), null);
    }

    private sealed class ConfiguredVoxelPort : IVoxelWorldPort
    {
        private readonly SessionRevisionVectorView _revision;
        internal ConfiguredVoxelPort(SessionRevisionVectorView revision) => _revision = revision;
        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) =>
            VoxelPrepareResult.Prepared("configured-token", request.DeadlineTick);
        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) =>
            VoxelCommitParticipantResult.Applied(_revision);
        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);
        public VoxelParticipantQueryResult Query(string sessionId, string txnId) =>
            new(TxnParticipantState.Applied, true, null, _revision);
        public SessionRevisionVectorView ReadRevision() => _revision;
    }

    private sealed class ConfiguredEcsPort : IEcsCommandCommitPort
    {
        public EcsCommandPortResult Apply(Lumio.GameRuntime.Command.Command command, string? resolvedEntityId) =>
            EcsCommandPortResult.Applied();
    }

}
