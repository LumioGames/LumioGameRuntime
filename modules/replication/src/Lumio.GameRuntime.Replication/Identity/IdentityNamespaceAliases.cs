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

    public static implicit operator Mapping.NetEntityId(NetEntityId value) => new(value.Value);

    public static implicit operator NetEntityId(Mapping.NetEntityId value) => new(value.Value);

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct MappingBindingResult(bool Succeeded, string? GeneratedErrorId, string? Detail)
{
    internal static MappingBindingResult From(Mapping.MappingBindingResult result) =>
        new(result.Succeeded, result.GeneratedErrorId, result.Detail);
}

public enum IdentityStoreState
{
    Active = 0,
    Invalidated = 1,
    Closed = 2,
}

/// <summary>Facade token corresponding to a generation-scoped identity store.</summary>
public readonly struct IdentityStoreToken : IEquatable<IdentityStoreToken>
{
    internal Mapping.IdentityStoreToken Inner { get; }

    public IdentityStoreToken(ulong generation)
    {
        Inner = new Mapping.IdentityStoreToken(generation);
    }

    internal IdentityStoreToken(Mapping.IdentityStoreToken inner)
    {
        Inner = inner;
    }

    public ulong Generation => Inner.Generation;

    public ulong WorkEpoch => Inner.WorkEpoch;

    public bool IsValid => Inner.IsValid;

    public bool Equals(IdentityStoreToken other) => Inner == other.Inner;

    public override bool Equals(object? obj) => obj is IdentityStoreToken other && Equals(other);

    public override int GetHashCode() => Inner.GetHashCode();

    public static implicit operator Mapping.IdentityStoreToken(IdentityStoreToken value) => value.Inner;

    public static implicit operator IdentityStoreToken(Mapping.IdentityStoreToken value) => new(value);

    public static bool operator ==(IdentityStoreToken left, IdentityStoreToken right) => left.Equals(right);

    public static bool operator !=(IdentityStoreToken left, IdentityStoreToken right) => !left.Equals(right);
}

/// <summary>Identity namespace facade over the canonical mapping table.</summary>
public sealed class NetEntityMappingTable
{
    private readonly Mapping.NetEntityMappingTable _inner;

    public NetEntityMappingTable() : this(1)
    {
    }

    public NetEntityMappingTable(ulong initialGeneration)
    {
        _inner = new Mapping.NetEntityMappingTable(initialGeneration);
    }

    public int Count => _inner.Count;

    public ulong Generation => _inner.Generation;

    public ulong WorkEpoch => _inner.WorkEpoch;

    public IdentityStoreState State => (IdentityStoreState)_inner.State;

    public bool IsInvalidated => _inner.IsInvalidated;

    public bool IsActive => _inner.IsActive;

    public bool IsClosed => _inner.IsClosed;

    public IdentityStoreToken CaptureToken() => _inner.CaptureToken();

    public IdentityStoreToken GetToken() => _inner.GetToken();

    public IdentityStoreToken CurrentToken => _inner.CurrentToken;

    public bool IsTokenCurrent(IdentityStoreToken token) => _inner.IsTokenCurrent(token.Inner);

