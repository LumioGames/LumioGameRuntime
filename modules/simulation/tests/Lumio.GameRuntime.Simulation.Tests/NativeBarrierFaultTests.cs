using System;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Simulation.Native;
using Lumio.GameRuntime.Simulation.Phases;
using Xunit;

namespace Lumio.GameRuntime.Simulation.Tests;

public sealed class NativeBarrierFaultTests
{
    [Fact]
    public void Native_completion_is_not_visible_before_barrier()
    {
        var fixture = Fixtures.PendingNativeCompletion();
        fixture.WorkerCallback();
        Assert.Equal(fixture.BeforeRevision, fixture.Revisions.Read());
        fixture.RunThrough(TickPhase.NativeJobBarrier);
        Assert.Equal(fixture.AfterRevision, fixture.Revisions.Read());
    }

    [Fact]
    public void PreBarrierPhasesDoNotPublishNativeCompletionsToWorldRevisions()
    {
        var fixture = Fixtures.PendingNativeCompletion();
        fixture.WorkerCallback();
        fixture.RunThrough(TickPhase.CrossWorldPrepare);
        Assert.Equal(fixture.BeforeRevision, fixture.Revisions.Read());
        Assert.Equal(1, fixture.QueuedCount);
    }

    [Fact]
    public void WorkerCallbackCopiesPayloadAndDoesNotWriteWorld()
    {
        var queue = new NativeCompletionQueue(NativeCompletionQueueCapacity: 4);
        var payload = new byte[] { 3, 4 };
        WorldRevisions revisions = new(1, 1, 1);
        WorkerCallback(queue, new NativeCompletion("job-a", "token-a", 1, payload), 1, ref revisions);
        payload[0] = 99;
        Assert.Equal(new WorldRevisions(1, 1, 1), revisions);
        Assert.Equal(3, queue.DrainAtBarrier(1).Items[0].Payload.ToArray()[0]);
    }

    [Fact]
    public void BarrierMergesByGeneratedJobThenTokenOrder()
    {
        var queue = new NativeCompletionQueue(NativeCompletionQueueCapacity: 8);
        Assert.Equal(NativeCompletionStatus.Accepted, queue.TryPublish(new NativeCompletion("job-b", "token-z", 1, new byte[] { 1 }), 1));
        Assert.Equal(NativeCompletionStatus.Accepted, queue.TryPublish(new NativeCompletion("job-a", "token-b", 1, new byte[] { 2 }), 1));
        Assert.Equal(NativeCompletionStatus.Accepted, queue.TryPublish(new NativeCompletion("job-a", "token-a", 1, new byte[] { 3 }), 1));

        NativeCompletionBatch batch = queue.DrainAtBarrier(1);

        Assert.Equal(
            new[] { "job-a:token-a", "job-a:token-b", "job-b:token-z" },
            batch.Items.Select(item => item.JobId + ":" + item.Token));
    }

    [Fact]
    public void ReliableQueueFullFaultsStopsAdmissionAndDoesNotDropAcceptedCompletions()
    {
        var queue = new NativeCompletionQueue(NativeCompletionQueueCapacity: 1);
        Assert.Equal(NativeCompletionStatus.Accepted, queue.TryPublish(new NativeCompletion("job-a", "token-a", 1, new byte[] { 1 }), 1));

        NativeCompletionStatus overflow = queue.TryPublish(new NativeCompletion("job-b", "token-b", 1, new byte[] { 2 }), 1);

        Assert.Equal(NativeCompletionStatus.Faulted, overflow);
        Assert.True(queue.IsFaulted);
        Assert.True(queue.StopDispatchSignal);
        Assert.True(queue.AdmissionStopped);
        Assert.Equal("QueueFull", queue.GeneratedErrorId);
        Assert.Equal(1, queue.Count);
        Assert.Equal(NativeCompletionStatus.Faulted, queue.TryPublish(new NativeCompletion("job-c", "token-c", 1, new byte[] { 3 }), 1));
        Assert.Equal(1, queue.Count);

        NativeCompletionBatch batch = queue.DrainAtBarrier(1);
        Assert.True(queue.IsFaulted);
        Assert.True(queue.AdmissionStopped);
        Assert.Single(batch.Items);
        Assert.Equal("job-a", batch.Items[0].JobId);
        Assert.Equal(NativeCompletionStatus.Faulted, queue.TryPublish(new NativeCompletion("job-d", "token-d", 1, new byte[] { 4 }), 1));
    }

