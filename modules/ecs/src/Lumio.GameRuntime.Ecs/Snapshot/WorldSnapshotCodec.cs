using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Snapshot bytes: magic LWM1 + version + instanceId + nextCounter + tick + nextMessageId + nextRoomSequence
/// + live persist fields + tombstones. See modules/ecs/README.md.
/// </summary>
internal static class WorldSnapshotCodec
{
    internal const uint Version = 1;
    private static readonly byte[] Magic = { (byte)'L', (byte)'W', (byte)'M', (byte)'1' };

    internal static byte[] Capture(World world)
    {
        var entities = new List<EntityRecord>();
        for (int i = 0; i < world.CreationOrder.Count; i++)
        {
            if (!world.Entities.TryGetValue(world.CreationOrder[i], out EntityRecord? record)) continue;
            if (record.Presence == Presence.Live) entities.Add(record);
        }

        var tombstones = new List<NetEntityId>(world.Tombstones);
        tombstones.Sort();

        var chunks = new List<byte[]>();
        chunks.Add(Magic);
        chunks.Add(U32(Version));
        chunks.Add(U64(world.InstanceId));
        chunks.Add(U64(world.NextCounter));
        chunks.Add(U64(world.Tick));
        chunks.Add(U64(world.NextMessageId));
        chunks.Add(U64(world.NextRoomSequence));
        chunks.Add(U32((uint)entities.Count));
        for (int i = 0; i < entities.Count; i++)
        {
            EntityRecord record = entities[i];
            chunks.Add(U64(record.Id.Counter));
            chunks.Add(Str(world.Registry.WireName(record.EntityType)));
            var fields = new List<KeyValuePair<string, FieldBlob>>();
            var writer = new SnapshotWriter(fields);
            for (int c = 0; c < record.Components.Length; c++)
                EcsRegistry.Generated(record.Components[c])?.CapturePersist(writer);
            chunks.Add(U32((uint)fields.Count));
            for (int f = 0; f < fields.Count; f++)
            {
                chunks.Add(Str(fields[f].Key));
                chunks.Add(new[] { fields[f].Value.Tag });
                chunks.Add(fields[f].Value.Bytes);
            }
        }

        chunks.Add(U32((uint)tombstones.Count));
        for (int i = 0; i < tombstones.Count; i++)
            chunks.Add(U64(tombstones[i].Counter));

        int size = 0;
        for (int i = 0; i < chunks.Count; i++) size += chunks[i].Length;
        byte[] dest = new byte[size];
        int offset = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            Buffer.BlockCopy(chunks[i], 0, dest, offset, chunks[i].Length);
            offset += chunks[i].Length;
        }

