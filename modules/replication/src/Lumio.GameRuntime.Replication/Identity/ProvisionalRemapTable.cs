using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Replication.Lifecycle;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct ProvisionalRemapResult(bool Succeeded, NetEntityId? AuthoritativeId, string? GeneratedErrorId)
{
    public static ProvisionalRemapResult Accepted(NetEntityId id) => new(true, id, null);

    public static ProvisionalRemapResult Rejected(string errorId) => new(false, null, errorId);

    internal static ProvisionalRemapResult Stale(string errorId) => Rejected(errorId);
}

public sealed class ProvisionalRemapTable
{
    private readonly ReplicationStoreScope _scope;
    private readonly Dictionary<NetEntityId, NetEntityId> _remaps = new();
    private readonly Dictionary<NetEntityId, NetEntityId> _byAuthoritative = new();

    public ProvisionalRemapTable() : this(1)
    {
    }

    public ProvisionalRemapTable(ulong initialGeneration)
        : this(new ReplicationStoreScope(initialGeneration))
    {
    }

    internal ProvisionalRemapTable(ReplicationStoreScope scope)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    public int Count
    {
        get { lock (_scope.Gate) return _scope.State == IdentityStoreState.Active ? _remaps.Count : 0; }
    }

    public ulong Generation
    {
        get { lock (_scope.Gate) return _scope.ConnectionGeneration; }
    }

    public ulong WorkEpoch
    {
        get { lock (_scope.Gate) return _scope.WorkEpoch; }
    }

    public IdentityStoreState State
    {
        get { lock (_scope.Gate) return _scope.State; }
    }

    public IdentityStoreToken CaptureToken()
    {
        lock (_scope.Gate) return _scope.CaptureLocked();
    }

    public IdentityStoreToken GetToken() => CaptureToken();

    public IdentityStoreToken CurrentToken => CaptureToken();

    public bool IsActive => State == IdentityStoreState.Active;

    public bool IsClosed => State == IdentityStoreState.Closed;

