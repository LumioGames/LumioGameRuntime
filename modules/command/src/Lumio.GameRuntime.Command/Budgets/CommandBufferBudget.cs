using System;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command;

/// <summary>Explicit command and byte limits. Bytes are caller-supplied estimates, never object sizes.</summary>
public readonly record struct CommandBufferBudget(ulong MaxCommands, ulong MaxBytes)
{
    public CommandBufferBudget(ulong maxBuffers, ulong maxCommands, ulong maxBytes)
        : this(maxCommands, maxBytes)
    {
        MaxBuffers = maxBuffers;
    }

    public CommandBufferBudget(int maxBuffers, int maxCommands, int maxBytes)
        : this(checked((ulong)maxBuffers), checked((ulong)maxCommands), checked((ulong)maxBytes))
    {
    }

    public CommandBufferBudget(int maxCommands, int maxBytes)
        : this(checked((ulong)maxCommands), checked((ulong)maxBytes))
    {
    }

    public ulong MaxBuffers { get; init; } = ulong.MaxValue;

    public ulong CommandBufferMaxCommands => MaxCommands;

    public ulong CommandBufferMaxBytes => MaxBytes;

    // A zero command allowance is valid for processors that are intentionally
    // command-free; the first append is then rejected by TryAdd.
    public bool IsValid => MaxBytes > 0UL && MaxBuffers > 0UL;

    public static CommandBufferBudget Unlimited { get; } =
        new(ulong.MaxValue, ulong.MaxValue);

    public static CommandBufferBudget FromProcessorBudget(ProcessorDescriptorBudget processor, ulong maxBytes) =>
        new(processor?.MaxCommands ?? throw new ArgumentNullException(nameof(processor)), maxBytes);

    public static CommandBufferBudget Stricter(CommandBufferBudget processor, CommandBufferBudget global) =>
        new(Math.Min(processor.MaxCommands, global.MaxCommands), Math.Min(processor.MaxBytes, global.MaxBytes))
        {
            MaxBuffers = Math.Min(processor.MaxBuffers, global.MaxBuffers)
        };

    public bool TryAdd(ulong currentCommands, ulong currentBytes, ulong commandBytes)
    {
        try
        {
            ulong nextCommands = checked(currentCommands + 1UL);
            ulong nextBytes = checked(currentBytes + commandBytes);
            return nextCommands <= MaxCommands && nextBytes <= MaxBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

public readonly record struct CommandBudgetUsage(ulong Commands, ulong Bytes)
{
    public bool Fits(CommandBufferBudget budget) =>
        Commands <= budget.MaxCommands && Bytes <= budget.MaxBytes;
}
