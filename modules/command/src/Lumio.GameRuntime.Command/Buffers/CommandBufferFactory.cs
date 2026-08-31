using System;

namespace Lumio.GameRuntime.Command;

public enum BufferOpenStatus
{
    Opened,
    Rejected,
    Retryable
}

public readonly record struct BufferOpenResult(
    BufferOpenStatus Status,
    ProcessorCommandBuffer? Buffer,
    CommandFailure? Failure)
{
    public bool Succeeded => Status == BufferOpenStatus.Opened && Buffer is not null;
}

public interface ICommandBufferFactory
{
    BufferOpenResult Open(in ProcessorInvocationKey key, in CommandBufferBudget budget);
}

public sealed class CommandBufferFactory : ICommandBufferFactory
{
    private readonly object _gate = new();
    private readonly ulong _maxBuffers;
    private ulong _openedBuffers;

    public CommandBufferFactory(ulong maxBuffers = ulong.MaxValue)
    {
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfZero(maxBuffers);
#else
        if (maxBuffers == 0UL) throw new ArgumentOutOfRangeException(nameof(maxBuffers));
#endif
        _maxBuffers = maxBuffers;
    }

    public ulong OpenedBuffers
    {
        get { lock (_gate) return _openedBuffers; }
    }

    public BufferOpenResult Open(in ProcessorInvocationKey key, in CommandBufferBudget budget)
    {
        if (!key.IsValid) return new BufferOpenResult(BufferOpenStatus.Rejected, null,
            CommandFailure.Rejected("InvalidArgument", "Processor invocation key is malformed."));
        if (!budget.IsValid) return new BufferOpenResult(BufferOpenStatus.Rejected, null,
            CommandFailure.Rejected("BudgetExceeded", "Command buffer budget is invalid."));

        lock (_gate)
        {
            if (_openedBuffers >= _maxBuffers || _openedBuffers >= budget.MaxBuffers)
            {
                return new BufferOpenResult(BufferOpenStatus.Retryable, null,
                    CommandFailure.Retryable("QueueFull", "Command buffer capacity is exhausted."));
            }

            _openedBuffers++;
        }

        var buffer = new ProcessorCommandBuffer(key.TickId, key.WorldId, key.ProcessorId, key.Phase, true, budget);
        return new BufferOpenResult(BufferOpenStatus.Opened, buffer, null);
    }

    public void Reset() { lock (_gate) _openedBuffers = 0UL; }
}