        return dest;
    }

    internal static SnapshotHeader Read(ReadOnlyMemory<byte> bytes, out List<SnapshotEntity> entities, out List<ulong> tombstoneCounters)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        int offset = 0;
        if (span.Length < 4 || span[0] != Magic[0] || span[1] != Magic[1] || span[2] != Magic[2] || span[3] != Magic[3])
            throw new InvalidOperationException("Snapshot magic is not LWM1.");
        offset += 4;
        uint version = ReadU32(span, ref offset);
        if (version != Version) throw new InvalidOperationException("Unsupported snapshot version.");
        ulong instanceId = ReadU64(span, ref offset);
        ulong nextCounter = ReadU64(span, ref offset);
        ulong tick = ReadU64(span, ref offset);
        ulong nextMessageId = ReadU64(span, ref offset);
        ulong nextRoomSequence = ReadU64(span, ref offset);
        uint entityCount = ReadU32(span, ref offset);
        entities = new List<SnapshotEntity>((int)entityCount);
        for (uint i = 0; i < entityCount; i++)
        {
            ulong counter = ReadU64(span, ref offset);
            string typeName = ReadStr(span, ref offset);
            uint fieldCount = ReadU32(span, ref offset);
            var fields = new Dictionary<string, FieldBlob>(StringComparer.Ordinal);
            for (uint f = 0; f < fieldCount; f++)
            {
                string id = ReadStr(span, ref offset);
                byte tag = span[offset++];
                byte[] payload = ReadBlob(span, ref offset, tag);
                fields[id] = new FieldBlob(tag, payload);
            }

            entities.Add(new SnapshotEntity(counter, typeName, fields));
        }

        uint tombCount = ReadU32(span, ref offset);
        tombstoneCounters = new List<ulong>((int)tombCount);
        for (uint i = 0; i < tombCount; i++)
            tombstoneCounters.Add(ReadU64(span, ref offset));

        return new SnapshotHeader(instanceId, nextCounter, tick, nextMessageId, nextRoomSequence);
    }

    private static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] U64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Str(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
        byte[] bytes = new byte[4 + utf8.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, bytes, 4, utf8.Length);
        return bytes;
    }

    private static uint ReadU32(ReadOnlySpan<byte> span, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static ulong ReadU64(ReadOnlySpan<byte> span, ref int offset)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, 8));
        offset += 8;
        return value;
    }

    private static string ReadStr(ReadOnlySpan<byte> span, ref int offset)
    {
        int length = (int)ReadU32(span, ref offset);
        string value = Encoding.UTF8.GetString(span.Slice(offset, length));
        offset += length;
        return value;
    }

    private static byte[] ReadBlob(ReadOnlySpan<byte> span, ref int offset, byte tag)
    {
        if (tag == 1)
        {
            int length = (int)ReadU32(span, ref offset);
            byte[] bytes = span.Slice(offset, length).ToArray();
            offset += length;
            return bytes;
        }

        if (tag == 2)
        {
            byte[] bytes = span.Slice(offset, 8).ToArray();
            offset += 8;
            return bytes;
        }

        if (tag == 3)
        {
            byte[] bytes = { span[offset] };
            offset += 1;
            return bytes;
        }

        throw new InvalidOperationException("Unknown persist tag.");
    }

    internal readonly struct SnapshotHeader
    {
        internal SnapshotHeader(ulong instanceId, ulong nextCounter, ulong tick, ulong nextMessageId, ulong nextRoomSequence)
        {
            InstanceId = instanceId;
            NextCounter = nextCounter;
            Tick = tick;
            NextMessageId = nextMessageId;
            NextRoomSequence = nextRoomSequence;
        }

        internal readonly ulong InstanceId;
        internal readonly ulong NextCounter;
        internal readonly ulong Tick;
        internal readonly ulong NextMessageId;
        internal readonly ulong NextRoomSequence;
    }

    internal readonly struct SnapshotEntity
    {
        internal SnapshotEntity(ulong counter, string typeName, Dictionary<string, FieldBlob> fields)
        {
            Counter = counter;
            TypeName = typeName;
            Fields = fields;
        }

        internal readonly ulong Counter;
        internal readonly string TypeName;
        internal readonly Dictionary<string, FieldBlob> Fields;
    }

    internal readonly struct FieldBlob
    {
        internal FieldBlob(byte tag, byte[] bytes)
        {
            Tag = tag;
            Bytes = bytes;
        }

        internal readonly byte Tag;
        internal readonly byte[] Bytes;
    }

    private sealed class SnapshotWriter : PersistWriter
    {
        private readonly List<KeyValuePair<string, FieldBlob>> _fields;

        internal SnapshotWriter(List<KeyValuePair<string, FieldBlob>> fields) => _fields = fields;

        public void WriteString(string attributeId, string? value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
            byte[] payload = new byte[4 + utf8.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)utf8.Length);
            Buffer.BlockCopy(utf8, 0, payload, 4, utf8.Length);
            _fields.Add(new KeyValuePair<string, FieldBlob>(attributeId, new FieldBlob(1, payload)));
        }

        public void WriteUInt64(string attributeId, ulong value)
        {
            byte[] payload = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
            _fields.Add(new KeyValuePair<string, FieldBlob>(attributeId, new FieldBlob(2, payload)));
        }

        public void WriteBoolean(string attributeId, bool value) =>
            _fields.Add(new KeyValuePair<string, FieldBlob>(attributeId, new FieldBlob(3, new[] { value ? (byte)1 : (byte)0 })));
    }

    internal sealed class SnapshotReader : PersistReader
    {
        private readonly Dictionary<string, FieldBlob> _fields;

        internal SnapshotReader(Dictionary<string, FieldBlob> fields) => _fields = fields;

        public bool TryReadString(string attributeId, out string value)
        {
            value = string.Empty;
            if (!_fields.TryGetValue(attributeId, out FieldBlob blob) || blob.Tag != 1) return false;
            value = Encoding.UTF8.GetString(blob.Bytes);
            return true;
        }

        public bool TryReadUInt64(string attributeId, out ulong value)
        {
            value = 0;
            if (!_fields.TryGetValue(attributeId, out FieldBlob blob) || blob.Tag != 2) return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(blob.Bytes);
            return true;
        }

        public bool TryReadBoolean(string attributeId, out bool value)
        {
            value = false;
            if (!_fields.TryGetValue(attributeId, out FieldBlob blob) || blob.Tag != 3) return false;
            value = blob.Bytes[0] != 0;
            return true;
        }
    }
}
