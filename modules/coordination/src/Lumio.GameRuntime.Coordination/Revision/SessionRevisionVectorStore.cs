using System;
using Lumio.GameRuntime.GeneratedContracts;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Coordination;

public enum RevisionAdvanceStatus
{
    Advanced,
    Rejected,
    Fatal
}

public enum RevisionReservationStatus
{
    Reserved,
    Rejected,
    Fatal
}

public readonly record struct RevisionAdvanceResult(
    RevisionAdvanceStatus Status,
    SessionRevisionVectorView Current,
    CoordinationFailure? Failure)
{
    public bool Succeeded => Status == RevisionAdvanceStatus.Advanced;
}

internal readonly record struct RevisionReservationResult(
    RevisionReservationStatus Status,
    SessionRevisionVectorView Current,
    RevisionAdvanceReservation? Reservation,
    CoordinationFailure? Failure)
{
    internal bool Succeeded => Status == RevisionReservationStatus.Reserved && Reservation is not null;
}

internal sealed class RevisionAdvanceReservation
{
    private readonly SessionRevisionVectorStore _owner;
    private readonly TxnAuthorityOperation _operation;
    private readonly long _token;
    private int _state;

    internal RevisionAdvanceReservation(
        SessionRevisionVectorStore owner,
        TxnAuthorityOperation operation,
        long token,
        SessionRevisionVectorView expected,
        SessionRevisionVectorView next)
    {
        _owner = owner;
        _operation = operation;
        _token = token;
        IdentityDigest = operation.Identity.DigestHex;
        Expected = expected;
        Next = next;
    }

    internal string IdentityDigest { get; }

    internal SessionRevisionVectorView Expected { get; }

    internal SessionRevisionVectorView Next { get; }

    internal RevisionAdvanceResult Commit()
    {
        if (System.Threading.Volatile.Read(ref _state) == 1)
            return new RevisionAdvanceResult(RevisionAdvanceStatus.Advanced, Next, null);
        if (System.Threading.Volatile.Read(ref _state) == 2)
            return new RevisionAdvanceResult(
                RevisionAdvanceStatus.Rejected,
                Expected,
                CoordinationFailure.Rejected("RevisionConflict", "Revision reservation was released."));
        return _owner.CommitReservation(this, _operation);
    }

    internal void Release()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            _owner.ReleaseReservation(this);
    }

    internal long Token => _token;

    internal TxnAuthorityOperation Operation => _operation;

    internal bool TryMarkCommitted() =>
        System.Threading.Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal bool IsCommitted => System.Threading.Volatile.Read(ref _state) == 1;

}

/// <summary>Read-only publicly; mutation requires a live session authority operation.</summary>
public sealed class SessionRevisionVectorStore
{
    private readonly object _gate = new();
    private SessionRevisionVectorView _current;
    private RevisionAdvanceReservation? _reservation;
    private long _reservationToken;

