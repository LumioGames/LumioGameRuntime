using System;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Validation;

namespace Lumio.GameRuntime.Replication.Resync;

public readonly record struct ResyncDecision(bool RequiresResync, string? Reason, ReplicationValidationCode Code)
{
    public static ResyncDecision Noop() => new(false, null, ReplicationValidationCode.Accepted);

    public static ResyncDecision Required(string reason, ReplicationValidationCode code) => new(true, reason, code);
}

public sealed class ResyncCoordinator
{
    private readonly ReplicationEnvelopeValidator _validator = new();

    public ResyncDecision Evaluate(ReplicationContext context, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong sequence, ulong expectedSequence)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (!context.Baselines.TryGet(baseSnapshotId, out History.BaselineRecord? baseline) || baseline is null || !baseline.Acknowledged)
            return ResyncDecision.Required("UnknownBaseline", ReplicationValidationCode.UnknownBaseline);
        ReplicationValidationResult sequenceResult = _validator.ValidateSequence(sequence, expectedSequence);
        if (!sequenceResult.Succeeded) return ResyncDecision.Required(sequenceResult.Detail ?? "Gap", sequenceResult.Code);
        DeltaChainResult chain = context.Deltas.TryGetContiguous(baseSnapshotId, fromRevision, toRevision);
        if (chain.Status != DeltaChainStatus.Complete)
        {
            return ResyncDecision.Required(chain.Status == DeltaChainStatus.UnknownBaseline ? "HistoryExhausted" : chain.Status.ToString(), ReplicationValidationCode.Gap);
        }
        return ResyncDecision.Noop();
    }

    public ReplicationContextTransitionResult Request(ReplicationContext context, string reason)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(reason)) return ReplicationContextTransitionResult.Rejected(context.State, "InvalidArgument");
        if (context.State == ReplicationContextState.Resyncing) return ReplicationContextTransitionResult.Accepted(context.State);
        return context.BeginResync();
    }

    public ReplicationContextTransitionResult Complete(ReplicationContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (context.State == ReplicationContextState.Active) return ReplicationContextTransitionResult.Accepted(context.State);
        return context.CompleteResync();
    }
}
