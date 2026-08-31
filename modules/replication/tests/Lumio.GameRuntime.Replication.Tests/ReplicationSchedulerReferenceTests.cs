using System;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Replication;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ReplicationSchedulerReferenceTests
{
    private static readonly double[] BoundaryThresholds = { 100d, 200d };
    private static readonly double[] NoThresholds = Array.Empty<double>();
    private static readonly double[] NonFiniteValues = { double.NaN, double.PositiveInfinity, double.NegativeInfinity };
    private static readonly double[] SingleThreshold = { 100d };

    [Fact]
    public void OrderingPromotesStarvedItemsDeterministically()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(8, 10, 0, 0, new[] { 2d, 5d }));
        scheduler.Enqueue(new("low", 2, 1, 9, 0, 10));
        scheduler.Enqueue(new("high", 1, 10, 9, 0, 10));
        scheduler.Enqueue(new("starved", 3, 0, 0, 0, 10));

        Assert.Equal(new[] { "starved", "high", "low" }, scheduler.Plan(10, 8, new PermitProvider(30)).Items.Select(value => value.Key));
    }

    [Fact]
    public void OldestStarvedItemBeatsNewerHighPriority()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(8, 5, 0, 0, NoThresholds));
        scheduler.Enqueue(new("old-low", 1, 1, 0, 0, 1));
        scheduler.Enqueue(new("newer-high", 2, 10, 1, 0, 1));
        scheduler.Enqueue(new("newest-high", 3, 10, 2, 0, 1));

        Assert.Equal("old-low", scheduler.Plan(10, 1, new PermitProvider(1)).Items.Single().Key);
    }

    [Fact]
    public void ConstantClockRequeueRotatesStarvedItems()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(2, 0, 0, 0, NoThresholds));
        scheduler.Enqueue(new("high", 1, 10, 0, 0, 1));
        scheduler.Enqueue(new("low", 2, 1, 0, 0, 1));
        var selected = new string[2];
        for (var index = 0; index < selected.Length; index++)
        {
            selected[index] = scheduler.Plan(0, 1, new PermitProvider(1)).Items.Single().Key;
            Assert.Equal(EnqueueStatus.Accepted, scheduler.Requeue(
                new ReplicationWorkItem(selected[index], (ulong)(index == 0 ? 1 : 2), index == 0 ? 10 : 1, 0, 0, 1), 0));
        }

        Assert.Equal(new[] { "high", "low" }, selected);
    }

    [Fact]
    public void NewHighPriorityAdmissionsDoNotStarveOlderWork()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(2, 0, 0, 0, NoThresholds));
        scheduler.Enqueue(new("high-0", 1, 10, 0, 0, 1));
        scheduler.Enqueue(new("low", 2, 1, 0, 0, 1));
        var selected = new string[4];
        for (var index = 0; index < selected.Length; index++)
        {
            selected[index] = scheduler.Plan(0, 1, new PermitProvider(1)).Items.Single().Key;
            Assert.Equal(EnqueueStatus.Accepted, scheduler.Enqueue(new($"high-{index + 1}", (ulong)(index + 3), 10, 0, 0, 1)));
        }

        Assert.Equal(new[] { "high-0", "low", "high-1", "high-2" }, selected);
        Assert.Equal(2, scheduler.Count);
    }

    [Fact]
    public void PermitDenialDoesNotRemoveHeadOrBlockLaterItems()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(8, 10, 0, 0, NoThresholds));
        scheduler.Enqueue(new("large", 1, 10, 0, 0, 10));
        scheduler.Enqueue(new("small", 2, 1, 0, 0, 1));

        var plan = scheduler.Plan(0, 8, new PermitProvider(1));

        Assert.Equal("small", plan.Items.Single().Key);
        Assert.Equal(1, plan.TruncatedCount);
        Assert.Contains("permit-denied", plan.Trace);
        Assert.Equal(1, scheduler.Count);
    }

    [Fact]
    public void FrequencyGateAndDeterministicJitterAreStableAndBounded()
    {
        var options = new ReplicationSchedulerOptions(4, 30, 2, 0.5, NoThresholds);
        var firstScheduler = new ReplicationScheduler(options);
        var secondScheduler = new ReplicationScheduler(options);
        firstScheduler.Enqueue(new("same", 7, 1, 0, 0, 10));
        secondScheduler.Enqueue(new("same", 7, 1, 0, 0, 10));

        Assert.Empty(firstScheduler.Plan(2, 8, new PermitProvider(20)).Items);
        ReplicationPlan first = firstScheduler.Plan(2.6, 8, new PermitProvider(20));
        ReplicationPlan second = secondScheduler.Plan(2.6, 8, new PermitProvider(20));
        Assert.Equal(first.Items.Single().EligibleAtSeconds, second.Items.Single().EligibleAtSeconds);
        Assert.InRange(first.Items.Single().EligibleAtSeconds, 2, 2.5);
    }

    [Fact]
    public void TruncationKeepsQueuedWorkAndRequeuePreservesOriginalRevision()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(4, 10, 0, 0, NoThresholds));
        var item = new ReplicationWorkItem("delta", 42, 3, 0, 0, 1);
        Assert.Equal(EnqueueStatus.Accepted, scheduler.Enqueue(item));
        Assert.Equal(EnqueueStatus.Accepted, scheduler.Enqueue(new("other", 43, 1, 0, 0, 1)));
        Assert.Equal(EnqueueStatus.Duplicate, scheduler.Enqueue(item));

        ReplicationPlan plan = scheduler.Plan(1, 1, new PermitProvider(2));
        Assert.Single(plan.Items);
        Assert.Equal(1, plan.TruncatedCount);
        Assert.Equal(EnqueueStatus.Accepted, scheduler.Requeue(plan.Items[0], 1));
        Assert.Equal(42UL, scheduler.Plan(2, 2, new PermitProvider(2)).Items[0].Revision);
    }

    [Fact]
    public void CapacityOverflowIsExplicitAndDoesNotDropExistingWork()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(1, 10, 0, 0, NoThresholds));
        Assert.Equal(EnqueueStatus.Accepted, scheduler.Enqueue(new("one", 1, 1, 0, 0, 1)));
        Assert.Equal(EnqueueStatus.QueueFull, scheduler.Enqueue(new("two", 2, 1, 0, 0, 1)));

        Assert.Equal("one", scheduler.Plan(0, 1, new PermitProvider(1)).Items.Single().Key);
    }

    [Theory]
    [InlineData(1, SlowClientLevel.Normal)]
    [InlineData(100, SlowClientLevel.Congested)]
    [InlineData(200, SlowClientLevel.Slow)]
    public void SlowClientLevelUsesBothThresholdBoundaries(int truncatedCount, SlowClientLevel expected)
    {
        Assert.Equal(expected, PlanWithTruncation(truncatedCount, BoundaryThresholds).SlowClientLevel);
    }

    [Fact]
    public void EmptyThresholdsPreserveCongestedFallbackForTruncation()
    {
        Assert.Equal(SlowClientLevel.Congested, PlanWithTruncation(1, NoThresholds).SlowClientLevel);
    }

    [Theory]
    [InlineData(1, SlowClientLevel.Normal)]
    [InlineData(100, SlowClientLevel.Congested)]
    [InlineData(200, SlowClientLevel.Congested)]
    public void SingleThresholdProvidesNormalAndCongestedLevels(int truncatedCount, SlowClientLevel expected)
    {
        Assert.Equal(expected, PlanWithTruncation(truncatedCount, SingleThreshold).SlowClientLevel);
    }

    [Fact]
    public void RejectsNonFiniteOptionsAndUnorderedThresholds()
    {
        foreach (double value in NonFiniteValues)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationScheduler(
                new ReplicationSchedulerOptions(4, value, 0, 0, NoThresholds)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationScheduler(
                new ReplicationSchedulerOptions(4, 0, value, 0, NoThresholds)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationScheduler(
                new ReplicationSchedulerOptions(4, 0, 0, value, NoThresholds)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationScheduler(
            new ReplicationSchedulerOptions(4, 0, 0, 0, new[] { 2d, 1d })));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationScheduler(
            new ReplicationSchedulerOptions(4, 0, 0, 0, new[] { -1d })));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationScheduler(
            new ReplicationSchedulerOptions(4, 0, 0, 0, new[] { double.PositiveInfinity })));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationScheduler(
            new ReplicationSchedulerOptions(4, 0, 0, 0, null!)));
    }

    [Fact]
    public void RejectsNonFiniteItemAndPlanTimes()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(8, 10, 0, 0, NoThresholds));
        foreach (double value in NonFiniteValues)
        {
            Assert.Equal(EnqueueStatus.Invalid, scheduler.Enqueue(new("enqueue-time", 1, 1, value, 0, 1)));
            Assert.Equal(EnqueueStatus.Invalid, scheduler.Enqueue(new("available-time", 2, 1, 0, value, 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Plan(value, 1, new PermitProvider(1)));
            Assert.Equal(EnqueueStatus.Invalid, scheduler.Requeue(new("requeue-time", 3, 1, value, 0, 1), 0));
            Assert.Equal(EnqueueStatus.Invalid, scheduler.Requeue(new("requeue-available", 4, 1, 0, value, 1), 0));
            Assert.Equal(EnqueueStatus.Invalid, scheduler.Requeue(new("requeue-now", 5, 1, 0, 0, 1), value));
        }
    }

    [Fact]
    public void StructuralIdentityDoesNotUseDelimiterConcatenation()
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(8, 10, 0, 0, NoThresholds));
        Assert.Equal(EnqueueStatus.Accepted, scheduler.Enqueue(new("key\u001fwith-delimiter", 1, 1, 0, 0, 1)));
        Assert.Equal(EnqueueStatus.Accepted, scheduler.Enqueue(new("key", 2, 1, 0, 0, 1)));
        FieldInfo? field = typeof(ReplicationScheduler).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.NotEqual(typeof(string), field!.FieldType.GetGenericArguments()[0]);
    }

    private static ReplicationPlan PlanWithTruncation(int truncatedCount, double[] thresholds)
    {
        var scheduler = new ReplicationScheduler(new ReplicationSchedulerOptions(truncatedCount, 10, 0, 0, thresholds));
        for (var index = 0; index < truncatedCount; index++)
            Assert.Equal(EnqueueStatus.Accepted,
                scheduler.Enqueue(new($"item-{index}", (ulong)index, 1, 0, 0, 1)));
        return scheduler.Plan(0, 0, new PermitProvider(0));
    }

    private sealed class PermitProvider : IReplicationPermitProvider
    {
        private readonly long _remaining;

        public PermitProvider(long remaining) => _remaining = remaining;

        public bool TryAcquire(long bytes, out ReplicationPermit permit)
        {
            if (bytes <= _remaining)
            {
                permit = new ReplicationPermit(bytes);
                return true;
            }

            permit = default;
            return false;
        }
    }
}
