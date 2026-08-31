using System;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SimulationDurableReplayV3Tests
{
    [Fact]
    public void CommittedReplaySurvivesMemoryCacheEviction()
    {
        var infrastructure = new TestTickInfrastructure(replayPort: new TestDurableTickReplayPort(512));
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(0, 1, 256, 1_048_576) { CacheCapacity = 256 },
            TickTestExecutors.CompleteComposition(infrastructure: infrastructure));
        for (ulong tickId = 1; tickId <= 257; tickId++)
        {
            TickRunResult committed = runner.Run(Request(tickId));
            Assert.Equal(TickRunStatus.Succeeded, committed.Status);
        }

        TickRunResult replay = runner.Run(Request(1));

        Assert.Equal(TickRunStatus.IdempotentSame, replay.Status);
        Assert.True(replay.IsCommitted);
        Assert.Equal(1UL, replay.TickId);
    }

    [Fact]
    public void CommittedReplaySurvivesRunnerRestartWithSharedDurableStore()
    {
        var replayPort = new TestDurableTickReplayPort();
        var failurePort = new TestSimulationFailureBundlePort();
        TickRunner first = Runner(1, replayPort, failurePort);
        TickRunResult committed = first.Run(Request(1));
        TickRunner restarted = Runner(2, replayPort, failurePort);

        TickRunResult replay = restarted.Run(Request(1));

        Assert.Equal(TickRunStatus.Succeeded, committed.Status);
        Assert.Equal(TickRunStatus.IdempotentSame, replay.Status);
        Assert.Equal(committed.StateHashHex, replay.StateHashHex);
        Assert.Equal(committed.RequestHashHex, replay.RequestHashHex);
    }

    [Fact]
    public void DurableReplayFailStopsTheSameTickWithADifferentDigest()
    {
        var replayPort = new TestDurableTickReplayPort();
        var failurePort = new TestSimulationFailureBundlePort();
        Assert.Equal(TickRunStatus.Succeeded, Runner(1, replayPort, failurePort).Run(Request(1)).Status);
        TickRunner restarted = Runner(2, replayPort, failurePort);
        var changed = new OpaqueIngressView("client-1", 1, 1, 1, new byte[] { 9 });

        TickRunResult replay = restarted.Run(Request(1, new[] { changed }));

        Assert.Equal(TickRunStatus.Faulted, replay.Status);
        Assert.Equal("RevisionConflict", replay.GeneratedErrorId);
        Assert.False(replay.IsCommitted);
        Assert.True(replay.FirstFailure!.CommitPointReached);
        Assert.True(restarted.IsFaulted);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("config")]
    public void DurableReplayFailStopsWhenAuthoritativeIdentityChanged(string identity)
    {
        var replayPort = new TestDurableTickReplayPort();
        var failurePort = new TestSimulationFailureBundlePort();
        Assert.Equal(TickRunStatus.Succeeded, Runner(1, replayPort, failurePort).Run(Request(1)).Status);
        var changedState = new TestAuthoritativeTickStatePort((tickId, _) => identity == "release"
            ? TickTestExecutors.State(tickId, gameReleaseId: "release-2")
            : TickTestExecutors.State(tickId, configSnapshotId: "config-2"));
        var changedInfrastructure = new TestTickInfrastructure(
            statePort: changedState,
            replayPort: replayPort,
            failurePort: failurePort);
        var restarted = TickRunner.FromComposition(
            new TickRunnerOptions(0, 2, 256, 1_048_576) { SessionId = "durable-session" },
            TickTestExecutors.CompleteComposition(infrastructure: changedInfrastructure));

        TickRunResult replay = restarted.Run(Request(1));

        Assert.Equal(TickRunStatus.Faulted, replay.Status);
        Assert.Equal("RevisionConflict", replay.GeneratedErrorId);
        Assert.True(restarted.IsFaulted);
    }

    [Fact]
    public void MissingDurableReplayCapabilityFailsBeforePhaseExecution()
    {
        var executions = 0;
        var infrastructure = new TestTickInfrastructure();
        TickExecutorComposition composition = TickExecutorComposition.ForHandlers(
            TickTestExecutors.Complete((_, _) => executions++),
            TickExecutorCapability.All,
            infrastructure.StatePort,
            null,
            infrastructure.FailurePort);
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), composition);

        TickRunResult result = runner.Run(Request(1));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("CapabilityMissing", result.GeneratedErrorId);
        Assert.Equal(0, executions);
        Assert.False(result.IsCommitted);
    }

    [Fact]
    public void CorruptDurableLookupFailStopsBeforePhaseExecution()
    {
        var replayPort = new TestDurableTickReplayPort { LookupOverride = DurableTickReplayLookupStatus.Corrupt };
        var executions = 0;
        var infrastructure = new TestTickInfrastructure(replayPort: replayPort);
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1),
            TickTestExecutors.CompleteComposition((_, _) => executions++, infrastructure));

        TickRunResult result = runner.Run(Request(1));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("EvidenceDigestMismatch", result.GeneratedErrorId);
        Assert.Equal(0, executions);
        Assert.True(runner.IsFaulted);
    }

    [Fact]
    public void DurableReplayHandlesMaximumTickWithoutOverflowAcrossRestart()
    {
        var replayPort = new TestDurableTickReplayPort();
        var failurePort = new TestSimulationFailureBundlePort();
        TickRunner first = Runner(ulong.MaxValue, replayPort, failurePort);

        TickRunResult committed = first.Run(Request(ulong.MaxValue));
        TickRunResult inMemoryReplay = first.Run(Request(ulong.MaxValue));
        TickRunResult restartedReplay = Runner(ulong.MaxValue, replayPort, failurePort).Run(Request(ulong.MaxValue));

        Assert.Equal(TickRunStatus.Succeeded, committed.Status);
        Assert.Equal(TickRunStatus.IdempotentSame, inMemoryReplay.Status);
        Assert.Equal(TickRunStatus.IdempotentSame, restartedReplay.Status);
        Assert.Equal(ulong.MaxValue, first.NextTickId);
    }

    [Fact]
    public void ReplayPersistenceFailureTurnsCommittedTickIntoFailStop()
    {
        var replayPort = new TestDurableTickReplayPort
        {
            WriteStatus = DurableTickReplayWriteStatus.Rejected
        };
        var infrastructure = new TestTickInfrastructure(replayPort: replayPort);
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1),
            TickTestExecutors.CompleteComposition((phase, context) =>
            {
                if (phase == TickPhase.ApplyInputs) Assert.True(context.TryEmitOutput("committed", new byte[] { 1 }));
            }, infrastructure));

        TickRunResult result = runner.Run(Request(1));

        Assert.Equal(TickRunStatus.PostCommitFaulted, result.Status);
        Assert.Equal("EvidenceMissing", result.GeneratedErrorId);
        Assert.True(result.IsCommitted);
        Assert.Single(result.Outputs);
        Assert.True(runner.IsFaulted);
        Assert.Equal(DurableFailureEvidenceStatus.Durable, result.FailureEvidenceStatus);
    }

    private static TickRunner Runner(
        ulong initialTickId,
        TestDurableTickReplayPort replayPort,
        TestSimulationFailureBundlePort failurePort)
    {
        var infrastructure = new TestTickInfrastructure(
            replayPort: replayPort,
            failurePort: failurePort);
        return TickRunner.FromComposition(
            new TickRunnerOptions(0, initialTickId, 256, 1_048_576) { SessionId = "durable-session" },
            TickTestExecutors.CompleteComposition(infrastructure: infrastructure));
    }

    private static HostTickRequest Request(ulong tickId, OpaqueIngressView[]? inputs = null) =>
        new(tickId, 1, inputs ?? Array.Empty<OpaqueIngressView>());
}
