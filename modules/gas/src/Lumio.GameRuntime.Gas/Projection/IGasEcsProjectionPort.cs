using System;
using Lumio.GameRuntime.Command;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Gas;

/// <summary>The four ECS containers GAS may project. fx_key is an Effect field, not FxComponent.</summary>
public static class GasEcsComponentTypes
{
    public const string Ability = "AbilityComponent";
    public const string Effect = "EffectComponent";
    public const string Attribute = "AttributeComponent";
    public const string Tag = "TagComponent";
}

/// <summary>Public Command/ECS field identity. ComponentFieldId is not a public ECS type.</summary>
public readonly record struct GasAuthoritativeField(string ComponentType, string FieldName)
{
    public static GasAuthoritativeField Ability(string fieldName) => new(GasEcsComponentTypes.Ability, fieldName);

    public static GasAuthoritativeField Effect(string fieldName) => new(GasEcsComponentTypes.Effect, fieldName);

    public static GasAuthoritativeField Attribute(string fieldName) => new(GasEcsComponentTypes.Attribute, fieldName);

    public static GasAuthoritativeField Tag(string fieldName) => new(GasEcsComponentTypes.Tag, fieldName);

    public bool IsAllowedComponent =>
        string.Equals(ComponentType, GasEcsComponentTypes.Ability, StringComparison.Ordinal) ||
        string.Equals(ComponentType, GasEcsComponentTypes.Effect, StringComparison.Ordinal) ||
        string.Equals(ComponentType, GasEcsComponentTypes.Attribute, StringComparison.Ordinal) ||
        string.Equals(ComponentType, GasEcsComponentTypes.Tag, StringComparison.Ordinal);
}

/// <summary>Authoritative field read. Canonical bytes are a copy of the projection payload.</summary>
public readonly record struct GasProjectionReadResult(
    bool Found,
    ReadOnlyMemory<byte> CanonicalValue,
    string? GeneratedErrorId)
{
    public static GasProjectionReadResult Present(ReadOnlyMemory<byte> canonicalValue) =>
        new(true, canonicalValue.ToArray(), null);

    public static GasProjectionReadResult Missing() => new(false, ReadOnlyMemory<byte>.Empty, null);

    public static GasProjectionReadResult Failed(string generatedErrorId) =>
        new(false, ReadOnlyMemory<byte>.Empty, generatedErrorId);
}

/// <summary>Authoritative field write. Success means the projection port accepted the bytes.</summary>
public readonly record struct GasProjectionWriteResult(bool Written, string? GeneratedErrorId)
{
    public static GasProjectionWriteResult Accepted() => new(true, null);

    public static GasProjectionWriteResult Rejected(string generatedErrorId) => new(false, generatedErrorId);
}

/// <summary>Narrow ECS/Command projection. GAS never keeps a second Attribute/Tag/Ability/Effect store.</summary>
public interface IGasEcsProjectionPort
{
    GasProjectionReadResult ReadAuthoritative(LocalEntityId entity, in GasAuthoritativeField field);

    GasProjectionWriteResult WriteAuthoritative(
        LocalEntityId entity,
        in GasAuthoritativeField field,
        ReadOnlySpan<byte> canonicalValue);
}

/// <summary>Write-only adapter over the public CommandBuffer writer. Reads do not invent a store.</summary>
public sealed class CommandBufferGasProjectionPort : IGasEcsProjectionPort
{
    private readonly CommandBufferWriter _writer;

    public CommandBufferGasProjectionPort(CommandBufferWriter writer)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(writer);
#else
        if (writer is null) throw new ArgumentNullException(nameof(writer));
#endif
        _writer = writer;
    }

    public GasProjectionReadResult ReadAuthoritative(LocalEntityId entity, in GasAuthoritativeField field) =>
        GasProjectionReadResult.Missing();

    public GasProjectionWriteResult WriteAuthoritative(
        LocalEntityId entity,
        in GasAuthoritativeField field,
        ReadOnlySpan<byte> canonicalValue)
    {
        if (!field.IsAllowedComponent || string.IsNullOrWhiteSpace(field.FieldName) || entity.IsDefault)
            return GasProjectionWriteResult.Rejected(GasErrorIds.InvalidArgument);

        CommandAppendResult appended = _writer.Write(
            entity.ToString(),
            field.ComponentType,
            field.FieldName,
            canonicalValue.ToArray());
        return appended.IsAccepted
            ? GasProjectionWriteResult.Accepted()
            : GasProjectionWriteResult.Rejected(appended.GeneratedErrorId ?? GasErrorIds.InvalidArgument);
    }
}
