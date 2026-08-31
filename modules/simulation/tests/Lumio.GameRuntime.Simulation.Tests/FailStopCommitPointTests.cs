using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class FailStopCommitPointTests
{
    [Fact]
    public void PreCommitFailureFaultsSessionAndDoesNotReportCommit()
    {
        Dictionary<TickPhase, PhaseHandler> executors = TickTestExecutors.Complete();
        executors[TickPhase.ApplyInputs] = _ => throw new InvalidOperationException("boom");
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(executors));
        var result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));
        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Equal(TickPhase.ApplyInputs, result.FirstFailure!.Phase);
        Assert.True(runner.IsFaulted);
    }

    [Fact]
    public void RepeatingCommittedTickReturnsTheCachedIdempotentResult()
    {
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.CompleteComposition());
        var request = new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>());
        var first = runner.Run(request);
        var duplicate = runner.Run(request);
        Assert.Equal(TickRunStatus.Succeeded, first.Status);
        Assert.Equal(TickRunStatus.IdempotentSame, duplicate.Status);
        Assert.Equal(first.StateHashHex, duplicate.StateHashHex);
        Assert.Equal(first, duplicate.CachedResult);
    }

    [Fact]
    public void EveryRequiredPhaseExecutesExactlyOnceInCanonicalOrder()
    {
        var executed = new List<TickPhase>();
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.CompleteComposition((phase, _) => executed.Add(phase)));

        TickRunResult result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(TickRunStatus.Succeeded, result.Status);
        Assert.True(result.IsCommitted);
        Assert.Equal(PhaseGraph.Default.Phases, executed);
        Assert.Equal(PhaseGraph.Default.Phases, result.PhaseTrace);
        Assert.All(executed.GroupBy(phase => phase), group => Assert.Single(group));
    }

    [Fact]
    public void MissingExecutorFaultsBeforeAnyPhaseOrOutputIsPublished()
    {
        Dictionary<TickPhase, PhaseHandler> executors = TickTestExecutors.Complete((phase, context) =>
        {
            if (phase == TickPhase.IngressCapture) Assert.True(context.TryEmitOutput("staged", new byte[] { 1 }));
        });
        executors.Remove(TickPhase.EgressPublish);
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(executors));

        TickRunResult result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Empty(result.PhaseTrace);
        Assert.Empty(result.Outputs);
        Assert.Equal(TickPhase.EgressPublish, result.FirstFailure!.Phase);
        Assert.Equal("InternalInvariant", result.GeneratedErrorId);
        Assert.True(runner.IsFaulted);
    }

    [Fact]
    public void PhaseExecutorCannotAccessRunnerOwnedPhaseControls()
    {
        MethodInfo? commit = typeof(TickExecutionContext).GetMethod(
            "MarkCommitted",
            BindingFlags.Instance | BindingFlags.Public);
        MethodInfo? enter = typeof(TickExecutionContext).GetMethod(
            "EnterPhase",
            BindingFlags.Instance | BindingFlags.Public);

        Assert.Null(commit);
        Assert.Null(enter);
    }

    [Fact]
    public void OutputStagedByThrowingExecutorIsNotPublished()
    {
        Dictionary<TickPhase, PhaseHandler> executors = TickTestExecutors.Complete();
        executors[TickPhase.ApplyInputs] = context =>
        {
            Assert.True(context.TryEmitOutput("staged", new byte[] { 1 }));
            throw new InvalidOperationException("boom");
        };
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(executors));

        TickRunResult result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public void FinalizeFailureDiscardsStagedOutputs()
    {
        Dictionary<TickPhase, PhaseHandler> executors = TickTestExecutors.Complete((phase, context) =>
        {
            if (phase == TickPhase.ApplyInputs) Assert.True(context.TryEmitOutput("staged", new byte[] { 1 }));
        });
        executors[TickPhase.GasAndEventFinalize] = _ => throw new InvalidOperationException("finalize failed");
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(executors));

        TickRunResult result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public void SuccessfulFinalizePublishesEachStagedOutputOnce()
    {
        var executions = 0;
        Dictionary<TickPhase, PhaseHandler> executors = TickTestExecutors.Complete((phase, context) =>
        {
            executions++;
            if (phase == TickPhase.ApplyInputs) Assert.True(context.TryEmitOutput("staged", new byte[] { 1 }));
            if (phase == TickPhase.CommitDecision) Assert.Empty(context.Outputs);
            if (phase == TickPhase.ReplicationProjection) Assert.Single(context.Outputs);
        });
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(executors));
        var request = new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>());

        TickRunResult first = runner.Run(request);
        TickRunResult duplicate = runner.Run(request);

        Assert.True(first.IsCommitted);
        Assert.Single(first.Outputs);
        Assert.Single(duplicate.Outputs);
        Assert.Equal(13, executions);
    }

    [Fact]
    public void EgressPhaseCanPublishOutputAfterFinalizeCommit()
    {
        Dictionary<TickPhase, PhaseHandler> executors = TickTestExecutors.Complete((phase, context) =>
        {
            if (phase == TickPhase.EgressPublish) Assert.True(context.TryEmitOutput("egress", new byte[] { 2 }));
        });
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(executors));

        TickRunResult result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(TickRunStatus.Succeeded, result.Status);
        Assert.True(result.IsCommitted);
        Assert.Single(result.Outputs);
        Assert.Equal("egress", result.Outputs[0].Key);
    }

    [Theory]
    [InlineData(TickPhase.ApplyInputs, true)]
    [InlineData(TickPhase.EcsCommandBufferCommit, false)]
    public void CancellationOrBudgetFailureBeforeFinalizeDoesNotCommit(TickPhase failurePhase, bool cancelled)
    {
        Dictionary<TickPhase, PhaseHandler> executors = TickTestExecutors.Complete();
        executors[failurePhase] = context =>
        {
            Assert.True(context.TryEmitOutput("staged", new byte[] { 1 }));
            if (cancelled) throw new OperationCanceledException("cancelled");
            throw new TickExecutionException("budget exceeded");
        };
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(executors));

        TickRunResult result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Empty(result.Outputs);
        Assert.Equal(failurePhase, result.FirstFailure!.Phase);
    }
}
