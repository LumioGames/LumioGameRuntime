using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Replication;

public readonly record struct ReplicationSchedulerOptions(
    int Capacity,
    double StarvationCapSeconds,
    double FrequencyGateSeconds,
    double JitterMaxSeconds,
    IReadOnlyList<double> SlowClientThresholds)
{
    public bool IsValid
    {
        get
        {
            if (Capacity <= 0 ||
                !ReplicationValidation.IsFiniteNonNegative(StarvationCapSeconds) ||
                !ReplicationValidation.IsFiniteNonNegative(FrequencyGateSeconds) ||
                !ReplicationValidation.IsFiniteNonNegative(JitterMaxSeconds) ||
                SlowClientThresholds is null)
                return false;

            var previous = 0d;
            for (var index = 0; index < SlowClientThresholds.Count; index++)
            {
                double threshold = SlowClientThresholds[index];
                if (!ReplicationValidation.IsFiniteNonNegative(threshold) || (index > 0 && threshold < previous))
                    return false;
                previous = threshold;
            }

            return true;
        }
    }
}

internal readonly record struct ReplicationIdentity(string Key, ulong Revision);

internal readonly record struct QueuedReplicationWorkItem(ReplicationWorkItem Item, long QueueOrder);

public readonly record struct ReplicationWorkItem(
    string Key,
    ulong Revision,
    int Priority,
    double EnqueuedAtSeconds,
    double AvailableAtSeconds,
    long EstimatedBytes)
{
    public double EligibleAtSeconds => AvailableAtSeconds;

    internal ReplicationIdentity Identity => new(Key, Revision);
}

public readonly record struct ReplicationPermit(long Bytes);

public interface IReplicationPermitProvider
{
    bool TryAcquire(long bytes, out ReplicationPermit permit);
}

public enum EnqueueStatus
{
    Accepted,
    Duplicate,
    QueueFull,
    Invalid
}

public enum SlowClientLevel
{
    Normal,
    Congested,
    Slow
}

public sealed class ReplicationPlan
{
    internal ReplicationPlan(List<ReplicationWorkItem> items, int truncatedCount, SlowClientLevel level, List<string> trace)
    {
        Items = items.AsReadOnly();
        TruncatedCount = truncatedCount;
        SlowClientLevel = level;
        Trace = trace.AsReadOnly();
    }

    public IReadOnlyList<ReplicationWorkItem> Items { get; }
    public int TruncatedCount { get; }
    public SlowClientLevel SlowClientLevel { get; }
    public IReadOnlyList<string> Trace { get; }
}

/// <summary>Owns bounded replication pacing and fair service ordering.</summary>
public sealed class ReplicationScheduler
{
    private readonly object _gate = new();
    private readonly ReplicationSchedulerOptions _options;
    private readonly double[] _slowClientThresholds;
    private readonly Dictionary<ReplicationIdentity, QueuedReplicationWorkItem> _items = new();
    private long _lastQueueOrder;

    public ReplicationScheduler(ReplicationSchedulerOptions options)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _slowClientThresholds = new double[options.SlowClientThresholds.Count];
        for (var index = 0; index < _slowClientThresholds.Length; index++)
            _slowClientThresholds[index] = options.SlowClientThresholds[index];
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public EnqueueStatus Enqueue(ReplicationWorkItem item)
    {
        if (!ReplicationValidation.IsValidWorkItem(item)) return EnqueueStatus.Invalid;
        lock (_gate)
        {
            ReplicationIdentity identity = item.Identity;
            if (_items.ContainsKey(identity)) return EnqueueStatus.Duplicate;
            if (_items.Count >= _options.Capacity) return EnqueueStatus.QueueFull;

            double jittered = Math.Max(item.AvailableAtSeconds,
                item.EnqueuedAtSeconds + _options.FrequencyGateSeconds + DeterministicJitter(item));
            if (!ReplicationValidation.IsFinite(jittered)) return EnqueueStatus.Invalid;
            long queueOrder;
            try { queueOrder = checked(_lastQueueOrder + 1); }
            catch (OverflowException) { return EnqueueStatus.Invalid; }
            _items.Add(identity, new QueuedReplicationWorkItem(item with { AvailableAtSeconds = jittered }, queueOrder));
            _lastQueueOrder = queueOrder;
            return EnqueueStatus.Accepted;
        }
    }

