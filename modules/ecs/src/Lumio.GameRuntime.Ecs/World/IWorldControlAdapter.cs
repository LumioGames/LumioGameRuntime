namespace Lumio.GameRuntime.Ecs;

public interface IWorldControlAdapter
{
    bool TryHandle(WorldMessage message, out ErrorMessage? failure);

    /// <summary>Handles a control and optionally emits an internal drain query result.</summary>
    bool TryHandle(WorldMessage message, out ErrorMessage? failure, out WorldMessage? queryResult)
    {
        queryResult = null;
        return TryHandle(message, out failure);
    }

    bool TryResolveConnection(NetEntityId observerId, out string connection);
}
