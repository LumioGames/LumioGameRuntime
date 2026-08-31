using System;
using System.Linq;
using Lumio.GameRuntime.Simulation.Ingress;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class IngressCanonicalizationTests
{
    [Fact]
    public void OpaqueIngressIsCopiedAndCanonicallyOrderedBySessionAndSequence()
    {
        var queue = new IngressQueue(new IngressQueueOptions(4, 1024));
        var first = new OpaqueIngress("s2", 2, 4, 1, new byte[] { 3, 2, 1 });
        var second = new OpaqueIngress("s1", 1, 4, 1, new byte[] { 9 });
        Assert.Equal(IngressEnqueueStatus.Accepted, queue.TryEnqueue(in first));
        Assert.Equal(IngressEnqueueStatus.Accepted, queue.TryEnqueue(in second));
        first.Payload[0] = 99;

        var batch = queue.CaptureForTick(4);
        Assert.True(batch.Succeeded);
        Assert.Equal(new[] { "s1", "s2" }, batch.Batch!.Items.Select(x => x.SessionId));
        Assert.Equal(3, batch.Batch.Items[1].Payload[0]);
    }

    [Fact]
    public void QueueFullIsExplicitAndDoesNotDropExistingIngress()
    {
        var queue = new IngressQueue(new IngressQueueOptions(1, 32));
        Assert.Equal(IngressEnqueueStatus.Accepted, queue.TryEnqueue(new OpaqueIngress("s", 1, 1, 1, new byte[] { 1 })));
        Assert.Equal(IngressEnqueueStatus.QueueFull, queue.TryEnqueue(new OpaqueIngress("s", 2, 1, 1, new byte[] { 2 })));
        Assert.Equal(1, queue.Count);
    }
}
