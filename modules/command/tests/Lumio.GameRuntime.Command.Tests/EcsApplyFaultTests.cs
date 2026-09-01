using System;
using Lumio.GameRuntime.Ecs;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class EcsApplyFaultTests
{
    [Fact]
    public void InfrastructureFailureIsFailStopAndNeverBusinessReject()
    {
        var buffer = new ProcessorCommandBuffer(1UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("entity-a");
        PreparedGameDelta prepared = new CommandPreflightValidator(new CommandPreflightOptions
        {
            Context = AllowAllCommandValidationContext.Instance
        }).Prepare(new CommandBufferMerger().Merge(1UL, new[] { buffer.Seal() }));
        var executor = new EcsCommandCommitExecutor(new FaultPort());
        CommandModule module = CommandModule.Create(executor: executor);
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);
        CommandApplyReceipt result = module.Apply(prepared);
        Assert.Equal(CommandApplyStatus.InfrastructureFault, result.Status);
        Assert.True(executor.IsFaulted);
        Assert.Equal(result.Status, module.Apply(prepared).Status);
        Assert.DoesNotContain(Enum.GetNames<CommandApplyStatus>(), name => name.Contains("Reject", StringComparison.Ordinal));
    }

    [Fact]
    public void WorldFaultsAfterFirstSuccessfulStorageApplyWithoutUndo()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady(throwAfterSuccessfulMutations: 2);
        var buffer = new ProcessorCommandBuffer(21UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Create(CommandEcsHarness.EntityTypeName, out DeferredEntityToken token).IsAccepted);
        Assert.True(buffer.Writer.Write(token, CommandEcsHarness.ComponentName, CommandEcsHarness.FieldId, CommandEcsHarness.FieldBytes).IsAccepted);
        PreparedGameDelta prepared = PrepareWithWorld(harness, 21UL, buffer);
        CommandModule module = RunningWorldModule(harness);

        CommandApplyReceipt result = module.Apply(prepared);

        Assert.Equal(CommandApplyStatus.InfrastructureFault, result.Status);
        Assert.DoesNotContain(Enum.GetNames<CommandApplyStatus>(), name => name.Contains("Reject", StringComparison.Ordinal));
        Assert.Equal(EcsWorldState.Faulted, harness.World.State);
        Assert.Equal(0, harness.Storage.UndoCalls);
        Assert.True(harness.Storage.MutationCalls >= 1);
        Assert.Equal(1, harness.Storage.CreateCalls);
        Assert.Equal(1, harness.Storage.WriteCalls);
    }

    [Fact]
    public void WorldBackedApplyIsIdempotent()
    {
        using CommandEcsHarness harness = CommandEcsHarness.CreateReady();
        var buffer = new ProcessorCommandBuffer(22UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.True(buffer.Writer.Create(CommandEcsHarness.EntityTypeName, out _).IsAccepted);
        PreparedGameDelta prepared = PrepareWithWorld(harness, 22UL, buffer);
        CommandModule module = RunningWorldModule(harness);

        CommandApplyReceipt first = module.Apply(prepared);
        CommandApplyReceipt second = module.Apply(prepared);

        Assert.Equal(CommandApplyStatus.Applied, first.Status);
        Assert.Equal(CommandApplyStatus.AlreadyApplied, second.Status);
        Assert.Equal(first.CanonicalDigest.ToArray(), second.CanonicalDigest.ToArray());
        Assert.Equal(1, harness.Storage.CreateCalls);
        Assert.Equal(EcsWorldState.Running, harness.World.State);
        Assert.DoesNotContain(Enum.GetNames<CommandApplyStatus>(), name => name.Contains("Reject", StringComparison.Ordinal));
    }

    private static PreparedGameDelta PrepareWithWorld(CommandEcsHarness harness, ulong tick, ProcessorCommandBuffer buffer)
    {
        CommandModule module = RunningWorldModule(harness);
        CommandPreflightResult prepared = module.Prepare(new CommandBufferMerger().Merge(tick, new[] { buffer.Seal() }));
        Assert.True(prepared.IsPrepared);
        Assert.NotNull(prepared.Delta);
        return prepared.Delta;
    }

    private static CommandModule RunningWorldModule(CommandEcsHarness harness)
    {
        CommandModule module = CommandModule.Create(world: harness.World);
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);
        return module;
    }

    private sealed class FaultPort : IEcsCommandCommitPort
    {
        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId) => EcsCommandPortResult.Fault();
    }
}
