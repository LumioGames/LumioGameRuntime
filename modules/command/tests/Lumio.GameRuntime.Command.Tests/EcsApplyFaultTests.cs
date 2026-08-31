using Xunit;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class EcsApplyFaultTests
{
    [Fact]
    public void InfrastructureFailureIsFailStopAndNeverBusinessReject()
    {
        var buffer = new ProcessorCommandBuffer(1UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("entity-a");
        PreparedGameDelta prepared = new CommandPreflightValidator().Prepare(new CommandBufferMerger().Merge(1UL, new[] { buffer.Seal() }));
        var executor = new EcsCommandCommitExecutor(new FaultPort());
        CommandModule module = CommandModule.Create(executor: executor);
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);
        CommandApplyReceipt result = module.Apply(prepared);
        Assert.Equal(CommandApplyStatus.InfrastructureFault, result.Status);
        Assert.True(executor.IsFaulted);
        Assert.Equal(result.Status, module.Apply(prepared).Status);
    }

    private sealed class FaultPort : IEcsCommandCommitPort
    {
        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId) => EcsCommandPortResult.Fault();
    }
}
