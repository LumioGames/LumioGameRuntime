using System;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class PublicSurfaceTests
{
    [Fact]
    public void ReferenceStorageAdapterIsNotPublicStableSurface()
    {
        Type adapterType = typeof(ReferenceWorldStorageAdapter);

        Assert.False(adapterType.IsPublic);
        Assert.False(adapterType.IsNestedPublic);
    }
}
