using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Replication.Binding;
using Lumio.GameRuntime.Samples.Username;

namespace Lumio.GameRuntime.Replication.Tests;

internal static class TestBindingFactory
{
    internal static EntityBindingQuery Create()
    {
        var manager = WorldManager.Create(GeneratedRegistry.Instance, 0x1000000000000001UL);
        manager.Start(Thread.CurrentThread);
        return EntityBindingQuery.Create(manager);
    }
}
