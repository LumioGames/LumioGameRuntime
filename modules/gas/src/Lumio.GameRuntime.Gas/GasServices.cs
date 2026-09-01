using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Gas;

/// <summary>Stable candidate facade: type registry and World-local context factory.</summary>
public sealed class GasServices
{
    private readonly GasModule _module;

    internal GasServices(GasModule module)
    {
        _module = module;
    }

    public GasTypeRegistry Types => _module.Types;

    public GasWorldContext CreateWorldContext(WorldId worldId, IGasEcsProjectionPort projection) =>
        _module.CreateWorldContext(worldId, projection);
}
