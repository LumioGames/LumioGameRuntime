using System;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct TombstoneHorizonInputs(
    ulong? UnconfirmedBaseline,
    ulong? DeltaHistory,
    ulong? Reconnect,
    ulong? Prediction,
    ulong? MigrationReplay)
{
    public bool IsKnown => UnconfirmedBaseline.HasValue && DeltaHistory.HasValue && Reconnect.HasValue && Prediction.HasValue && MigrationReplay.HasValue;
}

public readonly record struct TombstoneHorizonResult(bool Known, ulong Horizon)
{
    /// <summary>All retention pins have advanced beyond the tombstone revision.</summary>
    public bool CanCollect(ulong destroyRevision) => Known && Horizon > destroyRevision;

    public bool CanCollect(ulong destroyRevision, ulong currentRevision) =>
        Known && Horizon > destroyRevision && currentRevision > Horizon && destroyRevision < currentRevision;

    /// <summary>Returns false for unknown or understated horizons.</summary>
    public bool IsValidFor(ulong destroyRevision) => Known && Horizon > destroyRevision;

    public bool IsConservative => !Known;
}

public static class TombstoneHorizonCalculator
{
    public static TombstoneHorizonResult Calculate(in TombstoneHorizonInputs inputs)
    {
        if (!inputs.IsKnown) return new TombstoneHorizonResult(false, 0);
        ulong max = Math.Max(inputs.UnconfirmedBaseline!.Value, inputs.DeltaHistory!.Value);
        max = Math.Max(max, inputs.Reconnect!.Value);
        max = Math.Max(max, inputs.Prediction!.Value);
        max = Math.Max(max, inputs.MigrationReplay!.Value);
        return new TombstoneHorizonResult(true, max);
    }
}
