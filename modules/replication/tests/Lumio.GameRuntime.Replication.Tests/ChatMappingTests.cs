using System;
using System.Linq;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Ingress;
using Lumio.GameRuntime.Replication.Chat;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ChatMappingTests
{
    [Fact]
    public void FrozenMappingConstantsMatchC1FieldNames()
    {
        Assert.Equal("lumio.gameplay-envelope.v1", ChatMapping.ContractId);
        Assert.Equal("chat.input", ChatMapping.InputMappingId);
        Assert.Equal(new[] { "text" }, ChatMapping.InputFieldOrder);
        Assert.Equal(512, ChatMapping.MaxTextUtf8Bytes);
        Assert.Equal(1, ChatMapping.MaxChatInputPerSenderPerTick);
        Assert.Equal(64, ChatMapping.IngressQueueCapacity);
        Assert.Equal(ChatIngressCapture.PerConnectionCapacity, ChatMapping.IngressQueueCapacity);
        Assert.Equal("reject", ChatMapping.BoundedInputPolicy);
        Assert.NotEqual("lumio.hello-wire.v1", ChatMapping.ContractId);
    }

    [Fact]
    public void DownstreamMappingReturnsEveryRpcInOrder()
    {
        var sender = new NetEntityId(7, 1);
        var target = new NetEntityId(7, 2);
        var change = new WorldChangeMessage(4, Array.Empty<CreateRecord>(), Array.Empty<FieldChange>(), Array.Empty<NetEntityId>(), new[]
        {
            new ClientRpcRecord(target, "ChatComponent", "OnChatMessage", new object?[] { "one" }, 1, 1, sender, 4),
            new ClientRpcRecord(target, "ChatComponent", "OnChatMessage", new object?[] { "two" }, 2, 2, sender, 4),
        });

        ChatMappingResult mapped = new ChatTypedMapping().ApplyDownstream("C1", System.Text.Encoding.UTF8.GetString(WireCodec.EncodePack(change)));

        Assert.Equal(new[] { "one", "two" }, mapped.Events!.Select(static item => item.Text).ToArray());
    }
}
