using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

public sealed partial class EcsWorld
{
    internal IWorldStorageAdapter PersistStorage => _storage;

    internal IReadOnlyList<ComponentTypeDefinition> PersistRegisteredComponentTypes
    {
        get
        {
            lock (_lifecycleSync)
            {
                if (_componentsByName.Count == 0)
                    return Array.Empty<ComponentTypeDefinition>();
                var types = new ComponentTypeDefinition[_componentsByName.Count];
                _componentsByName.Values.CopyTo(types, 0);
                return types;
            }
        }
    }
}
