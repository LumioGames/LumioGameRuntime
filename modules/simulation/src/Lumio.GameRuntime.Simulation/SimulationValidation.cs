using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.GameRuntime.Simulation;

internal static class SimulationValidation
{
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

    internal static bool IsDiagnosticName(string value)
    {
        if (value.Length is < 2 or > 128 || !IsAsciiLetter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
        {
            char item = value[i];
            if (!(IsAsciiAlphaNumeric(item) || item is '.' or '_' or '-')) return false;
        }

        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value) => IsAsciiLetter(value) || value is >= '0' and <= '9';

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}

internal static class SimulationHash
{
    internal static string Sha256Hex(byte[] bytes)
    {
        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(bytes));
    }

    internal static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
