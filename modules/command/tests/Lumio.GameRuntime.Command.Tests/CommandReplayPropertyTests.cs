using System.Linq;
using Xunit;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class CommandReplayPropertyTests
{
    [Fact]
    public void PreparedDigestIsStableAcrossReplay()
    {
        var buffer = new ProcessorCommandBuffer(3UL, "processor-a", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("entity-a");
        PreparedGameDelta prepared = new CommandPreflightValidator(new CommandPreflightOptions
        {
            Context = AllowAllCommandValidationContext.Instance
        }).Prepare(new CommandBufferMerger().Merge(3UL, new[] { buffer.Seal() }));
        byte[] digest = prepared.CanonicalDigest.ToArray();
        for (int i = 0; i < 100; i++) Assert.Equal(digest, prepared.CanonicalDigest.ToArray());
    }
}
