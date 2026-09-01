using System;

namespace Lumio.GameRuntime.Ecs;

internal sealed class QueryBatch
{
    internal QueryBatch(
        WorldId worldId,
        TickId tickId,
        uint epoch,
        in QuerySpec spec,
        LocalEntityId[] entities)
    {
        WorldId = worldId;
        TickId = tickId;
        Epoch = epoch;
        Spec = spec;
        Entities = entities;
    }

    public WorldId WorldId { get; }

    public TickId TickId { get; }

    public uint Epoch { get; }

    public QuerySpec Spec { get; }

    public ReadOnlyMemory<LocalEntityId> Entities { get; }

    public int Count => Entities.Length;
}
