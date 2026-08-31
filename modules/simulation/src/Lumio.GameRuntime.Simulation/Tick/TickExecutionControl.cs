using System;
using System.Threading;

namespace Lumio.GameRuntime.Simulation.Tick;

public readonly record struct TickExecutionControl(
    ulong DeadlineTickId,
    TimeSpan Timeout,
    ulong MaxWorkUnits,
    ulong MaxCommands,
    bool CooperativeChecksRequired,
    CancellationToken CancellationToken)
{
    public static TickExecutionControl ForTick(ulong tickId) => new(
        tickId,
        TimeSpan.FromSeconds(30),
        1_000_000,
        1_000_000,
        true,
        CancellationToken.None);
}
