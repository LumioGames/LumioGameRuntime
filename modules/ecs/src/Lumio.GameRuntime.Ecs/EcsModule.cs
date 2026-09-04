using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Composition boundary for the manager-owned ECS runtime.</summary>
public sealed class EcsModule : IDisposable
{
    private readonly List<WorldManager> _managers = new();
    private bool _disposed;

    internal WorldManager Track(WorldManager manager)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EcsModule));
        _managers.Add(manager);
        return manager;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < _managers.Count; i++) _managers[i].Dispose();
        _managers.Clear();
    }
}
