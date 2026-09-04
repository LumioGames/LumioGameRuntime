using System;
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
}
