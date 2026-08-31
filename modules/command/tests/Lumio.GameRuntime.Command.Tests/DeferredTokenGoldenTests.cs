using System.Collections.Generic;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class DeferredTokenGoldenTests
{
    [Fact]
    public void TokenScopeIncludesTickProcessorAndLocalSequence()
    {
        var first = new DeferredEntityToken(12UL, "processor-a", 1UL);
        var secondProcessor = new DeferredEntityToken(12UL, "processor-b", 1UL);
        var nextTick = new DeferredEntityToken(13UL, "processor-a", 1UL);

        Assert.NotEqual(first, secondProcessor);
        Assert.NotEqual(first, nextTick);
        Assert.Equal("12:default:processor-a:0:1", first.CanonicalKey);

        var map = new DeferredEntityMap(12UL);
        Assert.True(map.TryAdd(first, "local-entity-1", out _));
        Assert.True(map.TryResolve(first, 12UL, out string? resolved));
        Assert.Equal("local-entity-1", resolved);
        Assert.False(map.TryResolve(first, 13UL, out _));
    }

    [Fact]
    public void CreateWriterUsesItsOwnProcessorSequence()
    {
        var a = new ProcessorCommandBuffer(3UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        var b = new ProcessorCommandBuffer(3UL, "processor-b", ProcessorDescriptorPhase.ProcessorPlan);
        a.Writer.Create("avatar", out DeferredEntityToken first);
        b.Writer.Create("avatar", out DeferredEntityToken second);
        Assert.Equal(1UL, first.LocalSequence);
        Assert.Equal(1UL, second.LocalSequence);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DeferredTokenFromAnotherWorldIsRejected()
    {
        var source = new ProcessorCommandBuffer(3UL, "world-a", "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        DeferredEntityToken token = source.Writer.AllocateDeferredEntity();
        var target = new ProcessorCommandBuffer(3UL, "world-b", "processor-b", ProcessorDescriptorPhase.ProcessorPlan);
        Assert.Equal(CommandAppendStatus.Rejected, target.Writer.Write(token, "avatar", "health").Status);
    }

    [Fact]
    public void CanonicalIdentityIncludesWorldAndBufferGeneration()
    {
        var worldA = new DeferredEntityToken(3UL, "world-a", "processor-a", 1UL, 1UL);
        var worldB = new DeferredEntityToken(3UL, "world-b", "processor-a", 1UL, 1UL);
        var nextGeneration = new DeferredEntityToken(3UL, "world-a", "processor-a", 2UL, 1UL);

        Assert.NotEqual(worldA.CanonicalKey, worldB.CanonicalKey);
        Assert.NotEqual(worldA.CanonicalKey, nextGeneration.CanonicalKey);

        Command first = StructuralCommand.Write(
            new CommandSortKey(ProcessorDescriptorPhase.ProcessorPlan, "processor-a", 1UL),
            worldA, "avatar", "health");
        Command second = StructuralCommand.Write(
            new CommandSortKey(ProcessorDescriptorPhase.ProcessorPlan, "processor-a", 1UL),
            worldB, "avatar", "health");
        Command third = StructuralCommand.Write(
            new CommandSortKey(ProcessorDescriptorPhase.ProcessorPlan, "processor-a", 1UL),
            nextGeneration, "avatar", "health");

        Assert.NotEqual(first.CanonicalDigestHex, second.CanonicalDigestHex);
        Assert.NotEqual(first.CanonicalDigestHex, third.CanonicalDigestHex);
    }

    [Fact]
    public void CanonicalIdentityEscapesDelimiterBearingIdentifiers()
    {
        var first = new DeferredEntityToken(3UL, "world:a", "processor", 1UL, 1UL);
        var second = new DeferredEntityToken(3UL, "world", "a:processor", 1UL, 1UL);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first.CanonicalKey, second.CanonicalKey);
        Assert.NotEqual(first.CanonicalBytes.ToArray(), second.CanonicalBytes.ToArray());
    }
}
