namespace Lumio.GameRuntime.Coordination;

public enum CrossWorldTxnState
{
    Created,
    Prepared,
    CommitIntent,
    Committed,
    Aborted,
    Expired,
    Indeterminate
}

public enum TxnParticipantKind
{
    VoxelCommit,
    EcsCommandBufferCommit
}

public readonly record struct TxnParticipantMarkers(
    TxnParticipantState VoxelCommit,
    TxnParticipantState EcsCommandBufferCommit);

public readonly record struct TxnTransitionResult(
    bool Succeeded,
    CrossWorldTxnState State,
    CoordinationFailure? Failure)
{
    public static TxnTransitionResult Success(CrossWorldTxnState state) => new(true, state, null);

    public static TxnTransitionResult Reject(CrossWorldTxnState state, string errorId, string detail) =>
        new(false, state, CoordinationFailure.Rejected(errorId, detail));
}

public enum TxnCommitStatus
{
    Committed,
    AlreadyCommitted,
    Retryable,
    Aborted,
    Expired,
    Indeterminate,
    Fatal
}

public enum TxnPrepareStatus
{
    Prepared,
    Rejected,
    Retryable,
    Fatal
}
