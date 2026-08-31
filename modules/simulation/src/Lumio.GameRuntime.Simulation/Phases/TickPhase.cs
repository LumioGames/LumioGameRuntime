using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Simulation.Phases;

/// <summary>Runtime projection of the generated tick phase enum.</summary>
public enum TickPhase
{
    IngressCapture = (int)ProcessorDescriptorPhase.IngressCapture,
    DecodeAndCanonicalize = (int)ProcessorDescriptorPhase.DecodeAndCanonicalize,
    ApplyInputs = (int)ProcessorDescriptorPhase.ApplyInputs,
    ProcessorPlan = (int)ProcessorDescriptorPhase.ProcessorPlan,
    CrossWorldPrepare = (int)ProcessorDescriptorPhase.CrossWorldPrepare,
    NativeJobBarrier = (int)ProcessorDescriptorPhase.NativeJobBarrier,
    CommitDecision = (int)ProcessorDescriptorPhase.CommitDecision,
    VoxelCommit = (int)ProcessorDescriptorPhase.VoxelCommit,
    EcsCommandBufferCommit = (int)ProcessorDescriptorPhase.EcsCommandBufferCommit,
    GasAndEventFinalize = (int)ProcessorDescriptorPhase.GasAndEventFinalize,
    ReplicationProjection = (int)ProcessorDescriptorPhase.ReplicationProjection,
    SnapshotHashMetrics = (int)ProcessorDescriptorPhase.SnapshotHashMetrics,
    EgressPublish = (int)ProcessorDescriptorPhase.EgressPublish
}

public enum PhaseFailureClass
{
    BusinessReject,
    SessionFault,
    ProcessFault
}

public enum CancelPoint
{
    BeforeCommit,
    NotCancellable
}

public enum Visibility
{
    WithinTickPrivate,
    AfterCommit
}

public readonly record struct PhaseContract(
    TickPhase Phase,
    string[] Inputs,
    string[] WritableDomains,
    PhaseFailureClass FailureClass,
    CancelPoint CancelPoint,
    Visibility Visibility,
    bool IsAuthoritativeCommitPoint)
{
    public string OverBudgetAction => "FailStop";

    public string RepeatTickResult => "IdempotentSame";
}
