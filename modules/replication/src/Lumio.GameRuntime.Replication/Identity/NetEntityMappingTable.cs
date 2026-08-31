using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct NetEntityId(string Value)
{
    public bool IsValid => ReplicationValidation.IsNetId(Value);

    public static NetEntityId Parse(string value)
    {
        if (!ReplicationValidation.IsNetId(value)) throw new ArgumentException("A lowercase 128-bit NetEntityId is required.", nameof(value));
        return new NetEntityId(value);
    }

    public static bool TryParse(string? value, out NetEntityId id)
    {
        if (ReplicationValidation.IsNetId(value)) { id = new NetEntityId(value!); return true; }
        id = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct MappingBindingResult(bool Succeeded, string? GeneratedErrorId, string? Detail)
{
    public static MappingBindingResult Accepted() => new(true, null, null);

    public static MappingBindingResult Rejected(string errorId, string detail) => new(false, errorId, detail);
}

public sealed class NetEntityMappingTable
{
    private readonly object _gate = new();
    private readonly Dictionary<NetEntityId, LocalBinding> _byNet = new();
    private readonly Dictionary<string, NetEntityId> _byLocal = new(StringComparer.Ordinal);

    public int Count
    {
        get { lock (_gate) return _byNet.Count; }
    }

    public MappingBindingResult Bind(NetEntityId netEntityId, string localEntityId)
    {
        if (!netEntityId.IsValid || !TryParseLocal(localEntityId, out ulong index, out ulong generation))
            return MappingBindingResult.Rejected("ManifestMalformed", "NetEntityId or LocalEntityId is invalid.");
        lock (_gate)
        {
            if (_byNet.ContainsKey(netEntityId) || _byLocal.ContainsKey(localEntityId))
                return MappingBindingResult.Rejected("InvalidArgument", "The mapping is already bound.");
            _byNet.Add(netEntityId, new LocalBinding(localEntityId, generation));
            _byLocal.Add(localEntityId, netEntityId);
            return MappingBindingResult.Accepted();
        }
    }

    public MappingBindingResult Bind(string netEntityId, string localEntityId) =>
        NetEntityId.TryParse(netEntityId, out NetEntityId parsed)
            ? Bind(parsed, localEntityId)
            : MappingBindingResult.Rejected("ManifestMalformed", "NetEntityId is invalid.");

    public MappingBindingResult Bind(Lumio.Gen.ContractTypes.EntityIdentity identity)
    {
        Identity.EntityIdentityValidationResult validation = Identity.EntityIdentityValidator.Validate(identity);
        if (!validation.Succeeded || identity.LocalEntityId is null)
            return MappingBindingResult.Rejected(validation.GeneratedErrorId ?? "ManifestMalformed", validation.Detail ?? "Entity identity is invalid.");
        return Bind(identity.NetEntityId, identity.LocalEntityId);
    }

    public bool TryResolveLocal(NetEntityId netEntityId, ulong expectedGeneration, out string? localEntityId)
    {
        lock (_gate)
        {
            if (_byNet.TryGetValue(netEntityId, out LocalBinding? binding) && binding.Generation == expectedGeneration)
            {
                localEntityId = binding.LocalEntityId;
                return true;
            }
            localEntityId = null;
            return false;
        }
    }

    public bool TryResolveNet(string localEntityId, out NetEntityId netEntityId)
    {
        lock (_gate) return _byLocal.TryGetValue(localEntityId, out netEntityId);
    }

    public bool Remove(NetEntityId netEntityId)
    {
        lock (_gate)
        {
            if (!_byNet.Remove(netEntityId, out LocalBinding? binding)) return false;
            _byLocal.Remove(binding.LocalEntityId);
            return true;
        }
    }

    public IReadOnlyDictionary<NetEntityId, string> Snapshot()
    {
        lock (_gate)
        {
            var result = new Dictionary<NetEntityId, string>();
            foreach (KeyValuePair<NetEntityId, LocalBinding> item in _byNet) result.Add(item.Key, item.Value.LocalEntityId);
            return new ReadOnlyDictionary<NetEntityId, string>(result);
        }
    }

    private static bool TryParseLocal(string? value, out ulong index, out ulong generation)
    {
        index = 0;
        generation = 0;
        if (string.IsNullOrEmpty(value)) return false;
        int separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 || value.IndexOf(':', separator + 1) >= 0) return false;
        return ulong.TryParse(value.Substring(0, separator), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) &&
            ulong.TryParse(value.Substring(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out generation);
    }

    private sealed record LocalBinding(string LocalEntityId, ulong Generation);
}
