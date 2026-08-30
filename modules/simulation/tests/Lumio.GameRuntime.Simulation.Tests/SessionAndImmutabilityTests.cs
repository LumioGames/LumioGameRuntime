using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Simulation.Ingress;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Session;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class SessionAndImmutabilityTests
{
    [Fact]
    public void PhaseRecordsIdentifyTheSingleCommitPoint()
    {
        var runner = new TickRunner(new TickRunnerOptions(1));
        TickRunResult result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));

        Assert.Equal(13, result.PhaseRecords.Count);
        Assert.Single(result.PhaseRecords, value => value.AuthoritativeCommitPoint);
        Assert.True(result.PhaseRecords[9].Completed);
        Assert.True(result.PhaseRecords[9].AuthoritativeCommitPoint);
    }

    [Fact]
    public void SessionTransitionsToFaultedWhenTheRunnerFails()
    {
        var module = SimulationModule.Create();
        using var session = module.CreateSession(SimulationSessionOptions.Default("session-1"));
        SessionEpoch epoch = session.Epoch;
        Assert.True(session.Initialize(epoch).Succeeded);
        Assert.True(session.Prime(epoch).Succeeded);
        Assert.True(session.Start(epoch).Succeeded);

        var request = new HostTickRequest(1, epoch.Value, 0, 1, Array.Empty<OpaqueIngressView>());
        session.Runner.SetHandler(TickPhase.ApplyInputs, _ => throw new InvalidOperationException("failure"));
        TickRunResult result = session.RunTick(request);

        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.Equal(SimulationSessionState.Faulted, session.State);
    }

    [Fact]
    public void OpaqueIngressPayloadCannotMutateCapturedBatch()
    {
        var queue = new IngressQueue(new IngressBudget(4, 128));
        Assert.Equal(IngressEnqueueStatus.Accepted, queue.TryEnqueue(new OpaqueIngress("session-1", 1, 1, 1, new byte[] { 7 })));
        var batch = queue.CaptureForTick(1).Batch!;
        byte[] exposed = batch.Items[0].Payload;
        exposed[0] = 99;
        Assert.Equal(7, batch.Items[0].Payload[0]);
    }
}
