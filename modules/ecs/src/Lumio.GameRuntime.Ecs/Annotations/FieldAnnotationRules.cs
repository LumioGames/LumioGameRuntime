using System;

namespace Lumio.GameRuntime.Ecs.Annotations;

/// <summary>C-2 three-dimension tokens and illegal combination checks.</summary>
public static class FieldAnnotationRules
{
    /// <summary>Unmarked persistence: never entered into ECS snapshot/restore.</summary>
    public const string DefaultPersistence = PersistenceEphemeral;

    /// <summary>Unmarked replication: never placed on the wire.</summary>
    public const string DefaultReplication = ReplicationNotReplicated;

    /// <summary>Unmarked visibility: server-authoritative only.</summary>
    public const string DefaultVisibility = VisibilityServerOnly;

    /// <summary>C-2 persistence token for non-persisted fields.</summary>
    public const string PersistenceEphemeral = "ephemeral";

    /// <summary>C-2 persistence token for snapshot/restore fields.</summary>
    public const string PersistencePersistent = "persistent";

    /// <summary>C-2 replication token for fields that never enter the replica stream.</summary>
    public const string ReplicationNotReplicated = "not-replicated";

    /// <summary>C-2 replication token for fields that may enter a client world.</summary>
    public const string ReplicationReplicated = "replicated";

    /// <summary>C-2 visibility token for server-only fields.</summary>
    public const string VisibilityServerOnly = "server-only";

    /// <summary>C-2 visibility token for room-public fields.</summary>
    public const string VisibilityRoomPublic = "room-public";

    /// <summary>C-2 visibility token for AOI-scoped fields.</summary>
    public const string VisibilityAoiScoped = "aoi-scoped";

    /// <summary>C-2 visibility token for claim-scoped fields.</summary>
    public const string VisibilityClaimScoped = "claim-scoped";

    /// <summary>Maps <see cref="Scope"/> onto a C-2 visibility token.</summary>
    public static string Token(Scope scope) => scope switch
    {
        Scope.Room => VisibilityRoomPublic,
        Scope.Aoi => VisibilityAoiScoped,
        Scope.Owner => VisibilityServerOnly,
        Scope.Claim => VisibilityClaimScoped,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    /// <summary>
    /// Rejects C-2-illegal dimension triples: replicated+server-only, and
    /// aoi-scoped/claim-scoped/room-public + not-replicated.
    /// </summary>
    public static void Validate(string attributeId, string persistence, string replication, string visibility)
    {
        AnnotationGuard.NotNull(attributeId, nameof(attributeId));
        AnnotationGuard.NotNull(persistence, nameof(persistence));
        AnnotationGuard.NotNull(replication, nameof(replication));
        AnnotationGuard.NotNull(visibility, nameof(visibility));

        if (!IsPersistence(persistence))
            throw new InvalidOperationException("unknown persistence token: " + persistence + " (" + attributeId + ")");
        if (!IsReplication(replication))
            throw new InvalidOperationException("unknown replication token: " + replication + " (" + attributeId + ")");
        if (!IsVisibility(visibility))
            throw new InvalidOperationException("unknown visibility token: " + visibility + " (" + attributeId + ")");

        if (string.Equals(replication, ReplicationReplicated, StringComparison.Ordinal) &&
            string.Equals(visibility, VisibilityServerOnly, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "illegal attribute combination: " + attributeId + ": replicated + server-only");
        }

        if (string.Equals(replication, ReplicationNotReplicated, StringComparison.Ordinal) &&
            (string.Equals(visibility, VisibilityAoiScoped, StringComparison.Ordinal) ||
             string.Equals(visibility, VisibilityClaimScoped, StringComparison.Ordinal) ||
             string.Equals(visibility, VisibilityRoomPublic, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "illegal attribute combination: " + attributeId + ": " + visibility + " + not-replicated");
        }
    }

    private static bool IsPersistence(string value) =>
        string.Equals(value, PersistenceEphemeral, StringComparison.Ordinal) ||
        string.Equals(value, PersistencePersistent, StringComparison.Ordinal);

    private static bool IsReplication(string value) =>
        string.Equals(value, ReplicationNotReplicated, StringComparison.Ordinal) ||
        string.Equals(value, ReplicationReplicated, StringComparison.Ordinal);

    private static bool IsVisibility(string value) =>
        string.Equals(value, VisibilityServerOnly, StringComparison.Ordinal) ||
        string.Equals(value, VisibilityRoomPublic, StringComparison.Ordinal) ||
        string.Equals(value, VisibilityAoiScoped, StringComparison.Ordinal) ||
        string.Equals(value, VisibilityClaimScoped, StringComparison.Ordinal);
}
