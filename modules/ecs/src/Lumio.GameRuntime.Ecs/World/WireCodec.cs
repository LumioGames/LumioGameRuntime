using System;
using System.Buffers.Binary;
using System.Text;

namespace Lumio.GameRuntime.Ecs;

internal static class WireCodec
{
    internal const string ChatInput = "chat.input";
    internal const string ChatEvent = "chat.event";
    internal const string FieldWrite = "field.write";
    internal const string ServerRpc = "server.rpc";

    internal static byte[] EncodeUtf8(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
        byte[] bytes = new byte[4 + utf8.Length];
        int offset = 0;
        WriteUtf8(bytes, ref offset, utf8);
        return bytes;
    }

    internal static bool TryReadUtf8Payload(ReadOnlySpan<byte> payload, out string text)
    {
        int offset = 0;
        if (!TryReadString(payload, ref offset, out text) || offset != payload.Length) return false;
        return true;
    }

    internal static byte[] EncodeFieldWrite(NetEntityId target, string componentId, string fieldId, string value)
    {
        byte[] component = Encoding.UTF8.GetBytes(componentId ?? string.Empty);
        byte[] field = Encoding.UTF8.GetBytes(fieldId ?? string.Empty);
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
        byte[] bytes = new byte[8 + 4 + component.Length + 4 + field.Length + 4 + utf8.Length];
        int offset = 0;
        WriteUInt64(bytes, ref offset, target.Counter);
        WriteUtf8(bytes, ref offset, component);
        WriteUtf8(bytes, ref offset, field);
        WriteUtf8(bytes, ref offset, utf8);
        return bytes;
    }

    internal static bool TryDecodeFieldWrite(ReadOnlySpan<byte> payload, out ulong counter, out string componentId, out string fieldId, out string value)
    {
        counter = 0;
        componentId = string.Empty;
        fieldId = string.Empty;
        value = string.Empty;
        int offset = 0;
        if (!TryReadUInt64(payload, ref offset, out counter)) return false;
        if (!TryReadString(payload, ref offset, out componentId)) return false;
        if (!TryReadString(payload, ref offset, out fieldId)) return false;
        if (!TryReadString(payload, ref offset, out value)) return false;
        return offset == payload.Length;
    }

    internal static byte[] EncodeServerRpc(string componentId, string method, string argument)
    {
        byte[] component = Encoding.UTF8.GetBytes(componentId ?? string.Empty);
        byte[] methodBytes = Encoding.UTF8.GetBytes(method ?? string.Empty);
        byte[] utf8 = Encoding.UTF8.GetBytes(argument ?? string.Empty);
        byte[] bytes = new byte[4 + component.Length + 4 + methodBytes.Length + 4 + utf8.Length];
        int offset = 0;
        WriteUtf8(bytes, ref offset, component);
        WriteUtf8(bytes, ref offset, methodBytes);
        WriteUtf8(bytes, ref offset, utf8);
        return bytes;
    }

    internal static bool TryDecodeServerRpc(ReadOnlySpan<byte> payload, out string componentId, out string method, out string argument)
    {
        componentId = string.Empty;
        method = string.Empty;
        argument = string.Empty;
        int offset = 0;
        if (!TryReadString(payload, ref offset, out componentId)) return false;
        if (!TryReadString(payload, ref offset, out method)) return false;
        if (!TryReadString(payload, ref offset, out argument)) return false;
        return offset == payload.Length;
    }

    internal static void WriteUInt64(byte[] dest, ref int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(dest.AsSpan(offset, 8), value);
        offset += 8;
    }

    internal static void WriteUtf8(byte[] dest, ref int offset, byte[] utf8)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest.AsSpan(offset, 4), (uint)utf8.Length);
        offset += 4;
        utf8.CopyTo(dest, offset);
        offset += utf8.Length;
    }

    internal static bool TryReadUInt64(ReadOnlySpan<byte> payload, ref int offset, out ulong value)
    {
        value = 0;
        if (offset + 8 > payload.Length) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8));
        offset += 8;
        return true;
    }

    internal static bool TryReadString(ReadOnlySpan<byte> payload, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset + 4 > payload.Length) return false;
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
        offset += 4;
        if (length < 0 || offset + length > payload.Length) return false;
        value = Encoding.UTF8.GetString(payload.Slice(offset, length));
        offset += length;
        return true;
    }
}
