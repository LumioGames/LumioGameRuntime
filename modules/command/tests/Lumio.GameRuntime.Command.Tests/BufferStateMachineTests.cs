using System;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class BufferStateMachineTests
{
    [Fact]
    public void OpenBufferCanSealOnlyOnceAndWriterCannotAdvanceLifecycle()
    {
        var buffer = new ProcessorCommandBuffer(7UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.Equal(CommandBufferState.Open, buffer.State);
        Assert.True(buffer.Writer.Destroy("entity-a").IsAccepted);

        SealedCommandBuffer sealedBuffer = buffer.Seal();
        Assert.Equal(CommandBufferState.Sealed, buffer.State);
        Assert.False(buffer.Writer.Destroy("entity-b").IsAccepted);
        Assert.Throws<InvalidOperationException>(() => buffer.Seal());

        var merger = new CommandBufferMerger();
        MergedCommandBatch merged = merger.Merge(7UL, new[] { sealedBuffer });
        Assert.Equal(CommandBufferState.Merged, merged.State);
        Assert.Equal(CommandBufferState.Merged, buffer.State);
    }

    [Fact]
    public void StructuralCommandsRequireDeclaredCapability()
    {
        var buffer = new ProcessorCommandBuffer(1UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan, false);
        CommandAppendResult result = buffer.Writer.Destroy("entity-a");
        Assert.Equal(CommandAppendStatus.Rejected, result.Status);
        Assert.Equal("MessagePermissionDenied", result.GeneratedErrorId);
    }
}
