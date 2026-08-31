using Lumio.GameRuntime.Simulation.Phases;

namespace Lumio.GameRuntime.Simulation.Errors;

/// <summary>Compatibility projection of a generated simulation failure.</summary>
public sealed record SimulationFailure(
    PhaseFailureClass Class,
    string GeneratedErrorId,
    string Detail)
{
    public static SimulationFailure Rejected(string errorId, string detail) =>
        new(PhaseFailureClass.BusinessReject, errorId, detail);

    public static SimulationFailure Fatal(string errorId, string detail) =>
        new(PhaseFailureClass.ProcessFault, errorId, detail);
}
