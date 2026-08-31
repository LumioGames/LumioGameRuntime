using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Simulation.Phases;

/// <summary>Immutable projection of tick-phase-contract.schema.json.</summary>
public sealed class PhaseContractTable
{
    private readonly PhaseContract[] _contracts;
    private readonly Dictionary<TickPhase, PhaseContract> _byPhase;

    private PhaseContractTable(PhaseContract[] contracts)
    {
        _contracts = new PhaseContract[contracts.Length];
        _byPhase = new Dictionary<TickPhase, PhaseContract>();
        for (var i = 0; i < contracts.Length; i++)
        {
            PhaseContract contract = Clone(contracts[i]);
            _contracts[i] = contract;
            _byPhase.Add(contract.Phase, contract);
        }
    }

    public static PhaseContractTable Default { get; } = CreateDefault();

    public IReadOnlyList<PhaseContract> Contracts
    {
        get
        {
            var copy = new PhaseContract[_contracts.Length];
            for (var i = 0; i < _contracts.Length; i++) copy[i] = Clone(_contracts[i]);
            return Array.AsReadOnly(copy);
        }
    }

    public PhaseContract this[TickPhase phase] => Clone(_byPhase[phase]);

    public int CommitPointCount
    {
        get
        {
            var count = 0;
            foreach (PhaseContract contract in _contracts)
                if (contract.IsAuthoritativeCommitPoint) count++;
            return count;
        }
    }

    public string TickModel => "FailStop";

    public TickPhase CommitPoint => TickPhase.GasAndEventFinalize;

    public bool Validate(out string? detail)
    {
        detail = null;
        if (_contracts.Length != 13)
        {
            detail = "Exactly thirteen phase contracts are required.";
            return false;
        }

        if (CommitPointCount != 1 || this[TickPhase.GasAndEventFinalize].IsAuthoritativeCommitPoint == false)
        {
            detail = "GasAndEventFinalize must be the only authoritative commit point.";
            return false;
        }

        for (var i = 0; i < _contracts.Length; i++)
        {
            if ((int)_contracts[i].Phase != i)
            {
                detail = "Phase ordinals are not contiguous and canonical.";
                return false;
            }

            if (_contracts[i].OverBudgetAction != "FailStop" || _contracts[i].RepeatTickResult != "IdempotentSame")
            {
                detail = "The generated failure/repeat semantics were changed.";
                return false;
            }

            if (i >= (int)TickPhase.VoxelCommit && _contracts[i].CancelPoint != CancelPoint.NotCancellable)
            {
                detail = "VoxelCommit and later phases cannot be cancelled.";
                return false;
            }
        }

        if (this[TickPhase.GasAndEventFinalize].Visibility != Visibility.AfterCommit)
        {
            detail = "Commit point visibility must be AfterCommit.";
            return false;
        }

        return true;
    }

    public static PhaseContractTable CreateDefault()
    {
        return new PhaseContractTable(new[]
        {
            Contract(TickPhase.IngressCapture, new[] { "HostIngress" }, new[] { "IngressQueue" }, PhaseFailureClass.ProcessFault, CancelPoint.BeforeCommit, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.DecodeAndCanonicalize, new[] { "IngressQueue" }, new[] { "CanonicalCommandSet" }, PhaseFailureClass.BusinessReject, CancelPoint.BeforeCommit, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.ApplyInputs, new[] { "CanonicalCommandSet" }, new[] { "InputApplySet" }, PhaseFailureClass.BusinessReject, CancelPoint.BeforeCommit, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.ProcessorPlan, new[] { "ProcessorDescriptors" }, new[] { "ProcessorPlan" }, PhaseFailureClass.SessionFault, CancelPoint.BeforeCommit, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.CrossWorldPrepare, new[] { "Commands" }, new[] { "PreparedGameDelta" }, PhaseFailureClass.BusinessReject, CancelPoint.BeforeCommit, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.NativeJobBarrier, new[] { "NativeJobs" }, new[] { "NativeCompletions" }, PhaseFailureClass.ProcessFault, CancelPoint.BeforeCommit, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.CommitDecision, new[] { "PreparedGameDelta" }, new[] { "CommitIntent" }, PhaseFailureClass.SessionFault, CancelPoint.BeforeCommit, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.VoxelCommit, new[] { "CommitIntent" }, new[] { "VoxelWorld" }, PhaseFailureClass.ProcessFault, CancelPoint.NotCancellable, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.EcsCommandBufferCommit, new[] { "CommitIntent" }, new[] { "GameWorld" }, PhaseFailureClass.ProcessFault, CancelPoint.NotCancellable, Visibility.WithinTickPrivate, false),
            Contract(TickPhase.GasAndEventFinalize, new[] { "GameWorld" }, new[] { "GasEvents" }, PhaseFailureClass.ProcessFault, CancelPoint.NotCancellable, Visibility.AfterCommit, true),
            Contract(TickPhase.ReplicationProjection, new[] { "GasEvents" }, new[] { "ReplicationView" }, PhaseFailureClass.ProcessFault, CancelPoint.NotCancellable, Visibility.AfterCommit, false),
            Contract(TickPhase.SnapshotHashMetrics, new[] { "ReplicationView" }, new[] { "SnapshotHash" }, PhaseFailureClass.ProcessFault, CancelPoint.NotCancellable, Visibility.AfterCommit, false),
            Contract(TickPhase.EgressPublish, new[] { "ReplicationView" }, new[] { "EgressQueue" }, PhaseFailureClass.ProcessFault, CancelPoint.NotCancellable, Visibility.AfterCommit, false)
        });
    }

    private static PhaseContract Contract(TickPhase phase, string[] inputs, string[] writes, PhaseFailureClass failure, CancelPoint cancel, Visibility visibility, bool commit) =>
        new(phase, inputs, writes, failure, cancel, visibility, commit);

    private static PhaseContract Clone(PhaseContract value) =>
        value with
        {
            Inputs = (string[])value.Inputs.Clone(),
            WritableDomains = (string[])value.WritableDomains.Clone()
        };
}
