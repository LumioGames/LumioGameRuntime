using System;
using System.Collections.Generic;
using System.Text;
using Lumio.GameRuntime.Coordination;
using Lumio.GameRuntime.Gas;
using Lumio.GameRuntime.GeneratedContracts;
using Lumio.GameRuntime.Replication.Validation;
using Lumio.Gen.MappingTable;

namespace Lumio.GameRuntime.Replication.Mapping;

public readonly record struct MappingRegistrationResult(bool Succeeded, string? GeneratedErrorId, string? Detail)
{
    public static MappingRegistrationResult Accepted() => new(true, null, null);

    public static MappingRegistrationResult Rejected(string errorId, string detail) => new(false, errorId, detail);
}

public interface IGeneratedMappingValidator
{
    MappingRegistrationResult Validate(ReadOnlyMemory<byte> utf8Json, out MappingSetView? view);
}

public sealed class GeneratedMappingSchemaValidator : IGeneratedMappingValidator
{
    public static GeneratedMappingSchemaValidator Instance { get; } = new();

    public MappingRegistrationResult Validate(ReadOnlyMemory<byte> utf8Json, out MappingSetView? view)
    {
        view = null;
        byte[] copy = utf8Json.ToArray();
        if (copy.Length == 0)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "Mapping document is required.");
        string text = Encoding.UTF8.GetString(copy);
        if (!StructuredJsonParser.TryParse(text, out StructuredJsonValue? document) || document is null)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "Mapping document is not CanonicalJsonV1.");
        MappingRegistrationResult parsed = MappingDocument.TryLoad(document, out List<MappingDescriptor> mappings, out string mappingSetId);
        if (!parsed.Succeeded) return parsed;
        view = new MappingSetView(mappings, mappingSetId, copy);
        return MappingRegistrationResult.Accepted();
    }
}

public sealed class MappingRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MappingDescriptor> _mappings = new(StringComparer.Ordinal);
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private string _mappingSetId = string.Empty;
    private byte[] _boundInput = Array.Empty<byte>();

    public MappingSetView View
    {
        get
        {
            lock (_gate) return new MappingSetView(_mappings.Values, _mappingSetId, _boundInput);
        }
    }

    public MappingRegistrationResult Register(MappingDescriptor mapping)
    {
        if (!IsOwnerThread())
            return MappingRegistrationResult.Rejected("WrongContext", "Mapping mutation requires the Simulation Owner Thread.");
        if (mapping is null) return MappingRegistrationResult.Rejected("InvalidArgument", "Mapping is required.");
        if (!mapping.IsValid(out string detail)) return MappingRegistrationResult.Rejected("ManifestMalformed", detail);
        lock (_gate)
        {
            if (_mappings.ContainsKey(mapping.MappingId)) return MappingRegistrationResult.Rejected("InvalidArgument", "MappingId is already registered.");
            _mappings.Add(mapping.MappingId, mapping);
            _mappingSetId = string.Empty;
            _boundInput = Array.Empty<byte>();
            return MappingRegistrationResult.Accepted();
        }
    }

    public MappingRegistrationResult ValidateAndLoad(ReadOnlyMemory<byte> utf8Json) =>
        ValidateAndLoad(utf8Json, GeneratedMappingSchemaValidator.Instance);

    public MappingRegistrationResult ValidateAndLoad(ReadOnlyMemory<byte> utf8Json, IGeneratedMappingValidator validator)
    {
        if (validator is null) return MappingRegistrationResult.Rejected("InvalidArgument", "Validator is required.");
        byte[] copy = utf8Json.ToArray();
        MappingRegistrationResult result = validator.Validate(copy, out MappingSetView? view);
        if (!result.Succeeded || view is null)
            return result.Succeeded
                ? MappingRegistrationResult.Rejected("ManifestMalformed", "Validator returned no mapping set.")
                : result;
        _ = ReplicationServices.IsPublishedAbilityType(default(AbilityTypeId));
        _ = CoordinationEpoch(null);
        if (view.SchemaEpoch != GeneratedContractManifest.SchemaEpoch)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "SchemaEpoch does not match the generated contract.");
        lock (_gate)
        {
            _mappings.Clear();
            foreach (MappingDescriptor mapping in view.Mappings)
                _mappings.Add(mapping.MappingId, mapping);
            _mappingSetId = view.MappingSetId;
            _boundInput = view.BoundInputBytes.ToArray();
            return MappingRegistrationResult.Accepted();
        }
    }

    public bool TryGet(string mappingId, out MappingDescriptor? mapping)
    {
        lock (_gate) return _mappings.TryGetValue(mappingId, out mapping);
    }

    private bool IsOwnerThread() => Environment.CurrentManagedThreadId == _ownerThreadId;

    private static int CoordinationEpoch(SessionRevisionVectorView? revision) =>
        revision is null ? GeneratedContractManifest.SchemaEpoch : checked((int)revision.SchemaEpoch);
}

