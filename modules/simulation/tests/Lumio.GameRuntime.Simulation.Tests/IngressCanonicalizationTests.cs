using System;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
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
        Assert.Equal(IngressEnqueueStatus.Backpressured, IngressEnqueueStatus.QueueFull);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void ExactCapacityAndBytesAreAcceptedAndOverflowIsBackpressured()
    {
        IngressBudget budget = IngressBudget.FromNamedParameters(IngressQueueCapacity: 2, IngressQueueBytes: 4);
        var queue = new IngressQueue(budget);
        Assert.Equal(IngressEnqueueStatus.Accepted, queue.TryEnqueue(new OpaqueIngress("s", 1, 1, 1, new byte[] { 1, 2 })));
        Assert.Equal(IngressEnqueueStatus.Accepted, queue.TryEnqueue(new OpaqueIngress("s", 2, 1, 1, new byte[] { 3, 4 })));
        Assert.Equal(2, queue.Count);
        Assert.Equal(4, queue.Bytes);
        Assert.Equal(IngressEnqueueStatus.Backpressured, queue.TryEnqueue(new OpaqueIngress("s", 3, 1, 1, new byte[] { 5 })));
        Assert.Equal(2, queue.Count);
        Assert.Equal(4, queue.Bytes);
    }

    [Fact]
    public void ItemLargerThanByteBudgetIsRejectedWithoutPartialAcceptance()
    {
        var queue = new IngressQueue(IngressQueueCapacity: 4, IngressQueueBytes: 2);
        Assert.Equal(IngressEnqueueStatus.Rejected, queue.TryEnqueue(new OpaqueIngress("s", 1, 1, 1, new byte[] { 1, 2, 3 })));
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.Bytes);
    }

    [Fact]
    public void BatchOverflowIsMarkedPartialAndKeepsAcceptedItems()
    {
        var queue = new IngressQueue(new IngressBudget(2, 32));
        IngressEnqueueBatchResult result = queue.TryEnqueueBatch(
            new[]
            {
                new OpaqueIngress("s", 1, 1, 1, new byte[] { 1 }),
                new OpaqueIngress("s", 2, 1, 1, new byte[] { 2 }),
                new OpaqueIngress("s", 3, 1, 1, new byte[] { 3 })
            },
            currentTickId: 1,
            currentGeneration: 1);

        Assert.True(result.IsPartial);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(1, result.BackpressuredCount);
        Assert.Equal(2, queue.Count);
        Assert.Equal(2, queue.CaptureForTick(1).Batch!.Items.Count);
    }

    [Fact]
    public void SameEnvelopeSetIsCanonicalAcrossArrivalOrders()
    {
        OpaqueIngress next = Envelope("s-b", 2, "cmd-b", IngressArrivalClass.NextTick, new byte[] { 2 });
        OpaqueIngress currentA = Envelope("s-a", 1, "cmd-a", IngressArrivalClass.CurrentTick, new byte[] { 1 });
        OpaqueIngress currentC = Envelope("s-c", 3, "cmd-c", IngressArrivalClass.CurrentTick, new byte[] { 3 });

        CanonicalInputBatch left = InputCanonicalizer.Canonicalize(4, new[] { next, currentC, currentA });
        CanonicalInputBatch right = InputCanonicalizer.Canonicalize(4, new[] { currentC, next, currentA });

        Assert.Equal(left.CanonicalHashHex, right.CanonicalHashHex);
        Assert.Equal(new[] { "cmd-a", "cmd-c", "cmd-b" }, left.Items.Select(item => item.CommandId));
        Assert.Equal(left.Items.Select(item => item.CommandId), right.Items.Select(item => item.CommandId));
    }

    [Fact]
    public void DuplicateInputReturnsOriginalIdempotentClassification()
    {
        var queue = new IngressQueue(new IngressBudget(4, 32));
        var original = new OpaqueIngress("s", 1, 1, 1, new byte[] { 1 }, "cmd-1", IngressArrivalClass.CurrentTick);
        var duplicate = new OpaqueIngress("s", 1, 1, 1, new byte[] { 9 }, "cmd-1", IngressArrivalClass.CurrentTick);
        Assert.Equal(IngressEnqueueStatus.Accepted, queue.TryEnqueue(original));
        Assert.Equal(IngressEnqueueStatus.Duplicate, queue.TryEnqueue(duplicate));
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, queue.CaptureForTick(1).Batch!.Items[0].Payload[0]);
    }

    [Fact]
    public void CanonicalOrderIgnoresPayloadTimestampsAndObjectIdentity()
    {
        var laterStamp = new OpaqueIngress("s2", 2, 1, 1, BitConverter.GetBytes(1L), "cmd-2", IngressArrivalClass.CurrentTick);
        var earlierStamp = new OpaqueIngress("s1", 1, 1, 1, BitConverter.GetBytes(99L), "cmd-1", IngressArrivalClass.CurrentTick);
        CanonicalInputBatch batch = InputCanonicalizer.Canonicalize(1, new[] { laterStamp, earlierStamp });
        Assert.Equal(new[] { "cmd-1", "cmd-2" }, batch.Items.Select(item => item.CommandId));
    }

    [Fact]
    public void LateInputMapsToFrozenApplyNextRejectResyncActions()
    {
        var queue = new IngressQueue(new IngressBudget(8, 64));
        Assert.Equal(LateInputAction.ApplyNext, queue.ClassifyLate(new OpaqueIngress("s", 1, 10, 1, new byte[] { 1 }), 10, 1));
        Assert.Equal(LateInputAction.ApplyNext, queue.ClassifyLate(new OpaqueIngress("s", 2, 11, 1, new byte[] { 2 }), 10, 1));
        Assert.Equal(LateInputAction.Reject, queue.ClassifyLate(new OpaqueIngress("s", 3, 9, 1, new byte[] { 3 }), 10, 1));
        Assert.Equal(LateInputAction.Resync, queue.ClassifyLate(new OpaqueIngress("s", 4, 13, 1, new byte[] { 4 }), 10, 1));
        Assert.Equal(LateInputAction.Resync, queue.ClassifyLate(new OpaqueIngress("s", 5, 10, 2, new byte[] { 5 }), 10, 1));
        Assert.Equal(3, Enum.GetValues<LateInputAction>().Length);
    }

    [Fact]
    public void BudgetProjectsExactCapabilityParameterNames()
    {
        Assert.Equal("IngressQueueCapacity", IngressBudget.CapacityParameterName);
        Assert.Equal("IngressQueueBytes", IngressBudget.BytesParameterName);
        IngressBudget budget = IngressBudget.FromNamedParameters(IngressQueueCapacity: 8, IngressQueueBytes: 256);
        Assert.Equal(8, budget.Capacity);
        Assert.Equal(256, budget.MaxBytes);
    }

    [Fact]
    public void IngressElementsDoNotExposeSocketOrConnectionTypes()
    {
        Assert.All(typeof(OpaqueIngress).GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
        {
            Assert.DoesNotContain("Socket", property.PropertyType.FullName, StringComparison.Ordinal);
            Assert.DoesNotContain("Connection", property.PropertyType.FullName, StringComparison.Ordinal);
        });
        Assert.Null(typeof(OpaqueIngress).GetProperty("Connection"));
        Assert.Null(typeof(IngressQueue).GetProperty("Socket"));
        Assert.NotEqual(typeof(Socket), typeof(OpaqueIngress));
    }

    private static OpaqueIngress Envelope(
        string sessionId,
        ulong sequence,
        string commandId,
        IngressArrivalClass arrivalClass,
        byte[] payload) =>
        new(sessionId, sequence, 4, 1, payload, commandId, arrivalClass);
}
