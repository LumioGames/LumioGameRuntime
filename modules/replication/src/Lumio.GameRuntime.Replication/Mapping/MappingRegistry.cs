using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct MappingRegistrationResult(bool Succeeded, string? GeneratedErrorId, string? Detail)
{
    public static MappingRegistrationResult Accepted() => new(true, null, null);

    public static MappingRegistrationResult Rejected(string errorId, string detail) => new(false, errorId, detail);
}

public sealed class MappingRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MappingDescriptor> _mappings = new(StringComparer.Ordinal);

    public MappingSetView View
    {
        get { lock (_gate) return new MappingSetView(_mappings.Values); }
    }

    public MappingRegistrationResult Register(MappingDescriptor mapping)
    {
        if (mapping is null) return MappingRegistrationResult.Rejected("InvalidArgument", "Mapping is required.");
        if (!mapping.IsValid(out string detail)) return MappingRegistrationResult.Rejected("ManifestMalformed", detail);
        lock (_gate)
        {
            if (_mappings.ContainsKey(mapping.MappingId)) return MappingRegistrationResult.Rejected("InvalidArgument", "MappingId is already registered.");
            _mappings.Add(mapping.MappingId, mapping);
            return MappingRegistrationResult.Accepted();
        }
    }

    public bool TryGet(string mappingId, out MappingDescriptor? mapping)
    {
        lock (_gate) return _mappings.TryGetValue(mappingId, out mapping);
    }
}
