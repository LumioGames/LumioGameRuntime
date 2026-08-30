using System;

namespace Lumio.GameRuntime.Replication.History;

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
}
