using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Validation;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class FinalAggregateRegressionTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly NetEntityId Id1 = NetEntityId.Parse("00000000000000010000000000000001");
    private static readonly NetEntityId Id2 = NetEntityId.Parse("00000000000000010000000000000002");
    private static readonly NetEntityId Id3 = NetEntityId.Parse("00000000000000010000000000000003");
    private static readonly NetEntityId Id4 = NetEntityId.Parse("00000000000000010000000000000004");

    [Fact]
    public void EqualGenerationContextsRejectForeignWorkToken()
    {
        using ReplicationContext first = CreateContext(7);
        using ReplicationContext second = CreateContext(7);
        IdentityStoreToken firstToken = CaptureWorkToken(first);
        IdentityStoreToken secondToken = CaptureWorkToken(second);
        Assert.True(second.BeginSnapshot().Succeeded);

        Assert.True(IsWorkTokenCurrent(first, firstToken));
        Assert.True(IsWorkTokenCurrent(second, secondToken));
        Assert.False(IsWorkTokenCurrent(second, firstToken));
        Assert.False(TryStageSnapshot(second, "snap-foreign", firstToken));
        Assert.False(TryStageDelta(second, "snap-foreign", firstToken));
        Assert.False(TryBindIdentity(second, AliveIdentity(Id1, 1), firstToken));
        Assert.False(TryAddRemap(second, ProvisionalIdentity(Id2, 1), AliveIdentity(Id3, 1), firstToken));
        Assert.False(TryAddTombstone(second, Id4, 20, firstToken));
        Assert.True(TryStageSnapshot(second, "snap-own", secondToken));
        Assert.True(TryBindIdentity(second, AliveIdentity(Id1, 1), secondToken));
        Assert.True(TryAddRemap(second, ProvisionalIdentity(Id2, 1), AliveIdentity(Id3, 1), secondToken));
        Assert.True(TryAddTombstone(second, Id4, 20, secondToken));
    }

    [Fact]
    public void PreResyncWorkCannotPublishAfterReplacementActivation()
    {
        using ReplicationContext context = CreateActiveContext(7, "snap-old", historyWindow: 1);
        IdentityStoreToken oldToken = CaptureWorkToken(context);
        ulong oldEpoch = WorkEpoch(context);

        Assert.True(context.BeginResync().Succeeded);
        Assert.Equal(7UL, context.ConnectionGeneration);
        Assert.Equal(oldEpoch + 1, WorkEpoch(context));
        FullSnapshotProjectionResult replacement = context.BuildFullSnapshot(
            "snap-new", new RevisionVector(2, 2, 2, 2, 2, 2, 1));
        Assert.True(replacement.Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-new", 2));
        Assert.True(context.CompleteResync().Succeeded);

        Assert.False(TryStageSnapshot(context, "snap-late", oldToken));
        Assert.False(TryStageDelta(context, "snap-old", oldToken));
        Assert.False(TryBindIdentity(context, AliveIdentity(Id1, 1), oldToken));
        Assert.True(context.Baselines.TryGetAcknowledged(out BaselineRecord? acknowledged));
        Assert.Equal("snap-new", acknowledged!.SnapshotId);
        Assert.False(context.Baselines.TryGet("snap-late", out _));
        Assert.Empty(context.Deltas.Snapshot());
        Assert.Empty(context.Identities.Snapshot());

        DeltaProjectionResult current = context.BuildDelta(
            "snap-new", 2, 3, 2, new RevisionVector(3, 3, 3, 3, 3, 3, 1),
            Array.Empty<TombstoneView>());
        Assert.True(current.Succeeded, current.Failure?.Detail);
    }

    [Fact]
    public void ContextBaselineViewCannotAcknowledgeOrMutate()
    {
        PropertyInfo property = typeof(ReplicationContext).GetProperty(nameof(ReplicationContext.Baselines))!;

        Assert.Equal("BaselineStoreView", property.PropertyType.Name);
        Assert.False(typeof(BaselineStore).IsAssignableFrom(property.PropertyType));
        AssertNoPublicMethods(property.PropertyType, "Ack", "Acknowledge", "Add", "Stage", "Release", "Reset", "Clear", "Invalidate", "Close");

        using ReplicationContext context = CreateContext(1);
        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot("snap-ack", Revision(1)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.False(context.Activate().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline("snap-ack", 1));
        Assert.True(context.Activate().Succeeded);
    }

    [Fact]
    public void FaultClearsAndInvalidatesAllFiveStores()
    {
        using ReplicationContext context = CreateActiveContext(7, "snap-fault", historyWindow: 8);
        IdentityStoreToken token = CaptureWorkToken(context);
        Assert.True(context.BuildDelta(
            "snap-fault", 1, 2, 1, Revision(2), Array.Empty<TombstoneView>()).Succeeded);
        Assert.True(TryBindIdentity(context, AliveIdentity(Id1, 1), token));
        Assert.True(TryAddRemap(context, ProvisionalIdentity(Id2, 1), AliveIdentity(Id3, 1), token));
        Assert.True(TryAddTombstone(context, Id4, 20, token));
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, StoreCounts(context));

        ReplicationContextTransitionResult faulted = context.Fault();

        Assert.True(faulted.Succeeded);
        Assert.Equal(ReplicationContextState.Faulted, context.State);
        Assert.Equal(new[] { 0, 0, 0, 0, 0 }, StoreCounts(context));
        Assert.All(StoreStates(context), state => Assert.Equal(IdentityStoreState.Invalidated, state));
        Assert.False(IsWorkTokenCurrent(context, token));
        Assert.False(TryStageSnapshot(context, "snap-after-fault", token));
    }

    [Fact]
    public void ContextAndStandaloneStoresExposeNoTokenlessMutationSurface()
    {
        AssertReadOnlyContextView(nameof(ReplicationContext.Baselines), typeof(BaselineStore));
        AssertReadOnlyContextView(nameof(ReplicationContext.Deltas), typeof(DeltaHistory));
        AssertReadOnlyContextView(nameof(ReplicationContext.Identities), typeof(NetEntityMappingTable));
        AssertReadOnlyContextView(nameof(ReplicationContext.ProvisionalRemaps), typeof(ProvisionalRemapTable));
        AssertReadOnlyContextView(nameof(ReplicationContext.Tombstones), typeof(TombstoneRegistry));

        AssertNoTokenlessPublicMutation(typeof(BaselineStore), typeof(IdentityStoreToken));
        AssertNoTokenlessPublicMutation(typeof(DeltaHistory), typeof(IdentityStoreToken));
        AssertNoTokenlessPublicMutation(typeof(NetEntityMappingTable), typeof(IdentityStoreToken));
        AssertNoTokenlessPublicMutation(typeof(ProvisionalRemapTable), typeof(IdentityStoreToken));
        AssertNoTokenlessPublicMutation(typeof(TombstoneRegistry), typeof(IdentityStoreToken));
        AssertNoTokenlessPublicMutation(
            typeof(Lumio.GameRuntime.Replication.Identity.NetEntityMappingTable),
            typeof(Lumio.GameRuntime.Replication.Identity.IdentityStoreToken));
        AssertNoTokenlessPublicMutation(
            typeof(Lumio.GameRuntime.Replication.Identity.ProvisionalRemapTable),
            typeof(Lumio.GameRuntime.Replication.Identity.IdentityStoreToken));
        AssertNoTokenlessPublicMutation(
            typeof(Lumio.GameRuntime.Replication.Identity.TombstoneRegistry),
            typeof(Lumio.GameRuntime.Replication.Identity.IdentityStoreToken));

        var baseline = new BaselineStore(new ReplicationBudget(4, 4096, 4, 4096), 7);
        var delta = new DeltaHistory(new ReplicationBudget(4, 4096, 4, 4096), 7);
        var mappings = new NetEntityMappingTable(7);
        var remaps = new ProvisionalRemapTable(7);
        var tombstones = new TombstoneRegistry(7);
        IdentityStoreToken baselineToken = baseline.CaptureToken();

        Assert.False(delta.IsTokenCurrent(baselineToken));
        Assert.False(mappings.IsTokenCurrent(baselineToken));
        Assert.False(remaps.IsTokenCurrent(baselineToken));
        Assert.False(tombstones.IsTokenCurrent(baselineToken));
        Assert.False(baseline.IsTokenCurrent(new IdentityStoreToken(7)));
        Assert.False(new Lumio.GameRuntime.Replication.Identity.IdentityStoreToken(7).IsValid);

        IdentityStoreToken deltaToken = delta.CaptureToken();
        Assert.True(baseline.Reset(baselineToken));
        Assert.True(delta.Reset(deltaToken));
        Assert.Equal(2UL, baseline.WorkEpoch);
        Assert.Equal(2UL, delta.WorkEpoch);
        Assert.False(baseline.IsTokenCurrent(baselineToken));
        Assert.False(delta.IsTokenCurrent(deltaToken));
    }

    [Fact]
    public void GeneratedGapDeltaRoundTripsCanonicalAdmission()
    {
        MappingSetView mappings = CreateMappings();
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        DeltaProjectionResult built = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 2, 1,
            Revision(2), mappings, Array.Empty<TombstoneView>(), gapDetected: true, resyncReason: "\u00e9");
        Assert.True(built.Succeeded);

        var admission = new ReplicationAdmissionContext(
            "session-1", "product", "release-1", "Delta",
            "Client", new[] { "replicate" }, 1,
            "session-1", "product", "release-1", "Client", new[] { "replicate" }, 1);
        ReplicationValidationResult result = new ReplicationEnvelopeValidator().ValidatePreQueue(
            built.Delta!.ToEnvelope("trace-1"), admission,
            "session-1", "product", "release-1", mappings.MappingSetHash, 1);

        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void RetryableHistoryAdmissionDoesNotConsumeSequence()
    {
        using ReplicationContext deltaContext = CreateActiveContext(1, "snap-delta", historyWindow: 1);
        DeltaProjectionResult retained = deltaContext.BuildDelta(
            "snap-delta", 1, 2, 1, Revision(2), Array.Empty<TombstoneView>());
        Assert.Equal(2UL, retained.Delta!.Sequence);
        DeltaProjectionResult rejected = deltaContext.BuildDelta(
            "snap-delta", 2, 3, 2, Revision(3), Array.Empty<TombstoneView>());
        Assert.Equal(ProjectionStatus.Retryable, rejected.Status);
        Assert.Equal(BaselineAckStatus.Acknowledged, deltaContext.AckDelta("snap-delta", 2, 2));
        DeltaProjectionResult retried = deltaContext.BuildDelta(
            "snap-delta", 2, 3, 2, Revision(3), Array.Empty<TombstoneView>());
        Assert.Equal(ProjectionStatus.Retryable, retried.Status);
        Assert.Equal(2UL, retained.Delta!.Sequence);

        using ReplicationContext baselineContext = CreateContext(1, historyWindow: 1);
        Assert.True(baselineContext.BeginSnapshot().Succeeded);
        Assert.Equal(1UL, baselineContext.BuildFullSnapshot("snap-a", Revision(1)).Snapshot!.Sequence);
        Assert.Equal(ProjectionStatus.Retryable,
            baselineContext.BuildFullSnapshot("snap-b", Revision(2)).Status);
        Assert.True(baselineContext.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, baselineContext.AckBaseline("snap-a", 1));
        Assert.True(baselineContext.Activate().Succeeded);
        Assert.True(baselineContext.BeginResync().Succeeded);
        FullSnapshotProjectionResult baselineRetry = baselineContext.BuildFullSnapshot("snap-b", Revision(2));
        Assert.True(baselineRetry.Succeeded);
        Assert.Equal(2UL, baselineRetry.Snapshot!.Sequence);
    }

    [Fact]
    public void StaleDestroyedIdentityCannotRemoveNewerLiveBinding()
    {
        var table = new NetEntityMappingTable(7);
        IdentityStoreToken token = table.CaptureToken();
        Assert.True(table.Bind(AliveIdentity(Id1, 5), token).Succeeded);

        MappingBindingResult destroyed = table.Bind(DestroyedIdentity(Id1, 2), token);

        Assert.False(destroyed.Succeeded);
        Assert.Equal("RevisionConflict", destroyed.GeneratedErrorId);
        Assert.True(table.TryResolveLocal(Id1, 5, out string? local, token));
        Assert.Equal("4:5", local);
        Assert.False(table.IsTombstoned(Id1, token));
    }

    [Fact]
    public void AckCursorEvictionCannotReopenAcknowledgedBaseline()
    {
        var history = new DeltaHistory(new ReplicationBudget(1, 4096, 8, 4096));
        IdentityStoreToken token = history.CaptureToken();
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-z", 1, 2, 1, 10, Hash), token));
        Assert.Equal(DeltaAckStatus.Acknowledged, history.Acknowledge("snap-z", 1, 2, token));
        Assert.Equal(DeltaHistoryStatus.Accepted,
            history.Add(new DeltaRecord("snap-a", 1, 2, 1, 10, Hash), token));
        Assert.Equal(DeltaAckStatus.Acknowledged, history.Acknowledge("snap-a", 1, 2, token));

        Assert.Equal(DeltaHistoryStatus.RevisionConflict,
            history.Add(new DeltaRecord("snap-z", 1, 2, 1, 10, Hash), token));
        Assert.Equal(DeltaChainStatus.UnknownBaseline,
            history.TryGetContiguous("snap-z", 2, 2, token).Status);
    }

    private static void AssertReadOnlyContextView(string propertyName, Type mutableType)
    {
        Type viewType = typeof(ReplicationContext).GetProperty(propertyName)!.PropertyType;
        Assert.EndsWith("View", viewType.Name, StringComparison.Ordinal);
        Assert.False(mutableType.IsAssignableFrom(viewType));
        AssertNoPublicMethods(viewType, "Add", "Append", "Stage", "Ack", "Acknowledge", "Bind", "Remove", "Collect", "Release", "Reset", "Clear", "Invalidate", "Close", "CaptureToken");
    }

    private static void AssertNoTokenlessPublicMutation(Type type, Type tokenType)
    {
        string[] names = { "Add", "Append", "Stage", "Ack", "Acknowledge", "Bind", "Remove", "Collect", "Release", "Reset", "Clear", "Invalidate", "Close" };
        MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => names.Contains(method.Name, StringComparer.Ordinal))
            .Where(method => method.GetParameters().All(parameter => parameter.ParameterType != tokenType))
            .ToArray();
        Assert.Empty(methods);
    }

    private static void AssertNoPublicMethods(Type type, params string[] names)
    {
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => names.Contains(method.Name, StringComparer.Ordinal));
    }

    private static IdentityStoreToken CaptureWorkToken(ReplicationContext context)
    {
        MethodInfo? method = typeof(ReplicationContext).GetMethod("CaptureWorkToken", Type.EmptyTypes);
        if (method is not null) return (IdentityStoreToken)method.Invoke(context, null)!;
        object store = typeof(ReplicationContext).GetProperty(nameof(ReplicationContext.Baselines))!.GetValue(context)!;
        return (IdentityStoreToken)store.GetType().GetMethod("CaptureToken", Type.EmptyTypes)!.Invoke(store, null)!;
    }

    private static bool IsWorkTokenCurrent(ReplicationContext context, IdentityStoreToken token)
    {
        MethodInfo? method = typeof(ReplicationContext).GetMethod("IsWorkTokenCurrent", new[] { typeof(IdentityStoreToken) });
        if (method is not null) return (bool)method.Invoke(context, new object[] { token })!;
        object store = typeof(ReplicationContext).GetProperty(nameof(ReplicationContext.Baselines))!.GetValue(context)!;
        return (bool)store.GetType().GetMethod("IsTokenCurrent", new[] { typeof(IdentityStoreToken) })!.Invoke(store, new object[] { token })!;
    }

    private static ulong WorkEpoch(ReplicationContext context)
    {
        PropertyInfo? property = typeof(ReplicationContext).GetProperty("WorkEpoch");
        return property is null ? 0 : (ulong)property.GetValue(context)!;
    }

    private static bool TryStageSnapshot(ReplicationContext context, string snapshotId, IdentityStoreToken token)
    {
        MethodInfo? contextMethod = typeof(ReplicationContext).GetMethod(
            nameof(ReplicationContext.BuildFullSnapshot),
            new[] { typeof(string), typeof(RevisionVector), typeof(IdentityStoreToken) });
        if (contextMethod is not null)
        {
            var result = (FullSnapshotProjectionResult)contextMethod.Invoke(context, new object[] { snapshotId, Revision(10), token })!;
            return result.Succeeded;
        }

        object store = typeof(ReplicationContext).GetProperty(nameof(ReplicationContext.Baselines))!.GetValue(context)!;
        var record = new BaselineRecord(snapshotId, 10, 10, Hash);
        object resultValue = store.GetType().GetMethod("Stage", new[] { typeof(BaselineRecord), typeof(IdentityStoreToken) })!
            .Invoke(store, new object[] { record, token })!;
        return (BaselineStoreStatus)resultValue is BaselineStoreStatus.Accepted or BaselineStoreStatus.Duplicate;
    }

    private static bool TryStageDelta(ReplicationContext context, string baseSnapshotId, IdentityStoreToken token)
    {
        MethodInfo? contextMethod = typeof(ReplicationContext).GetMethods()
            .SingleOrDefault(method => method.Name == nameof(ReplicationContext.BuildDelta) &&
                method.GetParameters().Any(parameter => parameter.ParameterType == typeof(IdentityStoreToken)));
        if (contextMethod is not null)
        {
            object[] arguments =
            {
                baseSnapshotId, 1UL, 2UL, 1UL, Revision(2), Array.Empty<TombstoneView>(), token, false, null!
            };
            var result = (DeltaProjectionResult)contextMethod.Invoke(context, arguments)!;
            return result.Succeeded;
        }

        object store = typeof(ReplicationContext).GetProperty(nameof(ReplicationContext.Deltas))!.GetValue(context)!;
        var record = new DeltaRecord(baseSnapshotId, 1, 2, 99, 10, Hash);
        object resultValue = store.GetType().GetMethod("Add", new[] { typeof(DeltaRecord), typeof(IdentityStoreToken) })!
            .Invoke(store, new object[] { record, token })!;
        return (DeltaHistoryStatus)resultValue is DeltaHistoryStatus.Accepted or DeltaHistoryStatus.Duplicate;
    }

    private static bool TryBindIdentity(ReplicationContext context, EntityIdentity identity, IdentityStoreToken token)
    {
        MethodInfo? contextMethod = typeof(ReplicationContext).GetMethod(
            "BindIdentity", new[] { typeof(EntityIdentity), typeof(IdentityStoreToken) });
        if (contextMethod is not null)
            return ((MappingBindingResult)contextMethod.Invoke(context, new object[] { identity, token })!).Succeeded;
        object store = typeof(ReplicationContext).GetProperty(nameof(ReplicationContext.Identities))!.GetValue(context)!;
        return ((MappingBindingResult)store.GetType().GetMethod("Bind", new[] { typeof(EntityIdentity), typeof(IdentityStoreToken) })!
            .Invoke(store, new object[] { identity, token })!).Succeeded;
    }

    private static bool TryAddRemap(
        ReplicationContext context,
        EntityIdentity provisional,
        EntityIdentity authoritative,
        IdentityStoreToken token)
    {
        MethodInfo? contextMethod = typeof(ReplicationContext).GetMethod(
            "AddProvisionalRemap", new[] { typeof(EntityIdentity), typeof(EntityIdentity), typeof(IdentityStoreToken) });
        if (contextMethod is not null)
            return ((ProvisionalRemapResult)contextMethod.Invoke(context, new object[] { provisional, authoritative, token })!).Succeeded;
        object store = typeof(ReplicationContext).GetProperty(nameof(ReplicationContext.ProvisionalRemaps))!.GetValue(context)!;
        return ((ProvisionalRemapResult)store.GetType().GetMethod(
            "Add", new[] { typeof(EntityIdentity), typeof(EntityIdentity), typeof(IdentityStoreToken) })!
            .Invoke(store, new object[] { provisional, authoritative, token })!).Succeeded;
    }

    private static bool TryAddTombstone(ReplicationContext context, NetEntityId id, ulong until, IdentityStoreToken token)
    {
        MethodInfo? contextMethod = typeof(ReplicationContext).GetMethod(
            "AddTombstone", new[] { typeof(NetEntityId), typeof(ulong), typeof(IdentityStoreToken) });
        if (contextMethod is not null)
            return (bool)contextMethod.Invoke(context, new object[] { id, until, token })!;
        object store = typeof(ReplicationContext).GetProperty(nameof(ReplicationContext.Tombstones))!.GetValue(context)!;
        return (bool)store.GetType().GetMethod("Add", new[] { typeof(NetEntityId), typeof(ulong), typeof(IdentityStoreToken) })!
            .Invoke(store, new object[] { id, until, token })!;
    }

    private static int[] StoreCounts(ReplicationContext context) =>
        new[] { context.Baselines.Count, context.Deltas.Count, context.Identities.Count, context.ProvisionalRemaps.Count, context.Tombstones.Count };

    private static IdentityStoreState[] StoreStates(ReplicationContext context) =>
        new[] { context.Baselines.State, context.Deltas.State, context.Identities.State, context.ProvisionalRemaps.State, context.Tombstones.State };

    private static EntityIdentity AliveIdentity(NetEntityId id, ulong generation) =>
        new(id.Value, "server-a", 7, 15, generation, "4:" + generation,
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Alive, null, null, 10, null);

    private static EntityIdentity ProvisionalIdentity(NetEntityId id, ulong generation) =>
        new(id.Value, "client-a", 7, 15, generation, "4:" + generation,
            EntityIdentityNamespace.Provisional, EntityIdentityLifecycle.Alive, null, null, 10, null);

    private static EntityIdentity DestroyedIdentity(NetEntityId id, ulong generation) =>
        new(id.Value, "server-a", 7, 15, generation, "4:" + generation,
            EntityIdentityNamespace.Authoritative, EntityIdentityLifecycle.Destroyed, 20, null, 10, null);

    private static RevisionVector Revision(ulong revision) =>
        new(revision, revision, revision, revision, revision, revision, 1);

    private static ReplicationContext CreateActiveContext(
        ulong generation,
        string snapshotId,
        int historyWindow)
    {
        ReplicationContext context = CreateContext(generation, historyWindow);
        Assert.True(context.BeginSnapshot().Succeeded);
        Assert.True(context.BuildFullSnapshot(snapshotId, Revision(1)).Succeeded);
        Assert.True(context.AwaitBaselineAck().Succeeded);
        Assert.Equal(BaselineAckStatus.Acknowledged, context.AckBaseline(snapshotId, 1));
        Assert.True(context.Activate().Succeeded);
        return context;
    }

    private static ReplicationContext CreateContext(ulong generation, int historyWindow = 8) =>
        new("session-1", "product", "release-1", CreateMappings(),
            new ReplicationBudget(historyWindow, 8192, 16, 8192), generation);

    private static MappingSetView CreateMappings()
    {
        var mappings = new MappingRegistry();
        Assert.True(mappings.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        return mappings.View;
    }
}
