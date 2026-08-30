using System;

namespace Lumio.GameRuntime.Command;

/// <summary>Runtime tagged command value; it has no wire/schema identifier.</summary>
public sealed class StructuralCommand : Command
{
    public StructuralCommand(
        CommandKind kind,
        string? targetEntityId = null,
        string? componentType = null,
        string? fieldName = null,
        ReadOnlyMemory<byte> payload = default,
        DeferredEntityToken? deferredTarget = null,
        string? commandId = null)
        : this(kind, new CommandSortKey(Lumio.Gen.ContractTypes.ProcessorDescriptorPhase.ProcessorPlan, "default", 1UL),
            targetEntityId, componentType, fieldName, payload, deferredTarget, commandId)
    {
    }

    public StructuralCommand(
        CommandKind kind,
        CommandSortKey sortKey,
        string? targetEntityId = null,
        string? componentType = null,
        string? fieldName = null,
        ReadOnlyMemory<byte> payload = default,
        DeferredEntityToken? deferredTarget = null,
        string? commandId = null,
        ulong? estimatedBytes = null)
        : base(kind, sortKey, targetEntityId, componentType, fieldName, payload, deferredTarget, commandId, estimatedBytes)
    {
    }

    public static StructuralCommand Create(CommandSortKey key, string componentType, DeferredEntityToken token,
        ReadOnlyMemory<byte> payload = default, string? commandId = null) =>
        new(CommandKind.Create, key, componentType: componentType, payload: payload, deferredTarget: token, commandId: commandId);

    public static StructuralCommand Write(CommandSortKey key, string entityId, string componentType, string fieldName,
        ReadOnlyMemory<byte> payload = default, string? commandId = null) =>
        new(CommandKind.Write, key, entityId, componentType, fieldName, payload, null, commandId);

    public static StructuralCommand Write(CommandSortKey key, DeferredEntityToken token, string componentType, string fieldName,
        ReadOnlyMemory<byte> payload = default, string? commandId = null) =>
        new(CommandKind.Write, key, null, componentType, fieldName, payload, token, commandId);

    public static StructuralCommand Destroy(CommandSortKey key, string entityId, string? commandId = null) =>
        new(CommandKind.Destroy, key, targetEntityId: entityId, commandId: commandId);
}
