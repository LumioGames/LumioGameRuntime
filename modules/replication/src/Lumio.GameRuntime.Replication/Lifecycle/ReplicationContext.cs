using System;
using System.Collections.Generic;
using System.Text;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Validation;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Replication.Lifecycle;

public sealed class ReplicationContext : IDisposable
{
    private readonly ReplicationStoreScope _scope;
    private readonly ReplicationProjection _projection;
    private readonly ReplicationBudget _budget;
    private readonly BaselineStore _baselineStore;
    private readonly DeltaHistory _deltaHistory;
    private readonly NetEntityMappingTable _identityStore;
    private readonly ProvisionalRemapTable _provisionalRemapStore;
    private readonly TombstoneRegistry _tombstoneStore;
    private ReplicationContextState _state = ReplicationContextState.Created;
    private string? _pendingBaselineId;
    private ulong _pendingBaselineSequence;
    private ulong _pendingBaselineRevision;
    private string? _lastAcknowledgedBaselineId;
    private ulong _lastAcknowledgedBaselineRevision;
    private BaselineAckStatus _lastBaselineAckResult;
    private bool _hasBaselineAckResult;
    private bool _disposed;
    private readonly int _ownerThreadId;

    public ReplicationContext(
        string sessionId,
        string productId,
        string gameReleaseId,
        MappingSetView mappings,
        ReplicationBudget budget,
        ulong connectionGeneration = 1)
    {
        if (!ReplicationValidation.IsIdentifier(sessionId) ||
            !ReplicationValidation.IsProductId(productId) ||
            !ReplicationValidation.IsReleaseId(gameReleaseId))
            throw new ArgumentException("Session/release identity is invalid.");
        if (mappings is null) throw new ArgumentNullException(nameof(mappings));
        if (!budget.IsValid) throw new ArgumentOutOfRangeException(nameof(budget));
        if (connectionGeneration == 0) throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        SessionId = sessionId;
        ProductId = productId;
        GameReleaseId = gameReleaseId;
        Mappings = mappings;
        _budget = budget;
        _projection = new ReplicationProjection(budget);
        _scope = new ReplicationStoreScope(connectionGeneration, ReplicationStoreScopeMode.ContextOwned);
        _baselineStore = new BaselineStore(budget, _scope);
        _deltaHistory = new DeltaHistory(budget, _scope);
        _identityStore = new NetEntityMappingTable(_scope);
        _provisionalRemapStore = new ProvisionalRemapTable(_scope);
        _tombstoneStore = new TombstoneRegistry(_scope);
        Baselines = new BaselineStoreView(_baselineStore);
        Deltas = new DeltaHistoryView(_deltaHistory);
        Identities = new NetEntityMappingView(_identityStore);
        ProvisionalRemaps = new ProvisionalRemapView(_provisionalRemapStore);
        Tombstones = new TombstoneRegistryView(_tombstoneStore);
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
    public BaselineStoreView Baselines { get; }
    public DeltaHistoryView Deltas { get; }
    public NetEntityMappingView Identities { get; }
    public ProvisionalRemapView ProvisionalRemaps { get; }
    public TombstoneRegistryView Tombstones { get; }

    public bool IsOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

    public ReplicationContextState State
    {
        get { lock (_scope.Gate) return _state; }
    }

    public ulong ConnectionGeneration
    {
        get { lock (_scope.Gate) return _scope.ConnectionGeneration; }
    }

    public ulong WorkEpoch
    {
        get { lock (_scope.Gate) return _scope.WorkEpoch; }
    }

    public IdentityStoreToken CaptureWorkToken()
    {
        lock (_scope.Gate)
        {
            return IsOwnerThread && IsContextUsableLocked()
                ? _scope.CaptureLocked()
                : default;
        }
    }

    public bool IsWorkTokenCurrent(IdentityStoreToken token)
    {
        lock (_scope.Gate) return IsContextUsableLocked() && _scope.IsCurrentLocked(token);
    }

    public ReplicationContextTransitionResult BeginSnapshot()
    {
        lock (_scope.Gate)
            return IsOwnerThread
                ? TransitionLocked(ReplicationContextState.Created, ReplicationContextState.Snapshotting)
                : ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
    }

    public ReplicationContextTransitionResult AwaitBaselineAck()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state is not (ReplicationContextState.Snapshotting or ReplicationContextState.Resyncing))
                return ReplicationContextTransitionResult.Rejected(_state, "InvalidArgument");
            _state = ReplicationContextState.AwaitingBaselineAck;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationContextTransitionResult Activate()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state != ReplicationContextState.AwaitingBaselineAck)
                return ReplicationContextTransitionResult.Rejected(_state, "InvalidArgument");
            if (!HasAcknowledgedBaselineForActivationLocked())
                return ReplicationContextTransitionResult.Rejected(_state, "SnapshotBaseMismatch");
            RegisterPendingBaselineLocked();
            _state = ReplicationContextState.Active;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationContextTransitionResult BeginResync()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state != ReplicationContextState.Active)
                return ReplicationContextTransitionResult.Rejected(_state, "InvalidArgument");
            if (!_scope.TryAdvanceWorkEpochLocked())
            {
                InvalidateStoresLocked();
                _state = ReplicationContextState.Faulted;
                return ReplicationContextTransitionResult.Rejected(_state, "ContextDestroyed");
            }

            _baselineStore.ClearContextLocked();
            ClearPendingBaselineLocked();
            _deltaHistory.ClearContextLocked();
            _projection.ResetIdempotency();
            _state = ReplicationContextState.Resyncing;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationContextTransitionResult CompleteResync()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state is not (ReplicationContextState.Resyncing or ReplicationContextState.AwaitingBaselineAck))
                return ReplicationContextTransitionResult.Rejected(_state, "InvalidArgument");
            if (!HasAcknowledgedBaselineForActivationLocked())
                return ReplicationContextTransitionResult.Rejected(_state, "SnapshotBaseMismatch");
            RegisterPendingBaselineLocked();
            _state = ReplicationContextState.Active;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationContextTransitionResult Drain()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state is not (ReplicationContextState.Active or ReplicationContextState.Resyncing))
                return ReplicationContextTransitionResult.Rejected(_state, "ContextClosing");
            _state = ReplicationContextState.Draining;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationContextTransitionResult Close()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state != ReplicationContextState.Draining)
                return ReplicationContextTransitionResult.Rejected(_state, "InvalidArgument");
            CloseStoresLocked();
            _state = ReplicationContextState.Closed;
            _disposed = true;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationContextTransitionResult Fault()
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread) return ReplicationContextTransitionResult.Rejected(_state, "WrongContext");
            if (_state is ReplicationContextState.Closed or ReplicationContextState.Faulted)
                return ReplicationContextTransitionResult.Rejected(_state, "ContextDestroyed");
            InvalidateStoresLocked();
            _state = ReplicationContextState.Faulted;
            return ReplicationContextTransitionResult.Accepted(_state);
        }
    }

    public ReplicationValidationResult ValidateConnectionGeneration(ulong generation)
    {
        lock (_scope.Gate)
        {
            return generation == _scope.ConnectionGeneration
                ? ReplicationValidationResult.Accepted()
                : ReplicationValidationResult.Rejected(
                    ReplicationValidationCode.StaleConnectionGeneration,
                    "Connection generation is stale.",
                    false,
                    "StaleConnectionGeneration");
        }
    }

    public BaselineAckStatus AckBaseline(string snapshotId, ulong revision)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread || !IsContextUsableLocked())
                return BaselineAckStatus.UnknownBaseline;
            if (_hasBaselineAckResult && _lastAcknowledgedBaselineId is not null &&
                string.Equals(_lastAcknowledgedBaselineId, snapshotId, StringComparison.Ordinal))
            {
                return _lastAcknowledgedBaselineRevision == revision
                    ? _lastBaselineAckResult
                    : BaselineAckStatus.RevisionConflict;
            }
            if (_state != ReplicationContextState.AwaitingBaselineAck)
                return BaselineAckStatus.UnknownBaseline;
            if (_pendingBaselineId is null || !string.Equals(_pendingBaselineId, snapshotId, StringComparison.Ordinal))
                return BaselineAckStatus.UnknownBaseline;
            BaselineAckStatus result = _baselineStore.Ack(snapshotId, revision, _scope.CaptureLocked());
            if (result is BaselineAckStatus.Acknowledged or BaselineAckStatus.AlreadyAcknowledged)
            {
                _lastAcknowledgedBaselineId = snapshotId;
                _lastAcknowledgedBaselineRevision = revision;
                _lastBaselineAckResult = result;
                _hasBaselineAckResult = true;
                return _lastBaselineAckResult;
            }
            return result;
        }
    }

    public FullSnapshotProjectionResult BuildFullSnapshot(string snapshotId, RevisionVector revision) =>
        BuildFullSnapshotCore(snapshotId, revision, default, tokenRequired: false);

    public FullSnapshotProjectionResult BuildFullSnapshot(
        string snapshotId,
        RevisionVector revision,
        IdentityStoreToken token) =>
        BuildFullSnapshotCore(snapshotId, revision, token, tokenRequired: true);

    private FullSnapshotProjectionResult BuildFullSnapshotCore(
        string snapshotId,
        RevisionVector revision,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            ReplicationFailure? fence = ValidateMutationLocked(token, tokenRequired);
            if (fence is not null)
                return new FullSnapshotProjectionResult(ProjectionStatus.Rejected, null, fence);
            IdentityStoreToken current = _scope.CaptureLocked();
            string? requestKey = null;
            if (Mappings.Mappings.Count <= _budget.ProjectionItemLimit &&
                ReplicationProjection.TryBuildFullSnapshotIdempotencyKey(
                    SessionId, ProductId, GameReleaseId, snapshotId, revision, Mappings, out string fullKey))
            {
                requestKey = fullKey;
                if (_baselineStore.TryGetByIdempotencyKey(requestKey, out BaselineRecord? retained) && retained is not null)
                {
                    if (retained.Projection is not null)
                        return new FullSnapshotProjectionResult(ProjectionStatus.Succeeded, retained.Projection, null);
                }
                if (_projection.TryGetFullSnapshotReplay(requestKey, out ulong replaySequence))
                {
                    FullSnapshotProjectionResult replayCandidate = _projection.BuildFullSnapshotCandidate(
                        SessionId, ProductId, GameReleaseId, snapshotId, revision!, Mappings);
                    if (replayCandidate.Succeeded)
                        return new FullSnapshotProjectionResult(
                            ProjectionStatus.Succeeded,
                            replayCandidate.Snapshot!.WithSequence(replaySequence),
                            null);
                }
            }
            if (_state is not (ReplicationContextState.Snapshotting or ReplicationContextState.Resyncing))
                return new FullSnapshotProjectionResult(
                    ProjectionStatus.Rejected,
                    null,
                    ReplicationFailure.Rejected(
                        "InvalidArgument",
                        "Full snapshot production requires snapshotting or resyncing state."));
            FullSnapshotProjectionResult result = _projection.BuildFullSnapshotCandidate(
                SessionId, ProductId, GameReleaseId, snapshotId, revision!, Mappings);
            if (!result.Succeeded) return result;
            requestKey ??= result.Snapshot!.IdempotencyKey;
            var baseline = new BaselineRecord(
                result.Snapshot!.SnapshotId,
                result.Snapshot.Revision.ReplicationRevision,
                Encoding.UTF8.GetByteCount(result.Snapshot.BodyJson),
                result.Snapshot.MappingSetHash,
                checked((int)result.Snapshot.SchemaEpoch))
            {
                Sequence = result.Snapshot.Sequence,
                IdempotencyKey = requestKey,
                Projection = result.Snapshot,
            };
            BaselineStoreStatus staged = _baselineStore.Stage(baseline, current);
            if (staged is not (BaselineStoreStatus.Accepted or BaselineStoreStatus.Duplicate))
                return new FullSnapshotProjectionResult(
                    ProjectionStatus.Retryable,
                    null,
                    ReplicationFailure.Retryable(
                        staged == BaselineStoreStatus.QueueFull ? "QueueFull" : "InvalidArgument",
                        "Baseline could not be retained."));
            if (staged == BaselineStoreStatus.Duplicate)
            {
                if (_baselineStore.TryGetByIdempotencyKey(requestKey, out BaselineRecord? retained) && retained is not null)
                {
                    if (retained.Projection is not null)
                        return new FullSnapshotProjectionResult(ProjectionStatus.Succeeded, retained.Projection, null);
                    return new FullSnapshotProjectionResult(
                        ProjectionStatus.Succeeded,
                        result.Snapshot.WithSequence(retained.Sequence),
                        null);
                }

                if (_baselineStore.TryGet(baseline.SnapshotId, out BaselineRecord? sameSnapshot) && sameSnapshot is not null)
                {
                    if (sameSnapshot.Projection is not null)
                        return new FullSnapshotProjectionResult(ProjectionStatus.Succeeded, sameSnapshot.Projection, null);
                    return new FullSnapshotProjectionResult(
                        ProjectionStatus.Succeeded,
                        result.Snapshot.WithSequence(sameSnapshot.Sequence),
                        null);
                }

                return new FullSnapshotProjectionResult(
                    ProjectionStatus.Succeeded,
                    result.Snapshot,
                    null);
            }
            if (!_projection.CommitFullSnapshot(result.Snapshot.Sequence, requestKey))
            {
                if (staged == BaselineStoreStatus.Accepted) _baselineStore.Release(baseline.SnapshotId, current);
                return new FullSnapshotProjectionResult(
                    ProjectionStatus.Retryable,
                    null,
                    ReplicationFailure.Retryable("QueueFull", "FullSnapshot replay could not be retained."));
            }

            _pendingBaselineId = baseline.SnapshotId;
            _pendingBaselineSequence = result.Snapshot.Sequence;
            _pendingBaselineRevision = baseline.Revision;

            return result;
        }
    }

    public DeltaProjectionResult BuildDelta(
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        ulong confirmationSequence,
        RevisionVector revision,
        IReadOnlyList<TombstoneView> tombstones,
        bool gapDetected = false,
        string? resyncReason = null) =>
        BuildDeltaCore(
            baseSnapshotId, fromRevision, toRevision, confirmationSequence, revision, tombstones,
            default, tokenRequired: false, gapDetected, resyncReason);

    public DeltaProjectionResult BuildDelta(
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        ulong confirmationSequence,
        RevisionVector revision,
        IReadOnlyList<TombstoneView> tombstones,
        IdentityStoreToken token,
        bool gapDetected = false,
        string? resyncReason = null) =>
        BuildDeltaCore(
            baseSnapshotId, fromRevision, toRevision, confirmationSequence, revision, tombstones,
            token, tokenRequired: true, gapDetected, resyncReason);

    private DeltaProjectionResult BuildDeltaCore(
        string baseSnapshotId,
        ulong fromRevision,
        ulong toRevision,
        ulong confirmationSequence,
        RevisionVector revision,
        IReadOnlyList<TombstoneView> tombstones,
        IdentityStoreToken token,
        bool tokenRequired,
        bool gapDetected,
        string? resyncReason)
    {
        lock (_scope.Gate)
        {
            ReplicationFailure? fence = ValidateMutationLocked(token, tokenRequired);
            if (fence is not null) return new DeltaProjectionResult(ProjectionStatus.Rejected, null, fence);
            if (_state != ReplicationContextState.Active)
                return new DeltaProjectionResult(
                    ProjectionStatus.Rejected,
                    null,
                    ReplicationFailure.Rejected("InvalidArgument", "Delta production requires an active context."));
            IdentityStoreToken current = _scope.CaptureLocked();
            string? requestKey = null;
            if (tombstones is not null && tombstones.Count <= _budget.ProjectionItemLimit &&
                ReplicationProjection.TryBuildDeltaIdempotencyKey(
                    SessionId, ProductId, GameReleaseId, baseSnapshotId, fromRevision, toRevision,
                    confirmationSequence, revision, Mappings, tombstones, gapDetected, resyncReason,
                    out string deltaKey))
            {
                requestKey = deltaKey;
                if (_deltaHistory.TryGetByIdempotencyKey(requestKey, out DeltaRecord? retained) && retained is not null && retained.Projection is not null)
                    return new DeltaProjectionResult(ProjectionStatus.Succeeded, retained.Projection, null);
                if (_projection.TryGetDeltaReplay(requestKey, out ulong replaySequence))
                {
                    DeltaProjectionResult replayCandidate = _projection.BuildDeltaCandidate(
                        SessionId, ProductId, GameReleaseId, baseSnapshotId, fromRevision, toRevision,
                        confirmationSequence, revision!, Mappings, tombstones!, gapDetected, resyncReason);
                    if (replayCandidate.Succeeded)
                        return new DeltaProjectionResult(
                            ProjectionStatus.Succeeded,
                            replayCandidate.Delta!.WithSequence(replaySequence),
                            null);
                }
            }
            // A durable replay remains valid after the materialized baseline or
            // delta record has rotated out. Resolve it before requiring the
            // current baseline so a transport retry cannot become generic
            // SnapshotBaseMismatch or allocate a fresh sequence.
            if (!_baselineStore.TryGet(baseSnapshotId, out BaselineRecord? baseline) ||
                baseline is null || !baseline.Acknowledged || fromRevision < baseline.Revision)
                return new DeltaProjectionResult(
                    ProjectionStatus.Rejected,
                    null,
                    ReplicationFailure.Rejected("SnapshotBaseMismatch", "Delta production requires an acknowledged baseline."));
            DeltaProjectionResult result = _projection.BuildDeltaCandidate(
                SessionId, ProductId, GameReleaseId, baseSnapshotId, fromRevision, toRevision,
                confirmationSequence, revision!, Mappings, tombstones!, gapDetected, resyncReason);
            if (!result.Succeeded) return result;
            requestKey ??= result.Delta!.IdempotencyKey;
            DeltaRecord deltaRecord = new DeltaRecord(
                    baseSnapshotId,
                    fromRevision,
                    toRevision,
                    result.Delta!.Sequence,
                    Encoding.UTF8.GetByteCount(result.Delta.BodyJson),
                    result.Delta.MappingSetHash,
                    checked((int)result.Delta.Revision.SchemaEpoch))
            {
                IdempotencyKey = requestKey,
                Projection = result.Delta,
            };
            DeltaHistoryStatus staged = _deltaHistory.Add(deltaRecord, current);
            if (staged is not (DeltaHistoryStatus.Accepted or DeltaHistoryStatus.Duplicate))
                return new DeltaProjectionResult(
                    ProjectionStatus.Retryable,
                    null,
                    ReplicationFailure.Retryable(
                        staged == DeltaHistoryStatus.QueueFull ? "QueueFull" : "InvalidArgument",
                        "Delta history could not be retained."));
            if (staged == DeltaHistoryStatus.Duplicate &&
                _deltaHistory.TryGetByIdempotencyKey(requestKey, out DeltaRecord? duplicateRecord) && duplicateRecord is not null)
            {
                if (duplicateRecord.Projection is not null)
                    return new DeltaProjectionResult(ProjectionStatus.Succeeded, duplicateRecord.Projection, null);
                return new DeltaProjectionResult(ProjectionStatus.Succeeded, result.Delta.WithSequence(duplicateRecord.Sequence), null);
            }
            if (!_projection.CommitDelta(result.Delta.Sequence, requestKey))
                return new DeltaProjectionResult(
                    ProjectionStatus.Retryable,
                    null,
                    ReplicationFailure.Retryable("QueueFull", "Delta replay could not be retained."));
            return result;
        }
    }

    public BaselineAckStatus AckDelta(
        string baseSnapshotId,
        ulong confirmationSequence,
        ulong confirmedRevision)
    {
        lock (_scope.Gate)
        {
            if (!IsOwnerThread || !IsContextUsableLocked() || _state != ReplicationContextState.Active)
                return BaselineAckStatus.UnknownBaseline;
            DeltaAckStatus result = _deltaHistory.Acknowledge(
                baseSnapshotId, confirmationSequence, confirmedRevision, _scope.CaptureLocked());
            return result switch
            {
                DeltaAckStatus.Acknowledged => BaselineAckStatus.Acknowledged,
                DeltaAckStatus.AlreadyAcknowledged => BaselineAckStatus.AlreadyAcknowledged,
                DeltaAckStatus.RevisionConflict => BaselineAckStatus.RevisionConflict,
                DeltaAckStatus.Invalid => BaselineAckStatus.Invalid,
                _ => BaselineAckStatus.UnknownBaseline,
            };
        }
    }

    public MappingBindingResult BindIdentity(EntityIdentity identity) =>
        BindIdentityCore(identity, null, default, tokenRequired: false);

    public MappingBindingResult BindIdentity(EntityIdentity identity, IdentityStoreToken token) =>
        BindIdentityCore(identity, null, token, tokenRequired: true);

    public MappingBindingResult BindIdentity(EntityIdentity identity, ulong currentRevision) =>
        BindIdentityCore(identity, currentRevision, default, tokenRequired: false);

    public MappingBindingResult BindIdentity(
        EntityIdentity identity,
        ulong currentRevision,
        IdentityStoreToken token) =>
        BindIdentityCore(identity, currentRevision, token, tokenRequired: true);

    private MappingBindingResult BindIdentityCore(
        EntityIdentity identity,
        ulong? currentRevision,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            string? errorId = ValidateCommandLocked(token, tokenRequired);
            if (errorId is not null)
                return MappingBindingResult.Rejected(errorId, "Replication context rejected identity work.");
            IdentityStoreToken current = _scope.CaptureLocked();
            return currentRevision.HasValue
                ? _identityStore.Bind(identity, currentRevision.Value, current)
                : _identityStore.Bind(identity, current);
        }
    }

    public MappingBindingResult DestroyIdentity(EntityIdentity identity) => BindIdentity(identity);

    public MappingBindingResult DestroyIdentity(EntityIdentity identity, IdentityStoreToken token) =>
        BindIdentity(identity, token);

    public bool DestroyIdentity(
        NetEntityId netEntityId,
        ulong destroyRevision,
        in TombstoneHorizonResult horizon) =>
        DestroyIdentityCore(netEntityId, destroyRevision, horizon, default, tokenRequired: false);

    public bool DestroyIdentity(
        NetEntityId netEntityId,
        ulong destroyRevision,
        in TombstoneHorizonResult horizon,
        IdentityStoreToken token) =>
        DestroyIdentityCore(netEntityId, destroyRevision, horizon, token, tokenRequired: true);

    private bool DestroyIdentityCore(
        NetEntityId netEntityId,
        ulong destroyRevision,
        in TombstoneHorizonResult horizon,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            return ValidateCommandLocked(token, tokenRequired) is null &&
                _identityStore.Remove(netEntityId, destroyRevision, horizon, _scope.CaptureLocked());
        }
    }

    public ProvisionalRemapResult AddProvisionalRemap(
        EntityIdentity provisional,
        EntityIdentity authoritative) =>
        AddProvisionalRemapCore(provisional, authoritative, default, tokenRequired: false);

    public ProvisionalRemapResult AddProvisionalRemap(
        EntityIdentity provisional,
        EntityIdentity authoritative,
        IdentityStoreToken token) =>
        AddProvisionalRemapCore(provisional, authoritative, token, tokenRequired: true);

    private ProvisionalRemapResult AddProvisionalRemapCore(
        EntityIdentity provisional,
        EntityIdentity authoritative,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            string? errorId = ValidateCommandLocked(token, tokenRequired);
            return errorId is null
                ? _provisionalRemapStore.Add(provisional, authoritative, _scope.CaptureLocked())
                : ProvisionalRemapResult.Rejected(errorId);
        }
    }

    public bool AddTombstone(NetEntityId id, ulong untilRevision) =>
        AddTombstoneCore(id, untilRevision, default, tokenRequired: false);

    public bool AddTombstone(NetEntityId id, ulong untilRevision, IdentityStoreToken token) =>
        AddTombstoneCore(id, untilRevision, token, tokenRequired: true);

    private bool AddTombstoneCore(
        NetEntityId id,
        ulong untilRevision,
        IdentityStoreToken token,
        bool tokenRequired)
    {
        lock (_scope.Gate)
        {
            return ValidateCommandLocked(token, tokenRequired) is null &&
                _tombstoneStore.Add(id, untilRevision, _scope.CaptureLocked());
        }
    }

    public int CollectTombstones(ulong revision, in TombstoneHorizonResult horizon)
    {
        lock (_scope.Gate)
        {
            return ValidateCommandLocked(default, tokenRequired: false) is null
                ? _tombstoneStore.Collect(revision, horizon, _scope.CaptureLocked())
                : 0;
        }
    }

    public int CollectTombstones(
        ulong revision,
        in TombstoneHorizonResult horizon,
        IdentityStoreToken token)
    {
        lock (_scope.Gate)
        {
            return ValidateCommandLocked(token, tokenRequired: true) is null
                ? _tombstoneStore.Collect(revision, horizon, _scope.CaptureLocked())
                : 0;
        }
    }

    public bool ReleaseTombstone(
        NetEntityId id,
        ulong currentRevision,
        in TombstoneHorizonResult horizon)
    {
        lock (_scope.Gate)
        {
            return ValidateCommandLocked(default, tokenRequired: false) is null &&
                _identityStore.ReleaseTombstone(id, currentRevision, horizon, _scope.CaptureLocked());
        }
    }

    public bool ReleaseTombstone(
        NetEntityId id,
        ulong currentRevision,
        in TombstoneHorizonResult horizon,
        IdentityStoreToken token)
    {
        lock (_scope.Gate)
        {
            return ValidateCommandLocked(token, tokenRequired: true) is null &&
                _identityStore.ReleaseTombstone(id, currentRevision, horizon, _scope.CaptureLocked());
        }
    }

    public bool TryAdvanceConnectionGeneration(ulong expectedGeneration, out ulong nextGeneration)
    {
        lock (_scope.Gate)
        {
            nextGeneration = _scope.ConnectionGeneration;
            if (!IsOwnerThread || _state is ReplicationContextState.Closed or ReplicationContextState.Faulted ||
                expectedGeneration != _scope.ConnectionGeneration || _scope.ConnectionGeneration == ulong.MaxValue)
                return false;
            ulong candidate = _scope.ConnectionGeneration + 1;
            try
            {
                ClearAllStoreDataLocked();
                ClearPendingBaselineLocked();
                _projection.ResetIdempotency();
                if (!_scope.TryAdvanceConnectionGenerationLocked(candidate))
                {
                    InvalidateStoresLocked();
                    _state = ReplicationContextState.Faulted;
                    return false;
                }
            }
            catch (Exception)
            {
                InvalidateStoresLocked();
                _state = ReplicationContextState.Faulted;
                return false;
            }

            nextGeneration = candidate;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_scope.Gate)
        {
            if (_disposed) return;
            CloseStoresLocked();
            _disposed = true;
            _state = ReplicationContextState.Closed;
        }
    }

    private ReplicationContextTransitionResult TransitionLocked(
        ReplicationContextState expected,
        ReplicationContextState next)
    {
        if (_state != expected || _scope.State != IdentityStoreState.Active)
            return ReplicationContextTransitionResult.Rejected(_state, "InvalidArgument");
        _state = next;
        return ReplicationContextTransitionResult.Accepted(_state);
    }

    private bool HasAcknowledgedBaselineForActivationLocked() =>
        _pendingBaselineId is not null && _baselineStore.IsAcknowledged(_pendingBaselineId);

    private void RegisterPendingBaselineLocked()
    {
        if (_pendingBaselineId is null) return;
        _deltaHistory.ResetForBaselineContextLocked(
            _pendingBaselineId,
            _pendingBaselineSequence,
            _pendingBaselineRevision);
    }

    private bool IsContextUsableLocked() =>
        _scope.State == IdentityStoreState.Active &&
        _state is not (ReplicationContextState.Closed or ReplicationContextState.Faulted or ReplicationContextState.Draining);

    private ReplicationFailure? ValidateMutationLocked(IdentityStoreToken token, bool tokenRequired)
    {
        if (!IsOwnerThread)
            return ReplicationFailure.Rejected("WrongContext", "Replication mutation requires the Simulation Owner Thread.");
        if (!IsContextUsableLocked())
            return ReplicationFailure.Rejected("ContextDestroyed", "Replication context is not usable.");
        string? errorId = ValidateTokenLocked(token, tokenRequired);
        return errorId is null
            ? null
            : ReplicationFailure.Rejected(errorId, "Replication work token is stale.");
    }

    private string? ValidateCommandLocked(IdentityStoreToken token, bool tokenRequired)
    {
        if (!IsOwnerThread) return "WrongContext";
        if (!IsContextUsableLocked()) return "ContextDestroyed";
        return ValidateTokenLocked(token, tokenRequired);
    }

    private string? ValidateTokenLocked(IdentityStoreToken token, bool tokenRequired)
    {
        if (!tokenRequired) return null;
        return _scope.ClassifyLocked(token) switch
        {
            ReplicationTokenStatus.Current => null,
            ReplicationTokenStatus.GenerationMismatch => "StaleConnectionGeneration",
            _ => "FencingTokenStale",
        };
    }

    private void ClearAllStoreDataLocked()
    {
        _baselineStore.ClearContextLocked();
        _deltaHistory.ClearContextLocked();
        _identityStore.ClearContextLocked();
        _provisionalRemapStore.ClearContextLocked();
        _tombstoneStore.ClearContextLocked();
    }

    private void InvalidateStoresLocked()
    {
        ClearAllStoreDataLocked();
        ClearPendingBaselineLocked();
        _projection.ResetIdempotency();
        _scope.TryTransitionTerminalLocked(close: false);
    }

    private void CloseStoresLocked()
    {
        ClearAllStoreDataLocked();
        ClearPendingBaselineLocked();
        _projection.ResetIdempotency();
        _scope.TryTransitionTerminalLocked(close: true);
    }

    private void ClearPendingBaselineLocked()
    {
        _pendingBaselineId = null;
        _pendingBaselineSequence = 0;
        _pendingBaselineRevision = 0;
        _lastAcknowledgedBaselineId = null;
        _lastAcknowledgedBaselineRevision = 0;
        _lastBaselineAckResult = default;
        _hasBaselineAckResult = false;
    }
}
