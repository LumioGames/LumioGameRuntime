using System;
using Lumio.GameRuntime.Simulation.Session;

namespace Lumio.GameRuntime.Simulation;

public sealed class SimulationModule
{
    public SimulationSession CreateSession(SimulationSessionOptions options) => new(options);

    public static SimulationModule Create() => new();
}
