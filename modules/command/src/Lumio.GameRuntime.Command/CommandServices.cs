namespace Lumio.GameRuntime.Command;

public sealed class CommandServices
{
    private readonly CommandModule _module;

    internal CommandServices(CommandModule module) => _module = module;

    public BufferOpenResult OpenBuffer(in ProcessorInvocationKey key, in CommandBufferBudget budget) =>
        _module.OpenBuffer(in key, in budget);

    public CommandMergeResult Merge(ulong tickId, System.Collections.Generic.IEnumerable<SealedCommandBuffer> buffers) =>
        _module.Merge(tickId, buffers);

    public CommandPreflightResult Prepare(MergedCommandBatch batch) => _module.Prepare(batch);

    public CommandApplyReceipt Apply(PreparedGameDelta delta) => _module.Apply(delta);
}
