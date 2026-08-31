namespace Lumio.GameRuntime.Ecs;

/// <summary>Stable candidate facade used by composition roots without exposing storage adapters.</summary>
public sealed class EcsServices
{
    private readonly EcsModule _module;

    public EcsServices(EcsModule module)
    {
        _module = module ?? throw new System.ArgumentNullException(nameof(module));
    }

    public EcsWorldCreateResult CreateWorld(in EcsWorldCreateRequest request) =>
        _module.CreateWorld(in request);
}
