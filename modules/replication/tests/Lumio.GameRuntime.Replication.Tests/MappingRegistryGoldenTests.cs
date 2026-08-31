using System;
using Lumio.GameRuntime.Replication.Mapping;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class MappingRegistryGoldenTests
{
    [Fact]
    public void MappingSetHashIsIndependentOfRegistrationOrder()
    {
        var first = new MappingRegistry();
        var second = new MappingRegistry();
        var a = MappingDescriptor.Create("mapping-actor-health", "Health", "current");
        var b = MappingDescriptor.Create("mapping-actor-transform", "Transform", "position");
        Assert.True(first.Register(b).Succeeded);
        Assert.True(first.Register(a).Succeeded);
        Assert.True(second.Register(a).Succeeded);
        Assert.True(second.Register(b).Succeeded);
        Assert.Equal(first.View.MappingSetHash, second.View.MappingSetHash);
    }

    [Fact]
    public void NetLocalGenerationMismatchDoesNotResolve()
    {
        var table = new NetEntityMappingTable();
        var net = NetEntityId.Parse("00000000000000010000000000000001");
        Assert.True(table.Bind(net, "4:2").Succeeded);
        Assert.False(table.TryResolveLocal(net, 1, out _));
        Assert.True(table.TryResolveLocal(net, 2, out var local));
        Assert.Equal("4:2", local);
    }
}
