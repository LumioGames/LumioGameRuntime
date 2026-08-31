using System;
using Lumio.GameRuntime.Simulation.Determinism;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class DeterminismReplayTests
{
    [Fact]
    public void SameSeedAndInputsProduceTheSameHashRegardlessOfProviderRegistrationOrder()
    {
        var a = new StateHashCoordinator();
        var b = new StateHashCoordinator();
        a.Register("z", "last");
        a.Register("a", "first");
        b.Register("a", "first");
        b.Register("z", "last");
        Assert.Equal(a.ComputeHashHex(), b.ComputeHashHex());

        var one = new DeterminismContext(7, 11, 1);
        var two = new DeterminismContext(7, 11, 1);
        Assert.Equal(one.OpenRngStream("processor").NextUInt64(), two.OpenRngStream("processor").NextUInt64());
    }
}