    public bool IsTokenCurrent(IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token);
    }

    internal ProvisionalRemapResult Add(NetEntityId provisional, NetEntityId authoritative) =>
        AddCore(provisional, authoritative, default, false);

    public ProvisionalRemapResult Add(NetEntityId provisional, NetEntityId authoritative, IdentityStoreToken token) =>
        AddCore(provisional, authoritative, token, true);

    internal ProvisionalRemapResult Add(Lumio.Gen.ContractTypes.EntityIdentity provisional, Lumio.Gen.ContractTypes.EntityIdentity authoritative) =>
        AddGenerated(provisional, authoritative, default, false);

    public ProvisionalRemapResult Add(
        Lumio.Gen.ContractTypes.EntityIdentity provisional,
        Lumio.Gen.ContractTypes.EntityIdentity authoritative,
        IdentityStoreToken token) =>
        AddGenerated(provisional, authoritative, token, true);

    public bool TryResolve(NetEntityId provisional, out NetEntityId authoritative)
    {
        lock (_scope.Gate)
        {
            if (_scope.State == IdentityStoreState.Active && _remaps.TryGetValue(provisional, out authoritative)) return true;
            authoritative = default;
            return false;
        }
    }

    public bool TryResolve(NetEntityId provisional, out NetEntityId authoritative, IdentityStoreToken token)
    {
        lock (_scope.Gate)
        {
            if (_scope.IsCurrentLocked(token) && _remaps.TryGetValue(provisional, out authoritative)) return true;
            authoritative = default;
            return false;
        }
    }

    public IReadOnlyDictionary<NetEntityId, NetEntityId> Snapshot()
    {
        lock (_scope.Gate) return _scope.State == IdentityStoreState.Active ? new Dictionary<NetEntityId, NetEntityId>(_remaps) : new Dictionary<NetEntityId, NetEntityId>();
    }

    public IReadOnlyDictionary<NetEntityId, NetEntityId> Snapshot(IdentityStoreToken token)
    {
        lock (_scope.Gate) return _scope.IsCurrentLocked(token) ? new Dictionary<NetEntityId, NetEntityId>(_remaps) : new Dictionary<NetEntityId, NetEntityId>();
    }

    public bool TrySnapshot(IdentityStoreToken token, out IReadOnlyDictionary<NetEntityId, NetEntityId> snapshot)
    {
        lock (_scope.Gate)
        {
            if (!_scope.IsCurrentLocked(token))
            {
                snapshot = new Dictionary<NetEntityId, NetEntityId>();
                return false;
            }

            snapshot = new Dictionary<NetEntityId, NetEntityId>(_remaps);
            return true;
        }
    }

    internal bool Reset(ulong nextGeneration)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryAdvanceConnectionGenerationLocked(nextGeneration)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Reset()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryAdvanceConnectionGenerationLocked()) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool ResetForGeneration(ulong nextGeneration) => Reset(nextGeneration);

    public bool ResetForGeneration(ulong nextGeneration, IdentityStoreToken expectedToken) => Reset(expectedToken, nextGeneration);

    public bool Reset(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryAdvanceConnectionGenerationLocked()) return false;
            ClearLocked();
            return true;
        }
    }

    public bool Reset(IdentityStoreToken expectedToken, ulong nextGeneration)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryAdvanceConnectionGenerationLocked(nextGeneration)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Invalidate()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryTransitionTerminalLocked(false)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Invalidate(ulong expectedGeneration)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || expectedGeneration != _scope.ConnectionGeneration || !_scope.TryTransitionTerminalLocked(false)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool Invalidate(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryTransitionTerminalLocked(false)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Close()
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.TryTransitionTerminalLocked(true)) return false;
            ClearLocked();
            return true;
        }
    }

    public bool Close(IdentityStoreToken expectedToken)
    {
        lock (_scope.Gate)
        {
            if (_scope.Mode != ReplicationStoreScopeMode.Standalone || !_scope.IsCurrentLocked(expectedToken) || !_scope.TryTransitionTerminalLocked(true)) return false;
            ClearLocked();
            return true;
        }
    }

    internal bool Clear() => Reset();

    internal bool InvalidateForGeneration(ulong expectedGeneration) => Invalidate(expectedGeneration);

    public bool InvalidateForGeneration(IdentityStoreToken expectedToken) => Invalidate(expectedToken);

    internal IdentityStoreToken ResetAndGetToken(ulong nextGeneration) =>
        Reset(nextGeneration) ? CaptureToken() : default;

    private ProvisionalRemapResult AddCore(NetEntityId provisional, NetEntityId authoritative, IdentityStoreToken token, bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active) return StaleToken(token, tokenRequired);
            if (!provisional.IsValid || !authoritative.IsValid || provisional == authoritative)
                return ProvisionalRemapResult.Rejected("InvalidArgument");
            if (_remaps.TryGetValue(provisional, out NetEntityId existing))
                return existing == authoritative ? ProvisionalRemapResult.Accepted(existing) : ProvisionalRemapResult.Rejected("RevisionConflict");
            if (_byAuthoritative.ContainsKey(authoritative))
                return ProvisionalRemapResult.Rejected("RevisionConflict");
            _remaps.Add(provisional, authoritative);
            _byAuthoritative.Add(authoritative, provisional);
            return ProvisionalRemapResult.Accepted(authoritative);
        }
    }

    private ProvisionalRemapResult AddGenerated(
        Lumio.Gen.ContractTypes.EntityIdentity? provisional,
        Lumio.Gen.ContractTypes.EntityIdentity? authoritative,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            if (tokenRequired ? !_scope.IsCurrentLocked(token) : _scope.State != IdentityStoreState.Active) return StaleToken(token, tokenRequired);
            Identity.EntityIdentityValidationResult provisionalValidation = Identity.EntityIdentityValidator.Validate(provisional!);
            Identity.EntityIdentityValidationResult authoritativeValidation = Identity.EntityIdentityValidator.Validate(authoritative!);
            if (!provisionalValidation.Succeeded || !authoritativeValidation.Succeeded)
                return ProvisionalRemapResult.Rejected("ManifestMalformed");
            if (provisional is null || authoritative is null ||
                provisional.Namespace != Lumio.Gen.ContractTypes.EntityIdentityNamespace.Provisional ||
                authoritative.Namespace != Lumio.Gen.ContractTypes.EntityIdentityNamespace.Authoritative ||
                provisional.Lifecycle != Lumio.Gen.ContractTypes.EntityIdentityLifecycle.Alive ||
                authoritative.Lifecycle != Lumio.Gen.ContractTypes.EntityIdentityLifecycle.Alive)
                return ProvisionalRemapResult.Rejected("ManifestMalformed");
            if (!MatchesLocalGeneration(provisional) || !MatchesLocalGeneration(authoritative))
                return ProvisionalRemapResult.Rejected("InvalidArgument");
            if (!NetEntityId.TryParse(provisional.NetEntityId, out NetEntityId provisionalId) ||
                !NetEntityId.TryParse(authoritative.NetEntityId, out NetEntityId authoritativeId))
                return ProvisionalRemapResult.Rejected("ManifestMalformed");
            return AddCoreLocked(provisionalId, authoritativeId);
        }
    }

    private ProvisionalRemapResult AddCoreLocked(NetEntityId provisional, NetEntityId authoritative)
    {
        if (provisional == authoritative) return ProvisionalRemapResult.Rejected("InvalidArgument");
        if (_remaps.TryGetValue(provisional, out NetEntityId existing))
            return existing == authoritative ? ProvisionalRemapResult.Accepted(existing) : ProvisionalRemapResult.Rejected("RevisionConflict");
        if (_byAuthoritative.ContainsKey(authoritative))
            return ProvisionalRemapResult.Rejected("RevisionConflict");
        _remaps.Add(provisional, authoritative);
        _byAuthoritative.Add(authoritative, provisional);
        return ProvisionalRemapResult.Accepted(authoritative);
    }

    private static bool MatchesLocalGeneration(Lumio.Gen.ContractTypes.EntityIdentity identity)
    {
        if (identity.LocalEntityId is null) return true;
        int separator = identity.LocalEntityId.IndexOf(':');
        if (separator <= 0 || separator == identity.LocalEntityId.Length - 1 || identity.LocalEntityId.IndexOf(':', separator + 1) >= 0)
            return false;
        return ulong.TryParse(identity.LocalEntityId.Substring(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong generation) &&
            generation == identity.Generation;
    }

    internal void ClearContextLocked() => ClearLocked();

    private void ClearLocked()
    {
        _remaps.Clear();
        _byAuthoritative.Clear();
    }

    private ProvisionalRemapResult StaleToken(IdentityStoreToken token, bool tokenRequired)
    {
        if (!tokenRequired) return ProvisionalRemapResult.Stale("StaleConnectionGeneration");
        return _scope.ClassifyLocked(token) == ReplicationTokenStatus.GenerationMismatch
            ? ProvisionalRemapResult.Stale("StaleConnectionGeneration")
            : ProvisionalRemapResult.Stale("FencingTokenStale");
    }
}

public sealed class ProvisionalRemapView
{
    private readonly ProvisionalRemapTable _store;

    internal ProvisionalRemapView(ProvisionalRemapTable store) => _store = store;

    public int Count => _store.Count;

    public ulong Generation => _store.Generation;

    public ulong WorkEpoch => _store.WorkEpoch;

    public IdentityStoreState State => _store.State;

    public bool IsActive => _store.IsActive;

    public bool IsClosed => _store.IsClosed;

    public bool TryResolve(NetEntityId provisional, out NetEntityId authoritative) =>
        _store.TryResolve(provisional, out authoritative);

    public IReadOnlyDictionary<NetEntityId, NetEntityId> Snapshot() => _store.Snapshot();
}
