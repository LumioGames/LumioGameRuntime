using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Command;

namespace Lumio.GameRuntime.Coordination;

public readonly record struct GameReservationRequest(
    string SessionId,
    string TxnId,
    ulong TickId,
    PreparedGameDelta Delta);

public enum GameReservationStatus
{
    Reserved,
    Rejected,
    Retryable,
    Fatal
}

public readonly record struct GameReservationResult(
    GameReservationStatus Status,
    ReservationLease? Lease,
    CoordinationFailure? Failure)
{
    public bool Succeeded => Status == GameReservationStatus.Reserved && Lease is not null;
}

public interface IGameReservationPort
{
    GameReservationResult Reserve(in GameReservationRequest request);
}

internal readonly record struct VoxelPrepareRequest(
    string SessionId,
    string TxnId,
    ulong TickId,
    ulong DeadlineTick,
    ulong ExpectedVoxelRevision,
    IReadOnlyDictionary<string, ulong> ExpectedChunkRevisionSet,
    int SchemaEpoch,
    PreparedGameDelta Delta);

internal enum VoxelPrepareStatus
{
    Prepared,
    Rejected,
    Retryable,
    Fatal
}

internal readonly record struct VoxelPrepareResult(
    VoxelPrepareStatus Status,
    string? PreparedVoxelToken,
    PreparedVoxelTokenLease? Lease,
    CoordinationFailure? Failure)
{
    internal bool Succeeded => Status == VoxelPrepareStatus.Prepared && Lease is not null && PreparedVoxelToken is not null;

    internal static VoxelPrepareResult Prepared(string token, ulong deadlineTick, Action? release = null) =>
        new(VoxelPrepareStatus.Prepared, token, new PreparedVoxelTokenLease(token, deadlineTick, release), null);

    internal static VoxelPrepareResult Rejected(string errorId, string detail) =>
        new(VoxelPrepareStatus.Rejected, null, null, CoordinationFailure.Rejected(errorId, detail));

    internal static VoxelPrepareResult Retryable(string errorId, string detail) =>
        new(VoxelPrepareStatus.Retryable, null, null, CoordinationFailure.Retryable(errorId, detail));

    internal static VoxelPrepareResult Fatal(string errorId, string detail) =>
        new(VoxelPrepareStatus.Fatal, null, null, CoordinationFailure.Fatal(errorId, detail));
}

internal interface IVoxelWorldPort
{
    VoxelPrepareResult Prepare(in VoxelPrepareRequest request);

    VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request);

    VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request);

    VoxelParticipantQueryResult Query(string sessionId, string txnId);

    SessionRevisionVectorView ReadRevision();
}

internal readonly record struct VoxelCommitParticipantRequest(
    string SessionId,
    string TxnId,
    ulong TickId,
    string PreparedVoxelToken);

internal enum VoxelCommitParticipantStatus
{
    Applied,
    AlreadyApplied,
    Rejected,
    Indeterminate,
    Faulted
}

internal readonly record struct VoxelCommitParticipantResult(
    VoxelCommitParticipantStatus Status,
    SessionRevisionVectorView? ResultRevision,
    string? GeneratedErrorId)
{
    internal static VoxelCommitParticipantResult Applied(SessionRevisionVectorView? revision = null) =>
        new(VoxelCommitParticipantStatus.Applied, revision, null);

    internal static VoxelCommitParticipantResult AlreadyApplied(SessionRevisionVectorView? revision = null) =>
        new(VoxelCommitParticipantStatus.AlreadyApplied, revision, null);

    internal static VoxelCommitParticipantResult Rejected(string errorId) =>
        new(VoxelCommitParticipantStatus.Rejected, null, errorId);

    internal static VoxelCommitParticipantResult Indeterminate(string errorId = "PanicBoundary") =>
        new(VoxelCommitParticipantStatus.Indeterminate, null, errorId);

    internal static VoxelCommitParticipantResult Faulted(string errorId = "PanicBoundary") =>
        new(VoxelCommitParticipantStatus.Faulted, null, errorId);
}

internal readonly record struct VoxelAbortParticipantRequest(string SessionId, string TxnId, string? PreparedVoxelToken);

internal readonly record struct VoxelAbortParticipantResult(bool Succeeded, string? GeneratedErrorId);

internal readonly record struct VoxelParticipantQueryResult(
    TxnParticipantState State,
    bool Available,
    string? GeneratedErrorId,
    SessionRevisionVectorView? ResultRevision)
{
    internal static VoxelParticipantQueryResult Unavailable(string errorId = "QueueFull") =>
        new(TxnParticipantState.Unknown, false, errorId, null);
}

