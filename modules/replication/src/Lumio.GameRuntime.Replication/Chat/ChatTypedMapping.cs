using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Replication.Validation;

namespace Lumio.GameRuntime.Replication.Chat;

public sealed class ChatTypedMapping
{
    private static readonly HashSet<string> InputEnvelopeMembers = new(StringComparer.Ordinal)
    {
        "messageType", "commands"
    };

    private static readonly HashSet<string> CommandBlockMembers = new(StringComparer.Ordinal)
    {
        "mappingId", "payload", "payloadSha256"
    };

    private static readonly HashSet<string> SnapshotMembers = new(StringComparer.Ordinal)
    {
        "messageType", "tickId", "revision", "stateBlocks"
    };

    private static readonly HashSet<string> DeltaMembers = new(StringComparer.Ordinal)
    {
        "messageType", "tickId", "revision", "changedBlocks"
    };

    private readonly object _gate = new();
    private readonly EntityBindingQuery _bindings;
    private readonly Dictionary<string, HashSet<string>> _membersByRoom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ChatMessageEvent>> _inbox = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ChatMessageEvent>> _displayed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _committedTick = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _nextMessageId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _nextRoomSequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<ChatMessageEvent>> _pendingDelta = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _observedSequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _observedMessageId = new(StringComparer.Ordinal);

    public ChatTypedMapping(EntityBindingQuery bindings)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    public ChatMappingResult AttachMember(string roomId, string connectionId)
    {
        lock (_gate)
        {
            BindingQueryResult resolved = _bindings.ResolveByConnection(roomId, connectionId);
            if (resolved.Outcome != "ok") return FromBinding(resolved);
            AddMember(roomId, connectionId);
            return ChatMappingResult.Ok();
        }
    }

    public ChatMappingResult SubmitInput(
        string roomId,
        string connectionId,
        ulong connectionGeneration,
        ulong authoritativeTick,
        ChatInput input)
    {
        if (input.Text is null) throw new ArgumentException("ChatInput.Text is required.", nameof(input));
        lock (_gate) return SubmitText(roomId, connectionId, connectionGeneration, authoritativeTick, input.Text);
    }

    public ChatMappingResult SubmitInputCommand(
        string roomId,
        string connectionId,
        ulong connectionGeneration,
        ulong authoritativeTick,
        string envelopeJson)
    {
        lock (_gate)
        {
            if (!TryParseInputCommand(envelopeJson, out string text, out ChatMappingResult failure))
                return failure;
            return SubmitText(roomId, connectionId, connectionGeneration, authoritativeTick, text);
        }
    }

    public ChatMessageEvent[] TakeDelivered(string connectionId)
    {
        lock (_gate)
        {
            if (!_inbox.TryGetValue(connectionId ?? string.Empty, out List<ChatMessageEvent>? queued) || queued.Count == 0)
                return Array.Empty<ChatMessageEvent>();
            var copy = queued.ToArray();
            queued.Clear();
            return copy;
        }
    }

    public ChatMessageEvent[] DisplayedEvents(string connectionId)
    {
        lock (_gate)
        {
            if (!_displayed.TryGetValue(connectionId ?? string.Empty, out List<ChatMessageEvent>? events))
                return Array.Empty<ChatMessageEvent>();
            return events.ToArray();
        }
    }

    public string BuildFullSnapshotJson(ulong tickId, ulong revision) =>
        "{\"messageType\":\"FullSnapshot\",\"tickId\":" + Number(tickId) +
        ",\"revision\":" + Number(revision) + ",\"stateBlocks\":[]}";

    public string BuildDeltaJson(string roomId, ulong tickId, ulong revision)
    {
        lock (_gate)
        {
            string blocks = "[]";
            if (_pendingDelta.TryGetValue(roomId ?? string.Empty, out Queue<ChatMessageEvent>? pending) &&
                pending.Count > 0)
            {
                ChatMessageEvent mapped = pending.Dequeue();
                byte[] payload = ChatPayload.EncodeEvent(mapped);
                string hex = ChatPayload.ToHex(payload);
                string digest = ReplicationValidation.Sha256Hex(payload);
                blocks = "[" + BlockJson(ChatMapping.EventMappingId, hex, digest) + "]";
            }

            return "{\"messageType\":\"Delta\",\"tickId\":" + Number(tickId) +
                   ",\"revision\":" + Number(revision) + ",\"changedBlocks\":" + blocks + "}";
        }
    }

