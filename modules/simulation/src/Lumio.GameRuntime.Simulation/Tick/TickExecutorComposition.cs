using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Phases;

namespace Lumio.GameRuntime.Simulation.Tick;

[Flags]
public enum TickExecutorCapability
{
    None = 0,
    IngressCapture = 1 << 0,
    DecodeAndCanonicalize = 1 << 1,
    ApplyInputs = 1 << 2,
    ProcessorPlan = 1 << 3,
    CrossWorldPrepare = 1 << 4,
    NativeJobBarrier = 1 << 5,
    CommitDecision = 1 << 6,
    VoxelCommit = 1 << 7,
    EcsCommandBufferCommit = 1 << 8,
    GasAndEventFinalize = 1 << 9,
    ReplicationProjection = 1 << 10,
    SnapshotHashMetrics = 1 << 11,
    EgressPublish = 1 << 12,
    All = (1 << 13) - 1
}

public enum PhaseOutcomeStatus
{
    Invalid,
    Succeeded,
    Rejected
}

public readonly record struct PhaseOutcome
{
    private PhaseOutcome(PhaseOutcomeStatus status, string? generatedErrorId, string? detail)
    {
        Status = status;
        GeneratedErrorId = generatedErrorId;
        Detail = detail;
    }

    public PhaseOutcomeStatus Status { get; }

    public string? GeneratedErrorId { get; }

    public string? Detail { get; }

    public bool Succeeded => Status == PhaseOutcomeStatus.Succeeded;

    public static PhaseOutcome Success() => new(PhaseOutcomeStatus.Succeeded, null, null);

    public static PhaseOutcome Reject(string generatedErrorId, string detail)
    {
        if (!SimulationValidation.IsStableErrorId(generatedErrorId)) throw new ArgumentException("A generated stable error ID is required.", nameof(generatedErrorId));
        if (string.IsNullOrWhiteSpace(detail)) throw new ArgumentException("A rejection detail is required.", nameof(detail));
        return new PhaseOutcome(PhaseOutcomeStatus.Rejected, generatedErrorId, detail);
    }
}

public interface ITickExecutorPort
{
    string ExecutorId { get; }

    bool IsAvailable { get; }
}

public interface IIngressTickExecutor : ITickExecutorPort
{
    PhaseOutcome Capture(TickExecutionContext context);

    PhaseOutcome DecodeAndCanonicalize(TickExecutionContext context);

    PhaseOutcome ApplyInputs(TickExecutionContext context);
}

public interface IProcessorPlanTickExecutor : ITickExecutorPort
{
    PhaseOutcome BuildPlan(TickExecutionContext context);
}

public interface ICrossWorldTickExecutor : ITickExecutorPort
{
    PhaseOutcome Prepare(TickExecutionContext context);

    PhaseOutcome DecideCommit(TickExecutionContext context);
}

public interface INativeJobBarrierTickExecutor : ITickExecutorPort
{
    PhaseOutcome CompleteNativeJobs(TickExecutionContext context);
}

public interface IVoxelCommitTickExecutor : ITickExecutorPort
{
    PhaseOutcome CommitVoxel(TickExecutionContext context);
}

public interface IEcsCommandBufferCommitTickExecutor : ITickExecutorPort
{
    PhaseOutcome CommitCommands(TickExecutionContext context);
}

public interface IGasAndEventFinalizeTickExecutor : ITickExecutorPort
{
    PhaseOutcome FinalizeGasAndEvents(TickExecutionContext context);
}

public interface IReplicationProjectionTickExecutor : ITickExecutorPort
{
    PhaseOutcome ProjectReplication(TickExecutionContext context);
}

public interface ISnapshotHashMetricsTickExecutor : ITickExecutorPort
{
    PhaseOutcome CaptureSnapshotHashMetrics(TickExecutionContext context);
}

public interface IEgressPublishTickExecutor : ITickExecutorPort
{
    PhaseOutcome PublishEgress(TickExecutionContext context);
}

/// <summary>Required authoritative ports for the fixed thirteen-phase Tick.</summary>
public sealed class TickExecutorComposition
{
    private readonly Dictionary<TickPhase, PhaseHandler> _handlers;
    private readonly IAuthoritativeTickStatePort? _statePort;
    private readonly IDurableTickReplayPort? _replayPort;
    private readonly ISimulationFailureBundlePort? _failurePort;

