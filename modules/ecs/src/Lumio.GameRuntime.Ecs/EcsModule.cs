using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Lumio.GameRuntime.Ecs.Tests")]
[assembly: InternalsVisibleTo("Lumio.GameRuntime.Command")]
[assembly: InternalsVisibleTo("Lumio.GameRuntime.Command.Tests")]
[assembly: InternalsVisibleTo("Lumio.GameRuntime.Replication.Tests")]
[assembly: InternalsVisibleTo("Lumio.GameRuntime.Samples.Username.Tests")]

namespace Lumio.GameRuntime.Ecs;

/// <summary>Composition boundary for independent ECS worlds.</summary>
public sealed class EcsModule : IDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<EcsWorld> _worlds = new();
    private bool _disposed;

    internal EcsWorldCreateResult CreateWorld(in EcsWorldCreateRequest request)
    {
        lock (_sync)
        {
            if (_disposed) return EcsWorldCreateResult.Rejected(EcsErrorCodes.WorldDisposed);
            if (!request.IsValid) return EcsWorldCreateResult.Rejected(EcsErrorCodes.InvalidArgument);
            foreach (EcsWorld existing in _worlds)
            {
                if (existing.WorldId == request.WorldId)
                    return EcsWorldCreateResult.Rejected(EcsErrorCodes.DuplicateRegistration);
            }
            EcsWorld world;
            try
            {
                world = new EcsWorld(request);
            }
            catch (ArgumentException)
            {
                return EcsWorldCreateResult.Rejected(EcsErrorCodes.InvalidArgument);
            }
            _worlds.Add(world);
            return new EcsWorldCreateResult(true, world, null);
        }
    }

    public void Dispose()
    {
        EcsWorld[] worlds;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            worlds = new EcsWorld[_worlds.Count];
            _worlds.CopyTo(worlds);
            _worlds.Clear();
        }
        foreach (EcsWorld world in worlds) world.ForceCleanup();
    }
}
