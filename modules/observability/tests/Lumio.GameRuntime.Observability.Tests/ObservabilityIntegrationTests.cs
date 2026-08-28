using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Observability;
using Xunit;

namespace Lumio.GameRuntime.Observability.Tests;

/// <summary>
/// T05.S01 / S05 / S07 中不依赖 generated validator 的口径:掉包计数必须出 metric,
/// BestEffort 必须真的流经有界队列,durable 写失败必须升级为 FatalEvidenceWrite。
/// S03 / S06 的 generated validator 与 Txn/Command/WAL 三个 generated overload
/// 在当前 LGE-V1.4-2026-08-27 基线下无对应生成物,记为上游阻塞,不在本文件覆盖。
/// </summary>
public sealed class ObservabilityIntegrationTests
{
    [Fact]
    public void DroppedBestEffortEventEmitsDroppedTotalMetricOnce()
    {
        var metrics = new CapturingMetricPort();
        var queue = DiagnosticEventQueue.Create(new DiagnosticQueueBudget(2, 4096), metrics);
        var first = Event(1);
        var second = Event(2);
        var third = Event(3);

        Assert.Equal(DiagnosticWriteStatus.Accepted, queue.TryWrite(in first).Status);
        Assert.Equal(DiagnosticWriteStatus.Accepted, queue.TryWrite(in second).Status);
        Assert.Equal(DiagnosticWriteStatus.DroppedBestEffort, queue.TryWrite(in third).Status);

        Assert.Equal(2, queue.Count);
        Assert.Single(metrics.Samples);
        Assert.Equal("runtime.diagnostic.dropped_total", metrics.Samples[0].MetricId);
        Assert.Equal(1d, metrics.Samples[0].Value);
        Assert.Equal(third.Correlation, metrics.Samples[0].Correlation);
    }

    [Fact]
    public void AcceptedEventDoesNotEmitDroppedTotalMetric()
    {
        var metrics = new CapturingMetricPort();
        var queue = DiagnosticEventQueue.Create(new DiagnosticQueueBudget(2, 4096), metrics);
        var value = Event(1);

        Assert.Equal(DiagnosticWriteStatus.Accepted, queue.TryWrite(in value).Status);

        Assert.Empty(metrics.Samples);
    }

    [Fact]
    public void ModuleRoutesBestEffortThroughBoundedQueueInsteadOfCallingPortDirectly()
    {
        var events = new CountingEventPort();
        var module = ObservabilityModule.Create(
            events,
            new CapturingMetricPort(),
            new NoopTracePort(),
            new DiagnosticQueueBudget(1, 4096),
            durableCapacity: 1);
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);

        var first = Event(1);
        var second = Event(2);

        Assert.Equal(EventEnqueueStatus.Accepted, module.Services.Events.Emit(in first).Status);
        Assert.Equal(EventEnqueueStatus.Sampled, module.Services.Events.Emit(in second).Status);
        Assert.Equal(0, events.Calls);
    }

    [Fact]
    public void ModuleDurableEventIsBackpressuredAndNeverDivertedToDiagnosticQueue()
    {
        var metrics = new CapturingMetricPort();
        var module = ObservabilityModule.Create(
            new CountingEventPort(),
            metrics,
            new NoopTracePort(),
            new DiagnosticQueueBudget(8, 4096),
            durableCapacity: 1);
        Assert.True(module.Configure().Succeeded);
        Assert.True(module.Start().Succeeded);

        var first = Event(1) with { Durability = "Durable" };
        var second = Event(2) with { Durability = "Durable" };

        Assert.Equal(EventEnqueueStatus.Accepted, module.Services.Events.Emit(in first).Status);

        var overflow = module.Services.Events.Emit(in second);

        Assert.Equal(EventEnqueueStatus.Backpressured, overflow.Status);
        Assert.NotEqual(EventEnqueueStatus.Sampled, overflow.Status);
        // 满载的 durable record 不得被降级成 BestEffort 掉包。
        Assert.Empty(metrics.Samples);
    }

    [Fact]
    public void EvidenceWriteFailureEscalatesToFatalEvidenceWrite()
    {
        var bundle = FailureBundleAssembler.Assemble(SnapshotContext()).Bundle;
        Assert.NotNull(bundle);

        var failure = FailureBundleAssembler.Write(bundle!, new RejectingDurablePort());

        Assert.NotNull(failure);
        Assert.Equal(ObservabilityFailureClass.Fatal, failure!.Value.Class);
        Assert.Equal(ObservabilityFailure.FatalEvidenceWriteErrorId, failure.Value.GeneratedErrorId);
        Assert.Equal(bundle!.FailureId, failure.Value.EvidenceReference);
    }

    [Fact]
    public void EvidenceWriteSuccessDoesNotEscalate()
    {
        var bundle = FailureBundleAssembler.Assemble(SnapshotContext()).Bundle;
        Assert.NotNull(bundle);

        Assert.Null(FailureBundleAssembler.Write(bundle!, new DurableEvidenceRouter(4)));
    }

    private static FailureContextSnapshot SnapshotContext() => new(
        "failure-1",
        "SIMULATION_FAULT",
        "Simulation",
        DateTimeOffset.UnixEpoch,
        Correlation(1),
        new string('a', 64),
        "snapshot-1",
        null,
        null,
        null,
        null,
        new List<FailureArtifactView> { new("bundle.json", new string('b', 64), 12) },
        true,
        "replay --tick 1");

    private static CorrelationView Correlation(int id) =>
        new("Session", "product", "release", "session", "world", "trace", "producer", (ulong)id);

    private static RuntimeEventView Event(int id) => new(
        $"event-{id}",
        "Diagnostic",
        "Info",
        DateTimeOffset.UnixEpoch,
        Correlation(id),
        "message",
        "BestEffort");

    private sealed class CapturingMetricPort : IMetricPort
    {
        public List<MetricSampleView> Samples { get; } = new();

        public MetricRecordResult Record(in MetricSampleView sample)
        {
            Samples.Add(sample);
            return MetricRecordResult.Accepted();
        }

        public MetricSnapshot CaptureSnapshot() => new(Samples.Count, DateTimeOffset.UnixEpoch);
    }

    private sealed class CountingEventPort : IRuntimeEventPort
    {
        public int Calls { get; private set; }

        public EventEnqueueResult Emit(in RuntimeEventView value)
        {
            Calls++;
            return EventEnqueueResult.Accepted();
        }
    }

    private sealed class NoopTracePort : ITracePort
    {
        public TraceScope Start(in TraceStartView start) => new();
    }

    private sealed class RejectingDurablePort : IDurableEvidencePort
    {
        public DurableEnqueueResult Enqueue(in DurableRecordView record) =>
            new(DurableEnqueueStatus.Rejected, 0UL, false, "EvidenceMissing");

        public DurableQueryResult Query(string idempotencyKey) =>
            new(DurableQueryStatus.NotFound, null, null);
    }
}
