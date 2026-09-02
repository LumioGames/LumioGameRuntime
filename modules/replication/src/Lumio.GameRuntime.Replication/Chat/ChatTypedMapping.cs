using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Replication.Validation;

namespace Lumio.GameRuntime.Replication.Chat;

/// <summary>
/// Client replica of C-1 FullSnapshot / Delta. Server chat.input no longer lives here:
/// it goes through <see cref="ChatCommandRuntime"/> IngressCapture → CommandBuffer → Tick.
/// </summary>
public sealed class ChatTypedMapping
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ChatMessageEvent>> _displayed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _observedSequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _observedMessageId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _awaitingLiveBaseline = new(StringComparer.Ordinal);

    /// <summary>Applies a C-1 FullSnapshot or Delta to a replica connection.</summary>
    public ChatMappingResult ApplyDownstream(string connectionId, string messageJson)
    {
        lock (_gate)
        {
            if (!ChatEnvelope.TryParseRoot(messageJson, out StructuredJsonValue root, out ChatMappingResult failure))
                return failure;
            if (!ChatEnvelope.TryRequiredString(root, "messageType", out string messageType))
                return ChatMappingResult.Reject("bad_envelope", "messageType is required");

            if (string.Equals(messageType, "FullSnapshot", StringComparison.Ordinal))
                return ApplySnapshot(connectionId, root);
            if (string.Equals(messageType, "Delta", StringComparison.Ordinal))
                return ApplyDelta(connectionId, root);
            return ChatMappingResult.Reject("bad_envelope", "unknown messageType");
        }
    }

    /// <summary>Events accepted into the replica chat window after FullSnapshot baseline + live Delta.</summary>
    public ChatMessageEvent[] DisplayedEvents(string connectionId)
    {
        lock (_gate)
        {
            if (!_displayed.TryGetValue(connectionId ?? string.Empty, out List<ChatMessageEvent>? events))
                return Array.Empty<ChatMessageEvent>();
            return events.ToArray();
        }
    }

    private ChatMappingResult ApplySnapshot(string connectionId, StructuredJsonValue root)
    {
        if (!ChatEnvelope.TryReadSnapshot(root, out ChatMappingResult failure))
            return failure;

        _displayed[connectionId] = new List<ChatMessageEvent>();
        _observedSequence.Remove(connectionId);
        _observedMessageId.Remove(connectionId);
        _awaitingLiveBaseline.Add(connectionId);
        return ChatMappingResult.Ok();
    }

    private ChatMappingResult ApplyDelta(string connectionId, StructuredJsonValue root)
    {
        if (!ChatEnvelope.TryReadDelta(root, out ChatMessageEvent[] events, out ChatMappingResult failure))
            return failure;
        if (events.Length == 0) return ChatMappingResult.Ok();

        ChatMessageEvent mapped = events[0];
        if (!_awaitingLiveBaseline.Remove(connectionId))
        {
            _observedSequence.TryGetValue(connectionId, out ulong lastSequence);
            _observedMessageId.TryGetValue(connectionId, out ulong lastMessage);
            bool sequenceOk = mapped.RoomSequence == lastSequence + 1UL;
            if (!sequenceOk || mapped.MessageId <= lastMessage)
                return ChatMappingResult.Reject("bad_envelope", "chat.event roomSequence must strictly increase");
        }

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
}
