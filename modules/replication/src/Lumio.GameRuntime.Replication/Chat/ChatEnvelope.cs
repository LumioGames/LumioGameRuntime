using System;
using System.Text;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Replication.Chat;

/// <summary>Thin chat-facing adapter over the Runtime-owned C-1 codec.</summary>
public static class ChatEnvelope
{
    public static ChatMappingResult Validate(string messageJson)
    {
        try
        {
            _ = WireCodec.DecodePack(Encoding.UTF8.GetBytes(messageJson ?? string.Empty));
            return ChatMappingResult.Ok();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return ChatMappingResult.Reject("bad_envelope", ex.Message);
        }
    }

    public static bool TryParseInputCommand(string envelopeJson, out string text, out ChatMappingResult failure)
    {
        text = string.Empty;
        try
        {
            InputCommandMessage input = WireCodec.DecodeInput(Encoding.UTF8.GetBytes(envelopeJson ?? string.Empty));
            if (input.MappingId != WireCodec.ChatInput || !WireCodec.TryReadUtf8Payload(input.Payload.Span, out text))
            {
                failure = ChatMappingResult.Reject("undecodable_payload", "chat input payload is not canonical", WireCodec.ChatInput);
                return false;
            }
            if (Encoding.UTF8.GetByteCount(text) > ChatMapping.MaxTextUtf8Bytes)
            {
                failure = ChatMappingResult.Reject("chat_text_too_long", "chat text exceeds the bounded input size", WireCodec.ChatInput);
                return false;
            }
            failure = ChatMappingResult.Ok();
            return true;
        }
        catch (FormatException ex)
        {
            failure = ChatMappingResult.Reject("bad_envelope", ex.Message);
            return false;
        }
    }
}
