using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Command;

public enum CommandKind
{
    Create,
    Write,
    Destroy
}

public readonly record struct CommandPayload(string TypeId, ReadOnlyMemory<byte> Bytes)
{
    public bool IsWellFormed => !string.IsNullOrWhiteSpace(TypeId) && Bytes.Length >= 0;
}

/// <summary>The only ordering key used when buffers are merged.</summary>
public readonly record struct CommandSortKey : IComparable<CommandSortKey>
{
    public CommandSortKey(ProcessorDescriptorPhase phase, string processorId, ulong localSequence)
    {
        if ((int)phase < (int)ProcessorDescriptorPhase.IngressCapture ||
            (int)phase > (int)ProcessorDescriptorPhase.EgressPublish)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (string.IsNullOrWhiteSpace(processorId) || !CommandValidation.IsIdentifier(processorId))
        {
            throw new ArgumentException("A processor ID is required.", nameof(processorId));
        }

        Phase = phase;
        ProcessorId = processorId;
        LocalSequence = localSequence;
    }

    public CommandSortKey(string phase, string processorId, ulong localSequence)
        : this(ParsePhase(phase), processorId, localSequence)
    {
    }

    public ProcessorDescriptorPhase Phase { get; }

    public string ProcessorId { get; }

    public ulong Sequence => LocalSequence;

    public ulong LocalSequence { get; }

    public int CompareTo(CommandSortKey other)
    {
        int phase = Phase.CompareTo(other.Phase);
        if (phase != 0) return phase;
        int processor = StringComparer.Ordinal.Compare(ProcessorId, other.ProcessorId);
        return processor != 0 ? processor : LocalSequence.CompareTo(other.LocalSequence);
    }

    public static bool operator <(CommandSortKey left, CommandSortKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(CommandSortKey left, CommandSortKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(CommandSortKey left, CommandSortKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(CommandSortKey left, CommandSortKey right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Concat(
        ProcessorDescriptorPhaseWire.Value(Phase), ":", ProcessorId, ":",
        LocalSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static ProcessorDescriptorPhase ParsePhase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A phase is required.", nameof(value));
        foreach (ProcessorDescriptorPhase phase in GetPhases())
        {
            if (string.Equals(ProcessorDescriptorPhaseWire.Value(phase), value, StringComparison.Ordinal)) return phase;
        }

        throw new ArgumentException("Unknown generated processor phase.", nameof(value));
    }

#if NET10_0_OR_GREATER
    private static ProcessorDescriptorPhase[] GetPhases() => Enum.GetValues<ProcessorDescriptorPhase>();
#else
    private static IEnumerable<ProcessorDescriptorPhase> GetPhases() =>
        (ProcessorDescriptorPhase[])Enum.GetValues(typeof(ProcessorDescriptorPhase));
#endif
}

/// <summary>Immutable, typed command entry. Payload bytes are explicit canonical input.</summary>
public class Command : IEquatable<Command>
{
    private readonly byte[] _payload;
    private readonly byte[] _canonicalBytes;
    private readonly byte[] _canonicalDigest;

    public Command(
        CommandKind kind,
        CommandSortKey sortKey,
        string? targetEntityId = null,
        string? componentType = null,
        string? fieldName = null,
        ReadOnlyMemory<byte> payload = default,
        DeferredEntityToken? deferredTarget = null,
        string? commandId = null,
        ulong? estimatedBytes = null)
    {
        Kind = kind;
        SortKey = sortKey;
        TargetEntityId = targetEntityId;
        ComponentType = componentType;
        FieldName = fieldName;
        DeferredTarget = deferredTarget;
        CommandId = commandId;
        _payload = payload.ToArray();
        EstimatedBytes = estimatedBytes ?? ComputeEstimatedBytes();
        _canonicalBytes = BuildCanonicalBytes();
        _canonicalDigest = CommandHashing.Sha256(_canonicalBytes);
    }

    public CommandKind Kind { get; }

    public CommandSortKey SortKey { get; }

    public string? TargetEntityId { get; }

    public string? Target => TargetEntityId;

    public string? ComponentType { get; }

    public string? ComponentId => ComponentType;

    public string? FieldName { get; }

    public string? FieldId => FieldName;

    public ReadOnlyMemory<byte> Payload => _payload;

    public ReadOnlyMemory<byte> Value => _payload;

    public DeferredEntityToken? DeferredTarget { get; }

    public string? CommandId { get; }

    public ulong EstimatedBytes { get; }

    public ReadOnlyMemory<byte> CanonicalBytes => _canonicalBytes;

    public ReadOnlyMemory<byte> CanonicalDigest => _canonicalDigest;

    public string CanonicalDigestHex => CommandHashing.ToHex(_canonicalDigest);

    public bool IsStructural => Kind is CommandKind.Create or CommandKind.Destroy;

    public bool Equals(Command? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || Kind != other.Kind || !SortKey.Equals(other.SortKey) ||
            !string.Equals(TargetEntityId, other.TargetEntityId, StringComparison.Ordinal) ||
            !string.Equals(ComponentType, other.ComponentType, StringComparison.Ordinal) ||
            !string.Equals(FieldName, other.FieldName, StringComparison.Ordinal) ||
            !Nullable.Equals(DeferredTarget, other.DeferredTarget) ||
            !string.Equals(CommandId, other.CommandId, StringComparison.Ordinal) ||
            !Payload.Span.SequenceEqual(other.Payload.Span)) return false;

        return true;
    }

    public override bool Equals(object? obj) => obj is Command other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine((int)Kind, SortKey, TargetEntityId, ComponentType, FieldName, CommandId);

    public override string ToString() => string.Concat(Kind, "@", SortKey);

    private ulong ComputeEstimatedBytes()
    {
        long value = 1L + 8L + 8L + Encoding.UTF8.GetByteCount(SortKey.ProcessorId);
        value += LengthOf(TargetEntityId) + LengthOf(ComponentType) + LengthOf(FieldName) + LengthOf(CommandId);
        value += DeferredTarget is null ? 0 : Encoding.UTF8.GetByteCount(DeferredTarget.Value.CanonicalKey);
        value += _payload.Length;
        return checked((ulong)value);
    }

    private byte[] BuildCanonicalBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)Kind);
        writer.Write((int)SortKey.Phase);
        WriteString(writer, SortKey.ProcessorId);
        writer.Write(SortKey.LocalSequence);
        WriteString(writer, TargetEntityId);
        WriteString(writer, ComponentType);
        WriteString(writer, FieldName);
        WriteString(writer, CommandId);
        if (DeferredTarget is DeferredEntityToken token)
        {
            writer.Write((byte)1);
            writer.Write(token.TickId);
            WriteString(writer, token.WorldId);
            WriteString(writer, token.ProcessorId);
            writer.Write(token.BufferGeneration);
            writer.Write(token.LocalSequence);
        }
        else
        {
            writer.Write((byte)0);
        }

        writer.Write(_payload.Length);
        writer.Write(_payload);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static int LengthOf(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
}

public static class CommandCanonical
{
    public static ReadOnlyMemory<byte> Digest(IEnumerable<Command> commands)
        => Digest(commands, "default");

    public static ReadOnlyMemory<byte> Digest(IEnumerable<Command> commands, string worldId)
    {
        using var stream = new MemoryStream();
        byte[] worldBytes = Encoding.UTF8.GetBytes(worldId ?? "default");
        stream.WriteByte((byte)worldBytes.Length);
        stream.Write(worldBytes, 0, worldBytes.Length);
        foreach (Command command in commands)
        {
            byte[] bytes = command.CanonicalBytes.ToArray();
            stream.WriteByte((byte)bytes.Length);
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)(bytes.Length >> 16));
            stream.WriteByte((byte)(bytes.Length >> 24));
            stream.Write(bytes, 0, bytes.Length);
        }

        return CommandHashing.Sha256(stream.ToArray());
    }
}

internal static class CommandHashing
{
    internal static byte[] Sha256(byte[] bytes)
    {
#if NET10_0_OR_GREATER
        return SHA256.HashData(bytes);
#else
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(bytes);
#endif
    }

    internal static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
