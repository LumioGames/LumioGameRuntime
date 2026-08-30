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

public readonly record struct VoxelPrepareRequest(
    string SessionId,
    string TxnId,
    ulong TickId,
    ulong DeadlineTick,
    ulong ExpectedVoxelRevision,
    IReadOnlyDictionary<string, ulong> ExpectedChunkRevisionSet,
    int SchemaEpoch,
    PreparedGameDelta Delta);

public enum VoxelPrepareStatus
{
    Prepared,
    Rejected,
    Retryable,
    Fatal
}

public readonly record struct VoxelPrepareResult(
    VoxelPrepareStatus Status,
    string? PreparedVoxelToken,
    PreparedVoxelTokenLease? Lease,
    CoordinationFailure? Failure)
{
    public bool Succeeded => Status == VoxelPrepareStatus.Prepared && Lease is not null && PreparedVoxelToken is not null;

    public static VoxelPrepareResult Prepared(string token, ulong deadlineTick, Action? release = null) =>
        new(VoxelPrepareStatus.Prepared, token, new PreparedVoxelTokenLease(token, deadlineTick, release), null);

    public static VoxelPrepareResult Rejected(string errorId, string detail) =>
        new(VoxelPrepareStatus.Rejected, null, null, CoordinationFailure.Rejected(errorId, detail));

    public static VoxelPrepareResult Retryable(string errorId, string detail) =>
        new(VoxelPrepareStatus.Retryable, null, null, CoordinationFailure.Retryable(errorId, detail));

    public static VoxelPrepareResult Fatal(string errorId, string detail) =>
        new(VoxelPrepareStatus.Fatal, null, null, CoordinationFailure.Fatal(errorId, detail));
}

public interface IVoxelWorldPort
{
    VoxelPrepareResult Prepare(in VoxelPrepareRequest request);

    VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request);

    VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request);

    VoxelParticipantQueryResult Query(string sessionId, string txnId);

    SessionRevisionVectorView ReadRevision();
}

public readonly record struct VoxelCommitParticipantRequest(
    string SessionId,
    string TxnId,
    ulong TickId,
    string PreparedVoxelToken);

public enum VoxelCommitParticipantStatus
{
    Applied,
    AlreadyApplied,
    Rejected,
    Indeterminate,
    Faulted
}

public readonly record struct VoxelCommitParticipantResult(
    VoxelCommitParticipantStatus Status,
    SessionRevisionVectorView? ResultRevision,
    string? GeneratedErrorId)
{
    public static VoxelCommitParticipantResult Applied(SessionRevisionVectorView? revision = null) =>
        new(VoxelCommitParticipantStatus.Applied, revision, null);

    public static VoxelCommitParticipantResult AlreadyApplied(SessionRevisionVectorView? revision = null) =>
        new(VoxelCommitParticipantStatus.AlreadyApplied, revision, null);

    public static VoxelCommitParticipantResult Rejected(string errorId) =>
        new(VoxelCommitParticipantStatus.Rejected, null, errorId);

    public static VoxelCommitParticipantResult Indeterminate(string errorId = "PanicBoundary") =>
        new(VoxelCommitParticipantStatus.Indeterminate, null, errorId);

    public static VoxelCommitParticipantResult Faulted(string errorId = "PanicBoundary") =>
        new(VoxelCommitParticipantStatus.Faulted, null, errorId);
}

public readonly record struct VoxelAbortParticipantRequest(string SessionId, string TxnId, string? PreparedVoxelToken);

public readonly record struct VoxelAbortParticipantResult(bool Succeeded, string? GeneratedErrorId);

