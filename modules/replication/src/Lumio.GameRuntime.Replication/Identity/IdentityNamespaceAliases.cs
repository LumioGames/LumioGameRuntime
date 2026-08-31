using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Replication.Identity;

/// <summary>Namespace-compatible view of the generated 128-bit network identity.</summary>
public readonly record struct NetEntityId(string Value)
{
    public bool IsValid => Mapping.NetEntityId.TryParse(Value, out _);

    public static NetEntityId Parse(string value)
    {
        _ = Mapping.NetEntityId.Parse(value);
        return new NetEntityId(value);
    }

    public static bool TryParse(string? value, out NetEntityId id)
    {
        if (Mapping.NetEntityId.TryParse(value, out _))
        {
            id = new NetEntityId(value!);
            return true;
        }

        id = default;
        return false;
    }

    public static implicit operator Mapping.NetEntityId(NetEntityId value) =>
        new(value.Value);

    public static implicit operator NetEntityId(Mapping.NetEntityId value) =>
        new(value.Value);

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct MappingBindingResult(bool Succeeded, string? GeneratedErrorId, string? Detail)
{
    internal static MappingBindingResult From(Mapping.MappingBindingResult result) =>
        new(result.Succeeded, result.GeneratedErrorId, result.Detail);
}

/// <summary>Identity namespace facade over the canonical mapping table.</summary>
public sealed class NetEntityMappingTable
{
    private readonly Mapping.NetEntityMappingTable _inner = new();

    public int Count => _inner.Count;

    public MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId) =>
        MappingBindingResult.From(_inner.Bind((Mapping.NetEntityId)netEntityId, localEntityId));

    public MappingBindingResult Bind(string netEntityId, string localEntityId) =>
        MappingBindingResult.From(_inner.Bind(netEntityId, localEntityId));

    public bool TryResolveLocal(NetEntityId netEntityId, ulong expectedGeneration, out string? localEntityId) =>
        _inner.TryResolveLocal((Mapping.NetEntityId)netEntityId, expectedGeneration, out localEntityId);

    public bool TryResolveNet(string localEntityId, out NetEntityId netEntityId)
    {
        if (_inner.TryResolveNet(localEntityId, out Mapping.NetEntityId value))
        {
            netEntityId = value;
            return true;
        }

        netEntityId = default;
        return false;
    }

    public bool Remove(NetEntityId netEntityId) => _inner.Remove((Mapping.NetEntityId)netEntityId);

    public IReadOnlyDictionary<NetEntityId, string> Snapshot()
    {
        var result = new Dictionary<NetEntityId, string>();
        foreach (KeyValuePair<Mapping.NetEntityId, string> item in _inner.Snapshot())
            result.Add(item.Key, item.Value);
        return result;
    }
}

public readonly record struct ProvisionalRemapResult(bool Succeeded, NetEntityId? AuthoritativeId, string? GeneratedErrorId)
{
    internal static ProvisionalRemapResult From(Mapping.ProvisionalRemapResult result) =>
        new(result.Succeeded, result.AuthoritativeId is Mapping.NetEntityId id ? (NetEntityId)id : null, result.GeneratedErrorId);
}

public sealed class ProvisionalRemapTable
{
    private readonly Mapping.ProvisionalRemapTable _inner = new();

    public ProvisionalRemapResult Add(NetEntityId provisional, NetEntityId authoritative) =>
        ProvisionalRemapResult.From(_inner.Add(provisional, authoritative));

    public bool TryResolve(NetEntityId provisional, out NetEntityId authoritative)
    {
        if (_inner.TryResolve(provisional, out Mapping.NetEntityId value))
        {
            authoritative = value;
            return true;
        }

        authoritative = default;
        return false;
    }

    public IReadOnlyDictionary<NetEntityId, NetEntityId> Snapshot()
    {
        var result = new Dictionary<NetEntityId, NetEntityId>();
        foreach (KeyValuePair<Mapping.NetEntityId, Mapping.NetEntityId> item in _inner.Snapshot())
            result.Add(item.Key, item.Value);
        return result;
    }
}

public readonly record struct TombstoneView(NetEntityId NetEntityId, ulong UntilRevision)
{
    internal Mapping.TombstoneView ToMapping() => new(NetEntityId, UntilRevision);
}

public readonly record struct TombstoneHorizonInputs(
    ulong? UnconfirmedBaseline,
    ulong? DeltaHistory,
    ulong? Reconnect,
    ulong? Prediction,
    ulong? MigrationReplay)
{
    internal Mapping.TombstoneHorizonInputs ToMapping() =>
        new(UnconfirmedBaseline, DeltaHistory, Reconnect, Prediction, MigrationReplay);
}

public readonly record struct TombstoneHorizonResult(bool Known, ulong Horizon)
{
    public bool CanCollect(ulong destroyRevision) => Known && Horizon > destroyRevision;

    public bool CanCollect(ulong destroyRevision, ulong currentRevision) => Known && currentRevision > Horizon && destroyRevision < currentRevision;

    internal static TombstoneHorizonResult From(Mapping.TombstoneHorizonResult result) =>
        new(result.Known, result.Horizon);
}

public static class TombstoneHorizonCalculator
{
    public static TombstoneHorizonResult Calculate(in TombstoneHorizonInputs inputs) =>
        TombstoneHorizonResult.From(Mapping.TombstoneHorizonCalculator.Calculate(inputs.ToMapping()));
}

public sealed class TombstoneRegistry
{
    private readonly Mapping.TombstoneRegistry _inner = new();

    public bool Add(NetEntityId id, ulong untilRevision) => _inner.Add(id, untilRevision);

    public bool Contains(NetEntityId id, ulong revision) => _inner.Contains(id, revision);

    public int Collect(ulong revision, in TombstoneHorizonResult horizon) =>
        _inner.Collect(revision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon));
}
