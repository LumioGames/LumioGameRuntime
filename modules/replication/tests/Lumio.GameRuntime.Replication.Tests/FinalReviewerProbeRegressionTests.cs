using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Validation;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class FinalReviewerProbeRegressionTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void DuplicatePendingSnapshotReusesItsSequenceAndPreservesDeltaPrefix()
    {
        using ReplicationContext context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult first = context.BuildFullSnapshot("snap-1", Revision(1));
        FullSnapshotProjectionResult duplicate = context.BuildFullSnapshot("snap-1", Revision(1));
        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded, duplicate.Failure?.Detail);

        Assert.Equal(first.Snapshot!.Sequence, duplicate.Snapshot!.Sequence);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-1", 1));
        Assert.True(context.Activate().Succeeded);
        DeltaProjectionResult delta = context.BuildDelta(
            "snap-1", 1, 2, 1, Revision(2), Array.Empty<TombstoneView>());
        Assert.True(delta.Succeeded);
        Assert.Equal(first.Snapshot.Sequence + 1, delta.Delta!.Sequence);
        Assert.Equal(DeltaChainStatus.Complete, context.Deltas.TryGetContiguous("snap-1", 1, 2).Status);
    }

    [Fact]
    public void SequenceExhaustionRejectsWithoutThrowing()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        typeof(ReplicationProjection).GetField("_nextSequence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(projection, ulong.MaxValue);
        FullSnapshotProjectionResult result = default;

        Exception? exception = Record.Exception(() =>
            result = projection.BuildFullSnapshot(
                "session-1", "product", "release-1", "snap-1", Revision(1), CreateMappings()));

        Assert.Null(exception);
        Assert.Equal(ProjectionStatus.Rejected, result.Status);
        Assert.Equal("CapacityExceeded", result.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void ProductAndReleaseIdentityFollowSchemaPatterns()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        MappingSetView mappings = CreateMappings();

        FullSnapshotProjectionResult product = projection.BuildFullSnapshot(
            "session-1", "product:bad", "release-1", "snap-1", Revision(1), mappings);
        FullSnapshotProjectionResult release = projection.BuildFullSnapshot(
            "session-1", "product", "release:bad", "snap-1", Revision(1), mappings);

        Assert.Equal(ProjectionStatus.Rejected, product.Status);
        Assert.Equal(ProjectionStatus.Rejected, release.Status);
    }

    [Fact]
    public void RevisionRejectsChunkCoordinatesOutsideInt32Range()
    {
        var initial = new RevisionVector(1, 1, 1,
            new Dictionary<string, ulong>(StringComparer.Ordinal) { ["c:0:0:0"] = 1 }, 1, 1, 1);
        var next = new RevisionVector(2, 2, 2,
            new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                ["c:0:0:0"] = 2,
                ["c:2147483648:0:0"] = 1,
            }, 2, 2, 1);
        var store = new AuthorityRevisionStore(initial);

        RevisionAdvanceResult result = store.Advance(next);

        Assert.False(next.IsValid);
        Assert.Equal(RevisionAdvanceStatus.Rejected, result.Status);
        Assert.True(store.Current.Equals(initial));
    }

    [Fact]
    public void ProvisionalRemapsAreBijective()
    {
        var remaps = new ProvisionalRemapTable();
        IdentityStoreToken token = remaps.CaptureToken();
        NetEntityId first = NetEntityId.Parse("00000000000000010000000000000001");
        NetEntityId second = NetEntityId.Parse("00000000000000010000000000000002");
        NetEntityId authoritative = NetEntityId.Parse("00000000000000010000000000000003");

        Assert.True(remaps.Add(ProvisionalIdentity(first), AuthoritativeIdentity(authoritative), token).Succeeded);
        ProvisionalRemapResult conflict = remaps.Add(ProvisionalIdentity(second), AuthoritativeIdentity(authoritative), token);

        Assert.False(conflict.Succeeded);
        Assert.Equal("RevisionConflict", conflict.GeneratedErrorId);
        Assert.Equal(1, remaps.Count);
    }

    [Fact]
    public void FullSnapshotDuplicateNonPendingRetainsOriginalSequence()
    {
        using ReplicationContext context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);

        FullSnapshotProjectionResult oldSnapshot = context.BuildFullSnapshot("snap-old", Revision(1));
        FullSnapshotProjectionResult pendingSnapshot = context.BuildFullSnapshot("snap-pending", Revision(2));
        FullSnapshotProjectionResult duplicate = context.BuildFullSnapshot("snap-old", Revision(1));

        Assert.True(oldSnapshot.Succeeded);
        Assert.True(pendingSnapshot.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.Equal(oldSnapshot.Snapshot!.Sequence, duplicate.Snapshot!.Sequence);
        Assert.Equal(2, context.Baselines.Count);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-pending", 2));
        Assert.True(context.Activate().Succeeded);
        DeltaProjectionResult delta = context.BuildDelta(
            "snap-pending", 2, 3, 2, Revision(3), Array.Empty<TombstoneView>());
        Assert.True(delta.Succeeded);
        Assert.Equal(pendingSnapshot.Snapshot!.Sequence + 1, delta.Delta!.Sequence);
    }

    [Fact]
    public void AcknowledgedOlderSnapshotDuplicateUsesRetainedIdentity()
    {
        using ReplicationContext context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult older = context.BuildFullSnapshot("snap-older", Revision(1));
        var store = (BaselineStore)typeof(ReplicationContext)
            .GetField("_baselineStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context)!;
        typeof(BaselineStore).GetMethod("Ack", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(ulong) }, null)!
            .Invoke(store, new object[] { "snap-older", 1UL });
        Assert.True(context.BuildFullSnapshot("snap-current", Revision(2)).Succeeded);
        typeof(BaselineStore).GetMethod("Ack", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(ulong) }, null)!
            .Invoke(store, new object[] { "snap-current", 2UL });

        FullSnapshotProjectionResult duplicate = context.BuildFullSnapshot("snap-older", Revision(1));

        Assert.True(older.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.Equal(older.Snapshot!.Sequence, duplicate.Snapshot!.Sequence);
    }

    [Fact]
    public void DirectProjectionFullSnapshotDuplicateIsIdempotent()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        MappingSetView mappings = CreateMappings();

        FullSnapshotProjectionResult first = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-1", Revision(1), mappings);
        FullSnapshotProjectionResult duplicate = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-1", Revision(1), mappings);
        FullSnapshotProjectionResult next = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-2", Revision(2), mappings);

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.True(next.Succeeded);
        Assert.Equal(first.Snapshot!.Sequence, duplicate.Snapshot!.Sequence);
        Assert.Equal(first.Snapshot.Sequence + 1, next.Snapshot!.Sequence);
    }

    [Fact]
    public void DirectProjectionDuplicateSurvivesSequenceExhaustion()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(1, 4096, 8, 4096));
        MappingSetView mappings = CreateMappings();
        FullSnapshotProjectionResult first = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-1", Revision(1), mappings);
        typeof(ReplicationProjection).GetField("_nextSequence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(projection, ulong.MaxValue);

        FullSnapshotProjectionResult duplicate = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-1", Revision(1), mappings);
        FullSnapshotProjectionResult newRequest = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-2", Revision(2), mappings);

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.Equal(first.Snapshot!.Sequence, duplicate.Snapshot!.Sequence);
        Assert.Equal(ProjectionStatus.Rejected, newRequest.Status);
        Assert.Equal("CapacityExceeded", newRequest.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void DirectProjectionDeltaDuplicateSurvivesSequenceExhaustion()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(1, 4096, 8, 4096));
        MappingSetView mappings = CreateMappings();
        DeltaProjectionResult first = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 2, 1,
            Revision(2), mappings, Array.Empty<TombstoneView>());
        typeof(ReplicationProjection).GetField("_nextSequence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(projection, ulong.MaxValue);

        DeltaProjectionResult duplicate = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 2, 1,
            Revision(2), mappings, Array.Empty<TombstoneView>());
        DeltaProjectionResult newRequest = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 2, 3, 2,
            Revision(3), mappings, Array.Empty<TombstoneView>());

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.Equal(first.Delta!.Sequence, duplicate.Delta!.Sequence);
        Assert.Equal(ProjectionStatus.Rejected, newRequest.Status);
        Assert.Equal("CapacityExceeded", newRequest.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void DeltaDuplicateBeforeAndAfterAcknowledgementIsIdempotent()
    {
        using ReplicationContext context = CreateActiveContext();

        DeltaProjectionResult first = BuildDelta(context, 1, 2, 1);
        DeltaProjectionResult duplicate = BuildDelta(context, 1, 2, 1);

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.Equal(first.Delta!.Sequence, duplicate.Delta!.Sequence);
        Assert.Equal(1, context.Deltas.Count);

        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckDelta("snap-base", first.Delta.Sequence, 2));
        DeltaProjectionResult afterAck = BuildDelta(context, 1, 2, 1);
        Assert.True(afterAck.Succeeded);
        Assert.Equal(first.Delta.Sequence, afterAck.Delta!.Sequence);
        Assert.Equal(0, context.Deltas.Count);

        DeltaProjectionResult next = BuildDelta(context, 2, 3, 2);
        Assert.True(next.Succeeded);
        Assert.Equal(first.Delta.Sequence + 1, next.Delta!.Sequence);
    }

    [Fact]
    public void DeltaDuplicateAtHistoryCapacityOneRemainsIdempotent()
    {
        using ReplicationContext context = CreateActiveContext(historyWindow: 1);
        DeltaProjectionResult first = BuildDelta(context, 1, 2, 1);
        DeltaProjectionResult beforeAck = BuildDelta(context, 1, 2, 1);
        Assert.True(first.Succeeded);
        Assert.True(beforeAck.Succeeded);
        Assert.Equal(first.Delta!.Sequence, beforeAck.Delta!.Sequence);

        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckDelta("snap-base", first.Delta.Sequence, 2));
        DeltaProjectionResult afterAck = BuildDelta(context, 1, 2, 1);
        Assert.True(afterAck.Succeeded);
        Assert.Equal(first.Delta.Sequence, afterAck.Delta!.Sequence);
    }

    [Fact]
    public void ContextDeltaDuplicateSurvivesSequenceExhaustionAfterAcknowledgement()
    {
        using ReplicationContext context = CreateActiveContext();
        DeltaProjectionResult first = BuildDelta(context, 1, 2, 1);
        Assert.True(first.Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckDelta("snap-base", first.Delta!.Sequence, 2));

        FieldInfo projectionField = typeof(ReplicationContext).GetField("_projection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var projection = (ReplicationProjection)projectionField.GetValue(context)!;
        typeof(ReplicationProjection).GetField("_nextSequence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(projection, ulong.MaxValue);

        DeltaProjectionResult duplicate = BuildDelta(context, 1, 2, 1);

        Assert.True(duplicate.Succeeded);
        Assert.Equal(first.Delta.Sequence, duplicate.Delta!.Sequence);
    }

    [Fact]
    public void DeltaSameRangeWithChangedAuthoritativePayloadIsNotAReplay()
    {
        using ReplicationContext context = CreateActiveContext();

        DeltaProjectionResult first = BuildDelta(context, 1, 2, 1);
        DeltaProjectionResult changed = context.BuildDelta(
            "snap-base", 1, 2, 99, Revision(2), Array.Empty<TombstoneView>());
        DeltaProjectionResult replay = BuildDelta(context, 1, 2, 1);

        Assert.True(first.Succeeded);
        Assert.True(changed.Succeeded);
        Assert.True(replay.Succeeded);
        Assert.NotEqual(first.Delta!.Sequence, changed.Delta!.Sequence);
        Assert.NotEqual(first.Delta.BodyJson, changed.Delta!.BodyJson);
        Assert.Equal(first.Delta.Sequence, replay.Delta!.Sequence);
    }

    [Fact]
    public void DirectProjectionDeltaDuplicateIsIdempotent()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        MappingSetView mappings = CreateMappings();

        DeltaProjectionResult first = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 2, 1,
            Revision(2), mappings, Array.Empty<TombstoneView>());
        DeltaProjectionResult duplicate = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 2, 1,
            Revision(2), mappings, Array.Empty<TombstoneView>());
        DeltaProjectionResult changed = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 2, 2,
            Revision(2), mappings, Array.Empty<TombstoneView>());

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.True(changed.Succeeded);
        Assert.Equal(first.Delta!.Sequence, duplicate.Delta!.Sequence);
        Assert.Equal(first.Delta.Sequence + 1, changed.Delta!.Sequence);
    }

    [Fact]
    public void EqualGenerationContextsDoNotShareFullSnapshotIdentity()
    {
        using ReplicationContext firstContext = CreateContext();
        using ReplicationContext secondContext = CreateContext();
        Assert.True(firstContext.BeginSnapshot().Succeeded);
        Assert.True(secondContext.BeginSnapshot().Succeeded);

        FullSnapshotProjectionResult first = firstContext.BuildFullSnapshot("snap-shared", Revision(1));
        FullSnapshotProjectionResult second = secondContext.BuildFullSnapshot("snap-shared", Revision(1));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotSame(first.Snapshot, second.Snapshot);
        Assert.Equal(first.Snapshot!.Sequence, second.Snapshot!.Sequence);
    }

    [Fact]
    public void EqualGenerationContextsDoNotShareDeltaIdentity()
    {
        using ReplicationContext firstContext = CreateActiveContext();
        using ReplicationContext secondContext = CreateActiveContext();

        DeltaProjectionResult first = BuildDelta(firstContext, 1, 2, 1);
        DeltaProjectionResult second = BuildDelta(secondContext, 1, 2, 1);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotSame(first.Delta, second.Delta);
        Assert.Equal(first.Delta!.Sequence, second.Delta!.Sequence);
    }

    [Fact]
    public void FullSnapshotAcknowledgedDuplicateDoesNotConsumeSequence()
    {
        using ReplicationContext context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult first = context.BuildFullSnapshot("snap-ack", Revision(1));
        Assert.True(first.Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-ack", 1));

        FullSnapshotProjectionResult duplicate = context.BuildFullSnapshot("snap-ack", Revision(1));

        Assert.True(duplicate.Succeeded, duplicate.Failure?.Detail);
        Assert.Equal(first.Snapshot!.Sequence, duplicate.Snapshot!.Sequence);
        Assert.True(context.Activate().Succeeded);

        FullSnapshotProjectionResult activeDuplicate = context.BuildFullSnapshot("snap-ack", Revision(1));
        Assert.True(activeDuplicate.Succeeded);
        Assert.Equal(first.Snapshot.Sequence, activeDuplicate.Snapshot!.Sequence);
    }

    [Fact]
    public void ContextFullSnapshotDuplicateSurvivesSequenceExhaustion()
    {
        using ReplicationContext context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult first = context.BuildFullSnapshot("snap-exhaust", Revision(1));
        Assert.True(first.Succeeded);

        var projection = (ReplicationProjection)typeof(ReplicationContext)
            .GetField("_projection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context)!;
        typeof(ReplicationProjection).GetField("_nextSequence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(projection, ulong.MaxValue);

        FullSnapshotProjectionResult duplicate = context.BuildFullSnapshot("snap-exhaust", Revision(1));
        FullSnapshotProjectionResult newRequest = context.BuildFullSnapshot("snap-new", Revision(2));

        Assert.True(duplicate.Succeeded);
        Assert.Equal(first.Snapshot!.Sequence, duplicate.Snapshot!.Sequence);
        Assert.Equal(ProjectionStatus.Rejected, newRequest.Status);
        Assert.Equal("CapacityExceeded", newRequest.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void DeltaIdentityIncludesRevisionAndTombstoneInputs()
    {
        using ReplicationContext context = CreateActiveContext();
        DeltaProjectionResult first = BuildDelta(context, 1, 2, 1);
        var changedRevision = new RevisionVector(2, 99, 2, 2, 2, 2, 1);
        NetEntityId id = NetEntityId.Parse("00000000000000010000000000000012");
        DeltaProjectionResult changed = context.BuildDelta(
            "snap-base", 1, 2, 1, changedRevision,
            new[] { new TombstoneView(id, 10) });

        Assert.True(first.Succeeded);
        Assert.True(changed.Succeeded);
        Assert.NotEqual(first.Delta!.Sequence, changed.Delta!.Sequence);
    }

    [Fact]
    public void ContextResyncDoesNotReuseOldIdempotencyIdentity()
    {
        using ReplicationContext context = CreateActiveContext();
        DeltaProjectionResult oldDelta = BuildDelta(context, 1, 2, 1);
        Assert.True(oldDelta.Succeeded);
        Assert.True(context.BeginResync().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-base", Revision(1)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-base", 1));
        Assert.True(context.CompleteResync().Succeeded);

        DeltaProjectionResult replacement = BuildDelta(context, 1, 2, 1);

        Assert.True(replacement.Succeeded);
        Assert.NotEqual(oldDelta.Delta!.Sequence, replacement.Delta!.Sequence);
    }

    [Fact]
    public void ContextTombstoneAuthorityFencesBindsAndExposesDestroy()
    {
        using ReplicationContext context = CreateActiveContext();
        NetEntityId added = NetEntityId.Parse("00000000000000010000000000000010");
        NetEntityId destroyed = NetEntityId.Parse("00000000000000010000000000000011");

        Assert.True(context.AddTombstone(added, 20));
        Assert.False(context.BindIdentity(AliveIdentity(added, 1)).Succeeded);

        Assert.True(context.BindIdentity(AliveIdentity(destroyed, 1)).Succeeded);
        MappingBindingResult destroy = context.DestroyIdentity(DestroyedIdentity(destroyed, 1));
        Assert.False(destroy.Succeeded);
        Assert.True(context.Tombstones.Contains(destroyed));

        TombstoneHorizonResult horizon = new(true, 21);
        Assert.Equal(2, context.CollectTombstones(22, horizon));
        Assert.False(context.Tombstones.Contains(added));
        Assert.False(context.Tombstones.Contains(destroyed));
        Assert.True(context.BindIdentity(AliveIdentity(added, 1)).Succeeded);
    }

    [Fact]
    public void ContextUnknownAndDuplicateDestroyKeepTheLongestSharedHorizon()
    {
        using ReplicationContext context = CreateActiveContext();
        NetEntityId id = NetEntityId.Parse("00000000000000010000000000000016");
        Assert.False(context.DestroyIdentity(id, 5, new TombstoneHorizonResult(false, 0)));
        Assert.True(context.Tombstones.Snapshot().TryGetValue(id, out ulong firstUntil));
        Assert.Equal(ulong.MaxValue, firstUntil);

        Assert.False(context.DestroyIdentity(id, 6, new TombstoneHorizonResult(true, 7)));
        Assert.True(context.Tombstones.Snapshot().TryGetValue(id, out ulong secondUntil));
        Assert.Equal(ulong.MaxValue, secondUntil);
    }

    [Fact]
    public void ContextTombstoneReleaseUpdatesBothViews()
    {
        using ReplicationContext context = CreateActiveContext();
        NetEntityId id = NetEntityId.Parse("00000000000000010000000000000013");
        Assert.True(context.AddTombstone(id, 10));
        Assert.True(context.Tombstones.Contains(id));
        Assert.True(context.Identities.IsTombstoned(id));

        Assert.True(context.ReleaseTombstone(id, 12, new TombstoneHorizonResult(true, 11)));

        Assert.False(context.Tombstones.Contains(id));
        Assert.False(context.Identities.IsTombstoned(id));
        Assert.True(context.BindIdentity(AliveIdentity(id, 1)).Succeeded);
    }

    [Fact]
    public void BindingPastTheSharedTombstoneHorizonReleasesBothViews()
    {
        using ReplicationContext context = CreateActiveContext();
        NetEntityId id = NetEntityId.Parse("00000000000000010000000000000015");
        Assert.True(context.AddTombstone(id, 10));

        Assert.True(context.BindIdentity(AliveIdentity(id, 1), 11).Succeeded);

        Assert.False(context.Tombstones.Contains(id));
        Assert.False(context.Identities.IsTombstoned(id));
    }

    [Fact]
    public void ContextResyncRetainsOneTombstoneHorizonAcrossBothViews()
    {
        using ReplicationContext context = CreateActiveContext();
        NetEntityId id = NetEntityId.Parse("00000000000000010000000000000014");
        Assert.True(context.AddTombstone(id, 10));
        Assert.True(context.BeginResync().Succeeded);

        Assert.True(context.Tombstones.Contains(id));
        Assert.True(context.Identities.IsTombstoned(id));

        Assert.True(context.BuildFullSnapshot("snap-replacement", Revision(1)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-replacement", 1));
        Assert.True(context.CompleteResync().Succeeded);
        Assert.True(context.Tombstones.Contains(id));
        Assert.True(context.Identities.IsTombstoned(id));
    }

    [Fact]
    public void InvalidRetryInputsRemainTypedRejections()
    {
        using ReplicationContext context = CreateActiveContext();

        DeltaProjectionResult malformedDelta = default;
        Exception? deltaException = Record.Exception(() => malformedDelta = context.BuildDelta(
            "snap-base", 1, 2, 1, Revision(2), new[] { default(TombstoneView) }));
        FullSnapshotProjectionResult malformedSnapshot = default;
        Exception? snapshotException = Record.Exception(() => malformedSnapshot = context.BuildFullSnapshot(null!, Revision(1)));

        Assert.Null(deltaException);
        Assert.Equal(ProjectionStatus.Rejected, malformedDelta.Status);
        Assert.Equal("ManifestMalformed", malformedDelta.Failure?.GeneratedErrorId);
        Assert.Null(snapshotException);
        Assert.Equal(ProjectionStatus.Rejected, malformedSnapshot.Status);
    }

    [Fact]
    public void DeltaIdempotencyRetentionIsBoundedByHistoryWindow()
    {
        var history = new DeltaHistory(new ReplicationBudget(1, 4096, 8, 4096));
        IdentityStoreToken token = history.CaptureToken();
        Assert.True(history.ResetForBaseline("snap-bound", 0, 0, token));
        token = history.CaptureToken();

        for (var index = 0; index < 32; index++)
        {
            DeltaRecord record = new("snap-bound", 0, 1, 1, 10, Hash)
            {
                IdempotencyKey = "key-" + index,
            };
            Assert.Equal(DeltaHistoryStatus.Accepted, history.Add(record, token));
            Assert.Equal(DeltaAckStatus.Acknowledged, history.Acknowledge("snap-bound", 1, 1, token));
            ReleaseHistory(history, "snap-bound");
        }

        FieldInfo mapField = typeof(DeltaHistory).GetField("_idempotency", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo orderField = typeof(DeltaHistory).GetField("_idempotencyOrder", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(((System.Collections.IDictionary)mapField.GetValue(history)!).Count <= 1);
        Assert.True(((System.Collections.ICollection)orderField.GetValue(history)!).Count <= 1);
    }

    [Fact]
    public void FullSnapshotIdempotencyRetentionIsBoundedByHistoryWindow()
    {
        var store = new BaselineStore(new ReplicationBudget(1, 4096, 8, 4096));
        IdentityStoreToken token = store.CaptureToken();
        for (var index = 0; index < 32; index++)
        {
            string id = "snap-bound-" + index;
            BaselineRecord record = new(id, (ulong)index, 10, Hash)
            {
                Sequence = (ulong)index + 1,
                IdempotencyKey = "key-" + index,
            };
            Assert.Equal(BaselineStoreStatus.Accepted, store.Add(record, token));
            Assert.Equal(BaselineAckStatus.Acknowledged, store.Ack(id, (ulong)index, token));
            Assert.True(store.Release(id, token));
        }

        FieldInfo mapField = typeof(BaselineStore).GetField("_idempotency", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo orderField = typeof(BaselineStore).GetField("_idempotencyOrder", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(((System.Collections.IDictionary)mapField.GetValue(store)!).Count <= 1);
        Assert.True(((System.Collections.ICollection)orderField.GetValue(store)!).Count <= 1);
    }

    [Fact]
    public void IdempotencyCachesRejectOversizedEntriesWithoutRetainingBytes()
    {
        var budget = new ReplicationBudget(1, 1, 8, 8192);
        var baseline = new BaselineStore(budget);
        IdentityStoreToken baselineToken = baseline.CaptureToken();
        Assert.Equal(BaselineStoreStatus.QueueFull,
            baseline.Add(new BaselineRecord("snap-budget", 1, 1, Hash) { IdempotencyKey = "key" }, baselineToken));

        var delta = new DeltaHistory(budget);
        IdentityStoreToken deltaToken = delta.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.QueueFull,
            delta.Add(new DeltaRecord("snap-budget", 1, 2, 1, 1, Hash) { IdempotencyKey = "key" }, deltaToken));

        var projection = new ReplicationProjection(budget);
        ulong beforeSequence = NextSequence(projection);
        FullSnapshotProjectionResult projected = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-budget", Revision(1), CreateMappings());

        object baselineMap = typeof(BaselineStore).GetField("_idempotency", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(baseline)!;
        object deltaMap = typeof(DeltaHistory).GetField("_idempotency", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(delta)!;
        long baselineBytes = (long)typeof(BaselineStore).GetField("_idempotencyBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(baseline)!;
        long deltaBytes = (long)typeof(DeltaHistory).GetField("_idempotencyBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(delta)!;
        object projectionCache = typeof(ReplicationProjection).GetField("_fullSnapshotIdentities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(projection)!;
        long projectionBytes = (long)projectionCache.GetType().GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(projectionCache)!;

        Assert.True(((System.Collections.IDictionary)baselineMap).Count <= 1);
        Assert.True(((System.Collections.IDictionary)deltaMap).Count <= 1);
        Assert.InRange(baselineBytes, 0, 1);
        Assert.InRange(deltaBytes, 0, 1);
        Assert.InRange(projectionBytes, 0, 1);
        Assert.False(projected.Succeeded);
        Assert.Equal(beforeSequence, NextSequence(projection));
    }

    [Fact]
    public void IdempotencyOverflowAdmissionIsAtomic()
    {
        var budget = new ReplicationBudget(2, long.MaxValue, 8, 8192);
        var baseline = new BaselineStore(budget);
        IdentityStoreToken baselineToken = baseline.CaptureToken();
        BaselineStoreStatus baselineStatus = default;
        Exception? baselineException = Record.Exception(() => baselineStatus = baseline.Add(
            new BaselineRecord("snap-overflow", 1, long.MaxValue, Hash) { IdempotencyKey = "key" }, baselineToken));

        var delta = new DeltaHistory(budget);
        IdentityStoreToken deltaToken = delta.CaptureToken();
        DeltaHistoryStatus deltaStatus = default;
        Exception? deltaException = Record.Exception(() => deltaStatus = delta.Add(
            new DeltaRecord("snap-overflow", 1, 2, 1, long.MaxValue, Hash) { IdempotencyKey = "key" }, deltaToken));

        Assert.Null(baselineException);
        Assert.Null(deltaException);
        Assert.Equal(BaselineStoreStatus.QueueFull, baselineStatus);
        Assert.Equal(DeltaHistoryStatus.QueueFull, deltaStatus);
        Assert.Equal(0, baseline.Count);
        Assert.Equal(0L, baseline.Bytes);
        Assert.Equal(0, delta.Count);
        Assert.Equal(0L, delta.Bytes);
        object baselineMap = typeof(BaselineStore).GetField("_idempotency", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(baseline)!;
        object deltaMap = typeof(DeltaHistory).GetField("_idempotency", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(delta)!;
        Assert.Equal(0, ((System.Collections.IDictionary)baselineMap).Count);
        Assert.Equal(0, ((System.Collections.IDictionary)deltaMap).Count);
    }

    [Fact]
    public void RotatedProjectionRequestsRequireDurableRetention()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(1, 1_000_000, 8, 1_000_000));
        FullSnapshotProjectionResult first = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-rotate-1", Revision(1), CreateMappings());
        ulong beforeSecond = NextSequence(projection);
        FullSnapshotProjectionResult second = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-rotate-2", Revision(2), CreateMappings());
        ulong afterSecond = NextSequence(projection);

        Assert.True(first.Succeeded, first.Failure?.Detail);
        Assert.False(second.Succeeded);
        Assert.Equal(ProjectionStatus.Retryable, second.Status);
        Assert.Equal("QueueFull", second.Failure?.GeneratedErrorId);
        Assert.Equal(beforeSecond, afterSecond);
        ulong beforeRetry = NextSequence(projection);
        FullSnapshotProjectionResult retry = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-rotate-2", Revision(2), CreateMappings());
        Assert.False(retry.Succeeded);
        Assert.Equal(ProjectionStatus.Retryable, retry.Status);
        Assert.Equal("QueueFull", retry.Failure?.GeneratedErrorId);
        Assert.Equal(beforeRetry, NextSequence(projection));
        ResetProjection(projection);
        FullSnapshotProjectionResult reopened = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-rotate-reopened", Revision(4), CreateMappings());
        Assert.True(reopened.Succeeded, reopened.Failure?.Detail);
    }

    [Fact]
    public void RotatedAcknowledgedDeltaRequestsRequireDurableRetention()
    {
        using ReplicationContext context = CreateActiveContext(historyWindow: 1);
        DeltaProjectionResult first = BuildDelta(context, 1, 2, 1);
        Assert.True(first.Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckDelta("snap-base", first.Delta!.Sequence, 2));
        DeltaProjectionResult second = BuildDelta(context, 2, 3, 2);

        Assert.False(second.Succeeded);
        Assert.Equal(ProjectionStatus.Retryable, second.Status);
        Assert.Equal("QueueFull", second.Failure?.GeneratedErrorId);
        ulong beforeRetry = NextSequence(GetProjection(context));
        DeltaProjectionResult retry = BuildDelta(context, 2, 3, 2);
        Assert.False(retry.Succeeded);
        Assert.Equal(ProjectionStatus.Retryable, retry.Status);
        Assert.Equal("QueueFull", retry.Failure?.GeneratedErrorId);
        Assert.Equal(beforeRetry, NextSequence(GetProjection(context)));
    }

    [Fact]
    public void SameGenerationSourceRevisionOrdersAliveAndDestroy()
    {
        NetEntityId id = NetEntityId.Parse("00000000000000010000000000000021");
        var table = new NetEntityMappingTable();
        IdentityStoreToken token = table.CaptureToken();
        EntityIdentity current = new(id.Value, "server-a", 7, 15, 5, "4:5",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive, null, null, 20, null);
        EntityIdentity staleDestroy = new(id.Value, "server-a", 7, 15, 5, null,
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Destroyed, 30, null, 10, null);

        Assert.True(table.Bind(current, token).Succeeded);
        MappingBindingResult destroy = table.Bind(staleDestroy, token);

        Assert.False(destroy.Succeeded);
        Assert.Equal("RevisionConflict", destroy.GeneratedErrorId);
        Assert.True(table.TryResolveLocal(id, 5, out string? local, token));
        Assert.Equal("4:5", local);

        EntityIdentity newestDestroy = new(id.Value, "server-a", 7, 15, 5, null,
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Destroyed, 40, null, 30, null);
        Assert.False(table.Bind(newestDestroy, token).Succeeded);
        EntityIdentity delayedAlive = new(id.Value, "server-a", 7, 15, 5, "4:5",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive, null, null, 25, null);
        Assert.False(table.Bind(delayedAlive, 41, token).Succeeded);
        Assert.Equal(0, table.Count);
        EntityIdentity newerAlive = new(id.Value, "server-a", 7, 15, 5, "4:5",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive, null, null, 31, null);
        MappingBindingResult reboundResult = table.Bind(newerAlive, 41, token);
        Assert.True(reboundResult.Succeeded, reboundResult.GeneratedErrorId + ":" + reboundResult.Detail);
        Assert.True(table.TryResolveLocal(id, 5, out string? rebound, token));
        Assert.Equal("4:5", rebound);
    }

    [Fact]
    public void AdmissionIdentityMustMatchEnvelopeAndExplicitExpectedIdentity()
    {
        const string body = "{\"role\":\"Client\"}";
        var envelope = new ReplicationEnvelope(
            "session-evil", "product", "release-1", 1, 256, 1,
            ReplicationEnvelopeMessageType.Handshake, ReplicationEnvelopeReliability.Reliable,
            new ReplicationEnvelopeIntegrity(ReplicationEnvelopeIntegrityAlgorithm.SHA256, Sha256(body)),
            "trace-1",
            new ReplicationEnvelopeTransportPolicy(65536, 4096, 32,
                ReplicationEnvelopeTransportPolicyAuthBinding.SessionAdmission,
                ReplicationEnvelopeTransportPolicyErrorClass.Rejectable),
            new OpaqueJson(body));
        var admission = new ReplicationAdmissionContext(
            "session-good", "product", "release-1", "Handshake",
            "Client", new[] { "replicate" }, 1,
            "session-good", "product", "release-1", "Client", new[] { "replicate" }, 1);

        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            envelope, admission, "session-evil", "product", "release-1", Hash, 1);

        Assert.False(result.Succeeded);
        Assert.Equal(ReplicationValidationCode.Invalid, result.Code);
        Assert.Equal("SessionMismatch", result.GeneratedErrorId);
    }

    [Fact]
    public void DuplicateBaselineAckAfterActivationReturnsOriginalResult()
    {
        using ReplicationContext context = CreateContext();
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult snapshot = context.BuildFullSnapshot("snap-ack-retry", Revision(1));
        Assert.True(snapshot.Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-ack-retry", 1));
        Assert.True(context.Activate().Succeeded);

        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-ack-retry", 1));
        Assert.Equal(ReplicationContextState.Active, context.State);
    }

    [Fact]
    public void DirectFullSnapshotRotationCannotForgetSuccessfulIdentity()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(1, 1_000_000, 8, 1_000_000));
        MappingSetView mappings = CreateMappings();
        FullSnapshotProjectionResult first = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-followup-a", Revision(1), mappings);
        ulong beforeSecond = NextSequence(projection);
        FullSnapshotProjectionResult second = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-followup-b", Revision(2), mappings);
        ulong afterSecond = NextSequence(projection);
        FullSnapshotProjectionResult third = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-followup-c", Revision(3), mappings);
        ulong afterThird = NextSequence(projection);
        FullSnapshotProjectionResult retrySecond = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-followup-b", Revision(2), mappings);

        Assert.True(first.Succeeded, first.Failure?.Detail);
        if (!second.Succeeded)
        {
            Assert.Equal(beforeSecond, afterSecond);
            Assert.False(third.Succeeded);
            Assert.False(retrySecond.Succeeded);
            return;
        }

        Assert.True(third.Succeeded || third.Failure is not null, third.Failure?.Detail);
        if (!third.Succeeded) Assert.Equal(afterSecond, afterThird);
        Assert.True(retrySecond.Succeeded, retrySecond.Failure?.Detail);
        Assert.Equal(second.Snapshot!.Sequence, retrySecond.Snapshot!.Sequence);
    }

    [Fact]
    public void DirectDeltaRotationCannotForgetSuccessfulIdentity()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(1, 1_000_000, 8, 1_000_000));
        MappingSetView mappings = CreateMappings();
        DeltaProjectionResult first = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-followup", 1, 2, 1,
            Revision(2), mappings, Array.Empty<TombstoneView>());
        ulong beforeSecond = NextSequence(projection);
        DeltaProjectionResult second = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-followup", 2, 3, 2,
            Revision(3), mappings, Array.Empty<TombstoneView>());
        ulong afterSecond = NextSequence(projection);
        DeltaProjectionResult third = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-followup", 3, 4, 3,
            Revision(4), mappings, Array.Empty<TombstoneView>());
        ulong afterThird = NextSequence(projection);
        DeltaProjectionResult retrySecond = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-followup", 2, 3, 2,
            Revision(3), mappings, Array.Empty<TombstoneView>());

        Assert.True(first.Succeeded, first.Failure?.Detail);
        if (!second.Succeeded)
        {
            Assert.Equal(beforeSecond, afterSecond);
            Assert.False(third.Succeeded);
            Assert.False(retrySecond.Succeeded);
            return;
        }

        Assert.True(third.Succeeded || third.Failure is not null, third.Failure?.Detail);
        if (!third.Succeeded) Assert.Equal(afterSecond, afterThird);
        Assert.True(retrySecond.Succeeded, retrySecond.Failure?.Detail);
        Assert.Equal(second.Delta!.Sequence, retrySecond.Delta!.Sequence);
    }

    [Fact]
    public void OversizedIdentityIsRejectedBeforeSequenceAllocationAndRetry()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(8, 1, 8, 1_000_000));
        MappingSetView mappings = CreateMappings();
        ulong before = NextSequence(projection);
        FullSnapshotProjectionResult first = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-oversized-followup", Revision(1), mappings);
        ulong afterFirst = NextSequence(projection);
        FullSnapshotProjectionResult retry = projection.BuildFullSnapshot(
            "session-1", "product", "release-1", "snap-oversized-followup", Revision(1), mappings);

        Assert.False(first.Succeeded);
        Assert.Equal(ProjectionStatus.Retryable, first.Status);
        Assert.Equal("QueueFull", first.Failure?.GeneratedErrorId);
        Assert.Equal(before, afterFirst);
        Assert.False(retry.Succeeded);
        Assert.Equal(ProjectionStatus.Retryable, retry.Status);
        Assert.Equal("QueueFull", retry.Failure?.GeneratedErrorId);
        Assert.Equal(afterFirst, NextSequence(projection));

        var deltaProjection = new ReplicationProjection(new ReplicationBudget(8, 1, 8, 1_000_000));
        ulong beforeDelta = NextSequence(deltaProjection);
        DeltaProjectionResult delta = deltaProjection.BuildDelta(
            "session-1", "product", "release-1", "snap-oversized-followup", 1, 2, 1,
            Revision(2), mappings, Array.Empty<TombstoneView>());
        ulong afterDelta = NextSequence(deltaProjection);
        DeltaProjectionResult deltaRetry = deltaProjection.BuildDelta(
            "session-1", "product", "release-1", "snap-oversized-followup", 1, 2, 1,
            Revision(2), mappings, Array.Empty<TombstoneView>());
        Assert.False(delta.Succeeded);
        Assert.Equal(ProjectionStatus.Retryable, delta.Status);
        Assert.Equal("QueueFull", delta.Failure?.GeneratedErrorId);
        Assert.Equal(beforeDelta, afterDelta);
        Assert.False(deltaRetry.Succeeded);
        Assert.Equal(ProjectionStatus.Retryable, deltaRetry.Status);
        Assert.Equal("QueueFull", deltaRetry.Failure?.GeneratedErrorId);
        Assert.Equal(afterDelta, NextSequence(deltaProjection));
    }

    [Fact]
    public void ContextBaselineRotationCannotForgetSuccessfulIdentity()
    {
        using ReplicationContext context = CreateContext(historyWindow: 1);
        Assert.True(context.BeginSnapshot().Succeeded);
        FullSnapshotProjectionResult first = context.BuildFullSnapshot("snap-context-followup-a", Revision(1));
        Assert.True(first.Succeeded, first.Failure?.Detail);
        Assert.Equal(BaselineAckStatus.Acknowledged, AckStoredBaseline(context, "snap-context-followup-a", 1));

        ReplicationProjection projection = GetProjection(context);
        ulong beforeSecond = NextSequence(projection);
        FullSnapshotProjectionResult second = context.BuildFullSnapshot("snap-context-followup-b", Revision(2));
        ulong afterSecond = NextSequence(projection);
        if (!second.Succeeded)
        {
            Assert.Equal(ProjectionStatus.Retryable, second.Status);
            Assert.Equal("QueueFull", second.Failure?.GeneratedErrorId);
            Assert.Equal(beforeSecond, afterSecond);
            Assert.True(context.Baselines.TryGet("snap-context-followup-a", out BaselineRecord? retained));
            Assert.True(retained!.Acknowledged);
            return;
        }

        Assert.Equal(BaselineAckStatus.Acknowledged, AckStoredBaseline(context, "snap-context-followup-b", 2));
        FullSnapshotProjectionResult third = context.BuildFullSnapshot("snap-context-followup-c", Revision(3));
        if (third.Succeeded)
            Assert.Equal(BaselineAckStatus.Acknowledged, AckStoredBaseline(context, "snap-context-followup-c", 3));
        FullSnapshotProjectionResult retrySecond = context.BuildFullSnapshot("snap-context-followup-b", Revision(2));

        Assert.True(retrySecond.Succeeded, retrySecond.Failure?.Detail);
        Assert.Equal(second.Snapshot!.Sequence, retrySecond.Snapshot!.Sequence);
    }

    [Fact]
    public void ContextDeltaRotationAfterAcknowledgementCannotForgetSuccessfulIdentity()
    {
        using ReplicationContext context = CreateActiveContext(historyWindow: 1);
        DeltaProjectionResult first = BuildDelta(context, 1, 2, 1);
        Assert.True(first.Succeeded, first.Failure?.Detail);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckDelta("snap-base", first.Delta!.Sequence, 2));

        DeltaProjectionResult second = BuildDelta(context, 2, 3, 2);
        if (!second.Succeeded)
        {
            Assert.Equal(ProjectionStatus.Retryable, second.Status);
            Assert.Equal("QueueFull", second.Failure?.GeneratedErrorId);
            Assert.Equal(0, context.Deltas.Count);
            return;
        }
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckDelta("snap-base", second.Delta!.Sequence, 3));
        DeltaProjectionResult third = BuildDelta(context, 3, 4, 3);
        if (third.Succeeded)
            Assert.Equal(BaselineAckStatus.Acknowledged, context.AckDelta("snap-base", third.Delta!.Sequence, 4));
        DeltaProjectionResult retrySecond = BuildDelta(context, 2, 3, 2);

        Assert.True(retrySecond.Succeeded, retrySecond.Failure?.Detail);
        Assert.Equal(second.Delta.Sequence, retrySecond.Delta!.Sequence);
    }

    [Fact]
    public void EvictedDeltaIdentityStillRejectsChangedPayloadWithSameKey()
    {
        var history = new DeltaHistory(new ReplicationBudget(1, 1_000_000, 8, 1_000_000));
        IdentityStoreToken token = history.CaptureToken();
        Assert.True(history.ResetForBaseline("base-followup-a", 0, 0, token));
        token = history.CaptureToken();
        DeltaRecord first = new("base-followup-a", 0, 1, 1, 10, Hash) { IdempotencyKey = "followup-same-key" };
        Assert.Equal(DeltaHistoryStatus.Accepted, history.Add(first, token));
        Assert.Equal(DeltaAckStatus.Acknowledged, history.Acknowledge("base-followup-a", 1, 1, token));

        InvokeRegisterBaseline(history, "base-followup-b", 0, 0);
        DeltaRecord second = new("base-followup-b", 0, 1, 1, 10, Hash) { IdempotencyKey = "followup-other-key" };
        DeltaHistoryStatus secondStatus = history.Add(second, token);
        if (secondStatus == DeltaHistoryStatus.Accepted)
        {
            Assert.Equal(DeltaAckStatus.Acknowledged, history.Acknowledge("base-followup-b", 1, 1, token));
            InvokeRegisterBaseline(history, "base-followup-a", 0, 0);
            DeltaRecord changed = new("base-followup-a", 0, 1, 1, 11, Hash) { IdempotencyKey = "followup-same-key" };
            Assert.Equal(DeltaHistoryStatus.RevisionConflict, history.Add(changed, token));
        }
        else
        {
            Assert.Equal(DeltaHistoryStatus.QueueFull, secondStatus);
        }

        // With room for both identities, a changed payload must still collide
        // with the retained key rather than being mistaken for a replay.
        var retainedHistory = new DeltaHistory(new ReplicationBudget(2, 1_000_000, 8, 1_000_000));
        IdentityStoreToken retainedToken = retainedHistory.CaptureToken();
        DeltaRecord retained = new("base-retained", 0, 1, 1, 10, Hash) { IdempotencyKey = "retained-same-key" };
        DeltaRecord changedRetained = new("base-retained", 0, 1, 1, 11, Hash) { IdempotencyKey = "retained-same-key" };
        Assert.Equal(DeltaHistoryStatus.Accepted, retainedHistory.Add(retained, retainedToken));
        Assert.Equal(DeltaHistoryStatus.RevisionConflict, retainedHistory.Add(changedRetained, retainedToken));
    }

    private static RevisionVector Revision(ulong revision) =>
        new(revision, revision, revision, revision, revision, revision, 1);

    private static ulong NextSequence(ReplicationProjection projection) =>
        (ulong)typeof(ReplicationProjection).GetField("_nextSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(projection)!;

    private static void ResetProjection(ReplicationProjection projection) =>
        typeof(ReplicationProjection).GetMethod("ResetIdempotency", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(projection, null);

    private static ReplicationProjection GetProjection(ReplicationContext context) =>
        (ReplicationProjection)typeof(ReplicationContext).GetField("_projection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;

    private static BaselineAckStatus AckStoredBaseline(ReplicationContext context, string snapshotId, ulong revision)
    {
        var store = (BaselineStore)typeof(ReplicationContext)
            .GetField("_baselineStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context)!;
        return (BaselineAckStatus)typeof(BaselineStore)
            .GetMethod("Ack", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(ulong) }, null)!
            .Invoke(store, new object[] { snapshotId, revision })!;
    }

    private static void InvokeRegisterBaseline(DeltaHistory history, string baseSnapshotId, ulong sequence, ulong revision) =>
        typeof(DeltaHistory)
            .GetMethod("RegisterBaseline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(history, new object[] { baseSnapshotId, sequence, revision });

    private static void ReleaseHistory(DeltaHistory history, string baseSnapshotId) =>
        typeof(DeltaHistory)
            .GetMethod("Release", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null)!
            .Invoke(history, new object[] { baseSnapshotId });

    private static DeltaProjectionResult BuildDelta(ReplicationContext context, ulong from, ulong to, ulong confirmation) =>
        context.BuildDelta("snap-base", from, to, confirmation, Revision(to), Array.Empty<TombstoneView>());

    private static ReplicationContext CreateActiveContext(int historyWindow = 8)
    {
        ReplicationContext context = CreateContext(historyWindow);
        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-base", Revision(1)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-base", 1));
        Assert.True(context.Activate().Succeeded);
        return context;
    }

    private static EntityIdentity ProvisionalIdentity(NetEntityId id) =>
        new(id.Value, "client-provisional", 7, 15, 1, "6:1",
            EntityIdentityNamespace.Provisional, EntityIdentityLifecycle.Alive,
            null, null, null, null);

    private static EntityIdentity AuthoritativeIdentity(NetEntityId id) =>
        new(id.Value, "server-a", 7, 15, 1, "7:1",
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, null, null);

    private static EntityIdentity AliveIdentity(NetEntityId id, ulong generation) =>
        new(id.Value, "server-a", 7, 15, generation, "4:" + generation,
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, 10, null);

    private static EntityIdentity DestroyedIdentity(NetEntityId id, ulong generation) =>
        new(id.Value, "server-a", 7, 15, generation, "4:" + generation,
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Destroyed,
            20, null, 10, null);

    private static ReplicationContext CreateContext(int historyWindow = 8) =>
        new("session-1", "product", "release-1", CreateMappings(),
            new ReplicationBudget(historyWindow, 8192, 16, 8192));

    private static MappingSetView CreateMappings()
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        return mappings.View;
    }

    private static string Sha256(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(64);
        foreach (byte item in digest) builder.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

}
