using System;
using Lumio.GameRuntime.Coordination;
using Lumio.GameRuntime.Gas;

namespace Lumio.GameRuntime.Replication;

public sealed class ReplicationServices
{
    public ReplicationServices(ReplicationBudget budget)
    {
        Budget = budget;
    }

    public ReplicationBudget Budget { get; }

    public static bool IsPublishedAbilityType(AbilityTypeId typeId) => !typeId.IsDefault;

    public static ulong ReadReplicationRevision(SessionRevisionVectorView view)
    {
        if (view is null) throw new ArgumentNullException(nameof(view));
        return view.ReplicationRevision;
    }
}
