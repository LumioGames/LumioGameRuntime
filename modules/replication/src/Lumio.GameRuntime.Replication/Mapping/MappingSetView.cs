using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace Lumio.GameRuntime.Replication.Mapping;

public enum MappingRole
{
    ServerToClient,
    ClientToServer,
    SharedProjection
}

public enum MappingOwner
{
    OwnerOnly,
    ServerAuthority,
    AllClients,
    LocalOnly
}

public enum MappingVisibility
{
    AOI,
    Owner,
    Global,
    None
}

public enum MappingReliability
{
    Reliable,
    Unreliable,
    ReliableOnChange
}

public enum MappingLifecycle
{
    Spawn,
    Continuous,
    AddRemove,
    Tombstone
}

public enum MappingPrediction
{
    Authoritative,
    Predicted,
    PresentationOnly
}

public readonly record struct MappingFieldRef(string? Entity, string Component, string Field)
{
    public bool IsValid => !string.IsNullOrEmpty(Component) && !string.IsNullOrEmpty(Field) &&
        ReplicationValidation.IsIdentifier(Component) && ReplicationValidation.IsIdentifier(Field) &&
        (Entity is null || ReplicationValidation.IsIdentifier(Entity));
}

public sealed class MappingDescriptor
{
    public MappingDescriptor(
        string mappingId,
        ulong schemaVersion,
        MappingFieldRef source,
        MappingFieldRef target,
        MappingRole role,
        MappingOwner owner,
        MappingVisibility visibility,
        MappingReliability reliability,
        bool initial,
        bool continuous,
        string? quantization,
        MappingLifecycle lifecycle,
        MappingPrediction prediction,
        string? permission)
    {
        MappingId = mappingId;
        SchemaVersion = schemaVersion;
        Source = source;
        Target = target;
        Role = role;
        Owner = owner;
        Visibility = visibility;
        Reliability = reliability;
        Initial = initial;
        Continuous = continuous;
        Quantization = quantization;
        Lifecycle = lifecycle;
        Prediction = prediction;
        Permission = permission;
    }

    public string MappingId { get; }
    public ulong SchemaVersion { get; }
    public MappingFieldRef Source { get; }
    public MappingFieldRef Target { get; }
    public MappingRole Role { get; }
    public MappingOwner Owner { get; }
    public MappingVisibility Visibility { get; }
    public MappingReliability Reliability { get; }
    public bool Initial { get; }
    public bool Continuous { get; }
    public string? Quantization { get; }
    public MappingLifecycle Lifecycle { get; }
    public MappingPrediction Prediction { get; }
    public string? Permission { get; }

    public static MappingDescriptor Create(string mappingId, string component, string field) =>
        new(mappingId, 1, new MappingFieldRef("entity", component, field), new MappingFieldRef("entity", component, field), MappingRole.ServerToClient, MappingOwner.AllClients, MappingVisibility.Global, MappingReliability.ReliableOnChange, true, true, null, MappingLifecycle.Continuous, MappingPrediction.Authoritative, null);

    public bool IsValid(out string detail)
    {
        if (!ReplicationValidation.IsIdentifier(MappingId)) { detail = "mappingId is invalid."; return false; }
        if (SchemaVersion == 0) { detail = "schemaVersion must be positive."; return false; }
        if (!Source.IsValid || !Target.IsValid) { detail = "source and target component/field IDs are required."; return false; }
        if (!Enum.IsDefined(typeof(MappingRole), Role) || !Enum.IsDefined(typeof(MappingOwner), Owner) ||
            !Enum.IsDefined(typeof(MappingVisibility), Visibility) || !Enum.IsDefined(typeof(MappingReliability), Reliability) ||
            !Enum.IsDefined(typeof(MappingLifecycle), Lifecycle) || !Enum.IsDefined(typeof(MappingPrediction), Prediction))
        {
            detail = "mapping enum value is unknown.";
            return false;
        }
        if (Quantization is not null && Quantization.Length > 128) { detail = "quantization is too long."; return false; }
        if (Permission is not null && Permission.Length > 256) { detail = "permission is too long."; return false; }
        detail = string.Empty;
        return true;
    }
}

public sealed class MappingSetView
{
    private readonly ReadOnlyCollection<MappingDescriptor> _mappings;
    private readonly byte[] _canonicalBytes;

    internal MappingSetView(IEnumerable<MappingDescriptor> mappings)
    {
        var ordered = mappings.OrderBy(value => value.MappingId, StringComparer.Ordinal).ToList();
        _mappings = new ReadOnlyCollection<MappingDescriptor>(ordered);
        var builder = new StringBuilder("{\"digestDomain\":\"ReplicationMappingSetV1\",\"mappings\":[");
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append('"').Append(Escape(ordered[i].MappingId)).Append('"');
        }
        builder.Append("]}");
        _canonicalBytes = Encoding.UTF8.GetBytes(builder.ToString());
        MappingSetHash = ReplicationValidation.Sha256Hex((byte[])_canonicalBytes.Clone());
    }

    public static MappingSetView Empty { get; } = new(Array.Empty<MappingDescriptor>());

    public IReadOnlyList<MappingDescriptor> Mappings => _mappings;

    public IReadOnlyList<string> MappingIds => _mappings.Select(value => value.MappingId).ToArray();

    public string MappingSetHash { get; }

    public ReadOnlyMemory<byte> CanonicalBytes => (byte[])_canonicalBytes.Clone();

    public int SchemaEpoch => Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch;

    public ReadOnlyMemory<byte> GetCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public bool Contains(string mappingId)
    {
        foreach (MappingDescriptor value in _mappings) if (value.MappingId == mappingId) return true;
        return false;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