    internal MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId) =>
        MappingBindingResult.From(_inner.Bind((Mapping.NetEntityId)netEntityId, localEntityId));

    internal MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId, ulong currentRevision) =>
        MappingBindingResult.From(_inner.Bind((Mapping.NetEntityId)netEntityId, localEntityId, currentRevision));

    public MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId, IdentityStoreToken token) =>
        MappingBindingResult.From(_inner.Bind((Mapping.NetEntityId)netEntityId, localEntityId, token.Inner));

    public MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId, ulong currentRevision, IdentityStoreToken token) =>
        MappingBindingResult.From(_inner.Bind((Mapping.NetEntityId)netEntityId, localEntityId, currentRevision, token.Inner));

    internal MappingBindingResult Bind(string netEntityId, string localEntityId) =>
        MappingBindingResult.From(_inner.Bind(netEntityId, localEntityId));

    internal MappingBindingResult Bind(string netEntityId, string localEntityId, ulong currentRevision) =>
        MappingBindingResult.From(_inner.Bind(netEntityId, localEntityId, currentRevision));

    public MappingBindingResult Bind(string netEntityId, string localEntityId, IdentityStoreToken token) =>
        MappingBindingResult.From(_inner.Bind(netEntityId, localEntityId, token.Inner));

    public MappingBindingResult Bind(string netEntityId, string localEntityId, ulong currentRevision, IdentityStoreToken token) =>
        MappingBindingResult.From(_inner.Bind(netEntityId, localEntityId, currentRevision, token.Inner));

    internal MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity) =>
        MappingBindingResult.From(_inner.Bind(identity));

    internal MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity, ulong currentRevision) =>
        MappingBindingResult.From(_inner.Bind(identity, currentRevision));

    public MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity, IdentityStoreToken token) =>
        MappingBindingResult.From(_inner.Bind(identity, token.Inner));

    public MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity, ulong currentRevision, IdentityStoreToken token) =>
        MappingBindingResult.From(_inner.Bind(identity, currentRevision, token.Inner));

    internal MappingBindingResult TryBind(NetEntityId netEntityId, string localEntityId) => Bind(netEntityId, localEntityId);

    internal MappingBindingResult TryBind(NetEntityId netEntityId, string localEntityId, ulong currentRevision) => Bind(netEntityId, localEntityId, currentRevision);

    internal bool DestroyAndTombstone(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonResult horizon) =>
        Remove(netEntityId, destroyRevision, horizon);

    public bool TryResolveLocal(NetEntityId netEntityId, ulong expectedGeneration, out string? localEntityId) =>
        _inner.TryResolveLocal((Mapping.NetEntityId)netEntityId, expectedGeneration, out localEntityId);

    public bool TryResolveLocal(NetEntityId netEntityId, ulong expectedGeneration, out string? localEntityId, IdentityStoreToken token) =>
        _inner.TryResolveLocal((Mapping.NetEntityId)netEntityId, expectedGeneration, out localEntityId, token.Inner);

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

    public bool TryResolveNet(string localEntityId, out NetEntityId netEntityId, IdentityStoreToken token)
    {
        if (_inner.TryResolveNet(localEntityId, out Mapping.NetEntityId value, token.Inner))
        {
            netEntityId = value;
            return true;
        }

        netEntityId = default;
        return false;
    }

    internal bool Remove(NetEntityId netEntityId) => _inner.Remove((Mapping.NetEntityId)netEntityId);

    internal bool Remove(NetEntityId netEntityId, ulong tombstoneUntilRevision) => _inner.Remove((Mapping.NetEntityId)netEntityId, tombstoneUntilRevision);

    internal bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonResult horizon) =>
        _inner.Remove((Mapping.NetEntityId)netEntityId, destroyRevision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon));

    internal bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonInputs inputs) =>
        _inner.Remove((Mapping.NetEntityId)netEntityId, destroyRevision, inputs.ToMapping());

    internal bool Remove(NetEntityId netEntityId, ulong destroyRevision, ulong tombstoneUntilRevision) =>
        _inner.Remove((Mapping.NetEntityId)netEntityId, destroyRevision, tombstoneUntilRevision);

    public bool Remove(NetEntityId netEntityId, IdentityStoreToken token) => _inner.Remove((Mapping.NetEntityId)netEntityId, token.Inner);

    public bool Remove(NetEntityId netEntityId, ulong tombstoneUntilRevision, IdentityStoreToken token) =>
        _inner.Remove((Mapping.NetEntityId)netEntityId, tombstoneUntilRevision, token.Inner);

    public bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        _inner.Remove((Mapping.NetEntityId)netEntityId, destroyRevision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon), token.Inner);

    public bool Remove(NetEntityId netEntityId, ulong destroyRevision, in TombstoneHorizonInputs inputs, IdentityStoreToken token) =>
        _inner.Remove((Mapping.NetEntityId)netEntityId, destroyRevision, inputs.ToMapping(), token.Inner);

    public bool Remove(NetEntityId netEntityId, ulong destroyRevision, ulong tombstoneUntilRevision, IdentityStoreToken token) =>
        _inner.Remove((Mapping.NetEntityId)netEntityId, destroyRevision, tombstoneUntilRevision, token.Inner);

    public bool IsTombstoned(NetEntityId netEntityId) => _inner.IsTombstoned((Mapping.NetEntityId)netEntityId);

    public bool IsTombstoned(NetEntityId netEntityId, ulong revision) => _inner.IsTombstoned((Mapping.NetEntityId)netEntityId, revision);

    public bool IsTombstoned(NetEntityId netEntityId, IdentityStoreToken token) => _inner.IsTombstoned((Mapping.NetEntityId)netEntityId, token.Inner);

    public bool IsTombstoned(NetEntityId netEntityId, ulong revision, IdentityStoreToken token) =>
        _inner.IsTombstoned((Mapping.NetEntityId)netEntityId, revision, token.Inner);

    internal int CollectTombstones(ulong currentRevision, in TombstoneHorizonResult horizon) =>
        _inner.CollectTombstones(currentRevision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon));

    public int CollectTombstones(ulong currentRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        _inner.CollectTombstones(currentRevision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon), token.Inner);

    internal bool ReleaseTombstone(NetEntityId netEntityId, ulong currentRevision, in TombstoneHorizonResult horizon) =>
        _inner.ReleaseTombstone((Mapping.NetEntityId)netEntityId, currentRevision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon));

    public bool ReleaseTombstone(NetEntityId netEntityId, ulong currentRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        _inner.ReleaseTombstone((Mapping.NetEntityId)netEntityId, currentRevision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon), token.Inner);

    public IReadOnlyDictionary<NetEntityId, ulong> Tombstones
    {
        get
        {
            var result = new Dictionary<NetEntityId, ulong>();
            foreach (KeyValuePair<Mapping.NetEntityId, ulong> item in _inner.Tombstones) result.Add(item.Key, item.Value);
            return result;
        }
    }

    public IReadOnlyDictionary<NetEntityId, ulong> SnapshotTombstones(IdentityStoreToken token)
    {
        var result = new Dictionary<NetEntityId, ulong>();
        foreach (KeyValuePair<Mapping.NetEntityId, ulong> item in _inner.SnapshotTombstones(token.Inner)) result.Add(item.Key, item.Value);
        return result;
    }

    public IReadOnlyDictionary<NetEntityId, string> Snapshot()
    {
        var result = new Dictionary<NetEntityId, string>();
        foreach (KeyValuePair<Mapping.NetEntityId, string> item in _inner.Snapshot()) result.Add(item.Key, item.Value);
        return result;
    }

    public IReadOnlyDictionary<NetEntityId, string> Snapshot(IdentityStoreToken token)
    {
        var result = new Dictionary<NetEntityId, string>();
        foreach (KeyValuePair<Mapping.NetEntityId, string> item in _inner.Snapshot(token.Inner)) result.Add(item.Key, item.Value);
        return result;
    }

    internal bool Reset() => _inner.Reset();

    internal bool Reset(ulong nextGeneration) => _inner.Reset(nextGeneration);

    internal bool ResetForGeneration(ulong nextGeneration) => _inner.ResetForGeneration(nextGeneration);

    public bool ResetForGeneration(ulong nextGeneration, IdentityStoreToken expectedToken) => _inner.ResetForGeneration(nextGeneration, expectedToken.Inner);

    public bool Reset(IdentityStoreToken expectedToken) => _inner.Reset(expectedToken.Inner);

    public bool Reset(IdentityStoreToken expectedToken, ulong nextGeneration) => _inner.Reset(expectedToken.Inner, nextGeneration);

    internal bool Invalidate() => _inner.Invalidate();

    internal bool Invalidate(ulong expectedGeneration) => _inner.Invalidate(expectedGeneration);

    public bool Invalidate(IdentityStoreToken expectedToken) => _inner.Invalidate(expectedToken.Inner);

    internal bool Close() => _inner.Close();

    public bool Close(IdentityStoreToken expectedToken) => _inner.Close(expectedToken.Inner);

    internal bool Clear() => _inner.Clear();

    internal bool InvalidateForGeneration(ulong expectedGeneration) => _inner.InvalidateForGeneration(expectedGeneration);

    public bool InvalidateForGeneration(IdentityStoreToken expectedToken) => _inner.InvalidateForGeneration(expectedToken.Inner);

    internal IdentityStoreToken ResetAndGetToken(ulong nextGeneration) => _inner.ResetAndGetToken(nextGeneration);
}

