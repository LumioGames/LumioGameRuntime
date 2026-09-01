using System;
using System.Collections.Generic;
using Lumio.GameRuntime.GeneratedContracts;

namespace Lumio.GameRuntime.Gas;

/// <summary>Caller-supplied generated type metadata. Bytes are copied at registration.</summary>
public readonly record struct GasTypeDescriptor(
    string SchemaId,
    uint SchemaVersion,
    int SchemaEpoch,
    ReadOnlyMemory<byte> CanonicalBytes);

/// <summary>Type registration outcome. Compatible duplicates are not errors.</summary>
public enum GasRegistrationStatus
{
    Registered,
    AlreadyRegistered,
    Rejected,
    Fatal
}

/// <summary>Type registration result. Fatal conflicts keep the original descriptor.</summary>
public readonly record struct GasRegistrationResult(
    GasRegistrationStatus Status,
    string? GeneratedErrorId)
{
    public bool Succeeded => Status is GasRegistrationStatus.Registered or GasRegistrationStatus.AlreadyRegistered;

    public static GasRegistrationResult Registered() => new(GasRegistrationStatus.Registered, null);

    public static GasRegistrationResult AlreadyRegistered() => new(GasRegistrationStatus.AlreadyRegistered, null);

    public static GasRegistrationResult Rejected(string generatedErrorId) =>
        new(GasRegistrationStatus.Rejected, generatedErrorId);

    public static GasRegistrationResult Fatal(string generatedErrorId) =>
        new(GasRegistrationStatus.Fatal, generatedErrorId);
}

/// <summary>Generated Ability/Effect metadata registry. Frozen at Ready and then immutable.</summary>
public sealed class GasTypeRegistry
{
    private readonly Dictionary<uint, GasTypeDescriptor> _abilities = new();
    private readonly Dictionary<uint, GasTypeDescriptor> _effects = new();
    private bool _frozen;

    public bool IsFrozen => _frozen;

    public GasRegistrationResult RegisterAbility(AbilityTypeId typeId, in GasTypeDescriptor descriptor) =>
        Register(_abilities, typeId.Value, typeId.IsDefault, descriptor);

    public GasRegistrationResult RegisterEffect(EffectTypeId typeId, in GasTypeDescriptor descriptor) =>
        Register(_effects, typeId.Value, typeId.IsDefault, descriptor);

    public bool TryGetAbility(AbilityTypeId typeId, out GasTypeDescriptor descriptor) =>
        _abilities.TryGetValue(typeId.Value, out descriptor);

    public bool TryGetEffect(EffectTypeId typeId, out GasTypeDescriptor descriptor) =>
        _effects.TryGetValue(typeId.Value, out descriptor);

    public IReadOnlyList<AbilityTypeId> EnumerateAbilitiesCanonical()
    {
        var ids = new AbilityTypeId[_abilities.Count];
        int index = 0;
        foreach (uint value in _abilities.Keys)
        {
            ids[index++] = new AbilityTypeId(value);
        }

        Array.Sort(ids, CompareAbility);
        return ids;
    }

    public IReadOnlyList<EffectTypeId> EnumerateEffectsCanonical()
    {
        var ids = new EffectTypeId[_effects.Count];
        int index = 0;
        foreach (uint value in _effects.Keys)
        {
            ids[index++] = new EffectTypeId(value);
        }

        Array.Sort(ids, CompareEffect);
        return ids;
    }

    internal void Freeze() => _frozen = true;

    private GasRegistrationResult Register(
        Dictionary<uint, GasTypeDescriptor> table,
        uint typeId,
        bool typeIdIsDefault,
        in GasTypeDescriptor descriptor)
    {
        if (_frozen)
            return GasRegistrationResult.Rejected(GasErrorIds.InvalidArgument);
        if (typeIdIsDefault || string.IsNullOrWhiteSpace(descriptor.SchemaId))
            return GasRegistrationResult.Rejected(GasErrorIds.InvalidArgument);
        if (descriptor.SchemaEpoch != GeneratedContractManifest.SchemaEpoch)
            return GasRegistrationResult.Rejected(GasErrorIds.StaleEpoch);

        var stored = new GasTypeDescriptor(
            descriptor.SchemaId,
            descriptor.SchemaVersion,
            descriptor.SchemaEpoch,
            descriptor.CanonicalBytes.ToArray());

        if (table.TryGetValue(typeId, out GasTypeDescriptor existing))
        {
            return AreCompatible(existing, stored)
                ? GasRegistrationResult.AlreadyRegistered()
                : GasRegistrationResult.Fatal(GasErrorIds.PackageIdentityConflict);
        }

        table.Add(typeId, stored);
        return GasRegistrationResult.Registered();
    }

    private static bool AreCompatible(in GasTypeDescriptor left, in GasTypeDescriptor right) =>
        string.Equals(left.SchemaId, right.SchemaId, StringComparison.Ordinal) &&
        left.SchemaVersion == right.SchemaVersion &&
        left.SchemaEpoch == right.SchemaEpoch &&
        left.CanonicalBytes.Span.SequenceEqual(right.CanonicalBytes.Span);

    private static int CompareAbility(AbilityTypeId left, AbilityTypeId right) => left.Value.CompareTo(right.Value);

    private static int CompareEffect(EffectTypeId left, EffectTypeId right) => left.Value.CompareTo(right.Value);
}
