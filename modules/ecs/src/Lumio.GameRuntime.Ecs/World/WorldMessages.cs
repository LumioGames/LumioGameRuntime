using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Unified inbox item: input commands (server), welcome messages, and world-change packs (client).
/// The only legal cross-thread entry is <see cref="WorldManager.Enqueue"/>.
/// </summary>
public abstract class WorldMessage
{
    private protected WorldMessage()
    {
    }

    /// <summary>Optional host connection this message is addressed to. Null means broadcast.</summary>
    public string? Connection { get; init; }
}

/// <summary>Client-to-server input: <c>chat.input</c>, <c>field.write</c>, or a ServerRpc envelope.</summary>
public sealed class InputCommandMessage : WorldMessage
{
    /// <summary>Creates an input command.</summary>
    public InputCommandMessage(string mappingId, NetEntityId sender, ReadOnlyMemory<byte> payload, string? connection = null)
    {
        MappingId = mappingId ?? throw new ArgumentNullException(nameof(mappingId));
        Sender = sender;
        Payload = payload;
        Commands = new[] { new InputCommandPart(MappingId, Payload) };
        Connection = connection;
    }

    public InputCommandMessage(NetEntityId sender, IReadOnlyList<InputCommandPart> commands, string? connection = null)
    {
        Sender = sender;
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Connection = connection;
        MappingId = Commands.Count == 0 ? string.Empty : Commands[0].MappingId;
        Payload = Commands.Count == 0 ? ReadOnlyMemory<byte>.Empty : Commands[0].Payload;
    }

    /// <summary>C-1 mapping id (<c>chat.input</c>, <c>field.write</c>, or a generated rpc id).</summary>
    public string MappingId { get; }

    /// <summary>Sending entity. For owner field writes this is the bound self.</summary>
    public NetEntityId Sender { get; }

    /// <summary>LumioBinV1 payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    public IReadOnlyList<InputCommandPart> Commands { get; }
}

public readonly struct InputCommandPart
{
    public InputCommandPart(string mappingId, ReadOnlyMemory<byte> payload)
    {
        MappingId = mappingId ?? throw new ArgumentNullException(nameof(mappingId));
        Payload = payload;
    }

    public string MappingId { get; }
    public ReadOnlyMemory<byte> Payload { get; }
}

/// <summary>First client message after connect: world instance id + this connection's entity.</summary>
public sealed class WelcomeMessage : WorldMessage
{
    /// <summary>Creates a welcome message.</summary>
    public WelcomeMessage(ulong instanceId, NetEntityId self, string? connection = null)
        : this(instanceId, self, 1UL, connection)
    {
    }

    /// <summary>Creates a welcome message with the current binding generation.</summary>
    public WelcomeMessage(ulong instanceId, NetEntityId self, ulong connectionGeneration, string? connection = null)
    {
        InstanceId = instanceId;
        Self = self;
        ConnectionGeneration = connectionGeneration;
        Connection = connection;
    }

    /// <summary>Server world instance id. The client uses it to compose 128-bit identities.</summary>
    public ulong InstanceId { get; }

    /// <summary>This connection's bound entity. Applied to <see cref="World.Self"/> at commit.</summary>
    public NetEntityId Self { get; }

    /// <summary>Binding generation delivered with this welcome.</summary>
    public ulong ConnectionGeneration { get; }
}

/// <summary>One create record: entity type + net id + visible field values.</summary>
public sealed class CreateRecord
{
    /// <summary>Creates a create record.</summary>
    public CreateRecord(string entityType, NetEntityId netEntityId, IReadOnlyList<FieldValue> fields)
    {
        EntityType = entityType ?? throw new ArgumentNullException(nameof(entityType));
        NetEntityId = netEntityId;
        Fields = fields ?? Array.Empty<FieldValue>();
    }

    /// <summary>Declared entity type wire name or CLR name.</summary>
    public string EntityType { get; }

    /// <summary>Issued identity.</summary>
    public NetEntityId NetEntityId { get; }

    /// <summary>Visible Sync field snapshot at create time.</summary>
    public IReadOnlyList<FieldValue> Fields { get; }
}

/// <summary>One replicated field value.</summary>
public readonly struct FieldValue
{
    /// <summary>Creates a field value.</summary>
    public FieldValue(string componentId, string fieldId, object? value)
    {
        ComponentId = componentId;
        FieldId = fieldId;
        Value = value;
    }

    /// <summary>Component type name.</summary>
    public string ComponentId { get; }

    /// <summary>Field name in camelCase.</summary>
    public string FieldId { get; }

    /// <summary>Decoded value.</summary>
    public object? Value { get; }

    /// <summary>C-2 attribute id.</summary>
    public string AttributeId => ComponentId + "." + FieldId;
}

