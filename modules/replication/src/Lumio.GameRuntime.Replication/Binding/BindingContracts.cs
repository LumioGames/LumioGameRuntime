using System;

namespace Lumio.GameRuntime.Replication.Binding;

public readonly record struct ConnectionBinding(
    string AccountId,
    string RoomId,
    string NetEntityId,
    string EntityType,
    ulong ConnectionGeneration);

public sealed class BindingRecordRequest
{
    public string? AccountId { get; init; }
    public string? RoomId { get; init; }
    public string? NetEntityId { get; init; }
    public string? EntityType { get; init; }
    public ulong? ConnectionGeneration { get; init; }
    public object? AccountEntityRef { get; init; }
    public object? StorageHandle { get; init; }
    public object? HostPointer { get; init; }
}

public sealed class AttributeQueryRequest
{
    public string? CallerScope { get; init; }
    public string? RoomId { get; init; }
    public string? NetEntityId { get; init; }
    public string? AttributeId { get; init; }
    public ulong? ConnectionGeneration { get; init; }
    public string? Origin { get; init; }
    public object? AccountEntityRef { get; init; }
    public object? StorageHandle { get; init; }
    public object? HostPointer { get; init; }
}

public readonly record struct AttributeDeclaration(
    string AttributeId,
    string ValueType,
    string Persistence,
    string Replication,
    string Visibility);

public readonly record struct BindingQueryResult(
    string Outcome,
    string? Code = null,
    string? Detail = null,
    ConnectionBinding? Binding = null,
    ulong? AuthoritativeRevision = null,
    string? NetEntityId = null,
    string? RoomId = null,
    string? EntityType = null,
    string? AttributeId = null,
    object? Value = null,
    ulong? ObservedRevision = null,
    ulong? ObservedTick = null)
{
    public static BindingQueryResult OkBinding(ConnectionBinding binding, ulong? revision = null) =>
        new("ok", Binding: binding, AuthoritativeRevision: revision, NetEntityId: binding.NetEntityId, RoomId: binding.RoomId, EntityType: binding.EntityType);

    public static BindingQueryResult OkEntity(string netEntityId, string roomId, string entityType, ulong revision) =>
        new("ok", AuthoritativeRevision: revision, NetEntityId: netEntityId, RoomId: roomId, EntityType: entityType);

    public static BindingQueryResult OkAttribute(
        string netEntityId,
        string roomId,
        string attributeId,
        object value,
        ulong observedRevision,
        ulong observedTick) =>
        new(
            "ok",
            NetEntityId: netEntityId,
            RoomId: roomId,
            AttributeId: attributeId,
            Value: value,
            ObservedRevision: observedRevision,
            ObservedTick: observedTick);

    public static BindingQueryResult OutcomeFailure(string outcome, string? detail = null) =>
        new(outcome, Detail: detail);

    public static BindingQueryResult RequestError(string code, string detail) =>
        new("request_error", code, detail);
}