public readonly record struct ProvisionalRemapResult(bool Succeeded, NetEntityId? AuthoritativeId, string? GeneratedErrorId)
{
    internal static ProvisionalRemapResult From(Mapping.ProvisionalRemapResult result) =>
        new(result.Succeeded, result.AuthoritativeId is Mapping.NetEntityId id ? (NetEntityId)id : null, result.GeneratedErrorId);
}

public sealed class ProvisionalRemapTable
{
    private readonly Mapping.ProvisionalRemapTable _inner;

    public ProvisionalRemapTable() : this(1)
    {
    }

    public ProvisionalRemapTable(ulong initialGeneration)
    {
        _inner = new Mapping.ProvisionalRemapTable(initialGeneration);
    }

    public int Count => _inner.Count;

    public ulong Generation => _inner.Generation;

    public ulong WorkEpoch => _inner.WorkEpoch;

    public IdentityStoreState State => (IdentityStoreState)_inner.State;

    public IdentityStoreToken CaptureToken() => _inner.CaptureToken();

    public IdentityStoreToken GetToken() => _inner.GetToken();

    public IdentityStoreToken CurrentToken => _inner.CurrentToken;

    public bool IsActive => State == IdentityStoreState.Active;

    public bool IsClosed => State == IdentityStoreState.Closed;

