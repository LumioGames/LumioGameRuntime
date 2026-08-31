using System;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Validation;

namespace Lumio.GameRuntime.Replication.Lifecycle;

public sealed class ReplicationContext : IDisposable
{
    private readonly object _gate = new();
    private readonly ReplicationBudget _budget;
    private readonly ReplicationProjection _projection;
    private ReplicationContextState _state = ReplicationContextState.Created;
    private ulong _connectionGeneration;
    private bool _disposed;
    private readonly int _ownerThreadId;

    public ReplicationContext(string sessionId, string productId, string gameReleaseId, MappingSetView mappings, ReplicationBudget budget, ulong connectionGeneration = 1)
    {
        if (!ReplicationValidation.IsIdentifier(sessionId) || !ReplicationValidation.IsIdentifier(productId) || !ReplicationValidation.IsIdentifier(gameReleaseId)) throw new ArgumentException("Session/release identity is invalid.");
        if (mappings is null) throw new ArgumentNullException(nameof(mappings));
        if (!budget.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));
        if (connectionGeneration == 0) throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        SessionId = sessionId;
        ProductId = productId;
        GameReleaseId = gameReleaseId;
        Mappings = mappings;
        _budget = budget;
        _projection = new ReplicationProjection(budget);
        _connectionGeneration = connectionGeneration;
        Baselines = new BaselineStore(budget);
        Deltas = new DeltaHistory(budget);
        Identities = new NetEntityMappingTable();
        ProvisionalRemaps = new ProvisionalRemapTable();
        Tombstones = new TombstoneRegistry();
        _ownerThreadId = Environment.CurrentManagedThreadId;
        ContextId = new ReplicationContextId(connectionGeneration);
        WorldId = sessionId;
    }

    public string SessionId { get; }
    public string ProductId { get; }
    public string GameReleaseId { get; }
    public ReplicationContextId ContextId { get; }
    public string WorldId { get; }
    public MappingSetView Mappings { get; }
    public BaselineStore Baselines { get; }
    public DeltaHistory Deltas { get; }
    public NetEntityMappingTable Identities { get; }
    public ProvisionalRemapTable ProvisionalRemaps { get; }
    public TombstoneRegistry Tombstones { get; }

    public bool IsOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

    public ReplicationContextState State
    {
        get { lock (_gate) return _state; }
    }

    public ulong ConnectionGeneration
    {
        get { lock (_gate) return _connectionGeneration; }
    }

    public ReplicationContextTransitionResult BeginSnapshot()
    {
        lock (_gate) return IsOwnerThread ? Transition(ReplicationContextState.Created, ReplicationContextState.Snapshotting) : ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
    }

    public ReplicationContextTransitionResult AwaitBaselineAck()
    {
        lock (_gate) return IsOwnerThread ? Transition(ReplicationContextState.Snapshotting, ReplicationContextState.AwaitingBaselineAck) : ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
    }

    public ReplicationContextTransitionResult Activate()
    {
        lock (_gate) return IsOwnerThread ? Transition(ReplicationContextState.AwaitingBaselineAck, ReplicationContextState.Active) : ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
    }

    public ReplicationContextTransitionResult BeginResync()
    {
        lock (_gate) return IsOwnerThread ? Transition(ReplicationContextState.Active, ReplicationContextState.Resyncing) : ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
    }

    public ReplicationContextTransitionResult CompleteResync()
    {
        lock (_gate) return IsOwnerThread ? Transition(ReplicationContextState.Resyncing, ReplicationContextState.Active) : ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
    }

    public ReplicationContextTransitionResult Drain()
    {
        lock (_gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state is not (ReplicationContextState.Active or ReplicationContextState.Resyncing)) return ReplicationContextTransitionResult.Rejected(_state, "ContextClosing");
            _state = ReplicationContextState.Draining;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationContextTransitionResult Close()
    {
        lock (_gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state != ReplicationContextState.Draining) return ReplicationContextTransitionResult.Rejected(_state, "InvalidArgument");
            _state = ReplicationContextState.Closed;
            _disposed = true;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationContextTransitionResult Fault()
    {
        lock (_gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state == ReplicationContextState.Closed) return ReplicationContextTransitionResult.Rejected(_state, "ContextDestroyed");
            _state = ReplicationContextState.Faulted;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationValidationResult ValidateConnectionGeneration(ulong generation) =>
        generation == ConnectionGeneration
            ? ReplicationValidationResult.Accepted()
            : ReplicationValidationResult.Rejected(ReplicationValidationCode.StaleConnectionGeneration, "Connection generation is stale.", false);

    public BaselineAckStatus AckBaseline(string snapshotId, ulong revision)
    {
        lock (_gate)
        {
            if (!IsOwnerThread || _state is ReplicationContextState.Closed or ReplicationContextState.Faulted or ReplicationContextState.Draining)
                return BaselineAckStatus.UnknownBaseline;
            return Baselines.Ack(snapshotId, revision);
        }
    }

    public FullSnapshotProjectionResult BuildFullSnapshot(string snapshotId, RevisionVector revision) =>
        IsUsableForProjection() ? _projection.BuildFullSnapshot(SessionId, ProductId, GameReleaseId, snapshotId, revision, Mappings) :
            new FullSnapshotProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("ContextDestroyed", "Replication context is not usable."));

    public DeltaProjectionResult BuildDelta(string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence, RevisionVector revision, System.Collections.Generic.IReadOnlyList<TombstoneView> tombstones, bool gapDetected = false, string? resyncReason = null) =>
        IsUsableForProjection() ? _projection.BuildDelta(SessionId, ProductId, GameReleaseId, baseSnapshotId, fromRevision, toRevision, confirmationSequence, revision, Mappings, tombstones, gapDetected, resyncReason) :
            new DeltaProjectionResult(ProjectionStatus.Rejected, null, ReplicationFailure.Rejected("ContextDestroyed", "Replication context is not usable."));

    public bool TryAdvanceConnectionGeneration(ulong expectedGeneration, out ulong nextGeneration)
    {
        lock (_gate)
        {
            nextGeneration = _connectionGeneration;
            if (!IsOwnerThread || _state is ReplicationContextState.Closed or ReplicationContextState.Faulted || expectedGeneration != _connectionGeneration) return false;
            if (_connectionGeneration == ulong.MaxValue) return false;
            nextGeneration = ++_connectionGeneration;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_state != ReplicationContextState.Closed) _state = ReplicationContextState.Closed;
        }
    }

    private ReplicationContextTransitionResult Transition(ReplicationContextState expected, ReplicationContextState next)
    {
        if (_state != expected) return ReplicationContextTransitionResult.Rejected(_state, "InvalidArgument");
        _state = next;
        return ReplicationContextTransitionResult.Accepted(_state);
    }

    private bool IsUsableForProjection()
    {
        lock (_gate) return IsOwnerThread && _state is not (ReplicationContextState.Closed or ReplicationContextState.Faulted or ReplicationContextState.Draining);
    }
}
