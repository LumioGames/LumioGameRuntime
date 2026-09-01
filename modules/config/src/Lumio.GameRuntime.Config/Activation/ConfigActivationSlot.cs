using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Config;

/// <summary>Version pointer for one snapshot identity in the activation slot.</summary>
public enum ConfigVersionState
{
    /// <summary>Unknown identity.</summary>
    None = 0,

    /// <summary>Waiting at the staged pointer; not visible to new tick leases.</summary>
    Staged = 1,

    /// <summary>Current Active pointer.</summary>
    Active = 2,

    /// <summary>Replaced at a barrier; existing leases may still read it.</summary>
    Superseded = 3,
}

/// <summary>Outcome of staging one immutable snapshot.</summary>
/// <param name="Staged">True when the candidate occupies the staged slot.</param>
/// <param name="SnapshotId">Candidate identity.</param>
/// <param name="ErrorId">Generated catalog id when rejected; otherwise null.</param>
public readonly record struct ConfigStageResult(
    bool Staged,
    ConfigSnapshotId SnapshotId,
    string? ErrorId);

/// <summary>Outcome of a Tick Barrier activation.</summary>
/// <param name="Activated">True when the staged snapshot became Active.</param>
/// <param name="ActiveSnapshotId">Active identity after the call.</param>
/// <param name="ErrorId">Generated catalog id when rejected; otherwise null.</param>
public readonly record struct ConfigActivationResult(
    bool Activated,
    ConfigSnapshotId ActiveSnapshotId,
    string? ErrorId);

/// <summary>
/// Single staged slot plus Active pointer. Staging never switches the Active
/// pointer; only <see cref="ActivateAtBarrier"/> on the owner thread does.
/// </summary>
public sealed class ConfigActivationSlot
{
    private readonly object _gate = new();
    private readonly int _ownerThreadId;
    private readonly HashSet<ulong> _superseded = new();
    private ConfigSnapshot? _active;
    private ConfigSnapshot? _staged;

    /// <summary>Bind the constructing thread as the Simulation Owner Thread.</summary>
    public ConfigActivationSlot()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>Current Active snapshot. Throws when none has been activated.</summary>
    public IConfigSnapshotView Active
    {
        get
        {
            lock (_gate)
            {
                return _active ?? throw new InvalidOperationException("No active ConfigSnapshot.");
            }
        }
    }

    /// <summary>
    /// Place a validated snapshot in the staged slot. Same id+hash is idempotent.
    /// Never overwrites Active.
    /// </summary>
    public ConfigStageResult Stage(ConfigSnapshot snapshot)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(snapshot);
#else
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
#endif

        lock (_gate)
        {
            if (IsSameSnapshot(_staged, snapshot))
            {
                return new ConfigStageResult(true, snapshot.SnapshotId, null);
            }

            _staged = snapshot;
            return new ConfigStageResult(true, snapshot.SnapshotId, null);
        }
    }

    /// <summary>
    /// Owner-thread Tick Barrier switch. No staged candidate returns Rejected
    /// (<c>InvalidArgument</c>) and leaves Active unchanged. Non-owner returns
    /// <c>WrongContext</c>.
    /// </summary>
    public ConfigActivationResult ActivateAtBarrier(TickId tickId)
    {
        _ = tickId;
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            lock (_gate)
            {
                return new ConfigActivationResult(false, _active?.SnapshotId ?? default, "WrongContext");
            }
        }

        lock (_gate)
        {
            if (_staged is null)
            {
                return new ConfigActivationResult(false, _active?.SnapshotId ?? default, "InvalidArgument");
            }

            if (IsSameSnapshot(_active, _staged))
            {
                ConfigSnapshotId activeId = _active!.SnapshotId;
                _staged = null;
                return new ConfigActivationResult(true, activeId, null);
            }

            if (_active is not null)
            {
                _superseded.Add(_active.SnapshotId.Value);
            }

            _active = _staged;
            _staged = null;
            _superseded.Remove(_active.SnapshotId.Value);
            return new ConfigActivationResult(true, _active.SnapshotId, null);
        }
    }

    /// <summary>Pin the current Active snapshot to a lease for this tick.</summary>
    public ConfigSnapshotLease AcquireForTick(TickId tickId)
    {
        lock (_gate)
        {
            if (_active is null)
            {
                throw new InvalidOperationException("No active ConfigSnapshot.");
            }

            return new ConfigSnapshotLease(_active, tickId);
        }
    }

    /// <summary>Staged/Active/Superseded pointer for one identity.</summary>
    public ConfigVersionState GetVersionState(ConfigSnapshotId snapshotId)
    {
        lock (_gate)
        {
            if (_active is not null && _active.SnapshotId == snapshotId)
            {
                return ConfigVersionState.Active;
            }

            if (_staged is not null && _staged.SnapshotId == snapshotId)
            {
                return ConfigVersionState.Staged;
            }

            if (_superseded.Contains(snapshotId.Value))
            {
                return ConfigVersionState.Superseded;
            }

            return ConfigVersionState.None;
        }
    }

    private static bool IsSameSnapshot(ConfigSnapshot? left, ConfigSnapshot right) =>
        left is not null &&
        left.SnapshotId == right.SnapshotId &&
        string.Equals(left.OutputHashHex, right.OutputHashHex, StringComparison.Ordinal);
}
