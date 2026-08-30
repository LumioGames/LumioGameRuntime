namespace Lumio.GameRuntime.Ecs;

public enum EcsWorldState
{
    Created,
    Registering,
    Ready,
    Running,
    Draining,
    Disposed,
    Faulted
}
