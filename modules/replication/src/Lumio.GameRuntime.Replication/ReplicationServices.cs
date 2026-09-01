namespace Lumio.GameRuntime.Replication;

public sealed class ReplicationServices
{
    public ReplicationServices(ReplicationBudget budget)
    {
        Budget = budget;
    }

    public ReplicationBudget Budget { get; }
}
