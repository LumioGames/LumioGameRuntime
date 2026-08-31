using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;

namespace Lumio.GameRuntime.Simulation.Failure;

/// <summary>Failure namespace facade retained for callers following the module layout.</summary>
public sealed class FailStopController : global::Lumio.GameRuntime.Simulation.FailStopController
{
}

public sealed record PhaseFailureRecord(
    ulong TickId,
    TickPhase Phase,
    string? ProcessorId,
    string GeneratedErrorId,
    string Detail,
    bool CommitPointReached)
{
    public static implicit operator global::Lumio.GameRuntime.Simulation.Tick.PhaseFailureRecord(PhaseFailureRecord value) =>
        new(value.TickId, value.Phase, value.ProcessorId, value.GeneratedErrorId, value.Detail, value.CommitPointReached);
}
