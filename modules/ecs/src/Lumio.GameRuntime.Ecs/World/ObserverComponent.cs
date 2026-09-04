namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Engine-owned connection observer state. It is attached to bindable entity
/// templates and is intentionally not persisted or replicated as a gameplay field.
/// </summary>
public sealed class ObserverComponent : Component
{
    /// <summary>Whether the entity currently has an admitted observer.</summary>
    public bool Connected;

    /// <summary>Monotonic connection generation for reconnect/rebind.</summary>
    public ulong ConnectionGeneration;

    /// <summary>Tick at which the observer became disconnected.</summary>
    public ulong DisconnectedAtTick;

    /// <summary>Last authoritative tick projected to this observer; zero requests a full census.</summary>
    public ulong ProjectedTick;
}