    public ChatMappingResult ApplyDownstream(string connectionId, string messageJson)
    {
        lock (_gate)
        {
            if (!StructuredJsonParser.TryParse(messageJson, out StructuredJsonValue? root) ||
                root is null ||
                root.Kind != StructuredJsonKind.Object)
                return ChatMappingResult.Reject("bad_envelope", "downstream message is not a JSON object");
            if (!TryRequiredString(root, "messageType", out string messageType))
                return ChatMappingResult.Reject("bad_envelope", "messageType is required");

            if (string.Equals(messageType, "FullSnapshot", StringComparison.Ordinal))
                return ApplySnapshot(connectionId, root);
            if (string.Equals(messageType, "Delta", StringComparison.Ordinal))
                return ApplyDelta(connectionId, root);
            return ChatMappingResult.Reject("bad_envelope", "unknown messageType");
        }
    }

    private ChatMappingResult SubmitText(
        string roomId,
        string connectionId,
        ulong connectionGeneration,
        ulong authoritativeTick,
        string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > ChatMapping.MaxTextUtf8Bytes)
            return ChatMappingResult.Reject("chat_text_too_long", "chat text exceeds 512 UTF-8 bytes", ChatMapping.InputMappingId);

        BindingQueryResult resolved = _bindings.ResolveByConnection(roomId, connectionId);
        if (resolved.Outcome != "ok" || !resolved.Binding.HasValue)
            return FromBinding(resolved);

        ConnectionBinding binding = resolved.Binding.Value;
        BindingQueryResult occupancy = _bindings.ResolveByNetEntityId(
            roomId,
            binding.NetEntityId,
            connectionGeneration,
            "server-authoritative");
        if (occupancy.Outcome != "ok")
            return FromBinding(occupancy);

        string senderKey = roomId + "\n" + binding.NetEntityId;
        if (_committedTick.TryGetValue(senderKey, out ulong usedTick) && usedTick == authoritativeTick)
            return ChatMappingResult.Reject("chat_rate_exceeded", "second chat.input within the authoritative tick", ChatMapping.InputMappingId);

