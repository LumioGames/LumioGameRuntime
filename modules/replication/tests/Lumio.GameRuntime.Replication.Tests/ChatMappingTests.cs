using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Replication.Chat;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ChatMappingTests
{
    private const string FrozenBlob = "f558b4042ed180a9ea53a74d04b5865442bee025";
    private const string GgPayload = "020000006767";
    private const string GgSha256 = "5dbd584f1718b8bcd0dab4abeea83169f4a990defab81a8316ed845798d92dab";
    private const string GgEventPayload = "0100000000000000010000000000000065000000000000000200000067670700000000000000";
    private const string GgEventSha256 = "9fafc556e56dc024a90caf7c102dfccfed4189c708e0a51b0139aab28277670c";

    [Fact]
    public void VendoredContractBlobMatchesFrozenC1()
    {
        string path = Path.Combine(
            FindRepoRoot(),
            "modules",
            "replication",
            "contracts",
            "gameplay-command-envelope-v1.json");
        Assert.True(File.Exists(path), path);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(FrozenBlob, GitBlobSha1(bytes));
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

        ParameterInfo[] parameters = typeof(ChatTypedMapping)
            .GetMethod(nameof(ChatTypedMapping.SubmitInput))!
            .GetParameters();
        Assert.DoesNotContain(parameters, static parameter =>
            parameter.Name is "sender" or "senderNetEntityId" or "sequence" or "commandSequence" or "frame" or "clientFrame");
    }

    [Fact]
    public void FrozenMappingConstantsMatchC1FieldNames()
    {
        Assert.Equal("lumio.gameplay-envelope.v1", ChatMapping.ContractId);
        Assert.Equal("chat.input", ChatMapping.InputMappingId);
        Assert.Equal("chat.event", ChatMapping.EventMappingId);
        Assert.Equal(new[] { "text" }, ChatMapping.InputFieldOrder);
        Assert.Equal(
            new[] { "messageId", "roomSequence", "senderNetEntityId", "text", "appliedTick" },
            ChatMapping.EventFieldOrder);
        Assert.Equal(512, ChatMapping.MaxTextUtf8Bytes);
        Assert.Equal(1, ChatMapping.MaxChatInputPerSenderPerTick);
        Assert.Equal("reject", ChatMapping.BoundedInputPolicy);
        Assert.NotEqual("lumio.hello-wire.v1", ChatMapping.ContractId);
    }

    [Fact]
    public void TextOnlyInputMapsEventSenderToBoundNetEntityId()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));

        ChatMappingResult result = mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput("gg"));

        AssertOk(result);
        ChatMessageEvent mapped = Assert.Single(mapping.TakeDelivered("C1"));
        Assert.Equal(1UL, mapped.MessageId);
        Assert.Equal(1UL, mapped.RoomSequence);
        Assert.Equal("101", mapped.SenderNetEntityId);
        Assert.Equal("gg", mapped.Text);
        Assert.Equal(7UL, mapped.AppliedTick);
        Assert.Equal(mapped, result.Event);
        AssertUnchangedChatComponent(bindings, "101");
    }

    [Fact]
    public void FrozenChatInputEnvelopeRoundtripsGgGolden()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));

        ChatMappingResult result = mapping.SubmitInputCommand(
            "room-01",
            "C1",
            1,
            7,
            InputCommand(GgPayload, GgSha256));

        AssertOk(result);
        ChatMessageEvent mapped = Assert.Single(mapping.TakeDelivered("C1"));
        Assert.Equal("101", mapped.SenderNetEntityId);
        Assert.Equal("gg", mapped.Text);
        string delta = mapping.BuildDeltaJson("room-01", 7, 1);
        Assert.Contains("\"mappingId\":\"chat.event\"", delta, StringComparison.Ordinal);
        Assert.Contains(GgEventPayload, delta, StringComparison.Ordinal);
        Assert.Contains(GgEventSha256, delta, StringComparison.Ordinal);
    }

    [Fact]
    public void LengthCapRejectsWithChatTextTooLongAndNoWrite()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));

        string over = new string('a', 513);
        ChatMappingResult typed = mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput(over));
        AssertRejected(typed, "chat_text_too_long", "chat.input");
        Assert.Empty(mapping.TakeDelivered("C1"));
        AssertUnchangedChatComponent(bindings, "101");

        ChatMappingResult wire = mapping.SubmitInputCommand(
            "room-01",
            "C1",
            1,
            7,
            InputCommand(EncodeChatInput(over).Payload, EncodeChatInput(over).Sha256));
        AssertRejected(wire, "chat_text_too_long", "chat.input");
        Assert.Empty(mapping.TakeDelivered("C1"));
        AssertUnchangedChatComponent(bindings, "101");
    }

    [Fact]
    public void SecondChatInputFromSameSenderInOneTickIsRateRejected()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        AssertOk(Admit(bindings, connection: "C2", account: "acct-08", net: "102"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));
        AssertOk(mapping.AttachMember("room-01", "C2"));

        AssertOk(mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput("gg")));
        ChatMappingResult second = mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput("no"));
        AssertRejected(second, "chat_rate_exceeded", "chat.input");
        AssertOk(mapping.SubmitInput("room-01", "C2", 1, 7, new ChatInput("ok")));

        ChatMessageEvent[] delivered = mapping.TakeDelivered("C1");
        Assert.Equal(2, delivered.Length);
        Assert.Equal("gg", delivered[0].Text);
        Assert.Equal("ok", delivered[1].Text);
        Assert.DoesNotContain(delivered, static item => item.Text == "no");
        AssertUnchangedChatComponent(bindings, "101");
    }

    [Fact]
    public void UnauthorizedAndStaleGenerationProduceNoWrite()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));

        ChatMappingResult missing = mapping.SubmitInput("room-01", "missing", 1, 7, new ChatInput("gg"));
        Assert.False(missing.Succeeded);
        Assert.Null(missing.Event);
        Assert.Empty(mapping.TakeDelivered("C1"));

        AssertOk(bindings.Rebind("C1", "C1-next"));
        ChatMappingResult stale = mapping.SubmitInput("room-01", "C1-next", 1, 7, new ChatInput("gg"));
        Assert.Equal("stale_generation", stale.Code);
        Assert.False(stale.Succeeded);
        Assert.Empty(mapping.TakeDelivered("C1-next"));
        AssertUnchangedChatComponent(bindings, "101");

        ChatMappingResult rebound = mapping.SubmitInput("room-01", "C1-next", 2, 7, new ChatInput("gg"));
        AssertOk(rebound);
        Assert.Equal("101", rebound.Event!.Value.SenderNetEntityId);
    }

    [Fact]
    public void OrderedRoomDeliveryReachesEveryPermittedMember()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        AssertOk(Admit(bindings, connection: "C2", account: "acct-08", net: "102"));
        AssertOk(Admit(bindings, connection: "C3", account: "acct-09", room: "room-02", net: "201"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));
        AssertOk(mapping.AttachMember("room-01", "C2"));
        AssertOk(mapping.AttachMember("room-02", "C3"));

        AssertOk(mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput("one")));
        AssertOk(mapping.SubmitInput("room-01", "C2", 1, 8, new ChatInput("two")));

        ChatMessageEvent[] first = mapping.TakeDelivered("C1");
        ChatMessageEvent[] second = mapping.TakeDelivered("C2");
        Assert.Equal(2, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(1UL, first[0].RoomSequence);
        Assert.Equal(2UL, first[1].RoomSequence);
        Assert.Equal("101", first[0].SenderNetEntityId);
        Assert.Equal("102", first[1].SenderNetEntityId);
        Assert.Empty(mapping.TakeDelivered("C3"));
    }

    [Fact]
    public void DuplicateAndOutOfOrderEventsAreSuppressedWithoutCorruption()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        var mapping = new ChatTypedMapping(bindings);
        string delta = Delta(GgEventPayload, GgEventSha256, 7, 1);

        AssertOk(mapping.ApplyDownstream("observer", delta));
        ChatMessageEvent first = Assert.Single(mapping.DisplayedEvents("observer"));
        Assert.Equal(1UL, first.RoomSequence);
        Assert.Equal("101", first.SenderNetEntityId);

        ChatMappingResult duplicate = mapping.ApplyDownstream("observer", delta);
        AssertRejected(duplicate, "bad_envelope");
        Assert.Single(mapping.DisplayedEvents("observer"));

        string rollback = Delta(
            EncodeChatEvent(2, 1, 101, "no", 8).Payload,
            EncodeChatEvent(2, 1, 101, "no", 8).Sha256,
            8,
            2);
        ChatMappingResult outOfOrder = mapping.ApplyDownstream("observer", rollback);
        AssertRejected(outOfOrder, "bad_envelope");
        ChatMessageEvent remaining = Assert.Single(mapping.DisplayedEvents("observer"));
        Assert.Equal("gg", remaining.Text);
        Assert.Equal(1UL, remaining.MessageId);
    }

    [Fact]
    public void TwoIdenticalRunsAreDeterministicAndBindTheSameSender()
    {
        ChatMessageEvent[] first = RunOnce();
        ChatMessageEvent[] second = RunOnce();
        Assert.Equal(first, second);
        Assert.Equal("101", first[0].SenderNetEntityId);
        Assert.Equal("102", first[1].SenderNetEntityId);
        Assert.Equal(1UL, first[0].RoomSequence);
        Assert.Equal(2UL, first[1].RoomSequence);
    }

    [Fact]
    public void ChatEventNeverAppearsInFullSnapshot()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));
        AssertOk(mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput("gg")));

        string snapshot = mapping.BuildFullSnapshotJson(7, 1);
        Assert.Contains("\"messageType\":\"FullSnapshot\"", snapshot, StringComparison.Ordinal);
        Assert.Contains("\"stateBlocks\":[]", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("chat.event", snapshot, StringComparison.Ordinal);

        ChatMappingResult replay = mapping.ApplyDownstream(
            "observer",
            "{\"messageType\":\"FullSnapshot\",\"tickId\":7,\"revision\":1,\"stateBlocks\":[" +
            Block("chat.event", GgEventPayload, GgEventSha256) + "]}");
        AssertRejected(replay, "state_block_kind_mismatch");
        Assert.Empty(mapping.DisplayedEvents("observer"));

        AssertOk(mapping.ApplyDownstream(
            "observer",
            "{\"messageType\":\"FullSnapshot\",\"tickId\":0,\"revision\":0,\"stateBlocks\":[]}"));
        Assert.Empty(mapping.DisplayedEvents("observer"));
    }

    [Fact]
    public void LiveSeq2AfterEmptySnapshotIsSessionBaselineNotBadEnvelope()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));
        AssertOk(mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput("one")));
        AssertOk(mapping.SubmitInput("room-01", "C1", 1, 8, new ChatInput("two")));

        string snapshot = mapping.BuildFullSnapshotJson(8, 1);
        AssertOk(mapping.ApplyDownstream("observer", snapshot));
        Assert.Empty(mapping.DisplayedEvents("observer"));

        mapping.BuildDeltaJson("room-01", 7, 1);
        string live = mapping.BuildDeltaJson("room-01", 8, 2);
        ChatMappingResult applied = mapping.ApplyDownstream("observer", live);
        AssertOk(applied);
        ChatMessageEvent shown = Assert.Single(mapping.DisplayedEvents("observer"));
        Assert.Equal(2UL, shown.RoomSequence);
        Assert.Equal(2UL, shown.MessageId);
        Assert.Equal("two", shown.Text);
        Assert.Equal("101", shown.SenderNetEntityId);

        ChatMappingResult duplicate = mapping.ApplyDownstream("observer", live);
        AssertRejected(duplicate, "bad_envelope");
        Assert.Single(mapping.DisplayedEvents("observer"));

        (string Payload, string Sha256) rollbackBytes = EncodeChatEvent(1, 1, 101, "one", 7);
        ChatMappingResult rollback = mapping.ApplyDownstream(
            "observer",
            Delta(rollbackBytes.Payload, rollbackBytes.Sha256, 7, 1));
        AssertRejected(rollback, "bad_envelope");
        ChatMessageEvent remaining = Assert.Single(mapping.DisplayedEvents("observer"));
        Assert.Equal(2UL, remaining.RoomSequence);
        Assert.Equal("two", remaining.Text);
    }

    [Fact]
    public void NonDecimalBoundNetEntityIdIsRejectedAndDoesNotForgeSenderZero()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "N1"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));

        ChatMappingResult submitted = mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput("gg"));
        AssertRejected(submitted, "bad_envelope", ChatMapping.InputMappingId);
        Assert.Empty(mapping.TakeDelivered("C1"));
        AssertUnchangedChatComponent(bindings, "N1");

        string delta = mapping.BuildDeltaJson("room-01", 7, 1);
        Assert.DoesNotContain("chat.event", delta, StringComparison.Ordinal);
        Assert.Contains("\"changedBlocks\":[]", delta, StringComparison.Ordinal);
        AssertOk(mapping.ApplyDownstream("observer", delta));
        Assert.Empty(mapping.DisplayedEvents("observer"));
        Assert.DoesNotContain(mapping.DisplayedEvents("observer"), static item => item.SenderNetEntityId == "0");
    }

    [Fact]
    public void ExtraClientSequenceOrSenderOnEnvelopeIsRejectedWithNoWrite()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));

        string extra = "{\"messageType\":\"InputCommand\",\"commandSequence\":9,\"commands\":[" +
                       Block("chat.input", GgPayload, GgSha256) + "]}";
        ChatMappingResult result = mapping.SubmitInputCommand("room-01", "C1", 1, 7, extra);
        AssertRejected(result, "bad_envelope");
        Assert.Empty(mapping.TakeDelivered("C1"));
        AssertUnchangedChatComponent(bindings, "101");
    }

    private static ChatMessageEvent[] RunOnce()
    {
        using EntityBindingQuery bindings = EntityBindingQuery.Create();
        AssertOk(Admit(bindings, net: "101"));
        AssertOk(Admit(bindings, connection: "C2", account: "acct-08", net: "102"));
        var mapping = new ChatTypedMapping(bindings);
        AssertOk(mapping.AttachMember("room-01", "C1"));
        AssertOk(mapping.AttachMember("room-01", "C2"));
        AssertOk(mapping.SubmitInput("room-01", "C1", 1, 7, new ChatInput("gg")));
        AssertOk(mapping.SubmitInput("room-01", "C2", 1, 8, new ChatInput("ok")));
        return mapping.TakeDelivered("C1");
    }

    private static BindingQueryResult Admit(
        EntityBindingQuery sut,
        string connection = "C1",
        string account = "acct-07",
        string room = "room-01",
        string net = "101",
        string type = "player",
        ulong generation = 1) =>
        sut.Bind(
            connection,
            new BindingRecordRequest
            {
                AccountId = account,
                RoomId = room,
                NetEntityId = net,
                EntityType = type,
                ConnectionGeneration = generation,
            });

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

    private static void AssertUnchangedChatComponent(EntityBindingQuery bindings, string netEntityId)
    {
        BindingQueryResult text = bindings.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = netEntityId,
                AttributeId = "ChatComponent.lastMessageText",
            });
        BindingQueryResult tick = bindings.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = "room-01",
                NetEntityId = netEntityId,
                AttributeId = "ChatComponent.lastMessageTick",
            });
        Assert.Equal("ok", text.Outcome);
        Assert.Equal(string.Empty, text.Value);
        Assert.Equal(0UL, tick.Value);
    }

    private static string InputCommand(string payload, string sha256) =>
        "{\"messageType\":\"InputCommand\",\"commands\":[" + Block("chat.input", payload, sha256) + "]}";

    private static string Delta(string payload, string sha256, ulong tickId, ulong revision) =>
        "{\"messageType\":\"Delta\",\"tickId\":" + tickId.ToString(CultureInfo.InvariantCulture) +
        ",\"revision\":" + revision.ToString(CultureInfo.InvariantCulture) +
        ",\"changedBlocks\":[" + Block("chat.event", payload, sha256) + "]}";

    private static string Block(string mappingId, string payload, string sha256) =>
        "{\"mappingId\":\"" + mappingId + "\",\"payload\":\"" + payload + "\",\"payloadSha256\":\"" + sha256 + "\"}";

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
