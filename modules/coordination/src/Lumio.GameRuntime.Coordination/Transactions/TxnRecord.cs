using System;
using Lumio.GameRuntime.Command;

namespace Lumio.GameRuntime.Coordination;

/// <summary>Session-scoped transaction metadata and guarded state machine.</summary>
public class TxnRecord
{
    private readonly object _gate = new();
    private CrossWorldTxnState _state;
    private TxnParticipantState _voxelParticipant;
    private TxnParticipantState _ecsParticipant;

    public TxnRecord(
        string sessionId,
        string txnId,
        ulong tickId,
        string commandId,
        SessionRevisionVectorView expectedRevision,
        ulong deadlineTick,
        string requestDigest,
        string? predictionKey = null,
        PreparedGameDelta? preparedGameDelta = null,
        string gameReleaseId = "runtime")
    {
        if (!IsIdentifier(sessionId)) throw new ArgumentException("A valid session ID is required.", nameof(sessionId));
        if (!IsIdentifier(txnId)) throw new ArgumentException("A valid transaction ID is required.", nameof(txnId));
        if (!IsIdentifier(commandId)) throw new ArgumentException("A valid command ID is required.", nameof(commandId));
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(expectedRevision);
#else
        if (expectedRevision is null) throw new ArgumentNullException(nameof(expectedRevision));
#endif
        if (string.IsNullOrWhiteSpace(requestDigest)) throw new ArgumentException("A request digest is required.", nameof(requestDigest));

        SessionId = sessionId;
        TxnId = txnId;
        TickId = tickId;
        CommandId = commandId;
        ExpectedRevision = expectedRevision;
        DeadlineTick = deadlineTick;
        RequestDigest = requestDigest;
        GameReleaseId = string.IsNullOrWhiteSpace(gameReleaseId) ? "runtime" : gameReleaseId;
        PredictionKey = predictionKey;
        PreparedGameDelta = preparedGameDelta;
        _state = CrossWorldTxnState.Created;
        _voxelParticipant = TxnParticipantState.NotStarted;
        _ecsParticipant = TxnParticipantState.NotStarted;
    }

    public string SessionId { get; }

    public string TxnId { get; }

    public ulong TickId { get; }

    public string CommandId { get; }

    public string? PredictionKey { get; }

    public SessionRevisionVectorView ExpectedRevision { get; }

    public ulong DeadlineTick { get; }

    public string RequestDigest { get; }

    public string GameReleaseId { get; }

    public PreparedGameDelta? PreparedGameDelta { get; private set; }

    public string? PreparedVoxelToken { get; private set; }

    public SessionRevisionVectorView? ResultRevision { get; private set; }

    public SessionRevisionVectorView? ResultRevisionVector => ResultRevision;

    public string? AbortReason { get; private set; }

    public bool CommitIntentPersisted { get; private set; }

    public CommandBufferState CommandBufferState => PreparedGameDelta?.Batch.State ?? CommandBufferState.Open;

    public CrossWorldTxnState State
    {
        get { lock (_gate) return _state; }
    }

    public TxnParticipantState VoxelParticipant
    {
        get { lock (_gate) return _voxelParticipant; }
    }

    public TxnParticipantState EcsParticipant
    {
        get { lock (_gate) return _ecsParticipant; }
    }

    public TxnParticipantMarkers ParticipantMarkers => new(VoxelParticipant, EcsParticipant);

    public bool IsTerminal => State is CrossWorldTxnState.Committed or CrossWorldTxnState.Aborted or CrossWorldTxnState.Expired;

