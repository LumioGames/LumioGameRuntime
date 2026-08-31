using System;
using System.Text;

namespace Lumio.GameRuntime.Command;

/// <summary>A temporary entity identity scoped to one tick and processor invocation.</summary>
public readonly record struct DeferredEntityToken : IComparable<DeferredEntityToken>
{
    public DeferredEntityToken(ulong tickId, string processorId, ulong localSequence)
        : this(tickId, "default", processorId, 0UL, localSequence)
    {
    }

    public DeferredEntityToken(ulong tickId, string worldId, string processorId, ulong localSequence)
        : this(tickId, worldId, processorId, 0UL, localSequence)
    {
    }

    public DeferredEntityToken(ulong tickId, string worldId, string processorId, ulong bufferGeneration, ulong localSequence)
    {
        if (string.IsNullOrWhiteSpace(worldId) || !IsValidIdentifier(worldId))
        {
            throw new ArgumentException("A valid world ID is required.", nameof(worldId));
        }

        if (string.IsNullOrWhiteSpace(processorId) || !IsValidIdentifier(processorId))
        {
            throw new ArgumentException("A valid processor ID is required.", nameof(processorId));
        }

        TickId = tickId;
        WorldId = worldId;
        ProcessorId = processorId;
        BufferGeneration = bufferGeneration;
        LocalSequence = localSequence;
    }

    public ulong TickId { get; }

    public string WorldId { get; }

    public string ProcessorId { get; }

    public ulong BufferGeneration { get; }

    public ulong LocalSequence { get; }

    public bool IsValid =>
        WorldId is not null &&
        IsValidIdentifier(WorldId) &&
        ProcessorId is not null &&
        ProcessorId.Length > 0 &&
        IsValidIdentifier(ProcessorId);

    // The canonical key contains every field that participates in token
    // equality so it can safely be used in command digests and idempotency
    // keys.
    public string CanonicalKey => string.Concat(
        TickId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ":",
        Escape(WorldId),
        ":",
        Escape(ProcessorId),
        ":",
        BufferGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ":",
        LocalSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public ReadOnlyMemory<byte> CanonicalBytes => Encoding.UTF8.GetBytes(CanonicalKey);

    public bool BelongsToTick(ulong tickId) => TickId == tickId;

    public ulong Sequence => LocalSequence;

    public ulong LocalTokenSequence => LocalSequence;

    public int CompareTo(DeferredEntityToken other)
    {
        int tick = TickId.CompareTo(other.TickId);
        if (tick != 0) return tick;
        int world = StringComparer.Ordinal.Compare(WorldId, other.WorldId);
        if (world != 0) return world;
        int processor = StringComparer.Ordinal.Compare(ProcessorId, other.ProcessorId);
        if (processor != 0) return processor;
        int generation = BufferGeneration.CompareTo(other.BufferGeneration);
        return generation != 0 ? generation : LocalSequence.CompareTo(other.LocalSequence);
    }

    public static bool operator <(DeferredEntityToken left, DeferredEntityToken right) => left.CompareTo(right) < 0;

    public static bool operator <=(DeferredEntityToken left, DeferredEntityToken right) => left.CompareTo(right) <= 0;

    public static bool operator >(DeferredEntityToken left, DeferredEntityToken right) => left.CompareTo(right) > 0;

    public static bool operator >=(DeferredEntityToken left, DeferredEntityToken right) => left.CompareTo(right) >= 0;

    public override string ToString() => CanonicalKey;

    private static string Escape(string value) => value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace(":", "%3A", StringComparison.Ordinal);

    private static bool IsValidIdentifier(string value)
    {
        if (value.Length is < 1 or > 128 || !IsAsciiAlphaNumeric(value[0])) return false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!(IsAsciiAlphaNumeric(c) || c is '.' or '_' or ':' or '-')) return false;
        }

        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