    public SessionRevisionVectorStore(SessionRevisionVectorView initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public SessionRevisionVectorStore(SessionRevisionVector initial)
        : this(new SessionRevisionVectorView(initial))
    {
    }

    public SessionRevisionVectorView Read()
    {
        lock (_gate) return _current;
    }

    public SessionRevisionVectorView ReadView() => Read();

    public bool CompareExpected(SessionRevisionVectorView expected, out CoordinationFailure? failure)
    {
        if (expected is null)
        {
            failure = CoordinationFailure.Rejected("InvalidArgument", "Expected revision is required.");
            return false;
        }

        SessionRevisionVectorView current = Read();
        if (current.Equals(expected))
        {
            failure = null;
            return true;
        }

        failure = CoordinationFailure.Rejected("RevisionConflict", "Expected revision does not match current revision.");
        return false;
    }

    internal RevisionReservationResult TryReserveStrict(
        SessionRevisionVectorView expected,
        SessionRevisionVectorView next,
        TxnAuthorityOperation operation)
    {
        if (expected is null || next is null || operation is null || !operation.Owns(this))
            return ReservationReject("InvalidArgument", "A live authority operation and both revisions are required.");

        lock (_gate)
        {
            if (_reservation is not null)
            {
                if (ReferenceEquals(_reservation.Operation, operation) &&
                    string.Equals(_reservation.IdentityDigest, operation.Identity.DigestHex, StringComparison.Ordinal) &&
                    _reservation.Expected.Equals(expected) && _reservation.Next.Equals(next))
                {
                    return new RevisionReservationResult(
                        RevisionReservationStatus.Reserved,
                        _current,
                        _reservation,
                        null);
                }

                return new RevisionReservationResult(
                    RevisionReservationStatus.Rejected,
                    _current,
                    null,
                    CoordinationFailure.Retryable("RevisionConflict", "Another authority operation owns the revision reservation."));
            }

            if (!ValidateStrict(expected, next, operation.Identity, out RevisionAdvanceStatus status, out CoordinationFailure? failure))
            {
                return new RevisionReservationResult(
                    status == RevisionAdvanceStatus.Fatal ? RevisionReservationStatus.Fatal : RevisionReservationStatus.Rejected,
                    _current,
                    null,
                    failure);
            }

            long token;
            try { token = checked(++_reservationToken); }
            catch (OverflowException)
            {
                return new RevisionReservationResult(
                    RevisionReservationStatus.Fatal,
                    _current,
                    null,
                    CoordinationFailure.Fatal("InternalInvariant", "Revision reservation tokens were exhausted."));
            }

            _reservation = new RevisionAdvanceReservation(this, operation, token, expected, next);
            return new RevisionReservationResult(RevisionReservationStatus.Reserved, _current, _reservation, null);
        }
    }

    internal RevisionAdvanceResult RestoreCommitted(
        SessionRevisionVectorView expected,
        SessionRevisionVectorView result,
        TxnAuthorityOperation operation)
    {
        if (expected is null || result is null || operation is null || !operation.Owns(this))
            return Reject("InvalidArgument", "A live authority operation and both revisions are required.");

        lock (_gate)
        {
            if (_reservation is not null)
                return new RevisionAdvanceResult(
                    RevisionAdvanceStatus.Rejected,
                    _current,
                    CoordinationFailure.Retryable("RevisionConflict", "A revision transition is currently reserved."));

            if (!IdentityMatches(expected, result, operation.Identity, out CoordinationFailure? identityFailure))
                return new RevisionAdvanceResult(RevisionAdvanceStatus.Fatal, _current, identityFailure);

            if (result.Equals(expected) || !result.IsMonotonicFrom(expected))
                return new RevisionAdvanceResult(
                    RevisionAdvanceStatus.Rejected,
                    _current,
                    CoordinationFailure.Rejected("RevisionConflict", "Durable result must strictly advance the expected revision."));

            if (_current.Equals(result) || _current.IsMonotonicFrom(result))
                return new RevisionAdvanceResult(RevisionAdvanceStatus.Advanced, _current, null);

            if (!_current.Equals(expected))
                return new RevisionAdvanceResult(
                    RevisionAdvanceStatus.Rejected,
                    _current,
                    CoordinationFailure.Rejected("RevisionConflict", "Durable result does not start at the expected revision."));

            _current = result;
            return new RevisionAdvanceResult(RevisionAdvanceStatus.Advanced, _current, null);
        }
    }

    internal RevisionAdvanceResult CommitReservation(
        RevisionAdvanceReservation reservation,
        TxnAuthorityOperation operation)
    {
        if (reservation is null || operation is null || !operation.Owns(this))
            return Reject("InvalidArgument", "A live revision reservation authority is required.");

        lock (_gate)
        {
            if (_reservation is null || !ReferenceEquals(_reservation, reservation) ||
                !ReferenceEquals(reservation.Operation, operation) || _reservation.Token != reservation.Token ||
                !string.Equals(reservation.IdentityDigest, operation.Identity.DigestHex, StringComparison.Ordinal))
            {
                if (reservation.IsCommitted && _current.Equals(reservation.Next))
                    return new RevisionAdvanceResult(RevisionAdvanceStatus.Advanced, _current, null);
                return new RevisionAdvanceResult(
                    RevisionAdvanceStatus.Fatal,
                    _current,
                    CoordinationFailure.Fatal("InternalInvariant", "Revision reservation ownership was lost."));
            }

            if (!reservation.TryMarkCommitted())
            {
                return _current.Equals(reservation.Next)
                    ? new RevisionAdvanceResult(RevisionAdvanceStatus.Advanced, _current, null)
                    : new RevisionAdvanceResult(
                        RevisionAdvanceStatus.Fatal,
                        _current,
                        CoordinationFailure.Fatal("InternalInvariant", "Revision reservation was already completed."));
            }

            if (!_current.Equals(reservation.Expected))
            {
                _reservation = null;
                return new RevisionAdvanceResult(
                    RevisionAdvanceStatus.Rejected,
                    _current,
                    CoordinationFailure.Rejected("RevisionConflict", "Current revision changed while reserved."));
            }

            _current = reservation.Next;
            _reservation = null;
            return new RevisionAdvanceResult(RevisionAdvanceStatus.Advanced, _current, null);
        }
    }

    internal void ReleaseReservation(RevisionAdvanceReservation reservation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_reservation, reservation)) _reservation = null;
        }
    }

    private bool ValidateStrict(
        SessionRevisionVectorView expected,
        SessionRevisionVectorView next,
        TxnIdentity identity,
        out RevisionAdvanceStatus status,
        out CoordinationFailure? failure)
    {
        if (!_current.Equals(expected))
        {
            status = RevisionAdvanceStatus.Rejected;
            failure = CoordinationFailure.Rejected("RevisionConflict", "Expected revision does not match current revision.");
            return false;
        }

        if (!IdentityMatches(expected, next, identity, out failure))
        {
            status = RevisionAdvanceStatus.Fatal;
            return false;
        }

        if (next.Equals(expected) || !next.IsMonotonicFrom(expected))
        {
            status = RevisionAdvanceStatus.Rejected;
            failure = CoordinationFailure.Rejected("RevisionConflict", "A transaction must strictly advance its expected revision.");
            return false;
        }

        status = RevisionAdvanceStatus.Advanced;
        failure = null;
        return true;
    }

    private static bool IdentityMatches(
        SessionRevisionVectorView expected,
        SessionRevisionVectorView result,
        TxnIdentity identity,
        out CoordinationFailure? failure)
    {
        if (!string.Equals(expected.CanonicalDigestHex, identity.ExpectedRevisionDigest, StringComparison.Ordinal))
        {
            failure = CoordinationFailure.Fatal("EvidenceDigestMismatch", "Revision expectation is not bound to the transaction identity.");
            return false;
        }

        if (expected.SchemaEpoch != result.SchemaEpoch)
        {
            failure = CoordinationFailure.Fatal("InternalInvariant", "Schema epoch cannot change in a session.");
            return false;
        }

        if (result.TickId != identity.TickId)
        {
            failure = CoordinationFailure.Fatal("RevisionConflict", "Result revision TickId does not match the transaction.");
            return false;
        }

        failure = null;
        return true;
    }

    private RevisionReservationResult ReservationReject(string errorId, string detail) =>
        new(RevisionReservationStatus.Rejected, Read(), null, CoordinationFailure.Rejected(errorId, detail));

    private RevisionAdvanceResult Reject(string errorId, string detail)
    {
        lock (_gate)
        {
            return new RevisionAdvanceResult(
                RevisionAdvanceStatus.Rejected,
                _current,
                CoordinationFailure.Rejected(errorId, detail));
        }
    }
}
