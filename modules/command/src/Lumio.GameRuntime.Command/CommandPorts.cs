using System;

namespace Lumio.GameRuntime.Command;

public interface ICommandPreparePort
{
    CommandMergeResult Merge(ReadOnlySpan<SealedCommandBuffer> buffers, ulong tickId);

    CommandPrepareResult Prepare(in MergedCommandBatch batch, in CommandPrepareContext context);
}

public interface ICommandApplyPort
{
    CommandApplyReceipt Apply(PreparedGameDelta delta);
}

internal sealed class CommandPreparePort : ICommandPreparePort
{
    private readonly CommandPreflightValidator _validator;
    private readonly CommandBufferMerger _merger;

    public CommandPreparePort(CommandPreflightValidator? validator = null, CommandBufferMerger? merger = null)
    {
        _validator = validator ?? new CommandPreflightValidator();
        _merger = merger ?? new CommandBufferMerger();
    }

    public CommandMergeResult Merge(ReadOnlySpan<SealedCommandBuffer> buffers, ulong tickId)
    {
        SealedCommandBuffer[] copy = buffers.ToArray();
        return _merger.TryMergeResult(tickId, copy);
    }

    public CommandPrepareResult Prepare(in MergedCommandBatch batch, in CommandPrepareContext context) =>
        _validator.Prepare(in batch, in context);
}

internal sealed class CommandApplyPort : ICommandApplyPort
{
    private readonly CommandModule _module;

    internal CommandApplyPort(CommandModule module) => _module = module ?? throw new ArgumentNullException(nameof(module));

    public CommandApplyReceipt Apply(PreparedGameDelta delta) => _module.Apply(delta);
}
