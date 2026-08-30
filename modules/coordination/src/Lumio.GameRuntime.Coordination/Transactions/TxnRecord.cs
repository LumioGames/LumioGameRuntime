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

    public void AttachPreparedDelta(PreparedGameDelta delta, string? preparedVoxelToken = null)
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

    public TxnTransitionResult TryTransition(CrossWorldTxnState next)
    {
        lock (_gate)
        {
            if (_state == next)
            {
                if (next == CrossWorldTxnState.CommitIntent) CommitIntentPersisted = true;
                return TxnTransitionResult.Success(_state);
            }

            if (!IsAllowed(_state, next))
            {
                return TxnTransitionResult.Reject(_state, "InvalidArgument", string.Concat("Illegal transaction transition: ", _state, " -> ", next));
            }

            if (next == CrossWorldTxnState.CommitIntent) CommitIntentPersisted = true;
            _state = next;
            return TxnTransitionResult.Success(_state);
        }
    }

    public TxnTransitionResult Transition(CrossWorldTxnState next) => TryTransition(next);

    public TxnTransitionResult MarkCommitIntentPersisted()
    {
        TxnTransitionResult result = TryTransition(CrossWorldTxnState.CommitIntent);
        return result;
    }

    public TxnTransitionResult Abort(string reason)
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

    public TxnTransitionResult Expire() => TryTransition(CrossWorldTxnState.Expired);

    public TxnTransitionResult Cancel() => Abort("Cancelled");

    public TxnTransitionResult MarkResultRevision(SessionRevisionVectorView revision)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(revision);
#else
        if (revision is null) throw new ArgumentNullException(nameof(revision));
#endif
        lock (_gate)
        {
            if (_state != CrossWorldTxnState.Committed)
                return TxnTransitionResult.Reject(_state, "InvalidArgument", "Result revision requires a committed transaction.");
            if (ResultRevision is not null)
            {
                return ResultRevision.Equals(revision)
                    ? TxnTransitionResult.Success(_state)
                    : TxnTransitionResult.Reject(_state, "RevisionConflict", "Committed result revision cannot be replaced.");
            }
            ResultRevision = revision;
            return TxnTransitionResult.Success(_state);
        }
    }

    public TxnTransitionResult MarkParticipant(TxnParticipantKind participant, TxnParticipantState state)
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
