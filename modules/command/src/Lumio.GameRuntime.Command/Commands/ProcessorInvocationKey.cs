using System;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command;

/// <summary>Value identity for one processor invocation; it never contains runtime addresses.</summary>
public readonly record struct ProcessorInvocationKey(
    ulong TickId,
    string WorldId,
    ProcessorDescriptorPhase Phase,
    string ProcessorId)
{
    public ProcessorInvocationKey(ulong tickId, ProcessorDescriptorPhase phase, string processorId, string worldId)
        : this(tickId, worldId, phase, processorId)
    {
    }

    public ProcessorInvocationKey(ulong tickId, string processorId, string worldId, ProcessorDescriptorPhase phase)
        : this(tickId, worldId, phase, processorId)
    {
    }

    public ProcessorInvocationKey(ulong tickId, string processorId, ProcessorDescriptorPhase phase)
        : this(tickId, "default", phase, processorId)
    {
    }

    public ProcessorInvocationKey(string worldId, ulong tickId, string processorId, ProcessorDescriptorPhase phase)
        : this(tickId, worldId, phase, processorId)
    {
    }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(WorldId) && !string.IsNullOrWhiteSpace(ProcessorId) &&
        CommandValidation.IsIdentifier(WorldId) && CommandValidation.IsIdentifier(ProcessorId);
}
