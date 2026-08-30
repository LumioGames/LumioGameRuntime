using System;
using System.Collections.Generic;
using System.Linq;
using Lumio.GameRuntime.Simulation.Phases;

namespace Lumio.GameRuntime.Simulation.Tick;

public delegate void PhaseHandler(TickExecutionContext context);

public readonly record struct TickRunnerOptions(
    ulong Seed,
    ulong InitialTickId,
    int MaxOutputItems,
    long MaxOutputBytes)
{
    public TickRunnerOptions(int cacheCapacity)
        : this(0, 1, 256, 1_048_576)
    {
        CacheCapacity = cacheCapacity;
    }

    public int CacheCapacity { get; init; } = 256;

    public bool IsValid => MaxOutputItems > 0 && MaxOutputBytes > 0 && CacheCapacity > 0;
}

public sealed class TickRunner
{
    private readonly object _gate = new();
    private readonly TickRunnerOptions _options;
    private readonly Dictionary<TickPhase, PhaseHandler> _handlers;
    private readonly TickResultCache _cache;
    private readonly FailStopController _failStop;
    private readonly int _ownerThreadId;
    private bool _running;
    private bool _faulted;
    private ulong _nextTickId;

    public TickRunner(TickRunnerOptions options, IReadOnlyDictionary<TickPhase, PhaseHandler>? handlers = null)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _nextTickId = options.InitialTickId;
        _handlers = handlers is null
            ? new Dictionary<TickPhase, PhaseHandler>()
            : new Dictionary<TickPhase, PhaseHandler>(handlers);
        _cache = new TickResultCache(options.CacheCapacity);
        _failStop = new FailStopController();
        _ownerThreadId = Environment.CurrentManagedThreadId;
        if (!PhaseGraph.Default.ValidateAgainstGeneratedContract().Succeeded) throw new InvalidOperationException("The generated phase contract is invalid.");
    }

    public TickRunner(int cacheCapacity = 256)
        : this(new TickRunnerOptions(0, 1, 256, 1_048_576) { CacheCapacity = cacheCapacity })
    {
    }

    public ulong NextTickId
    {
        get { lock (_gate) return _nextTickId; }
    }

    public bool IsFaulted
    {
        get { lock (_gate) return _faulted; }
    }

    public FailStopController FailStop => _failStop;

    /// <summary>Registers a deterministic phase hook before the owner starts a tick.</summary>
    public bool SetHandler(TickPhase phase, PhaseHandler handler)
    {
        if (!Enum.IsDefined(typeof(TickPhase), phase) || handler is null) return false;
        lock (_gate)
        {
            if (_running || _faulted) return false;
            _handlers[phase] = handler;
            return true;
        }
    }

    public bool RemoveHandler(TickPhase phase)
    {
        lock (_gate)
        {
            if (_running || _faulted) return false;
            return _handlers.Remove(phase);
        }
    }

    public TickRunResult Run(HostTickRequest request) => Run(in request);

    public TickRunResult Run(in HostTickRequest request)
    {
        lock (_gate)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return TickRunResult.Rejected(request.TickId, "WrongContext", "Tick execution belongs to the owner thread.");
            string requestHash = request.ComputeCanonicalHashHex();
            if (_cache.TryGet(request.TickId, out TickRunResult? existing))
            {
                if (existing!.RequestHashHex == requestHash) return existing.AsIdempotent();
                var conflict = new PhaseFailureRecord(request.TickId, TickPhase.IngressCapture, null, "RevisionConflict", "The same TickId was supplied with different canonical inputs.", existing.IsCommitted);
                _failStop.FailStop(conflict);
                _faulted = true;
                return TickRunResult.Faulted(request.TickId, requestHash, conflict);
            }

            if (_faulted) return TickRunResult.Rejected(request.TickId, "ContextDestroyed", "The simulation is faulted and must be rebuilt.");
            if (_running) return TickRunResult.Rejected(request.TickId, "WrongContext", "run_tick is not reentrant.");
            if (request.TickId != _nextTickId) return TickRunResult.Rejected(request.TickId, "RevisionConflict", "TickId is not the next logical tick.");
            if (!request.IsWellFormed) return TickRunResult.Rejected(request.TickId, "ManifestMalformed", "The tick request is not well formed.");
            if (request.SchemaEpoch != Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch)
                return TickRunResult.Rejected(request.TickId, "StaleEpoch", "Schema epoch does not match generated contracts.");

            _running = true;
            var context = new TickExecutionContext(request, _options.MaxOutputItems, _options.MaxOutputBytes);
            try
            {
                foreach (TickPhase phase in PhaseGraph.Default.Phases)
                {
                    context.EnterPhase(phase);
                    if (_handlers.TryGetValue(phase, out PhaseHandler? handler)) handler(context);
                    if (phase == TickPhase.GasAndEventFinalize && !context.IsCommitted) context.MarkCommitted();
                    context.CompleteCurrentPhase();
                }

                string stateHash = context.ComputeStateHash();
                TickRunResult result = TickRunResult.Success(context, requestHash, stateHash);
                _cache.Add(result);
                checked { _nextTickId++; }
                _running = false;
                return result;
            }
            catch (Exception exception)
            {
                TickPhase phase = context.CurrentPhase;
                string errorId = exception is TickExecutionException ? "InternalInvariant" : "PanicBoundary";
                var failure = new PhaseFailureRecord(request.TickId, phase, null, errorId, exception.Message, context.IsCommitted);
                context.RecordFailure(failure);
                _failStop.FailStop(failure);
                _faulted = true;
                TickRunResult result = TickRunResult.Faulted(context, requestHash, failure);
                _cache.Add(result);
                _running = false;
                return result;
            }
        }
    }
}
