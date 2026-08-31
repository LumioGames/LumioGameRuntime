using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Replication.Mapping;

namespace Lumio.GameRuntime.Replication.Lifecycle;

internal enum ReplicationStoreScopeMode
{
    ContextOwned,
    Standalone,
}

internal enum ReplicationTokenStatus
{
    Current,
    FencingMismatch,
    GenerationMismatch,
}

internal sealed class ReplicationStoreScope
{
    private readonly object _ownerIdentity = new();

    internal ReplicationStoreScope(
        ulong initialGeneration,
        ReplicationStoreScopeMode mode = ReplicationStoreScopeMode.Standalone)
    {
        if (initialGeneration == 0) throw new ArgumentOutOfRangeException(nameof(initialGeneration));
        ConnectionGeneration = initialGeneration;
        WorkEpoch = 1;
        Mode = mode;
    }

    internal object Gate { get; } = new();

    // Context-owned mapping and tombstone views share this dictionary so a
    // fence is observed atomically by every identity operation.
    internal Dictionary<NetEntityId, ulong> Tombstones { get; } = new();

    internal ulong ConnectionGeneration { get; private set; }

    internal ulong WorkEpoch { get; private set; }

    internal IdentityStoreState State { get; private set; } = IdentityStoreState.Active;

    internal ReplicationStoreScopeMode Mode { get; }

    internal IdentityStoreToken CaptureLocked() =>
        State == IdentityStoreState.Active
            ? new IdentityStoreToken(_ownerIdentity, ConnectionGeneration, WorkEpoch)
            : default;

    internal ReplicationTokenStatus ClassifyLocked(IdentityStoreToken token)
    {
        if (State != IdentityStoreState.Active || !token.HasOwner(_ownerIdentity) || token.WorkEpoch != WorkEpoch)
            return ReplicationTokenStatus.FencingMismatch;
        return token.Generation == ConnectionGeneration
            ? ReplicationTokenStatus.Current
            : ReplicationTokenStatus.GenerationMismatch;
    }

    internal bool IsCurrentLocked(IdentityStoreToken token) =>
        ClassifyLocked(token) == ReplicationTokenStatus.Current;

    internal bool TryAdvanceConnectionGenerationLocked(ulong nextGeneration)
    {
        if (State != IdentityStoreState.Active || nextGeneration == 0 || nextGeneration <= ConnectionGeneration)
            return false;
        ConnectionGeneration = nextGeneration;
        WorkEpoch = 1;
        return true;
    }

    internal bool TryAdvanceConnectionGenerationLocked()
    {
        if (ConnectionGeneration == ulong.MaxValue) return false;
        return TryAdvanceConnectionGenerationLocked(ConnectionGeneration + 1);
    }

    internal bool TryAdvanceWorkEpochLocked()
    {
        if (State != IdentityStoreState.Active || WorkEpoch == ulong.MaxValue) return false;
        WorkEpoch++;
        return true;
    }

    internal bool TryTransitionTerminalLocked(bool close)
    {
        if (State == IdentityStoreState.Closed) return false;
        if (State == IdentityStoreState.Invalidated)
        {
            if (!close) return false;
            State = IdentityStoreState.Closed;
            return true;
        }

        State = close ? IdentityStoreState.Closed : IdentityStoreState.Invalidated;
        return true;
    }
}
