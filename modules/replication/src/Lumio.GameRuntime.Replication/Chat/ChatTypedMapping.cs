using System;
using System.Collections.Generic;
using System.Text;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Replication.Chat;

/// <summary>Applies decoded Runtime WorldChange records to a UI event stream.</summary>
public sealed class ChatTypedMapping
{
    public ChatMappingResult ApplyDownstream(string connectionId, string messageJson)
    {
        _ = connectionId;
        try
        {
            if (WireCodec.DecodePack(Encoding.UTF8.GetBytes(messageJson ?? string.Empty)) is not WorldChangeMessage change)
                return ChatMappingResult.Reject("bad_envelope", "expected a WorldChange packet");
            if (change.Rpcs.Count == 0) return ChatMappingResult.Ok();
            var events = new List<ChatMessageEvent>(change.Rpcs.Count);
            for (int i = 0; i < change.Rpcs.Count; i++)
            {
                ClientRpcRecord rpc = change.Rpcs[i];
                string text = rpc.Args.Count == 0 ? string.Empty : rpc.Args[0]?.ToString() ?? string.Empty;
                events.Add(new ChatMessageEvent(rpc.MessageId, rpc.RoomSequence, rpc.Sender.ToHex(), text, rpc.AppliedTick));
            }
            return ChatMappingResult.Ok(events);
        }
        catch (FormatException ex)
        {
            return ChatMappingResult.Reject("bad_envelope", ex.Message);
        }
    }
}
