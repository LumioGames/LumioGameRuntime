using System;
using System.Collections.Generic;
using System.Text;
using Lumio.GameRuntime.Command;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Ingress;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Replication.Chat;

/// <summary>
/// Host-facing chat command path: InputCommand → bounded IngressCapture → CommandBuffer
/// at EcsCommandBufferCommit → ChatComponent last-message write and same-tick chat.event Delta.
/// </summary>
public sealed class ChatCommandRuntime : IDisposable
{
    private const string CommandWorldId = "chat";
    private const string CommitProcessorId = "chat-commit";

    private readonly object _gate = new();
    private readonly EntityBindingQuery? _bindings;
    private readonly bool _ownsBindings;
    private readonly ChatIngressWorld _world;
    private readonly ChatIngressCapture _ingress = new();
    private readonly CommandModule _commands;
    private readonly ChatTypedMapping _replica = new();
    private readonly Dictionary<string, Dictionary<ulong, ChatMessageEvent[]>> _eventsByRoomTick =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _roomByNet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _committedTick = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _nextMessageId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _nextRoomSequence = new(StringComparer.Ordinal);
    private readonly int _ownerThreadId;
    private ulong _currentTick;
    private ulong _revision;
    private bool _faulted;
    private bool _disposed;

    private ChatCommandRuntime(EntityBindingQuery? bindings, bool ownsBindings, ChatIngressWorld world, CommandModule commands)
    {
        _bindings = bindings;
        _ownsBindings = ownsBindings;
        _world = world;
        _commands = commands;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>Creates a runtime bound to the calling Simulation Owner Thread.</summary>
    public static ChatCommandRuntime Create(EntityBindingQuery? bindings = null, bool ownsBindings = false)
    {
        ChatIngressWorld world = ChatIngressWorld.Create();
        CommandModule commands = CommandModule.Create(world: world.World);
        if (!commands.Configure().Succeeded || !commands.Start().Succeeded)
        {
            world.Dispose();
            throw new InvalidOperationException("Command module failed to start for chat commit.");
        }

        return new ChatCommandRuntime(bindings, ownsBindings, world, commands);
    }

    /// <summary>Last committed authoritative tick. Zero before the first <see cref="RunTick"/>.</summary>
    public ulong CurrentTick
    {
        get { lock (_gate) return _currentTick; }
    }

    /// <summary>Authoritative revision after the last tick commit.</summary>
    public ulong Revision
    {
        get { lock (_gate) return _revision; }
    }

    /// <summary>True after an owner-thread fail-stop.</summary>
    public bool IsFaulted
    {
        get { lock (_gate) return _faulted; }
    }

    /// <summary>Managed thread id of the simulation owner.</summary>
    public int OwnerThreadId => _ownerThreadId;

    /// <summary>Live NetEntityIds in ordinal order. Used by FullSnapshot live-entity census.</summary>
    public IReadOnlyList<string> LiveNetEntityIds => _world.LiveNetEntityIds;

    /// <summary>Creates the ChatComponent entity for an already-bound connection.</summary>
    public ChatMappingResult AttachMember(string roomId, string connectionId)
    {
        lock (_gate)
        {
            if (!TryEnterOwnerWrite(out ChatMappingResult rejection))
                return rejection;
            BindingQueryResult resolved = ResolveConnection(roomId, connectionId);
            if (resolved.Outcome != "ok" || !resolved.Binding.HasValue)
                return FromBinding(resolved);
            string netEntityId = resolved.Binding.Value.NetEntityId;
            if (!_world.TryCreateEntity(netEntityId, out _))
                return ChatMappingResult.Reject("runtime_failure", "failed to create chat entity");
            _roomByNet[netEntityId] = roomId;
            return ChatMappingResult.Ok();
        }
    }

    /// <summary>Admits typed ChatInput into the bounded per-connection ingress. Network-thread safe.</summary>
    public ChatMappingResult AdmitInput(string roomId, string connectionId, ulong connectionGeneration, ChatInput input)
    {
        if (input.Text is null) throw new ArgumentException("ChatInput.Text is required.", nameof(input));
        if (Encoding.UTF8.GetByteCount(input.Text) > ChatMapping.MaxTextUtf8Bytes)
            return ChatMappingResult.Reject("chat_text_too_long", "chat text exceeds 512 UTF-8 bytes", ChatMapping.InputMappingId);

        lock (_gate)
        {
            if (_faulted)
                return ChatMappingResult.Reject("runtime_failure", "chat runtime is faulted");
            if (IsOwnerThread())
            {
                ChatMappingResult bound = ValidateSender(roomId, connectionId, connectionGeneration);
                if (!bound.Succeeded) return bound;
            }

            ChatIngressEnqueueStatus enqueued = _ingress.TryEnqueue(connectionId, input.Text);
            if (enqueued == ChatIngressEnqueueStatus.QueueFull)
                return ChatMappingResult.Reject("queue_full", "per-connection ingress is full", ChatMapping.InputMappingId);
            if (enqueued != ChatIngressEnqueueStatus.Accepted)
                return ChatMappingResult.Reject("bad_envelope", "chat.input was not admitted", ChatMapping.InputMappingId);
            return ChatMappingResult.Ok();
        }
    }

    /// <summary>Decodes a C-1 InputCommand and admits chat.input into ingress.</summary>
    public ChatMappingResult AdmitInputCommand(string roomId, string connectionId, ulong connectionGeneration, string envelopeJson)
    {
        if (!ChatEnvelope.TryParseInputCommand(envelopeJson, out string text, out ChatMappingResult parsed))
            return parsed;
        return AdmitInput(roomId, connectionId, connectionGeneration, new ChatInput(text));
    }

    /// <summary>
    /// Owner-thread ChatComponent.SetMessage. Off-thread calls fail-stop with zero writes.
    /// Direct SetMessage updates last-message state without emitting a live event.
    /// </summary>
    public ChatMappingResult SetMessage(string roomId, string netEntityId, string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        lock (_gate)
        {
            if (!TryEnterOwnerWrite(out ChatMappingResult rejection))
                return rejection;
            if (Encoding.UTF8.GetByteCount(text) > ChatMapping.MaxTextUtf8Bytes)
                return ChatMappingResult.Reject("chat_text_too_long", "chat text exceeds 512 UTF-8 bytes", ChatMapping.InputMappingId);
            return ApplySetMessage(roomId, netEntityId, text, _currentTick, events: null);
        }
    }

    /// <summary>IngressCapture + CommandBuffer commit on the Simulation Owner Thread.</summary>
    public ChatTickResult RunTick(ulong authoritativeTick)
    {
        lock (_gate)
        {
            if (!TryEnterOwnerWrite(out ChatMappingResult rejection))
                return new ChatTickResult(_currentTick, _revision, new[] { rejection }, Array.Empty<ChatMessageEvent>());
            if (authoritativeTick <= _currentTick)
                return new ChatTickResult(
                    _currentTick,
                    _revision,
                    new[] { ChatMappingResult.Reject("runtime_failure", "tick id must strictly increase") },
                    Array.Empty<ChatMessageEvent>());

            _currentTick = authoritativeTick;
            ChatIngressBatch captured = _ingress.CaptureForTick();
            var results = new ChatMappingResult[captured.Items.Count];
            var events = new List<ChatMessageEvent>(captured.Items.Count);
            var writes = new List<PendingWrite>(captured.Items.Count);
            var sendersThisTick = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < captured.Items.Count; i++)
            {
                ChatIngressItem item = captured.Items[i];
                ChatMappingResult prepared = PrepareWrite(item, authoritativeTick, sendersThisTick, out PendingWrite? write);
                results[i] = prepared;
                if (write.HasValue) writes.Add(write.Value);
            }

            if (writes.Count > 0)
            {
                ChatMappingResult committed = CommitWrites(authoritativeTick, writes, events);
                if (!committed.Succeeded)
                {
                    _faulted = true;
                    for (int i = 0; i < results.Length; i++)
                    {
                        if (results[i].Succeeded)
                            results[i] = committed;
                    }
                }
                else
                {
                    for (int i = 0; i < results.Length; i++)
                    {
                        if (results[i].Succeeded && results[i].Event is null)
                        {
                            ChatMessageEvent? matched = null;
                            for (int e = 0; e < events.Count; e++)
                            {
                                if (events[e].Text == captured.Items[i].Text &&
                                    events[e].AppliedTick == authoritativeTick)
                                {
                                    matched = events[e];
                                    break;
                                }
                            }

                            if (matched.HasValue)
                                results[i] = ChatMappingResult.Ok(matched);
                        }
                    }
                }
            }

            _revision++;
            ChatMessageEvent[] committedEvents = events.ToArray();
            RememberRoomEvents(authoritativeTick, writes, committedEvents);
            return new ChatTickResult(authoritativeTick, _revision, results, committedEvents);
        }
    }

