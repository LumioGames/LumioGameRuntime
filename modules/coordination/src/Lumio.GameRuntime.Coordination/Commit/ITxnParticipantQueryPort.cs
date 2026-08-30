using System;

namespace Lumio.GameRuntime.Coordination;

public readonly record struct TxnParticipantQueryResult(
    TxnParticipantState State,
    bool Available,
    string? GeneratedErrorId,
    SessionRevisionVectorView? ResultRevision)
{
    public static TxnParticipantQueryResult Unknown(string errorId = "QueueFull") =>
        new(TxnParticipantState.Unknown, false, errorId, null);

    public static TxnParticipantQueryResult Applied(SessionRevisionVectorView? revision = null) =>
        new(TxnParticipantState.Applied, true, null, revision);
}

public interface ITxnParticipantQueryPort
{
    TxnParticipantQueryResult Query(string sessionId, string txnId, TxnParticipantKind participant);
}
