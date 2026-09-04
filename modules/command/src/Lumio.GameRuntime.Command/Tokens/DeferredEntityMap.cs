using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Command;

/// <summary>Tick-scoped map from deferred tokens to committed local entity IDs.</summary>
public sealed class DeferredEntityMap
{
    private readonly Dictionary<DeferredEntityToken, string> _entries;

    public DeferredEntityMap(ulong tickId)
        : this(tickId, "default")
    {
    }

    public DeferredEntityMap(ulong tickId, string worldId, ulong bufferGeneration = 0UL)
    {
        if (string.IsNullOrWhiteSpace(worldId) || !CommandValidation.IsIdentifier(worldId))
            throw new ArgumentException("A valid world ID is required.", nameof(worldId));
        TickId = tickId;
        WorldId = worldId;
        BufferGeneration = bufferGeneration;
        _entries = new Dictionary<DeferredEntityToken, string>();
    }

    private DeferredEntityMap(ulong tickId, string worldId, ulong bufferGeneration, IDictionary<DeferredEntityToken, string> values)
    {
        TickId = tickId;
        WorldId = worldId;
        BufferGeneration = bufferGeneration;
        _entries = new Dictionary<DeferredEntityToken, string>(values);
    }

    public ulong TickId { get; }

    public string WorldId { get; }

    public ulong BufferGeneration { get; }

    public IReadOnlyDictionary<DeferredEntityToken, string> Values => _entries;

    public bool TryAdd(DeferredEntityToken token, string entityId, out string? generatedErrorId)
    {
        generatedErrorId = null;
        if (token.TickId != TickId || !string.Equals(token.WorldId, WorldId, StringComparison.Ordinal) ||
            (BufferGeneration != 0UL && token.BufferGeneration != 0UL && token.BufferGeneration != BufferGeneration) ||
            string.IsNullOrWhiteSpace(entityId))
        {
            generatedErrorId = "WrongContext";
            return false;
        }

        if (_entries.ContainsKey(token))
        {
            generatedErrorId = "InvalidArgument";
            return false;
        }

        _entries.Add(token, entityId);
        return true;
    }

    public bool TrySet(DeferredEntityToken token, string entityId, out string? generatedErrorId)
    {
        generatedErrorId = null;
        if (token.TickId != TickId || !string.Equals(token.WorldId, WorldId, StringComparison.Ordinal) ||
            (BufferGeneration != 0UL && token.BufferGeneration != 0UL && token.BufferGeneration != BufferGeneration) ||
            string.IsNullOrWhiteSpace(entityId))
        {
            generatedErrorId = "WrongContext";
            return false;
        }

        if (_entries.TryGetValue(token, out string? existing))
        {
            if (string.Equals(existing, entityId, StringComparison.Ordinal)) return true;
            generatedErrorId = "InvalidArgument";
            return false;
        }

        _entries.Add(token, entityId);
        return true;
    }

    public bool TryResolve(DeferredEntityToken token, ulong currentTick, out string? entityId)
    {
        entityId = null;
        if (currentTick != TickId || token.TickId != TickId || !string.Equals(token.WorldId, WorldId, StringComparison.Ordinal) ||
            (BufferGeneration != 0UL && token.BufferGeneration != 0UL && token.BufferGeneration != BufferGeneration)) return false;
        return _entries.TryGetValue(token, out entityId);
    }

    public DeferredEntityMap Snapshot() => new(TickId, WorldId, BufferGeneration, _entries);
}
