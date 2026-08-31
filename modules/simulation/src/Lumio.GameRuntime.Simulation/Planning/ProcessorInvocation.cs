using System;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Simulation.Planning;

public sealed class ProcessorInvocation
{
    internal ProcessorInvocation(ProcessorDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public ProcessorDescriptor Descriptor { get; }

    public string ProcessorId => Descriptor.ProcessorId;

    public TickPhase Phase => (TickPhase)Descriptor.Phase;
}