public readonly record struct TxnPrepareRequest(
    string SessionId,
    string TxnId,
    ulong TickId,
    string CommandId,
    ulong ExpectedGameRevision,
    ulong ExpectedVoxelRevision,
    IReadOnlyDictionary<string, ulong> ExpectedChunkRevisionSet,
    ulong DeadlineTick,
    int SchemaEpoch,
    PreparedGameDelta PreparedGameDelta,
    string RequestDigest,
    string? PredictionKey = null);

public sealed class CrossWorldPreparedTxn : IDisposable
{
    private readonly object _gate = new();
    private readonly ReservationBundle _reservations;
    private readonly Action<string>? _retire;
    private bool _commitInFlight;
    private bool _intentPersisted;
    private bool _disposed;

    internal CrossWorldPreparedTxn(
        TxnRecord record,
        ReservationLease gameReservation,
        PreparedVoxelTokenLease voxelReservation,
        Action<string>? retire = null)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        _reservations = new ReservationBundle(gameReservation, voxelReservation);
        _retire = retire;
        _intentPersisted = record.CommitIntentPersisted ||
            record.State is CrossWorldTxnState.CommitIntent or CrossWorldTxnState.Indeterminate or CrossWorldTxnState.Committed;
    }

    public TxnRecord Record { get; }

    internal ReservationLease GameReservation => _reservations.Game;

    internal PreparedVoxelTokenLease VoxelReservation => _reservations.Voxel;

    internal CoordinationFailure? ReleaseFailure { get; private set; }

    internal bool TryClaimForCommit(TxnIdentity identity, out CoordinationFailure? failure)
    {
        lock (_gate)
        {
            bool stateAllowed = !_intentPersisted
                ? Record.State == CrossWorldTxnState.Prepared && _reservations.IsActiveAt(Record.TickId)
                : (Record.State is CrossWorldTxnState.CommitIntent or CrossWorldTxnState.Indeterminate or CrossWorldTxnState.Committed) &&
                  (_reservations.IsActive || _reservations.IsCommitted);
            if (_disposed || _commitInFlight || !identity.Matches(Record) || !stateAllowed)
            {
                failure = CoordinationFailure.Rejected(
                    "InvalidArgument",
                    "Prepared transaction leases are inactive, terminal, or already claimed.");
                return false;
            }

            _commitInFlight = true;
            failure = null;
            return true;
        }
    }

    internal void MarkIntentPersisted()
    {
        lock (_gate) _intentPersisted = true;
    }

    internal void ReleaseCommitClaim()
    {
        lock (_gate) _commitInFlight = false;
    }

    internal bool CommitReservations()
    {
        lock (_gate) return _commitInFlight && _intentPersisted && _reservations.Commit();
    }

    public TxnTransitionResult Abort(string reason = "Cancelled")
    {
        TxnTransitionResult result;
        lock (_gate)
        {
            if (_commitInFlight || _intentPersisted)
                return TxnTransitionResult.Reject(Record.State, "QueueFull", "Commit authority already owns the prepared leases.");
            result = Record.Abort(reason);
            if (!result.Succeeded) return result;
            _disposed = true;
        }

        CoordinationFailure? releaseFailure = _reservations.Release();
        lock (_gate) ReleaseFailure = releaseFailure;
        _retire?.Invoke(Record.TxnId);
        return releaseFailure is null
            ? result
            : TxnTransitionResult.Reject(Record.State, "PanicBoundary", releaseFailure.Detail);
    }

    public TxnTransitionResult Expire()
    {
        TxnTransitionResult result;
        lock (_gate)
        {
            if (_commitInFlight || _intentPersisted)
                return TxnTransitionResult.Reject(Record.State, "QueueFull", "Commit authority already owns the prepared leases.");
            result = Record.Expire();
            if (!result.Succeeded) return result;
            _disposed = true;
        }

        CoordinationFailure? releaseFailure = _reservations.Expire();
        lock (_gate) ReleaseFailure = releaseFailure;
        _retire?.Invoke(Record.TxnId);
        return releaseFailure is null
            ? result
            : TxnTransitionResult.Reject(Record.State, "PanicBoundary", releaseFailure.Detail);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed || _commitInFlight || _intentPersisted) return;
            _disposed = true;
            if (Record.State is CrossWorldTxnState.Created or CrossWorldTxnState.Prepared)
                Record.Abort("Cancelled");
        }
        CoordinationFailure? releaseFailure = _reservations.Release();
        lock (_gate) ReleaseFailure = releaseFailure;
        _retire?.Invoke(Record.TxnId);
    }
}

