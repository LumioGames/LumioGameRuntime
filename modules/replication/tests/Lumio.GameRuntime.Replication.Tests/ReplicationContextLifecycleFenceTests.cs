using System;
using System.Threading;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ReplicationContextLifecycleFenceTests
{
    private static readonly NetEntityId Id1 = NetEntityId.Parse("00000000000000010000000000000001");
    private static readonly NetEntityId Id2 = NetEntityId.Parse("00000000000000010000000000000002");
    private static readonly NetEntityId Id3 = NetEntityId.Parse("00000000000000010000000000000003");
    private static readonly NetEntityId Id4 = NetEntityId.Parse("00000000000000010000000000000004");

    [Fact]
    public void NonDefaultGenerationInitializesEveryStore()
    {
        using ReplicationContext context = CreateContext(7);
        IdentityStoreToken token = context.CaptureWorkToken();

        Assert.Equal(7UL, context.ConnectionGeneration);
        Assert.Equal(1UL, context.WorkEpoch);
        AssertAllStoreGenerations(context, 7);
        Assert.All(StoreEpochs(context), epoch => Assert.Equal(1UL, epoch));
        Assert.True(context.IsWorkTokenCurrent(token));
    }

    [Fact]
    public void GenerationAdvanceClearsEveryGenerationScopedStore()
    {
        using ReplicationContext context = CreateActiveContext(7, "snap-old");
        IdentityStoreToken oldToken = context.CaptureWorkToken();
        PopulateRemainingStores(context, oldToken);
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, StoreCounts(context));

        Assert.True(context.TryAdvanceConnectionGeneration(7, out ulong nextGeneration));

        Assert.Equal(8UL, nextGeneration);
        Assert.Equal(8UL, context.ConnectionGeneration);
        Assert.Equal(1UL, context.WorkEpoch);
        AssertAllStoreGenerations(context, 8);
        Assert.Equal(new[] { 0, 0, 0, 0, 0 }, StoreCounts(context));
        Assert.False(context.IsWorkTokenCurrent(oldToken));

        IdentityStoreToken currentToken = context.CaptureWorkToken();
        Assert.True(context.BindIdentity(AliveIdentity(Id1, 2), currentToken).Succeeded);
    }

    [Fact]
    public void GenerationAdvanceCannotReuseOldAcknowledgedBaseline()
    {
        using ReplicationContext context = CreateActiveContext(7, "snap-g");

        Assert.True(context.TryAdvanceConnectionGeneration(7, out ulong nextGeneration));
        DeltaProjectionResult result = context.BuildDelta(
            "snap-g", 1, 2, 1, Revision(2), Array.Empty<TombstoneView>());

        Assert.Equal(8UL, nextGeneration);
        Assert.Equal(ProjectionStatus.Rejected, result.Status);
        Assert.Equal("SnapshotBaseMismatch", result.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void SharedScopePreventsPartialStoreGenerationAdvance()
    {
        using ReplicationContext context = CreateContext(7);
        IdentityStoreToken token = context.CaptureWorkToken();

        Assert.True(context.TryAdvanceConnectionGeneration(7, out ulong nextGeneration));

        Assert.Equal(8UL, nextGeneration);
        AssertAllStoreGenerations(context, 8);
        Assert.All(StoreEpochs(context), epoch => Assert.Equal(1UL, epoch));
        Assert.False(context.IsWorkTokenCurrent(token));
    }

    [Fact]
    public void RejectedGenerationAdvancesLeaveUsableStateUnchanged()
    {
        using ReplicationContext context = CreateContext(7);
        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-retained", Revision(1)).Succeeded);
        IdentityStoreToken token = context.CaptureWorkToken();

        Assert.False(context.TryAdvanceConnectionGeneration(6, out ulong wrongExpected));
        Assert.Equal(7UL, wrongExpected);
        Assert.True(context.IsWorkTokenCurrent(token));
        Assert.Equal(1, context.Baselines.Count);

        (bool Succeeded, ulong Next) nonOwner = RunOnDedicatedNonOwnerThread(() =>
        {
            bool succeeded = context.TryAdvanceConnectionGeneration(7, out ulong next);
            return (succeeded, next);
        });
        Assert.False(nonOwner.Succeeded);
        Assert.Equal(7UL, nonOwner.Next);
        Assert.True(context.IsWorkTokenCurrent(token));
        Assert.Equal(1, context.Baselines.Count);

        using ReplicationContext max = CreateContext(ulong.MaxValue);
        IdentityStoreToken maxToken = max.CaptureWorkToken();
        Assert.False(max.TryAdvanceConnectionGeneration(ulong.MaxValue, out ulong maxNext));
        Assert.Equal(ulong.MaxValue, maxNext);
        Assert.True(max.IsWorkTokenCurrent(maxToken));

        context.Dispose();
        ulong[] terminalGenerations = StoreGenerations(context);
        Assert.False(context.TryAdvanceConnectionGeneration(7, out ulong terminalNext));
        Assert.Equal(7UL, terminalNext);
        Assert.Equal(terminalGenerations, StoreGenerations(context));
    }

    [Fact]
    public void DisposeTerminatesAllRetainedStoreViews()
    {
        ReplicationContext context = CreateActiveContext(7, "snap-dispose");
        IdentityStoreToken token = context.CaptureWorkToken();
        PopulateRemainingStores(context, token);
        BaselineStoreView baselines = context.Baselines;
        DeltaHistoryView deltas = context.Deltas;
        NetEntityMappingView identities = context.Identities;
        ProvisionalRemapView remaps = context.ProvisionalRemaps;
        TombstoneRegistryView tombstones = context.Tombstones;

        context.Dispose();

        Assert.Equal(ReplicationContextState.Closed, context.State);
        Assert.Equal(IdentityStoreState.Closed, baselines.State);
        Assert.Equal(IdentityStoreState.Closed, deltas.State);
        Assert.Equal(IdentityStoreState.Closed, identities.State);
        Assert.Equal(IdentityStoreState.Closed, remaps.State);
        Assert.Equal(IdentityStoreState.Closed, tombstones.State);
        Assert.Equal(new[] { 0, 0, 0, 0, 0 }, StoreCounts(context));
        Assert.False(context.IsWorkTokenCurrent(token));
        Assert.False(context.BuildFullSnapshot("snap-late", Revision(2), token).Succeeded);
        Assert.False(context.BindIdentity(AliveIdentity(Id1, 2), token).Succeeded);
    }

    [Fact]
    public void GracefulCloseTerminatesAllRetainedStoreViews()
    {
        using ReplicationContext context = CreateActiveContext(7, "snap-close");
        IdentityStoreToken token = context.CaptureWorkToken();
        PopulateRemainingStores(context, token);
        Assert.True(context.Drain().Succeeded);

        ReplicationContextTransitionResult closed = context.Close();

        Assert.True(closed.Succeeded);
        Assert.Equal(ReplicationContextState.Closed, closed.State);
        Assert.Equal(new[] { 0, 0, 0, 0, 0 }, StoreCounts(context));
        Assert.All(StoreStates(context), state => Assert.Equal(IdentityStoreState.Closed, state));
    }

    [Fact]
    public void TerminalCleanupIsIdempotent()
    {
        ReplicationContext closed = CreateActiveContext(7, "snap-idempotent");
        Assert.True(closed.Drain().Succeeded);
        Assert.True(closed.Close().Succeeded);
        ulong[] closedGenerations = StoreGenerations(closed);

        closed.Dispose();
        closed.Dispose();

        Assert.Equal(closedGenerations, StoreGenerations(closed));
        Assert.All(StoreStates(closed), state => Assert.Equal(IdentityStoreState.Closed, state));

        ReplicationContext disposed = CreateContext(11);
        disposed.Dispose();
        ulong[] disposedGenerations = StoreGenerations(disposed);
        disposed.Dispose();
        Assert.Equal(disposedGenerations, StoreGenerations(disposed));
    }

    [Fact]
    public void LateCompletionUsesTheTokenCapturedWithItsWorkItem()
    {
        using ReplicationContext advanced = CreateContext(7);
        IdentityStoreToken generationToken = advanced.CaptureWorkToken();
        EntityIdentity scheduledIdentity = AliveIdentity(Id1, 1);
        Assert.True(advanced.TryAdvanceConnectionGeneration(7, out _));

        FullSnapshotProjectionResult oldSnapshot = advanced.BuildFullSnapshot(
            "snap-late", Revision(1), generationToken);
        MappingBindingResult oldIdentity = advanced.BindIdentity(scheduledIdentity, generationToken);

        Assert.Equal("StaleConnectionGeneration", oldSnapshot.Failure?.GeneratedErrorId);
        Assert.Equal("StaleConnectionGeneration", oldIdentity.GeneratedErrorId);
        Assert.Equal(new[] { 0, 0, 0, 0, 0 }, StoreCounts(advanced));

        ReplicationContext disposed = CreateContext(7);
        IdentityStoreToken disposedToken = disposed.CaptureWorkToken();
        disposed.Dispose();
        Assert.False(disposed.BuildFullSnapshot("snap-disposed", Revision(1), disposedToken).Succeeded);
        Assert.False(disposed.BindIdentity(AliveIdentity(Id2, 1), disposedToken).Succeeded);
    }

    private static void PopulateRemainingStores(ReplicationContext context, IdentityStoreToken token)
    {
        if (context.Deltas.Count == 0)
        {
            DeltaProjectionResult delta = context.BuildDelta(
                context.Baselines.Snapshot()[0].SnapshotId,
                1, 2, 1, Revision(2), Array.Empty<TombstoneView>(), token);
            Assert.True(delta.Succeeded, delta.Failure?.Detail);
        }

        Assert.True(context.BindIdentity(AliveIdentity(Id1, 1), token).Succeeded);
        Assert.True(context.AddProvisionalRemap(
            ProvisionalIdentity(Id2, 1), AliveIdentity(Id3, 1), token).Succeeded);
        Assert.True(context.AddTombstone(Id4, 30, token));
    }

    private static EntityIdentity AliveIdentity(NetEntityId id, ulong generation) =>
        new(id.Value, "server-a", 7, 15, generation, "4:" + generation,
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive,
            null, null, 10, null);

    private static EntityIdentity ProvisionalIdentity(NetEntityId id, ulong generation) =>
        new(id.Value, "client-a", 7, 15, generation, "4:" + generation,
            EntityIdentityNamespace.Provisional, EntityIdentityLifecycle.Alive,
            null, null, 10, null);

    private static RevisionVector Revision(ulong revision) =>
        new(revision, revision, revision, revision, revision, revision, 1);

    private static int[] StoreCounts(ReplicationContext context) =>
        new[]
        {
            context.Baselines.Count,
            context.Deltas.Count,
            context.Identities.Count,
            context.ProvisionalRemaps.Count,
            context.Tombstones.Count,
        };

    private static IdentityStoreState[] StoreStates(ReplicationContext context) =>
        new[]
        {
            context.Baselines.State,
            context.Deltas.State,
            context.Identities.State,
            context.ProvisionalRemaps.State,
            context.Tombstones.State,
        };

    private static ulong[] StoreGenerations(ReplicationContext context) =>
        new[]
        {
            context.Baselines.Generation,
            context.Deltas.Generation,
            context.Identities.Generation,
            context.ProvisionalRemaps.Generation,
            context.Tombstones.Generation,
        };

    private static ulong[] StoreEpochs(ReplicationContext context) =>
        new[]
        {
            context.Baselines.WorkEpoch,
            context.Deltas.WorkEpoch,
            context.Identities.WorkEpoch,
            context.ProvisionalRemaps.WorkEpoch,
            context.Tombstones.WorkEpoch,
        };

    private static void AssertAllStoreGenerations(ReplicationContext context, ulong expected) =>
        Assert.All(StoreGenerations(context), generation => Assert.Equal(expected, generation));

    private static ReplicationContext CreateActiveContext(ulong generation, string snapshotId)
    {
        ReplicationContext context = CreateContext(generation);
        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot(snapshotId, Revision(1)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline(snapshotId, 1));
        Assert.True(context.Activate().Succeeded);
        return context;
    }

    private static ReplicationContext CreateContext(ulong generation)
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        return new ReplicationContext(
            "session-1", "product", "release-1", mappings.View,
            new ReplicationBudget(16, 8192, 16, 8192), generation);
    }

    // Task.Run can resume on the owner ManagedThreadId under xunit collection parallel.
    private static T RunOnDedicatedNonOwnerThread<T>(Func<T> work)
    {
        int ownerThreadId = Environment.CurrentManagedThreadId;
        T result = default!;
        Exception? error = null;
        int workerThreadId = ownerThreadId;
        for (int attempt = 0; attempt < 3 && workerThreadId == ownerThreadId; attempt++)
        {
            error = null;
            var worker = new Thread(() =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                if (workerThreadId == ownerThreadId)
                    return;
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.NotEqual(ownerThreadId, workerThreadId);
        if (error is not null)
            throw error;
        return result;
    }
}