    public bool IsTokenCurrent(IdentityStoreToken token) => _inner.IsTokenCurrent(token.Inner);

    internal ProvisionalRemapResult Add(NetEntityId provisional, NetEntityId authoritative) =>
        ProvisionalRemapResult.From(_inner.Add((Mapping.NetEntityId)provisional, (Mapping.NetEntityId)authoritative));

    public ProvisionalRemapResult Add(NetEntityId provisional, NetEntityId authoritative, IdentityStoreToken token) =>
        ProvisionalRemapResult.From(_inner.Add((Mapping.NetEntityId)provisional, (Mapping.NetEntityId)authoritative, token.Inner));

    internal ProvisionalRemapResult Add(Lumio.Gen.ContractTypes.EntityIdentity provisional, Lumio.Gen.ContractTypes.EntityIdentity authoritative) =>
        ProvisionalRemapResult.From(_inner.Add(provisional, authoritative));

    public ProvisionalRemapResult Add(Lumio.Gen.ContractTypes.EntityIdentity provisional, Lumio.Gen.ContractTypes.EntityIdentity authoritative, IdentityStoreToken token) =>
        ProvisionalRemapResult.From(_inner.Add(provisional, authoritative, token.Inner));

    public bool TryResolve(NetEntityId provisional, out NetEntityId authoritative)
    {
        if (_inner.TryResolve((Mapping.NetEntityId)provisional, out Mapping.NetEntityId value))
        {
            authoritative = value;
            return true;
        }

        authoritative = default;
        return false;
    }

    public bool TryResolve(NetEntityId provisional, out NetEntityId authoritative, IdentityStoreToken token)
    {
        if (_inner.TryResolve((Mapping.NetEntityId)provisional, out Mapping.NetEntityId value, token.Inner))
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
        foreach (KeyValuePair<Mapping.NetEntityId, Mapping.NetEntityId> item in _inner.Snapshot()) result.Add(item.Key, item.Value);
        return result;
    }

    public IReadOnlyDictionary<NetEntityId, NetEntityId> Snapshot(IdentityStoreToken token)
    {
        var result = new Dictionary<NetEntityId, NetEntityId>();
        foreach (KeyValuePair<Mapping.NetEntityId, Mapping.NetEntityId> item in _inner.Snapshot(token.Inner)) result.Add(item.Key, item.Value);
        return result;
    }

