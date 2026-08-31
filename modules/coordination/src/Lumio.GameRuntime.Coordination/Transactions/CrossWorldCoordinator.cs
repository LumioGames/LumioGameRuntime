using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Command;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Coordination;

public readonly record struct TxnRequest(
    string SessionId,
    string TxnId,
    ulong TickId,
    string CommandId,
    SessionRevisionVectorView ExpectedRevision,
    ulong DeadlineTick,
    string RequestDigest,
    string? PredictionKey = null,
    PreparedGameDelta? PreparedGameDelta = null)
{
    public TxnRequest(
        string sessionId,
        string txnId,
        ulong tickId,
        string commandId,
        SessionRevisionVector expectedRevision,
        ulong deadlineTick,
        string requestDigest,
        string? predictionKey = null,
        PreparedGameDelta? preparedGameDelta = null)
        : this(sessionId, txnId, tickId, commandId, new SessionRevisionVectorView(expectedRevision), deadlineTick,
            requestDigest, predictionKey, preparedGameDelta)
    {
    }
}

public readonly record struct CrossWorldTxnRequestView(
    string SessionId,
    string TxnId,
    ulong TickId,
    string CommandId,
    SessionRevisionVectorView ExpectedRevision,
    ulong DeadlineTick,
    string RequestDigest,
    string? PredictionKey = null,
    PreparedGameDelta? PreparedGameDelta = null)
{
    public TxnRequest ToRequest() => new(SessionId, TxnId, TickId, CommandId, ExpectedRevision, DeadlineTick,
        RequestDigest, PredictionKey, PreparedGameDelta);
}

public readonly record struct TxnId(string Value)
{
    public static implicit operator string(TxnId id) => id.Value;

    public static implicit operator TxnId(string value) => new(value);

    public override string ToString() => Value;
}

public readonly record struct TxnResolutionResult(
    TxnCommitStatus Status,
    TxnRecord? Record,
    CoordinationFailure? Failure)
{
    public bool Succeeded => Status is TxnCommitStatus.Committed or TxnCommitStatus.AlreadyCommitted;
}

public readonly record struct TxnBeginResult(
    TxnLookupStatus Status,
    TxnRecord? Record,
    CoordinationFailure? Failure)
{
    public bool Succeeded => Status is TxnLookupStatus.New or TxnLookupStatus.Duplicate;

    public bool IsDuplicate => Status == TxnLookupStatus.Duplicate;
}

/// <summary>Owns transaction records and delegates participant work to barrier coordinators.</summary>
public sealed class CrossWorldCoordinator : ICoordinationServices
{
    private readonly object _gate = new();
    private readonly TxnIdempotencyIndex _index;
    private readonly SessionRevisionVectorStore? _revisions;
    private readonly ITxnResultEvidencePort? _resultEvidence;
    private readonly TxnPrepareCoordinator? _prepareCoordinator;
    private readonly CommitIntentCoordinator? _commitCoordinator;
    private readonly SnapshotCutCoordinator? _snapshotCoordinator;
    private bool _accepting = true;
    private bool _acceptingCommits = true;

    public CrossWorldCoordinator(TxnIdempotencyIndex? index = null) => _index = index ?? new TxnIdempotencyIndex();

