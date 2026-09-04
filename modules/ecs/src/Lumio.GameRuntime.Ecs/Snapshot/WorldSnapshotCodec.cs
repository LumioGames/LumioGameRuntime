using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Canonical persistence for the single World. Tombstones are derived, not serialized.</summary>
internal static class WorldSnapshotCodec
{
    internal const uint Version = 2;
    private static readonly byte[] Magic = { (byte)'L', (byte)'W', (byte)'M', (byte)'1' };

    internal static byte[] Capture(World world)
    {
        var chunks = new List<byte[]> { Magic, U32(Version), U64(world.InstanceId), U64(world.NextCounter), U64(world.Tick), U64(world.NextMessageId), U64(world.NextRoomSequence) };
        var entities = new List<EntityRecord>();
        for (int i = 0; i < world.CreationOrder.Count; i++)
        {
            EntityRecord? record = world.Record(world.CreationOrder[i]);
            if (record is not null) entities.Add(record);
        }
        chunks.Add(U32((uint)entities.Count));
        for (int i = 0; i < entities.Count; i++)
        {
            EntityRecord record = entities[i];
            chunks.Add(U64(record.Id.Counter));
            chunks.Add(Str(world.Registry.WireName(record.EntityType)));
            var fields = new List<KeyValuePair<string, FieldBlob>>();
            var writer = new SnapshotWriter(fields);
            for (int c = 0; c < record.Components.Length; c++) EcsRegistry.Generated(record.Components[c])?.CapturePersist(writer);
            chunks.Add(U32((uint)fields.Count));
            for (int f = 0; f < fields.Count; f++) { chunks.Add(Str(fields[f].Key)); chunks.Add(new[] { fields[f].Value.Tag }); chunks.Add(fields[f].Value.Bytes); }
        }
        int size = 0;
        for (int i = 0; i < chunks.Count; i++) size += chunks[i].Length;
        var result = new byte[size];
        int offset = 0;
        for (int i = 0; i < chunks.Count; i++) { Buffer.BlockCopy(chunks[i], 0, result, offset, chunks[i].Length); offset += chunks[i].Length; }
        return result;
    }