public readonly record struct VoxelParticipantQueryResult(
    TxnParticipantState State,
    bool Available,
    string? GeneratedErrorId,
    SessionRevisionVectorView? ResultRevision)
{
    public static VoxelParticipantQueryResult Unavailable(string errorId = "QueueFull") =>
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
    public CrossWorldPreparedTxn(TxnRecord record, ReservationLease gameReservation, PreparedVoxelTokenLease voxelReservation)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        GameReservation = gameReservation ?? throw new ArgumentNullException(nameof(gameReservation));
        VoxelReservation = voxelReservation ?? throw new ArgumentNullException(nameof(voxelReservation));
    }

    public TxnRecord Record { get; }

    public ReservationLease GameReservation { get; }

    public PreparedVoxelTokenLease VoxelReservation { get; }

    public TxnTransitionResult Abort(string reason = "Cancelled")
    {
        TxnTransitionResult result = Record.Abort(reason);
        if (result.Succeeded)
        {
            VoxelReservation.Release();
            GameReservation.Release();
        }

        return result;
    }

    public TxnTransitionResult Expire()
    {
        TxnTransitionResult result = Record.Expire();
        if (result.Succeeded)
        {
            VoxelReservation.Expire();
            GameReservation.Expire();
        }

        return result;
    }

    public void Dispose()
    {
        VoxelReservation.Dispose();
        GameReservation.Dispose();
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
    private readonly Dictionary<string, CrossWorldPreparedTxn> _preparedByTxn = new(StringComparer.Ordinal);

    public TxnPrepareCoordinator(
        SessionRevisionVectorStore revisions,
        CrossWorldCoordinator transactions,
        IGameReservationPort? game = null,
        IVoxelWorldPort? voxel = null)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _game = game ?? new NoOpGameReservationPort();
        _voxel = voxel ?? new NoOpVoxelWorldPort();
    }

    public TxnPrepareResult Prepare(in TxnPrepareRequest request)
    {
        // Prepare owns both reservation acquisition and the local idempotency
        // projection. Serialize the operation so duplicate callers cannot
        // acquire two leases for one transaction before either is published.
        lock (_gate)
        {
            return PrepareCore(in request);
        }
    }

    private TxnPrepareResult PrepareCore(in TxnPrepareRequest request)
    {
        if (!_acceptingPrepares)
            return Rejected("ContextClosing", "Coordinator is draining and no new prepare is accepted.");
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
            if (existing.Record?.PreparedGameDelta is PreparedGameDelta existingDelta &&
                !existingDelta.CanonicalDigest.Span.SequenceEqual(request.PreparedGameDelta.CanonicalDigest.Span))
            {
                return new TxnPrepareResult(
                    TxnPrepareStatus.Fatal,
                    null,
                    CoordinationFailure.Fatal("InvalidArgument", "A transaction ID was reused with a different prepared delta."));
            }

            if (_preparedByTxn.TryGetValue(request.TxnId, out CrossWorldPreparedTxn? prior))
                return new TxnPrepareResult(TxnPrepareStatus.Prepared, prior, null);
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
            game.Lease.Release();
            return new TxnPrepareResult(TxnPrepareStatus.Fatal, null, CoordinationFailure.Infrastructure("PanicBoundary", ex.Message));
        }

        if (!voxel.Succeeded || voxel.Lease is null || voxel.PreparedVoxelToken is null)
        {
            voxel.Lease?.Release();
            game.Lease.Release();
            return new TxnPrepareResult(
                voxel.Status switch
                {
                    VoxelPrepareStatus.Retryable => TxnPrepareStatus.Retryable,
                    VoxelPrepareStatus.Fatal => TxnPrepareStatus.Fatal,
                    _ => TxnPrepareStatus.Rejected
                }, null, voxel.Failure ?? CoordinationFailure.Rejected("CapacityExceeded", "Voxel reservation failed."));
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
            voxel.Lease.Release();
            game.Lease.Release();
            return new TxnPrepareResult(TxnPrepareStatus.Fatal, null,
                CoordinationFailure.Infrastructure("PanicBoundary", ex.Message));
        }
        if (!begin.Succeeded || begin.Record is null)
        {
            voxel.Lease.Release();
            game.Lease.Release();
            return new TxnPrepareResult(TxnPrepareStatus.Fatal, null, begin.Failure ?? CoordinationFailure.Fatal("InternalInvariant", "Unable to register transaction."));
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
                    voxel.Lease.Release();
                    game.Lease.Release();
                    return new TxnPrepareResult(TxnPrepareStatus.Fatal, null, transition.Failure);
                }
            }
        }
        catch (Exception ex)
        {
            voxel.Lease.Release();
            game.Lease.Release();
            return new TxnPrepareResult(TxnPrepareStatus.Fatal, null,
                CoordinationFailure.Infrastructure("PanicBoundary", ex.Message));
        }

        CrossWorldPreparedTxn prepared = new(record, game.Lease, voxel.Lease);
        _preparedByTxn[record.TxnId] = prepared;
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

    public TxnTransitionResult Abort(string txnId, string reason = "Cancelled")
    {
        lock (_gate)
        {
            if (!_preparedByTxn.TryGetValue(txnId, out CrossWorldPreparedTxn? prepared))
                return _transactions.Abort(txnId, reason);
            return prepared.Abort(reason);
        }
    }

    public TxnTransitionResult Expire(string txnId)
    {
        lock (_gate)
        {
            if (!_preparedByTxn.TryGetValue(txnId, out CrossWorldPreparedTxn? prepared))
                return _transactions.Expire(txnId);
            return prepared.Expire();
        }
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

    private sealed class NoOpGameReservationPort : IGameReservationPort
    {
        public GameReservationResult Reserve(in GameReservationRequest request) =>
            new(GameReservationStatus.Reserved, new ReservationLease(string.Concat("game:", request.TxnId)), null);
    }

    private sealed class NoOpVoxelWorldPort : IVoxelWorldPort
    {
        public VoxelPrepareResult Prepare(in VoxelPrepareRequest request) =>
            VoxelPrepareResult.Prepared(string.Concat("voxel:", request.TxnId), request.DeadlineTick);

        public VoxelCommitParticipantResult Commit(in VoxelCommitParticipantRequest request) => VoxelCommitParticipantResult.Applied();

        public VoxelAbortParticipantResult Abort(in VoxelAbortParticipantRequest request) => new(true, null);

        public VoxelParticipantQueryResult Query(string sessionId, string txnId) =>
            new(TxnParticipantState.NotStarted, true, null, null);

        public SessionRevisionVectorView ReadRevision() =>
            new(0UL, 0UL, 0UL, new Dictionary<string, ulong>(), 0UL, 0UL, 1UL);
    }
}
