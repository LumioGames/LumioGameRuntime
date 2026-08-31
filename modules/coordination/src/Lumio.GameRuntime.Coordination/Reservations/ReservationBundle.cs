using System;

namespace Lumio.GameRuntime.Coordination;

internal sealed class ReservationBundle
{
    internal ReservationBundle(ReservationLease game, PreparedVoxelTokenLease voxel)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        Voxel = voxel ?? throw new ArgumentNullException(nameof(voxel));
    }

    internal ReservationLease Game { get; }

    internal PreparedVoxelTokenLease Voxel { get; }

    internal bool IsActive =>
        Game.State == ReservationLeaseState.Active && Voxel.State == ReservationLeaseState.Active;

    internal bool IsCommitted =>
        Game.State == ReservationLeaseState.Committed && Voxel.State == ReservationLeaseState.Committed;

    internal bool IsActiveAt(ulong tick) =>
        IsActive &&
        (Game.DeadlineTick is not ulong gameDeadline || tick <= gameDeadline) &&
        tick <= Voxel.DeadlineTick;

    internal CoordinationFailure? Release()
    {
        Voxel.Release();
        Game.Release();
        return Failure();
    }

    internal CoordinationFailure? Expire()
    {
        Voxel.Expire();
        Game.Expire();
        return Failure();
    }

    internal bool Commit()
    {
        if (IsCommitted) return true;
        if (!IsActive) return false;
        bool voxel = Voxel.Commit();
        bool game = Game.Commit();
        return voxel && game;
    }

    internal static CoordinationFailure? Release(ReservationLease? game, PreparedVoxelTokenLease? voxel)
    {
        voxel?.Release();
        game?.Release();
        if (voxel?.ReleaseFailure is not null || game?.ReleaseFailure is not null)
            return CoordinationFailure.Infrastructure("PanicBoundary", "A participant reservation release callback failed.");
        return null;
    }

    private CoordinationFailure? Failure() =>
        Voxel.ReleaseFailure is not null || Game.ReleaseFailure is not null
            ? CoordinationFailure.Infrastructure("PanicBoundary", "A participant reservation release callback failed.")
            : null;
}
