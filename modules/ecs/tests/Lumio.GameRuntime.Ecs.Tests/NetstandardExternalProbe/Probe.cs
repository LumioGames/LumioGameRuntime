using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Ecs.NetstandardProbe;

/// <summary>External netstandard2.1 consumer of the public ECS surface.</summary>
public static class Probe
{
    /// <summary>Touches the public types Client assemblies are expected to reference.</summary>
    public static string Touch()
    {
        _ = typeof(WorldManager);
        _ = typeof(World);
        _ = typeof(Component);
        _ = typeof(Sync<string>);
        _ = typeof(SyncList<int>);
        _ = typeof(SyncDict<string, int>);
        _ = typeof(EcsComponentAttribute);
        _ = typeof(PersistAttribute);
        _ = typeof(EntityTypeAttribute);
        _ = typeof(HasAttribute);
        _ = typeof(ServerRpcAttribute);
        _ = typeof(ClientRpcAttribute);
        _ = typeof(NetEntityId);
        _ = typeof(WorldMessage);
        _ = typeof(WorldChangeMessage);
        _ = typeof(WelcomeMessage);
        _ = typeof(InputCommandMessage);
        return typeof(WorldManager).Assembly.GetName().Name ?? "Lumio.GameRuntime.Ecs";
    }
}
