using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumio.GameRuntime.GeneratedContracts;

[assembly: InternalsVisibleTo("Lumio.GameRuntime.Observability.Tests")]

namespace Lumio.GameRuntime.Observability;

public readonly record struct ObservabilityOptionsView(int SchemaEpoch)
{
    public static ObservabilityOptionsView ForGeneratedContracts() =>
        new(GeneratedContractManifest.SchemaEpoch);
}

public readonly record struct ObservabilityResult(
    bool Succeeded,
    ObservabilityState State,
    string? GeneratedErrorId)
{
    public static ObservabilityResult Accepted(ObservabilityState state) =>
        new(true, state, null);

    public static ObservabilityResult Rejected(ObservabilityState state, string generatedErrorId) =>
        new(false, state, generatedErrorId);
}

public sealed class ObservabilityModule
{
    private const string InvalidLifecycleErrorId = "ManifestMalformed";
    private const string InvalidEventErrorId = "ManifestMalformed";
    private readonly object _stateGate = new();
    private readonly Dictionary<string, ProducerSequence> _producerSequences = new(StringComparer.Ordinal);
    private readonly ObservabilityServices _services;
    private ObservabilityState _state = ObservabilityState.Created;

    internal ObservabilityModule(
        IRuntimeEventPort eventPort,
        IMetricPort metricPort,
        ITracePort tracePort)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(eventPort);
        ArgumentNullException.ThrowIfNull(metricPort);
        ArgumentNullException.ThrowIfNull(tracePort);
#else
        if (eventPort is null) throw new ArgumentNullException(nameof(eventPort));
        if (metricPort is null) throw new ArgumentNullException(nameof(metricPort));
        if (tracePort is null) throw new ArgumentNullException(nameof(tracePort));
#endif

