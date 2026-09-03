using System;
using System.Globalization;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// 128-bit network identity = world instance id (high 64) + in-world counter (low 64).
/// Wire form is 32 lowercase hex characters. Issued at commit, never reused.
/// </summary>
public readonly struct NetEntityId : IEquatable<NetEntityId>, IComparable<NetEntityId>
{
    /// <summary>World instance id assigned by the host when the server world is created.</summary>
    public ulong InstanceId { get; }

    /// <summary>Monotonic in-world counter issued at commit.</summary>
    public ulong Counter { get; }

    /// <summary>Creates an identity from its two halves.</summary>
    public NetEntityId(ulong instanceId, ulong counter)
    {
        InstanceId = instanceId;
        Counter = counter;
    }

    /// <summary>True when both halves are zero (never issued).</summary>
    public bool IsDefault => InstanceId == 0UL && Counter == 0UL;

    /// <summary>32-character lowercase hex encoding (instance then counter).</summary>
    public string ToHex()
    {
        Span<char> chars = stackalloc char[32];
        WriteHex16(chars, InstanceId);
        WriteHex16(chars.Slice(16), Counter);
        return new string(chars);
    }

    /// <inheritdoc />
    public override string ToString() => ToHex();

    /// <summary>Parses a 32-hex identity. Throws when the token is not a 128-bit hex id.</summary>
    public static NetEntityId Parse(string value)
    {
        if (!TryParse(value, out NetEntityId id))
            throw new ArgumentException("NetEntityId must be 32 lowercase hex characters.", nameof(value));
        return id;
    }

    /// <summary>Attempts to parse a 32-hex identity.</summary>
    public static bool TryParse(string? value, out NetEntityId id)
    {
        id = default;
        if (value is null || value.Length != 32) return false;
        if (!TryReadHex16(value.AsSpan(0, 16), out ulong instance)) return false;
        if (!TryReadHex16(value.AsSpan(16, 16), out ulong counter)) return false;
        id = new NetEntityId(instance, counter);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(NetEntityId other) => InstanceId == other.InstanceId && Counter == other.Counter;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NetEntityId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(InstanceId, Counter);

    /// <inheritdoc />
    public int CompareTo(NetEntityId other)
    {
        int instance = InstanceId.CompareTo(other.InstanceId);
        return instance != 0 ? instance : Counter.CompareTo(other.Counter);
    }

    /// <summary>Equality.</summary>
    public static bool operator ==(NetEntityId left, NetEntityId right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(NetEntityId left, NetEntityId right) => !left.Equals(right);

    /// <summary>Ordering.</summary>
    public static bool operator <(NetEntityId left, NetEntityId right) => left.CompareTo(right) < 0;

    /// <summary>Ordering.</summary>
    public static bool operator >(NetEntityId left, NetEntityId right) => left.CompareTo(right) > 0;

    /// <summary>Ordering.</summary>
    public static bool operator <=(NetEntityId left, NetEntityId right) => left.CompareTo(right) <= 0;

    /// <summary>Ordering.</summary>
    public static bool operator >=(NetEntityId left, NetEntityId right) => left.CompareTo(right) >= 0;

    private static void WriteHex16(Span<char> dest, ulong value)
    {
        for (int i = 15; i >= 0; i--)
        {
            int nibble = (int)(value & 0xFUL);
            dest[i] = (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));
            value >>= 4;
        }
    }

    private static bool TryReadHex16(ReadOnlySpan<char> text, out ulong value)
    {
        value = 0UL;
        for (int i = 0; i < 16; i++)
        {
            char ch = text[i];
            int nibble;
            if (ch is >= '0' and <= '9') nibble = ch - '0';
            else if (ch is >= 'a' and <= 'f') nibble = ch - 'a' + 10;
            else return false;
            value = (value << 4) | (uint)nibble;
        }

        return true;
    }
}
