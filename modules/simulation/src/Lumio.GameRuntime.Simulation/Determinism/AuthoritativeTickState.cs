using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace Lumio.GameRuntime.Simulation.Determinism;

internal interface IAuthoritativeTickStatePort
{
    bool IsAvailable { get; }

    AuthoritativeTickStateSnapshot Capture(ulong tickId);
}

internal sealed class SimulationRevisionSnapshot
{
    private readonly IReadOnlyDictionary<string, ulong> _chunkRevisionSet;

    internal SimulationRevisionSnapshot(
        ulong tickId,
        ulong gameRevision,
        ulong voxelWorldRevision,
        IReadOnlyDictionary<string, ulong> chunkRevisionSet,
        ulong replicationRevision,
        ulong configRevision,
        ulong schemaEpoch)
    {
        TickId = tickId;
        GameRevision = gameRevision;
        VoxelWorldRevision = voxelWorldRevision;
        ReplicationRevision = replicationRevision;
        ConfigRevision = configRevision;
        SchemaEpoch = schemaEpoch;
        _chunkRevisionSet = new ReadOnlyDictionary<string, ulong>(
            chunkRevisionSet is null
                ? new Dictionary<string, ulong>(StringComparer.Ordinal)
                : new Dictionary<string, ulong>(chunkRevisionSet, StringComparer.Ordinal));
    }

    internal ulong TickId { get; }

    internal ulong GameRevision { get; }

    internal ulong VoxelWorldRevision { get; }

    internal IReadOnlyDictionary<string, ulong> ChunkRevisionSet => _chunkRevisionSet;

    internal ulong ReplicationRevision { get; }

    internal ulong ConfigRevision { get; }

    internal ulong SchemaEpoch { get; }

    internal string CanonicalValue
    {
        get
        {
            using var stream = new MemoryStream();
            WriteUInt64(stream, TickId);
            WriteUInt64(stream, GameRevision);
            WriteUInt64(stream, VoxelWorldRevision);
            WriteUInt64(stream, (ulong)_chunkRevisionSet.Count);
            foreach (KeyValuePair<string, ulong> chunk in _chunkRevisionSet.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                WriteString(stream, chunk.Key);
                WriteUInt64(stream, chunk.Value);
            }

            WriteUInt64(stream, ReplicationRevision);
            WriteUInt64(stream, ConfigRevision);
            WriteUInt64(stream, SchemaEpoch);
            return Convert.ToBase64String(stream.ToArray());
        }
    }

    internal bool IsWellFormed(ulong requestedTickId, int schemaEpoch, bool committed)
    {
        if (schemaEpoch < 0 || SchemaEpoch != (ulong)schemaEpoch) return false;
        if (committed ? TickId != requestedTickId : TickId > requestedTickId) return false;
        foreach (string chunkId in _chunkRevisionSet.Keys)
            if (!SimulationValidation.IsIdentifier(chunkId)) return false;
        return true;
    }

    internal SimulationRevisionSnapshot Snapshot() => new(
        TickId,
        GameRevision,
        VoxelWorldRevision,
        _chunkRevisionSet,
        ReplicationRevision,
        ConfigRevision,
        SchemaEpoch);

    internal static void WriteUInt64(Stream stream, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8) stream.WriteByte((byte)(value >> shift));
    }

    internal static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt64(stream, (ulong)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }
}

internal sealed class AuthoritativeTickStateSnapshot
{
    private readonly IReadOnlyList<string> _preparedTokens;
    private readonly IReadOnlyList<string> _participantTokens;

    internal AuthoritativeTickStateSnapshot(
        string gameReleaseId,
        string worldId,
        string configSnapshotId,
        string manifestHashHex,
        SimulationRevisionSnapshot revisions,
        string ecsHashHex,
        string commandHashHex,
        string coordinationHashHex,
        string voxelHashHex,
        string gasHashHex,
        string replicationHashHex,
        IReadOnlyList<string> preparedTokens,
        IReadOnlyList<string> participantTokens,
        string? snapshotId,
        string? noSnapshotReason)
    {
        GameReleaseId = gameReleaseId;
        WorldId = worldId;
        ConfigSnapshotId = configSnapshotId;
        ManifestHashHex = manifestHashHex;
        Revisions = revisions?.Snapshot()!;
        EcsHashHex = ecsHashHex;
        CommandHashHex = commandHashHex;
        CoordinationHashHex = coordinationHashHex;
        VoxelHashHex = voxelHashHex;
        GasHashHex = gasHashHex;
        ReplicationHashHex = replicationHashHex;
        _preparedTokens = CopyTokens(preparedTokens);
        _participantTokens = CopyTokens(participantTokens);
        SnapshotId = snapshotId;
        NoSnapshotReason = noSnapshotReason;
    }

