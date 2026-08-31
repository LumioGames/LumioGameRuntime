using Xunit;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class CommandBudgetTests
{
    [Fact]
    public void ExactBudgetIsAcceptedAndOverflowDoesNotMutate()
    {
        var buffer = new ProcessorCommandBuffer(1UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan, true,
            new CommandBufferBudget(1UL, 100UL));
        Assert.True(buffer.Writer.Destroy("entity-a").IsAccepted);
        CommandBudgetUsage before = buffer.Usage;
        Assert.False(buffer.Writer.Destroy("entity-b").IsAccepted);
        Assert.Equal(before, buffer.Usage);
    }
}
