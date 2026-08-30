using Lumio.GameRuntime.Simulation.Phases;

namespace Lumio.GameRuntime.Simulation;

public sealed class SimulationServices
{
    public SimulationServices()
    {
        PhaseGraph = PhaseGraph.Default;
        PhaseContracts = PhaseContractTable.Default;
    }

    public PhaseGraph PhaseGraph { get; }

    public PhaseContractTable PhaseContracts { get; }
}