    internal string GameReleaseId { get; }

    internal string WorldId { get; }

    internal string ConfigSnapshotId { get; }

    internal string ManifestHashHex { get; }

    internal SimulationRevisionSnapshot Revisions { get; }

    internal string EcsHashHex { get; }

    internal string CommandHashHex { get; }

    internal string CoordinationHashHex { get; }

    internal string VoxelHashHex { get; }

    internal string GasHashHex { get; }

    internal string ReplicationHashHex { get; }

    internal IReadOnlyList<string> PreparedTokens => _preparedTokens;

    internal IReadOnlyList<string> ParticipantTokens => _participantTokens;

    internal string? SnapshotId { get; }

    internal string? NoSnapshotReason { get; }

    internal bool IsWellFormed(ulong tickId, int schemaEpoch, bool committed)
    {
        if (!SimulationValidation.IsIdentifier(GameReleaseId) ||
            !SimulationValidation.IsIdentifier(WorldId) ||
            !SimulationValidation.IsIdentifier(ConfigSnapshotId) ||
            !SimulationValidation.IsHash256(ManifestHashHex) ||
            Revisions is null ||
            !Revisions.IsWellFormed(tickId, schemaEpoch, committed) ||
            !SimulationValidation.IsHash256(EcsHashHex) ||
            !SimulationValidation.IsHash256(CommandHashHex) ||
            !SimulationValidation.IsHash256(CoordinationHashHex) ||
            !SimulationValidation.IsHash256(VoxelHashHex) ||
            !SimulationValidation.IsHash256(GasHashHex) ||
            !SimulationValidation.IsHash256(ReplicationHashHex) ||
            !TokensAreWellFormed(_preparedTokens) ||
            !TokensAreWellFormed(_participantTokens))
        {
            return false;
        }

        bool hasSnapshot = SnapshotId is not null;
        bool hasNoSnapshotReason = NoSnapshotReason is not null;
        if (hasSnapshot == hasNoSnapshotReason) return false;
        if (hasSnapshot) return SimulationValidation.IsIdentifier(SnapshotId);
        return NoSnapshotReason is "PreFirstSnapshot" or "BootstrapFault" or "LoaderFailed";
    }

    internal bool HasSameIdentity(AuthoritativeTickStateSnapshot other) =>
        other is not null &&
        string.Equals(GameReleaseId, other.GameReleaseId, StringComparison.Ordinal) &&
        string.Equals(WorldId, other.WorldId, StringComparison.Ordinal) &&
        string.Equals(ConfigSnapshotId, other.ConfigSnapshotId, StringComparison.Ordinal) &&
        string.Equals(ManifestHashHex, other.ManifestHashHex, StringComparison.Ordinal);

    internal AuthoritativeTickStateSnapshot Snapshot() => new(
        GameReleaseId,
        WorldId,
        ConfigSnapshotId,
        ManifestHashHex,
        Revisions,
        EcsHashHex,
        CommandHashHex,
        CoordinationHashHex,
        VoxelHashHex,
        GasHashHex,
        ReplicationHashHex,
        _preparedTokens,
        _participantTokens,
        SnapshotId,
        NoSnapshotReason);

    private static IReadOnlyList<string> CopyTokens(IReadOnlyList<string> values)
    {
        if (values is null) return Array.Empty<string>();
        var copy = new string[values.Count];
        for (var index = 0; index < values.Count; index++) copy[index] = values[index];
        Array.Sort(copy, StringComparer.Ordinal);
        return new ReadOnlyCollection<string>(copy);
    }

    private static bool TokensAreWellFormed(IReadOnlyList<string> tokens)
    {
        string? previous = null;
        foreach (string token in tokens)
        {
            if (!SimulationValidation.IsIdentifier(token) || string.Equals(previous, token, StringComparison.Ordinal)) return false;
            previous = token;
        }

        return true;
    }
}
