using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

internal sealed class ChangeSetBuilder
{
    private readonly List<ChangeEntry> _entries = new();
    private readonly WorldId _worldId;
    private readonly TickId _tickId;
    private readonly int _maxEntries;
    private bool _published;

    public ChangeSetBuilder(WorldId worldId, TickId tickId, int maxEntries)
    {
        if (worldId.IsDefault) throw new ArgumentOutOfRangeException(nameof(worldId));
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
#else
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
#endif
        _worldId = worldId;
        _tickId = tickId;
        _maxEntries = maxEntries;
    }

    public int Count => _entries.Count;

    public bool IsPublished => _published;

    public StorageOperationResult TryAppend(in ChangeEntry entry)
    {
        if (_published) return StorageOperationResult.Rejected(EcsErrorCodes.InvalidState);
        if (_entries.Count >= _maxEntries) return StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded);
        _entries.Add(new ChangeEntry(
            entry.Entity,
            entry.ComponentType,
            entry.Field,
            entry.CanonicalBefore,
            entry.CanonicalAfter));
        return StorageOperationResult.Accepted();
    }

    public ChangeSet Build()
    {
        _published = true;
        return new ChangeSet(_worldId, _tickId, _entries.ToArray());
    }
}
