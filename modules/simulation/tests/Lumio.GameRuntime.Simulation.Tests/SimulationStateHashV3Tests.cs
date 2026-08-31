using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SimulationStateHashV3Tests
{
    [Fact]
    public void DifferentCommittedOutputsProduceDifferentStateHashes()
    {
        TickRunResult first = RunWithOutput(new byte[] { 1 });
        TickRunResult second = RunWithOutput(new byte[] { 2 });

        Assert.Equal(TickRunStatus.Succeeded, first.Status);
        Assert.Equal(TickRunStatus.Succeeded, second.Status);
        Assert.NotEqual(first.StateHashHex, second.StateHashHex);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("world")]
    [InlineData("config")]
    [InlineData("manifest")]
    [InlineData("revision")]
    [InlineData("ecs")]
    [InlineData("command")]
    [InlineData("coordination")]
    [InlineData("voxel")]
    [InlineData("gas")]
    [InlineData("replication")]
    public void EveryRequiredAuthorityContributorChangesTheStateHash(string contributor)
    {
        string baseline = RunWithState(TickTestExecutors.State(1)).StateHashHex;
        string changed = RunWithState(ChangedState(contributor)).StateHashHex;

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void SuccessfulHashRegistersIdentityRevisionSubsystemPhaseAndOutputContributors()
    {
        TickExecutionContext? captured = null;
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1) { SessionId = "hash-session" },
            TickTestExecutors.CompleteComposition((_, context) => captured = context));

        TickRunResult result = runner.Run(Request());
        IReadOnlyDictionary<string, string> contributors = captured!.Hashes.Snapshot();

        Assert.Equal(TickRunStatus.Succeeded, result.Status);
        Assert.Contains("identity.session", contributors.Keys);
        Assert.Contains("identity.world", contributors.Keys);
        Assert.Contains("identity.release", contributors.Keys);
        Assert.Contains("identity.manifest", contributors.Keys);
        Assert.Contains("identity.config", contributors.Keys);
        Assert.Contains("revision.vector", contributors.Keys);
        Assert.Contains("state.ecs", contributors.Keys);
        Assert.Contains("state.command", contributors.Keys);
        Assert.Contains("state.coordination", contributors.Keys);
        Assert.Contains("state.voxel", contributors.Keys);
        Assert.Contains("state.gas", contributors.Keys);
        Assert.Contains("state.replication", contributors.Keys);
        Assert.Contains("outputs.count", contributors.Keys);
        for (var index = 0; index < 13; index++) Assert.Contains($"phase.{index:D2}", contributors.Keys);
        Assert.True(captured.Hashes.CaptureSummary().IsComplete);

        var selfDeclared = new StateHashCoordinator();
        foreach (KeyValuePair<string, string> contributor in contributors)
            selfDeclared.Register(contributor.Key, contributor.Value);
        Assert.False(selfDeclared.CaptureSummary().IsComplete);
        Assert.Throws<InvalidOperationException>(() => captured.Hashes.Register("late", "mutation"));
    }

    [Fact]
    public void ArbitraryProviderSetIsNotReportedAsACompleteAuthoritativeHash()
    {
        var coordinator = new StateHashCoordinator();
        coordinator.Register("self-declared", "value");

        StateHashSummary summary = coordinator.CaptureSummary();

        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void MissingStateCapabilityFailsBeforeAnyPhaseExecutes()
    {
        var executions = 0;
        var infrastructure = new TestTickInfrastructure();
        TickExecutorComposition composition = TickExecutorComposition.ForHandlers(
            TickTestExecutors.Complete((_, _) => executions++),
            TickExecutorCapability.All,
            null,
            infrastructure.ReplayPort,
            infrastructure.FailurePort);
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), composition);

        TickRunResult result = runner.Run(Request());

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("CapabilityMissing", result.GeneratedErrorId);
        Assert.False(result.IsCommitted);
        Assert.Equal(0, executions);
    }

    [Fact]
    public void CorruptCommittedContributorCannotProduceASuccessfulTick()
    {
        var statePort = new TestAuthoritativeTickStatePort((tickId, capture) =>
            capture == 1
                ? TickTestExecutors.State(tickId)
                : TickTestExecutors.State(tickId, ecsHashHex: "corrupt"));
        var infrastructure = new TestTickInfrastructure(statePort: statePort);
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1),
            TickTestExecutors.CompleteComposition(infrastructure: infrastructure));

        TickRunResult result = runner.Run(Request());

        Assert.Equal(TickRunStatus.PostCommitFaulted, result.Status);
        Assert.Equal("InternalInvariant", result.GeneratedErrorId);
        Assert.True(result.IsCommitted);
        Assert.True(runner.IsFaulted);
    }

    [Fact]
    public void AuthoritySnapshotDefensivelyCopiesRevisionAndTokenInputs()
    {
        var chunks = new Dictionary<string, ulong>(StringComparer.Ordinal) { ["chunk:0:0:0"] = 7 };
        var prepared = new[] { "prepared-1" };
        var participants = new[] { "participant-1" };
        var revision = new SimulationRevisionSnapshot(1, 1, 1, chunks, 1, 1, 1);
        AuthoritativeTickStateSnapshot snapshot = TickTestExecutors.State(
            1,
            revisions: revision,
            preparedTokens: prepared,
            participantTokens: participants);

        chunks["chunk:0:0:0"] = 99;
        prepared[0] = "mutated";
        participants[0] = "mutated";

        Assert.Equal(7UL, snapshot.Revisions.ChunkRevisionSet["chunk:0:0:0"]);
        Assert.Equal("prepared-1", snapshot.PreparedTokens[0]);
        Assert.Equal("participant-1", snapshot.ParticipantTokens[0]);
    }

    private static TickRunResult RunWithOutput(byte[] payload)
    {
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1),
            TickTestExecutors.CompleteComposition((phase, context) =>
            {
                if (phase == TickPhase.ApplyInputs) Assert.True(context.TryEmitOutput("result", payload));
            }));
        return runner.Run(Request());
    }

    private static TickRunResult RunWithState(AuthoritativeTickStateSnapshot state)
    {
        var infrastructure = new TestTickInfrastructure(
            statePort: new TestAuthoritativeTickStatePort((_, _) => state));
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1),
            TickTestExecutors.CompleteComposition(infrastructure: infrastructure));
        return runner.Run(Request());
    }

    private static AuthoritativeTickStateSnapshot ChangedState(string contributor) => contributor switch
    {
        "release" => TickTestExecutors.State(1, gameReleaseId: "release-2"),
        "world" => TickTestExecutors.State(1, worldId: "world-2"),
        "config" => TickTestExecutors.State(1, configSnapshotId: "config-2"),
        "manifest" => TickTestExecutors.State(1, manifestHashHex: TickTestExecutors.Digest('1')),
        "revision" => TickTestExecutors.State(1, revisions: new SimulationRevisionSnapshot(
            1,
            2,
            1,
            new Dictionary<string, ulong>(StringComparer.Ordinal) { ["chunk:0:0:0"] = 1 },
            1,
            1,
            1)),
        "ecs" => TickTestExecutors.State(1, ecsHashHex: TickTestExecutors.Digest('1')),
        "command" => TickTestExecutors.State(1, commandHashHex: TickTestExecutors.Digest('1')),
        "coordination" => TickTestExecutors.State(1, coordinationHashHex: TickTestExecutors.Digest('1')),
        "voxel" => TickTestExecutors.State(1, voxelHashHex: TickTestExecutors.Digest('1')),
        "gas" => TickTestExecutors.State(1, gasHashHex: TickTestExecutors.Digest('1')),
        "replication" => TickTestExecutors.State(1, replicationHashHex: TickTestExecutors.Digest('1')),
        _ => throw new ArgumentOutOfRangeException(nameof(contributor))
    };

    private static HostTickRequest Request() => new(1, 1, Array.Empty<OpaqueIngressView>());
}
