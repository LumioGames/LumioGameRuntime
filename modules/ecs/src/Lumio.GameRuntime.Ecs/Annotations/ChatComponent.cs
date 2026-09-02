namespace Lumio.GameRuntime.Ecs.Annotations;

/// <summary>First annotated ECS component: persist-only last-message fields, never replicated.</summary>
[EcsComponent]
public sealed class ChatComponent
{
    /// <summary>Authoritative last chat text; persistent, not replicated, server-only.</summary>
    [Persist]
    public string LastMessageText { get; set; } = string.Empty;

    /// <summary>Authoritative last chat tick; persistent, not replicated, server-only.</summary>
    [Persist]
    public ulong LastMessageTick { get; set; }

    /// <summary>Persist-only probe field consumed by the existing binding-query contract cases.</summary>
    [Persist]
    public string LastMessagePersistOnly { get; set; } = string.Empty;
}
