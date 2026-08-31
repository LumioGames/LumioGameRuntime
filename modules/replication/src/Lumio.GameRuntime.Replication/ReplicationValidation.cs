using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.GameRuntime.Replication;

internal static class ReplicationValidation
{
    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    internal static bool IsFiniteNonNegative(double value) => IsFinite(value) && value >= 0;

    internal static bool IsValidWorkItem(ReplicationWorkItem item) =>
        !string.IsNullOrWhiteSpace(item.Key) &&
        item.EstimatedBytes > 0 &&
        IsFinite(item.EnqueuedAtSeconds) &&
        IsFinite(item.AvailableAtSeconds);

    internal static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !IsAsciiAlphaNumeric(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
        {
            char item = value[i];
            if (!(IsAsciiAlphaNumeric(item) || item is '.' or '_' or ':' or '-')) return false;
        }
        return true;
    }

    internal static bool IsProductId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 32 && IsIdentifier(value) && value.IndexOf(':') < 0;

    internal static bool IsReleaseId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && IsIdentifier(value) && value.IndexOf(':') < 0;

    internal static bool IsTraceId(string? value) => IsIdentifier(value);

    internal static bool IsChunkId(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("c:", StringComparison.Ordinal)) return false;
        string[] parts = value.Split(':');
        if (parts.Length != 4) return false;
        for (var i = 1; i < parts.Length; i++)
            if (!IsCanonicalChunkCoordinate(parts[i])) return false;
        return true;
    }

    internal static bool IsHash256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (char item in value)
            if (!(item is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    internal static bool IsNetId(string? value)
    {
        if (value is null || value.Length != 32) return false;
        foreach (char item in value)
            if (!(item is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    internal static string Sha256Hex(byte[] value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(value);
        var builder = new StringBuilder(64);
        foreach (byte item in digest) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    internal static string Crc32CHex(byte[] value)
    {
        const uint polynomial = 0x82F63B78U;
        uint crc = 0xFFFFFFFFU;
        foreach (byte item in value)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1U) == 0 ? 0U : polynomial);
        }

        crc ^= 0xFFFFFFFFU;
        return crc.ToString("x8", CultureInfo.InvariantCulture);
    }

    internal static bool ConstantTimeEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length) return false;
        var difference = 0;
        for (var index = 0; index < left.Length; index++)
            difference |= left[index] ^ right[index];
        return difference == 0;
    }

    internal static string LengthPrefix(string value) => string.Concat(value.Length.ToString(CultureInfo.InvariantCulture), ":", value);

    private static bool IsCanonicalChunkCoordinate(string value)
    {
        if (value == "0") return true;
        if (string.IsNullOrEmpty(value)) return false;
        int start = value[0] == '-' ? 1 : 0;
        int digitCount = value.Length - start;
        if (digitCount is < 1 or > 10 || start == 1 && digitCount == 0) return false;
        if (value[start] is < '1' or > '9') return false;
        for (var index = start + 1; index < value.Length; index++)
            if (value[index] is < '0' or > '9') return false;
        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsAsciiAlphaNumeric(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}

public enum ReplicationFailureClass
{
    Rejected,
    Retryable,
    Fatal,
    InfrastructureFault
}

public sealed record ReplicationFailure(
    ReplicationFailureClass Class,
    string GeneratedErrorId,
    string Detail,
    string? Evidence = null)
{
    public static ReplicationFailure Rejected(string errorId, string detail, string? evidence = null) => new(ReplicationFailureClass.Rejected, errorId, detail, evidence);

    public static ReplicationFailure Retryable(string errorId, string detail, string? evidence = null) => new(ReplicationFailureClass.Retryable, errorId, detail, evidence);

    public static ReplicationFailure Fatal(string errorId, string detail, string? evidence = null) => new(ReplicationFailureClass.Fatal, errorId, detail, evidence);
}

public readonly record struct ReplicationOperationResult(bool Succeeded, ReplicationFailure? Failure)
{
    public static ReplicationOperationResult Accepted() => new(true, null);

    public static ReplicationOperationResult Rejected(string errorId, string detail) => new(false, ReplicationFailure.Rejected(errorId, detail));
}