    internal bool Reset() => _inner.Reset();

    internal bool Reset(ulong nextGeneration) => _inner.Reset(nextGeneration);

    internal bool ResetForGeneration(ulong nextGeneration) => _inner.ResetForGeneration(nextGeneration);

    public bool ResetForGeneration(ulong nextGeneration, IdentityStoreToken expectedToken) => _inner.ResetForGeneration(nextGeneration, expectedToken.Inner);

    public bool Reset(IdentityStoreToken expectedToken) => _inner.Reset(expectedToken.Inner);

    public bool Reset(IdentityStoreToken expectedToken, ulong nextGeneration) => _inner.Reset(expectedToken.Inner, nextGeneration);

    internal bool Invalidate() => _inner.Invalidate();

    internal bool Invalidate(ulong expectedGeneration) => _inner.Invalidate(expectedGeneration);

    public bool Invalidate(IdentityStoreToken expectedToken) => _inner.Invalidate(expectedToken.Inner);

    internal bool Close() => _inner.Close();

    public bool Close(IdentityStoreToken expectedToken) => _inner.Close(expectedToken.Inner);

    internal bool Clear() => _inner.Clear();

    internal bool InvalidateForGeneration(ulong expectedGeneration) => _inner.InvalidateForGeneration(expectedGeneration);

    public bool InvalidateForGeneration(IdentityStoreToken expectedToken) => _inner.InvalidateForGeneration(expectedToken.Inner);

    internal IdentityStoreToken ResetAndGetToken(ulong nextGeneration) => _inner.ResetAndGetToken(nextGeneration);
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

    public bool CanCollect(ulong destroyRevision, ulong currentRevision) =>
        Known && Horizon > destroyRevision && currentRevision > Horizon && destroyRevision < currentRevision;

    public bool IsValidFor(ulong destroyRevision) => Known && Horizon > destroyRevision;

    public bool IsConservative => !Known;

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
    private readonly Mapping.TombstoneRegistry _inner;

    public TombstoneRegistry() : this(1)
    {
    }

    public TombstoneRegistry(ulong initialGeneration)
    {
        _inner = new Mapping.TombstoneRegistry(initialGeneration);
    }

    public int Count => _inner.Count;

    public ulong Generation => _inner.Generation;

    public ulong WorkEpoch => _inner.WorkEpoch;

    public IdentityStoreState State => (IdentityStoreState)_inner.State;

    public IdentityStoreToken CaptureToken() => _inner.CaptureToken();

    public IdentityStoreToken GetToken() => _inner.GetToken();

    public IdentityStoreToken CurrentToken => _inner.CurrentToken;

    public bool IsActive => State == IdentityStoreState.Active;

    public bool IsClosed => State == IdentityStoreState.Closed;

    public bool IsTokenCurrent(IdentityStoreToken token) => _inner.IsTokenCurrent(token.Inner);

    internal bool Add(NetEntityId id, ulong untilRevision) => _inner.Add((Mapping.NetEntityId)id, untilRevision);

    public bool Add(NetEntityId id, ulong untilRevision, IdentityStoreToken token) => _inner.Add((Mapping.NetEntityId)id, untilRevision, token.Inner);

