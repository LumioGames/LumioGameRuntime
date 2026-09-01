using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Lumio.GameRuntime.Ecs;

internal readonly struct ChangeEntry : IEquatable<ChangeEntry>
{
    public ChangeEntry(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlyMemory<byte> canonicalBefore,
        ReadOnlyMemory<byte> canonicalAfter)
    {
        Entity = entity;
        ComponentType = componentType;
        Field = field;
        CanonicalBefore = canonicalBefore.ToArray();
        CanonicalAfter = canonicalAfter.ToArray();
    }

    public LocalEntityId Entity { get; }

    public ComponentTypeId ComponentType { get; }

    public ComponentFieldId Field { get; }

    public ReadOnlyMemory<byte> CanonicalBefore { get; }

    public ReadOnlyMemory<byte> CanonicalAfter { get; }

    public bool Equals(ChangeEntry other) =>
        Entity == other.Entity &&
        ComponentType == other.ComponentType &&
        Field == other.Field &&
        CanonicalBefore.Span.SequenceEqual(other.CanonicalBefore.Span) &&
        CanonicalAfter.Span.SequenceEqual(other.CanonicalAfter.Span);

    public override bool Equals(object? obj) => obj is ChangeEntry other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Entity);
        hash.Add(ComponentType);
        hash.Add(Field);
        hash.Add(CanonicalBefore.Length);
        hash.Add(CanonicalAfter.Length);
        return hash.ToHashCode();
    }

    public static bool operator ==(ChangeEntry left, ChangeEntry right) => left.Equals(right);

    public static bool operator !=(ChangeEntry left, ChangeEntry right) => !left.Equals(right);
}

internal sealed class ChangeEntryCanonicalComparer : IComparer<ChangeEntry>
{
    public static ChangeEntryCanonicalComparer Instance { get; } = new();

    public int Compare(ChangeEntry x, ChangeEntry y)
    {
        int entity = x.Entity.CompareTo(y.Entity);
        if (entity != 0) return entity;
        int component = x.ComponentType.CompareTo(y.ComponentType);
        return component != 0 ? component : x.Field.CompareTo(y.Field);
    }
}

internal sealed class ChangeSet
{
    private static readonly byte[] Magic =
    {
        (byte)'L', (byte)'G', (byte)'E', (byte)'C', (byte)'H', (byte)'G', (byte)'0', (byte)'1'
    };

    private readonly ChangeEntry[] _entries;
    private readonly byte[] _canonicalBytes;

    public ChangeSet(WorldId worldId, TickId tickId, ReadOnlySpan<ChangeEntry> entries)
    {
        WorldId = worldId;
        TickId = tickId;
        _entries = new ChangeEntry[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            ChangeEntry entry = entries[index];
            _entries[index] = new ChangeEntry(
                entry.Entity,
                entry.ComponentType,
                entry.Field,
                entry.CanonicalBefore,
                entry.CanonicalAfter);
        }

        Array.Sort(_entries, ChangeEntryCanonicalComparer.Instance);
        _canonicalBytes = Encode(worldId, tickId, _entries);
        CanonicalHashHex = CanonicalHash.Of(_canonicalBytes);
    }

    public WorldId WorldId { get; }

    public TickId TickId { get; }

    public ReadOnlyMemory<ChangeEntry> Entries => _entries;

    public ReadOnlyMemory<byte> CanonicalBytes => _canonicalBytes;

    public string CanonicalHashHex { get; }

    private static byte[] Encode(WorldId worldId, TickId tickId, ChangeEntry[] entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(worldId.Value);
            writer.Write(tickId.Value);
            writer.Write(entries.Length);
            for (int index = 0; index < entries.Length; index++)
            {
                ChangeEntry entry = entries[index];
                writer.Write(entry.Entity.Index);
                writer.Write(entry.Entity.Generation);
                writer.Write(entry.ComponentType.Value);
                writer.Write(entry.Field.Value);
                WriteBuffer(writer, entry.CanonicalBefore.Span);
                WriteBuffer(writer, entry.CanonicalAfter.Span);
            }

            writer.Flush();
        }

        return stream.ToArray();
    }

    private static void WriteBuffer(BinaryWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Write(value.Length);
        if (value.Length == 0) return;
#if NET10_0_OR_GREATER
        writer.Write(value);
#else
        writer.Write(value.ToArray());
#endif
    }
}

internal static class CanonicalHash
{
    public static string Of(ChangeSet set) => set.CanonicalHashHex;

    public static string Of(byte[] bytes) => Of((ReadOnlySpan<byte>)bytes);

    public static string Of(ReadOnlyMemory<byte> bytes) => Of(bytes.Span);

    public static string Of(ReadOnlySpan<byte> bytes)
    {
#if NET10_0_OR_GREATER
        byte[] digest = SHA256.HashData(bytes);
#else
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(bytes.ToArray());
#endif
        return ToLowercaseHex(digest);
    }

    private static string ToLowercaseHex(byte[] digest)
    {
        const string Digits = "0123456789abcdef";
        var chars = new char[digest.Length * 2];
        for (int index = 0; index < digest.Length; index++)
        {
            chars[index * 2] = Digits[digest[index] >> 4];
            chars[(index * 2) + 1] = Digits[digest[index] & 0x0F];
        }

        return new string(chars);
    }
}
