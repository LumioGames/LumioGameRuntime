using System;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class R5CodecContractTests
{
    [Fact]
    public void WorldChangeRoundTripsThroughTheSingleRuntimeCodec()
    {
        var id = new NetEntityId(7UL, 101UL);
        var pack = new WorldChangeMessage(
            3UL,
            new[] { new CreateRecord("player", id, new[] { new FieldValue("IdentityComponent", "name", "Alice") }) },
            new[] { new FieldChange(id, "IdentityComponent", "name", "Bob", ChangeReason.Sync) },
            new[] { new NetEntityId(7UL, 99UL) },
            Array.Empty<ClientRpcRecord>());

        byte[] bytes = WireCodec.EncodePack(pack);
        WorldChangeMessage decoded = Assert.IsType<WorldChangeMessage>(WireCodec.DecodePack(bytes));
        Assert.Equal(3UL, decoded.Tick);
        Assert.Equal("Alice", decoded.Creates[0].Fields[0].Value);
        Assert.Equal("Bob", decoded.Fields[0].Value);
        Assert.Equal(new NetEntityId(7UL, 99UL), decoded.Destroys[0]);
    }

    [Fact]
    public void LegacyEnvelopeShapesAreRejected()
    {
        Assert.Throws<FormatException>(() => WireCodec.DecodePack(System.Text.Encoding.UTF8.GetBytes(
            "{\"messageType\":\"FullSnapshot\",\"tickId\":1,\"revision\":1,\"stateBlocks\":[]}")));
    }
}