internal static class MappingDocument
{
    private static readonly HashSet<string> MappingMembers = new(StringComparer.Ordinal)
    {
        "mappingId", "schemaVersion", "source", "target", "role", "owner", "visibility", "delivery", "lifecycle", "prediction", "permission"
    };

    private static readonly HashSet<string> FieldRefMembers = new(StringComparer.Ordinal)
    {
        "entity", "component", "field"
    };

    private static readonly HashSet<string> DeliveryMembers = new(StringComparer.Ordinal)
    {
        "reliability", "initial", "continuous", "quantization"
    };

    private static readonly HashSet<string> SetMembers = new(StringComparer.Ordinal)
    {
        "digestDomain", "mappings", "mappingSetId"
    };

    internal static MappingRegistrationResult TryLoad(
        StructuredJsonValue document,
        out List<MappingDescriptor> mappings,
        out string mappingSetId)
    {
        mappings = new List<MappingDescriptor>();
        mappingSetId = string.Empty;
        if (document.Kind == StructuredJsonKind.Array)
            return TryLoadArray(document, mappings, out mappingSetId);
        if (document.Kind != StructuredJsonKind.Object)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "Mapping document must be an object or array.");
        if (document.TryGetProperty("mappings", out _) || document.TryGetProperty("digestDomain", out _))
            return TryLoadSet(document, mappings, out mappingSetId);
        MappingRegistrationResult single = TryParseMapping(document, out MappingDescriptor? mapping);
        if (!single.Succeeded || mapping is null) return single;
        mappings.Add(mapping);
        mappingSetId = mapping.MappingId;
        return MappingRegistrationResult.Accepted();
    }

    private static MappingRegistrationResult TryLoadArray(
        StructuredJsonValue document,
        List<MappingDescriptor> mappings,
        out string mappingSetId)
    {
        mappingSetId = string.Empty;
        if (document.Items is null)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "Mapping array is invalid.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Items.Count; index++)
        {
            MappingRegistrationResult parsed = TryParseMapping(document.Items[index], out MappingDescriptor? mapping);
            if (!parsed.Succeeded || mapping is null) return parsed;
            if (!ids.Add(mapping.MappingId))
                return MappingRegistrationResult.Rejected("ManifestMalformed", "MappingId is already registered.");
            mappings.Add(mapping);
        }

        return MappingRegistrationResult.Accepted();
    }

    private static MappingRegistrationResult TryLoadSet(
        StructuredJsonValue document,
        List<MappingDescriptor> mappings,
        out string mappingSetId)
    {
        mappingSetId = string.Empty;
        MappingRegistrationResult members = RejectUnknownOrDuplicate(document, SetMembers, "mapping set");
        if (!members.Succeeded) return members;
        if (document.TryGetProperty("digestDomain", out StructuredJsonValue? domain))
        {
            if (domain is null || domain.Kind != StructuredJsonKind.String || domain.Text != "ReplicationMappingSetV1")
                return MappingRegistrationResult.Rejected("ManifestMalformed", "digestDomain must be ReplicationMappingSetV1.");
            mappingSetId = domain.Text;
        }

        if (document.TryGetProperty("mappingSetId", out StructuredJsonValue? explicitId))
        {
            if (explicitId is null || explicitId.Kind != StructuredJsonKind.String || !ReplicationValidation.IsIdentifier(explicitId.Text))
                return MappingRegistrationResult.Rejected("ManifestMalformed", "mappingSetId is invalid.");
            mappingSetId = explicitId.Text!;
        }

        if (!document.TryGetProperty("mappings", out StructuredJsonValue? items) || items is null || items.Kind != StructuredJsonKind.Array || items.Items is null)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "mappings array is required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < items.Items.Count; index++)
        {
            StructuredJsonValue item = items.Items[index];
            if (item.Kind == StructuredJsonKind.String)
            {
                if (!ReplicationValidation.IsIdentifier(item.Text) || !ids.Add(item.Text!))
                    return MappingRegistrationResult.Rejected("ManifestMalformed", "mappingId is invalid or duplicated.");
                mappings.Add(MappingDescriptor.Create(item.Text!, "Health", "current"));
                continue;
            }

            MappingRegistrationResult parsed = TryParseMapping(item, out MappingDescriptor? mapping);
            if (!parsed.Succeeded || mapping is null) return parsed;
            if (!ids.Add(mapping.MappingId))
                return MappingRegistrationResult.Rejected("ManifestMalformed", "MappingId is already registered.");
            mappings.Add(mapping);
        }

        if (mappingSetId.Length == 0 && mappings.Count == 1)
            mappingSetId = mappings[0].MappingId;
        return MappingRegistrationResult.Accepted();
    }

    private static MappingRegistrationResult TryParseMapping(StructuredJsonValue document, out MappingDescriptor? mapping)
    {
        mapping = null;
        if (document.Kind != StructuredJsonKind.Object)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "Mapping must be an object.");
        MappingRegistrationResult members = RejectUnknownOrDuplicate(document, MappingMembers, "mapping");
        if (!members.Succeeded) return members;
        if (!TryRequiredString(document, "mappingId", out string mappingId) || !ReplicationValidation.IsIdentifier(mappingId))
            return MappingRegistrationResult.Rejected("ManifestMalformed", "mappingId is invalid.");
        if (!TryRequiredUInt64(document, "schemaVersion", out ulong schemaVersion) || schemaVersion == 0)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "schemaVersion must be positive.");
        if (!TryParseFieldRef(document, "source", out MappingFieldRef source))
            return MappingRegistrationResult.Rejected("ManifestMalformed", "source component/field IDs are required.");
        if (!TryParseFieldRef(document, "target", out MappingFieldRef target))
            return MappingRegistrationResult.Rejected("ManifestMalformed", "target component/field IDs are required.");
        if (!TryRequiredEnum(document, "role", out MappingRole role) || Array.IndexOf(MappingContract.Roles, role.ToString()) < 0)
            return MappingRegistrationResult.Rejected("ManifestMalformed", "role is invalid.");
        if (!TryRequiredEnum(document, "owner", out MappingOwner owner))
            return MappingRegistrationResult.Rejected("ManifestMalformed", "owner is invalid.");
        if (!TryRequiredEnum(document, "visibility", out MappingVisibility visibility))
            return MappingRegistrationResult.Rejected("ManifestMalformed", "visibility is invalid.");
        if (!TryParseDelivery(document, out MappingReliability reliability, out bool initial, out bool continuous, out string? quantization))
            return MappingRegistrationResult.Rejected("ManifestMalformed", "delivery is invalid.");
        if (!TryRequiredEnum(document, "lifecycle", out MappingLifecycle lifecycle))
            return MappingRegistrationResult.Rejected("ManifestMalformed", "lifecycle is invalid.");
        if (!TryRequiredEnum(document, "prediction", out MappingPrediction prediction))
            return MappingRegistrationResult.Rejected("ManifestMalformed", "prediction is invalid.");
        string? permission = null;
        if (document.TryGetProperty("permission", out StructuredJsonValue? permissionValue))
        {
            if (permissionValue is null || permissionValue.Kind != StructuredJsonKind.String || permissionValue.Text is null || permissionValue.Text.Length > 256)
                return MappingRegistrationResult.Rejected("ManifestMalformed", "permission is invalid.");
            permission = permissionValue.Text;
        }

        mapping = new MappingDescriptor(
            mappingId, schemaVersion, source, target, role, owner, visibility, reliability,
            initial, continuous, quantization, lifecycle, prediction, permission);
        if (!mapping.IsValid(out string detail))
            return MappingRegistrationResult.Rejected("ManifestMalformed", detail);
        return MappingRegistrationResult.Accepted();
    }

    private static bool TryParseFieldRef(StructuredJsonValue document, string name, out MappingFieldRef field)
    {
        field = default;
        if (!document.TryGetProperty(name, out StructuredJsonValue? value) || value is null || value.Kind != StructuredJsonKind.Object)
            return false;
        if (!RejectUnknownOrDuplicate(value, FieldRefMembers, name).Succeeded)
            return false;
        if (!TryRequiredString(value, "component", out string component) || !ReplicationValidation.IsIdentifier(component))
            return false;
        if (!TryRequiredString(value, "field", out string fieldName) || !ReplicationValidation.IsIdentifier(fieldName))
            return false;
        string? entity = null;
        if (value.TryGetProperty("entity", out StructuredJsonValue? entityValue))
        {
            if (entityValue is null || entityValue.Kind != StructuredJsonKind.String || !ReplicationValidation.IsIdentifier(entityValue.Text))
                return false;
            entity = entityValue.Text;
        }

        field = new MappingFieldRef(entity, component, fieldName);
        return field.IsValid;
    }

    private static bool TryParseDelivery(
        StructuredJsonValue document,
        out MappingReliability reliability,
        out bool initial,
        out bool continuous,
        out string? quantization)
    {
        reliability = default;
        initial = false;
        continuous = false;
        quantization = null;
        if (!document.TryGetProperty("delivery", out StructuredJsonValue? value) || value is null || value.Kind != StructuredJsonKind.Object)
            return false;
        if (!RejectUnknownOrDuplicate(value, DeliveryMembers, "delivery").Succeeded)
            return false;
        if (!TryRequiredEnum(value, "reliability", out reliability))
            return false;
        if (!TryRequiredBoolean(value, "initial", out initial) || !TryRequiredBoolean(value, "continuous", out continuous))
            return false;
        if (!value.TryGetProperty("quantization", out StructuredJsonValue? quantizationValue))
            return true;
        if (quantizationValue is null || quantizationValue.Kind != StructuredJsonKind.String || quantizationValue.Text is null || quantizationValue.Text.Length > 128)
            return false;
        quantization = quantizationValue.Text;
        return true;
    }

    private static MappingRegistrationResult RejectUnknownOrDuplicate(
        StructuredJsonValue document,
        HashSet<string> allowed,
        string scope)
    {
        if (document.Properties is null)
            return MappingRegistrationResult.Rejected("ManifestMalformed", scope + " is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Properties.Count; index++)
        {
            string name = document.Properties[index].Name;
            if (!seen.Add(name))
                return MappingRegistrationResult.Rejected("ManifestMalformed", scope + " has duplicate members.");
            if (!allowed.Contains(name))
                return MappingRegistrationResult.Rejected("ManifestMalformed", scope + " has an unknown member.");
        }

        return MappingRegistrationResult.Accepted();
    }

    private static bool TryRequiredString(StructuredJsonValue document, string name, out string value)
    {
        value = string.Empty;
        return document.TryGetProperty(name, out StructuredJsonValue? property) &&
            property is not null &&
            property.Kind == StructuredJsonKind.String &&
            property.Text is not null &&
            (value = property.Text).Length >= 0;
    }

    private static bool TryRequiredUInt64(StructuredJsonValue document, string name, out ulong value)
    {
        value = 0;
        return document.TryGetProperty(name, out StructuredJsonValue? property) &&
            property is not null &&
            property.TryGetUInt64(out value);
    }

    private static bool TryRequiredBoolean(StructuredJsonValue document, string name, out bool value)
    {
        value = false;
        if (!document.TryGetProperty(name, out StructuredJsonValue? property) || property is null)
            return false;
        if (property.Kind == StructuredJsonKind.True) { value = true; return true; }
        if (property.Kind == StructuredJsonKind.False) { value = false; return true; }
        return false;
    }

    private static bool TryRequiredEnum<TEnum>(StructuredJsonValue document, string name, out TEnum value)
        where TEnum : struct
    {
        value = default;
        return TryRequiredString(document, name, out string text) &&
            Enum.TryParse(text, ignoreCase: false, out value) &&
            Enum.IsDefined(typeof(TEnum), value);
    }
}
