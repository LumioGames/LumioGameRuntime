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
    private static readonly string[] RequiredProviderIds =
    {
        "schemaEpoch",
        "seed",
        "epoch",
        "identity.session",
        "identity.world",
        "identity.release",
        "identity.manifest",
        "identity.config",
        "tick",
        "inputs",
        "revision.vector",
        "state.ecs",
        "state.command",
        "state.coordination",
        "state.voxel",
        "state.gas",
        "state.replication",
        "tokens.prepared.count",
        "tokens.participant.count",
        "outputs.count"
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _providers = new(StringComparer.Ordinal);
    private bool _sealed;
    private bool _authoritativeComplete;

    public int ProviderCount
    {
        get { lock (_gate) return _providers.Count; }
    }

    public void Register(string providerId, string canonicalValue)
    {
        if (!SimulationValidation.IsIdentifier(providerId)) throw new ArgumentException("A valid provider ID is required.", nameof(providerId));
        if (canonicalValue is null) throw new ArgumentNullException(nameof(canonicalValue));
        lock (_gate)
        {
            if (_sealed) throw new InvalidOperationException("The hash context is closed.");
            if (_providers.ContainsKey(providerId)) throw new InvalidOperationException("A provider may only be registered once per tick.");
            _providers.Add(providerId, canonicalValue);
            _authoritativeComplete = false;
        }
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
        KeyValuePair<string, string>[] providers;
        lock (_gate) providers = _providers.ToArray();
        return ComputeHashHex(providers);
    }

    public StateHashSummary CaptureSummary()
    {
        KeyValuePair<string, string>[] providers;
        bool authoritativeComplete;
        lock (_gate)
        {
            providers = _providers.ToArray();
            authoritativeComplete = _authoritativeComplete;
        }
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> provider in providers) snapshot.Add(provider.Key, provider.Value);
        return new StateHashSummary(
            ComputeHashHex(providers),
            providers.Length,
            authoritativeComplete && IsComplete(snapshot));
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate) return new Dictionary<string, string>(_providers, StringComparer.Ordinal);
    }

    internal void Seal()
    {
        lock (_gate) _sealed = true;
    }

    internal StateHashSummary CaptureAuthoritativeSummary()
    {
        KeyValuePair<string, string>[] providers;
        bool complete;
        lock (_gate)
        {
            providers = _providers.ToArray();
            var snapshot = new Dictionary<string, string>(_providers, StringComparer.Ordinal);
            complete = IsComplete(snapshot);
            _authoritativeComplete = complete;
        }

        return new StateHashSummary(ComputeHashHex(providers), providers.Length, complete);
    }

    private static string ComputeHashHex(IEnumerable<KeyValuePair<string, string>> providers)
    {
        using var stream = new System.IO.MemoryStream();
        foreach (KeyValuePair<string, string> entry in providers.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Write(stream, entry.Key);
            Write(stream, entry.Value);
        }

        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream.ToArray()));
    }

    private static bool IsComplete(IReadOnlyDictionary<string, string> providers)
    {
        foreach (string required in RequiredProviderIds)
            if (!providers.ContainsKey(required)) return false;
        for (var index = 0; index < 13; index++)
            if (!providers.ContainsKey($"phase.{index:D2}")) return false;
        return HasIndexedProviders(providers, "tokens.prepared") &&
            HasIndexedProviders(providers, "tokens.participant") &&
            HasIndexedOutputProviders(providers);
    }

    private static bool HasIndexedProviders(IReadOnlyDictionary<string, string> providers, string prefix)
    {
        if (!int.TryParse(
            providers[prefix + ".count"],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int count) || count < 0)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
            if (!providers.ContainsKey($"{prefix}.{index:D6}")) return false;
        return true;
    }

    private static bool HasIndexedOutputProviders(IReadOnlyDictionary<string, string> providers)
    {
        if (!int.TryParse(
            providers["outputs.count"],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int count) || count < 0)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            string prefix = $"output.{index:D6}";
            if (!providers.ContainsKey(prefix + ".key") ||
                !providers.ContainsKey(prefix + ".length") ||
                !providers.ContainsKey(prefix + ".payload"))
            {
                return false;
            }
        }

        return true;
    }

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
