using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Replication.Chat;
using Lumio.GameRuntime.Replication.Mapping;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ChatCommandRuntimeTests
{
    private const string FrozenBlob = "f045da8e2fc2f22c87a5aa6cb80cc7f88da0788e";
    private const string N04Sha256 = "a47e92d663ba8f9726cf8defdacf2f56ebbaf1b93a8be9b7435430fad48bddc0";
    private const string GgPayload = "020000006767";
    private const string GgSha256 = "5dbd584f1718b8bcd0dab4abeea83169f4a990defab81a8316ed845798d92dab";
    private const string GgEventPayload = "0100000000000000010000000000000065000000000000000200000067670700000000000000";
    private const string GgEventSha256 = "9fafc556e56dc024a90caf7c102dfccfed4189c708e0a51b0139aab28277670c";
    private const string EntityTypeMapping = "mapping-entity-identity-entity-type";

    [Fact]
    public void VendoredContractBlobMatchesFrozenC1()
    {
        string path = Path.Combine(FindRepoRoot(), "modules", "replication", "contracts", "gameplay-command-envelope-v1.json");
        Assert.True(File.Exists(path), path);
        Assert.Equal(FrozenBlob, GitBlobSha1(File.ReadAllBytes(path)));
    }

    [Fact]
    public void GeneratedAttributeDeclarationsMatchN04Sha256()
    {
        string path = Path.Combine(FindRepoRoot(), "modules", "ecs", "generated", "attribute-declarations.json");
        Assert.True(File.Exists(path), path);
        string normalized = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        Assert.Equal(N04Sha256, Sha256Hex(Encoding.UTF8.GetBytes(normalized)));
    }

    [Fact]
    public void ChatInputIsTextOnlyWithNoClientSequenceFrameOrSender()
    {
        string[] names = typeof(ChatInput)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Text" }, names);

        ParameterInfo[] parameters = typeof(ChatCommandRuntime)
            .GetMethod(nameof(ChatCommandRuntime.AdmitInput))!
            .GetParameters();
        Assert.DoesNotContain(parameters, static parameter =>
            parameter.Name is "sender" or "senderNetEntityId" or "sequence" or "commandSequence" or "frame" or "clientFrame");
    }

    [Fact]
    public void TypedMappingHasNoPrivateDeliveryQueue()
    {
        FieldInfo[] fields = typeof(ChatTypedMapping).GetFields(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, static field =>
            field.FieldType.Name.Contains("Queue", StringComparison.Ordinal) ||
            field.Name.Contains("inbox", StringComparison.OrdinalIgnoreCase) ||
            field.Name.Contains("pending", StringComparison.OrdinalIgnoreCase));
        Assert.Null(typeof(ChatTypedMapping).GetMethod("TakeDelivered"));
        Assert.Null(typeof(ChatTypedMapping).GetMethod("SubmitInput"));
        Assert.Null(typeof(ChatTypedMapping).GetMethod("SubmitInputCommand"));
    }

    [Fact]
    public void C1ContractCasesRoundTripThroughEnvelope()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ContractPath()));
        foreach (JsonElement testCase in document.RootElement.GetProperty("testCases").EnumerateArray())
        {
            string name = testCase.GetProperty("name").GetString()!;
            string json = testCase.GetProperty("message").GetRawText();
            ChatMappingResult result = ChatEnvelope.Validate(json);
            Assert.True(result.Succeeded, name + ": " + result.Code + " " + result.Detail);
        }

        foreach (JsonElement invalid in document.RootElement.GetProperty("invalidCases").EnumerateArray())
        {
            if (!invalid.GetProperty("validatorCheck").GetBoolean()) continue;
            string name = invalid.GetProperty("name").GetString()!;
            string expected = invalid.GetProperty("expectedRejection").GetString()!;
            string json = invalid.GetProperty("payload").GetRawText();
            ChatMappingResult result = ChatEnvelope.Validate(json);
            Assert.False(result.Succeeded, name);
            Assert.Equal(expected, result.Code);
        }
    }

    [Fact]
    public void AdmitIssuesRuntimeHexNetEntityIdAndRejectsHostMinting()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        ConnectionBinding first = Admit(bindings);
        Assert.True(NetEntityId.TryParse(first.NetEntityId, out _));
        Assert.Equal(32, first.NetEntityId.Length);

        BindingQueryResult hostSupplied = bindings.Admit(
            new AdmitRequest
            {
                Connection = "C-host",
                AccountId = "acct-host",
                RoomId = "room-01",
                EntityType = "player",
                NetEntityId = first.NetEntityId,
            });
        Assert.Equal("request_error", hostSupplied.Outcome);
        Assert.Equal("invalid_binding_shape", hostSupplied.Code);
        Assert.Null(hostSupplied.Binding);
    }

    [Fact]
    public void ValidChatInputUpdatesComponentAndEmitsDeltaEventOnTheSameTick()
    {
        using ChatCommandRuntime runtime = RoomWith(2);
        string sender = Net(runtime, 0);
        string peer = Net(runtime, 1);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("gg")));
        Assert.Equal(0UL, runtime.CurrentTick);
        AssertLastMessage(runtime, sender, string.Empty, 0UL);
        AssertLastMessage(runtime, peer, string.Empty, 0UL);

        ChatTickResult tick = runtime.RunTick(7);
        Assert.Equal(7UL, runtime.CurrentTick);
        Assert.Equal(7UL, tick.AppliedTick);
        AssertLastMessage(runtime, sender, "gg", 7UL);
        AssertLastMessage(runtime, peer, string.Empty, 0UL);
        ChatMessageEvent emitted = Assert.Single(tick.Events);
        Assert.Equal(1UL, emitted.MessageId);
        Assert.Equal(1UL, emitted.RoomSequence);
        Assert.Equal(sender, emitted.SenderNetEntityId);
        Assert.Equal("gg", emitted.Text);
        Assert.Equal(7UL, emitted.AppliedTick);

        (string Payload, string Sha256) wire = EncodeChatEvent(1, 1, SenderU64(sender), "gg", 7);
        string delta = Assert.Single(runtime.BuildDelta("room-01", 7, tick.Revision));
        Assert.Contains("\"messageType\":\"Delta\"", delta, StringComparison.Ordinal);
        Assert.Contains("\"mappingId\":\"chat.event\"", delta, StringComparison.Ordinal);
        Assert.Contains(wire.Payload, delta, StringComparison.Ordinal);
        Assert.Contains(wire.Sha256, delta, StringComparison.Ordinal);
        AssertOk(runtime.ApplyDownstream("observer", delta));
        ChatMessageEvent shown = Assert.Single(runtime.DisplayedEvents("observer"));
        Assert.Equal(emitted.MessageId, shown.MessageId);
        Assert.Equal(emitted.RoomSequence, shown.RoomSequence);
        Assert.Equal(emitted.Text, shown.Text);
        Assert.Equal(emitted.AppliedTick, shown.AppliedTick);
    }

    [Fact]
    public void FrozenChatInputEnvelopeCommitsThroughCommandBuffer()
    {
        using ChatCommandRuntime runtime = RoomWith();
        string sender = Net(runtime, 0);
        ChatMappingResult admitted = runtime.AdmitInputCommand(
            "room-01",
            "C1",
            1,
            InputCommand(GgPayload, GgSha256));
        AssertOk(admitted);
        ChatTickResult tick = runtime.RunTick(7);
        Assert.True(Assert.Single(tick.Results).Succeeded);
        AssertLastMessage(runtime, sender, "gg", 7UL);
        (string Payload, string Sha256) wire = EncodeChatEvent(1, 1, SenderU64(sender), "gg", 7);
        string delta = Assert.Single(runtime.BuildDelta("room-01", 7, tick.Revision));
        Assert.Contains(wire.Payload, delta, StringComparison.Ordinal);
        Assert.Contains(wire.Sha256, delta, StringComparison.Ordinal);
    }

    [Fact]
    public void LengthCapRejectsWithChatTextTooLongAndNoWrite()
    {
        using ChatCommandRuntime runtime = RoomWith();
        string sender = Net(runtime, 0);
        string over = new string('a', 513);
        ChatMappingResult typed = runtime.AdmitInput("room-01", "C1", 1, new ChatInput(over));
        AssertRejected(typed, "chat_text_too_long", "chat.input");

        (string Payload, string Sha256) wire = EncodeChatInput(over);
        ChatMappingResult command = runtime.AdmitInputCommand(
            "room-01",
            "C1",
            1,
            InputCommand(wire.Payload, wire.Sha256));
        AssertRejected(command, "chat_text_too_long", "chat.input");

        ChatTickResult tick = runtime.RunTick(7);
        Assert.Empty(tick.Events);
        AssertLastMessage(runtime, sender, string.Empty, 0UL);
        string delta = Assert.Single(runtime.BuildDelta("room-01", 7, tick.Revision));
        Assert.Contains("\"changedBlocks\":[]", delta, StringComparison.Ordinal);
        Assert.DoesNotContain("chat.event", delta, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondChatInputFromSameSenderInOneTickIsRateRejected()
    {
        using ChatCommandRuntime runtime = RoomWith(2);
        string first = Net(runtime, 0);
        string second = Net(runtime, 1);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("gg")));
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("no")));
        AssertOk(runtime.AdmitInput("room-01", "C2", 1, new ChatInput("ok")));

        ChatTickResult tick = runtime.RunTick(7);
        Assert.Equal(2, tick.Results.Count(static result => result.Succeeded));
        ChatMappingResult rate = Assert.Single(tick.Results, static result => result.Code == "chat_rate_exceeded");
        Assert.Equal("chat.input", rate.MappingId);
        AssertLastMessage(runtime, first, "gg", 7UL);
        AssertLastMessage(runtime, second, "ok", 7UL);
        Assert.Equal(2, tick.Events.Count);
        Assert.DoesNotContain(tick.Events, static item => item.Text == "no");
        Assert.Equal(2, runtime.BuildDelta("room-01", 7, tick.Revision).Count);
    }

    [Fact]
    public void IngressQueueFullRejectsWithoutComponentWrite()
    {
        using ChatCommandRuntime runtime = RoomWith();
        string sender = Net(runtime, 0);
        for (int i = 0; i < ChatMapping.IngressQueueCapacity; i++)
            AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("n" + i)));

        ChatMappingResult overflow = runtime.AdmitInput("room-01", "C1", 1, new ChatInput("overflow"));
        AssertRejected(overflow, "queue_full", "chat.input");
        AssertLastMessage(runtime, sender, string.Empty, 0UL);

        ChatTickResult tick = runtime.RunTick(7);
        Assert.DoesNotContain(tick.Events, static item => item.Text == "overflow");
        AssertLastMessage(runtime, sender, "n0", 7UL);
    }

    [Fact]
    public void NetworkThreadSetMessageFailStopsWithZeroComponentWrite()
    {
        using ChatCommandRuntime runtime = RoomWith();
        string sender = Net(runtime, 0);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("keep")));
        ChatTickResult committed = runtime.RunTick(7);
        AssertLastMessage(runtime, sender, "keep", 7UL);
        Assert.Single(committed.Events);

        ChatMappingResult? offThread = null;
        int workerThreadId = 0;
        var worker = new Thread(() =>
        {
            workerThreadId = Environment.CurrentManagedThreadId;
            offThread = runtime.SetMessage("room-01", sender, "hack");
        });
        worker.IsBackground = true;
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));

        Assert.NotEqual(runtime.OwnerThreadId, workerThreadId);
        Assert.NotNull(offThread);
        Assert.False(offThread!.Value.Succeeded);
        Assert.Equal("runtime_failure", offThread.Value.Code);
        Assert.True(runtime.IsFaulted);
        AssertLastMessage(runtime, sender, "keep", 7UL);

        ChatTickResult afterFault = runtime.RunTick(8);
        Assert.True(runtime.IsFaulted);
        Assert.Empty(afterFault.Events);
        AssertLastMessage(runtime, sender, "keep", 7UL);
    }

    [Fact]
    public void NetworkThreadAdmitQueuesWithoutWritingUntilOwnerTick()
    {
        using ChatCommandRuntime runtime = RoomWith();
        string sender = Net(runtime, 0);
        ChatMappingResult? admitted = null;
        var worker = new Thread(() =>
            admitted = runtime.AdmitInput("room-01", "C1", 1, new ChatInput("gg")));
        worker.IsBackground = true;
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));

        AssertOk(admitted!.Value);
        Assert.False(runtime.IsFaulted);
        AssertLastMessage(runtime, sender, string.Empty, 0UL);
        Assert.Equal(0UL, runtime.CurrentTick);

        ChatTickResult tick = runtime.RunTick(7);
        AssertLastMessage(runtime, sender, "gg", 7UL);
        Assert.Equal("gg", Assert.Single(tick.Events).Text);
    }

    [Fact]
    public void UnauthorizedAndStaleGenerationProduceNoWrite()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        ConnectionBinding bound = Admit(bindings);
        using var runtime = ChatCommandRuntime.Create(bindings);
        AssertOk(runtime.AttachMember("room-01", "C1"));

        ChatMappingResult missing = runtime.AdmitInput("room-01", "missing", 1, new ChatInput("gg"));
        Assert.False(missing.Succeeded);
        ChatTickResult empty = runtime.RunTick(7);
        Assert.Empty(empty.Events);
        AssertLastMessage(runtime, bound.NetEntityId, string.Empty, 0UL);

        AssertOk(bindings.Rebind("C1", "C1-next"));
        ChatMappingResult stale = runtime.AdmitInput("room-01", "C1-next", 1, new ChatInput("gg"));
        Assert.Equal("stale_generation", stale.Code);
        Assert.False(stale.Succeeded);
        Assert.Empty(runtime.RunTick(8).Events);

        ChatMappingResult rebound = runtime.AdmitInput("room-01", "C1-next", 2, new ChatInput("gg"));
        AssertOk(rebound);
        ChatTickResult committed = runtime.RunTick(9);
        Assert.Equal(bound.NetEntityId, Assert.Single(committed.Events).SenderNetEntityId);
    }

    [Fact]
    public void SetMessageAfterEntityDestructionRejectsWithZeroComponentWrite()
    {
        using ChatCommandRuntime runtime = RoomWith(2);
        string first = Net(runtime, 0);
        string second = Net(runtime, 1);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("first")));
        AssertOk(runtime.AdmitInput("room-01", "C2", 1, new ChatInput("peer")));
        ChatTickResult firstTick = runtime.RunTick(7);
        Assert.Equal(2, firstTick.Events.Count);
        AssertLastMessage(runtime, first, "first", 7UL);

        Assert.True(runtime.DestroyEntity(first));
        Assert.False(runtime.TryGetLastMessage(first, out _, out _));
        AssertLastMessage(runtime, second, "peer", 7UL);

        ChatMappingResult destroyedWrite = runtime.SetMessage("room-01", first, "after-destroy");
        Assert.False(destroyedWrite.Succeeded);
        Assert.Equal("runtime_failure", destroyedWrite.Code);
        Assert.False(runtime.IsFaulted);
        Assert.False(runtime.TryGetLastMessage(first, out string? resurrected, out _));
        Assert.Null(resurrected);
        AssertLastMessage(runtime, second, "peer", 7UL);

        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("queued-after-destroy")));
        ChatTickResult secondTick = runtime.RunTick(8);
        Assert.DoesNotContain(secondTick.Events, item => item.SenderNetEntityId == first);
        Assert.False(runtime.TryGetLastMessage(first, out _, out _));
        AssertLastMessage(runtime, second, "peer", 7UL);
    }

    [Fact]
    public void DuplicateAndOutOfOrderEventsAreSuppressedWithoutCorruption()
    {
        using var runtime = ChatCommandRuntime.Create();
        string delta = Delta(GgEventPayload, GgEventSha256, 7, 1);

        AssertOk(runtime.ApplyDownstream("observer", delta));
        ChatMessageEvent first = Assert.Single(runtime.DisplayedEvents("observer"));
        Assert.Equal(1UL, first.RoomSequence);
        Assert.Equal("101", first.SenderNetEntityId);

        ChatMappingResult duplicate = runtime.ApplyDownstream("observer", delta);
        AssertRejected(duplicate, "bad_envelope");
        Assert.Single(runtime.DisplayedEvents("observer"));

        string rollback = Delta(
            EncodeChatEvent(2, 1, 101, "no", 8).Payload,
            EncodeChatEvent(2, 1, 101, "no", 8).Sha256,
            8,
            2);
        ChatMappingResult outOfOrder = runtime.ApplyDownstream("observer", rollback);
        AssertRejected(outOfOrder, "bad_envelope");
        ChatMessageEvent remaining = Assert.Single(runtime.DisplayedEvents("observer"));
        Assert.Equal("gg", remaining.Text);
        Assert.Equal(1UL, remaining.MessageId);
    }

    [Fact]
    public void TwoIdenticalRunsAreDeterministicAndBindTheSameSender()
    {
        ChatMessageEvent[] first = RunOnce();
        ChatMessageEvent[] second = RunOnce();
        Assert.Equal(first, second);
        Assert.Equal(
            first.Select(static item => (item.MessageId, item.RoomSequence, item.AppliedTick)).ToArray(),
            second.Select(static item => (item.MessageId, item.RoomSequence, item.AppliedTick)).ToArray());
        Assert.True(NetEntityId.TryParse(first[0].SenderNetEntityId, out _));
        Assert.True(NetEntityId.TryParse(first[1].SenderNetEntityId, out _));
        Assert.NotEqual(first[0].SenderNetEntityId, first[1].SenderNetEntityId);
        Assert.Equal(1UL, first[0].RoomSequence);
        Assert.Equal(2UL, first[1].RoomSequence);
        Assert.Equal(7UL, first[0].AppliedTick);
        Assert.Equal(8UL, first[1].AppliedTick);
    }

    [Fact]
    public void OrderedRoomDeliveryIsolatesSequencePerRoom()
    {
        EntityBindingQuery bindings = EntityBindingQuery.Create();
        Admit(bindings, "C1", "acct-07", "room-01");
        Admit(bindings, "C2", "acct-08", "room-01");
        Admit(bindings, "C3", "acct-09", "room-02");
        using var runtime = ChatCommandRuntime.Create(bindings, ownsBindings: true);
        AssertOk(runtime.AttachMember("room-01", "C1"));
        AssertOk(runtime.AttachMember("room-01", "C2"));
        AssertOk(runtime.AttachMember("room-02", "C3"));

        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("one")));
        AssertOk(runtime.AdmitInput("room-02", "C3", 1, new ChatInput("iso")));
        ChatTickResult shared = runtime.RunTick(7);
        Assert.Equal(2, shared.Events.Count);
        ChatMessageEvent room01First = Assert.Single(shared.Events, static item => item.Text == "one");
        ChatMessageEvent room02First = Assert.Single(shared.Events, static item => item.Text == "iso");
        Assert.Equal(1UL, room01First.RoomSequence);
        Assert.Equal(1UL, room01First.MessageId);
        Assert.Equal(1UL, room02First.RoomSequence);
        Assert.Equal(1UL, room02First.MessageId);

        string delta01 = Assert.Single(runtime.BuildDelta("room-01", 7, shared.Revision));
        string delta02 = Assert.Single(runtime.BuildDelta("room-02", 7, shared.Revision));
        (string Payload, string Sha256) wire01 = EncodeChatEvent(1, 1, SenderU64(room01First.SenderNetEntityId), "one", 7);
        (string Payload, string Sha256) wire02 = EncodeChatEvent(1, 1, SenderU64(room02First.SenderNetEntityId), "iso", 7);
        Assert.Contains(wire01.Payload, delta01, StringComparison.Ordinal);
        Assert.DoesNotContain(wire02.Payload, delta01, StringComparison.Ordinal);
        Assert.Contains(wire02.Payload, delta02, StringComparison.Ordinal);
        Assert.DoesNotContain(wire01.Payload, delta02, StringComparison.Ordinal);

        AssertOk(runtime.ApplyDownstream("obs-02", delta02));
        ChatMessageEvent shown02 = Assert.Single(runtime.DisplayedEvents("obs-02"));
        Assert.Equal(1UL, shown02.RoomSequence);
        Assert.Equal("iso", shown02.Text);

        AssertOk(runtime.AdmitInput("room-01", "C2", 1, new ChatInput("two")));
        ChatTickResult later = runtime.RunTick(8);
        ChatMessageEvent room01Second = Assert.Single(later.Events);
        Assert.Equal(2UL, room01Second.RoomSequence);
        Assert.Equal(2UL, room01Second.MessageId);
        Assert.Equal("two", room01Second.Text);
        string later02 = Assert.Single(runtime.BuildDelta("room-02", 8, later.Revision));
        Assert.Contains("\"changedBlocks\":[]", later02, StringComparison.Ordinal);
        (string Payload, string Sha256) wireLater = EncodeChatEvent(2, 2, SenderU64(room01Second.SenderNetEntityId), "two", 8);
        Assert.DoesNotContain(wireLater.Payload, later02, StringComparison.Ordinal);
    }

    [Fact]
    public void C1RegistersEntityIdentityAsKindState()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ContractPath()));
        JsonElement mappings = document.RootElement.GetProperty("mappings");
        Assert.Equal("state", mappings.GetProperty("entity.identity").GetProperty("kind").GetString());
        Assert.Equal(
            new[] { "netEntityId", "entityType", "unmappedMark" },
            mappings.GetProperty("entity.identity").GetProperty("fieldOrder").EnumerateArray()
                .Select(static item => item.GetString()).ToArray());
        Assert.Equal("command", mappings.GetProperty("chat.input").GetProperty("kind").GetString());
        Assert.Equal("event", mappings.GetProperty("chat.event").GetProperty("kind").GetString());
        Assert.Equal("componentState", mappings.GetProperty("chat.component").GetProperty("kind").GetString());
        Assert.False(mappings.TryGetProperty(EntityTypeMapping, out _));
        Assert.False(mappings.TryGetProperty("mapping-entity-identity-claimed-mark", out _));
    }

    [Fact]
    public void UnregisteredIdentityMappingInSnapshotIsRejected()
    {
        string snapshot = "{\"messageType\":\"FullSnapshot\",\"tickId\":7,\"revision\":1,\"stateBlocks\":[" +
                          Block(EntityTypeMapping, GgPayload, GgSha256) + "]}";
        ChatMappingResult result = ChatEnvelope.Validate(snapshot);
        AssertRejected(result, "state_block_kind_mismatch");
    }

    [Fact]
    public void BuildFullSnapshotEncodesTwoLiveEntityIdentities()
    {
        EntityBindingQuery bindings = EntityBindingQuery.Create();
        ConnectionBinding player = Admit(bindings, "C1", "acct-07", "room-01", "player");
        ConnectionBinding bot = Admit(bindings, "C2", "acct-08", "room-01", "bot");
        using var runtime = ChatCommandRuntime.Create(bindings, ownsBindings: true);
        AssertOk(runtime.AttachMember("room-01", "C1"));
        AssertOk(runtime.AttachMember("room-01", "C2"));

        ulong playerId = SenderU64(player.NetEntityId);
        ulong botId = SenderU64(bot.NetEntityId);
        string snapshot = runtime.BuildFullSnapshot("room-01", 7, 1);
        AssertOk(ChatEnvelope.Validate(snapshot));
        Assert.DoesNotContain("chat.event", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("chat.component", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(EntityTypeMapping, snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-entity-identity-claimed-mark", snapshot, StringComparison.Ordinal);
        Assert.True(TryDecodeEntityIdentity(snapshot, out DecodedIdentity[] records), snapshot);
        Assert.Equal(2, records.Length);

        DecodedIdentity[] expected = playerId < botId
            ? new[]
            {
                new DecodedIdentity(playerId, "player", string.Empty),
                new DecodedIdentity(botId, "bot", string.Empty),
            }
            : new[]
            {
                new DecodedIdentity(botId, "bot", string.Empty),
                new DecodedIdentity(playerId, "player", string.Empty),
            };
        Assert.Equal(expected, records);
        Assert.Equal(new[] { playerId, botId }.OrderBy(static id => id).ToArray(),
            records.Select(static record => record.NetEntityId).ToArray());
        Assert.DoesNotContain(records, static record => record.UnmappedMark == "mark");
    }

    [Fact]
    public void BuildFullSnapshotHasEmptyStateBlocksWhenRoomHasNoLiveEntities()
    {
        EntityBindingQuery bindings = EntityBindingQuery.Create();
        using var runtime = ChatCommandRuntime.Create(bindings, ownsBindings: true);
        string snapshot = runtime.BuildFullSnapshot("room-01", 0, 0);
        AssertOk(ChatEnvelope.Validate(snapshot));
        Assert.Contains("\"stateBlocks\":[]", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("entity.identity", snapshot, StringComparison.Ordinal);
        Assert.True(TryDecodeEntityIdentity(snapshot, out DecodedIdentity[] records), snapshot);
        Assert.Empty(records);
    }

    [Fact]
    public void BuildFullSnapshotOmitsChatEventAndUnregisteredIdentityMappings()
    {
        using ChatCommandRuntime runtime = RoomWith(2);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("gg")));
        ChatTickResult tick = runtime.RunTick(7);

        string snapshot = runtime.BuildFullSnapshot("room-01", 7, tick.Revision);
        Assert.Contains("\"messageType\":\"FullSnapshot\"", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("chat.event", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("chat.component", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(EntityTypeMapping, snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("mapping-entity-identity-claimed-mark", snapshot, StringComparison.Ordinal);
        AssertOk(ChatEnvelope.Validate(snapshot));

        ChatMappingResult replay = runtime.ApplyDownstream(
            "observer",
            "{\"messageType\":\"FullSnapshot\",\"tickId\":7,\"revision\":1,\"stateBlocks\":[" +
            Block("chat.event", GgEventPayload, GgEventSha256) + "]}");
        AssertRejected(replay, "state_block_kind_mismatch");
        Assert.Empty(runtime.DisplayedEvents("observer"));

        AssertOk(runtime.ApplyDownstream("observer", snapshot));
        Assert.Empty(runtime.DisplayedEvents("observer"));
    }

    [Fact]
    public void LiveSeq2AfterSnapshotIsSessionBaselineNotBadEnvelope()
    {
        using ChatCommandRuntime runtime = RoomWith();
        string sender = Net(runtime, 0);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("one")));
        runtime.RunTick(7);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("two")));
        ChatTickResult second = runtime.RunTick(8);

        string snapshot = runtime.BuildFullSnapshot("room-01", 8, second.Revision);
        AssertOk(runtime.ApplyDownstream("observer", snapshot));
        Assert.Empty(runtime.DisplayedEvents("observer"));

        string live = Assert.Single(runtime.BuildDelta("room-01", 8, second.Revision));
        ChatMappingResult applied = runtime.ApplyDownstream("observer", live);
        AssertOk(applied);
        ChatMessageEvent shown = Assert.Single(runtime.DisplayedEvents("observer"));
        Assert.Equal(2UL, shown.RoomSequence);
        Assert.Equal(2UL, shown.MessageId);
        Assert.Equal("two", shown.Text);

        ChatMappingResult duplicate = runtime.ApplyDownstream("observer", live);
        AssertRejected(duplicate, "bad_envelope");
        Assert.Single(runtime.DisplayedEvents("observer"));
        Assert.Equal(sender, runtime.LiveNetEntityIds[0]);
    }

    [Fact]
    public void ExtraClientSequenceOrSenderOnEnvelopeIsRejectedWithNoWrite()
    {
        using ChatCommandRuntime runtime = RoomWith();
        string sender = Net(runtime, 0);
        string extra = "{\"messageType\":\"InputCommand\",\"commandSequence\":9,\"commands\":[" +
                       Block("chat.input", GgPayload, GgSha256) + "]}";
        ChatMappingResult result = runtime.AdmitInputCommand("room-01", "C1", 1, extra);
        AssertRejected(result, "bad_envelope");
        Assert.Empty(runtime.RunTick(7).Events);
        AssertLastMessage(runtime, sender, string.Empty, 0UL);
    }

    [Fact]
    public void BuildDeltaIsIdempotentAndDoesNotDequeue()
    {
        using ChatCommandRuntime runtime = RoomWith();
        string sender = Net(runtime, 0);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("gg")));
        ChatTickResult tick = runtime.RunTick(7);
        string first = Assert.Single(runtime.BuildDelta("room-01", 7, tick.Revision));
        string second = Assert.Single(runtime.BuildDelta("room-01", 7, tick.Revision));
        Assert.Equal(first, second);
        (string Payload, string Sha256) wire = EncodeChatEvent(1, 1, SenderU64(sender), "gg", 7);
        Assert.Contains(wire.Payload, first, StringComparison.Ordinal);
    }

    private static ChatMessageEvent[] RunOnce()
    {
        using ChatCommandRuntime runtime = RoomWith(2);
        AssertOk(runtime.AdmitInput("room-01", "C1", 1, new ChatInput("gg")));
        ChatTickResult first = runtime.RunTick(7);
        AssertOk(runtime.AdmitInput("room-01", "C2", 1, new ChatInput("ok")));
        ChatTickResult second = runtime.RunTick(8);
        return first.Events.Concat(second.Events).ToArray();
    }

    private static ChatCommandRuntime RoomWith(int members = 1)
    {
        EntityBindingQuery bindings = EntityBindingQuery.Create();
        for (int i = 0; i < members; i++)
        {
            string connection = i == 0 ? "C1" : "C" + (i + 1).ToString(CultureInfo.InvariantCulture);
            string account = "acct-" + (7 + i).ToString(CultureInfo.InvariantCulture);
            ConnectionBinding bound = Admit(bindings, connection, account);
            Assert.True(NetEntityId.TryParse(bound.NetEntityId, out _), bound.NetEntityId);
        }

        var runtime = ChatCommandRuntime.Create(bindings, ownsBindings: true);
        for (int i = 0; i < members; i++)
        {
            string connection = i == 0 ? "C1" : "C" + (i + 1).ToString(CultureInfo.InvariantCulture);
            AssertOk(runtime.AttachMember("room-01", connection));
        }

        return runtime;
    }

    private static string Net(ChatCommandRuntime runtime, int index) => runtime.LiveNetEntityIds[index];

    private static ulong SenderU64(string netEntityId)
    {
        Assert.True(ulong.TryParse(netEntityId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value));
        return value;
    }

    private static ConnectionBinding Admit(
        EntityBindingQuery sut,
        string connection = "C1",
        string account = "acct-07",
        string room = "room-01",
        string type = "player")
    {
        BindingQueryResult result = sut.Admit(connection, account, room, type);
        AssertOk(result);
        Assert.True(result.Binding.HasValue);
        return result.Binding.Value;
    }

    private static void AssertOk(BindingQueryResult result)
    {
        Assert.Equal("ok", result.Outcome);
        Assert.Null(result.Code);
    }

    private static void AssertOk(ChatMappingResult result)
    {
        Assert.True(result.Succeeded, result.Code + ": " + result.Detail);
        Assert.Null(result.Code);
    }

    private static void AssertRejected(ChatMappingResult result, string code, string? mappingId = null)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Code);
        Assert.Null(result.Event);
        if (mappingId is not null) Assert.Equal(mappingId, result.MappingId);
    }

    private static void AssertLastMessage(ChatCommandRuntime runtime, string netEntityId, string text, ulong tick)
    {
        Assert.True(runtime.TryGetLastMessage(netEntityId, out string? actual, out ulong actualTick));
        Assert.Equal(text, actual);
        Assert.Equal(tick, actualTick);
    }

    private static string InputCommand(string payload, string sha256) =>
        "{\"messageType\":\"InputCommand\",\"commands\":[" + Block("chat.input", payload, sha256) + "]}";

    private static string Delta(string payload, string sha256, ulong tickId, ulong revision) =>
        "{\"messageType\":\"Delta\",\"tickId\":" + tickId.ToString(CultureInfo.InvariantCulture) +
        ",\"revision\":" + revision.ToString(CultureInfo.InvariantCulture) +
        ",\"changedBlocks\":[" + Block("chat.event", payload, sha256) + "]}";

    private static string Block(string mappingId, string payload, string sha256) =>
        "{\"mappingId\":\"" + mappingId + "\",\"payload\":\"" + payload + "\",\"payloadSha256\":\"" + sha256 + "\"}";

    private readonly record struct DecodedIdentity(ulong NetEntityId, string EntityType, string UnmappedMark);

    private static bool TryDecodeEntityIdentity(string snapshot, out DecodedIdentity[] records)
    {
        records = Array.Empty<DecodedIdentity>();
        using JsonDocument document = JsonDocument.Parse(snapshot);
        if (!document.RootElement.TryGetProperty("stateBlocks", out JsonElement blocks) ||
            blocks.ValueKind != JsonValueKind.Array)
            return false;
        if (blocks.GetArrayLength() == 0) return true;
        if (blocks.GetArrayLength() != 1) return false;

        JsonElement block = blocks[0];
        if (!string.Equals(block.GetProperty("mappingId").GetString(), "entity.identity", StringComparison.Ordinal))
            return false;

        string payloadHex = block.GetProperty("payload").GetString() ?? string.Empty;
        string sha256 = block.GetProperty("payloadSha256").GetString() ?? string.Empty;
        if ((payloadHex.Length & 1) != 0) return false;
        byte[] payload = Convert.FromHexString(payloadHex);
        if (!string.Equals(sha256, Sha256Hex(payload), StringComparison.Ordinal)) return false;

        int offset = 0;
        if (offset + 4 > payload.Length) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
        offset += 4;
        var decoded = new List<DecodedIdentity>((int)count);
        ulong previous = 0;
        for (uint i = 0; i < count; i++)
        {
            if (offset + 8 > payload.Length) return false;
            ulong netEntityId = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset, 8));
            offset += 8;
            if (!TryReadUtf8(payload, ref offset, out string entityType) ||
                !TryReadUtf8(payload, ref offset, out string unmappedMark))
                return false;
            if (i > 0 && netEntityId <= previous) return false;
            if (entityType is not ("player" or "bot")) return false;
            previous = netEntityId;
            decoded.Add(new DecodedIdentity(netEntityId, entityType, unmappedMark));
        }

        if (offset != payload.Length) return false;
        records = decoded.ToArray();
        return true;
    }

    private static bool TryReadUtf8(byte[] data, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset + 4 > data.Length) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;
        if (length > (uint)(data.Length - offset)) return false;
        value = Encoding.UTF8.GetString(data, offset, (int)length);
        offset += (int)length;
        return true;
    }

    private static (string Payload, string Sha256) EncodeChatInput(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] bytes = new byte[4 + utf8.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), (uint)utf8.Length);
        utf8.CopyTo(bytes, 4);
        return (ToHex(bytes), Sha256Hex(bytes));
    }

    private static (string Payload, string Sha256) EncodeChatEvent(
        ulong messageId,
        ulong roomSequence,
        ulong senderNetEntityId,
        string text,
        ulong appliedTick)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] bytes = new byte[8 + 8 + 8 + 4 + utf8.Length + 8];
        int offset = 0;
        WriteU64(bytes, ref offset, messageId);
        WriteU64(bytes, ref offset, roomSequence);
        WriteU64(bytes, ref offset, senderNetEntityId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)utf8.Length);
        offset += 4;
        utf8.CopyTo(bytes, offset);
        offset += utf8.Length;
        WriteU64(bytes, ref offset, appliedTick);
        return (ToHex(bytes), Sha256Hex(bytes));
    }

    private static void WriteU64(byte[] dest, ref int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(dest.AsSpan(offset, 8), value);
        offset += 8;
    }

    private static string ToHex(byte[] value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (byte item in value) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string Sha256Hex(byte[] value) => ToHex(SHA256.HashData(value));

    private static string GitBlobSha1(byte[] content)
    {
        byte[] header = Encoding.UTF8.GetBytes("blob " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0");
#pragma warning disable CA5350
        using var sha = SHA1.Create();
#pragma warning restore CA5350
        sha.TransformBlock(header, 0, header.Length, null, 0);
        sha.TransformFinalBlock(content, 0, content.Length);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string ContractPath() =>
        Path.Combine(FindRepoRoot(), "modules", "replication", "contracts", "gameplay-command-envelope-v1.json");

    private static string FindRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(current, "modules")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Repository root was not found from " + AppContext.BaseDirectory);
    }
}