    internal void AttachPreparedDelta(PreparedGameDelta delta, string? preparedVoxelToken = null)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(delta);
#else
        if (delta is null) throw new ArgumentNullException(nameof(delta));
#endif
        lock (_gate)
        {
            if (_state != CrossWorldTxnState.Created) throw new InvalidOperationException("Prepared data can only be attached to a created transaction.");
            PreparedGameDelta = delta;
            PreparedVoxelToken = preparedVoxelToken;
        }
    }

    internal TxnTransitionResult TryTransition(CrossWorldTxnState next)
    {
        lock (_gate)
        {
            if (next is CrossWorldTxnState.CommitIntent or CrossWorldTxnState.Committed)
                return TxnTransitionResult.Reject(
                    _state,
                    "CapabilityMissing",
                    "Sensitive transaction transitions require a durable proof or commit certificate.");
            if (_state == next)
            {
                return TxnTransitionResult.Success(_state);
            }

            if (!IsAllowed(_state, next))
            {
                return TxnTransitionResult.Reject(_state, "InvalidArgument", string.Concat("Illegal transaction transition: ", _state, " -> ", next));
            }

            _state = next;
            return TxnTransitionResult.Success(_state);
        }
    }

    internal TxnTransitionResult MarkCommitIntentPersisted(TxnJournalProof proof)
    {
        if (proof is null || proof.Stage != TxnJournalStage.CommitIntent || !proof.Identity.Matches(this))
            return TxnTransitionResult.Reject(State, "EvidenceDigestMismatch", "Commit intent proof does not match the transaction.");
        lock (_gate)
        {
            if (_state == CrossWorldTxnState.CommitIntent)
            {
                CommitIntentPersisted = true;
                return TxnTransitionResult.Success(_state);
            }
            if (_state != CrossWorldTxnState.Prepared)
                return TxnTransitionResult.Reject(_state, "InvalidArgument", "Durable commit intent requires a prepared transaction.");
            CommitIntentPersisted = true;
            _state = CrossWorldTxnState.CommitIntent;
            return TxnTransitionResult.Success(_state);
        }
    }

    internal TxnTransitionResult Abort(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) reason = "ValidationFailed";
        lock (_gate)
        {
            if (_state == CrossWorldTxnState.Aborted) return TxnTransitionResult.Success(_state);
            if (!IsAllowed(_state, CrossWorldTxnState.Aborted))
                return TxnTransitionResult.Reject(_state, "InvalidArgument", "Abort is not legal after durable commit intent.");
            AbortReason = reason;
            _state = CrossWorldTxnState.Aborted;
            return TxnTransitionResult.Success(_state);
        }
    }

    internal TxnTransitionResult Expire() => TryTransition(CrossWorldTxnState.Expired);

    internal TxnTransitionResult Cancel() => Abort("Cancelled");

    internal TxnTransitionResult MarkParticipant(TxnParticipantKind participant, TxnParticipantState state)
    {
        lock (_gate)
        {
            if (_state is not (CrossWorldTxnState.CommitIntent or CrossWorldTxnState.Indeterminate or CrossWorldTxnState.Committed))
                return TxnTransitionResult.Reject(_state, "InvalidArgument", "Participant markers require durable commit intent.");

            if (participant is not (TxnParticipantKind.VoxelCommit or TxnParticipantKind.EcsCommandBufferCommit) ||
                state is not (TxnParticipantState.NotStarted or TxnParticipantState.Unknown or TxnParticipantState.Applied or TxnParticipantState.Failed))
            {
                return TxnTransitionResult.Reject(_state, "InvalidArgument", "Participant marker is malformed.");
            }

            TxnParticipantState current = participant == TxnParticipantKind.VoxelCommit
                ? _voxelParticipant
                : _ecsParticipant;
            if (!IsParticipantTransitionAllowed(current, state))
            {
                return TxnTransitionResult.Reject(_state, "InternalInvariant", "Participant marker cannot regress or change terminal outcome.");
            }

            if (participant == TxnParticipantKind.VoxelCommit) _voxelParticipant = state;
            else _ecsParticipant = state;
            return TxnTransitionResult.Success(_state);
        }
    }

    internal bool CanPublishCommitted(SessionRevisionVectorView revision, out CoordinationFailure? failure)
    {
        if (revision is null)
        {
            failure = CoordinationFailure.Rejected("InvalidArgument", "A result revision is required.");
            return false;
        }

        lock (_gate)
        {
            if (_state is CrossWorldTxnState.Aborted or CrossWorldTxnState.Expired ||
                _voxelParticipant == TxnParticipantState.Failed ||
                _ecsParticipant == TxnParticipantState.Failed)
            {
                failure = CoordinationFailure.Fatal("InternalInvariant", "Terminal or failed local state contradicts the commit certificate.");
                return false;
            }

            if (_state == CrossWorldTxnState.Committed && ResultRevision is not null && !ResultRevision.Equals(revision))
            {
                failure = CoordinationFailure.Fatal("RevisionConflict", "Committed result revision cannot be replaced.");
                return false;
            }

            if (revision.TickId != TickId || revision.SchemaEpoch != ExpectedRevision.SchemaEpoch ||
                revision.Equals(ExpectedRevision) || !revision.IsMonotonicFrom(ExpectedRevision))
            {
                failure = CoordinationFailure.Fatal("RevisionConflict", "Commit certificate result revision is invalid for the transaction.");
                return false;
            }

            failure = null;
            return true;
        }
    }

    internal TxnTransitionResult PublishCommitted(TxnCommitCertificate certificate)
    {
        if (certificate is null || !certificate.Operation.Identity.Matches(this) ||
            !certificate.Intent.Identity.Equals(certificate.Operation.Identity) ||
            !certificate.VoxelMarker.Identity.Equals(certificate.Operation.Identity) ||
            !certificate.EcsMarker.Identity.Equals(certificate.Operation.Identity) ||
            !certificate.Terminal.Identity.Equals(certificate.Operation.Identity) ||
            !certificate.Evidence.Matches(this) ||
            !certificate.Evidence.ResultRevision.Equals(certificate.ResultRevision))
        {
            return TxnTransitionResult.Reject(State, "EvidenceDigestMismatch", "Commit certificate does not match the transaction.");
        }

        if (!CanPublishCommitted(certificate.ResultRevision, out CoordinationFailure? failure))
            return TxnTransitionResult.Reject(State, failure!.GeneratedErrorId, failure.Detail);

        lock (_gate)
        {
            CommitIntentPersisted = true;
            _voxelParticipant = TxnParticipantState.Applied;
            _ecsParticipant = TxnParticipantState.Applied;
            ResultRevision = certificate.ResultRevision;
            _state = CrossWorldTxnState.Committed;
            return TxnTransitionResult.Success(_state);
        }
    }

    public bool TryReadParticipant(TxnParticipantKind participant, out TxnParticipantState state)
    {
        lock (_gate)
        {
            if (participant == TxnParticipantKind.VoxelCommit)
            {
                state = _voxelParticipant;
                return true;
            }

            if (participant == TxnParticipantKind.EcsCommandBufferCommit)
            {
                state = _ecsParticipant;
                return true;
            }

            state = TxnParticipantState.Unknown;
            return false;
        }
    }

    public Lumio.Gen.ContractTypes.TxnJournalRecord ToJournalRecord(
        Lumio.Gen.ContractTypes.TxnJournalRecordRecordKind kind,
        string idempotencyKey,
        ulong recordSequence = 0UL,
        string? previousHash = null,
        Lumio.Gen.ContractTypes.TxnJournalRecordCommitState commitState = Lumio.Gen.ContractTypes.TxnJournalRecordCommitState.Pending) =>
        TxnJournalRecordFactory.Create(
            SessionId,
            GameReleaseId,
            TickId,
            TxnId,
            kind,
            idempotencyKey,
            commitState,
            Lumio.Gen.ContractTypes.TxnJournalRecordDurabilityState.Durable,
            CommandId,
            recordSequence,
            previousHash);

    private static bool IsAllowed(CrossWorldTxnState current, CrossWorldTxnState next) =>
        (current, next) switch
        {
            (CrossWorldTxnState.Created, CrossWorldTxnState.Prepared) => true,
            (CrossWorldTxnState.Created, CrossWorldTxnState.Aborted) => true,
            (CrossWorldTxnState.Created, CrossWorldTxnState.Expired) => true,
            (CrossWorldTxnState.Prepared, CrossWorldTxnState.CommitIntent) => true,
            (CrossWorldTxnState.Prepared, CrossWorldTxnState.Aborted) => true,
            (CrossWorldTxnState.Prepared, CrossWorldTxnState.Expired) => true,
            (CrossWorldTxnState.CommitIntent, CrossWorldTxnState.Committed) => true,
            (CrossWorldTxnState.CommitIntent, CrossWorldTxnState.Indeterminate) => true,
            (CrossWorldTxnState.Indeterminate, CrossWorldTxnState.Committed) => true,
            _ => false
        };

    private static bool IsParticipantTransitionAllowed(TxnParticipantState current, TxnParticipantState next) =>
        current == next ||
        current == TxnParticipantState.NotStarted ||
        current == TxnParticipantState.Unknown && (next is TxnParticipantState.Applied or TxnParticipantState.Failed);

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        if (!(value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')) return false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-')) return false;
        }

        return true;
    }
}
