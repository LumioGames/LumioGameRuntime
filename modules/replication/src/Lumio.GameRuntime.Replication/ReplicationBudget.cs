namespace Lumio.GameRuntime.Replication;

/// <summary>Root-namespace projection of the bounded replication budget.</summary>
public readonly record struct ReplicationBudget(
    int HistoryWindow,
    long HistoryBytes,
    int ProjectionItemLimit,
    long ProjectionBytes)
{
    public bool IsValid => HistoryWindow > 0 && HistoryBytes > 0 && ProjectionItemLimit > 0 && ProjectionBytes > 0;

    public int MaxHistoryEntries => HistoryWindow;
    public long MaxHistoryBytes => HistoryBytes;
    public int MaxProjectionItems => ProjectionItemLimit;
    public long MaxProjectionBytes => ProjectionBytes;

    public ReplicationBudget(int historyWindow, long historyBytes)
        : this(historyWindow, historyBytes, historyWindow, historyBytes)
    {
    }

    public static implicit operator History.ReplicationBudget(ReplicationBudget value) =>
        new(value.HistoryWindow, value.HistoryBytes, value.ProjectionItemLimit, value.ProjectionBytes);

    public static implicit operator ReplicationBudget(History.ReplicationBudget value) =>
        new(value.HistoryWindow, value.HistoryBytes, value.ProjectionItemLimit, value.ProjectionBytes);
}