    /// <summary>
    /// C-1 FullSnapshot JSON. Live rooms emit one entity.identity stateBlock;
    /// zero live entities emit empty stateBlocks.
    /// </summary>
    public string BuildFullSnapshot(string roomId, ulong tickId, ulong revision)
    {
        lock (_gate)
        {
            IReadOnlyList<EntityIdentityRecord> records = CollectIdentityRecords(roomId);
            return ChatEnvelope.FullSnapshot(tickId, revision, records);
        }
    }

    /// <summary>
    /// C-1 Delta JSON frames for a committed tick. One envelope per chat.event because
    /// mappingId must be unique inside a block array. Empty ticks return one empty Delta.
    /// Idempotent: does not dequeue.
    /// </summary>
    public IReadOnlyList<string> BuildDelta(string roomId, ulong tickId, ulong revision)
    {
        lock (_gate)
        {
            ChatMessageEvent[] events = Array.Empty<ChatMessageEvent>();
            if (_eventsByRoomTick.TryGetValue(roomId ?? string.Empty, out Dictionary<ulong, ChatMessageEvent[]>? byTick) &&
                byTick.TryGetValue(tickId, out ChatMessageEvent[]? found) &&
                found is not null)
                events = found;
            return ChatEnvelope.DeltaFrames(tickId, revision, events);
        }
    }

