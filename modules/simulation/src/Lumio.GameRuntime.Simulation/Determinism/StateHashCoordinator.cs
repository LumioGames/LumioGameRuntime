using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.GameRuntime.Simulation.Determinism;

public readonly record struct StateHashSummary(string HashHex, int ProviderCount, bool IsComplete);

/// <summary>Canonical hash registry. Registration order and diagnostic timing never affect the digest.</summary>
public sealed class StateHashCoordinator
{
    private readonly Dictionary<string, string> _providers = new(StringComparer.Ordinal);

    public int ProviderCount => _providers.Count;

    public void Register(string providerId, string canonicalValue)
    {
        if (!SimulationValidation.IsIdentifier(providerId)) throw new ArgumentException("A valid provider ID is required.", nameof(providerId));
        if (canonicalValue is null) throw new ArgumentNullException(nameof(canonicalValue));
        if (_providers.ContainsKey(providerId)) throw new InvalidOperationException("A provider may only be registered once per tick.");
        _providers.Add(providerId, canonicalValue);
    }

    public bool TryRegister(string providerId, string canonicalValue)
    {
        try
        {
            Register(providerId, canonicalValue);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void RegisterHashInput(string providerId, string canonicalValue) => Register(providerId, canonicalValue);

    public bool TryRegisterHashInput(string providerId, string canonicalValue) => TryRegister(providerId, canonicalValue);

    public string ComputeHashHex()
    {
        using var stream = new System.IO.MemoryStream();
        foreach (KeyValuePair<string, string> entry in _providers.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Write(stream, entry.Key);
            Write(stream, entry.Value);
        }

        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream.ToArray()));
    }

    public StateHashSummary CaptureSummary() => new(ComputeHashHex(), _providers.Count, true);

    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(_providers, StringComparer.Ordinal);

    private static void Write(System.IO.Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        ulong length = (ulong)bytes.Length;
        for (var shift = 0; shift < 64; shift += 8) stream.WriteByte((byte)(length >> shift));
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
