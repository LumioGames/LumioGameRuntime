using System;
using System.Collections.Generic;
using Lumio.GameRuntime.GeneratedContracts;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Coordination;

public enum RevisionAdvanceStatus
{
    Advanced,
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

/// <summary>The sole owner of the session revision vector.</summary>
public sealed class SessionRevisionVectorStore
{
    private readonly object _gate = new();
    private SessionRevisionVectorView _current;

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

    public RevisionAdvanceResult TryAdvance(SessionRevisionVectorView next, bool committed)
    {
        if (next is null)
        {
            return Reject("InvalidArgument", "A revision vector is required.");
        }

        lock (_gate)
        {
            if (!committed)
            {
                return new RevisionAdvanceResult(
                    RevisionAdvanceStatus.Rejected,
                    _current,
                    CoordinationFailure.Rejected("InvalidArgument", "Only committed results may advance revisions."));
            }

            if (next.SchemaEpoch != _current.SchemaEpoch)
            {
                return new RevisionAdvanceResult(
                    RevisionAdvanceStatus.Fatal,
                    _current,
                    CoordinationFailure.Fatal("InternalInvariant", "Schema epoch cannot change in a session."));
            }

            if (!next.IsMonotonicFrom(_current))
            {
                return new RevisionAdvanceResult(
                    RevisionAdvanceStatus.Rejected,
                    _current,
                    CoordinationFailure.Rejected("RevisionConflict", "Revision vector would regress."));
            }

            _current = next;
            return new RevisionAdvanceResult(RevisionAdvanceStatus.Advanced, _current, null);
        }
    }

    public RevisionAdvanceResult AdvanceCommitted(SessionRevisionVectorView next) => TryAdvance(next, true);

    public RevisionAdvanceResult Advance(SessionRevisionVectorView next, bool committed = true) => TryAdvance(next, committed);

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
