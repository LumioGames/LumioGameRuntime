using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Replication;

/// <summary>Immutable replication-side view of the generated SessionRevisionVector.</summary>
public sealed class RevisionVector : IEquatable<RevisionVector>
{
    private readonly IReadOnlyDictionary<string, ulong> _chunks;

    public RevisionVector(ulong tickId, ulong gameRevision, ulong voxelWorldRevision, ulong chunkRevision, ulong replicationRevision, ulong configRevision, ulong schemaEpoch)
        : this(tickId, gameRevision, voxelWorldRevision, new Dictionary<string, ulong>(StringComparer.Ordinal) { ["c:0:0:0"] = chunkRevision }, replicationRevision, configRevision, schemaEpoch)
    {
    }

    public RevisionVector(ulong tickId, ulong gameRevision, ulong voxelWorldRevision, IReadOnlyDictionary<string, ulong>? chunkRevisionSet, ulong replicationRevision, ulong configRevision, ulong schemaEpoch)
    {
        TickId = tickId;
        GameRevision = gameRevision;
        VoxelWorldRevision = voxelWorldRevision;
        _chunks = new ReadOnlyDictionary<string, ulong>(new Dictionary<string, ulong>(chunkRevisionSet ?? new Dictionary<string, ulong>(), StringComparer.Ordinal));
        ReplicationRevision = replicationRevision;
        ConfigRevision = configRevision;
        SchemaEpoch = schemaEpoch;
    }

    public RevisionVector(SessionRevisionVector generated)
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
    public IReadOnlyDictionary<string, ulong> ChunkRevisionSet => _chunks;
    public ulong ReplicationRevision { get; }
    public ulong ConfigRevision { get; }
    public ulong SchemaEpoch { get; }

    public bool IsValid => SchemaEpoch <= int.MaxValue &&
        SchemaEpoch == Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch &&
        _chunks.All(item => ReplicationValidation.IsChunkId(item.Key));

    public SessionRevisionVector ToGenerated() => new(TickId, GameRevision, VoxelWorldRevision, new Dictionary<string, ulong>(_chunks, StringComparer.Ordinal), ReplicationRevision, ConfigRevision, SchemaEpoch);

    public bool IsMonotonicFrom(RevisionVector previous)
    {
        if (previous is null || SchemaEpoch != previous.SchemaEpoch || TickId < previous.TickId || GameRevision < previous.GameRevision || VoxelWorldRevision < previous.VoxelWorldRevision || ReplicationRevision < previous.ReplicationRevision || ConfigRevision < previous.ConfigRevision) return false;
        foreach (KeyValuePair<string, ulong> item in previous._chunks)
            if (!_chunks.TryGetValue(item.Key, out ulong current) || current < item.Value) return false;
        return true;
    }

    public bool IsStrictlyAfter(RevisionVector previous)
    {
        if (!IsMonotonicFrom(previous)) return false;
        return TickId > previous.TickId ||
            GameRevision > previous.GameRevision ||
            VoxelWorldRevision > previous.VoxelWorldRevision ||
            ReplicationRevision > previous.ReplicationRevision ||
            ConfigRevision > previous.ConfigRevision ||
            _chunks.Any(item => !previous._chunks.TryGetValue(item.Key, out ulong oldValue) || oldValue != item.Value);
    }

    public bool Equals(RevisionVector? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || TickId != other.TickId || GameRevision != other.GameRevision || VoxelWorldRevision != other.VoxelWorldRevision || ReplicationRevision != other.ReplicationRevision || ConfigRevision != other.ConfigRevision || SchemaEpoch != other.SchemaEpoch || _chunks.Count != other._chunks.Count) return false;
        return _chunks.All(item => other._chunks.TryGetValue(item.Key, out ulong value) && value == item.Value);
    }

    public override bool Equals(object? obj) => obj is RevisionVector other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(TickId, GameRevision, VoxelWorldRevision, ReplicationRevision, ConfigRevision, SchemaEpoch, _chunks.Count);

}

public enum RevisionAdvanceStatus
{
    Advanced,
    AlreadyCurrent,
    Rejected,
    Fatal
}

