using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Coordination;

/// <summary>Immutable, defensive view of the generated SessionRevisionVector contract.</summary>
public sealed class SessionRevisionVectorView : IEquatable<SessionRevisionVectorView>
{
    private readonly Dictionary<string, ulong> _chunkRevisionSet;
    private readonly ReadOnlyDictionary<string, ulong> _chunkRevisionView;
    private readonly byte[] _canonicalDigest;

    public SessionRevisionVectorView(
        ulong tickId,
        ulong gameRevision,
        ulong voxelWorldRevision,
        IReadOnlyDictionary<string, ulong>? chunkRevisionSet,
        ulong replicationRevision,
        ulong configRevision,
        ulong schemaEpoch)
    {
        TickId = tickId;
        GameRevision = gameRevision;
        VoxelWorldRevision = voxelWorldRevision;
        _chunkRevisionSet = new Dictionary<string, ulong>(StringComparer.Ordinal);
        if (chunkRevisionSet is not null)
        {
            foreach (KeyValuePair<string, ulong> entry in chunkRevisionSet)
            {
                if (!IsChunkId(entry.Key)) throw new ArgumentException("Invalid voxel chunk ID.", nameof(chunkRevisionSet));
                _chunkRevisionSet.Add(entry.Key, entry.Value);
            }
        }
        _chunkRevisionView = new ReadOnlyDictionary<string, ulong>(_chunkRevisionSet);
        ReplicationRevision = replicationRevision;
        ConfigRevision = configRevision;
        SchemaEpoch = schemaEpoch;
        _canonicalDigest = ComputeDigest();
    }

    public SessionRevisionVectorView(SessionRevisionVector generated)
        : this(
            generated?.TickId ?? throw new ArgumentNullException(nameof(generated)),
            generated.GameRevision,
            generated.VoxelWorldRevision,
            generated.ChunkRevisionSet,
            generated.ReplicationRevision,
            generated.ConfigRevision,
            generated.SchemaEpoch)
    {
    }

    public ulong TickId { get; }

    public ulong GameRevision { get; }

    public ulong VoxelWorldRevision { get; }

    public IReadOnlyDictionary<string, ulong> ChunkRevisionSet => _chunkRevisionView;

    public ulong ReplicationRevision { get; }

    public ulong ConfigRevision { get; }

    public ulong SchemaEpoch { get; }

    public ReadOnlyMemory<byte> CanonicalDigest => _canonicalDigest;

    public string CanonicalDigestHex => ToHex(_canonicalDigest);

    public SessionRevisionVector ToGenerated() => new(
        TickId,
        GameRevision,
        VoxelWorldRevision,
        new Dictionary<string, ulong>(_chunkRevisionSet, StringComparer.Ordinal),
        ReplicationRevision,
        ConfigRevision,
        SchemaEpoch);

    public bool IsMonotonicFrom(SessionRevisionVectorView previous)
    {
        if (previous is null || SchemaEpoch != previous.SchemaEpoch || TickId < previous.TickId ||
            GameRevision < previous.GameRevision || VoxelWorldRevision < previous.VoxelWorldRevision ||
            ReplicationRevision < previous.ReplicationRevision || ConfigRevision < previous.ConfigRevision)
        {
            return false;
        }

        foreach (KeyValuePair<string, ulong> entry in previous.ChunkRevisionSet)
        {
            if (!_chunkRevisionSet.TryGetValue(entry.Key, out ulong value) || value < entry.Value) return false;
        }

        return true;
    }

    public bool Equals(SessionRevisionVectorView? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || TickId != other.TickId || GameRevision != other.GameRevision ||
            VoxelWorldRevision != other.VoxelWorldRevision || ReplicationRevision != other.ReplicationRevision ||
            ConfigRevision != other.ConfigRevision || SchemaEpoch != other.SchemaEpoch ||
            _chunkRevisionSet.Count != other._chunkRevisionSet.Count) return false;

        foreach (KeyValuePair<string, ulong> entry in _chunkRevisionSet)
        {
            if (!other._chunkRevisionSet.TryGetValue(entry.Key, out ulong value) || value != entry.Value) return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is SessionRevisionVectorView other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(TickId, GameRevision, VoxelWorldRevision, ReplicationRevision, ConfigRevision, SchemaEpoch, _chunkRevisionSet.Count);

    public static SessionRevisionVectorView FromGenerated(SessionRevisionVector vector) => new(vector);

    // SHA-256 over UTF-8: tick|game|voxel|{chunkId=rev;}|repl|config|epoch
    // Chunk keys are ordinal-sorted. Not LumioBinV1 (LittleEndian binary profile).
    private byte[] ComputeDigest()
    {
        var builder = new StringBuilder();
        builder.Append(TickId).Append('|').Append(GameRevision).Append('|').Append(VoxelWorldRevision).Append('|');
        foreach (KeyValuePair<string, ulong> entry in _chunkRevisionSet.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.Append(entry.Key).Append('=').Append(entry.Value).Append(';');
        }

        builder.Append('|').Append(ReplicationRevision).Append('|').Append(ConfigRevision).Append('|').Append(SchemaEpoch);
#if NET10_0_OR_GREATER
        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
#else
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
#endif
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static bool IsChunkId(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("c:", StringComparison.Ordinal)) return false;
        string[] parts = value.Split(':');
        if (parts.Length != 4) return false;
        for (int index = 1; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length == 0 || part.Length > 11) return false;
            if (part[0] == '-')
            {
                if (part.Length == 1 || part[1] == '0' || !int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return false;
            }
            else
            {
                if ((part.Length > 1 && part[0] == '0') || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
            }
        }

        return true;
    }
}
