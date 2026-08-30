using System;

namespace Lumio.GameRuntime.Coordination;

public readonly record struct CoordinationBudget(
    int MaxTransactions,
    int MaxReservations,
    int MaxSnapshotPins)
{
    public bool IsValid => MaxTransactions > 0 && MaxReservations > 0 && MaxSnapshotPins > 0;

    public static CoordinationBudget Unlimited { get; } =
        new(int.MaxValue, int.MaxValue, int.MaxValue);
}
