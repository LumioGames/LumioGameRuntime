using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lumio.GameRuntime.Replication.Chat;

internal static class ChatPayload
{
    internal static bool TryDecodeHex(string? hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (hex is null || (hex.Length & 1) != 0) return false;
        bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            int hi = Nibble(hex[i * 2]);
            int lo = Nibble(hex[(i * 2) + 1]);
            if (hi < 0 || lo < 0) return false;
            bytes[i] = (byte)((hi << 4) | lo);
        }

        return true;
    }

    internal static bool TryDecodeInput(byte[] payload, out string text, out string code)
    {
        text = string.Empty;
        code = "undecodable_payload";
        int offset = 0;
        if (!TryReadString(payload, ref offset, out text, out bool tooLong) || offset != payload.Length)
            return false;
        if (tooLong)
        {
            code = "chat_text_too_long";
            return false;
        }

        code = string.Empty;
        return true;
    }

    internal static bool TryDecodeEvent(byte[] payload, out ChatMessageEvent mapped, out string code)
    {
        mapped = default;
        code = "undecodable_payload";
        int offset = 0;
        if (!TryReadUInt64(payload, ref offset, out ulong messageId) ||
            !TryReadUInt64(payload, ref offset, out ulong roomSequence) ||
            !TryReadUInt64(payload, ref offset, out ulong sender) ||
            !TryReadString(payload, ref offset, out string text, out bool tooLong) ||
            !TryReadUInt64(payload, ref offset, out ulong appliedTick) ||
            offset != payload.Length)
            return false;
        if (tooLong)
        {
            code = "chat_text_too_long";
            return false;
        }

        mapped = new ChatMessageEvent(
            messageId,
            roomSequence,
            sender.ToString(CultureInfo.InvariantCulture),
            text,
            appliedTick);
        code = string.Empty;
        return true;
    }

    internal static bool TryParseSender(string? netEntityId, out ulong sender)
    {
        sender = 0;
        if (string.IsNullOrEmpty(netEntityId)) return false;
        if (netEntityId.Length == 32)
            return ulong.TryParse(netEntityId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out sender);
        return ulong.TryParse(netEntityId, NumberStyles.None, CultureInfo.InvariantCulture, out sender);
    }

    internal static byte[] EncodeLiveAttribute(IReadOnlyList<(ulong NetEntityId, string Value)> rows)
    {
        int size = 0;
        var utf8Rows = new byte[rows.Count][];
        for (int i = 0; i < rows.Count; i++)
        {
            utf8Rows[i] = Encoding.UTF8.GetBytes(rows[i].Value ?? string.Empty);
            size = checked(size + 8 + 4 + utf8Rows[i].Length);
        }

        byte[] bytes = new byte[size];
        int offset = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            WriteUInt64(bytes, ref offset, rows[i].NetEntityId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)utf8Rows[i].Length);
            offset += 4;
            utf8Rows[i].CopyTo(bytes, offset);
            offset += utf8Rows[i].Length;
        }

        return bytes;
    }

    internal static byte[] EncodeEvent(ChatMessageEvent mapped)
    {
        if (!TryParseSender(mapped.SenderNetEntityId, out ulong sender))
            throw new ArgumentException("senderNetEntityId is not a C-1 u64.", nameof(mapped));
        byte[] utf8 = Encoding.UTF8.GetBytes(mapped.Text ?? string.Empty);
        byte[] bytes = new byte[8 + 8 + 8 + 4 + utf8.Length + 8];
        int offset = 0;
        WriteUInt64(bytes, ref offset, mapped.MessageId);
        WriteUInt64(bytes, ref offset, mapped.RoomSequence);
        WriteUInt64(bytes, ref offset, sender);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)utf8.Length);
        offset += 4;
        utf8.CopyTo(bytes, offset);
        offset += utf8.Length;
        WriteUInt64(bytes, ref offset, mapped.AppliedTick);
        return bytes;
    }

    internal static string ToHex(byte[] value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (byte item in value) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static bool TryReadUInt64(byte[] data, ref int offset, out ulong value)
    {
        value = 0;
        if (offset + 8 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
        offset += 8;
        return true;
    }

    private static bool TryReadString(byte[] data, ref int offset, out string value, out bool tooLong)
    {
        value = string.Empty;
        tooLong = false;
        if (offset + 4 > data.Length) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;
        if (length > (uint)(data.Length - offset)) return false;
        if (length > ChatMapping.MaxTextUtf8Bytes)
        {
            tooLong = true;
            offset += (int)length;
            return true;
        }

        value = Encoding.UTF8.GetString(data, offset, (int)length);
        offset += (int)length;
        return true;
    }

    private static void WriteUInt64(byte[] dest, ref int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(dest.AsSpan(offset, 8), value);
        offset += 8;
    }

    private static int Nibble(char value)
    {
        if (value is >= '0' and <= '9') return value - '0';
        if (value is >= 'a' and <= 'f') return value - 'a' + 10;
        return -1;
    }
}
