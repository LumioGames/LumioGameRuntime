using System;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Resync;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class LifecycleHistoryRegressionTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void AcknowledgingOneBaselineRetainsOtherPendingBaselines()
    {
        var store = new BaselineStore(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken token = store.CaptureToken();

        Assert.Equal(BaselineStoreStatus.Accepted, store.Add(new BaselineRecord("snap-a", 10, 100, Hash), token));
        Assert.Equal(BaselineStoreStatus.Accepted, store.Add(new BaselineRecord("snap-b", 11, 100, Hash), token));

        Assert.Equal(BaselineAckStatus.Acknowledged, store.Ack("snap-a", 10, token));
        Assert.True(store.TryGet("snap-b", out BaselineRecord? pending));
        Assert.False(pending!.Acknowledged);
        Assert.Equal(BaselineAckStatus.Acknowledged, store.Ack("snap-b", 11, token));
    }

    [Fact]
    public void BaselineStoreRejectsCallerOwnedAcknowledgementState()
    {
        var store = new BaselineStore(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken token = store.CaptureToken();

        Assert.Equal(BaselineStoreStatus.Invalid,
            store.Add(new BaselineRecord("injected", 1, 1, Hash, Acknowledged: true), token));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void BaselineStoreRejectsAnEmptyMappingHash()
    {
        var store = new BaselineStore(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken token = store.CaptureToken();

        Assert.Equal(BaselineStoreStatus.Invalid,
            store.Add(new BaselineRecord("empty-hash", 1, 1, string.Empty), token));
    }

    [Fact]
    public void ResyncCompletionRequiresAcknowledgeOfTheNewlyStagedBaseline()
    {
        var context = CreateContext(new ReplicationBudget(8, 4096, 8, 320));
        var revision = new RevisionVector(1, 1, 1, 1, 1, 1, 1);

        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-1", revision).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-1", 1));
        Assert.True(context.Activate().Succeeded);
        Assert.True(context.BeginResync().Succeeded);
        Assert.False(context.Baselines.TryGet("snap-1", out _));

        FullSnapshotProjectionResult replacement = context.BuildFullSnapshot(new string('r', 128), revision);
        Assert.Equal(ProjectionStatus.Retryable, replacement.Status);
        Assert.True(context.AwaitBaselineAck().Succeeded);

        ReplicationContextTransitionResult completion = context.CompleteResync();

        Assert.False(completion.Succeeded);
        Assert.Equal(ReplicationContextState.AwaitingBaselineAck, completion.State);
    }

    [Fact]
    public void SuccessfulResyncCannotCompleteBeforeTheReplacementAck()
    {
        var context = CreateContext(new ReplicationBudget(8, 4096, 8, 4096));
        var firstRevision = new RevisionVector(1, 1, 1, 1, 1, 1, 1);
        var replacementRevision = new RevisionVector(2, 2, 2, 2, 2, 2, 1);

        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-1", firstRevision).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-1", 1));
        Assert.True(context.Activate().Succeeded);
        Assert.True(context.BeginResync().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-2", replacementRevision).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);

        Assert.False(context.CompleteResync().Succeeded);
        Assert.Equal(BaselineAckStatus.UnknownBaseline, context.AckBaseline("snap-1", 1));
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-2", 2));
        Assert.True(context.CompleteResync().Succeeded);

        DeltaProjectionResult delta = context.BuildDelta(
            "snap-2", 2, 3, 1, new RevisionVector(3, 3, 3, 3, 3, 3, 1),
            Array.Empty<TombstoneView>());
        Assert.True(delta.Succeeded);
        Assert.Equal(DeltaChainStatus.Complete,
            context.Deltas.TryGetContiguous("snap-2", 2, 3).Status);
    }

    [Fact]
    public void UnknownBaselineRequiresResyncForAnEmptyRange()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));

        DeltaChainResult result = history.TryGetContiguous("missing", 0, 0);

        Assert.Equal(DeltaChainStatus.UnknownBaseline, result.Status);
        Assert.True(result.RequiresResync);
    }

    [Fact]
    public void SequenceGapsCannotProduceACompleteDeltaChain()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted, history.Add(new DeltaRecord("snap-1", 1, 2, 1, 10, Hash), token));
        Assert.Equal(DeltaHistoryStatus.Accepted, history.Add(new DeltaRecord("snap-1", 2, 3, 3, 10, Hash), token));

        DeltaChainResult result = history.TryGetContiguous("snap-1", 1, 3);

        Assert.Equal(DeltaChainStatus.Gap, result.Status);
        Assert.True(result.RequiresResync);
    }

    [Fact]
    public void MissingRevisionLinkOnKnownHistoryIsReportedAsAGap()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted, history.Add(new DeltaRecord("snap-1", 10, 11, 1, 10, Hash), token));

        DeltaChainResult result = history.TryGetContiguous("snap-1", 9, 11);

        Assert.Equal(DeltaChainStatus.Gap, result.Status);
    }

    [Fact]
    public void AcknowledgementCannotSkipASequenceOrRevision()
    {
        var history = new DeltaHistory(new ReplicationBudget(8, 4096, 8, 4096));
        IdentityStoreToken token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted, history.Add(new DeltaRecord("snap-1", 1, 2, 1, 10, Hash), token));
        Assert.Equal(DeltaHistoryStatus.Accepted, history.Add(new DeltaRecord("snap-1", 4, 5, 2, 10, Hash), token));

        Assert.Equal(DeltaAckStatus.UnknownHistory, history.Acknowledge("snap-1", 2, 5, token));
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void AckedHistoryCannotSatisfyAStaleEmptyRange()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted, history.Add(new DeltaRecord("snap-1", 1, 2, 1, 10, Hash), token));
        Assert.Equal(DeltaAckStatus.Acknowledged, history.Acknowledge("snap-1", 1, 2, token));

        DeltaChainResult result = history.TryGetContiguous("snap-1", 1, 1);

        Assert.NotEqual(DeltaChainStatus.Complete, result.Status);
        Assert.True(result.RequiresResync);
    }

    [Fact]
    public void ResyncEvaluationRequiresAnAcknowledgedKnownBaseline()
    {
        var context = CreateContext(new ReplicationBudget(8, 4096, 8, 4096));
        var revision = new RevisionVector(1, 1, 1, 1, 1, 1, 1);
        var coordinator = new ResyncCoordinator();

        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-1", revision).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.True(coordinator.Evaluate(context, "snap-1", 1, 1, 1, 1).RequiresResync);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-1", 1));
        Assert.True(context.Activate().Succeeded);

        ResyncDecision decision = coordinator.Evaluate(context, "snap-1", 1, 1, 1, 1);

        Assert.False(decision.RequiresResync);
    }

    [Fact]
    public void EmptyMappingHashesAreRejectedAndCannotPassRepairValidation()
    {
        var history = new DeltaHistory(new ReplicationBudget(4, 4096, 8, 4096));
        IdentityStoreToken token = history.CaptureToken();

        Assert.Equal(DeltaHistoryStatus.Invalid,
            history.Add(new DeltaRecord("snap-1", 1, 2, 1, 10, string.Empty), token));
        Assert.NotEqual(DeltaChainStatus.Complete,
            history.TryBuildRepairRange("snap-1", 1, 2, Hash).Status);
    }

    private static ReplicationContext CreateContext(ReplicationBudget budget)
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        return new ReplicationContext("session-1", "product", "release-1", mappings.View, budget);
    }
}
