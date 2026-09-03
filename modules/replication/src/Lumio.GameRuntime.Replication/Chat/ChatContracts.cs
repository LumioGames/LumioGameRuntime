using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Replication.Chat;

public static class ChatMapping
{
    public const string ContractId = "lumio.gameplay-envelope.v1";
    public const string InputMappingId = "chat.input";
    public const string EventMappingId = "chat.event";
    public const string ComponentMappingId = "chat.component";
    public const string IdentityMappingId = "entity.identity";
    public const int MaxTextUtf8Bytes = 512;
    public const int MaxChatInputPerSenderPerTick = 1;
    public const int MaxCommandsPerEnvelope = 16;
    public const int MaxBlocksPerEnvelope = 4096;
    public const int IngressQueueCapacity = 64;
    public const string BoundedInputPolicy = "reject";

    public static readonly string[] InputFieldOrder = { "text" };

    public static readonly string[] EventFieldOrder =
    {
        "messageId", "roomSequence", "senderNetEntityIdInstanceId", "senderNetEntityIdCounter", "text", "appliedTick"
    };

    public static readonly string[] ComponentFieldOrder = { "lastMessageText", "lastMessageTick" };

    public static readonly string[] IdentityFieldOrder = { "netEntityId", "entityType", "unmappedMark" };
}

internal readonly record struct EntityIdentityRecord(
    ulong NetEntityId,
    string EntityType,
    string UnmappedMark);

public readonly record struct ChatInput(string Text);

public readonly record struct ChatMessageEvent(
    ulong MessageId,
    ulong RoomSequence,
    string SenderNetEntityId,
    string Text,
    ulong AppliedTick);

public readonly record struct ChatMappingResult(
    bool Succeeded,
    string? Code = null,
    string? Detail = null,
    string? MappingId = null,
    ChatMessageEvent? Event = null)
{
    public static ChatMappingResult Ok(ChatMessageEvent? mapped = null) =>
        new(true, Event: mapped);

    public static ChatMappingResult Reject(string code, string detail, string? mappingId = null) =>
        new(false, code, detail, mappingId);
}

/// <summary>One IngressCapture → CommandBuffer → EcsCommandBufferCommit tick.</summary>
public readonly record struct ChatTickResult(
    ulong AppliedTick,
    ulong Revision,
    IReadOnlyList<ChatMappingResult> Results,
    IReadOnlyList<ChatMessageEvent> Events);
