using System;
using System.Collections.Generic;
using System.Threading;
using Lumio.GameRuntime.Coordination;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Simulation.Session;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SessionLifecycleTests
{
    [Fact]
    public void SessionFollowsExactLifecycleIncludingPauseResumeAndPausedDrain()
    {
        using var session = SimulationModule.Create().CreateSession(SimulationSessionOptions.Default("lifecycle"));
        IRuntimeSession runtime = session;
        SessionEpoch epoch = session.Epoch;

        Assert.Equal(SimulationSessionState.Created, runtime.State);
        Assert.Equal("lifecycle", runtime.SessionId);
        Assert.False(runtime.WorldId.IsDefault);

        Assert.True(session.Initialize(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Initialized, runtime.State);
        Assert.True(session.Prime(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Ready, runtime.State);
        Assert.True(session.Start(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Running, runtime.State);
        Assert.True(session.Pause(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Paused, runtime.State);
        Assert.True(session.Resume(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Running, runtime.State);
        Assert.True(session.Pause(epoch).Succeeded);
        Assert.True(session.Drain(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Draining, runtime.State);
        Assert.True(session.MarkSnapshotted(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Snapshotted, runtime.State);
        Assert.True(session.DisposeSession(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Disposed, runtime.State);
    }

    [Theory]
    [InlineData(SimulationSessionState.Created)]
    [InlineData(SimulationSessionState.Initialized)]
    [InlineData(SimulationSessionState.Ready)]
    [InlineData(SimulationSessionState.Running)]
    [InlineData(SimulationSessionState.Paused)]
    [InlineData(SimulationSessionState.Draining)]
    [InlineData(SimulationSessionState.Snapshotted)]
    public void AnyActiveStateCanTransitionToFaulted(SimulationSessionState target)
    {
        using SimulationSession session = CreateAt(target);
        IRuntimeSession runtime = session;

        session.Fault();

        Assert.Equal(SimulationSessionState.Faulted, runtime.State);
    }

    [Fact]
    public void DisposedSessionDoesNotTransitionToFaulted()
    {
        using SimulationSession session = CreateAt(SimulationSessionState.Snapshotted);
        Assert.True(session.DisposeSession(session.Epoch).Succeeded);

        session.Fault();

        Assert.Equal(SimulationSessionState.Disposed, session.State);
    }

    [Fact]
    public void DisposedAndFaultedSessionsRejectRunTick()
    {
        using SimulationSession disposed = CreateAt(SimulationSessionState.Snapshotted);
        IRuntimeSession disposedRuntime = disposed;
        Assert.True(disposed.DisposeSession(disposed.Epoch).Succeeded);
        TickRunResult disposedResult = disposedRuntime.RunTick(new TickInput(Request()));

        using SimulationSession faulted = CreateAt(SimulationSessionState.Running);
        IRuntimeSession faultedRuntime = faulted;
        faulted.Fault();
        TickRunResult faultedResult = faultedRuntime.RunTick(new TickInput(Request()));

        Assert.Equal(TickRunStatus.Rejected, disposedResult.Status);
        Assert.Equal("ContextClosing", disposedResult.GeneratedErrorId);
        Assert.Equal(SimulationSessionState.Disposed, disposedRuntime.State);
        Assert.Equal(TickRunStatus.Rejected, faultedResult.Status);
        Assert.Equal("ContextClosing", faultedResult.GeneratedErrorId);
        Assert.Equal(SimulationSessionState.Faulted, faultedRuntime.State);
    }

    [Fact]
    public void InitializeAndRunTickSucceedOnOwnerThread()
    {
        using var session = SimulationModule.Create().CreateSession(
            SimulationSessionOptions.Default("owner-success"),
            TickTestExecutors.CompleteComposition());
        IRuntimeSession runtime = session;
        SessionEpoch epoch = session.Epoch;

        Assert.True(session.Initialize(epoch).Succeeded);
        Assert.True(session.Prime(epoch).Succeeded);
        Assert.True(session.Start(epoch).Succeeded);
        TickRunResult result = runtime.RunTick(new TickInput(Request()));

        Assert.Equal(TickRunStatus.Succeeded, result.Status);
        Assert.True(result.IsCommitted);
        Assert.Equal(SimulationSessionState.Running, runtime.State);
        Assert.Equal(new WorldId(1UL), runtime.WorldId);
    }

    [Fact]
    public void NonOwnerRunTickFaultsBeforeWorldReadOrWrite()
    {
        var worldTouched = false;
        var infrastructure = new TestTickInfrastructure(
            new TestAuthoritativeTickStatePort((_, _) =>
            {
                worldTouched = true;
                return TickTestExecutors.State(1);
            }));
        using var session = SimulationModule.Create().CreateSession(
            SimulationSessionOptions.Default("owner-violation"),
            TickTestExecutors.CompleteComposition(infrastructure: infrastructure));
        SessionEpoch epoch = session.Epoch;
        Assert.True(session.Initialize(epoch).Succeeded);
        Assert.True(session.Prime(epoch).Succeeded);
        Assert.True(session.Start(epoch).Succeeded);
        ulong tickIdBefore = session.CurrentTickId;
        IRuntimeSession runtime = session;
        TickRunResult? result = null;
        var thread = new Thread(() => result = runtime.RunTick(new TickInput(Request())));

        thread.Start();
        thread.Join();

        Assert.NotNull(result);
        Assert.Equal(TickRunStatus.Faulted, result!.Status);
        Assert.Equal("WrongContext", result.GeneratedErrorId);
        Assert.NotNull(result.Error);
        Assert.Equal(SimulationSessionState.Faulted, runtime.State);
        Assert.False(worldTouched);
        Assert.Equal(tickIdBefore, session.CurrentTickId);
        Assert.False(session.Runner.IsFaulted);
    }

    [Fact]
    public void RevisionAndTxnQueriesForwardToCoordinationWithoutCachingAMutableCopy()
    {
        var coordination = new RecordingCoordinationReadPort();
        using var session = new SimulationSession(
            SimulationSessionOptions.Default("coord-forward"),
            TickTestExecutors.CompleteComposition(),
            coordination: coordination);

        SessionRevisionVectorView firstRevision = session.ReadRevision();
        SessionRevisionVectorView secondRevision = session.ReadRevision();
        TxnResolutionResult firstTxn = session.QueryTxn("txn-1");
        TxnResolutionResult secondTxn = session.QueryTxn("txn-1");

        Assert.Equal(1UL, firstRevision.TickId);
        Assert.Equal(2UL, secondRevision.TickId);
        Assert.False(ReferenceEquals(firstRevision, secondRevision));
        Assert.Equal(2, coordination.RevisionReads);
        Assert.Equal(2, coordination.TxnQueries);
        Assert.Equal(TxnCommitStatus.Fatal, firstTxn.Status);
        Assert.Equal(TxnCommitStatus.Fatal, secondTxn.Status);
        Assert.Null(firstTxn.Record);
        Assert.Null(secondTxn.Record);
    }

    private static SimulationSession CreateAt(SimulationSessionState target)
    {
        var session = SimulationModule.Create().CreateSession(SimulationSessionOptions.Default("state-" + target));
        SessionEpoch epoch = session.Epoch;
        if (target == SimulationSessionState.Created) return session;
        Assert.True(session.Initialize(epoch).Succeeded);
        if (target == SimulationSessionState.Initialized) return session;
        Assert.True(session.Prime(epoch).Succeeded);
        if (target == SimulationSessionState.Ready) return session;
        Assert.True(session.Start(epoch).Succeeded);
        if (target == SimulationSessionState.Running) return session;
        if (target == SimulationSessionState.Paused)
        {
            Assert.True(session.Pause(epoch).Succeeded);
            return session;
        }

        Assert.True(session.Drain(epoch).Succeeded);
        if (target == SimulationSessionState.Draining) return session;
        Assert.True(session.MarkSnapshotted(epoch).Succeeded);
        Assert.Equal(SimulationSessionState.Snapshotted, session.State);
        return session;
    }

    private static HostTickRequest Request() => new(1, 1, Array.Empty<OpaqueIngressView>());

    private sealed class RecordingCoordinationReadPort : ICoordinationReadPort
    {
        internal int RevisionReads { get; private set; }

        internal int TxnQueries { get; private set; }

        public SessionRevisionVectorView ReadRevision()
        {
            RevisionReads++;
            return new SessionRevisionVectorView(
                (ulong)RevisionReads,
                (ulong)RevisionReads,
                0UL,
                new Dictionary<string, ulong>(StringComparer.Ordinal),
                0UL,
                0UL,
                1UL);
        }

        public TxnResolutionResult QueryTxn(string txnId)
        {
            TxnQueries++;
            return new TxnResolutionResult(TxnCommitStatus.Fatal, null, null);
        }
    }
}
