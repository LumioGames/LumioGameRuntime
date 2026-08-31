namespace Lumio.GameRuntime.Replication.Lifecycle;

public enum ReplicationContextState
{
    Created,
    Snapshotting,
    AwaitingBaselineAck,
    Active,
    Resyncing,
    Draining,
    Closed,
    Faulted
}

public readonly record struct ReplicationContextId(ulong Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct ReplicationContextTransitionResult(bool Succeeded, ReplicationContextState State, string? GeneratedErrorId)
{
    public static ReplicationContextTransitionResult Accepted(ReplicationContextState state) => new(true, state, null);

    public static ReplicationContextTransitionResult Rejected(ReplicationContextState state, string errorId) => new(false, state, errorId);
}
