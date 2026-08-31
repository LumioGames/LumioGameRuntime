using System;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command;

/// <summary>Convenience writer whose lifetime is bounded by one processor invocation.</summary>
public sealed class CommandBufferWriter
{
    private readonly ProcessorCommandBuffer _buffer;

    internal CommandBufferWriter(ProcessorCommandBuffer buffer) => _buffer = buffer;

    public CommandAppendResult Append(Command command) => _buffer.Append(command);

    public CommandAppendResult Append(in StructuralCommand command) => _buffer.Append(in command);

    public CommandAppendResult TryAppend(in StructuralCommand command) => _buffer.Append(in command);

    public DeferredEntityToken AllocateDeferredEntity() => _buffer.AllocateDeferredEntity();

    public CommandAppendResult Create(string componentType, out DeferredEntityToken token, ReadOnlyMemory<byte> payload = default,
        string? commandId = null)
    {
        token = _buffer.AllocateDeferredEntity();
        CommandAppendResult result = _buffer.Append(CommandKind.Create, componentType: componentType, payload: payload,
            deferredTarget: token, commandId: commandId);
        if (!result.IsAccepted)
        {
            _buffer.RollbackDeferredEntity(token);
            token = default;
        }
        return result;
    }

    public CommandAppendResult Create(string componentType, out DeferredEntityToken token, byte[] payload, string? commandId = null) =>
        Create(componentType, out token, payload.AsMemory(), commandId);

    public CommandAppendResult Write(string entityId, string componentType, string fieldName,
        ReadOnlyMemory<byte> payload = default, string? commandId = null) =>
        _buffer.Append(CommandKind.Write, entityId, componentType, fieldName, payload, null, commandId);

    public CommandAppendResult Write(string entityId, string componentType, string fieldName,
        CommandPayload payload, string? commandId = null) =>
        Write(entityId, componentType, fieldName, payload.Bytes, commandId);

    public CommandAppendResult Write(DeferredEntityToken target, string componentType, string fieldName,
        ReadOnlyMemory<byte> payload = default, string? commandId = null) =>
        _buffer.Append(CommandKind.Write, null, componentType, fieldName, payload, target, commandId);

    public CommandAppendResult Write(DeferredEntityToken target, string componentType, string fieldName,
        CommandPayload payload, string? commandId = null) =>
        Write(target, componentType, fieldName, payload.Bytes, commandId);

    public CommandAppendResult Destroy(string entityId, string? commandId = null) =>
        _buffer.Append(CommandKind.Destroy, entityId, commandId: commandId);

    public CommandAppendResult Destroy(DeferredEntityToken target, string? commandId = null) =>
        _buffer.Append(CommandKind.Destroy, deferredTarget: target, commandId: commandId);
}
