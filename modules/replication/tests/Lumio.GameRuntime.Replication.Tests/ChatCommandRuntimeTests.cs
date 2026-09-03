using System;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Replication.Chat;
using Lumio.GameRuntime.Samples.Username;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ChatCommandRuntimeTests
{
    public ChatCommandRuntimeTests()
    {
        EcsRegistry.Current = GeneratedRegistry.Instance;
    }

    [Fact]
    public void ChatInputWritesLastMessageAndEmitsClientRpc()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        using ChatCommandRuntime runtime = ChatCommandRuntime.Create(bindings);
        BindingQueryResult admitted = bindings.Admit("C1", "acct-07", "room-01", "player");
        NetEntityId id = NetEntityId.Parse(admitted.Binding!.Value.NetEntityId);
        ChatMappingResult admittedInput = runtime.AdmitInput("room-01", "C1", 1, new ChatInput("gg"));
        Assert.True(admittedInput.Succeeded);
        ChatTickResult tick = runtime.RunTick(1);
        Assert.True(tick.Events.Count >= 1);
        BindingQueryResult query = bindings.QueryAttribute(
            "server-authoritative",
            "room-01",
            id.ToHex(),
            "ChatComponent.lastMessageText");
        Assert.Equal("gg", query.Value);
    }
}
