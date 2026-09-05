using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Replication.Binding;

public readonly record struct ConnectionBinding(
    string AccountId,
    string RoomId,
    string NetEntityId,
    string EntityType,
    ulong ConnectionGeneration);

public enum RebindMode
{
    Reconnect = 0,
    Takeover = 1,
}

public sealed class AdmitRequest
{
    public string? Connection { get; init; }
    public string? AccountId { get; init; }
    public string? RoomId { get; init; }
    public string? EntityType { get; init; }
    public string? NetEntityId { get; init; }
    public string? SessionId { get; init; }
    public object? AccountEntityRef { get; init; }
    public object? StorageHandle { get; init; }
    public object? HostPointer { get; init; }
    public object? HostHandle { get; init; }
}

public sealed class BindingRecordRequest
{
    public string? AccountId { get; init; }
    public string? RoomId { get; init; }
    public string? NetEntityId { get; init; }
    public string? EntityType { get; init; }
    public ulong? ConnectionGeneration { get; init; }
    public string? SessionId { get; init; }
    public object? AccountEntityRef { get; init; }
    public object? StorageHandle { get; init; }
    public object? HostPointer { get; init; }
    public object? HostHandle { get; init; }
    public string? MintedBy { get; init; }
}

public sealed class AttributeQueryRequest
{
    public string? CallerScope { get; init; }
    public string? RoomId { get; init; }
    public string? NetEntityId { get; init; }
    public string? AttributeId { get; init; }
    public ulong? ConnectionGeneration { get; init; }
    public string? Origin { get; init; }
    public string? SessionId { get; init; }
    public object? AccountEntityRef { get; init; }
    public object? StorageHandle { get; init; }
    public object? HostPointer { get; init; }
    public object? HostHandle { get; init; }
}

public readonly record struct IssuedNetEntity(
    string NetEntityId,
    string RoomId,
    string EntityType,
    bool Tombstoned);

internal sealed class IdentityTableSnapshot
{
    internal IdentityTableSnapshot(IReadOnlyList<IssuedNetEntity> records)
    {
        Records = records ?? Array.Empty<IssuedNetEntity>();
    }

    public IReadOnlyList<IssuedNetEntity> Records { get; }
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
    ulong? ObservedTick = null,
    ConnectionBinding[]? Bindings = null)
{
    public static BindingQueryResult OkBinding(ConnectionBinding binding, ulong? revision = null) =>
        new("ok", Binding: binding, AuthoritativeRevision: revision, NetEntityId: binding.NetEntityId, RoomId: binding.RoomId, EntityType: binding.EntityType);

    public static BindingQueryResult OkBindings(string roomId, ConnectionBinding[] bindings) =>
        new("ok", RoomId: roomId, Bindings: bindings);

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

/// <summary>Internal result for an owner-thread expiry request.</summary>
public sealed class ExpireEntityResult : WorldMessage
{
    public ExpireEntityResult(string requestId, string outcome, string? code = null, string? detail = null)
    {
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        ValidateOutcome(outcome, code, detail);
        Code = code;
        Detail = detail;
    }

    public string RequestId { get; }
    public string Outcome { get; }
    public string? Code { get; }
    public string? Detail { get; }

    private static void ValidateOutcome(string outcome, string? code, string? detail)
    {
        if (outcome == "request_error")
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(detail)) throw new ArgumentException("request_error requires code and detail.", nameof(outcome));
            return;
        }
        if (outcome is not ("accepted" or "tombstoned" or "non_existent") || code is not null || detail is not null)
            throw new ArgumentException("Invalid expiry result outcome shape.", nameof(outcome));
    }
}

/// <summary>Internal result for an owner-thread binding resolution request.</summary>
public sealed class ResolveBindingResult : WorldMessage
{
    public ResolveBindingResult(
        string requestId,
        string outcome,
        ConnectionBinding? binding = null,
        ulong? observedRevision = null,
        string? code = null,
        string? detail = null)
    {
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        ValidateOutcome(outcome, binding, observedRevision, code, detail);
        Binding = binding;
        ObservedRevision = observedRevision;
        Code = code;
        Detail = detail;
    }

    public string RequestId { get; }
    public string Outcome { get; }
    public ConnectionBinding? Binding { get; }
    public ulong? ObservedRevision { get; }
    public string? Code { get; }
    public string? Detail { get; }

    private static void ValidateOutcome(string outcome, ConnectionBinding? binding, ulong? observedRevision, string? code, string? detail)
    {
        if (outcome == "ok")
        {
            if (!binding.HasValue || !observedRevision.HasValue || code is not null || detail is not null) throw new ArgumentException("ok resolve result requires binding and observedRevision.", nameof(outcome));
            return;
        }
        if (outcome == "request_error")
        {
            if (binding.HasValue || observedRevision.HasValue || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(detail)) throw new ArgumentException("request_error resolve result requires code and detail only.", nameof(outcome));
            return;
        }
        if (outcome is not ("non_existent" or "stale_generation" or "invisible" or "unauthorized" or "tombstoned") || binding.HasValue || observedRevision.HasValue || code is not null || detail is not null)
            throw new ArgumentException("Invalid resolve result outcome shape.", nameof(outcome));
    }
}

/// <summary>Internal result for an owner-thread attribute query request.</summary>
public sealed class AttributeQueryResult : WorldMessage
{
    public AttributeQueryResult(
        string requestId,
        string outcome,
        string? netEntityId = null,
        string? roomId = null,
        string? attributeId = null,
        object? value = null,
        ulong? observedRevision = null,
        ulong? observedTick = null,
        string? code = null,
        string? detail = null)
    {
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        ValidateOutcome(outcome, netEntityId, roomId, attributeId, value, observedRevision, observedTick, code, detail);
        NetEntityId = netEntityId;
        RoomId = roomId;
        AttributeId = attributeId;
        Value = value;
        ObservedRevision = observedRevision;
        ObservedTick = observedTick;
        Code = code;
        Detail = detail;
    }

    public string RequestId { get; }
    public string Outcome { get; }
    public string? NetEntityId { get; }
    public string? RoomId { get; }
    public string? AttributeId { get; }
    public object? Value { get; }
    public ulong? ObservedRevision { get; }
    public ulong? ObservedTick { get; }
    public string? Code { get; }
    public string? Detail { get; }

    private static void ValidateOutcome(
        string outcome,
        string? netEntityId,
        string? roomId,
        string? attributeId,
        object? value,
        ulong? observedRevision,
        ulong? observedTick,
        string? code,
        string? detail)
    {
        if (outcome == "ok")
        {
            if (string.IsNullOrEmpty(netEntityId) || string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(attributeId) || !observedRevision.HasValue || !observedTick.HasValue || code is not null || detail is not null)
                throw new ArgumentException("ok attribute result requires identity, value, observedRevision, and observedTick.", nameof(outcome));
            _ = value;
            return;
        }
        if (outcome == "request_error")
        {
            if (netEntityId is not null || roomId is not null || attributeId is not null || value is not null || observedRevision.HasValue || observedTick.HasValue || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(detail))
                throw new ArgumentException("request_error attribute result requires code and detail only.", nameof(outcome));
            return;
        }
        if (outcome is not ("non_existent" or "stale_generation" or "invisible" or "unauthorized" or "tombstoned") || netEntityId is not null || roomId is not null || attributeId is not null || value is not null || observedRevision.HasValue || observedTick.HasValue || code is not null || detail is not null)
            throw new ArgumentException("Invalid attribute result outcome shape.", nameof(outcome));
    }
}
