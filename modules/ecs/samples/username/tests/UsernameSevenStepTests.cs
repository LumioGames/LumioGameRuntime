extern alias UsernameServer;
extern alias UsernameClient;

using System;
using System.Collections.Generic;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using UsernameClient::Lumio.GameRuntime.Samples.Username.Components.Identity;
using UsernameClient::Lumio.GameRuntime.Samples.Username.EntityTypes;
using UsernameClient::Lumio.GameRuntime.Samples.Username.Host;
using UsernameServer::Lumio.GameRuntime.Samples.Username.Components.Chat;
using UsernameServer::Lumio.GameRuntime.Samples.Username.Components.Identity;
using UsernameServer::Lumio.GameRuntime.Samples.Username.EntityTypes;
using UsernameServer::Lumio.GameRuntime.Samples.Username.Host;
using Xunit;
using ClientChat = UsernameClient::Lumio.GameRuntime.Samples.Username.Components.Chat.ChatComponent;
using ClientIdentity = UsernameClient::Lumio.GameRuntime.Samples.Username.Components.Identity.IdentityComponent;
using ClientPlayer = UsernameClient::Lumio.GameRuntime.Samples.Username.EntityTypes.PlayerEntity;
using ServerChat = UsernameServer::Lumio.GameRuntime.Samples.Username.Components.Chat.ChatComponent;
using ServerIdentity = UsernameServer::Lumio.GameRuntime.Samples.Username.Components.Identity.IdentityComponent;
using ServerPlayer = UsernameServer::Lumio.GameRuntime.Samples.Username.EntityTypes.PlayerEntity;

namespace Lumio.GameRuntime.Samples.Username.Tests;

public sealed class UsernameSevenStepTests
{
    private const ulong InstanceId = 0x1000000000000001UL;

    [Fact]
    public void SevenStepUsernameDemo()
    {
        WorldManager server = ServerBootstrap.Boot(InstanceId);
        WorldManager client = ClientBootstrap.Boot();
        MemorySink sink = new();
        server.SnapshotSink = sink;

        Assert.True(server.World.Single<WorldSaveComponent>() is not null);
        Assert.Equal(InstanceId, server.World.InstanceId);

        ServerBootstrap.AdmitPlayer(server, "acct-07");
        server.Tick();
        NetEntityId player = default;
        foreach (ServerIdentity identity in server.World.Each<ServerIdentity>())
        {
            if (identity.AccountId == "acct-07")
            {
                player = identity.Entity;
                break;
            }
        }

        Assert.False(player.IsDefault);
        Assert.Equal(InstanceId, player.InstanceId);
        Assert.True(player.Counter >= 1UL);
        server.BindSelf("C1", player);

        Pump(server, client);

        Assert.Equal(player, client.World.Self.Id);
        bool sawWorld = false;
        foreach (WorldSaveComponent _ in client.World.Each<WorldSaveComponent>())
            sawWorld = true;
        Assert.True(sawWorld);

        IReadOnlyList<string> life = client.World.LifecycleOf(player);
        Assert.Equal(new[] { "Awake", "PostAttribute", "Start" }, life);
        Assert.True(client.World.TypeOf(player).Is<ClientPlayer>());
        Assert.True(server.World.TypeOf(player).Is<ServerPlayer>());

        client.World.Self.Get<ClientIdentity>().Name.Value = "ABCD";
        client.Tick();
        Pump(server, client);
        Assert.Equal("ABCD", server.World.Get<ServerIdentity>(player).Name.Value);
        Assert.Equal("ABCD", client.World.Get<ClientIdentity>(player).Name.Value);

        client.World.Self.Get<ClientIdentity>().Name.Value = "this-name-is-way-too-long";
        client.Tick();
        Pump(server, client);
        Assert.Equal("ABCD", server.World.Get<ServerIdentity>(player).Name.Value);

        client.World.Self.Get<ClientChat>().Say("gg");
        client.Tick();
        Pump(server, client);
        Assert.Equal("gg", server.World.Get<ServerChat>(player).LastMessageText);
        Assert.True(server.World.Get<ServerChat>(player).LastMessageTick > 0UL);

        Assert.Equal("ABCD", ClientUsage.NameOf(client.World, player));
        Assert.True(ClientUsage.IsPlayer(client.World, player));

        ServerBootstrap.Save(server, "slot-1");
        server.Tick();
        Assert.NotNull(sink.Last);
        WorldManager restored = ServerBootstrap.Restore(sink.Last!);
        restored.Start(Thread.CurrentThread);
        Assert.True(restored.World.IsLive(player));
        Assert.Equal("ABCD", restored.World.Get<ServerIdentity>(player).Name.Value);
        Assert.False(restored.World.Get<ServerIdentity>(player).Connected);
        EntityOrder extra = restored.World.Commands.Create<ServerPlayer>();
        extra.Get<ServerIdentity>().AccountId = "acct-new";
        restored.Tick();
        NetEntityId newer = extra.AssignedId;
        Assert.NotEqual(player, newer);
        Assert.True(newer.Counter >= player.Counter);
    }