    internal bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonResult horizon) =>
        _inner.Add((Mapping.NetEntityId)id, destroyRevision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon));

    internal bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonInputs inputs) =>
        _inner.Add((Mapping.NetEntityId)id, destroyRevision, inputs.ToMapping());

    public bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        _inner.Add((Mapping.NetEntityId)id, destroyRevision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon), token.Inner);

    public bool Add(NetEntityId id, ulong destroyRevision, in TombstoneHorizonInputs inputs, IdentityStoreToken token) =>
        _inner.Add((Mapping.NetEntityId)id, destroyRevision, inputs.ToMapping(), token.Inner);

    internal bool Add(NetEntityId id, ulong destroyRevision, ulong tombstoneUntilRevision) =>
        _inner.Add((Mapping.NetEntityId)id, destroyRevision, tombstoneUntilRevision);

    public bool Add(NetEntityId id, ulong destroyRevision, ulong tombstoneUntilRevision, IdentityStoreToken token) =>
        _inner.Add((Mapping.NetEntityId)id, destroyRevision, tombstoneUntilRevision, token.Inner);

    public bool Contains(NetEntityId id, ulong revision) => _inner.Contains((Mapping.NetEntityId)id, revision);

    public bool Contains(NetEntityId id, ulong revision, IdentityStoreToken token) => _inner.Contains((Mapping.NetEntityId)id, revision, token.Inner);

    public bool Contains(NetEntityId id) => _inner.Contains((Mapping.NetEntityId)id);

    public bool Contains(NetEntityId id, IdentityStoreToken token) => _inner.Contains((Mapping.NetEntityId)id, token.Inner);

    internal int Collect(ulong revision, in TombstoneHorizonResult horizon) =>
        _inner.Collect(revision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon));

    public int Collect(ulong revision, in TombstoneHorizonResult horizon, IdentityStoreToken token) =>
        _inner.Collect(revision, new Mapping.TombstoneHorizonResult(horizon.Known, horizon.Horizon), token.Inner);

    public bool CanCollect(NetEntityId id, in TombstoneHorizonInputs inputs, ulong currentRevision) =>
        _inner.CanCollect((Mapping.NetEntityId)id, inputs.ToMapping(), currentRevision);

    public bool CanCollect(NetEntityId id, in TombstoneHorizonInputs inputs, ulong currentRevision, IdentityStoreToken token) =>
        _inner.CanCollect((Mapping.NetEntityId)id, inputs.ToMapping(), currentRevision, token.Inner);

    internal bool Remove(NetEntityId id) => _inner.Remove((Mapping.NetEntityId)id);

    public bool Remove(NetEntityId id, IdentityStoreToken token) => _inner.Remove((Mapping.NetEntityId)id, token.Inner);

    public IReadOnlyDictionary<NetEntityId, ulong> Snapshot()
    {
        var result = new Dictionary<NetEntityId, ulong>();
        foreach (KeyValuePair<Mapping.NetEntityId, ulong> item in _inner.Snapshot()) result.Add(item.Key, item.Value);
        return result;
    }

    public IReadOnlyDictionary<NetEntityId, ulong> Snapshot(IdentityStoreToken token)
    {
        var result = new Dictionary<NetEntityId, ulong>();
        foreach (KeyValuePair<Mapping.NetEntityId, ulong> item in _inner.Snapshot(token.Inner)) result.Add(item.Key, item.Value);
        return result;
    }

    internal bool Reset() => _inner.Reset();

    internal bool Reset(ulong nextGeneration) => _inner.Reset(nextGeneration);

    internal bool ResetForGeneration(ulong nextGeneration) => _inner.ResetForGeneration(nextGeneration);

    public bool Reset(IdentityStoreToken expectedToken) => _inner.Reset(expectedToken.Inner);

    public bool Reset(IdentityStoreToken expectedToken, ulong nextGeneration) => _inner.Reset(expectedToken.Inner, nextGeneration);

    internal bool Invalidate() => _inner.Invalidate();

    internal bool Invalidate(ulong expectedGeneration) => _inner.Invalidate(expectedGeneration);

    public bool Invalidate(IdentityStoreToken expectedToken) => _inner.Invalidate(expectedToken.Inner);

    internal bool Close() => _inner.Close();

    public bool Close(IdentityStoreToken expectedToken) => _inner.Close(expectedToken.Inner);

    internal bool Clear() => _inner.Clear();

    internal bool InvalidateForGeneration(ulong expectedGeneration) => _inner.InvalidateForGeneration(expectedGeneration);

    public bool InvalidateForGeneration(IdentityStoreToken expectedToken) => _inner.InvalidateForGeneration(expectedToken.Inner);

    internal IdentityStoreToken ResetAndGetToken(ulong nextGeneration) => _inner.ResetAndGetToken(nextGeneration);
}
