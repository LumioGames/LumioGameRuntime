using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace Lumio.GameRuntime.Replication.Projection;

public enum ProjectionBatchStatus
{
    Accepted,
    QueueFull,
    Invalid
}

public sealed class ProjectionBatch
{
    private const long ArrayCountPrefixBytes = sizeof(uint);
    private const long FieldLengthPrefixBytes = sizeof(uint);
    private readonly int _maxItems;
    private readonly long _maxBytes;
    private readonly List<ProjectionBlock> _blocks = new();
    private long _bytes;

    public ProjectionBatch(int maxItems, long maxBytes)
    {
        if (maxItems <= 0) throw new ArgumentOutOfRangeException(nameof(maxItems));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxItems = maxItems;
        _maxBytes = maxBytes;
    }

    public int Count => _blocks.Count;

    public long Bytes => _bytes;

    public int MaxItems => _maxItems;

    public long MaxBytes => _maxBytes;

    public IReadOnlyList<ProjectionBlock> Blocks
    {
        get
        {
            ProjectionBlock[] snapshot = _blocks
                .OrderBy(value => value.MappingId, StringComparer.Ordinal)
                .Select(Clone)
                .ToArray();
            return new ReadOnlyCollection<ProjectionBlock>(snapshot);
        }
    }

    public ProjectionBatchStatus Add(ProjectionBlock block)
    {
        if (block is null || !ReplicationValidation.IsIdentifier(block.MappingId) || block.Payload is null ||
            !ReplicationValidation.IsHash256(block.PayloadHash)) return ProjectionBatchStatus.Invalid;
        string actualHash = ReplicationValidation.Sha256Hex(block.Payload);
        if (!ReplicationValidation.ConstantTimeEquals(block.PayloadHash, actualHash)) return ProjectionBatchStatus.Invalid;
        if (_blocks.Any(value => value.MappingId == block.MappingId)) return ProjectionBatchStatus.Invalid;
        if (_blocks.Count >= _maxItems) return ProjectionBatchStatus.QueueFull;
        if (!TryGetSerializedBlockBytes(block, out long blockBytes)) return ProjectionBatchStatus.QueueFull;
        long batchPrefix = _blocks.Count == 0 ? ArrayCountPrefixBytes : 0;
        if (!TryAdd(_bytes, blockBytes, batchPrefix, out long nextBytes) || nextBytes > _maxBytes)
            return ProjectionBatchStatus.QueueFull;
        _blocks.Add(block with { Payload = (byte[])block.Payload.Clone() });
        _bytes = nextBytes;
        return ProjectionBatchStatus.Accepted;
    }

    private static bool TryGetSerializedBlockBytes(ProjectionBlock block, out long bytes)
    {
        bytes = 0;
        try
        {
            // LumioBinV1 encodes the block as a closed struct: each string/byte
            // member carries a u32 byte-length prefix and the enclosing array
            // contributes one u32 element-count prefix (added by Add).
            long mappingBytes = Encoding.UTF8.GetByteCount(block.MappingId);
            long payloadBytes = block.Payload.LongLength;
            long payloadHashBytes = Encoding.UTF8.GetByteCount(block.PayloadHash);
            bytes = checked(
                FieldLengthPrefixBytes + mappingBytes +
                FieldLengthPrefixBytes + payloadBytes +
                FieldLengthPrefixBytes + payloadHashBytes);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryAdd(long current, long blockBytes, long batchPrefix, out long result)
    {
        try
        {
            result = checked(current + blockBytes + batchPrefix);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static ProjectionBlock Clone(ProjectionBlock block) =>
        block with { Payload = (byte[])block.Payload.Clone() };
}

public sealed record ProjectionBlock(string MappingId, byte[] Payload, string PayloadHash)
{
    public static ProjectionBlock Create(string mappingId, byte[] payload) =>
        new(mappingId, (byte[])payload.Clone(), ReplicationValidation.Sha256Hex(payload));
}