    /// <summary>Builds a fail-closed foundation composition; real participant ports are required for commit.</summary>
    public CrossWorldCoordinator(SessionRevisionVectorStore revisions, TxnIdempotencyIndex? index = null)
        : this(index)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _resultEvidence = new MissingTxnResultEvidencePort();
        var voxel = new FailClosedVoxelWorldPort(revisions);
        _prepareCoordinator = new TxnPrepareCoordinator(revisions, this, null, voxel);
        _commitCoordinator = new CommitIntentCoordinator(
            revisions,
            new InMemoryTxnJournalPort(),
            voxel,
            new EcsCommandCommitExecutor(),
            null,
            new MissingTxnResultEvidencePort());
        _snapshotCoordinator = new SnapshotCutCoordinator(revisions);
    }

    internal CrossWorldCoordinator(
        SessionRevisionVectorStore revisions,
        IGameReservationPort game,
        IVoxelWorldPort voxel,
        EcsCommandCommitExecutor ecs,
        ITxnJournalPort journal,
        IEcsCommandCommitRevisionPort ecsRevision,
        ITxnResultEvidencePort? resultEvidence = null,
        TxnIdempotencyIndex? index = null)
        : this(index)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _resultEvidence = resultEvidence ?? new MissingTxnResultEvidencePort();
        _prepareCoordinator = new TxnPrepareCoordinator(
            revisions,
            this,
            game ?? throw new ArgumentNullException(nameof(game)),
            voxel ?? throw new ArgumentNullException(nameof(voxel)));
        _commitCoordinator = new CommitIntentCoordinator(
            revisions,
            journal ?? throw new ArgumentNullException(nameof(journal)),
            voxel,
            ecs ?? throw new ArgumentNullException(nameof(ecs)),
            ecsRevision ?? throw new ArgumentNullException(nameof(ecsRevision)),
            resultEvidence ?? new MissingTxnResultEvidencePort());
        _snapshotCoordinator = new SnapshotCutCoordinator(revisions);
    }

    public CrossWorldCoordinator(
        SessionRevisionVectorStore revisions,
        TxnPrepareCoordinator prepareCoordinator,
        CommitIntentCoordinator commitCoordinator,
        SnapshotCutCoordinator? snapshotCoordinator = null,
        TxnIdempotencyIndex? index = null)
        : this(index)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _resultEvidence = null;
        _prepareCoordinator = prepareCoordinator ?? throw new ArgumentNullException(nameof(prepareCoordinator));
        _commitCoordinator = commitCoordinator ?? throw new ArgumentNullException(nameof(commitCoordinator));
        _snapshotCoordinator = snapshotCoordinator;
    }

    public TxnIdempotencyIndex Idempotency => _index;

    public SessionRevisionVectorView ReadRevision() => _revisions?.Read() ??
        throw new InvalidOperationException("Revision store is not configured.");

    public TxnPrepareResult PrepareTxn(in TxnPrepareRequest request)
    {
        lock (_gate)
        {
            if (!_accepting)
                return new TxnPrepareResult(
                    TxnPrepareStatus.Rejected,
                    null,
                    CoordinationFailure.Rejected("ContextClosing", "Coordinator is not accepting transaction prepares."));
        }
        return _prepareCoordinator is null
            ? new TxnPrepareResult(TxnPrepareStatus.Fatal, null, CoordinationFailure.Fatal("InternalInvariant", "Prepare coordinator is not configured."))
            : _prepareCoordinator.Prepare(in request);
    }

    internal void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
            _acceptingCommits = false;
        }
        _prepareCoordinator?.StopPreparing();
    }

    internal void StopPreparing()
    {
        lock (_gate) _accepting = false;
        _prepareCoordinator?.StopPreparing();
    }

    internal void ResumeAccepting()
    {
        lock (_gate)
        {
            _accepting = true;
            _acceptingCommits = true;
        }
        _prepareCoordinator?.ResumePreparing();
    }

    public bool Accepting
    {
        get { lock (_gate) return _accepting; }
    }

    public TxnCommitResult CommitTxn(CrossWorldPreparedTxn prepared)
    {
        lock (_gate)
        {
            if (!_acceptingCommits)
                return new TxnCommitResult(
                    TxnCommitStatus.Fatal,
                    prepared?.Record.VoxelParticipant ?? TxnParticipantState.NotStarted,
                    prepared?.Record.EcsParticipant ?? TxnParticipantState.NotStarted,
                    null,
                    Array.Empty<string>(),
                    CoordinationFailure.Rejected("ContextClosing", "Coordinator is not accepting transaction commits."),
                    prepared?.Record);
        }
        return _commitCoordinator is null
            ? new TxnCommitResult(TxnCommitStatus.Fatal, TxnParticipantState.NotStarted, TxnParticipantState.NotStarted,
                null, Array.Empty<string>(), CoordinationFailure.Fatal("InternalInvariant", "Commit coordinator is not configured."))
            : _commitCoordinator.Commit(prepared);
    }

    public TxnTransitionResult AbortTxn(string txnId, string reason) =>
        _prepareCoordinator?.Abort(txnId, reason) ?? Abort(txnId, reason);

    public SnapshotCutOpenResult BeginSnapshotCut(in SnapshotCutRequest request)
    {
        lock (_gate)
        {
            if (!_acceptingCommits)
                return SnapshotCutOpenResult.Reject(
                    CoordinationFailure.Rejected("ContextClosing", "Coordinator is not accepting snapshot cuts."));
        }
        return _snapshotCoordinator is null
            ? SnapshotCutOpenResult.Reject(CoordinationFailure.Fatal("InternalInvariant", "Snapshot coordinator is not configured."))
            : _snapshotCoordinator.TryOpen(in request);
    }

    public TxnBeginResult Begin(in TxnRequest request)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return new TxnBeginResult(TxnLookupStatus.Conflict, null,
                    CoordinationFailure.Rejected("ContextClosing", "Coordinator is draining."));
            }

            TxnRecord record;
            try
            {
                record = new TxnRecord(
                    request.SessionId,
                    request.TxnId,
                    request.TickId,
                    request.CommandId,
                    request.ExpectedRevision,
                    request.DeadlineTick,
                    request.RequestDigest,
                    request.PredictionKey,
                    request.PreparedGameDelta);
            }
            catch (ArgumentException ex)
            {
                return new TxnBeginResult(TxnLookupStatus.Conflict, null,
                    CoordinationFailure.Rejected("InvalidArgument", ex.Message));
            }

            TxnLookupResult result = _index.Register(record);
            return new TxnBeginResult(result.Status, result.Record, result.Failure);
        }
    }

    public TxnBeginResult Begin(TxnRequest request) => Begin(in request);

    public TxnBeginResult Begin(in CrossWorldTxnRequestView request) => Begin(request.ToRequest());

    public bool TryGet(string txnId, out TxnRecord? record) => _index.TryGet(txnId, out record);

    internal TxnTransitionResult Abort(string txnId, string reason) =>
        _index.TryGet(txnId, out TxnRecord? record) && record is not null
            ? record.Abort(reason)
            : TxnTransitionResult.Reject(CrossWorldTxnState.Created, "InvalidArgument", "Unknown transaction.");

    internal TxnTransitionResult Expire(string txnId) =>
        _index.TryGet(txnId, out TxnRecord? record) && record is not null
            ? record.Expire()
            : TxnTransitionResult.Reject(CrossWorldTxnState.Created, "InvalidArgument", "Unknown transaction.");

    public TxnRecord? Resolve(string txnId) => _index.TryGet(txnId, out TxnRecord? record) ? record : null;

    public TxnResolutionResult ResolveTxn(string txnId) =>
        _index.TryGet(txnId, out TxnRecord? record) && record is not null
            ? ResolveRecord(record)
            : new TxnResolutionResult(TxnCommitStatus.Fatal, null, CoordinationFailure.Rejected("InvalidArgument", "Unknown transaction."));

    public IReadOnlyList<TxnRecord> InFlight
    {
        get
        {
            var values = new List<TxnRecord>();
            foreach (TxnRecord record in _index.Snapshot())
            {
                if (!record.IsTerminal) values.Add(record);
            }

            return values.AsReadOnly();
        }
    }

    private TxnResolutionResult ResolveRecord(TxnRecord record)
    {
        if (record.State == CrossWorldTxnState.Committed && record.ResultRevision is not null &&
            record.VoxelParticipant == TxnParticipantState.Applied &&
            record.EcsParticipant == TxnParticipantState.Applied)
        {
            if (_resultEvidence is null)
                return new TxnResolutionResult(TxnCommitStatus.Indeterminate, record,
                    CoordinationFailure.Infrastructure("EvidenceMissing", "Durable result evidence is not configured."));
            TxnResultEvidenceReadResult evidence;
            TxnResultEvidenceIdentity identity = EvidenceIdentity(record);
            try { evidence = _resultEvidence.Read(in identity); }
            catch (Exception ex)
            {
                return new TxnResolutionResult(TxnCommitStatus.Indeterminate, record,
                    CoordinationFailure.Infrastructure("PanicBoundary", ex.Message));
            }
            if (!evidence.IsFound || evidence.Evidence is null || !evidence.Evidence.Matches(record) ||
                !evidence.Evidence.ResultRevision.Equals(record.ResultRevision) ||
                record.ResultRevision.Equals(record.ExpectedRevision) ||
                !record.ResultRevision.IsMonotonicFrom(record.ExpectedRevision))
                return new TxnResolutionResult(TxnCommitStatus.Indeterminate, record,
                    CoordinationFailure.Infrastructure(evidence.GeneratedErrorId ?? "EvidenceDigestMismatch",
                        "Committed result evidence is missing or inconsistent."));
            if (_revisions is not null && !_revisions.Read().Equals(record.ResultRevision))
                return new TxnResolutionResult(TxnCommitStatus.Indeterminate, record,
                    CoordinationFailure.Infrastructure("RevisionConflict",
                        "Only recovery authority may restore a committed result revision."));
            return new TxnResolutionResult(TxnCommitStatus.AlreadyCommitted, record, null);
        }

        return record.State switch
        {
            CrossWorldTxnState.Committed => new TxnResolutionResult(TxnCommitStatus.Indeterminate, record,
                CoordinationFailure.Infrastructure("RevisionConflict", "Committed transaction has no verified participant revision.")),
            CrossWorldTxnState.Aborted => new TxnResolutionResult(TxnCommitStatus.Aborted, record, null),
            CrossWorldTxnState.Expired => new TxnResolutionResult(TxnCommitStatus.Expired, record, null),
            CrossWorldTxnState.Indeterminate => new TxnResolutionResult(TxnCommitStatus.Indeterminate, record,
                CoordinationFailure.Infrastructure("PanicBoundary", "Participant state is not proven.")),
            _ => new TxnResolutionResult(TxnCommitStatus.Retryable, record,
                CoordinationFailure.Retryable("QueueFull", "Transaction is still pending."))
        };
    }

    private static TxnResultEvidenceIdentity EvidenceIdentity(TxnRecord record) =>
        new(record.SessionId, record.TxnId, record.CommandId, record.TickId,
            record.RequestDigest, record.ExpectedRevision.CanonicalDigestHex, record.GameReleaseId);

    private sealed class FailClosedVoxelWorldPort : IVoxelWorldPort
    {
        private readonly SessionRevisionVectorStore _revisions;

        public FailClosedVoxelWorldPort(SessionRevisionVectorStore revisions) => _revisions = revisions;

        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) =>
            VoxelPrepareResult.Fatal("CapabilityMissing", "A real Voxel participant is required.");

        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) =>
            VoxelCommitParticipantResult.Faulted("CapabilityMissing");

        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);

        public VoxelParticipantQueryResult Query(string sessionId, string txnId) =>
            VoxelParticipantQueryResult.Unavailable("CapabilityMissing");

        public SessionRevisionVectorView ReadRevision() => _revisions.Read();
    }
}
