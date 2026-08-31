using System;
using System.Collections.Generic;
using System.Threading;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Session;
using Lumio.GameRuntime.Simulation.Tick;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SimulationExecutionControlV3Tests
{
    [Fact]
    public void PreCancelledTickFaultsWithoutCommitOrOutput()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var session = RunningSession(TickTestExecutors.CompleteComposition((phase, context) =>
        {
            if (phase == TickPhase.IngressCapture) Assert.True(context.TryEmitOutput("staged", new byte[] { 1 }));
        }));

        TickExecutionControl control = Control() with { CancellationToken = source.Token };
        TickRunResult result = session.RunTick(Request(control));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("Cancelled", result.GeneratedErrorId);
        Assert.False(result.IsCommitted);
        Assert.Empty(result.Outputs);
        Assert.Equal(SimulationSessionState.Faulted, session.State);
    }

    [Fact]
    public void SlowSynchronousExecutorIsCheckedAfterCooperativeBoundary()
    {
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ApplyInputs] = context =>
        {
            Assert.True(context.TryEmitOutput("staged", new byte[] { 1 }));
            Thread.Sleep(30);
            return PhaseOutcome.Success();
        };
        using var session = RunningSession(TickTestExecutors.Composition(handlers));
        TickExecutionControl control = Control(timeout: TimeSpan.FromMilliseconds(5));

        TickRunResult result = session.RunTick(Request(control));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("TimedOut", result.GeneratedErrorId);
        Assert.False(result.IsCommitted);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public void LogicalWorkAndCommandLimitsFailBeforeCommit()
    {
        using var work = RunningSession(TickTestExecutors.CompleteComposition());
        TickRunResult workResult = work.RunTick(Request(Control(maxWorkUnits: 3)));
        Assert.Equal("BudgetExceeded", workResult.GeneratedErrorId);
        Assert.False(workResult.IsCommitted);

        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ApplyInputs] = context =>
        {
            context.ConsumeCommands(2);
            return PhaseOutcome.Success();
        };
        using var commands = RunningSession(TickTestExecutors.Composition(handlers), "commands");
        TickRunResult commandResult = commands.RunTick(Request(Control(maxCommands: 1)));
        Assert.Equal("BudgetExceeded", commandResult.GeneratedErrorId);
        Assert.False(commandResult.IsCommitted);
    }

    [Fact]
    public void NonCooperativeControlFailsClosedBeforePhaseExecution()
    {
        var executions = 0;
        using var session = RunningSession(TickTestExecutors.CompleteComposition((_, _) => executions++));

        TickRunResult result = session.RunTick(Request(Control(cooperativeChecksRequired: false)));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("CapabilityMissing", result.GeneratedErrorId);
        Assert.Equal(0, executions);
        Assert.False(result.IsCommitted);
    }

    [Fact]
    public void ProcessorBudgetChecksMicrosAndCommandsCooperatively()
    {
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ProcessorPlan] = context =>
        {
            context.CheckProcessorBudget(new ProcessorDescriptorBudget(10, 1), elapsedMicros: 11, emittedCommands: 1);
            return PhaseOutcome.Success();
        };
        using var session = RunningSession(TickTestExecutors.Composition(handlers));

        TickRunResult result = session.RunTick(Request(Control()));

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal("BudgetExceeded", result.GeneratedErrorId);
        Assert.False(result.IsCommitted);
    }

    [Fact]
    public void EveryExecutorSeesTheSameImmutableControlAndCanCheckpoint()
    {
        TickExecutionControl control = Control(maxWorkUnits: 100, maxCommands: 5);
        var executions = 0;
        using var session = RunningSession(TickTestExecutors.CompleteComposition((_, context) =>
        {
            Assert.Equal(control, context.ExecutionControl);
            context.Checkpoint();
            executions++;
        }));

        TickRunResult result = session.RunTick(Request(control));

        Assert.Equal(TickRunStatus.Succeeded, result.Status);
        Assert.Equal(13, executions);
    }

    private static TickExecutionControl Control(
        TimeSpan? timeout = null,
        ulong maxWorkUnits = 100,
        ulong maxCommands = 100,
        bool cooperativeChecksRequired = true) =>
        new(
            DeadlineTickId: 1,
            timeout ?? TimeSpan.FromSeconds(1),
            maxWorkUnits,
            maxCommands,
            cooperativeChecksRequired,
            TestContext.Current.CancellationToken);

    private static HostTickRequest Request(TickExecutionControl control) =>
        new(1, 1, 0, 1, Array.Empty<OpaqueIngressView>(), control);

    private static SimulationSession RunningSession(TickExecutorComposition composition, string sessionId = "execution-control")
    {
        var session = SimulationModule.Create().CreateSession(SimulationSessionOptions.Default(sessionId), composition);
        SessionEpoch epoch = session.Epoch;
        Assert.True(session.Initialize(epoch).Succeeded);
        Assert.True(session.Prime(epoch).Succeeded);
        Assert.True(session.Start(epoch).Succeeded);
        return session;
    }
}
