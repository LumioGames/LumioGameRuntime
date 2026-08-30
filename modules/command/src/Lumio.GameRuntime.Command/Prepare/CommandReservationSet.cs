using System;

namespace Lumio.GameRuntime.Command;

/// <summary>Atomic, idempotently releasable reservations made during preflight.</summary>
public sealed class CommandReservationSet : IDisposable
{
    private readonly object _gate = new();
    private bool _released;
    private bool _committed;

    public CommandReservationSet(ulong entitySlots, ulong changeEntries, ulong bytes)
    {
        EntitySlots = entitySlots;
        ChangeEntries = changeEntries;
        Bytes = bytes;
    }

    public ulong TokenReferences { get; init; }

    public ulong ComponentCapacity { get; init; }

    public ulong EntitySlots { get; }

    public ulong ChangeEntries { get; }

    public ulong Bytes { get; }

    public bool IsReleased
    {
        get { lock (_gate) return _released; }
    }

    public bool IsCommitted
    {
        get { lock (_gate) return _committed; }
    }

    public int ReleaseCount { get; private set; }

    public void Commit()
    {
        lock (_gate)
        {
            if (_released) return;
            _committed = true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_released || _committed) return;
            _released = true;
            ReleaseCount++;
        }
    }

    public void Dispose() => Release();
}
