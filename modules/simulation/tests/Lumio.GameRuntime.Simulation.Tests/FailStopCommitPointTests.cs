using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class FailStopCommitPointTests
{
    [Fact]
    public void PreCommitFailureFaultsSessionAndDoesNotReportCommit()
    {
        var runner = new TickRunner(new TickRunnerOptions(1), new Dictionary<TickPhase, PhaseHandler>
        {
            [TickPhase.ApplyInputs] = _ => throw new InvalidOperationException("boom")
        });
        var result = runner.Run(new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>()));
        Assert.Equal(TickRunStatus.Faulted, result.Status);
        Assert.False(result.IsCommitted);
        Assert.Equal(TickPhase.ApplyInputs, result.FirstFailure!.Phase);
        Assert.True(runner.IsFaulted);
    }

    [Fact]
    public void RepeatingCommittedTickReturnsTheCachedIdempotentResult()
    {
        var runner = new TickRunner(new TickRunnerOptions(1));
        var request = new HostTickRequest(1, 1, Array.Empty<OpaqueIngressView>());
        var first = runner.Run(request);
        var duplicate = runner.Run(request);
        Assert.Equal(TickRunStatus.Succeeded, first.Status);
        Assert.Equal(TickRunStatus.IdempotentSame, duplicate.Status);
        Assert.Equal(first.StateHashHex, duplicate.StateHashHex);
        Assert.Equal(first, duplicate.CachedResult);
    }
}
