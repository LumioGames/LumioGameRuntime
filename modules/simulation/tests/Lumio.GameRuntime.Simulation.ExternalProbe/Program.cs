using System;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Simulation;
using Lumio.GameRuntime.Simulation.Tick;

var ports = new NoOpExecutorPorts();
IIngressTickExecutor ingress = ports;
_ = ingress;

int publicCompositionConstructors = typeof(TickExecutorComposition).GetConstructors().Length;
int publicCompositionInjection = typeof(SimulationModule)
    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
    .Count(method => method.Name == nameof(SimulationModule.CreateSession) &&
        method.GetParameters().Any(parameter => parameter.ParameterType == typeof(TickExecutorComposition)));

Console.WriteLine($"PUBLIC_COMPOSITION_CONSTRUCTORS={publicCompositionConstructors}");
Console.WriteLine($"PUBLIC_COMPOSITION_INJECTION={publicCompositionInjection}");
return publicCompositionConstructors == 0 && publicCompositionInjection == 0 ? 0 : 1;

internal sealed class NoOpExecutorPorts :
    IIngressTickExecutor,
    IProcessorPlanTickExecutor,
    ICrossWorldTickExecutor,
    INativeJobBarrierTickExecutor,
    IVoxelCommitTickExecutor,
    IEcsCommandBufferCommitTickExecutor,
    IGasAndEventFinalizeTickExecutor,
    IReplicationProjectionTickExecutor,
    ISnapshotHashMetricsTickExecutor,
    IEgressPublishTickExecutor
{
    public string ExecutorId => "external-no-op";

    public bool IsAvailable => true;

    public PhaseOutcome Capture(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome DecodeAndCanonicalize(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome ApplyInputs(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome BuildPlan(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome Prepare(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome DecideCommit(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome CompleteNativeJobs(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome CommitVoxel(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome CommitCommands(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome FinalizeGasAndEvents(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome ProjectReplication(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome CaptureSnapshotHashMetrics(TickExecutionContext context) => PhaseOutcome.Success();

    public PhaseOutcome PublishEgress(TickExecutionContext context) => PhaseOutcome.Success();
}
