namespace Lumio.GameRuntime.Ecs;

public interface IWorldControlAdapter
{
    bool TryHandle(WorldMessage message, out ErrorMessage? failure);
    bool TryResolveConnection(NetEntityId observerId, out string connection);
}