public readonly record struct TxnPrepareResult(
    TxnPrepareStatus Status,
    CrossWorldPreparedTxn? Prepared,
    CoordinationFailure? Failure)
{
    public bool IsPrepared => Status == TxnPrepareStatus.Prepared && Prepared is not null;
}

/// <summary>Runs all CrossWorld preflight checks before acquiring reservations.</summary>
public sealed class TxnPrepareCoordinator
{
    private readonly object _gate = new();
    private readonly SessionRevisionVectorStore _revisions;
    private readonly CrossWorldCoordinator _transactions;
    private readonly IGameReservationPort _game;
    private readonly IVoxelWorldPort _voxel;
    private bool _acceptingPrepares = true;
    private bool _prepareInFlight;
    private readonly Dictionary<string, CrossWorldPreparedTxn> _preparedByTxn = new(StringComparer.Ordinal);

    public TxnPrepareCoordinator(
        SessionRevisionVectorStore revisions,
        CrossWorldCoordinator transactions)
        : this(revisions, transactions, null, null)
    {
    }

    internal TxnPrepareCoordinator(
        SessionRevisionVectorStore revisions,
        CrossWorldCoordinator transactions,
        IGameReservationPort? game,
        IVoxelWorldPort? voxel)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _game = game ?? new FailClosedGameReservationPort();
        _voxel = voxel ?? new FailClosedVoxelWorldPort();
    }

    public TxnPrepareResult Prepare(in TxnPrepareRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TxnId))
            return Rejected("InvalidArgument", "Transaction prepare request is malformed.");
        lock (_gate)
        {
            if (!_acceptingPrepares)
                return Rejected("ContextClosing", "Coordinator is draining and no new prepare is accepted.");
            if (_prepareInFlight)
                return new TxnPrepareResult(
                    TxnPrepareStatus.Retryable,
                    null,
                    CoordinationFailure.Retryable("QueueFull", "Another transaction prepare is in flight."));
            _prepareInFlight = true;
        }

        try
        {
            return PrepareCore(in request);
        }
        finally
        {
            lock (_gate) _prepareInFlight = false;
        }
    }

    private TxnPrepareResult PrepareCore(in TxnPrepareRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.TxnId) ||
            string.IsNullOrWhiteSpace(request.CommandId) || request.PreparedGameDelta is null)
        {
            return Rejected("InvalidArgument", "Transaction prepare request is malformed.");
        }

        TxnLookupResult existing = _transactions.Idempotency.Lookup(request.TxnId, request.RequestDigest);
        if (existing.Status == TxnLookupStatus.Conflict)
            return new TxnPrepareResult(TxnPrepareStatus.Fatal, null, existing.Failure);
        if (existing.Status == TxnLookupStatus.Duplicate)
        {
            if (existing.Record is not TxnRecord existingRecord ||
                !string.Equals(existingRecord.SessionId, request.SessionId, StringComparison.Ordinal) ||
                !string.Equals(existingRecord.CommandId, request.CommandId, StringComparison.Ordinal) ||
                existingRecord.TickId != request.TickId ||
                existingRecord.DeadlineTick != request.DeadlineTick ||
                !string.Equals(existingRecord.RequestDigest, request.RequestDigest, StringComparison.Ordinal) ||
                !string.Equals(existingRecord.PredictionKey, request.PredictionKey, StringComparison.Ordinal) ||
                existingRecord.ExpectedRevision.GameRevision != request.ExpectedGameRevision ||
                existingRecord.ExpectedRevision.VoxelWorldRevision != request.ExpectedVoxelRevision ||
                !ChunkRevisionsMatch(existingRecord.ExpectedRevision.ChunkRevisionSet, request.ExpectedChunkRevisionSet))
            {
                return new TxnPrepareResult(
                    TxnPrepareStatus.Fatal,
                    null,
                    CoordinationFailure.Fatal(
                        "InvalidArgument",
                        "A transaction ID was reused with a different full request identity."));
            }

            if (existing.Record?.PreparedGameDelta is PreparedGameDelta existingDelta &&
                !existingDelta.CanonicalDigest.Span.SequenceEqual(request.PreparedGameDelta.CanonicalDigest.Span))
            {
                return new TxnPrepareResult(
                    TxnPrepareStatus.Fatal,
                    null,
                    CoordinationFailure.Fatal("InvalidArgument", "A transaction ID was reused with a different prepared delta."));
            }

            CrossWorldPreparedTxn? prior;
            lock (_gate) _preparedByTxn.TryGetValue(request.TxnId, out prior);
            if (prior is not null)
            {
                if (prior.Record.State == CrossWorldTxnState.Prepared &&
                    prior.GameReservation.State == ReservationLeaseState.Active &&
                    prior.VoxelReservation.State == ReservationLeaseState.Active)
                    return new TxnPrepareResult(TxnPrepareStatus.Prepared, prior, null);

                lock (_gate) _preparedByTxn.Remove(request.TxnId);
                return new TxnPrepareResult(
                    TxnPrepareStatus.Rejected,
                    null,
                    CoordinationFailure.Rejected(
                        prior.Record.State == CrossWorldTxnState.Expired ? "TimedOut" : "InvalidArgument",
                        "The original transaction preparation is terminal or no longer owns active leases."));
            }

            return new TxnPrepareResult(
                TxnPrepareStatus.Rejected,
                null,
                CoordinationFailure.Rejected("InvalidArgument", "The original prepared capability was retired."));
        }

        SessionRevisionVectorView current = _revisions.Read();
        if (current.GameRevision != request.ExpectedGameRevision || current.VoxelWorldRevision != request.ExpectedVoxelRevision ||
            !ChunkRevisionsMatch(current.ChunkRevisionSet, request.ExpectedChunkRevisionSet))
        {
            return Rejected("RevisionConflict", "Expected revision does not match the session revision.");
        }

        if (request.SchemaEpoch != (int)current.SchemaEpoch || request.PreparedGameDelta.SchemaEpoch != request.SchemaEpoch)
        {
            return Rejected("ManifestUnsupportedVersion", "Schema epoch does not match the session.");
        }

        if (request.TickId > request.DeadlineTick)
        {
            return Rejected("TimedOut", "Transaction deadline has elapsed.");
        }

        if (!request.PreparedGameDelta.IsValid || request.PreparedGameDelta.TickId != request.TickId)
        {
            return Rejected("InvalidArgument", "Prepared game delta is not valid for this tick.");
        }

        GameReservationResult game;
        try
        {
            game = _game.Reserve(new GameReservationRequest(request.SessionId, request.TxnId, request.TickId, request.PreparedGameDelta));
        }
        catch (Exception ex)
        {
            return new TxnPrepareResult(TxnPrepareStatus.Fatal, null,
                CoordinationFailure.Infrastructure("PanicBoundary", ex.Message));
        }
        if (!game.Succeeded || game.Lease is null)
        {
            return new TxnPrepareResult(
                game.Status switch
                {
                    GameReservationStatus.Retryable => TxnPrepareStatus.Retryable,
                    GameReservationStatus.Fatal => TxnPrepareStatus.Fatal,
                    _ => TxnPrepareStatus.Rejected
                }, null, game.Failure ?? CoordinationFailure.Rejected("CapacityExceeded", "Game reservation failed."));
        }

        VoxelPrepareResult voxel;
        try
        {
            voxel = _voxel.Prepare(new VoxelPrepareRequest(
                request.SessionId,
                request.TxnId,
                request.TickId,
                request.DeadlineTick,
                request.ExpectedVoxelRevision,
                new Dictionary<string, ulong>(request.ExpectedChunkRevisionSet, StringComparer.Ordinal),
                request.SchemaEpoch,
                request.PreparedGameDelta));
        }
        catch (Exception ex)
        {
            return Rollback(
                game.Lease,
                null,
                new TxnPrepareResult(TxnPrepareStatus.Fatal, null,
                    CoordinationFailure.Infrastructure("PanicBoundary", ex.Message)));
        }

        if (!voxel.Succeeded || voxel.Lease is null || voxel.PreparedVoxelToken is null)
        {
            return Rollback(game.Lease, voxel.Lease, new TxnPrepareResult(
                voxel.Status switch
                {
                    VoxelPrepareStatus.Retryable => TxnPrepareStatus.Retryable,
                    VoxelPrepareStatus.Fatal => TxnPrepareStatus.Fatal,
                    _ => TxnPrepareStatus.Rejected
                }, null, voxel.Failure ?? CoordinationFailure.Rejected("CapacityExceeded", "Voxel reservation failed.")));
        }

        TxnBeginResult begin;
        try
        {
            begin = _transactions.Begin(new TxnRequest(
                request.SessionId,
                request.TxnId,
                request.TickId,
                request.CommandId,
                current,
                request.DeadlineTick,
                request.RequestDigest,
                request.PredictionKey,
                request.PreparedGameDelta));
        }
        catch (Exception ex)
        {
            return Rollback(game.Lease, voxel.Lease,
                new TxnPrepareResult(TxnPrepareStatus.Fatal, null,
                    CoordinationFailure.Infrastructure("PanicBoundary", ex.Message)));
        }
        if (!begin.Succeeded || begin.Record is null)
        {
            return Rollback(game.Lease, voxel.Lease,
                new TxnPrepareResult(TxnPrepareStatus.Fatal, null,
                    begin.Failure ?? CoordinationFailure.Fatal("InternalInvariant", "Unable to register transaction.")));
        }

        TxnRecord record = begin.Record;
        try
        {
            if (record.State == CrossWorldTxnState.Created)
            {
                record.AttachPreparedDelta(request.PreparedGameDelta, voxel.PreparedVoxelToken);
                TxnTransitionResult transition = record.TryTransition(CrossWorldTxnState.Prepared);
                if (!transition.Succeeded)
                {
                    return Rollback(game.Lease, voxel.Lease,
                        new TxnPrepareResult(TxnPrepareStatus.Fatal, null, transition.Failure));
                }
            }
        }
        catch (Exception ex)
        {
            return Rollback(game.Lease, voxel.Lease,
                new TxnPrepareResult(TxnPrepareStatus.Fatal, null,
                    CoordinationFailure.Infrastructure("PanicBoundary", ex.Message)));
        }

        CrossWorldPreparedTxn prepared = new(record, game.Lease, voxel.Lease, Retire);
        lock (_gate) _preparedByTxn[record.TxnId] = prepared;
        return new TxnPrepareResult(TxnPrepareStatus.Prepared, prepared, null);
    }

    public TxnPrepareResult Prepare(TxnPrepareRequest request) => Prepare(in request);

    public void StopPreparing()
    {
        lock (_gate) _acceptingPrepares = false;
    }

    public void ResumePreparing()
    {
        lock (_gate) _acceptingPrepares = true;
    }

    public bool AcceptingPrepares
    {
        get { lock (_gate) return _acceptingPrepares; }
    }

    public bool TryGetPrepared(string txnId, out CrossWorldPreparedTxn? prepared)
    {
        lock (_gate) return _preparedByTxn.TryGetValue(txnId, out prepared);
    }

    private void Retire(string txnId)
    {
        lock (_gate) _preparedByTxn.Remove(txnId);
    }

    public TxnTransitionResult Abort(string txnId, string reason = "Cancelled")
    {
        CrossWorldPreparedTxn? prepared;
        lock (_gate)
        {
            _preparedByTxn.TryGetValue(txnId, out prepared);
        }
        return prepared is null ? _transactions.Abort(txnId, reason) : prepared.Abort(reason);
    }

    public TxnTransitionResult Expire(string txnId)
    {
        CrossWorldPreparedTxn? prepared;
        lock (_gate)
        {
            _preparedByTxn.TryGetValue(txnId, out prepared);
        }
        return prepared is null ? _transactions.Expire(txnId) : prepared.Expire();
    }

    private static bool ChunkRevisionsMatch(IReadOnlyDictionary<string, ulong> left, IReadOnlyDictionary<string, ulong> right)
    {
        if (left.Count != right.Count) return false;
        foreach (KeyValuePair<string, ulong> entry in left)
        {
            if (!right.TryGetValue(entry.Key, out ulong value) || value != entry.Value) return false;
        }

        return true;
    }

    private static TxnPrepareResult Rejected(string errorId, string detail) =>
        new(TxnPrepareStatus.Rejected, null, CoordinationFailure.Rejected(errorId, detail));

    private static TxnPrepareResult Rollback(
        ReservationLease? game,
        PreparedVoxelTokenLease? voxel,
        TxnPrepareResult result)
    {
        CoordinationFailure? releaseFailure = ReservationBundle.Release(game, voxel);
        return releaseFailure is null
            ? result
            : new TxnPrepareResult(TxnPrepareStatus.Fatal, null, releaseFailure);
    }

    private sealed class FailClosedGameReservationPort : IGameReservationPort
    {
        public GameReservationResult Reserve(in GameReservationRequest request) =>
            new(GameReservationStatus.Fatal, null,
                CoordinationFailure.Infrastructure("CapabilityMissing", "A real game reservation participant is required."));
    }

    private sealed class FailClosedVoxelWorldPort : IVoxelWorldPort
    {
        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) =>
            VoxelPrepareResult.Fatal("CapabilityMissing", "A real Voxel participant is required.");

        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) =>
            VoxelCommitParticipantResult.Faulted("CapabilityMissing");

        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);

        public VoxelParticipantQueryResult Query(string sessionId, string txnId) =>
            VoxelParticipantQueryResult.Unavailable("CapabilityMissing");

        public SessionRevisionVectorView ReadRevision() =>
            new(0UL, 0UL, 0UL, new Dictionary<string, ulong>(), 0UL, 0UL, 1UL);
    }
}
