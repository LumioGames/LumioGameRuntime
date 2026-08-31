using System;
using Lumio.GameRuntime.Command;
using Lumio.GameRuntime.GeneratedContracts;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lumio.GameRuntime.Coordination.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lumio.GameRuntime.Coordination.VoxelAdapters")]

namespace Lumio.GameRuntime.Coordination;

public sealed class CoordinationModule
{
    private readonly object _gate = new();
    private CoordinatorState _state = CoordinatorState.Created;
    private readonly CoordinationServices _services;

    private CoordinationModule(SessionRevisionVectorStore revisions, CrossWorldCoordinator transactions)
    {
        transactions.StopAccepting();
        _services = new CoordinationServices(revisions, transactions, new CommandPreflightValidator());
    }

    public static CoordinationModule Create(SessionRevisionVectorView? initialRevision = null)
    {
        initialRevision ??= new SessionRevisionVectorView(0UL, 0UL, 0UL, new System.Collections.Generic.Dictionary<string, ulong>(), 0UL, 0UL, (ulong)GeneratedContractManifest.SchemaEpoch);
        var revisions = new SessionRevisionVectorStore(initialRevision);
        return new CoordinationModule(revisions, new CrossWorldCoordinator(revisions));
    }

    /// <summary>Creates a configured composition; the resolver must project the persisted ECS participant result.</summary>
    internal static CoordinationModule Create(
        SessionRevisionVectorView initialRevision,
        IGameReservationPort game,
        IVoxelWorldPort voxel,
        EcsCommandCommitExecutor ecs,
        ITxnJournalPort journal,
        Func<TxnRecord, CommandApplyReceipt, SessionRevisionVectorView?> ecsRevisionResolver,
        ITxnResultEvidencePort? resultEvidence = null)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(initialRevision);
        ArgumentNullException.ThrowIfNull(ecsRevisionResolver);
#else
        if (initialRevision is null) throw new ArgumentNullException(nameof(initialRevision));
        if (ecsRevisionResolver is null) throw new ArgumentNullException(nameof(ecsRevisionResolver));
#endif
        var revisions = new SessionRevisionVectorStore(initialRevision);
        var revisionPort = new DelegateEcsCommandCommitRevisionPort(ecsRevisionResolver);
        return new CoordinationModule(
            revisions,
            new CrossWorldCoordinator(revisions, game, voxel, ecs, journal, revisionPort,
                resultEvidence ?? new MissingTxnResultEvidencePort()));
    }

    private sealed class DelegateEcsCommandCommitRevisionPort : IEcsCommandCommitRevisionPort
    {
        private readonly Func<TxnRecord, CommandApplyReceipt, SessionRevisionVectorView?> _resolver;

        internal DelegateEcsCommandCommitRevisionPort(
            Func<TxnRecord, CommandApplyReceipt, SessionRevisionVectorView?> resolver) => _resolver = resolver;

        public SessionRevisionVectorView? ReadResultRevision(TxnRecord record, CommandApplyReceipt receipt) =>
            _resolver(record, receipt);
    }

    public CoordinatorState State
    {
        get { lock (_gate) return _state; }
    }

    public CoordinationServices Services => _services;

    public SessionRevisionVectorStore Revisions => _services.Revisions;

    public CoordinationLifecycleResult Configure()
    {
        lock (_gate)
        {
            if (_state != CoordinatorState.Created) return Fail("InvalidArgument");
            _state = CoordinatorState.Ready;
            return new CoordinationLifecycleResult(true, _state, null);
        }
    }

    public CoordinationLifecycleResult Start()
    {
        lock (_gate)
        {
            if (_state != CoordinatorState.Ready) return Fail("InvalidArgument");
            _state = CoordinatorState.Running;
            _services.Transactions.ResumeAccepting();
            return new CoordinationLifecycleResult(true, _state, null);
        }
    }

    public CoordinationLifecycleResult BeginDrain()
    {
        lock (_gate)
        {
            if (_state != CoordinatorState.Running) return Fail("InvalidArgument");
            _state = CoordinatorState.Draining;
            _services.Transactions.StopPreparing();
            return new CoordinationLifecycleResult(true, _state, null);
        }
    }

    public CoordinationLifecycleResult Dispose()
    {
        lock (_gate)
        {
            if (_state is not (CoordinatorState.Draining or CoordinatorState.Ready)) return Fail("InvalidArgument");
            _services.Transactions.StopAccepting();
            _state = CoordinatorState.Disposed;
            return new CoordinationLifecycleResult(true, _state, null);
        }
    }

    public CoordinationLifecycleResult Fault(string errorId)
    {
        if (string.IsNullOrWhiteSpace(errorId)) throw new ArgumentException("An error ID is required.", nameof(errorId));
        lock (_gate)
        {
            if (_state is CoordinatorState.Disposed or CoordinatorState.Faulted) return Fail("InvalidArgument");
            _services.Transactions.StopAccepting();
            _state = CoordinatorState.Faulted;
            return new CoordinationLifecycleResult(true, _state, CoordinationFailure.Fatal(errorId, "Coordinator faulted."));
        }
    }

    private CoordinationLifecycleResult Fail(string errorId) =>
        new(false, _state, CoordinationFailure.Rejected(errorId, "Invalid coordinator lifecycle transition."));
}
