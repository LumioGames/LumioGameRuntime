using System;
using System.Collections.Generic;
using System.Text;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Binding;

namespace Lumio.GameRuntime.Replication.Chat;

/// <summary>Host-facing chat path: InputCommand → WorldManager inbox → ApplyInputs → ClientRpc outbox.</summary>
public sealed class ChatCommandRuntime : IDisposable
{
    private readonly EntityBindingQuery _bindings;
    private readonly bool _ownsBindings;
    private bool _disposed;

    private ChatCommandRuntime(EntityBindingQuery bindings, bool ownsBindings)
    {
        _bindings = bindings;
        _ownsBindings = ownsBindings;
    }

    public static ChatCommandRuntime Create(EntityBindingQuery bindings, bool ownsBindings = false) =>
        new(bindings ?? throw new ArgumentNullException(nameof(bindings)), ownsBindings);

    public WorldManager Manager => _bindings.Manager;

    public ulong CurrentTick => Manager.World.Tick;

    public ulong Revision => Manager.World.Revision;

    public IReadOnlyList<string> LiveNetEntityIds
    {
        get
        {
            var ids = new List<string>();
            foreach (NetEntityId id in Manager.World.IssuedIds)
            {
                if (Manager.World.IsLive(id)) ids.Add(id.ToHex());
            }

            return ids;
        }
    }

    public ChatMappingResult AttachMember(string roomId, string connectionId)
    {
        BindingQueryResult resolved = _bindings.ResolveByConnection(roomId, connectionId);
        if (resolved.Outcome != "ok" || !resolved.Binding.HasValue)
            return ChatMappingResult.Reject("binding_not_found", resolved.Detail ?? "no binding");
        return ChatMappingResult.Ok();
    }

    public ChatMappingResult AdmitInput(string roomId, string connectionId, ulong connectionGeneration, ChatInput input)
    {
        _ = roomId;
        _ = connectionGeneration;
        if (input.Text is null) throw new ArgumentException("ChatInput.Text is required.", nameof(input));
        if (Encoding.UTF8.GetByteCount(input.Text) > ChatMapping.MaxTextUtf8Bytes)
            return ChatMappingResult.Reject("chat_text_too_long", "chat text exceeds 512 UTF-8 bytes", ChatMapping.InputMappingId);
        if (!_bindings.TryResolveConnection(connectionId, out NetEntityId sender))
            return ChatMappingResult.Reject("binding_not_found", "no active binding");
        Manager.Enqueue(new InputCommandMessage(ChatMapping.InputMappingId, sender, EncodeUtf8(input.Text), connectionId));
        return ChatMappingResult.Ok();
    }

    public ChatTickResult RunTick(ulong authoritativeTick)
    {
        _ = authoritativeTick;
        Manager.Tick();
        var events = new List<ChatMessageEvent>();
        foreach (WorldMessage message in Manager.DrainOutbox())
        {
            if (message is not WorldChangeMessage change) continue;
            for (int i = 0; i < change.Rpcs.Count; i++)
            {
                ClientRpcRecord rpc = change.Rpcs[i];
                string text = rpc.Args.Count > 0 ? rpc.Args[0]?.ToString() ?? string.Empty : string.Empty;
                events.Add(new ChatMessageEvent(rpc.MessageId, rpc.RoomSequence, rpc.Sender.ToHex(), text, rpc.AppliedTick));
            }
        }

        return new ChatTickResult(Manager.World.Tick, Manager.World.Revision, Array.Empty<ChatMappingResult>(), events);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsBindings) _bindings.Dispose();
    }

    private static byte[] EncodeUtf8(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
        byte[] bytes = new byte[4 + utf8.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, bytes, 4, utf8.Length);
        return bytes;
    }
}
