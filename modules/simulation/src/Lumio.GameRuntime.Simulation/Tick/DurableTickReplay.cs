using System;

namespace Lumio.GameRuntime.Simulation.Tick;

internal readonly record struct DurableTickReplayKey(string SessionId, ulong Epoch, ulong TickId)
{
    internal bool IsWellFormed =>
        SimulationValidation.IsIdentifier(SessionId) && Epoch != 0 && TickId != 0;
}

internal enum DurableTickReplayLookupStatus
{
    Missing,
    Found,
    Unavailable,
    Corrupt
}

internal enum DurableTickReplayWriteStatus
{
    Durable,
    Unavailable,
    Rejected,
    Corrupt
}

internal readonly record struct DurableTickReplayLookup(
    DurableTickReplayLookupStatus Status,
    DurableTickReplayRecord? Record);

internal interface IDurableTickReplayPort
{
    bool IsAvailable { get; }

    int RetentionCapacity { get; }

    DurableTickReplayLookup Lookup(in DurableTickReplayKey key);

    DurableTickReplayWriteStatus Persist(DurableTickReplayRecord record);
}

internal sealed class DurableTickReplayRecord
{
    internal DurableTickReplayRecord(DurableTickReplayKey key, TickRunResult result)
    {
        Key = key;
        Result = result?.SnapshotForPersistence()!;
    }

    internal DurableTickReplayKey Key { get; }

    internal TickRunResult Result { get; }

    internal bool IsWellFormedFor(in DurableTickReplayKey key)
    {
        if (!Key.IsWellFormed || Key != key || Result is null || Result.TickId != Key.TickId || !Result.IsCommitted)
            return false;
        if (!SimulationValidation.IsHash256(Result.RequestHashHex)) return false;
        if (Result.Status == TickRunStatus.Succeeded)
            return SimulationValidation.IsHash256(Result.StateHashHex) && Result.FirstFailure is null;
        if (Result.Status != TickRunStatus.PostCommitFaulted || Result.FirstFailure is null) return false;
        return string.IsNullOrEmpty(Result.StateHashHex) || SimulationValidation.IsHash256(Result.StateHashHex);
    }
}
