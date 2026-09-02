namespace Lumio.GameRuntime.Ecs.Annotations;

/// <summary>Identity component. <see cref="AccountId"/> stays unmarked and must not enter the declaration table.</summary>
[EcsComponent]
public sealed class EntityIdentity
{
    /// <summary>Room-visible entity kind. Ephemeral, replicated, room-public.</summary>
    [Replicate]
    [Visibility(VisibilityKind.RoomPublic)]
    [AttributeValueType("enum:entityType")]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Claim-scoped replica mark used by binding-query coverage.</summary>
    [Replicate]
    [Visibility(VisibilityKind.ClaimScoped)]
    public string ClaimedMark { get; set; } = string.Empty;

    /// <summary>Declared but unmapped replica mark used by binding-query coverage.</summary>
    [Replicate]
    [Visibility(VisibilityKind.RoomPublic)]
    public string UnmappedMark { get; set; } = string.Empty;

    /// <summary>Persistent business identity. Unmarked: never on the wire, never persisted, never declared.</summary>
    public string AccountId { get; set; } = string.Empty;
}