    [Fact]
    public void NativeBudgetProjectsExactCapabilityParameterName()
    {
        Assert.Equal("NativeCompletionQueueCapacity", NativeCompletionBudget.CapacityParameterName);
        NativeCompletionBudget budget = NativeCompletionBudget.FromNamedParameter(NativeCompletionQueueCapacity: 3);
        Assert.Equal(3, budget.Capacity);
        Assert.True(budget.IsValid);
    }

    [Fact]
    public void NativeJobBarrierIsTheGeneratedPhaseGraphBarrier()
    {
        Assert.Equal(TickPhase.NativeJobBarrier, PhaseGraph.Default.Phases[5]);
        Assert.Contains("NativeCompletions", PhaseContractTable.Default[TickPhase.NativeJobBarrier].WritableDomains, StringComparer.Ordinal);
        Assert.Equal(PhaseFailureClass.ProcessFault, PhaseContractTable.Default[TickPhase.NativeJobBarrier].FailureClass);
    }

    [Fact]
    public void ChannelTypeIsNotPartOfThePublicNativeQueueSurface()
    {
        Assert.All(
            typeof(NativeCompletionQueue).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            property => Assert.DoesNotContain("Channel", property.PropertyType.FullName, StringComparison.Ordinal));
        Assert.All(
            typeof(NativeCompletionQueue).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            method =>
            {
                if (method.DeclaringType == typeof(object)) return;
                Assert.DoesNotContain("Channel", method.ReturnType.FullName, StringComparison.Ordinal);
                Assert.All(method.GetParameters(), parameter => Assert.DoesNotContain("Channel", parameter.ParameterType.FullName, StringComparison.Ordinal));
            });
    }

    private static void WorkerCallback(
        NativeCompletionQueue queue,
        in NativeCompletion completion,
        ulong generation,
        ref WorldRevisions revisions)
    {
        _ = revisions;
        NativeCompletionStatus status = queue.TryPublish(in completion, generation);
        Assert.Equal(NativeCompletionStatus.Accepted, status);
    }
}

internal readonly record struct WorldRevisions(ulong Ecs, ulong Voxel, ulong Gas);

internal sealed class RevisionClock
{
    private WorldRevisions _value;

    internal RevisionClock(WorldRevisions initial)
    {
        _value = initial;
    }

    internal WorldRevisions Read() => _value;

    internal void ApplyNativeCompletions(int count)
    {
        _value = new WorldRevisions(
            _value.Ecs + (ulong)count,
            _value.Voxel + (ulong)count,
            _value.Gas + (ulong)count);
    }
}

internal static class Fixtures
{
    internal static PendingNativeCompletionFixture PendingNativeCompletion() => new();
}

internal sealed class PendingNativeCompletionFixture
{
    private readonly NativeCompletionQueue _queue = new(NativeCompletionQueueCapacity: 4);
    private readonly NativeCompletion _completion = new("job-a", "token-a", 1, new byte[] { 11 });
    private readonly RevisionClock _revisions = new(new WorldRevisions(1, 1, 1));
    private readonly ulong _generation = 1;

    internal WorldRevisions BeforeRevision { get; } = new(1, 1, 1);

    internal WorldRevisions AfterRevision { get; } = new(2, 2, 2);

    internal RevisionClock Revisions => _revisions;

    internal int QueuedCount => _queue.Count;

    internal void WorkerCallback()
    {
        NativeCompletionStatus status = _queue.TryPublish(in _completion, _generation);
        Assert.Equal(NativeCompletionStatus.Accepted, status);
    }

    internal void RunThrough(TickPhase stopInclusive)
    {
        foreach (TickPhase phase in PhaseGraph.Default.Phases)
        {
            if (phase == TickPhase.NativeJobBarrier)
            {
                NativeCompletionBatch batch = _queue.DrainAtBarrier(_generation);
                _revisions.ApplyNativeCompletions(batch.Items.Count);
            }

            if (phase == stopInclusive) break;
        }
    }
}
