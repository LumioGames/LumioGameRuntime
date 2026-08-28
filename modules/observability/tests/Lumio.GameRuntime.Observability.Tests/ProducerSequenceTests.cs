using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Lumio.GameRuntime.Observability;
using Xunit;

namespace Lumio.GameRuntime.Observability.Tests;

public sealed class ProducerSequenceTests
{
    [Fact]
    public void ProducerSequenceIsMonotonicAndOverflowIsVisible()
    {
        var producer = new ProducerSequence(0UL);

        for (ulong expected = 1UL; expected <= 10_000UL; expected++)
        {
            var sequence = producer.Next();
            Assert.Equal(expected, sequence.Value);
        }

        Assert.Throws<OverflowException>(() => new ProducerSequence(ulong.MaxValue).Next());
    }

    [Fact]
    public async Task EachProducerSequenceIsUniqueUnderConcurrentProduction()
    {
        var sequence = new ProducerSequence(0UL);
        var values = new ConcurrentBag<ulong>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                values.Add(sequence.Next().Value);
            }
        })));

        Assert.Equal(8_000, values.Count);
        Assert.Equal(8_000, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 8_000).Select(value => (ulong)value), values.OrderBy(value => value));
    }

    [Fact]
    public void SeparateProducerInstancesDoNotShareSequenceState()
    {
        var first = new ProducerSequence(0UL);
        var second = new ProducerSequence(0UL);

        Assert.Equal(1UL, first.Next().Value);
        Assert.Equal(2UL, first.Next().Value);
        Assert.Equal(1UL, second.Next().Value);
        Assert.Equal(3UL, first.Next().Value);
        Assert.Equal(2UL, second.Next().Value);
    }

    [Fact]
    public void SeparateProducerIdsGetIndependentSequences()
    {
        var module = ObservabilityModule.Create(
            new RecordingEventPort(),
            new RecordingMetricPort(),
            new RecordingTracePort());
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);

        Assert.Equal(1UL, module.NextEventSequence("producer-a").Value);
        Assert.Equal(2UL, module.NextEventSequence("producer-a").Value);
        Assert.Equal(1UL, module.NextEventSequence("producer-b").Value);
        Assert.Equal(3UL, module.NextEventSequence("producer-a").Value);
        Assert.Equal(2UL, module.NextEventSequence("producer-b").Value);
    }

    [Fact]
    public void MissingCorrelationIsRejectedBeforePortIsCalled()
    {
        var eventPort = new RecordingEventPort();
        var module = ObservabilityModule.Create(eventPort, new RecordingMetricPort(), new RecordingTracePort());
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);

        var value = new RuntimeEventView(
            "evt-1",
            "Diagnostic",
            "Info",
            DateTimeOffset.UnixEpoch,
            default,
            "message",
            "BestEffort");

        var result = module.Services.Events.Emit(in value);

        Assert.Equal(EventEnqueueStatus.Rejected, result.Status);
        Assert.Equal("ManifestMalformed", result.GeneratedErrorId);
        Assert.Equal(0, eventPort.Calls);
    }

    [Fact]
    public void LifecycleAcceptsRunningFlushCloseAndFaultPaths()
    {
        var module = ObservabilityModule.Create(new RecordingEventPort(), new RecordingMetricPort(), new RecordingTracePort());

        Assert.Equal(ObservabilityState.Created, module.State);
        Assert.Equal(ObservabilityState.Configured, module.Configure().State);
        Assert.Equal(ObservabilityState.Running, module.Start().State);
        Assert.Equal(ObservabilityState.Degraded, module.MarkDegraded().State);
        Assert.Equal(ObservabilityState.Running, module.Recover().State);
        Assert.Equal(ObservabilityState.Flushing, module.BeginFlush().State);
        Assert.Equal(ObservabilityState.Closed, module.Close().State);
        var closedEvent = ValidEvent();
        Assert.False(module.Services.Events.Emit(in closedEvent).IsAccepted);

        var faulted = ObservabilityModule.Create(new RecordingEventPort(), new RecordingMetricPort(), new RecordingTracePort());
        Assert.Equal(ObservabilityState.Configured, faulted.Configure().State);
        Assert.Equal(ObservabilityState.Faulted, faulted.Fault("QueueFull").State);
        var faultedEvent = ValidEvent();
        Assert.False(faulted.Services.Events.Emit(in faultedEvent).IsAccepted);
    }

    private static RuntimeEventView ValidEvent() => new(
        "evt-1",
        "Diagnostic",
        "Info",
        DateTimeOffset.UnixEpoch,
        new CorrelationView("Session", "product", "release", "session", "world", "trace", "producer", 1UL),
        "message",
        "BestEffort");

    private sealed class RecordingEventPort : IRuntimeEventPort
    {
        public int Calls { get; private set; }

        public EventEnqueueResult Emit(in RuntimeEventView value)
        {
            Calls++;
            return EventEnqueueResult.Accepted();
        }
    }

    private sealed class RecordingMetricPort : IMetricPort
    {
        public MetricRecordResult Record(in MetricSampleView sample) => MetricRecordResult.Accepted();

        public MetricSnapshot CaptureSnapshot() => new(0, DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingTracePort : ITracePort
    {
        public TraceScope Start(in TraceStartView start) => new();
    }
}