/// <summary>One field mutation in a world-change pack.</summary>
public readonly struct FieldChange
{
    /// <summary>Creates a field change.</summary>
    public FieldChange(NetEntityId netEntityId, string componentId, string fieldId, object? value, ChangeReason reason)
        : this(netEntityId, componentId, fieldId, value, reason, default)
    {
    }

    /// <summary>Creates a field change optionally addressed to one observer.</summary>
    public FieldChange(NetEntityId netEntityId, string componentId, string fieldId, object? value, ChangeReason reason, NetEntityId observerId)
    {
        NetEntityId = netEntityId;
        ComponentId = componentId;
        FieldId = fieldId;
        Value = value;
        Reason = reason;
        ObserverId = observerId;
    }

    /// <summary>Target entity.</summary>
    public NetEntityId NetEntityId { get; }

    /// <summary>Component type name.</summary>
    public string ComponentId { get; }

    /// <summary>Field name in camelCase.</summary>
    public string FieldId { get; }

    /// <summary>New value.</summary>
    public object? Value { get; }

    /// <summary>Sync or Correction.</summary>
    public ChangeReason Reason { get; }

    /// <summary>Observer that should receive a correction; zero means normal broadcast.</summary>
    public NetEntityId ObserverId { get; }
}

/// <summary>One ClientRpc invocation in a world-change pack.</summary>
public readonly struct ClientRpcRecord
{
    /// <summary>Creates a client RPC record.</summary>
    public ClientRpcRecord(
        NetEntityId target,
        string componentId,
        string method,
        IReadOnlyList<object?> args,
        ulong messageId,
        ulong roomSequence,
        NetEntityId sender,
        ulong appliedTick,
        Scope scope = Scope.Room)
    {
        Target = target;
        ComponentId = componentId;
        Method = method;
        Args = args;
        MessageId = messageId;
        RoomSequence = roomSequence;
        Sender = sender;
        AppliedTick = appliedTick;
        Scope = scope;
    }

    /// <summary>Entity whose component receives the RPC.</summary>
    public NetEntityId Target { get; }

    /// <summary>Component type name.</summary>
    public string ComponentId { get; }

    /// <summary>Method name.</summary>
    public string Method { get; }

    /// <summary>Decoded arguments.</summary>
    public IReadOnlyList<object?> Args { get; }

    /// <summary>Stamped message id.</summary>
    public ulong MessageId { get; }

    /// <summary>World-strict sequence.</summary>
    public ulong RoomSequence { get; }

    /// <summary>Original sender.</summary>
    public NetEntityId Sender { get; }

    /// <summary>Tick the event was committed on.</summary>
    public ulong AppliedTick { get; }
    public Scope Scope { get; }
}

/// <summary>
/// One tick pack: creates (first, WorldEntity first among them), field changes, destroys, ClientRpcs.
/// Applied atomically on the client after staging.
/// </summary>
public sealed class WorldChangeMessage : WorldMessage
{
    /// <summary>Creates a world-change pack.</summary>
    public WorldChangeMessage(
        ulong tick,
        IReadOnlyList<CreateRecord> creates,
        IReadOnlyList<FieldChange> fields,
        IReadOnlyList<NetEntityId> destroys,
        IReadOnlyList<ClientRpcRecord> rpcs,
        string? connection = null,
        NetEntityId observerId = default)
    {
        Tick = tick;
        Creates = creates ?? Array.Empty<CreateRecord>();
        Fields = fields ?? Array.Empty<FieldChange>();
        Destroys = destroys ?? Array.Empty<NetEntityId>();
        Rpcs = rpcs ?? Array.Empty<ClientRpcRecord>();
        Connection = connection;
        ObserverId = observerId;
    }

    /// <summary>Authoritative tick of this pack.</summary>
    public ulong Tick { get; }

    /// <summary>Create records, already ordered with WorldEntity first.</summary>
    public IReadOnlyList<CreateRecord> Creates { get; }

    /// <summary>Field mutations after creates.</summary>
    public IReadOnlyList<FieldChange> Fields { get; }

    /// <summary>Destroyed identities.</summary>
    public IReadOnlyList<NetEntityId> Destroys { get; }

    /// <summary>ClientRpcs stamped this tick.</summary>
    public IReadOnlyList<ClientRpcRecord> Rpcs { get; }

    /// <summary>Observer identity addressed by this pack. Zero means broadcast/unaddressed.</summary>
    public NetEntityId ObserverId { get; }
}