        _services = new ObservabilityServices(
            new EventPortFacade(this, eventPort),
            new MetricPortFacade(this, metricPort),
            new TracePortFacade(this, tracePort));
    }

    public static ObservabilityModule Create(
        IRuntimeEventPort eventPort,
        IMetricPort metricPort,
        ITracePort tracePort) =>
        new(eventPort, metricPort, tracePort);

    public ObservabilityState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public ObservabilityServices Services => _services;

    public ObservabilityResult Configure() => Configure(ObservabilityOptionsView.ForGeneratedContracts());

    public ObservabilityResult Configure(ObservabilityOptionsView options)
    {
        lock (_stateGate)
        {
            if (_state != ObservabilityState.Created ||
                options.SchemaEpoch != GeneratedContractManifest.SchemaEpoch)
            {
                return ObservabilityResult.Rejected(_state, InvalidLifecycleErrorId);
            }

            _state = ObservabilityState.Configured;
            return ObservabilityResult.Accepted(_state);
        }
    }

    public ObservabilityResult Start()
    {
        lock (_stateGate)
        {
            if (_state != ObservabilityState.Configured)
            {
                return ObservabilityResult.Rejected(_state, InvalidLifecycleErrorId);
            }

            _state = ObservabilityState.Running;
            return ObservabilityResult.Accepted(_state);
        }
    }

    public ObservabilityResult MarkDegraded()
    {
        lock (_stateGate)
        {
            if (_state != ObservabilityState.Running)
            {
                return ObservabilityResult.Rejected(_state, InvalidLifecycleErrorId);
            }

            _state = ObservabilityState.Degraded;
            return ObservabilityResult.Accepted(_state);
        }
    }

    public ObservabilityResult Recover()
    {
        lock (_stateGate)
        {
            if (_state != ObservabilityState.Degraded)
            {
                return ObservabilityResult.Rejected(_state, InvalidLifecycleErrorId);
            }

            _state = ObservabilityState.Running;
            return ObservabilityResult.Accepted(_state);
        }
    }

    public ObservabilityResult BeginFlush()
    {
        lock (_stateGate)
        {
            if (_state is not (ObservabilityState.Running or ObservabilityState.Degraded))
            {
                return ObservabilityResult.Rejected(_state, InvalidLifecycleErrorId);
            }

            _state = ObservabilityState.Flushing;
            return ObservabilityResult.Accepted(_state);
        }
    }

    public ObservabilityResult Close()
    {
        lock (_stateGate)
        {
            if (_state != ObservabilityState.Flushing)
            {
                return ObservabilityResult.Rejected(_state, InvalidLifecycleErrorId);
            }

            _state = ObservabilityState.Closed;
            return ObservabilityResult.Accepted(_state);
        }
    }

    public ObservabilityResult Fault(string generatedErrorId)
    {
        if (string.IsNullOrWhiteSpace(generatedErrorId) || !IsGeneratedErrorId(generatedErrorId))
        {
            throw new ArgumentException("A stable generated error ID is required.", nameof(generatedErrorId));
        }

        lock (_stateGate)
        {
            if (_state is ObservabilityState.Closed or ObservabilityState.Faulted)
            {
                return ObservabilityResult.Rejected(_state, InvalidLifecycleErrorId);
            }

            _state = ObservabilityState.Faulted;
            return ObservabilityResult.Accepted(_state);
        }
    }

    public EventSequence NextEventSequence(string producerId)
    {
        if (string.IsNullOrWhiteSpace(producerId))
        {
            throw new ArgumentException("A producer ID is required.", nameof(producerId));
        }

        lock (_stateGate)
        {
            if (_state is not (ObservabilityState.Running or ObservabilityState.Degraded))
            {
                throw new InvalidOperationException($"Producer registration is not allowed in {_state}.");
            }

            if (!_producerSequences.TryGetValue(producerId, out ProducerSequence? sequence))
            {
                sequence = new ProducerSequence(0UL);
                _producerSequences.Add(producerId, sequence);
            }

            return sequence.Next();
        }
    }

    private bool IsAcceptingInput => State is ObservabilityState.Running or ObservabilityState.Degraded;

    private static EventEnqueueResult RejectEvent() =>
        EventEnqueueResult.Rejected(InvalidEventErrorId);

    private static bool IsGeneratedErrorId(string value)
    {
        foreach (string errorId in Lumio.Gen.ContractTypes.Catalog.StableErrorIds)
        {
            if (string.Equals(errorId, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class EventPortFacade : IRuntimeEventPort
    {
        private readonly ObservabilityModule _owner;
        private readonly IRuntimeEventPort _inner;

        internal EventPortFacade(ObservabilityModule owner, IRuntimeEventPort inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public EventEnqueueResult Emit(in RuntimeEventView value)
        {
            if (!_owner.IsAcceptingInput || !value.IsWellFormed)
            {
                return RejectEvent();
            }

            return _inner.Emit(in value);
        }
    }

    private sealed class MetricPortFacade : IMetricPort
    {
        private readonly ObservabilityModule _owner;
        private readonly IMetricPort _inner;

        internal MetricPortFacade(ObservabilityModule owner, IMetricPort inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public MetricRecordResult Record(in MetricSampleView sample)
        {
            if (!_owner.IsAcceptingInput || !sample.IsWellFormed)
            {
                return MetricRecordResult.Rejected(InvalidEventErrorId);
            }

            return _inner.Record(in sample);
        }

        public MetricSnapshot CaptureSnapshot() => _inner.CaptureSnapshot();
    }

    private sealed class TracePortFacade : ITracePort
    {
        private readonly ObservabilityModule _owner;
        private readonly ITracePort _inner;

        internal TracePortFacade(ObservabilityModule owner, ITracePort inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public TraceScope Start(in TraceStartView start)
        {
            if (!_owner.IsAcceptingInput || !start.IsWellFormed)
            {
                return new TraceScope();
            }

            return _inner.Start(in start);
        }
    }
}
