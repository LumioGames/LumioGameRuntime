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

public sealed class CommandPreparePort : ICommandPreparePort
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

public sealed class CommandApplyPort : ICommandApplyPort
{
    private readonly EcsCommandCommitExecutor _executor;

    public CommandApplyPort(EcsCommandCommitExecutor? executor = null) => _executor = executor ?? new EcsCommandCommitExecutor();

    public CommandApplyReceipt Apply(PreparedGameDelta delta) => _executor.Apply(delta);
}
