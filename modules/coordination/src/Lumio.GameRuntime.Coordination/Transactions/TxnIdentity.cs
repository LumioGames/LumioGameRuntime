using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.GameRuntime.Coordination;

internal sealed class TxnIdentity : IEquatable<TxnIdentity>
{
    private readonly byte[] _canonicalBytes;

    internal TxnIdentity(
        string sessionId,
        string gameReleaseId,
        string txnId,
        string commandId,
        ulong tickId,
        string requestDigest,
        string expectedRevisionDigest)
    {
        SessionId = Required(sessionId, nameof(sessionId));
        GameReleaseId = Required(gameReleaseId, nameof(gameReleaseId));
        TxnId = Required(txnId, nameof(txnId));
        CommandId = Required(commandId, nameof(commandId));
        RequestDigest = Required(requestDigest, nameof(requestDigest));
        ExpectedRevisionDigest = Required(expectedRevisionDigest, nameof(expectedRevisionDigest));
        TickId = tickId;
        _canonicalBytes = Encode();
        DigestHex = Hex(Hash(_canonicalBytes));
    }

    internal string SessionId { get; }

    internal string GameReleaseId { get; }

    internal string TxnId { get; }

    internal string CommandId { get; }

    internal ulong TickId { get; }

    internal string RequestDigest { get; }

    internal string ExpectedRevisionDigest { get; }

    internal string DigestHex { get; }

    internal ReadOnlyMemory<byte> CanonicalBytes => _canonicalBytes;

    internal static TxnIdentity From(TxnRecord record)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(record);
#else
        if (record is null) throw new ArgumentNullException(nameof(record));
#endif
        return new TxnIdentity(
            record.SessionId,
            record.GameReleaseId,
            record.TxnId,
            record.CommandId,
            record.TickId,
            record.RequestDigest,
            record.ExpectedRevision.CanonicalDigestHex);
    }

    internal bool Matches(TxnRecord record) => record is not null && Equals(From(record));

    public bool Equals(TxnIdentity? other) =>
        other is not null && _canonicalBytes.AsSpan().SequenceEqual(other._canonicalBytes);

    public override bool Equals(object? obj) => obj is TxnIdentity other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(DigestHex);

    // Internal identity key for journal proofs. Not LumioBinV1 (that profile is LittleEndian).
    private byte[] Encode()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(1);
        Write(stream, SessionId);
        Write(stream, GameReleaseId);
        Write(stream, TxnId);
        Write(stream, CommandId);
        Span<byte> tick = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(tick, TickId);
        stream.Write(tick);
        Write(stream, RequestDigest);
        Write(stream, ExpectedRevisionDigest);
        return stream.ToArray();
    }

    private static void Write(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A transaction identity field is required.", parameterName)
            : value;

    private static byte[] Hash(byte[] bytes)
    {
#if NET10_0_OR_GREATER
        return SHA256.HashData(bytes);
#else
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(bytes);
#endif
    }

    private static string Hex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}

/// <summary>
/// Request digest hasher used until Task 20 injects the Persistence canonical Port.
/// SHA-256 over UTF-8 fields joined by LF (U+000A), no trailing LF:
/// sessionId, gameReleaseId, txnId, commandId, tickId (decimal invariant), expectedRevisionDigest.
/// This is not LumioBinV1. A caller-supplied 64-hex digest is stored opaquely on the record.
/// </summary>
internal static class TxnRequestDigest
{
    internal static string HashHex(
        string sessionId,
        string gameReleaseId,
        string txnId,
        string commandId,
        ulong tickId,
        string expectedRevisionDigest)
    {
        string payload = string.Concat(
            sessionId,
            "\n",
            gameReleaseId,
            "\n",
            txnId,
            "\n",
            commandId,
            "\n",
            tickId.ToString(CultureInfo.InvariantCulture),
            "\n",
            expectedRevisionDigest);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
#if NET10_0_OR_GREATER
        byte[] hash = SHA256.HashData(bytes);
#else
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
#endif
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte value in hash)
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
