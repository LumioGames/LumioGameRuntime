using Xunit;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class CommandConflictGoldenTests
{
    [Fact]
    public void DuplicateDestroyProducesStableGeneratedError()
    {
        var buffer = new ProcessorCommandBuffer(1UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("entity-a");
        buffer.Writer.Destroy("entity-a");
        CommandPreflightResult result = new CommandPreflightValidator().TryPrepare(new CommandBufferMerger().Merge(1UL, new[] { buffer.Seal() }));
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Equal("InvalidArgument", result.Failure!.GeneratedErrorId);
    }

    [Fact]
    public void CrossProcessorWritesToOneFieldProduceConflictEvidence()
    {
        var first = new ProcessorCommandBuffer(1UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        var second = new ProcessorCommandBuffer(1UL, "processor-b", ProcessorDescriptorPhase.ProcessorPlan);
        first.Writer.Write("entity-a", "avatar", "health");
        second.Writer.Write("entity-a", "avatar", "health");
        MergedCommandBatch merged = new CommandBufferMerger().Merge(1UL, new[] { first.Seal(), second.Seal() });
        CommandPreflightResult result = new CommandPreflightValidator().TryPrepare(merged);
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Equal("InvalidArgument", result.Failure!.GeneratedErrorId);
        Assert.NotNull(result.Failure.Conflict);
    }
}
