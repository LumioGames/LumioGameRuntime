using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumio.GameRuntime.Simulation.Phases;

public readonly record struct PhaseGraphValidationResult(bool Succeeded, string? GeneratedErrorId, string? Detail)
{
    public static PhaseGraphValidationResult Valid() => new(true, null, null);

    public static PhaseGraphValidationResult Invalid(string errorId, string detail) => new(false, errorId, detail);
}

/// <summary>Fixed, plugin-free phase graph. Its order is the generated contract order.</summary>
public sealed class PhaseGraph
{
    private readonly TickPhase[] _phases;

    private PhaseGraph(TickPhase[] phases, PhaseContractTable contracts)
    {
        _phases = phases;
        Contracts = contracts;
    }

    public static PhaseGraph Default { get; } = new(
        Enum.GetValues(typeof(TickPhase)).Cast<TickPhase>().OrderBy(value => (int)value).ToArray(),
        PhaseContractTable.Default);

    public IReadOnlyList<TickPhase> Phases => Array.AsReadOnly((TickPhase[])_phases.Clone());

    public PhaseContractTable Contracts { get; }

    public string TickModel => "FailStop";

    public TickPhase CommitPoint => TickPhase.GasAndEventFinalize;

    public IEnumerable<TickPhase> CommitPoints => _phases.Where(phase => Contracts[phase].IsAuthoritativeCommitPoint);

    public TickPhase GetNext(TickPhase phase)
    {
        var index = Array.IndexOf(_phases, phase);
        if (index < 0 || index + 1 >= _phases.Length) throw new ArgumentOutOfRangeException(nameof(phase));
        return _phases[index + 1];
    }

    public bool TryGetNext(TickPhase phase, out TickPhase next)
    {
        int index = Array.IndexOf(_phases, phase);
        if (index < 0 || index + 1 >= _phases.Length)
        {
            next = default;
            return false;
        }

        next = _phases[index + 1];
        return true;
    }

    public PhaseGraphValidationResult ValidateAgainstGeneratedContract()
    {
        if (_phases.Length != 13) return PhaseGraphValidationResult.Invalid("InternalInvariant", "The tick graph must contain thirteen phases.");
        for (var i = 0; i < _phases.Length; i++)
            if ((int)_phases[i] != i) return PhaseGraphValidationResult.Invalid("InternalInvariant", "The phase graph is not in generated order.");
        if (!Contracts.Validate(out string? detail)) return PhaseGraphValidationResult.Invalid("InternalInvariant", detail ?? "Invalid phase contract.");
        return PhaseGraphValidationResult.Valid();
    }
}
