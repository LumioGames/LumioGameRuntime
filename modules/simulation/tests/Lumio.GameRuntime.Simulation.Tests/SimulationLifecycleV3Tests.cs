using System;
using System.Collections.Generic;
using System.Threading;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Session;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SimulationLifecycleV3Tests
{
    [Fact]
    public void NonOwnerCannotDisposeRunningSession()
    {
        using var session = RunningSession(TickTestExecutors.CompleteComposition());
        var thread = new Thread(session.Dispose);

        thread.Start();
        thread.Join();

        Assert.Equal(SimulationSessionState.Running, session.State);
        Assert.Equal(TickRunStatus.Succeeded, session.RunTick(Request()).Status);
    }

    [Fact]
    public void ReentrantDisposePreventsCommitAndClosesAfterCleanup()
    {
        SimulationSession? session = null;
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ApplyInputs] = _ =>
        {
            session!.Dispose();
            return PhaseOutcome.Success();
        };
        session = RunningSession(TickTestExecutors.Composition(handlers));

        TickRunResult result = session.RunTick(Request());

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Equal("ContextClosing", result.GeneratedErrorId);
        Assert.Equal(SimulationSessionState.Disposed, session.State);
    }

    [Fact]
    public void ConcurrentNonOwnerDisposeCannotCloseInFlightSession()
    {
        using var entered = new ManualResetEventSlim();
        Thread? disposer = null;
        SimulationSession? session = null;
        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ApplyInputs] = _ =>
        {
            disposer = new Thread(() =>
            {
                entered.Wait();
                session!.Dispose();
            });
            disposer.Start();
            entered.Set();
            Thread.Sleep(25);
            return PhaseOutcome.Success();
        };
        session = RunningSession(TickTestExecutors.Composition(handlers));

        TickRunResult result = session.RunTick(Request());
        disposer!.Join();

        Assert.Equal(TickRunStatus.Succeeded, result.Status);
        Assert.True(result.IsCommitted);
        Assert.Equal(SimulationSessionState.Running, session.State);
        session.Dispose();
    }

    [Fact]
    public void OwnerDisposalHasExplicitCreatedRunningFaultedAndRepeatedFinalStates()
    {
        using var created = SimulationModule.Create().CreateSession(SimulationSessionOptions.Default("created"));
        created.Dispose();
        Assert.Equal(SimulationSessionState.Disposed, created.State);
        created.Dispose();
        Assert.Equal(SimulationSessionState.Disposed, created.State);

        using var running = RunningSession(TickTestExecutors.CompleteComposition(), "running");
        running.Dispose();
        Assert.Equal(SimulationSessionState.Disposed, running.State);

        Dictionary<TickPhase, PhaseHandler> handlers = TickTestExecutors.Complete();
        handlers[TickPhase.ApplyInputs] = _ => throw new InvalidOperationException("fault");
        using var faulted = RunningSession(
            TickTestExecutors.Composition(handlers),
            "faulted");
        Assert.Equal(TickRunStatus.Faulted, faulted.RunTick(Request()).Status);
        Assert.Equal(SimulationSessionState.Faulted, faulted.State);
        faulted.Dispose();
        Assert.Equal(SimulationSessionState.Disposed, faulted.State);
    }

    private static SimulationSession RunningSession(TickExecutorComposition composition, string sessionId = "lifecycle-v3")
    {
        var session = SimulationModule.Create().CreateSession(SimulationSessionOptions.Default(sessionId), composition);
        SessionEpoch epoch = session.Epoch;
        Assert.True(session.Initialize(epoch).Succeeded);
        Assert.True(session.Prime(epoch).Succeeded);
        Assert.True(session.Start(epoch).Succeeded);
        return session;
    }

    private static HostTickRequest Request() => new(1, 1, Array.Empty<OpaqueIngressView>());
}
