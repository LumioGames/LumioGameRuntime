using System;

namespace Lumio.GameRuntime.Coordination;

public enum ReservationLeaseState
{
    Active,
    Released,
    Committed,
    Expired
}

/// <summary>A reservation handle whose release operation is idempotent.</summary>
public sealed class ReservationLease : IDisposable
{
    private readonly object _gate = new();
    private readonly Action? _release;
    private ReservationLeaseState _state = ReservationLeaseState.Active;
    private readonly ulong? _deadlineTick;

    public ReservationLease(string leaseId, Action? release = null)
        : this(leaseId, null, release)
    {
    }

    public ReservationLease(string leaseId, ulong? deadlineTick, Action? release = null)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) throw new ArgumentException("A lease ID is required.", nameof(leaseId));
        LeaseId = leaseId;
        _deadlineTick = deadlineTick;
        _release = release;
    }

    public string LeaseId { get; }

    public ReservationLeaseState State
    {
        get { lock (_gate) return _state; }
    }

    public int ReleaseCount { get; private set; }

    public Exception? ReleaseFailure { get; private set; }

    public ulong? DeadlineTick => _deadlineTick;

    public bool ExpireAt(ulong tick)
    {
        if (_deadlineTick is not ulong deadline || tick <= deadline) return false;
        return Expire();
    }

    public bool Release()
    {
        lock (_gate)
        {
            if (_state is ReservationLeaseState.Released or ReservationLeaseState.Expired or ReservationLeaseState.Committed) return false;
            _state = ReservationLeaseState.Released;
            ReleaseCount++;
        }

        try { _release?.Invoke(); }
        catch (Exception ex)
        {
            lock (_gate) ReleaseFailure ??= ex;
        }
        return true;
    }

    public bool Expire()
    {
        lock (_gate)
        {
            if (_state is ReservationLeaseState.Released or ReservationLeaseState.Expired or ReservationLeaseState.Committed) return false;
            _state = ReservationLeaseState.Expired;
            ReleaseCount++;
        }

        try { _release?.Invoke(); }
        catch (Exception ex)
        {
            lock (_gate) ReleaseFailure ??= ex;
        }
        return true;
    }

    internal bool Commit()
    {
        lock (_gate)
        {
            if (_state != ReservationLeaseState.Active) return false;
            _state = ReservationLeaseState.Committed;
            return true;
        }
    }

    public void Dispose() => Release();
}
