using System;
using Lumio.GameRuntime.Simulation.Session;
using Lumio.GameRuntime.Simulation.Tick;

namespace Lumio.GameRuntime.Simulation;

public sealed class SimulationModule
{
    public SimulationSession CreateSession(SimulationSessionOptions options) => new(options);

    internal SimulationSession CreateSession(SimulationSessionOptions options, TickExecutorComposition composition) =>
        new(options, composition ?? throw new ArgumentNullException(nameof(composition)));

    public static SimulationModule Create() => new();
}
