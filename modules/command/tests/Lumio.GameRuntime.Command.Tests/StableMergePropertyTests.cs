using System;
using System.Collections.Generic;
using System.Linq;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class StableMergePropertyTests
{
    private static readonly string[] FirstOrder = { "processor-c", "processor-a", "processor-b" };
    private static readonly string[] SecondOrder = { "processor-b", "processor-c", "processor-a" };

    [Fact]
    public void MergeOrderDoesNotDependOnBufferArrivalOrder()
    {
        SealedCommandBuffer[] first = Buffers(FirstOrder);
        SealedCommandBuffer[] second = Buffers(SecondOrder);
        var merger = new CommandBufferMerger();
        MergedCommandBatch left = merger.Merge(4UL, first);
        MergedCommandBatch right = merger.Merge(4UL, second);

        Assert.Equal(left.Commands, right.Commands);
        Assert.Equal(left.CanonicalDigest.ToArray(), right.CanonicalDigest.ToArray());
        Assert.Equal(left.Commands.OrderBy(command => command.SortKey), left.Commands);
    }

    private static SealedCommandBuffer[] Buffers(IEnumerable<string> processors)
    {
        var result = new List<SealedCommandBuffer>();
        foreach (string processor in processors)
        {
            var buffer = new ProcessorCommandBuffer(4UL, processor, ProcessorDescriptorPhase.ProcessorPlan);
            buffer.Writer.Destroy(string.Concat("entity-", processor));
            result.Add(buffer.Seal());
        }

        return result.ToArray();
    }
}
