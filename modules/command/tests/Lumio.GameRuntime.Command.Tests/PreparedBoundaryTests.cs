using System;
using System.Collections.Generic;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class PreparedBoundaryTests
{
    [Fact]
    public void InvalidTargetIsRejectedBeforeApply()
    {
        var buffer = new ProcessorCommandBuffer(1UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("missing");
        CommandPreflightResult result = new CommandPreflightValidator(new CommandPreflightOptions
        {
            Context = new MissingEntityContext()
        }).TryPrepare(new CommandBufferMerger().Merge(1UL, new[] { buffer.Seal() }));
        Assert.Equal(CommandPreflightStatus.Rejected, result.Status);
        Assert.Null(result.Delta);
    }

    [Fact]
    public void ValidPreparedDeltaCanBeAppliedIdempotently()
    {
        var buffer = new ProcessorCommandBuffer(2UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("entity-a");
        MergedCommandBatch merged = new CommandBufferMerger().Merge(2UL, new[] { buffer.Seal() });
        PreparedGameDelta prepared = new CommandPreflightValidator().Prepare(merged);
        var executor = new EcsCommandCommitExecutor();
        CommandApplyReceipt first = executor.Apply(prepared);
        CommandApplyReceipt second = executor.Apply(prepared);
        Assert.Equal(CommandApplyStatus.Applied, first.Status);
        Assert.Equal(CommandApplyStatus.AlreadyApplied, second.Status);
        Assert.Equal(first.CanonicalDigest.ToArray(), second.CanonicalDigest.ToArray());
    }

    [Fact]
    public void CreateWriteDestroyResolvesDeferredTargetInStableApplyOrder()
    {
        var buffer = new ProcessorCommandBuffer(5UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Create("avatar", out DeferredEntityToken token);
        buffer.Writer.Write(token, "avatar", "health", new byte[] { 100 });
        buffer.Writer.Destroy(token);
        PreparedGameDelta prepared = new CommandPreflightValidator().Prepare(new CommandBufferMerger().Merge(5UL, new[] { buffer.Seal() }));
        var port = new CapturingPort();
        CommandApplyReceipt receipt = new EcsCommandCommitExecutor(port).Apply(prepared);
        Assert.Equal(CommandApplyStatus.Applied, receipt.Status);
        Assert.Equal(3, port.Calls);
        Assert.All(port.ResolvedTargets, target => Assert.True(target is null || target == "entity-created"));
    }

    private sealed class MissingEntityContext : ICommandValidationContext
    {
        public bool EntityExists(string entityId) => false;
    }

    private sealed class CapturingPort : IEcsCommandCommitPort
    {
        public int Calls { get; private set; }
        public List<string?> ResolvedTargets { get; } = new();
        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId)
        {
            Calls++;
            ResolvedTargets.Add(resolvedEntityId);
            return command.Kind == CommandKind.Create ? EcsCommandPortResult.Applied("entity-created") : EcsCommandPortResult.Applied();
        }
    }
}