    public ReplicationPlan Plan(double nowSeconds, int maxItems, IReplicationPermitProvider permitProvider)
    {
        if (!ReplicationValidation.IsFinite(nowSeconds)) throw new ArgumentOutOfRangeException(nameof(nowSeconds));
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfNegative(maxItems);
        ArgumentNullException.ThrowIfNull(permitProvider);
#else
        if (maxItems < 0) throw new ArgumentOutOfRangeException(nameof(maxItems));
        if (permitProvider is null) throw new ArgumentNullException(nameof(permitProvider));
#endif

        lock (_gate)
        {
            var eligible = new List<QueuedReplicationWorkItem>();
            foreach (QueuedReplicationWorkItem queuedItem in _items.Values)
                if (queuedItem.Item.AvailableAtSeconds <= nowSeconds) eligible.Add(queuedItem);
            eligible.Sort((left, right) => Compare(left, right, nowSeconds));

            var selected = new List<ReplicationWorkItem>();
            var trace = new List<string>();
            for (var index = 0; index < eligible.Count && selected.Count < maxItems; index++)
            {
                ReplicationWorkItem candidate = eligible[index].Item;
                // The permit is acquired before removal, so denied work remains queued.
                if (!permitProvider.TryAcquire(candidate.EstimatedBytes, out _))
                {
                    trace.Add("permit-denied");
                    continue;
                }

                _items.Remove(candidate.Identity);
                selected.Add(candidate);
            }

            int truncated = eligible.Count - selected.Count;
            SlowClientLevel level = SlowClientLevel.Normal;
            if (truncated > 0)
            {
                if (_slowClientThresholds.Length == 0 || truncated >= _slowClientThresholds[0])
                    level = SlowClientLevel.Congested;
                if (_slowClientThresholds.Length > 1 && truncated >= _slowClientThresholds[1])
                    level = SlowClientLevel.Slow;
                trace.Add("truncated-refill");
            }

            return new ReplicationPlan(selected, truncated, level, trace);
        }
    }

    public EnqueueStatus Requeue(ReplicationWorkItem item, double nowSeconds)
    {
        if (!ReplicationValidation.IsValidWorkItem(item) || !ReplicationValidation.IsFinite(nowSeconds))
            return EnqueueStatus.Invalid;
        double availableAtSeconds = nowSeconds + _options.FrequencyGateSeconds + DeterministicJitter(item);
        if (!ReplicationValidation.IsFinite(availableAtSeconds)) return EnqueueStatus.Invalid;
        ReplicationWorkItem retry = item with
        {
            EnqueuedAtSeconds = nowSeconds,
            AvailableAtSeconds = availableAtSeconds
        };
        return Enqueue(retry);
    }

    private int Compare(QueuedReplicationWorkItem left, QueuedReplicationWorkItem right, double now)
    {
        double leftWaitSeconds = now - left.Item.EnqueuedAtSeconds;
        double rightWaitSeconds = now - right.Item.EnqueuedAtSeconds;
        bool leftStarved = leftWaitSeconds >= _options.StarvationCapSeconds;
        bool rightStarved = rightWaitSeconds >= _options.StarvationCapSeconds;
        int value = rightStarved.CompareTo(leftStarved);
        if (value != 0) return value;
        if (leftStarved)
        {
            value = rightWaitSeconds.CompareTo(leftWaitSeconds);
            if (value != 0) return value;
            value = left.QueueOrder.CompareTo(right.QueueOrder);
            if (value != 0) return value;
        }

        value = right.Item.Priority.CompareTo(left.Item.Priority);
        if (value != 0) return value;
        if (!leftStarved)
        {
            value = rightWaitSeconds.CompareTo(leftWaitSeconds);
            if (value != 0) return value;
        }

        value = left.Item.Revision.CompareTo(right.Item.Revision);
        return value != 0 ? value : string.CompareOrdinal(left.Item.Key, right.Item.Key);
    }

    private double DeterministicJitter(ReplicationWorkItem item)
    {
        if (_options.JitterMaxSeconds == 0) return 0;
        unchecked
        {
            uint hash = 2166136261;
            foreach (char character in item.Key)
            {
                hash ^= character;
                hash *= 16777619;
            }
            hash ^= (uint)item.Revision;
            hash *= 16777619;
            return (hash / (double)uint.MaxValue) * _options.JitterMaxSeconds;
        }
    }
}