    internal TickExecutorComposition(
        IIngressTickExecutor ingress,
        IProcessorPlanTickExecutor processorPlan,
        ICrossWorldTickExecutor crossWorld,
        INativeJobBarrierTickExecutor nativeJobBarrier,
        IVoxelCommitTickExecutor voxelCommit,
        IEcsCommandBufferCommitTickExecutor ecsCommit,
        IGasAndEventFinalizeTickExecutor gasFinalize,
        IReplicationProjectionTickExecutor replicationProjection,
        ISnapshotHashMetricsTickExecutor snapshotHashMetrics,
        IEgressPublishTickExecutor egressPublish)
    {
        RequireAvailable(ingress, nameof(ingress));
        RequireAvailable(processorPlan, nameof(processorPlan));
        RequireAvailable(crossWorld, nameof(crossWorld));
        RequireAvailable(nativeJobBarrier, nameof(nativeJobBarrier));
        RequireAvailable(voxelCommit, nameof(voxelCommit));
        RequireAvailable(ecsCommit, nameof(ecsCommit));
        RequireAvailable(gasFinalize, nameof(gasFinalize));
        RequireAvailable(replicationProjection, nameof(replicationProjection));
        RequireAvailable(snapshotHashMetrics, nameof(snapshotHashMetrics));
        RequireAvailable(egressPublish, nameof(egressPublish));

        _handlers = new Dictionary<TickPhase, PhaseHandler>
        {
            [TickPhase.IngressCapture] = ingress.Capture,
            [TickPhase.DecodeAndCanonicalize] = ingress.DecodeAndCanonicalize,
            [TickPhase.ApplyInputs] = ingress.ApplyInputs,
            [TickPhase.ProcessorPlan] = processorPlan.BuildPlan,
            [TickPhase.CrossWorldPrepare] = crossWorld.Prepare,
            [TickPhase.NativeJobBarrier] = nativeJobBarrier.CompleteNativeJobs,
            [TickPhase.CommitDecision] = crossWorld.DecideCommit,
            [TickPhase.VoxelCommit] = voxelCommit.CommitVoxel,
            [TickPhase.EcsCommandBufferCommit] = ecsCommit.CommitCommands,
            [TickPhase.GasAndEventFinalize] = gasFinalize.FinalizeGasAndEvents,
            [TickPhase.ReplicationProjection] = replicationProjection.ProjectReplication,
            [TickPhase.SnapshotHashMetrics] = snapshotHashMetrics.CaptureSnapshotHashMetrics,
            [TickPhase.EgressPublish] = egressPublish.PublishEgress
        };
        Capabilities = TickExecutorCapability.All;
    }

    private TickExecutorComposition(
        IReadOnlyDictionary<TickPhase, PhaseHandler> handlers,
        TickExecutorCapability capabilities,
        IAuthoritativeTickStatePort? statePort,
        IDurableTickReplayPort? replayPort,
        ISimulationFailureBundlePort? failurePort)
    {
        _handlers = new Dictionary<TickPhase, PhaseHandler>(handlers);
        Capabilities = capabilities;
        _statePort = statePort;
        _replayPort = replayPort;
        _failurePort = failurePort;
    }

    public TickExecutorCapability Capabilities { get; }

    public bool IsComplete
    {
        get
        {
            if (_statePort is null || !_statePort.IsAvailable ||
                _replayPort is null || !_replayPort.IsAvailable || _replayPort.RetentionCapacity <= 0 ||
                _failurePort is null || !_failurePort.IsAvailable)
            {
                return false;
            }

            foreach (TickPhase phase in PhaseGraph.Default.Phases)
            {
                if (!_handlers.TryGetValue(phase, out PhaseHandler? handler) || handler is null) return false;
                if ((Capabilities & CapabilityFor(phase)) != CapabilityFor(phase)) return false;
            }

            return true;
        }
    }

    internal IReadOnlyDictionary<TickPhase, PhaseHandler> Handlers =>
        new ReadOnlyDictionary<TickPhase, PhaseHandler>(_handlers);

    internal IAuthoritativeTickStatePort? StatePort => _statePort;

    internal IDurableTickReplayPort? ReplayPort => _replayPort;

    internal ISimulationFailureBundlePort? FailurePort => _failurePort;

    internal static TickExecutorComposition ForHandlers(
        IReadOnlyDictionary<TickPhase, PhaseHandler> handlers,
        TickExecutorCapability capabilities,
        IAuthoritativeTickStatePort? statePort = null,
        IDurableTickReplayPort? replayPort = null,
        ISimulationFailureBundlePort? failurePort = null) =>
        new(handlers, capabilities, statePort, replayPort, failurePort);

    internal static TickExecutorCapability CapabilityFor(TickPhase phase) => phase switch
    {
        TickPhase.IngressCapture => TickExecutorCapability.IngressCapture,
        TickPhase.DecodeAndCanonicalize => TickExecutorCapability.DecodeAndCanonicalize,
        TickPhase.ApplyInputs => TickExecutorCapability.ApplyInputs,
        TickPhase.ProcessorPlan => TickExecutorCapability.ProcessorPlan,
        TickPhase.CrossWorldPrepare => TickExecutorCapability.CrossWorldPrepare,
        TickPhase.NativeJobBarrier => TickExecutorCapability.NativeJobBarrier,
        TickPhase.CommitDecision => TickExecutorCapability.CommitDecision,
        TickPhase.VoxelCommit => TickExecutorCapability.VoxelCommit,
        TickPhase.EcsCommandBufferCommit => TickExecutorCapability.EcsCommandBufferCommit,
        TickPhase.GasAndEventFinalize => TickExecutorCapability.GasAndEventFinalize,
        TickPhase.ReplicationProjection => TickExecutorCapability.ReplicationProjection,
        TickPhase.SnapshotHashMetrics => TickExecutorCapability.SnapshotHashMetrics,
        TickPhase.EgressPublish => TickExecutorCapability.EgressPublish,
        _ => TickExecutorCapability.None
    };

    private static void RequireAvailable(ITickExecutorPort? port, string parameterName)
    {
        if (port is null) throw new ArgumentNullException(parameterName);
        if (!SimulationValidation.IsIdentifier(port.ExecutorId))
            throw new ArgumentException("The executor port must identify its owner.", parameterName);
        if (!port.IsAvailable)
            throw new ArgumentException("The executor port is unavailable and cannot form an authoritative composition.", parameterName);
    }
}
