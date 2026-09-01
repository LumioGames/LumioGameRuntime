namespace Lumio.GameRuntime.Coordination;

/// <summary>
/// Runtime view of <c>cross-world-txn.schema.json</c> <c>participantMarkers.voxelCommit</c> /
/// <c>ecsCommandBufferCommit</c>. Wire names and declaration order match the schema enum
/// <c>NotStarted|Unknown|Applied|Failed</c>. Generated C# has no standalone enum type.
/// </summary>
public enum TxnParticipantState
{
    NotStarted,
    Unknown,
    Applied,
    Failed
}

public static class TxnParticipantStateWire
{
    public static string Value(TxnParticipantState value) => value switch
    {
        TxnParticipantState.NotStarted => "NotStarted",
        TxnParticipantState.Unknown => "Unknown",
        TxnParticipantState.Applied => "Applied",
        TxnParticipantState.Failed => "Failed",
        _ => string.Empty
    };

    public static bool TryParse(string value, out TxnParticipantState state)
    {
        foreach (TxnParticipantState candidate in GetValues())
        {
            if (string.Equals(Value(candidate), value, System.StringComparison.Ordinal))
            {
                state = candidate;
                return true;
            }
        }

        state = TxnParticipantState.Unknown;
        return false;
    }

#if NET10_0_OR_GREATER
    private static TxnParticipantState[] GetValues() => System.Enum.GetValues<TxnParticipantState>();
#else
    private static TxnParticipantState[] GetValues() => (TxnParticipantState[])System.Enum.GetValues(typeof(TxnParticipantState));
#endif
}
