using System;
using System.Text;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Validation;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class FormalReviewDeltaRegressionTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly NetEntityId Id1 = NetEntityId.Parse("00000000000000010000000000000001");
    private static readonly NetEntityId Id2 = NetEntityId.Parse("00000000000000010000000000000002");

    [Fact]
    public void UnknownDeltaSequencePrefixRequiresResync()
    {
        var history = new DeltaHistory(new ReplicationBudget(8, 4096, 8, 4096));
        IdentityStoreToken token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-1", 1, 2, 4, 10, Hash), token));
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-1", 2, 3, 5, 10, Hash), token));

        DeltaChainResult result = history.TryGetContiguous("snap-1", 2, 3, token);

        Assert.Equal(DeltaChainStatus.Gap, result.Status);
        Assert.True(result.RequiresResync);
    }

    [Fact]
    public void UnknownAndDuplicateDestroyRetainTheLongestHorizon()
    {
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();

        Assert.False(table.Remove(Id1, 5, new TombstoneHorizonResult(true, 10), token));
        Assert.Equal(10UL, table.Tombstones[Id1]);
        Assert.False(table.Bind(Id1, "4:1", currentRevision: 10, token).Succeeded);

        Assert.True(table.Bind(Id2, "5:1", token).Succeeded);
        Assert.True(table.Remove(Id2, 5, new TombstoneHorizonResult(true, 10), token));
        Assert.False(table.Remove(Id2, 6, new TombstoneHorizonResult(true, 20), token));
        Assert.Equal(20UL, table.Tombstones[Id2]);
    }

    [Fact]
    public void SnapshotAndAcknowledgementsRequireTheirLifecycleStates()
    {
        using ReplicationContext context = CreateContext();

        FullSnapshotProjectionResult created = context.BuildFullSnapshot("snap-created", Revision(1));
        Assert.Equal(ProjectionStatus.Rejected, created.Status);
        Assert.Equal("InvalidArgument", created.Failure?.GeneratedErrorId);
        Assert.Empty(context.Baselines.Snapshot());

        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult snapshot = context.BuildFullSnapshot("snap-1", Revision(1));
        Assert.True(snapshot.Succeeded);
        Assert.Equal(BaselineAckStatus.UnknownBaseline, context.AckBaseline("snap-1", 1));
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-1", 1));
        Assert.Equal(BaselineAckStatus.UnknownBaseline, context.AckDelta("snap-1", 1, 1));
        Assert.True(context.Activate().Succeeded);
    }

    [Fact]
    public void DeltaCannotStartBeforeAcknowledgedBaselineRevision()
    {
        using ReplicationContext context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-5", Revision(5)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-5", 5));
        Assert.True(context.Activate().Succeeded);

        DeltaProjectionResult result = context.BuildDelta(
            "snap-5", 1, 6, 1, Revision(6), Array.Empty<TombstoneView>());

        Assert.Equal(ProjectionStatus.Rejected, result.Status);
        Assert.Equal("SnapshotBaseMismatch", result.Failure?.GeneratedErrorId);
        Assert.Empty(context.Deltas.Snapshot());
    }

    [Fact]
    public void ResyncClearsEveryAbandonedBaselineRecord()
    {
        using ReplicationContext context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-abandoned", Revision(1)).Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-active", Revision(1)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-active", 1));
        Assert.True(context.Activate().Succeeded);
        Assert.Equal(2, context.Baselines.Count);

        Assert.True(context.BeginResync().Succeeded);

        Assert.Empty(context.Baselines.Snapshot());
    }

    [Fact]
    public void ZeroEnvelopeLengthFollowsTheGeneratedMinimum()
    {
        MappingSetView mappings = CreateMappings();
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        FullSnapshotProjectionResult built = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-1", Revision(1), mappings);
        ReplicationEnvelope original = built.Snapshot!.ToEnvelope("trace-1");
        var zeroLength = new ReplicationEnvelope(
            original.SessionId,
            original.ProductId,
            original.GameReleaseId,
            original.ProtocolVersion,
            0,
            original.Sequence,
            original.MessageType,
            original.Reliability,
            original.Integrity,
            original.TraceId,
            original.TransportPolicy,
            original.Body);
        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", "FullSnapshot",
            "Client", new[] { "replicate" }, 1,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            zeroLength, admission, "session-1", "product", "release-1", mappings.MappingSetHash, 1);

        Assert.True(result.Succeeded, result.Detail);
    }

    private static RevisionVector Revision(ulong revision) =>
        new(revision, revision, revision, revision, revision, revision, 1);

    private static ReplicationContext CreateContext() =>
        new("session-1", "product", "release-1", CreateMappings(),
            new ReplicationBudget(8, 8192, 16, 8192));

    private static MappingSetView CreateMappings()
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        return mappings.View;
    }
}