    /// <summary>Replica window after applying host-sent FullSnapshot / Delta bytes.</summary>
    public ChatMappingResult ApplyDownstream(string connectionId, string messageJson) =>
        _replica.ApplyDownstream(connectionId, messageJson);

    /// <summary>Replica-displayed events for a connection.</summary>
    public ChatMessageEvent[] DisplayedEvents(string connectionId) =>
        _replica.DisplayedEvents(connectionId);

    /// <summary>Reads persist-only last-message fields for a live entity.</summary>
    public bool TryGetLastMessage(string netEntityId, out string? text, out ulong tick)
    {
        text = null;
        tick = 0UL;
        lock (_gate)
        {
            if (!_world.TryGetEntity(netEntityId, out LocalEntityId local))
                return false;
            if (!_world.TryReadLastMessage(local, out string value, out tick))
                return false;
            text = value;
            return true;
        }
    }

    /// <summary>Destroys the chat entity. Owner thread only.</summary>
    public bool DestroyEntity(string netEntityId)
    {
        lock (_gate)
        {
            if (!IsOwnerThread())
            {
                _faulted = true;
                return false;
            }

            bool destroyed = _world.DestroyEntity(netEntityId);
            if (destroyed) _roomByNet.Remove(netEntityId);
            return destroyed;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _ingress.Complete();
        _world.Dispose();
        if (_ownsBindings) _bindings?.Dispose();
    }

    private ChatMappingResult PrepareWrite(
        ChatIngressItem item,
        ulong tick,
        HashSet<string> sendersThisTick,
        out PendingWrite? write)
    {
        write = null;
        if (_bindings is null)
            return ChatMappingResult.Reject("runtime_failure", "no binding table");

        BindingQueryResult self = _bindings.SelfLookup(item.ConnectionId, "client-replica");
        if (self.Outcome != "ok" || !self.Binding.HasValue)
            return FromBinding(self);

        ConnectionBinding binding = self.Binding.Value;
        BindingQueryResult occupancy = _bindings.ResolveByNetEntityId(
            binding.RoomId,
            binding.NetEntityId,
            binding.ConnectionGeneration,
            "server-authoritative");
        if (occupancy.Outcome != "ok")
            return FromBinding(occupancy);

        if (!ChatPayload.TryParseSender(binding.NetEntityId, out _))
            return ChatMappingResult.Reject(
                "bad_envelope",
                "bound NetEntityId is not a C-1 senderNetEntityId u64",
                ChatMapping.InputMappingId);

        if (!_world.TryGetEntity(binding.NetEntityId, out LocalEntityId local))
            return ChatMappingResult.Reject("runtime_failure", "chat entity is not live", ChatMapping.InputMappingId);

        string senderKey = binding.RoomId + "\n" + binding.NetEntityId;
        if ((_committedTick.TryGetValue(senderKey, out ulong usedTick) && usedTick == tick) ||
            !sendersThisTick.Add(senderKey))
            return ChatMappingResult.Reject("chat_rate_exceeded", "second chat.input within the authoritative tick", ChatMapping.InputMappingId);

        write = new PendingWrite(binding.RoomId, binding.NetEntityId, local, item.Text, senderKey);
        return ChatMappingResult.Ok();
    }

    private ChatMappingResult ApplySetMessage(
        string roomId,
        string netEntityId,
        string text,
        ulong appliedTick,
        List<ChatMessageEvent>? events)
    {
        if (!_world.TryGetEntity(netEntityId, out LocalEntityId local))
            return ChatMappingResult.Reject("runtime_failure", "chat entity is not live");

        string senderKey = roomId + "\n" + netEntityId;
        if (_committedTick.TryGetValue(senderKey, out ulong usedTick) && usedTick == appliedTick)
            return ChatMappingResult.Reject("chat_rate_exceeded", "second chat.input within the authoritative tick", ChatMapping.InputMappingId);

        var write = new PendingWrite(roomId, netEntityId, local, text, senderKey);
        var emitted = events ?? new List<ChatMessageEvent>();
        ChatMappingResult committed = CommitWrites(appliedTick, new[] { write }, emitted);
        if (!committed.Succeeded) return committed;
        if (events is null)
            return ChatMappingResult.Ok();
        return ChatMappingResult.Ok(emitted.Count == 0 ? null : emitted[emitted.Count - 1]);
    }

    private ChatMappingResult CommitWrites(
        ulong tick,
        IReadOnlyList<PendingWrite> writes,
        List<ChatMessageEvent> events)
    {
        BufferOpenResult opened = _commands.OpenBuffer(
            new ProcessorInvocationKey(tick, CommandWorldId, ProcessorDescriptorPhase.EcsCommandBufferCommit, CommitProcessorId),
            CommandBufferBudget.Unlimited);
        if (!opened.Succeeded || opened.Buffer is null)
            return ChatMappingResult.Reject("runtime_failure", opened.Failure?.Detail ?? "command buffer did not open");

        ProcessorCommandBuffer buffer = opened.Buffer;
        for (int i = 0; i < writes.Count; i++)
        {
            PendingWrite write = writes[i];
            string entityId = write.Local.ToString();
            CommandAppendResult text = buffer.Writer.Write(
                entityId,
                ChatIngressWorld.ComponentName,
                ChatIngressWorld.LastMessageTextFieldId,
                ChatIngressWorld.EncodeText(write.Text));
            CommandAppendResult tickField = buffer.Writer.Write(
                entityId,
                ChatIngressWorld.ComponentName,
                ChatIngressWorld.LastMessageTickFieldId,
                ChatIngressWorld.EncodeTick(tick));
            if (!text.IsAccepted || !tickField.IsAccepted)
                return ChatMappingResult.Reject("runtime_failure", "ChatComponent write was not appended");
        }

        CommandMergeResult merged = _commands.Merge(tick, new[] { buffer.Seal() });
        if (!merged.Succeeded || merged.Batch is null)
            return ChatMappingResult.Reject("runtime_failure", merged.GeneratedErrorId ?? "merge failed");

        CommandPreflightResult prepared = _commands.Prepare(merged.Batch);
        if (!prepared.IsPrepared || prepared.Delta is null)
            return ChatMappingResult.Reject("runtime_failure", prepared.Failure?.Detail ?? "prepare failed");

        CommandApplyReceipt applied = _commands.Apply(prepared.Delta);
        if (!applied.IsApplied)
            return ChatMappingResult.Reject("runtime_failure", applied.GeneratedErrorId ?? "apply failed");

        for (int i = 0; i < writes.Count; i++)
        {
            PendingWrite write = writes[i];
            _committedTick[write.SenderKey] = tick;
            if (events != null)
            {
                ulong messageId = Next(write.RoomId, _nextMessageId);
                ulong roomSequence = Next(write.RoomId, _nextRoomSequence);
                events.Add(new ChatMessageEvent(messageId, roomSequence, write.NetEntityId, write.Text, tick));
            }
        }

        return ChatMappingResult.Ok();
    }

    private void RememberRoomEvents(
        ulong tick,
        IReadOnlyList<PendingWrite> writes,
        ChatMessageEvent[] events)
    {
        if (writes.Count == 0 || events.Length == 0) return;
        var grouped = new Dictionary<string, List<ChatMessageEvent>>(StringComparer.Ordinal);
        int limit = Math.Min(writes.Count, events.Length);
        for (int i = 0; i < limit; i++)
        {
            string roomId = writes[i].RoomId;
            if (!grouped.TryGetValue(roomId, out List<ChatMessageEvent>? list))
            {
                list = new List<ChatMessageEvent>();
                grouped[roomId] = list;
            }

            list.Add(events[i]);
        }

        foreach (KeyValuePair<string, List<ChatMessageEvent>> pair in grouped)
        {
            if (!_eventsByRoomTick.TryGetValue(pair.Key, out Dictionary<ulong, ChatMessageEvent[]>? byTick))
            {
                byTick = new Dictionary<ulong, ChatMessageEvent[]>();
                _eventsByRoomTick[pair.Key] = byTick;
            }

            byTick[tick] = pair.Value.ToArray();
        }
    }

    private static ulong Next(string roomId, Dictionary<string, ulong> counters)
    {
        string key = roomId ?? string.Empty;
        if (!counters.TryGetValue(key, out ulong current)) current = 0;
        ulong next = current + 1UL;
        counters[key] = next;
        return next;
    }

    private IReadOnlyList<EntityIdentityRecord> CollectIdentityRecords(string roomId)
    {
        IReadOnlyList<string> live = LiveNetEntityIdsFor(roomId);
        if (live.Count == 0 || _bindings is null)
            return Array.Empty<EntityIdentityRecord>();

        var records = new List<EntityIdentityRecord>(live.Count);
        for (int i = 0; i < live.Count; i++)
        {
            string netEntityId = live[i];
            if (!ChatPayload.TryParseSender(netEntityId, out ulong id))
                continue;
            if (!TryReadEntityType(roomId, netEntityId, out string entityType))
                continue;
            records.Add(new EntityIdentityRecord(id, entityType, ReadUnmappedMark(roomId, netEntityId)));
        }

        records.Sort(static (left, right) => left.NetEntityId.CompareTo(right.NetEntityId));
        return records;
    }

    private bool TryReadEntityType(string roomId, string netEntityId, out string entityType)
    {
        entityType = string.Empty;
        BindingQueryResult attribute = _bindings!.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = roomId,
                NetEntityId = netEntityId,
                AttributeId = "EntityIdentity.entityType",
            });
        if (attribute.Outcome == "ok" &&
            attribute.Value is string fromAttribute &&
            IsWireEntityType(fromAttribute))
        {
            entityType = fromAttribute;
            return true;
        }

