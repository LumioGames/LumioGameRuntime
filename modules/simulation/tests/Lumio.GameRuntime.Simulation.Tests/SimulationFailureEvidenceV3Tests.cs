using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SimulationFailureEvidenceV3Tests
{
    [Fact]
    public void PreCommitFailStopPersistsCompleteSnapshotEvidence()
    {
        var infrastructure = new TestTickInfrastructure();
        TickRunResult result = RunFailure(TickPhase.ApplyInputs, infrastructure);

        FailureBundleReadResult read = infrastructure.FailurePort.Read(result.FailureEvidenceId!);
        SimulationFailureBundle bundle = Assert.IsType<SimulationFailureBundle>(read.Bundle);
        Assert.Equal(FailureBundleReadStatus.Found, read.Status);
        Assert.Equal(DurableFailureEvidenceStatus.Durable, result.FailureEvidenceStatus);
        Assert.Equal("simulation", bundle.SessionId);
        Assert.Equal(1UL, bundle.TickId);
        Assert.Equal(1UL, bundle.Epoch);
        Assert.Equal(TickPhase.ApplyInputs, bundle.Phase);
        Assert.Equal(TickPhase.DecodeAndCanonicalize, bundle.LastCompletedPhase);
        Assert.Equal("PanicBoundary", bundle.GeneratedErrorId);
        Assert.Equal("FailStop", bundle.FaultAction);
        Assert.False(bundle.CommitPointReached);
        Assert.Equal("snapshot-1", bundle.SnapshotId);
        Assert.Null(bundle.NoSnapshotReason);
        Assert.Null(bundle.BootstrapPhase);
        Assert.Equal(1UL, bundle.Revisions.TickId);
        Assert.Equal("prepared-1", Assert.Single(bundle.PreparedTokens));
        Assert.Equal("participant-1", Assert.Single(bundle.ParticipantTokens));
    }

    [Fact]
    public void PreSnapshotFailStopPersistsExplicitNoSnapshotEvidence()
    {
        var statePort = new TestAuthoritativeTickStatePort((tickId, _) => TickTestExecutors.State(
            tickId,
            snapshotId: null,
            noSnapshotReason: "PreFirstSnapshot"));
        var infrastructure = new TestTickInfrastructure(statePort: statePort);

        TickRunResult result = RunFailure(TickPhase.NativeJobBarrier, infrastructure);
        SimulationFailureBundle bundle = Assert.IsType<SimulationFailureBundle>(
            infrastructure.FailurePort.Read(result.FailureEvidenceId!).Bundle);

        Assert.Null(bundle.SnapshotId);
        Assert.Equal("PreFirstSnapshot", bundle.NoSnapshotReason);
        Assert.Equal(TickPhase.NativeJobBarrier.ToString(), bundle.BootstrapPhase);
        Assert.Equal(DurableFailureEvidenceStatus.Durable, result.FailureEvidenceStatus);
    }

    [Theory]
    [InlineData(TickPhase.ReplicationProjection)]
    [InlineData(TickPhase.SnapshotHashMetrics)]
    [InlineData(TickPhase.EgressPublish)]
    public void PostCommitFailStopPersistsCommittedFailureEvidence(TickPhase phase)
    {
        var infrastructure = new TestTickInfrastructure();

        TickRunResult result = RunFailure(phase, infrastructure);
        SimulationFailureBundle bundle = Assert.IsType<SimulationFailureBundle>(
            infrastructure.FailurePort.Read(result.FailureEvidenceId!).Bundle);

        Assert.Equal(TickRunStatus.PostCommitFaulted, result.Status);
        Assert.True(result.IsCommitted);
        Assert.True(bundle.CommitPointReached);
        Assert.Equal(phase, bundle.Phase);
        Assert.Equal((TickPhase)((int)phase - 1), bundle.LastCompletedPhase);
        Assert.Equal(DurableFailureEvidenceStatus.Durable, result.FailureEvidenceStatus);
    }

    [Fact]
    public void PostCommitEvidenceRejectsAPreTickRevisionSnapshot()
    {
        var staleRevision = new SimulationRevisionSnapshot(
            0,
            0,
            0,
            new Dictionary<string, ulong>(StringComparer.Ordinal),
            0,
            1,
            1);
        var statePort = new TestAuthoritativeTickStatePort((tickId, _) =>
            TickTestExecutors.State(tickId, revisions: staleRevision));
        var infrastructure = new TestTickInfrastructure(statePort: statePort);

        TickRunResult result = RunFailure(TickPhase.EgressPublish, infrastructure);

        Assert.Equal(TickRunStatus.PostCommitFaulted, result.Status);
        Assert.Equal(DurableFailureEvidenceStatus.Corrupt, result.FailureEvidenceStatus);
        Assert.Null(result.FailureEvidenceId);
    }

    [Theory]
    [InlineData("Rejected", DurableFailureEvidenceStatus.PersistenceFailed)]
    [InlineData("Corrupt", DurableFailureEvidenceStatus.Corrupt)]
    public void EvidencePersistenceFailurePreservesTheOriginalFirstFailure(
        string writeStatus,
        DurableFailureEvidenceStatus expectedStatus)
    {
        var failurePort = new TestSimulationFailureBundlePort
        {
            WriteStatus = Enum.Parse<FailureBundleWriteStatus>(writeStatus)
        };
        var infrastructure = new TestTickInfrastructure(failurePort: failurePort);

        TickRunResult result = RunFailure(TickPhase.ApplyInputs, infrastructure);

        Assert.Equal(TickPhase.ApplyInputs, result.FirstFailure!.Phase);
        Assert.Equal("PanicBoundary", result.FirstFailure.GeneratedErrorId);
        Assert.Equal("primary failure", result.FirstFailure.Detail);
        Assert.Equal(expectedStatus, result.FailureEvidenceStatus);
        Assert.Equal(FailureBundleReadStatus.Missing, failurePort.Read(result.FailureEvidenceId!).Status);
    }

    [Fact]
    public void MissingEvidencePortFailsClosedWithoutClaimingDurability()
    {
        var executions = 0;
        var infrastructure = new TestTickInfrastructure();
        TickExecutorComposition composition = TickExecutorComposition.ForHandlers(
            TickTestExecutors.Complete((_, _) => executions++),
            TickExecutorCapability.All,
            infrastructure.StatePort,
            infrastructure.ReplayPort,
            null);
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), composition);

        TickRunResult result = runner.Run(Request());

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("CapabilityMissing", result.GeneratedErrorId);
        Assert.Equal(DurableFailureEvidenceStatus.Unavailable, result.FailureEvidenceStatus);
        Assert.Equal(0, executions);
    }

    [Fact]
    public void FailureBundleIsImmutableAndReadableAfterRunnerRestart()
    {
        var chunks = new Dictionary<string, ulong>(StringComparer.Ordinal) { ["chunk:0:0:0"] = 7 };
        var prepared = new[] { "prepared-original" };
        var participants = new[] { "participant-original" };
        var state = TickTestExecutors.State(
            1,
            revisions: new SimulationRevisionSnapshot(1, 1, 1, chunks, 1, 1, 1),
            preparedTokens: prepared,
            participantTokens: participants);
        var statePort = new TestAuthoritativeTickStatePort((_, _) => state);
        var failurePort = new TestSimulationFailureBundlePort();
        var infrastructure = new TestTickInfrastructure(statePort: statePort, failurePort: failurePort);
        TickRunResult result = RunFailure(TickPhase.ApplyInputs, infrastructure);

        chunks["chunk:0:0:0"] = 99;
        prepared[0] = "mutated";
        participants[0] = "mutated";
        var restartedInfrastructure = new TestTickInfrastructure(failurePort: failurePort);
        SimulationFailureBundle bundle = Assert.IsType<SimulationFailureBundle>(
            restartedInfrastructure.FailurePort.Read(result.FailureEvidenceId!).Bundle);

        Assert.Equal(7UL, bundle.Revisions.ChunkRevisionSet["chunk:0:0:0"]);
        Assert.Equal("prepared-original", bundle.PreparedTokens[0]);
        Assert.Equal("participant-original", bundle.ParticipantTokens[0]);
    }

    [Fact]
    public void EquivalentFailuresProduceTheSameDeterministicEvidenceIdentity()
    {
        TickRunResult first = RunFailure(TickPhase.ApplyInputs, new TestTickInfrastructure());
        TickRunResult second = RunFailure(TickPhase.ApplyInputs, new TestTickInfrastructure());

        Assert.NotNull(first.FailureEvidenceId);
        Assert.Equal(first.FailureEvidenceId, second.FailureEvidenceId);
    }

    private static TickRunResult RunFailure(TickPhase phase, TestTickInfrastructure infrastructure)
    {
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[phase] = _ => throw new InvalidOperationException("primary failure");
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1),
            TickTestExecutors.Composition(handlers, infrastructure: infrastructure));
        return runner.Run(Request());
    }

    private static HostTickRequest Request() => new(1, 1, Array.Empty<OpaqueIngressView>());
}
