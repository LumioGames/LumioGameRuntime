using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.GameRuntime.Simulation.Determinism;

/// <summary>Tick-scoped deterministic inputs. Wall time, thread identity and object identity are absent by design.</summary>
public sealed class DeterminismContext
{
    private readonly ulong _seed;
    private readonly ulong _tickId;
    private readonly int _schemaEpoch;

    public DeterminismContext(ulong seed, ulong tickId, int schemaEpoch)
        : this(string.Empty, string.Empty, string.Empty, tickId, seed, schemaEpoch, string.Empty)
    {
    }

    public DeterminismContext(
        string gameReleaseId,
        string sessionId,
        string worldId,
        ulong tickId,
        ulong seed,
        int schemaEpoch,
        string configSnapshotId)
    {
        if (schemaEpoch < 0) throw new ArgumentOutOfRangeException(nameof(schemaEpoch));
        if (gameReleaseId is null) throw new ArgumentNullException(nameof(gameReleaseId));
        if (sessionId is null) throw new ArgumentNullException(nameof(sessionId));
        if (worldId is null) throw new ArgumentNullException(nameof(worldId));
        if (configSnapshotId is null) throw new ArgumentNullException(nameof(configSnapshotId));
        GameReleaseId = gameReleaseId;
        SessionId = sessionId;
        WorldId = worldId;
        ConfigSnapshotId = configSnapshotId;
        _seed = seed;
        _tickId = tickId;
        _schemaEpoch = schemaEpoch;
    }

    public ulong Seed => _seed;

    public string GameReleaseId { get; }

    public string SessionId { get; }

    public string WorldId { get; }

    public string ConfigSnapshotId { get; }

    public ulong TickId => _tickId;

    public int SchemaEpoch => _schemaEpoch;

    public string TimeUnit => "LogicalTick";

    public DeterministicRngStream OpenRngStream(string streamId)
    {
        if (!SimulationValidation.IsIdentifier(streamId)) throw new ArgumentException("A valid stream ID is required.", nameof(streamId));
        byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(_seed.ToString(CultureInfo.InvariantCulture), ":", _tickId.ToString(CultureInfo.InvariantCulture), ":", _schemaEpoch.ToString(CultureInfo.InvariantCulture), ":", streamId));
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(bytes);
        ulong state = 0;
        for (var i = 0; i < 8; i++) state = (state << 8) | digest[i];
        return new DeterministicRngStream(state == 0 ? 1UL : state);
    }

    public string DeriveStreamSeed(string streamId) => OpenRngStream(streamId).InitialState.ToString("x16", CultureInfo.InvariantCulture);

    public DeterministicRngStream OpenStream(string streamId) => OpenRngStream(streamId);
}

public struct DeterministicRngStream
{
    private ulong _state;

    internal DeterministicRngStream(ulong state)
    {
        _state = state;
        InitialState = state;
    }

    public ulong InitialState { get; }

    public ulong NextUInt64()
    {
        unchecked
        {
            ulong value = _state;
            value ^= value << 7;
            value ^= value >> 9;
            value ^= value << 8;
            _state = value == 0 ? 1UL : value;
            return _state;
        }
    }

    public uint NextUInt32() => (uint)(NextUInt64() >> 32);
}
