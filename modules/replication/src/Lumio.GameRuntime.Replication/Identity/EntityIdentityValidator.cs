using System;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Replication.Identity;

public enum EntityIdentityValidationCode
{
    Accepted,
    Invalid,
    NamespaceMismatch,
    TombstoneConflict,
    RemapConflict
}

public readonly record struct EntityIdentityValidationResult(
    EntityIdentityValidationCode Code,
    string? Detail,
    string? GeneratedErrorId)
{
    public bool Succeeded => Code == EntityIdentityValidationCode.Accepted;

    public static EntityIdentityValidationResult Accepted() => new(EntityIdentityValidationCode.Accepted, null, null);

    public static EntityIdentityValidationResult Rejected(EntityIdentityValidationCode code, string detail, string errorId = "ManifestMalformed") => new(code, detail, errorId);
}

/// <summary>Semantic checks frozen by the generated entity-identity contract.</summary>
public static class EntityIdentityValidator
{
    public static EntityIdentityValidationResult Validate(EntityIdentity identity)
    {
        if (identity is null) return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.Invalid, "Entity identity is required.");
        if (!Mapping.NetEntityId.TryParse(identity.NetEntityId, out _) || !IsIdentifier(identity.AuthorityDomain))
            return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.Invalid, "Entity identity identifiers are invalid.");
        if (!Enum.IsDefined(typeof(EntityIdentityNamespace), identity.Namespace) || !Enum.IsDefined(typeof(EntityIdentityLifecycle), identity.Lifecycle))
            return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.Invalid, "Entity identity enum value is unknown.");
        if (identity.LocalEntityId is not null && !IsLocalEntityId(identity.LocalEntityId))
            return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.Invalid, "LocalEntityId is invalid.");
        if (identity.Lifecycle == EntityIdentityLifecycle.Alive && identity.TombstoneUntilRevision.HasValue)
            return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.TombstoneConflict, "Alive entities cannot retain a tombstone horizon.");
        if (identity.Lifecycle == EntityIdentityLifecycle.Tombstoned && !identity.TombstoneUntilRevision.HasValue)
            return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.TombstoneConflict, "Tombstoned entities require a tombstone horizon.");
        if (identity.Namespace == EntityIdentityNamespace.Provisional && !identity.AuthorityDomain.StartsWith("client-", StringComparison.Ordinal))
            return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.NamespaceMismatch, "Provisional entities require a client authority domain.");
        if (identity.Namespace == EntityIdentityNamespace.Authoritative && identity.AuthorityDomain.StartsWith("client-", StringComparison.Ordinal))
            return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.NamespaceMismatch, "Authoritative entities cannot use a client authority domain.");
        if (identity.Namespace == EntityIdentityNamespace.Replay && (!identity.SourceRevision.HasValue || !ReplicationValidation.IsReleaseId(identity.SourceReleaseId)))
            return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.NamespaceMismatch, "Replay entities require source revision and release.");
        if (identity.RemappedFrom is not null)
        {
            if (!Mapping.NetEntityId.TryParse(identity.RemappedFrom, out _))
                return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.RemapConflict, "RemappedFrom is invalid.");
            if (identity.RemappedFrom == identity.NetEntityId)
                return EntityIdentityValidationResult.Rejected(EntityIdentityValidationCode.RemapConflict, "A remap must change the network identity.");
        }
        return EntityIdentityValidationResult.Accepted();
    }

    private static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        for (var i = 0; i < value.Length; i++)
        {
            char item = value[i];
            if (i == 0 && !IsAsciiAlphaNumeric(item)) return false;
            if (i > 0 && !(IsAsciiAlphaNumeric(item) || item is '.' or '_' or ':' or '-')) return false;
        }
        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');

    private static bool IsLocalEntityId(string value)
    {
        int separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 || value.IndexOf(':', separator + 1) >= 0) return false;
        return IsUnsignedDecimal(value.AsSpan(0, separator)) && IsUnsignedDecimal(value.AsSpan(separator + 1));
    }

    private static bool IsUnsignedDecimal(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return false;
        foreach (char item in value)
            if (item < '0' || item > '9') return false;
        return ulong.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
    }
}
