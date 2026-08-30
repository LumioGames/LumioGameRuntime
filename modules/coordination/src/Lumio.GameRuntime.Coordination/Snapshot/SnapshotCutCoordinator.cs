using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Lumio.GameRuntime.Coordination;

public readonly record struct SnapshotCutRequest(
    string SnapshotId,
    ulong TickId,
    ulong SchemaEpoch,
    bool IsBarrier,
    bool SessionPaused = false);

public sealed class SnapshotCutView : IEquatable<SnapshotCutView>
{
    public SnapshotCutView(string snapshotId, ulong tickId, SessionRevisionVectorView revisions, ulong schemaEpoch)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) throw new ArgumentException("A snapshot ID is required.", nameof(snapshotId));
        SnapshotId = snapshotId;
        TickId = tickId;
        Revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        SchemaEpoch = schemaEpoch;
    }

    public string SnapshotId { get; }

    public ulong TickId { get; }

    public SessionRevisionVectorView Revisions { get; }

    public SessionRevisionVectorView RevisionVector => Revisions;

    public ulong SchemaEpoch { get; }

    public bool Equals(SnapshotCutView? other) =>
        other is not null && string.Equals(SnapshotId, other.SnapshotId, StringComparison.Ordinal) &&
        TickId == other.TickId && SchemaEpoch == other.SchemaEpoch && Revisions.Equals(other.Revisions);

    public override bool Equals(object? obj) => obj is SnapshotCutView other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(SnapshotId, TickId, SchemaEpoch, Revisions);
}

public readonly record struct SnapshotPinResult(
    bool Succeeded,
    string? GeneratedErrorId,
    CoordinationFailure? Failure)
{
    public static SnapshotPinResult Success() => new(true, null, null);

    public static SnapshotPinResult FailureResult(string errorId, string detail, bool retryable = false) =>
        new(false, errorId, retryable ? CoordinationFailure.Retryable(errorId, detail) : CoordinationFailure.Rejected(errorId, detail));
}

public interface ISnapshotCutParticipant
{
    string Name { get; }

    int Order => 0;

    SessionRevisionVectorView ReadRevision();

    SnapshotPinResult TryPin(SnapshotCutView cut);

    void ReleasePin(SnapshotCutView cut);
}

public readonly record struct SnapshotCutOpenResult(
    bool Opened,
    SnapshotCutLease? Lease,
    CoordinationFailure? Failure)
{
    public static SnapshotCutOpenResult Open(SnapshotCutLease lease) => new(true, lease, null);

    public static SnapshotCutOpenResult Reject(CoordinationFailure failure) => new(false, null, failure);
}

/// <summary>Opens a consistent cut only at an approved barrier and releases pins atomically.</summary>
public sealed class SnapshotCutCoordinator
{
    private readonly SessionRevisionVectorStore _revisions;
    private readonly ReadOnlyCollection<ISnapshotCutParticipant> _participants;

    public SnapshotCutCoordinator(SessionRevisionVectorStore revisions, IEnumerable<ISnapshotCutParticipant>? participants = null)
    {
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        var materialized = (participants ?? Array.Empty<ISnapshotCutParticipant>()).ToList();
        if (materialized.Any(participant => participant is null))
            throw new ArgumentException("Snapshot participants cannot contain null entries.", nameof(participants));
        if (materialized.Any(participant => string.IsNullOrWhiteSpace(participant.Name)))
            throw new ArgumentException("Snapshot participants require stable names.", nameof(participants));
        if (materialized.GroupBy(participant => participant.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Snapshot participant names must be unique.", nameof(participants));
        _participants = materialized
            .OrderBy(participant => participant.Order)
            .ThenBy(participant => participant.Name, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    public SnapshotCutOpenResult TryOpen(in SnapshotCutRequest request)
    {
        if (!request.IsBarrier && !request.SessionPaused)
            return SnapshotCutOpenResult.Reject(CoordinationFailure.Rejected("InvalidArgument", "Snapshot cuts require a barrier or paused session."));
        if (string.IsNullOrWhiteSpace(request.SnapshotId))
            return SnapshotCutOpenResult.Reject(CoordinationFailure.Rejected("InvalidArgument", "Snapshot ID is required."));

        SessionRevisionVectorView revisions = _revisions.Read();
        if (request.TickId != revisions.TickId || request.SchemaEpoch != revisions.SchemaEpoch)
            return SnapshotCutOpenResult.Reject(CoordinationFailure.Rejected("RevisionConflict", "Cut request does not match the current revision vector."));

        var view = new SnapshotCutView(request.SnapshotId, request.TickId, revisions, request.SchemaEpoch);
        var pinned = new List<ISnapshotCutParticipant>(_participants.Count);
        foreach (ISnapshotCutParticipant participant in _participants)
        {
            SessionRevisionVectorView participantRevision;
            try { participantRevision = participant.ReadRevision(); }
            catch (Exception ex)
            {
                ReleasePinned(view, pinned);
                return SnapshotCutOpenResult.Reject(CoordinationFailure.Retryable("QueueFull", ex.Message));
            }

            if (!revisions.Equals(participantRevision))
            {
                ReleasePinned(view, pinned);
                return SnapshotCutOpenResult.Reject(CoordinationFailure.Rejected("RevisionConflict", "Snapshot participants do not share one revision vector."));
            }

            SnapshotPinResult pin;
            try { pin = participant.TryPin(view); }
            catch (Exception ex)
            {
                ReleasePinned(view, pinned);
                return SnapshotCutOpenResult.Reject(CoordinationFailure.Infrastructure("PanicBoundary", ex.Message));
            }

            if (!pin.Succeeded)
            {
                ReleasePinned(view, pinned);
                return SnapshotCutOpenResult.Reject(pin.Failure ?? CoordinationFailure.Rejected(pin.GeneratedErrorId ?? "CapacityExceeded", "Snapshot pin failed."));
            }

            pinned.Add(participant);
        }

        return SnapshotCutOpenResult.Open(new SnapshotCutLease(view, pinned.AsReadOnly()));
    }

    public SnapshotCutOpenResult TryOpen(SnapshotCutRequest request) => TryOpen(in request);

    private static void ReleasePinned(SnapshotCutView view, List<ISnapshotCutParticipant> pinned)
    {
        for (int index = pinned.Count - 1; index >= 0; index--)
        {
            try { pinned[index].ReleasePin(view); }
            catch (Exception) { }
        }
    }
}
