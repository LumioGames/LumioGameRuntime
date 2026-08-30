namespace Lumio.GameRuntime.Command;

public sealed class CommandServices
{
    internal CommandServices(
        CommandBufferMerger merger,
        CommandPreflightValidator preflight,
        EcsCommandCommitExecutor executor,
        CommandBufferFactory? bufferFactory = null)
    {
        Merger = merger;
        Preflight = preflight;
        Executor = executor;
        BufferFactory = bufferFactory ?? new CommandBufferFactory();
        PrepareServices = new CommandPreparePort(preflight, merger);
        ApplyServices = new CommandApplyPort(executor);
    }

    public CommandBufferMerger Merger { get; }

    public CommandPreflightValidator Preflight { get; }

    public EcsCommandCommitExecutor Executor { get; }

    public ICommandBufferFactory BufferFactory { get; }

    public CommandBufferFactory Buffers => (CommandBufferFactory)BufferFactory;

    public CommandPreflightValidator PreparePort => Preflight;

    public EcsCommandCommitExecutor ApplyPort => Executor;

    public ICommandPreparePort PrepareServices { get; }

    public ICommandApplyPort ApplyServices { get; }
}
