using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Command.Tests;

public sealed class CommandModuleLifecycleTests
{
    [Theory]
    [InlineData(CommandModuleState.Created)]
    [InlineData(CommandModuleState.Configured)]
    [InlineData(CommandModuleState.Draining)]
    [InlineData(CommandModuleState.Closed)]
    [InlineData(CommandModuleState.Faulted)]
    public void ApplyOutsideRunningIsRejectedWithoutCallingEcs(CommandModuleState state)
    {
        var port = new CountingPort();
        CommandModule module = ModuleAt(state, port);

        CommandApplyReceipt result = module.Apply(Prepared(2UL));

        Assert.False(result.IsApplied);
        Assert.Equal("ContextClosing", result.GeneratedErrorId);
        Assert.Equal(0, port.Calls);
    }

    [Theory]
    [InlineData(CommandModuleState.Created, false)]
    [InlineData(CommandModuleState.Configured, true)]
    [InlineData(CommandModuleState.Running, true)]
    [InlineData(CommandModuleState.Draining, false)]
    [InlineData(CommandModuleState.Closed, false)]
    [InlineData(CommandModuleState.Faulted, false)]
    public void PrepareAdmissionMatchesLifecycle(CommandModuleState state, bool expectedPrepared)
    {
        CommandModule module = ModuleAt(state, new CountingPort());

        CommandPreflightResult result = module.Prepare(Merged(3UL));

        Assert.Equal(expectedPrepared, result.IsPrepared);
        if (!expectedPrepared)
            Assert.Equal("ContextClosing", result.Failure?.GeneratedErrorId);
    }

    [Fact]
    public async Task DrainFencesNewApplyAndCloseWaitsForInflightApply()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var port = new BlockingPort(entered, release);
        CommandModule module = ModuleAt(CommandModuleState.Running, port);

        Task<CommandApplyReceipt> applying = Task.Run(() => module.Apply(Prepared(4UL)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.True(module.BeginDrain().Succeeded);
        CommandModuleResult earlyClose = module.Close();
        CommandApplyReceipt rejected = module.Apply(Prepared(5UL));

        Assert.False(earlyClose.Succeeded);
        Assert.Equal("ContextBusy", earlyClose.GeneratedErrorId);
        Assert.False(rejected.IsApplied);
        Assert.Equal("ContextClosing", rejected.GeneratedErrorId);
        Assert.Equal(1, port.Calls);

        release.Set();
        Assert.True((await applying).IsApplied);
        Assert.True(module.Close().Succeeded);
        Assert.Equal(CommandModuleState.Closed, module.State);
    }

    [Fact]
    public void PublicSurfaceDoesNotExposeRawApplyAuthority()
    {
        string[] executorMethods = typeof(EcsCommandCommitExecutor)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();
        string[] serviceProperties = typeof(CommandServices)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();
        string[] exportedTypes = typeof(CommandModule).Assembly.GetExportedTypes()
            .Select(type => type.Name)
            .ToArray();

        Assert.DoesNotContain("Apply", executorMethods);
        Assert.DoesNotContain("Commit", executorMethods);
        Assert.DoesNotContain("Executor", serviceProperties);
        Assert.DoesNotContain("ApplyPort", serviceProperties);
        Assert.DoesNotContain("ApplyServices", serviceProperties);
        Assert.DoesNotContain("CommandApplyPort", exportedTypes);
    }

    [Fact]
    public void ReentrantEcsCallbackCannotStartNestedApply()
    {
        var port = new ReentrantPort();
        CommandModule module = ModuleAt(CommandModuleState.Running, port);
        PreparedGameDelta nestedDelta = Prepared(7UL);
        port.Callback = () => module.Apply(nestedDelta);

        CommandApplyReceipt outer = module.Apply(Prepared(6UL));

        Assert.True(outer.IsApplied);
        Assert.NotNull(port.Nested);
        Assert.False(port.Nested!.Value.IsApplied);
        Assert.Equal(1, port.Calls);
    }

    private static CommandModule ModuleAt(CommandModuleState state, IEcsCommandCommitPort port)
    {
        CommandModule module = CommandModule.Create(executor: new EcsCommandCommitExecutor(port));
        switch (state)
        {
            case CommandModuleState.Created:
                break;
            case CommandModuleState.Configured:
                Assert.True(module.Configure().Succeeded);
                break;
            case CommandModuleState.Running:
                Assert.True(module.Configure().Succeeded);
                Assert.True(module.Start().Succeeded);
                break;
            case CommandModuleState.Draining:
                Assert.True(module.Configure().Succeeded);
                Assert.True(module.Start().Succeeded);
                Assert.True(module.BeginDrain().Succeeded);
                break;
            case CommandModuleState.Closed:
                Assert.True(module.Configure().Succeeded);
                Assert.True(module.Start().Succeeded);
                Assert.True(module.BeginDrain().Succeeded);
                Assert.True(module.Close().Succeeded);
                break;
            case CommandModuleState.Faulted:
                Assert.True(module.Configure().Succeeded);
                Assert.True(module.Start().Succeeded);
                Assert.True(module.Fault("PanicBoundary").Succeeded);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        return module;
    }

    private static PreparedGameDelta Prepared(ulong tick) =>
        new CommandPreflightValidator().Prepare(Merged(tick));

    private static MergedCommandBatch Merged(ulong tick)
    {
        var buffer = new ProcessorCommandBuffer(tick, "processor", ProcessorDescriptorPhase.ProcessorPlan);
        buffer.Writer.Destroy("entity");
        return new CommandBufferMerger().Merge(tick, new[] { buffer.Seal() });
    }

    private sealed class CountingPort : IEcsCommandCommitPort
    {
        public int Calls { get; private set; }

        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId)
        {
            Calls++;
            return EcsCommandPortResult.Applied();
        }
    }

    private sealed class BlockingPort : IEcsCommandCommitPort
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        internal BlockingPort(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public int Calls { get; private set; }

        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId)
        {
            Calls++;
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                return EcsCommandPortResult.Fault("TimedOut");
            return EcsCommandPortResult.Applied();
        }
    }

    private sealed class ReentrantPort : IEcsCommandCommitPort
    {
        private bool _inside;

        internal Func<CommandApplyReceipt>? Callback { get; set; }

        internal CommandApplyReceipt? Nested { get; private set; }

        public int Calls { get; private set; }

        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId)
        {
            Calls++;
            if (!_inside && Callback is not null)
            {
                _inside = true;
                Nested = Callback();
            }
            return EcsCommandPortResult.Applied();
        }
    }
}
