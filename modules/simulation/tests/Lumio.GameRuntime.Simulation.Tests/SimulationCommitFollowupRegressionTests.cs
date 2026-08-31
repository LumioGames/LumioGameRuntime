using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Session;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SimulationCommitFollowupRegressionTests
{
    [Fact]
    public void SessionDoesNotExposeAnExecutableRunner()
    {
        PropertyInfo? runner = typeof(SimulationSession).GetProperty(
            "Runner",
            BindingFlags.Public | BindingFlags.Instance);

        Assert.Null(runner);
        Assert.DoesNotContain(
            typeof(SimulationSession).Assembly.GetExportedTypes(),
            type => type.Name == "TickRunner");
        Assert.Single(
            typeof(SimulationSession).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name == nameof(SimulationSession.RunTick));
        Assert.DoesNotContain(
            typeof(SimulationSession).Assembly.GetExportedTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)),
            method => method.Name == "Run" && method.ReturnType == typeof(TickRunResult));
    }

    [Fact]
    public void SessionLifecycleRejectsTickBeforeStartAndAfterDispose()
    {
        var module = SimulationModule.Create();
        using var session = module.CreateSession(
            SimulationSessionOptions.Default("lifecycle"),
            TickTestExecutors.CompleteComposition());

        TickRunResult created = session.RunTick(Request());
        session.Dispose();
        TickRunResult disposed = session.RunTick(Request());

        Assert.Equal(TickRunStatus.Rejected, created.Status);
        Assert.Equal("ContextClosing", created.GeneratedErrorId);
        Assert.Equal(TickRunStatus.Rejected, disposed.Status);
        Assert.Equal("ContextClosing", disposed.GeneratedErrorId);
    }

    [Fact]
    public void ThirteenArbitraryNoOpHandlersCannotCommit()
    {
        var handlers = new Dictionary<TickPhase, PhaseHandler>();
        foreach (TickPhase phase in PhaseGraph.Default.Phases) handlers.Add(phase, _ => PhaseOutcome.Success());
        var runner = new TickRunner(new TickRunnerOptions(1), handlers);

        TickRunResult result = runner.Run(Request());

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Empty(result.Outputs);
        Assert.Equal("InternalInvariant", result.GeneratedErrorId);
        Assert.True(runner.IsFaulted);
    }

    [Fact]
    public void UnavailableNamedPortsCannotFormAComposition()
    {
        var ports = new UnavailableExecutorPorts();

        Assert.Throws<ArgumentException>(() => new TickExecutorComposition(
            ports,
            ports,
            ports,
            ports,
            ports,
            ports,
            ports,
            ports,
            ports,
            ports));
    }

    [Fact]
    public void MissingDeclaredCapabilityFaultsBeforeAnyPhaseRuns()
    {
        var executions = 0;
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete((_, _) => executions++);
        TickExecutorCapability capabilities = TickExecutorCapability.All & ~TickExecutorCapability.VoxelCommit;
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1),
            TickTestExecutors.Composition(handlers, capabilities));

        TickRunResult result = runner.Run(Request());

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Equal(0, executions);
        Assert.Equal(TickPhase.VoxelCommit, result.FirstFailure!.Phase);
    }

    [Fact]
    public void ContextRejectsLateOutputFromAnotherThreadAndKeepsPublishedResultImmutable()
    {
        TickExecutionContext? captured = null;
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.CompleteComposition((phase, context) =>
        {
            if (phase == TickPhase.ApplyInputs)
            {
                captured = context;
                Assert.True(context.TryEmitOutput("before", new byte[] { 1 }));
            }
        }));

        TickRunResult result = runner.Run(Request());
        bool lateEmit = true;
        var thread = new Thread(() => lateEmit = captured!.TryEmitOutput("late", new byte[] { 2 }));
        thread.Start();
        thread.Join();

        Assert.False(lateEmit);
        Assert.Single(result.Outputs);
        Assert.Single(captured!.Outputs);
        Assert.Equal("before", result.Outputs[0].Key);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("timed-out")]
    [InlineData("budget-exceeded")]
    public void StableCancellationAndBudgetFailuresRemainUncommitted(string failure)
    {
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ApplyInputs] = context =>
        {
            Assert.True(context.TryEmitOutput("staged", new byte[] { 1 }));
            throw failure switch
            {
                "cancelled" => new OperationCanceledException("cancelled"),
                "timed-out" => new TickTimedOutException(),
                _ => new TickBudgetExceededException()
            };
        };
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(handlers));

        TickRunResult result = runner.Run(Request());

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Empty(result.Outputs);
        Assert.Equal(failure switch
        {
            "cancelled" => "Cancelled",
            "timed-out" => "TimedOut",
            _ => "BudgetExceeded"
        }, result.GeneratedErrorId);
        Assert.True(runner.IsFaulted);
    }

    [Theory]
    [InlineData(TickPhase.DecodeAndCanonicalize)]
    [InlineData(TickPhase.ApplyInputs)]
    [InlineData(TickPhase.CrossWorldPrepare)]
    public void BusinessRejectPhasesReturnRejectedWithoutFaulting(TickPhase phase)
    {
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[phase] = _ => PhaseOutcome.Reject("InvalidArgument", "invalid command");
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(handlers));

        TickRunResult result = runner.Run(Request());

        Assert.Equal(TickRunStatus.Rejected, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Equal("InvalidArgument", result.GeneratedErrorId);
        Assert.Equal(PhaseFailureClass.BusinessReject, result.Error!.Class);
        Assert.False(runner.IsFaulted);
    }

    [Fact]
    public void InfrastructureExceptionInBusinessPhaseStillFailStops()
    {
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ApplyInputs] = _ => throw new TickExecutionException("BusinessReject: forged");
        var runner = TickRunner.FromComposition(
            new TickRunnerOptions(1),
            TickTestExecutors.Composition(handlers));

        TickRunResult result = runner.Run(Request());

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("InternalInvariant", result.GeneratedErrorId);
        Assert.True(runner.IsFaulted);
    }

    [Fact]
    public void BusinessRejectionRequiresAGeneratedStableErrorId()
    {
        Assert.Throws<ArgumentException>(() => PhaseOutcome.Reject("NotGenerated", "invalid command"));
    }

    [Fact]
    public void BusinessRejectionLeavesSessionRunningAndTheSameTickRetryable()
    {
        var reject = true;
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ApplyInputs] = _ => reject
            ? PhaseOutcome.Reject("InvalidArgument", "invalid command")
            : PhaseOutcome.Success();
        var module = SimulationModule.Create();
        using var session = module.CreateSession(
            SimulationSessionOptions.Default("business-reject"),
            TickTestExecutors.Composition(handlers));
        Start(session);

        TickRunResult rejected = session.RunTick(Request());
        reject = false;
        TickRunResult retried = session.RunTick(Request());

        Assert.Equal(TickRunStatus.Rejected, rejected.Status);
        Assert.Equal(TickRunStatus.Succeeded, retried.Status);
        Assert.Equal(SimulationSessionState.Running, session.State);
    }

    [Theory]
    [InlineData(TickPhase.ReplicationProjection)]
    [InlineData(TickPhase.SnapshotHashMetrics)]
    [InlineData(TickPhase.EgressPublish)]
    public void PostCommitPhaseFailureKeepsCommitVisibleAndFailStops(TickPhase phase)
    {
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete((current, context) =>
        {
            if (current == TickPhase.ApplyInputs) Assert.True(context.TryEmitOutput("committed", new byte[] { 1 }));
        });
        handlers[phase] = _ => throw new InvalidOperationException("post-commit failure");
        var runner = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.Composition(handlers));

        TickRunResult result = runner.Run(Request());
        TickRunResult duplicate = runner.Run(Request());

        Assert.Equal(TickRunStatus.PostCommitFaulted, result.Status);
        Assert.True(result.IsCommitted);
        Assert.True(result.FirstFailure!.CommitPointReached);
        Assert.Single(result.Outputs);
        Assert.Equal(TickRunStatus.IdempotentSame, duplicate.Status);
        Assert.True(duplicate.IsCommitted);
        Assert.Equal(result.FirstFailure, duplicate.FirstFailure);
        Assert.Equal(result.FailureEvidenceId, duplicate.FailureEvidenceId);
        Assert.Equal(DurableFailureEvidenceStatus.Durable, duplicate.FailureEvidenceStatus);
        Assert.True(runner.IsFaulted);
        Assert.Equal(result.FirstFailure, runner.FailStop.FirstFailure);
        Assert.NotNull(runner.DeferredFailure);
    }

    [Fact]
    public void PostCommitFailureFaultsSessionButStillAllowsExactDuplicateReplay()
    {
        var fail = true;
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.EgressPublish] = _ =>
        {
            if (fail) throw new InvalidOperationException("post-commit failure");
            return PhaseOutcome.Success();
        };
        var module = SimulationModule.Create();
        using var session = module.CreateSession(
            SimulationSessionOptions.Default("post-commit"),
            TickTestExecutors.Composition(handlers));
        Start(session);

        TickRunResult first = session.RunTick(Request());
        fail = false;
        TickRunResult duplicate = session.RunTick(Request());
        TickRunResult next = session.RunTick(Request(2));

        Assert.Equal(TickRunStatus.PostCommitFaulted, first.Status);
        Assert.True(first.IsCommitted);
        Assert.Equal(TickRunStatus.IdempotentSame, duplicate.Status);
        Assert.True(duplicate.IsCommitted);
        Assert.Equal(TickRunStatus.Rejected, next.Status);
        Assert.Equal("ContextDestroyed", next.GeneratedErrorId);
        Assert.Equal(SimulationSessionState.Faulted, session.State);
    }

    [Fact]
    public void FinalizeCannotCommitUntilItsOwnPhaseRecordIsCompleted()
    {
        var context = new TickExecutionContext(
            Request(),
            16,
            1024,
            Environment.CurrentManagedThreadId,
            "simulation",
            TickTestExecutors.State(1));
        context.EnterPhase(TickPhase.GasAndEventFinalize);

        Assert.Throws<TickExecutionException>(() => context.MarkCommitted());
        Assert.False(context.IsCommitted);

        context.CompleteCurrentPhase();
        context.MarkCommitted();
        context.Close();
        Assert.True(context.IsCommitted);
    }

    [Fact]
    public void TargetTickIdParticipatesInCanonicalOrdering()
    {
        var first = new OpaqueIngressView("session", 1, 2, 1, new byte[] { 7 });
        var second = new OpaqueIngressView("session", 1, 1, 1, new byte[] { 7 });
        var runnerA = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.CompleteComposition());
        var runnerB = TickRunner.FromComposition(new TickRunnerOptions(1), TickTestExecutors.CompleteComposition());

        TickRunResult a = runnerA.Run(Request(inputs: new[] { first, second }));
        TickRunResult b = runnerB.Run(Request(inputs: new[] { second, first }));

        Assert.Equal(a.RequestHashHex, b.RequestHashHex);
    }

    private static HostTickRequest Request(ulong tick = 1, IReadOnlyList<OpaqueIngressView>? inputs = null) =>
        new(tick, 1, inputs ?? Array.Empty<OpaqueIngressView>());

    private static void Start(SimulationSession session)
    {
        SessionEpoch epoch = session.Epoch;
        Assert.True(session.Initialize(epoch).Succeeded);
        Assert.True(session.Prime(epoch).Succeeded);
        Assert.True(session.Start(epoch).Succeeded);
    }

    private sealed class UnavailableExecutorPorts :
        IIngressTickExecutor,
        IProcessorPlanTickExecutor,
        ICrossWorldTickExecutor,
        INativeJobBarrierTickExecutor,
        IVoxelCommitTickExecutor,
        IEcsCommandBufferCommitTickExecutor,
        IGasAndEventFinalizeTickExecutor,
        IReplicationProjectionTickExecutor,
        ISnapshotHashMetricsTickExecutor,
        IEgressPublishTickExecutor
    {
        public string ExecutorId => "unavailable";

        public bool IsAvailable => false;

        public PhaseOutcome Capture(TickExecutionContext context) => default;

        public PhaseOutcome DecodeAndCanonicalize(TickExecutionContext context) => default;

        public PhaseOutcome ApplyInputs(TickExecutionContext context) => default;

        public PhaseOutcome BuildPlan(TickExecutionContext context) => default;

        public PhaseOutcome Prepare(TickExecutionContext context) => default;

        public PhaseOutcome DecideCommit(TickExecutionContext context) => default;

        public PhaseOutcome CompleteNativeJobs(TickExecutionContext context) => default;

        public PhaseOutcome CommitVoxel(TickExecutionContext context) => default;

        public PhaseOutcome CommitCommands(TickExecutionContext context) => default;

        public PhaseOutcome FinalizeGasAndEvents(TickExecutionContext context) => default;

        public PhaseOutcome ProjectReplication(TickExecutionContext context) => default;

        public PhaseOutcome CaptureSnapshotHashMetrics(TickExecutionContext context) => default;

        public PhaseOutcome PublishEgress(TickExecutionContext context) => default;
    }
}