public readonly record struct RevisionAdvanceResult(RevisionAdvanceStatus Status, RevisionVector Current, ReplicationFailure? Failure)
{
    public bool Succeeded => Status is RevisionAdvanceStatus.Advanced or RevisionAdvanceStatus.AlreadyCurrent;

    public bool IsIdempotent => Status == RevisionAdvanceStatus.AlreadyCurrent;
}

public sealed class AuthorityRevisionStore
{
    private readonly object _gate = new();
    private RevisionVector _current;

    public AuthorityRevisionStore(RevisionVector initial)
    {
        if (initial is null) throw new ArgumentNullException(nameof(initial));
        if (!initial.IsValid) throw new ArgumentOutOfRangeException(nameof(initial));
        _current = initial;
    }

    public RevisionVector Current => Read();

    public RevisionVector Read()
    {
        lock (_gate) return _current;
    }

    public RevisionAdvanceResult TryAdvance(RevisionVector next, bool committed = true)
    {
        if (next is null) return Reject("InvalidArgument", "Revision vector is required.");
        lock (_gate)
        {
            return TryAdvanceLocked(next, committed);
        }
    }

    public RevisionAdvanceResult Advance(RevisionVector next) => TryAdvance(next, true);

    public RevisionAdvanceResult TryAdvance(RevisionVector expectedCurrent, RevisionVector next, bool committed = true)
    {
        if (expectedCurrent is null) return Reject("InvalidArgument", "Expected revision vector is required.");
        lock (_gate)
        {
            if (!_current.Equals(expectedCurrent))
                return new RevisionAdvanceResult(RevisionAdvanceStatus.Rejected, _current, ReplicationFailure.Rejected("RevisionConflict", "Expected revision does not match current authority revision."));
            if (next is null)
                return new RevisionAdvanceResult(RevisionAdvanceStatus.Rejected, _current, ReplicationFailure.Rejected("InvalidArgument", "Revision vector is required."));
            return TryAdvanceLocked(next, committed);
        }
    }

    private RevisionAdvanceResult Reject(string id, string detail)
    {
        lock (_gate) return new RevisionAdvanceResult(RevisionAdvanceStatus.Rejected, _current, ReplicationFailure.Rejected(id, detail));
    }

    private RevisionAdvanceResult TryAdvanceLocked(RevisionVector next, bool committed)
    {
        if (!committed) return new RevisionAdvanceResult(RevisionAdvanceStatus.Rejected, _current, ReplicationFailure.Rejected("InvalidArgument", "Only committed results may advance revisions."));
        // Validate the complete candidate before comparing it with authority.
        // Otherwise an invalid extra chunk key can be ignored by the monotonic
        // comparison and become part of the authoritative vector.
        if (!next.IsValid)
        {
            if (next.SchemaEpoch != _current.SchemaEpoch ||
                next.SchemaEpoch > int.MaxValue ||
                next.SchemaEpoch != Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch)
                return new RevisionAdvanceResult(RevisionAdvanceStatus.Fatal, _current, ReplicationFailure.Fatal("StaleEpoch", "Schema epoch cannot change in a session."));
            return new RevisionAdvanceResult(RevisionAdvanceStatus.Rejected, _current, ReplicationFailure.Rejected("InvalidArgument", "Revision vector is malformed."));
        }
        if (next.SchemaEpoch != _current.SchemaEpoch) return new RevisionAdvanceResult(RevisionAdvanceStatus.Fatal, _current, ReplicationFailure.Fatal("StaleEpoch", "Schema epoch cannot change in a session."));
        if (!next.IsMonotonicFrom(_current)) return new RevisionAdvanceResult(RevisionAdvanceStatus.Rejected, _current, ReplicationFailure.Rejected("RevisionConflict", "Revision would regress."));
        if (next.Equals(_current)) return new RevisionAdvanceResult(RevisionAdvanceStatus.AlreadyCurrent, _current, null);
        _current = next;
        return new RevisionAdvanceResult(RevisionAdvanceStatus.Advanced, _current, null);
    }
}