    [Fact]
    public void DeterministicChatOrderAcrossTwoRuns()
    {
        string first = RunChatRound();
        string second = RunChatRound();
        Assert.Equal(first, second);
    }

    [Fact]
    public void EventOutboxDoesNotGrowAcrossOneThousandTicks()
    {
        WorldManager server = ServerBootstrap.Boot(InstanceId);
        ServerBootstrap.AdmitPlayer(server, "acct-07");
        server.Tick();
        NetEntityId player = default;
        foreach (ServerIdentity identity in server.World.Each<ServerIdentity>())
            if (identity.AccountId == "acct-07") player = identity.Entity;
        server.BindSelf("C1", player);
        server.Tick();
        _ = server.DrainOutbox();

        int baseline = GC.GetAllocatedBytesForCurrentThread() > 0 ? CountTracked(server) : CountTracked(server);
        for (int i = 0; i < 1000; i++)
        {
            server.Enqueue(new InputCommandMessage("chat.input", player, EncodeText("m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            server.Tick();
            _ = server.DrainOutbox();
        }

        Assert.Equal(baseline, CountTracked(server));
    }

    private static int CountTracked(WorldManager server) =>
        server.World.PendingRpcs.Count + server.World.Dirty.Count + server.DrainOutbox().Count;

    private static string RunChatRound()
    {
        WorldManager server = ServerBootstrap.Boot(InstanceId);
        var ids = new List<NetEntityId>();
        for (int i = 0; i < 100; i++)
        {
            ServerBootstrap.AdmitPlayer(server, "acct-" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
            server.Tick();
        }

        foreach (ServerIdentity identity in server.World.Each<ServerIdentity>())
        {
            if (identity.AccountId.StartsWith("acct-", StringComparison.Ordinal))
                ids.Add(identity.Entity);
        }

        ids.Sort();
        var shuffled = new List<NetEntityId>(ids);
        shuffled.Reverse();
        for (int i = 0; i < shuffled.Count; i++)
        {
            server.BindSelf("C" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), shuffled[i]);
            server.Enqueue(new InputCommandMessage("chat.input", shuffled[i], EncodeText("hi")));
        }

        server.Tick();
        var lines = new List<string>();
        foreach (WorldMessage message in server.DrainOutbox())
        {
            if (message is not WorldChangeMessage change) continue;
            for (int r = 0; r < change.Rpcs.Count; r++)
            {
                ClientRpcRecord rpc = change.Rpcs[r];
                lines.Add(string.Join("|", rpc.MessageId, rpc.RoomSequence, rpc.Sender.ToHex(), rpc.AppliedTick));
            }
        }

        return string.Join("\n", lines);
    }

    private static void Pump(WorldManager server, WorldManager client)
    {
        server.Tick();
        foreach (WorldMessage message in server.DrainOutbox())
            ClientBootstrap.OnNetworkMessage(client, message);
        client.Tick();
        foreach (WorldMessage message in client.DrainOutbox())
            server.Enqueue(message);
        server.Tick();
        foreach (WorldMessage message in server.DrainOutbox())
            ClientBootstrap.OnNetworkMessage(client, message);
        client.Tick();
    }

    private static byte[] EncodeText(string text)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] bytes = new byte[4 + utf8.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, bytes, 4, utf8.Length);
        return bytes;
    }

    private sealed class MemorySink : ISnapshotSink
    {
        public byte[]? Last;

        public void Write(string slot, ReadOnlyMemory<byte> snapshot) => Last = snapshot.ToArray();
    }
}
