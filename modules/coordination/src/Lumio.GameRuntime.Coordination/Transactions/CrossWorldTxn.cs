using Lumio.GameRuntime.Command;

namespace Lumio.GameRuntime.Coordination;

/// <summary>Named transaction projection matching the architecture binding; state is owned by TxnRecord.</summary>
public sealed class CrossWorldTxn : TxnRecord
{
    public CrossWorldTxn(
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
        : base(sessionId, txnId, tickId, commandId, expectedRevision, deadlineTick, requestDigest,
            predictionKey, preparedGameDelta, gameReleaseId)
    {
    }
}
