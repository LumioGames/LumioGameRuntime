using System;

namespace Lumio.GameRuntime.Coordination;

public sealed class PreparedVoxelTokenLease : IDisposable
{
    private readonly ReservationLease _lease;

    public PreparedVoxelTokenLease(string token, ulong deadlineTick, Action? release = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A voxel token is required.", nameof(token));
        Token = token;
        DeadlineTick = deadlineTick;
        _lease = new ReservationLease(token, deadlineTick, release);
    }

    public string Token { get; }

    public ulong DeadlineTick { get; }

    public ReservationLeaseState State => _lease.State;

    public int ReleaseCount => _lease.ReleaseCount;

    public bool Release() => _lease.Release();

    public bool Expire() => _lease.Expire();

    public bool ExpireAt(ulong tick) => _lease.ExpireAt(tick);

    public bool Commit() => _lease.Commit();

    public void Dispose() => _lease.Dispose();
}
