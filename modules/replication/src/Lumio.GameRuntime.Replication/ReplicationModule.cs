using Lumio.GameRuntime.Replication.Lifecycle;
using Lumio.GameRuntime.Replication.Mapping;

namespace Lumio.GameRuntime.Replication;

public sealed class ReplicationModule
{
    public ReplicationContext CreateContext(string sessionId, string productId, string gameReleaseId, MappingSetView mappings, ReplicationBudget budget, ulong connectionGeneration = 1) =>
        new(sessionId, productId, gameReleaseId, mappings, budget, connectionGeneration);

    public static ReplicationModule Create() => new();
}
