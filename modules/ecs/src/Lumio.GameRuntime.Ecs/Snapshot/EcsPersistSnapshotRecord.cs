using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lumio.GameRuntime.Ecs;

internal static class EcsPersistSnapshotRecord
{
    internal const ulong RecordVersion = 1UL;
    internal const ulong RecordSeq = 1UL;

    internal static byte[] Encode(EcsPersistSnapshotMaterial material)
    {
        byte[] payload = EncodePayload(material);
        string payloadHash = CanonicalHash.Of(payload);
        using var header = new MemoryStream();
        WriteUInt64(header, RecordVersion);
        WriteUInt64(header, RecordSeq);
        WriteUInt64(header, material.SchemaEpoch);
        WriteString(header, payloadHash);
        string checksum = CanonicalHash.Of(header.ToArray());
        WriteString(header, checksum);
        header.Write(payload, 0, payload.Length);
        return header.ToArray();
    }

    internal static StorageOperationResult TryDecode(ReadOnlySpan<byte> bytes, out EcsPersistSnapshotMaterial? material)
    {
        material = null;
        int offset = 0;
        if (!TryReadUInt64(bytes, ref offset, out ulong recordVersion) ||
            recordVersion != RecordVersion ||
            !TryReadUInt64(bytes, ref offset, out _) ||
            !TryReadUInt64(bytes, ref offset, out ulong schemaEpoch) ||
            schemaEpoch == 0UL ||
            !TryReadString(bytes, ref offset, out string payloadHash))
        {
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }

        ReadOnlySpan<byte> headerWithoutChecksum = bytes.Slice(0, offset);
        if (!TryReadString(bytes, ref offset, out string checksum))
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        if (!string.Equals(checksum, CanonicalHash.Of(headerWithoutChecksum), StringComparison.Ordinal) ||
            payloadHash.Length != 64)
        {
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }

        ReadOnlySpan<byte> payload = bytes.Slice(offset);
        if (!string.Equals(payloadHash, CanonicalHash.Of(payload), StringComparison.Ordinal))
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        if (!TryDecodePayload(payload, schemaEpoch, out material) || material is null)
        {
            material = null;
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
        }

        return StorageOperationResult.Accepted();
    }

    private static byte[] EncodePayload(EcsPersistSnapshotMaterial material)
    {
        using var stream = new MemoryStream();
        IReadOnlyList<EcsPersistEntityRecord> entities = material.Entities;
        WriteUInt32(stream, (uint)entities.Count);
        for (int entityIndex = 0; entityIndex < entities.Count; entityIndex++)
        {
            EcsPersistEntityRecord entity = entities[entityIndex];
            WriteUInt32(stream, entity.Entity.Index);
            WriteUInt32(stream, entity.Entity.Generation);
            IReadOnlyList<EcsPersistFieldRecord> fields = entity.Fields;
            WriteUInt32(stream, (uint)fields.Count);
            for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
            {
                EcsPersistFieldRecord field = fields[fieldIndex];
                WriteUInt64(stream, field.ComponentType.Value);
                WriteUInt64(stream, field.Field.Value);
                ReadOnlySpan<byte> value = field.CanonicalValue.Span;
                WriteUInt32(stream, (uint)value.Length);
                if (value.Length > 0)
                {
#if NET10_0_OR_GREATER
                    stream.Write(value);
#else
                    stream.Write(value.ToArray(), 0, value.Length);
#endif
                }
            }
        }

        return stream.ToArray();
    }

    private static bool TryDecodePayload(
        ReadOnlySpan<byte> payload,
        ulong schemaEpoch,
        out EcsPersistSnapshotMaterial? material)
    {
        material = null;
        int offset = 0;
        if (!TryReadUInt32(payload, ref offset, out uint entityCount))
            return false;
        var entities = new EcsPersistEntityRecord[entityCount];
        for (uint entityIndex = 0; entityIndex < entityCount; entityIndex++)
        {
            if (!TryReadUInt32(payload, ref offset, out uint index) ||
                !TryReadUInt32(payload, ref offset, out uint generation) ||
                !TryReadUInt32(payload, ref offset, out uint fieldCount) ||
                fieldCount == 0U)
            {
                return false;
            }

            var fields = new EcsPersistFieldRecord[fieldCount];
            for (uint fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
            {
                if (!TryReadUInt64(payload, ref offset, out ulong componentType) ||
                    !TryReadUInt64(payload, ref offset, out ulong fieldId) ||
                    !TryReadUInt32(payload, ref offset, out uint valueLength) ||
                    (uint)offset > (uint)payload.Length ||
                    valueLength > (uint)(payload.Length - offset))
                {
                    return false;
                }

                var value = payload.Slice(offset, (int)valueLength).ToArray();
                offset += (int)valueLength;
                if (componentType == 0UL || fieldId == 0UL || value.Length == 0)
                    return false;
                fields[fieldIndex] = new EcsPersistFieldRecord(
                    new ComponentTypeId(componentType),
                    new ComponentFieldId(fieldId),
                    value);
            }

            var entity = new LocalEntityId(index, generation);
            if (entity.IsDefault)
                return false;
            entities[entityIndex] = new EcsPersistEntityRecord(entity, fields);
        }

        if (offset != payload.Length)
            return false;
        material = new EcsPersistSnapshotMaterial(schemaEpoch, entities);
        return true;
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        WriteSpan(stream, buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        WriteSpan(stream, buffer);
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        WriteUInt32(stream, (uint)utf8.Length);
        stream.Write(utf8, 0, utf8.Length);
    }

    private static void WriteSpan(Stream stream, ReadOnlySpan<byte> value)
    {
#if NET10_0_OR_GREATER
        stream.Write(value);
#else
        stream.Write(value.ToArray(), 0, value.Length);
#endif
    }

    private static bool TryReadUInt64(ReadOnlySpan<byte> source, ref int offset, out ulong value)
    {
        value = 0UL;
        if ((uint)offset > (uint)source.Length || (uint)(source.Length - offset) < 8U) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8));
        offset += 8;
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> source, ref int offset, out uint value)
    {
        value = 0U;
        if ((uint)offset > (uint)source.Length || (uint)(source.Length - offset) < 4U) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
        offset += 4;
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> source, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadUInt32(source, ref offset, out uint length))
            return false;
        if ((uint)offset > (uint)source.Length || length > (uint)(source.Length - offset))
            return false;
        value = Encoding.UTF8.GetString(source.Slice(offset, (int)length));
        offset += (int)length;
        return true;
    }
}
