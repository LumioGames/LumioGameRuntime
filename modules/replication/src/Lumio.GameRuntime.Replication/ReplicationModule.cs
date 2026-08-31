using System;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;

namespace Lumio.GameRuntime.Replication;

public sealed class ReplicationModule
{
    public ReplicationContext CreateContext(string sessionId, string productId, string gameReleaseId, MappingSetView mappings, ReplicationBudget budget, ulong connectionGeneration = 1) =>
        new(sessionId, productId, gameReleaseId, mappings, budget, connectionGeneration);

    public static ReplicationModule Create() => new();
}

public sealed class ReplicationServices
{
    public ReplicationServices(ReplicationBudget budget)
    {
        Budget = budget;
    }

    public ReplicationBudget Budget { get; }
}
