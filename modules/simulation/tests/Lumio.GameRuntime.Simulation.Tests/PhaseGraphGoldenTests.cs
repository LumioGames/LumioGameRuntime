using System;
using System.Linq;
using Lumio.GameRuntime.Simulation.Phases;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class PhaseGraphGoldenTests
{
    [Fact]
    public void GraphMatchesGeneratedThirteenPhaseOrderAndHasOneCommitPoint()
    {
        var expected = new[]
        {
            TickPhase.IngressCapture,
            TickPhase.DecodeAndCanonicalize,
            TickPhase.ApplyInputs,
            TickPhase.ProcessorPlan,
            TickPhase.CrossWorldPrepare,
            TickPhase.NativeJobBarrier,
            TickPhase.CommitDecision,
            TickPhase.VoxelCommit,
            TickPhase.EcsCommandBufferCommit,
            TickPhase.GasAndEventFinalize,
            TickPhase.ReplicationProjection,
            TickPhase.SnapshotHashMetrics,
            TickPhase.EgressPublish
        };

        Assert.Equal(expected, PhaseGraph.Default.Phases);
        Assert.Equal(1, PhaseGraph.Default.CommitPoints.Count());
        Assert.Equal(TickPhase.GasAndEventFinalize, PhaseGraph.Default.CommitPoints.Single());
        Assert.True(PhaseGraph.Default.ValidateAgainstGeneratedContract().Succeeded);
    }

    [Fact]
    public void VoxelCommitAndLaterPhasesAreNotCancellable()
    {
        Assert.Equal(CancelPoint.NotCancellable, PhaseContractTable.Default[TickPhase.VoxelCommit].CancelPoint);
        Assert.Equal(Visibility.AfterCommit, PhaseContractTable.Default[TickPhase.ReplicationProjection].Visibility);
        Assert.True(PhaseContractTable.Default[TickPhase.GasAndEventFinalize].IsAuthoritativeCommitPoint);
    }
}
