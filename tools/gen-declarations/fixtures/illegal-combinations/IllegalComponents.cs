using Lumio.GameRuntime.Ecs.Annotations;

namespace Lumio.Tools.GenDeclarations.IllegalFixtures;

[EcsComponent]
public sealed class IllegalReplicatedServerOnly
{
    [Replicate]
    [Visibility(VisibilityKind.ServerOnly)]
    public string Mark { get; set; } = string.Empty;
}

[EcsComponent]
public sealed class IllegalAoiNotReplicated
{
    [Visibility(VisibilityKind.AoiScoped)]
    public string Mark { get; set; } = string.Empty;
}

[EcsComponent]
public sealed class IllegalClaimNotReplicated
{
    [Visibility(VisibilityKind.ClaimScoped)]
    public string Mark { get; set; } = string.Empty;
}

[EcsComponent]
public sealed class IllegalRoomPublicNotReplicated
{
    [Visibility(VisibilityKind.RoomPublic)]
    public string Mark { get; set; } = string.Empty;
}
