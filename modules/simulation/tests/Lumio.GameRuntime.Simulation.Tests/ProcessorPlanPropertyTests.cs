using System;
using System.Collections.Generic;
using System.Linq;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Planning;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class ProcessorPlanPropertyTests
{
    [Fact]
    public void DescriptorPermutationProducesTheSameStablePlanAndHash()
    {
        var first = Descriptor("alpha", new[] { "A" }, new[] { "X" });
        var second = Descriptor("beta", new[] { "B" }, new[] { "Y" });

        var a = ProcessorPlanBuilder.Build(new[] { second, first });
        var b = ProcessorPlanBuilder.Build(new[] { first, second });

        Assert.True(a.Succeeded);
        Assert.True(b.Succeeded);
        Assert.Equal(a.Plan!.CanonicalHashHex, b.Plan!.CanonicalHashHex);
        Assert.Equal(new[] { "alpha", "beta" }, a.Plan.OrderedDescriptors.Select(x => x.ProcessorId));
    }

    [Fact]
    public void InterProcessorWriteConflictIsRejectedButSelfReadWriteIsLegal()
    {
        var self = Descriptor("self", new[] { "Health" }, new[] { "Health" });
        Assert.True(ProcessorPlanBuilder.Build(new[] { self }).Succeeded);

        var left = Descriptor("left", new[] { "Input" }, new[] { "Health" });
        var right = Descriptor("right", new[] { "Input" }, new[] { "Health" });
        var result = ProcessorPlanBuilder.Build(new[] { left, right });
        Assert.False(result.Succeeded);
        Assert.Equal("InternalInvariant", result.Failure!.GeneratedErrorId);
    }

    [Fact]
    public void ZeroCommandProcessorBudgetIsRejected()
    {
        ProcessorPlanBuildResult result = ProcessorPlanBuilder.Build(new[]
        {
            Descriptor("zero-command-budget", Array.Empty<string>(), Array.Empty<string>(), maxCommands: 0)
        });

        Assert.False(result.Succeeded);
        Assert.Equal("ManifestMalformed", result.Failure!.GeneratedErrorId);
    }

    public static IEnumerable<object[]> StructuralCommandPhaseCases()
    {
        var allowed = new HashSet<TickPhase>
        {
            TickPhase.ApplyInputs,
            TickPhase.ProcessorPlan,
            TickPhase.CrossWorldPrepare,
            TickPhase.CommitDecision,
            TickPhase.GasAndEventFinalize
        };

        foreach (TickPhase phase in PhaseGraph.Default.Phases)
            yield return new object[] { phase, allowed.Contains(phase) };
    }

    [Theory]
    [MemberData(nameof(StructuralCommandPhaseCases))]
    public void StructuralCommandsAreAllowedOnlyInAdr030BusinessPhases(TickPhase phase, bool expectedAllowed)
    {
        ProcessorPlanBuildResult result = ProcessorPlanBuilder.Build(new[]
        {
            Descriptor("structural", Array.Empty<string>(), Array.Empty<string>(), phase, true)
        });

        Assert.Equal(expectedAllowed, result.Succeeded);
    }

    private static ProcessorDescriptor Descriptor(
        string id,
        IReadOnlyList<string> read,
        IReadOnlyList<string> write,
        TickPhase phase = TickPhase.ProcessorPlan,
        bool mayEmitStructuralCommands = false,
        ulong maxCommands = 4) =>
        new(id, ProcessorDescriptorRole.Server, (ProcessorDescriptorPhase)phase, "query", read, write, mayEmitStructuralCommands, null, null, ProcessorDescriptorDeterminismClass.Stable, new ProcessorDescriptorBudget(100, maxCommands), id + ".Diagnostic");
}