        BindingQueryResult occupancy = _bindings.ResolveByNetEntityId(
            roomId,
            netEntityId,
            connectionGeneration: null,
            "server-authoritative");
        if (occupancy.Outcome == "ok" && IsWireEntityType(occupancy.EntityType))
        {
            entityType = occupancy.EntityType!;
            return true;
        }

        return false;
    }

    private string ReadUnmappedMark(string roomId, string netEntityId)
    {
        BindingQueryResult result = _bindings!.QueryAttribute(
            new AttributeQueryRequest
            {
                CallerScope = "server-authoritative",
                RoomId = roomId,
                NetEntityId = netEntityId,
                AttributeId = "EntityIdentity.unmappedMark",
            });
        return result.Outcome == "ok" && result.Value is string mark ? mark : string.Empty;
    }

    private static bool IsWireEntityType(string? entityType) =>
        string.Equals(entityType, "player", StringComparison.Ordinal) ||
        string.Equals(entityType, "bot", StringComparison.Ordinal);

    private IReadOnlyList<string> LiveNetEntityIdsFor(string roomId)
    {
        IReadOnlyList<string> live = _world.LiveNetEntityIds;
        if (string.IsNullOrEmpty(roomId)) return live;
        var filtered = new List<string>(live.Count);
        for (int i = 0; i < live.Count; i++)
        {
            if (_roomByNet.TryGetValue(live[i], out string? room) &&
                string.Equals(room, roomId, StringComparison.Ordinal))
                filtered.Add(live[i]);
        }

        return filtered;
    }

    private ChatMappingResult ValidateSender(string roomId, string connectionId, ulong connectionGeneration)
    {
        BindingQueryResult resolved = ResolveConnection(roomId, connectionId);
        if (resolved.Outcome != "ok" || !resolved.Binding.HasValue)
            return FromBinding(resolved);

        ConnectionBinding binding = resolved.Binding.Value;
        BindingQueryResult occupancy = _bindings!.ResolveByNetEntityId(
            roomId,
            binding.NetEntityId,
            connectionGeneration,
            "server-authoritative");
        if (occupancy.Outcome != "ok")
            return FromBinding(occupancy);

        if (!ChatPayload.TryParseSender(binding.NetEntityId, out _))
            return ChatMappingResult.Reject(
                "bad_envelope",
                "bound NetEntityId is not a C-1 senderNetEntityId u64",
                ChatMapping.InputMappingId);

        return ChatMappingResult.Ok();
    }

    private BindingQueryResult ResolveConnection(string roomId, string connectionId)
    {
        if (_bindings is null)
            return BindingQueryResult.RequestError("binding_not_found", "no binding table");
        return _bindings.ResolveByConnection(roomId, connectionId);
    }

    private bool TryEnterOwnerWrite(out ChatMappingResult rejection)
    {
        if (!IsOwnerThread())
        {
            _faulted = true;
            rejection = ChatMappingResult.Reject("runtime_failure", "chat write requires Simulation Owner Thread");
            return false;
        }

        if (_faulted)
        {
            rejection = ChatMappingResult.Reject("runtime_failure", "chat runtime is faulted");
            return false;
        }

        rejection = default;
        return true;
    }

    private bool IsOwnerThread() => Environment.CurrentManagedThreadId == _ownerThreadId;

    private static ChatMappingResult FromBinding(BindingQueryResult result)
    {
        if (string.Equals(result.Outcome, "request_error", StringComparison.Ordinal))
            return ChatMappingResult.Reject(result.Code ?? "bad_envelope", result.Detail ?? result.Code ?? "binding request error");
        return ChatMappingResult.Reject(result.Outcome, result.Detail ?? result.Outcome);
    }

    private readonly record struct PendingWrite(
        string RoomId,
        string NetEntityId,
        LocalEntityId Local,
        string Text,
        string SenderKey);
}
