using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Lumio.GameRuntime.Replication.Projection;

public enum ProjectionBatchStatus
{
    Accepted,
    QueueFull,
    Invalid
}

public sealed class ProjectionBatch
{
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

    public IReadOnlyList<ProjectionBlock> Blocks => new ReadOnlyCollection<ProjectionBlock>(_blocks.OrderBy(value => value.MappingId, StringComparer.Ordinal).ToArray());

    public ProjectionBatchStatus Add(ProjectionBlock block)
    {
        if (block is null || !ReplicationValidation.IsIdentifier(block.MappingId) || block.Payload is null) return ProjectionBatchStatus.Invalid;
        if (_blocks.Any(value => value.MappingId == block.MappingId)) return ProjectionBatchStatus.Invalid;
        if (_blocks.Count >= _maxItems || block.Payload.Length > _maxBytes - _bytes) return ProjectionBatchStatus.QueueFull;
        _blocks.Add(block with { Payload = (byte[])block.Payload.Clone() });
        _bytes += block.Payload.Length;
        return ProjectionBatchStatus.Accepted;
    }
}

public sealed record ProjectionBlock(string MappingId, byte[] Payload, string PayloadHash);