        ulong messageId = Next(roomId, _nextMessageId);
        ulong roomSequence = Next(roomId, _nextRoomSequence);
        var mapped = new ChatMessageEvent(messageId, roomSequence, binding.NetEntityId, text, authoritativeTick);
        _committedTick[senderKey] = authoritativeTick;
        AddMember(roomId, connectionId);
        EnqueueDelta(roomId, mapped);
        Fanout(roomId, mapped);
        return ChatMappingResult.Ok(mapped);
    }

    private void AddMember(string roomId, string connectionId)
    {
        if (!_membersByRoom.TryGetValue(roomId, out HashSet<string>? members))
        {
            members = new HashSet<string>(StringComparer.Ordinal);
            _membersByRoom[roomId] = members;
        }

        members.Add(connectionId);
    }

    private void Fanout(string roomId, ChatMessageEvent mapped)
    {
        if (!_membersByRoom.TryGetValue(roomId, out HashSet<string>? members)) return;
        foreach (string connectionId in members)
        {
            BindingQueryResult live = _bindings.ResolveByConnection(roomId, connectionId);
            if (live.Outcome != "ok") continue;
            if (!_inbox.TryGetValue(connectionId, out List<ChatMessageEvent>? queued))
            {
                queued = new List<ChatMessageEvent>();
                _inbox[connectionId] = queued;
            }

            queued.Add(mapped);
        }
    }

    private void EnqueueDelta(string roomId, ChatMessageEvent mapped)
    {
        if (!_pendingDelta.TryGetValue(roomId, out Queue<ChatMessageEvent>? pending))
        {
            pending = new Queue<ChatMessageEvent>();
            _pendingDelta[roomId] = pending;
        }

        pending.Enqueue(mapped);
    }

    private ChatMappingResult ApplySnapshot(string connectionId, StructuredJsonValue root)
    {
        if (!Shape(root, SnapshotMembers, out ChatMappingResult shape)) return shape;
        if (!TryRequiredUInt64(root, "tickId", out _) || !TryRequiredUInt64(root, "revision", out _))
            return ChatMappingResult.Reject("bad_envelope", "FullSnapshot tickId/revision are required");
        if (!TryReadBlocks(root, "stateBlocks", requireStateKind: true, out ChatMessageEvent[] events, out ChatMappingResult blocks))
            return blocks;
        if (events.Length != 0)
            return ChatMappingResult.Reject("state_block_kind_mismatch", "chat.event must not appear in FullSnapshot.stateBlocks");

        _displayed[connectionId] = new List<ChatMessageEvent>();
        _observedSequence[connectionId] = 0;
        _observedMessageId[connectionId] = 0;
        return ChatMappingResult.Ok();
    }

    private ChatMappingResult ApplyDelta(string connectionId, StructuredJsonValue root)
    {
        if (!Shape(root, DeltaMembers, out ChatMappingResult shape)) return shape;
        if (!TryRequiredUInt64(root, "tickId", out _) || !TryRequiredUInt64(root, "revision", out _))
            return ChatMappingResult.Reject("bad_envelope", "Delta tickId/revision are required");
        if (!TryReadBlocks(root, "changedBlocks", requireStateKind: false, out ChatMessageEvent[] events, out ChatMappingResult blocks))
            return blocks;
        if (events.Length == 0) return ChatMappingResult.Ok();

        ChatMessageEvent mapped = events[0];
        _observedSequence.TryGetValue(connectionId, out ulong lastSequence);
        _observedMessageId.TryGetValue(connectionId, out ulong lastMessage);
        bool sequenceOk = mapped.RoomSequence == lastSequence + 1UL;
        if (!sequenceOk || mapped.MessageId <= lastMessage)
            return ChatMappingResult.Reject("bad_envelope", "chat.event roomSequence must strictly increase");

        if (!_displayed.TryGetValue(connectionId, out List<ChatMessageEvent>? window))
        {
            window = new List<ChatMessageEvent>();
            _displayed[connectionId] = window;
        }

        window.Add(mapped);
        _observedSequence[connectionId] = mapped.RoomSequence;
        _observedMessageId[connectionId] = mapped.MessageId;
        return ChatMappingResult.Ok(mapped);
    }

    private bool TryParseInputCommand(string envelopeJson, out string text, out ChatMappingResult failure)
    {
        text = string.Empty;
        failure = default;
        if (!StructuredJsonParser.TryParse(envelopeJson, out StructuredJsonValue? root) ||
            root is null ||
            root.Kind != StructuredJsonKind.Object)
        {
            failure = ChatMappingResult.Reject("bad_envelope", "InputCommand is not a JSON object");
            return false;
        }

        if (!Shape(root, InputEnvelopeMembers, out failure)) return false;
        if (!TryRequiredString(root, "messageType", out string messageType) ||
            !string.Equals(messageType, "InputCommand", StringComparison.Ordinal))
        {
            failure = ChatMappingResult.Reject("bad_envelope", "messageType must be InputCommand");
            return false;
        }

        if (!root.TryGetProperty("commands", out StructuredJsonValue? commands) ||
            commands is null ||
            commands.Kind != StructuredJsonKind.Array ||
            commands.Items is null)
        {
            failure = ChatMappingResult.Reject("bad_envelope", "commands array is required");
            return false;
        }

        if (commands.Items.Count > ChatMapping.MaxCommandsPerEnvelope)
        {
            failure = ChatMappingResult.Reject("bad_envelope", "commands exceed maxCommandsPerEnvelope");
            return false;
        }

        if (commands.Items.Count == 0)
        {
            failure = ChatMappingResult.Ok();
            return false;
        }

        string previous = string.Empty;
        string? decoded = null;
        for (var index = 0; index < commands.Items.Count; index++)
        {
            StructuredJsonValue block = commands.Items[index];
            if (!TryReadCommandBlock(block, out string mappingId, out string payloadHex, out string payloadSha, out failure))
                return false;
            if (index > 0 && string.CompareOrdinal(previous, mappingId) >= 0)
            {
                failure = ChatMappingResult.Reject("block_order_violation", "command mappingId must be strictly ascending and unique");
                return false;
            }

            previous = mappingId;
            if (!TryKind(mappingId, out string kind) || kind != "command")
            {
                failure = ChatMappingResult.Reject("unknown_command_type", "CommandBlock.mappingId is not a command mapping");
                return false;
            }

            if (!ChatPayload.TryDecodeHex(payloadHex, out byte[] payload))
            {
                failure = ChatMappingResult.Reject("undecodable_payload", "payload is not lowercase hex", mappingId);
                return false;
            }

            string digest = ReplicationValidation.Sha256Hex(payload);
            if (!ReplicationValidation.IsHash256(payloadSha) || !ReplicationValidation.ConstantTimeEquals(payloadSha, digest))
            {
                failure = ChatMappingResult.Reject("bad_payload_hash", "payloadSha256 does not match payload bytes", mappingId);
                return false;
            }

            if (!ChatPayload.TryDecodeInput(payload, out string commandText, out string decodeCode))
            {
                failure = ChatMappingResult.Reject(decodeCode, "chat.input payload rejected", mappingId);
                return false;
            }

            decoded = commandText;
        }

        text = decoded ?? string.Empty;
        return true;
    }

    private static bool TryReadCommandBlock(
        StructuredJsonValue block,
        out string mappingId,
        out string payloadHex,
        out string payloadSha,
        out ChatMappingResult failure)
    {
        mappingId = string.Empty;
        payloadHex = string.Empty;
        payloadSha = string.Empty;
        failure = default;
        if (block.Kind != StructuredJsonKind.Object || !Shape(block, CommandBlockMembers, out failure))
        {
            if (failure.Code is null)
                failure = ChatMappingResult.Reject("bad_envelope", "CommandBlock must be an object");
            return false;
        }

        if (!TryRequiredString(block, "mappingId", out mappingId) ||
            !TryRequiredString(block, "payload", out payloadHex) ||
            !TryRequiredString(block, "payloadSha256", out payloadSha))
        {
            failure = ChatMappingResult.Reject("bad_envelope", "CommandBlock fields are required");
            return false;
        }

        return true;
    }

    private static bool TryReadBlocks(
        StructuredJsonValue root,
        string name,
        bool requireStateKind,
        out ChatMessageEvent[] events,
        out ChatMappingResult failure)
    {
        events = Array.Empty<ChatMessageEvent>();
        failure = default;
        if (!root.TryGetProperty(name, out StructuredJsonValue? array) ||
            array is null ||
            array.Kind != StructuredJsonKind.Array ||
            array.Items is null)
        {
            failure = ChatMappingResult.Reject("bad_envelope", name + " array is required");
            return false;
        }

        if (array.Items.Count > ChatMapping.MaxBlocksPerEnvelope)
        {
            failure = ChatMappingResult.Reject("bad_envelope", name + " exceeds maxBlocksPerEnvelope");
            return false;
        }

        var decoded = new List<ChatMessageEvent>();
        string previous = string.Empty;
        for (var index = 0; index < array.Items.Count; index++)
        {
            StructuredJsonValue block = array.Items[index];
            if (!TryReadCommandBlock(block, out string mappingId, out string payloadHex, out string payloadSha, out failure))
                return false;
            if (index > 0 && string.CompareOrdinal(previous, mappingId) >= 0)
            {
                failure = ChatMappingResult.Reject("block_order_violation", "block mappingId must be strictly ascending and unique");
                return false;
            }

            previous = mappingId;
            if (!TryKind(mappingId, out string kind))
            {
                failure = ChatMappingResult.Reject("state_block_kind_mismatch", "mappingId is not registered");
                return false;
            }

            if (requireStateKind)
            {
                if (kind != "state")
                {
                    failure = ChatMappingResult.Reject("state_block_kind_mismatch", "FullSnapshot.stateBlocks only allow kind=state");
                    return false;
                }
            }
            else if (kind is not ("event" or "state"))
            {
                failure = ChatMappingResult.Reject("state_block_kind_mismatch", "Delta.changedBlocks only allow kind event or state");
                return false;
            }

            if (!ChatPayload.TryDecodeHex(payloadHex, out byte[] payload))
            {
                failure = ChatMappingResult.Reject("undecodable_payload", "payload is not lowercase hex", mappingId);
                return false;
            }

            string digest = ReplicationValidation.Sha256Hex(payload);
            if (!ReplicationValidation.IsHash256(payloadSha) || !ReplicationValidation.ConstantTimeEquals(payloadSha, digest))
            {
                failure = ChatMappingResult.Reject("bad_payload_hash", "payloadSha256 does not match payload bytes", mappingId);
                return false;
            }

            if (string.Equals(mappingId, ChatMapping.EventMappingId, StringComparison.Ordinal))
            {
                if (!ChatPayload.TryDecodeEvent(payload, out ChatMessageEvent mapped, out string decodeCode))
                {
                    failure = ChatMappingResult.Reject(decodeCode, "chat.event payload rejected", mappingId);
                    return false;
                }

                decoded.Add(mapped);
            }
        }

        events = decoded.ToArray();
        return true;
    }

    private static bool TryKind(string mappingId, out string kind)
    {
        kind = string.Empty;
        if (string.Equals(mappingId, ChatMapping.InputMappingId, StringComparison.Ordinal))
        {
            kind = "command";
            return true;
        }

        if (string.Equals(mappingId, ChatMapping.EventMappingId, StringComparison.Ordinal))
        {
            kind = "event";
            return true;
        }

        if (string.Equals(mappingId, ChatMapping.ComponentMappingId, StringComparison.Ordinal))
        {
            kind = "componentState";
            return true;
        }

        return false;
    }

    private static bool Shape(StructuredJsonValue document, HashSet<string> allowed, out ChatMappingResult failure)
    {
        failure = default;
        if (document.Properties is null)
        {
            failure = ChatMappingResult.Reject("bad_envelope", "object is required");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Properties.Count; index++)
        {
            string name = document.Properties[index].Name;
            if (!seen.Add(name) || !allowed.Contains(name))
            {
                failure = ChatMappingResult.Reject("bad_envelope", "unknown or duplicate member: " + name);
                return false;
            }
        }

        return true;
    }

    private static bool TryRequiredString(StructuredJsonValue document, string name, out string value)
    {
        value = string.Empty;
        return document.TryGetProperty(name, out StructuredJsonValue? property) &&
            property is not null &&
            property.Kind == StructuredJsonKind.String &&
            property.Text is not null &&
            (value = property.Text).Length >= 0;
    }

    private static bool TryRequiredUInt64(StructuredJsonValue document, string name, out ulong value)
    {
        value = 0;
        return document.TryGetProperty(name, out StructuredJsonValue? property) &&
            property is not null &&
            property.TryGetUInt64(out value);
    }

    private static ChatMappingResult FromBinding(BindingQueryResult result)
    {
        if (string.Equals(result.Outcome, "request_error", StringComparison.Ordinal))
            return ChatMappingResult.Reject(result.Code ?? "bad_envelope", result.Detail ?? result.Code ?? "binding request error");
        return ChatMappingResult.Reject(result.Outcome, result.Detail ?? result.Outcome);
    }

    private static ulong Next(string roomId, Dictionary<string, ulong> counters)
    {
        if (!counters.TryGetValue(roomId, out ulong current)) current = 0;
        ulong next = current + 1UL;
        counters[roomId] = next;
        return next;
    }

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string BlockJson(string mappingId, string payload, string sha256) =>
        "{\"mappingId\":\"" + mappingId + "\",\"payload\":\"" + payload + "\",\"payloadSha256\":\"" + sha256 + "\"}";
}
