using System;
using System.Linq;
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

    [Fact]
    public void WorldChangeRoundTripsAllRpcArguments()
    {
        var rpc = new ClientRpcRecord(
            new NetEntityId(7, 101), "ChatComponent", "OnChatMessage",
            new object?[] { "first", "second" }, 4, 5, new NetEntityId(7, 99), 6, Scope.Owner);
        byte[] bytes = WireCodec.EncodePack(new WorldChangeMessage(6, Array.Empty<CreateRecord>(), Array.Empty<FieldChange>(), Array.Empty<NetEntityId>(), new[] { rpc }));

        WorldChangeMessage decoded = Assert.IsType<WorldChangeMessage>(WireCodec.DecodePack(bytes));
        Assert.Equal(new[] { "first", "second" }, decoded.Rpcs[0].Args.Cast<string>().ToArray());
        Assert.Equal(Scope.Owner, decoded.Rpcs[0].Scope);
    }

    [Fact]
    public void InputEnvelopeDecodesEveryCommandBlock()
    {
        const string json = "{\"commands\":[{\"mappingId\":\"chat.input\",\"payload\":\"00000000\",\"payloadSha256\":\"" +
            "df3f619804a92fdb4057192dc43dd748ea778adc52bc498ce80524c014b81119\"},{\"mappingId\":\"chat.input\",\"payload\":\"00000000\",\"payloadSha256\":\"" +
            "df3f619804a92fdb4057192dc43dd748ea778adc52bc498ce80524c014b81119\"}],\"messageType\":\"InputCommand\"}";

        InputCommandMessage decoded = WireCodec.DecodeInput(System.Text.Encoding.UTF8.GetBytes(json), new NetEntityId(7, 1));
        Assert.Equal(2, decoded.Commands.Count);
    }
}
