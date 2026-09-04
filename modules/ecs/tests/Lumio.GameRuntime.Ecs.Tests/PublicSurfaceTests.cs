using System;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class PublicSurfaceTests
{
    [Fact]
    public void LegacyStorageAdapterIsAbsent()
    {
        Assert.DoesNotContain(typeof(EcsModule).Assembly.GetTypes(), static type => type.Name == "ReferenceWorldStorageAdapter");
    }
}
