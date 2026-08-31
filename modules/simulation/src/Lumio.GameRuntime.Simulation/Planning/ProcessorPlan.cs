using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Simulation.Planning;

public sealed class ProcessorPlan
{
    private readonly IReadOnlyList<ProcessorDescriptor> _orderedDescriptors;
    private readonly IReadOnlyList<ProcessorInvocation> _invocations;
    private readonly IReadOnlyDictionary<TickPhase, IReadOnlyList<ProcessorInvocation>> _byPhase;

    internal ProcessorPlan(
        IList<ProcessorDescriptor> orderedDescriptors,
        IDictionary<TickPhase, IReadOnlyList<ProcessorInvocation>> byPhase,
        string canonicalHashHex)
    {
        var descriptorCopies = new List<ProcessorDescriptor>(orderedDescriptors.Count);
        foreach (ProcessorDescriptor descriptor in orderedDescriptors) descriptorCopies.Add(CloneDescriptor(descriptor));
        _orderedDescriptors = new ReadOnlyCollection<ProcessorDescriptor>(descriptorCopies);
        var invocations = new List<ProcessorInvocation>(orderedDescriptors.Count);
        foreach (ProcessorDescriptor descriptor in descriptorCopies) invocations.Add(new ProcessorInvocation(descriptor));
        _invocations = new ReadOnlyCollection<ProcessorInvocation>(invocations);
        var phaseCopies = new Dictionary<TickPhase, IReadOnlyList<ProcessorInvocation>>();
        foreach (IGrouping<TickPhase, ProcessorInvocation> group in invocations.GroupBy(value => value.Phase))
            phaseCopies[group.Key] = new ReadOnlyCollection<ProcessorInvocation>(group.ToArray());
        _byPhase = new ReadOnlyDictionary<TickPhase, IReadOnlyList<ProcessorInvocation>>(phaseCopies);
        CanonicalHashHex = canonicalHashHex;
    }

    public IReadOnlyList<ProcessorDescriptor> OrderedDescriptors => _orderedDescriptors;

    public IReadOnlyList<ProcessorInvocation> Invocations => _invocations;

    public string CanonicalHashHex { get; }

    public IReadOnlyList<ProcessorInvocation> GetPhasePlan(TickPhase phase) =>
        _byPhase.TryGetValue(phase, out IReadOnlyList<ProcessorInvocation>? value)
            ? value
            : new ProcessorInvocation[0];

    public IReadOnlyList<ProcessorInvocation> GetForPhase(TickPhase phase) => GetPhasePlan(phase);

    private static ProcessorDescriptor CloneDescriptor(ProcessorDescriptor descriptor) =>
        new(
            descriptor.ProcessorId,
            descriptor.Role,
            descriptor.Phase,
            descriptor.Query,
            new ReadOnlyCollection<string>(descriptor.ReadSet.ToArray()),
            new ReadOnlyCollection<string>(descriptor.WriteSet.ToArray()),
            descriptor.MayEmitStructuralCommands,
            descriptor.Before is null ? null : new ReadOnlyCollection<string>(descriptor.Before.ToArray()),
            descriptor.After is null ? null : new ReadOnlyCollection<string>(descriptor.After.ToArray()),
            descriptor.DeterminismClass,
            descriptor.Budget is null ? null! : new ProcessorDescriptorBudget(descriptor.Budget.MaxMicros, descriptor.Budget.MaxCommands),
            descriptor.DiagnosticName);
}

public enum ProcessorPlanBuildStatus
{
    Built,
    Rejected
}

public sealed record ProcessorPlanFailure(string GeneratedErrorId, string Detail, IReadOnlyList<string> CanonicalPath);

public readonly record struct ProcessorPlanBuildResult(
    ProcessorPlanBuildStatus Status,
    ProcessorPlan? Plan,
    ProcessorPlanFailure? Failure)
{
    public bool Succeeded => Status == ProcessorPlanBuildStatus.Built && Plan is not null;
}
