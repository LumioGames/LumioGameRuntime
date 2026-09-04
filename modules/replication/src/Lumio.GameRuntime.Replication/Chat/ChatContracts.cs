using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Replication.Chat;

public static class ChatMapping
{
    public const string ContractId = "lumio.gameplay-envelope.v1";
    public const string InputMappingId = "chat.input";
    public const int MaxTextUtf8Bytes = 512;
    public const int MaxChatInputPerSenderPerTick = 1;
    public const int MaxCommandsPerEnvelope = 16;
    public const int IngressQueueCapacity = 64;
    public const string BoundedInputPolicy = "reject";
    public static readonly string[] InputFieldOrder = { "text" };
}

public readonly record struct ChatInput(string Text);

/// <summary>UI-facing projection of a ClientRpc chat invocation.</summary>
public readonly record struct ChatMessageEvent(ulong MessageId, ulong RoomSequence, string SenderNetEntityId, string Text, ulong AppliedTick);

public readonly record struct ChatMappingResult(bool Succeeded, string? Code = null, string? Detail = null, string? MappingId = null, ChatMessageEvent? Event = null)
{
    public static ChatMappingResult Ok(ChatMessageEvent? mapped = null) => new(true, Event: mapped);
    public static ChatMappingResult Reject(string code, string detail, string? mappingId = null) => new(false, code, detail, mappingId);
}

public readonly record struct ChatTickResult(ulong AppliedTick, ulong Revision, IReadOnlyList<ChatMappingResult> Results, IReadOnlyList<ChatMessageEvent> Events);