    internal static SnapshotHeader Read(ReadOnlyMemory<byte> bytes, out List<SnapshotEntity> entities)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        int offset = 0;
        if (span.Length < 4 || span[0] != Magic[0] || span[1] != Magic[1] || span[2] != Magic[2] || span[3] != Magic[3]) throw new InvalidOperationException("Snapshot magic is not LWM1.");
        offset += 4;
        uint version = ReadU32(span, ref offset);
        if (version != Version) throw new InvalidOperationException("Unsupported snapshot version.");
        ulong instance = ReadU64(span, ref offset);
        ulong next = ReadU64(span, ref offset);
        ulong tick = ReadU64(span, ref offset);
        ulong message = ReadU64(span, ref offset);
        ulong sequence = ReadU64(span, ref offset);
        uint count = ReadU32(span, ref offset);
        entities = new List<SnapshotEntity>((int)count);
        for (uint i = 0; i < count; i++)
        {
            ulong counter = ReadU64(span, ref offset);
            string type = ReadStr(span, ref offset);
            uint fieldCount = ReadU32(span, ref offset);
            var fields = new Dictionary<string, FieldBlob>(StringComparer.Ordinal);
            for (uint f = 0; f < fieldCount; f++)
            {
                string id = ReadStr(span, ref offset);
                if (offset >= span.Length) throw new InvalidOperationException("Snapshot field tag is missing.");
                byte tag = span[offset++];
                fields[id] = new FieldBlob(tag, ReadBlob(span, ref offset, tag));
            }
            entities.Add(new SnapshotEntity(counter, type, fields));
        }
        if (offset != span.Length) throw new InvalidOperationException("Snapshot has trailing data.");
        return new SnapshotHeader(instance, next, tick, message, sequence);
    }

    private static byte[] U32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); return bytes; }
    private static byte[] U64(ulong value) { var bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); return bytes; }
    private static byte[] Str(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var bytes = new byte[4 + utf8.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, bytes, 4, utf8.Length);
        return bytes;
    }
    private static uint ReadU32(ReadOnlySpan<byte> span, ref int offset) { if (offset + 4 > span.Length) throw new InvalidOperationException("Snapshot is truncated."); uint value = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4)); offset += 4; return value; }
    private static ulong ReadU64(ReadOnlySpan<byte> span, ref int offset) { if (offset + 8 > span.Length) throw new InvalidOperationException("Snapshot is truncated."); ulong value = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, 8)); offset += 8; return value; }
    private static string ReadStr(ReadOnlySpan<byte> span, ref int offset) { int len = checked((int)ReadU32(span, ref offset)); if (offset + len > span.Length) throw new InvalidOperationException("Snapshot string is truncated."); string value = Encoding.UTF8.GetString(span.Slice(offset, len)); offset += len; return value; }
    private static byte[] ReadBlob(ReadOnlySpan<byte> span, ref int offset, byte tag)
    {
        int len = tag is 1 or 4 ? checked((int)ReadU32(span, ref offset)) : tag == 2 ? 8 : tag == 3 ? 1 : 0;
        if (tag is not (1 or 2 or 3 or 4)) throw new InvalidOperationException("Unknown persist tag.");
        if (offset + len > span.Length) throw new InvalidOperationException("Snapshot field is truncated.");
        int start = tag is 1 or 4 ? offset - 4 : offset;
        byte[] bytes = span.Slice(start, len + (tag is 1 or 4 ? 4 : 0)).ToArray(); offset += len; return bytes;
    }

    internal readonly struct SnapshotHeader
    {
        internal SnapshotHeader(ulong instanceId, ulong nextCounter, ulong tick, ulong nextMessageId, ulong nextRoomSequence) { InstanceId = instanceId; NextCounter = nextCounter; Tick = tick; NextMessageId = nextMessageId; NextRoomSequence = nextRoomSequence; }
        internal readonly ulong InstanceId, NextCounter, Tick, NextMessageId, NextRoomSequence;
    }
    internal readonly struct SnapshotEntity
    {
        internal SnapshotEntity(ulong counter, string typeName, Dictionary<string, FieldBlob> fields) { Counter = counter; TypeName = typeName; Fields = fields; }
        internal readonly ulong Counter; internal readonly string TypeName; internal readonly Dictionary<string, FieldBlob> Fields;
    }
    internal readonly struct FieldBlob
    {
        internal FieldBlob(byte tag, byte[] bytes) { Tag = tag; Bytes = bytes; }
        internal readonly byte Tag; internal readonly byte[] Bytes;
    }
    private sealed class SnapshotWriter : IPersistWriter
    {
        private readonly List<KeyValuePair<string, FieldBlob>> _fields;
        internal SnapshotWriter(List<KeyValuePair<string, FieldBlob>> fields) => _fields = fields;
        public void WriteString(string id, string? value) { byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty); var payload = new byte[4 + utf8.Length]; BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)utf8.Length); Buffer.BlockCopy(utf8, 0, payload, 4, utf8.Length); _fields.Add(new KeyValuePair<string, FieldBlob>(id, new FieldBlob(1, payload))); }
        public void WriteContainer(string id, object value) { byte[] utf8 = Encoding.UTF8.GetBytes(value is ISyncContainer container ? WireCodec.ContainerText(container) : value?.ToString() ?? string.Empty); var payload = new byte[4 + utf8.Length]; BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)utf8.Length); Buffer.BlockCopy(utf8, 0, payload, 4, utf8.Length); _fields.Add(new KeyValuePair<string, FieldBlob>(id, new FieldBlob(4, payload))); }
        public void WriteUInt64(string id, ulong value) { var payload = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(payload, value); _fields.Add(new KeyValuePair<string, FieldBlob>(id, new FieldBlob(2, payload))); }
        public void WriteBoolean(string id, bool value) => _fields.Add(new KeyValuePair<string, FieldBlob>(id, new FieldBlob(3, new[] { value ? (byte)1 : (byte)0 })));
    }
    internal sealed class SnapshotReader : IPersistReader
    {
        private readonly Dictionary<string, FieldBlob> _fields;
        internal SnapshotReader(Dictionary<string, FieldBlob> fields) => _fields = fields;
        public bool TryReadString(string id, out string value) { value = string.Empty; if (!_fields.TryGetValue(id, out FieldBlob blob) || blob.Tag != 1) return false; int len = BinaryPrimitives.ReadInt32LittleEndian(blob.Bytes); value = Encoding.UTF8.GetString(blob.Bytes, 4, len); return true; }
        public bool TryReadContainer(string id, out object value) { value = string.Empty; if (!_fields.TryGetValue(id, out FieldBlob blob) || blob.Tag != 4) return false; int len = BinaryPrimitives.ReadInt32LittleEndian(blob.Bytes); value = Encoding.UTF8.GetString(blob.Bytes, 4, len); return true; }
        public bool TryReadUInt64(string id, out ulong value) { value = 0; if (!_fields.TryGetValue(id, out FieldBlob blob) || blob.Tag != 2) return false; value = BinaryPrimitives.ReadUInt64LittleEndian(blob.Bytes); return true; }
        public bool TryReadBoolean(string id, out bool value) { value = false; if (!_fields.TryGetValue(id, out FieldBlob blob) || blob.Tag != 3) return false; value = blob.Bytes[0] != 0; return true; }
    }
}
