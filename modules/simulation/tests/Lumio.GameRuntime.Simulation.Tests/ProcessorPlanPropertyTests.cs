using System.Collections.Generic;
using System.Linq;
using Lumio.Gen.ContractTypes;
using Lumio.GameRuntime.Simulation.Planning;
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

    private static ProcessorDescriptor Descriptor(string id, IReadOnlyList<string> read, IReadOnlyList<string> write) =>
        new(id, ProcessorDescriptorRole.Server, ProcessorDescriptorPhase.ProcessorPlan, "query", read, write, false, null, null, ProcessorDescriptorDeterminismClass.Stable, new ProcessorDescriptorBudget(100, 4), id + ".Diagnostic");
}
