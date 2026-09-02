using System;
using System.Linq;
using System.Text;
using System.Threading;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.Ecs.Ingress;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class ChatIngressCaptureTests
{
    [Fact]
    public void PerConnectionCapacityMatchesC1BoundedInput()
    {
        Assert.Equal(64, ChatIngressCapture.PerConnectionCapacity);
        var queue = new ChatIngressCapture();
        for (int i = 0; i < ChatIngressCapture.PerConnectionCapacity; i++)
            Assert.Equal(ChatIngressEnqueueStatus.Accepted, queue.TryEnqueue("C1", "n" + i));

        Assert.Equal(ChatIngressEnqueueStatus.QueueFull, queue.TryEnqueue("C1", "overflow"));
        Assert.Equal(ChatIngressEnqueueStatus.Accepted, queue.TryEnqueue("C2", "other"));

        ChatIngressBatch captured = queue.CaptureForTick();
        Assert.Equal(ChatIngressCapture.PerConnectionCapacity, captured.Items.Count(static item => item.ConnectionId == "C1"));
        Assert.Equal("n0", captured.Items[0].Text);
        Assert.Equal("other", Assert.Single(captured.Items, static item => item.ConnectionId == "C2").Text);
        Assert.DoesNotContain(captured.Items, static item => item.Text == "overflow");
    }

    [Fact]
    public void CaptureDrainsFifoAndLeavesLaterEnqueuesForTheNextTick()
    {
        var queue = new ChatIngressCapture();
        Assert.Equal(ChatIngressEnqueueStatus.Accepted, queue.TryEnqueue("C1", "first"));
        ChatIngressBatch first = queue.CaptureForTick();
        Assert.Equal("C1", Assert.Single(first.Items).ConnectionId);
        Assert.Equal("first", first.Items[0].Text);

        Assert.Equal(ChatIngressEnqueueStatus.Accepted, queue.TryEnqueue("C1", "second"));
        ChatIngressBatch second = queue.CaptureForTick();
        Assert.Equal("second", Assert.Single(second.Items).Text);
        Assert.Empty(queue.CaptureForTick().Items);
    }

    [Fact]
    public void NetworkThreadCanEnqueueWithoutCapturing()
    {
        var queue = new ChatIngressCapture();
        ChatIngressEnqueueStatus? status = null;
        var worker = new Thread(() => status = queue.TryEnqueue("C1", "gg"));
        worker.IsBackground = true;
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(ChatIngressEnqueueStatus.Accepted, status);
        Assert.Equal("gg", Assert.Single(queue.CaptureForTick().Items).Text);
    }

    [Fact]
    public void NullTextIsInvalidAndDoesNotOccupyCapacity()
    {
        var queue = new ChatIngressCapture();
        Assert.Equal(ChatIngressEnqueueStatus.Invalid, queue.TryEnqueue("C1", null!));
        Assert.Equal(ChatIngressEnqueueStatus.Accepted, queue.TryEnqueue("C1", "ok"));
        Assert.Equal("ok", Assert.Single(queue.CaptureForTick().Items).Text);
    }

    [Fact]
    public void Utf8LengthIsNotTheQueueBound()
    {
        var queue = new ChatIngressCapture();
        string cap = new string('a', 512);
        Assert.Equal(512, Encoding.UTF8.GetByteCount(cap));
        Assert.Equal(ChatIngressEnqueueStatus.Accepted, queue.TryEnqueue("C1", cap));
        Assert.Equal(cap, Assert.Single(queue.CaptureForTick().Items).Text);
    }

    [Fact]
    public void OffThreadWriteFailStopsWithZeroComponentWrite()
    {
        using ChatIngressWorld world = ChatIngressWorld.Create();
        Assert.True(world.TryCreateEntity("101", out LocalEntityId entity));
        Assert.Equal(EcsWorldState.Running, world.World.State);
        StorageOperationResult? offThread = null;
        var worker = new Thread(() => offThread = world.TryWriteLastMessage(entity, "hack", 1UL));
        worker.IsBackground = true;
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(StorageOperationStatus.Fatal, offThread?.Status);
        Assert.Equal(EcsErrorCodes.OwnerThreadViolation, offThread?.Error?.Code);
        Assert.Equal(EcsWorldState.Faulted, world.World.State);
        Assert.True(world.TryReadLastMessage(entity, out string text, out ulong tick));
        Assert.Equal(string.Empty, text);
        Assert.Equal(0UL, tick);
    }
}
