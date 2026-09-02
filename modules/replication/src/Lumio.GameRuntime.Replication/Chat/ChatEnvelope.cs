using System;
using System.Collections.Generic;
using System.Globalization;
using Lumio.GameRuntime.Replication.Validation;

namespace Lumio.GameRuntime.Replication.Chat;

/// <summary>C-1 envelope codec: validate InputCommand / FullSnapshot / Delta / Error and emit host-ready JSON.</summary>
public static class ChatEnvelope
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

    private static readonly HashSet<string> ErrorMembers = new(StringComparer.Ordinal)
    {
        "messageType", "code", "detail", "mappingId"
    };

    private static readonly HashSet<string> ErrorCodes = new(StringComparer.Ordinal)
    {
        "bad_envelope",
        "unsupported_contract",
        "unknown_command_type",
        "bad_payload_hash",
        "undecodable_payload",
        "block_order_violation",
        "state_block_kind_mismatch",
        "chat_text_too_long",
        "chat_rate_exceeded",
        "queue_full",
        "session_closed",
        "runtime_failure"
    };

    /// <summary>Validates a C-1 JSON message without applying it.</summary>
    public static ChatMappingResult Validate(string messageJson)
    {
        if (!StructuredJsonParser.TryParse(messageJson, out StructuredJsonValue? root) ||
            root is null ||
            root.Kind != StructuredJsonKind.Object)
            return ChatMappingResult.Reject("bad_envelope", "message is not a JSON object");
        if (!TryRequiredString(root, "messageType", out string messageType))
            return ChatMappingResult.Reject("bad_envelope", "messageType is required");

        if (string.Equals(messageType, "InputCommand", StringComparison.Ordinal))
        {
            if (!TryParseInputCommand(root, out _, out ChatMappingResult parsed))
                return parsed;
            return ChatMappingResult.Ok();
        }

        if (string.Equals(messageType, "FullSnapshot", StringComparison.Ordinal))
        {
            if (!TryReadSnapshot(root, out ChatMappingResult snapshot))
                return snapshot;
            return ChatMappingResult.Ok();
        }

        if (string.Equals(messageType, "Delta", StringComparison.Ordinal))
        {
            if (!TryReadDelta(root, out _, out ChatMappingResult delta))
                return delta;
            return ChatMappingResult.Ok();
        }

        if (string.Equals(messageType, "Error", StringComparison.Ordinal))
            return ValidateError(root);

        return ChatMappingResult.Reject("bad_envelope", "unknown messageType");
    }

    /// <summary>Decodes a C-1 InputCommand into chat.input text. Empty commands succeed with empty text.</summary>
    public static bool TryParseInputCommand(string envelopeJson, out string text, out ChatMappingResult failure)
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

        return TryParseInputCommand(root, out text, out failure);
    }

    /// <summary>Replicated EntityIdentity.entityType mapping used as kind=state on FullSnapshot.</summary>
    public const string EntityTypeStateMappingId = "mapping-entity-identity-entity-type";

    /// <summary>Replicated EntityIdentity.claimedMark mapping used as kind=state on FullSnapshot.</summary>
    public const string ClaimedMarkStateMappingId = "mapping-entity-identity-claimed-mark";

    /// <summary>One C-1 StateBlock (mappingId + LumioBinV1 payload).</summary>
    public readonly record struct ChatStateBlock(string MappingId, byte[] Payload);

    /// <summary>Builds a C-1 FullSnapshot. Empty stateBlocks remain the encoding of no live replicable state.</summary>
    public static string FullSnapshot(ulong tickId, ulong revision) =>
        FullSnapshot(tickId, revision, Array.Empty<ChatStateBlock>());

    /// <summary>Builds a C-1 FullSnapshot with live-entity stateBlocks sorted by mappingId.</summary>
    public static string FullSnapshot(ulong tickId, ulong revision, IReadOnlyList<ChatStateBlock> blocks)
    {
        string encoded = "[]";
        if (blocks is not null && blocks.Count > 0)
        {
            var ordered = new List<ChatStateBlock>(blocks);
            ordered.Sort(static (left, right) => string.CompareOrdinal(left.MappingId, right.MappingId));
            var builder = new System.Text.StringBuilder();
            builder.Append('[');
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0) builder.Append(',');
                byte[] payload = ordered[i].Payload ?? Array.Empty<byte>();
                string hex = ChatPayload.ToHex(payload);
                string digest = ReplicationValidation.Sha256Hex(payload);
                builder.Append(BlockJson(ordered[i].MappingId, hex, digest));
            }

            builder.Append(']');
            encoded = builder.ToString();
        }

        return "{\"messageType\":\"FullSnapshot\",\"tickId\":" + Number(tickId) +
               ",\"revision\":" + Number(revision) + ",\"stateBlocks\":" + encoded + "}";
    }

    /// <summary>Builds one C-1 Delta. Duplicate mappingId is illegal, so each chat.event is its own envelope.</summary>
    public static string Delta(ulong tickId, ulong revision, ChatMessageEvent? mapped)
    {
        string blocks = "[]";
        if (mapped.HasValue)
        {
            byte[] payload = ChatPayload.EncodeEvent(mapped.Value);
            string hex = ChatPayload.ToHex(payload);
            string digest = ReplicationValidation.Sha256Hex(payload);
            blocks = "[" + BlockJson(ChatMapping.EventMappingId, hex, digest) + "]";
        }

        return "{\"messageType\":\"Delta\",\"tickId\":" + Number(tickId) +
               ",\"revision\":" + Number(revision) + ",\"changedBlocks\":" + blocks + "}";
    }

    /// <summary>One C-1 Delta frame per committed chat.event, same tick, shared revision.</summary>
    public static IReadOnlyList<string> DeltaFrames(ulong tickId, ulong revision, IReadOnlyList<ChatMessageEvent> events)
    {
        if (events is null || events.Count == 0)
            return new[] { Delta(tickId, revision, null) };

        var frames = new string[events.Count];
        for (int i = 0; i < events.Count; i++)
            frames[i] = Delta(tickId, revision, events[i]);
        return frames;
    }

    internal static bool TryReadSnapshot(StructuredJsonValue root, out ChatMappingResult failure)
    {
        failure = default;
        if (!Shape(root, SnapshotMembers, out failure)) return false;
        if (!TryRequiredUInt64(root, "tickId", out _) || !TryRequiredUInt64(root, "revision", out _))
        {
            failure = ChatMappingResult.Reject("bad_envelope", "FullSnapshot tickId/revision are required");
            return false;
        }

        if (!TryReadBlocks(root, "stateBlocks", requireStateKind: true, out ChatMessageEvent[] events, out failure))
            return false;
        if (events.Length != 0)
        {
            failure = ChatMappingResult.Reject("state_block_kind_mismatch", "chat.event must not appear in FullSnapshot.stateBlocks");
            return false;
        }

        return true;
    }

    internal static bool TryReadDelta(StructuredJsonValue root, out ChatMessageEvent[] events, out ChatMappingResult failure)
    {
        events = Array.Empty<ChatMessageEvent>();
        failure = default;
        if (!Shape(root, DeltaMembers, out failure)) return false;
        if (!TryRequiredUInt64(root, "tickId", out _) || !TryRequiredUInt64(root, "revision", out _))
        {
            failure = ChatMappingResult.Reject("bad_envelope", "Delta tickId/revision are required");
            return false;
        }

        return TryReadBlocks(root, "changedBlocks", requireStateKind: false, out events, out failure);
    }

    internal static bool TryParseRoot(string messageJson, out StructuredJsonValue root, out ChatMappingResult failure)
    {
        root = null!;
        failure = default;
        if (!StructuredJsonParser.TryParse(messageJson, out StructuredJsonValue? parsed) ||
            parsed is null ||
            parsed.Kind != StructuredJsonKind.Object)
        {
            failure = ChatMappingResult.Reject("bad_envelope", "downstream message is not a JSON object");
            return false;
        }

        root = parsed;
        return true;
    }

    internal static bool TryRequiredString(StructuredJsonValue document, string name, out string value)
    {
        value = string.Empty;
        return document.TryGetProperty(name, out StructuredJsonValue? property) &&
            property is not null &&
            property.Kind == StructuredJsonKind.String &&
            property.Text is not null &&
            (value = property.Text).Length >= 0;
    }

    private static bool TryParseInputCommand(StructuredJsonValue root, out string text, out ChatMappingResult failure)
    {
        text = string.Empty;
        failure = default;
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

    private static ChatMappingResult ValidateError(StructuredJsonValue root)
    {
        if (!Shape(root, ErrorMembers, out ChatMappingResult shape)) return shape;
        if (!TryRequiredString(root, "messageType", out string messageType) ||
            !string.Equals(messageType, "Error", StringComparison.Ordinal))
            return ChatMappingResult.Reject("bad_envelope", "messageType must be Error");
        if (!TryRequiredString(root, "code", out string code) || !ErrorCodes.Contains(code))
            return ChatMappingResult.Reject("bad_envelope", "Error.code is not a C-1 error code");
        if (!TryRequiredString(root, "detail", out _))
            return ChatMappingResult.Reject("bad_envelope", "Error.detail is required");
        if (root.TryGetProperty("mappingId", out StructuredJsonValue? mapping) &&
            mapping is not null &&
            (mapping.Kind != StructuredJsonKind.String || mapping.Text is null || !TryKind(mapping.Text, out _)))
            return ChatMappingResult.Reject("bad_envelope", "Error.mappingId is not a registered mapping");
        return ChatMappingResult.Ok();
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

    internal static bool TryKind(string mappingId, out string kind)
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

        if (string.Equals(mappingId, EntityTypeStateMappingId, StringComparison.Ordinal) ||
            string.Equals(mappingId, ClaimedMarkStateMappingId, StringComparison.Ordinal))
        {
            kind = "state";
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

    private static bool TryRequiredUInt64(StructuredJsonValue document, string name, out ulong value)
    {
        value = 0;
        return document.TryGetProperty(name, out StructuredJsonValue? property) &&
            property is not null &&
            property.TryGetUInt64(out value);
    }

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string BlockJson(string mappingId, string payload, string sha256) =>
        "{\"mappingId\":\"" + mappingId + "\",\"payload\":\"" + payload + "\",\"payloadSha256\":\"" + sha256 + "\"}";
}
