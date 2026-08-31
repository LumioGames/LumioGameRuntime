using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Phases;

namespace Lumio.GameRuntime.Simulation.Tick;

internal delegate PhaseOutcome PhaseHandler(TickExecutionContext context);

internal readonly record struct TickRunnerOptions(
    ulong Seed,
    ulong InitialTickId,
    int MaxOutputItems,
    long MaxOutputBytes)
{
    internal TickRunnerOptions(int cacheCapacity)
        : this(0, 1, 256, 1_048_576)
    {
        CacheCapacity = cacheCapacity;
    }

    public int CacheCapacity { get; init; } = 256;

    public int IngressCapacity { get; init; } = 256;

    public long MaxIngressBytes { get; init; } = 1_048_576;

    public string SessionId { get; init; } = "simulation";

    public bool IsValid =>
        MaxOutputItems > 0 &&
        MaxOutputBytes > 0 &&
        CacheCapacity > 0 &&
        IngressCapacity > 0 &&
        MaxIngressBytes > 0 &&
        SimulationValidation.IsIdentifier(SessionId);
}

internal sealed class TickRunner
{
    private readonly object _gate = new();
    private readonly TickRunnerOptions _options;
    private readonly Dictionary<TickPhase, PhaseHandler> _handlers;
    private readonly TickResultCache _cache;
    private readonly FailStopController _failStop;
    private readonly int _ownerThreadId;
    private TickExecutorComposition _composition;
    private readonly bool _compositionIsExplicit;
    private bool _running;
    private bool _faulted;
    private ulong _nextTickId;
    private PhaseFailureRecord? _deferredFailure;

    internal TickRunner(TickRunnerOptions options, IReadOnlyDictionary<TickPhase, PhaseHandler>? handlers)
        : this(options, handlers, TickExecutorCapability.None, null, null, null, false)
    {
    }

    private TickRunner(
        TickRunnerOptions options,
        IReadOnlyDictionary<TickPhase, PhaseHandler>? handlers,
        TickExecutorCapability capabilities,
        IAuthoritativeTickStatePort? statePort,
        IDurableTickReplayPort? replayPort,
        ISimulationFailureBundlePort? failurePort,
        bool explicitComposition)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _nextTickId = options.InitialTickId;
        _handlers = handlers is null
            ? new Dictionary<TickPhase, PhaseHandler>()
            : new Dictionary<TickPhase, PhaseHandler>(handlers);
        _composition = TickExecutorComposition.ForHandlers(_handlers, capabilities, statePort, replayPort, failurePort);
        _compositionIsExplicit = explicitComposition;
        _cache = new TickResultCache(options.CacheCapacity);
        _failStop = new FailStopController();
        _ownerThreadId = Environment.CurrentManagedThreadId;
        if (!PhaseGraph.Default.ValidateAgainstGeneratedContract().Succeeded) throw new InvalidOperationException("The generated phase contract is invalid.");
    }

    internal TickRunner(TickRunnerOptions options)
        : this(options, new Dictionary<TickPhase, PhaseHandler>(), TickExecutorCapability.None, null, null, null, false)
    {
    }

    internal TickRunner(int cacheCapacity = 256)
        : this(new TickRunnerOptions(0, 1, 256, 1_048_576) { CacheCapacity = cacheCapacity })
    {
    }

    /// <summary>Creates a runner only from an explicit executor/capability composition.</summary>
    internal static TickRunner FromComposition(TickRunnerOptions options, TickExecutorComposition composition)
    {
        if (composition is null) throw new ArgumentNullException(nameof(composition));
        return new TickRunner(
            options,
            composition.Handlers,
            composition.Capabilities,
            composition.StatePort,
            composition.ReplayPort,
            composition.FailurePort,
            true);
    }

    internal ulong NextTickId
    {
        get { lock (_gate) return _nextTickId; }
    }

    internal bool IsFaulted
    {
        get { lock (_gate) return _faulted; }
    }

    internal PhaseFailureRecord? DeferredFailure
    {
        get { lock (_gate) return _deferredFailure; }
    }

    internal FailStopController FailStop => _failStop;

    /// <summary>Registers a phase executor for an explicit composition; not a public execution surface.</summary>
    internal bool SetHandler(TickPhase phase, PhaseHandler handler)
    {
        if (!Enum.IsDefined(typeof(TickPhase), phase) || handler is null) return false;
        lock (_gate)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId || _running || _faulted) return false;
            _handlers[phase] = handler;
            _composition = TickExecutorComposition.ForHandlers(
                _handlers,
                _composition.Capabilities,
                _composition.StatePort,
                _composition.ReplayPort,
                _composition.FailurePort);
            return true;
        }
    }

    internal bool RemoveHandler(TickPhase phase)
    {
        lock (_gate)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId || _running || _faulted) return false;
            bool removed = _handlers.Remove(phase);
            _composition = TickExecutorComposition.ForHandlers(
                _handlers,
                _composition.Capabilities,
                _composition.StatePort,
                _composition.ReplayPort,
                _composition.FailurePort);
            return removed;
        }
    }

    internal TickRunResult Run(HostTickRequest request) => Run(in request, null);

    internal TickRunResult Run(in HostTickRequest request) => Run(in request, null);

    internal TickRunResult Run(in HostTickRequest request, Func<bool>? lifecycleGuard)
    {
        lock (_gate)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return TickRunResult.Rejected(request.TickId, "WrongContext", "Tick execution belongs to the owner thread.");

            if (!request.IsWellFormed) return TickRunResult.Rejected(request.TickId, "ManifestMalformed", "The tick request is not well formed.");
            if (request.SchemaEpoch != Lumio.GameRuntime.GeneratedContracts.GeneratedContractManifest.SchemaEpoch)
                return TickRunResult.Rejected(request.TickId, "StaleEpoch", "Schema epoch does not match generated contracts.");
            if (request.HasExplicitSeed && request.Seed != _options.Seed)
                return TickRunResult.Rejected(request.TickId, "InvalidArgument", "The request seed does not match the authoritative session seed.");

            HostTickRequest effectiveRequest = request.WithAuthoritativeSeed(_options.Seed);
            if (!effectiveRequest.TryValidateIngress(_options.IngressCapacity, _options.MaxIngressBytes, out string ingressErrorId, out string ingressDetail))
                return TickRunResult.Rejected(request.TickId, ingressErrorId, ingressDetail);

            string canonicalRequestHash;
            try
            {
                canonicalRequestHash = effectiveRequest.ComputeCanonicalHashHex();
            }
            catch (Exception exception) when (exception is ArgumentException or EncoderFallbackException or OverflowException)
            {
                return TickRunResult.Rejected(request.TickId, "ManifestMalformed", "The captured ingress cannot be canonicalized.");
            }

            if (TryFindCompositionFailure(out TickPhase missingPhase, out string compositionErrorId, out string compositionDetail))
            {
                var failure = new PhaseFailureRecord(request.TickId, missingPhase, null, compositionErrorId, compositionDetail, false);
                return FailWithoutContext(effectiveRequest, canonicalRequestHash, failure);
            }

            if (!TryCaptureAuthority(effectiveRequest, false, out AuthoritativeTickStateSnapshot? initialAuthority, out string authorityDetail))
            {
                var failure = new PhaseFailureRecord(
                    request.TickId,
                    TickPhase.IngressCapture,
                    null,
                    "InternalInvariant",
                    authorityDetail,
                    false);
                return FailWithoutContext(effectiveRequest, canonicalRequestHash, failure);
            }

            string requestHash = ComputeAuthoritativeRequestHash(canonicalRequestHash, initialAuthority!);
            if (_cache.TryGet(request.TickId, out TickRunResult? existing))
            {
                if (existing!.RequestHashHex == requestHash) return existing.AsIdempotent();
                var conflict = new PhaseFailureRecord(
                    request.TickId,
                    TickPhase.IngressCapture,
                    null,
                    "RevisionConflict",
                    "The same TickId was supplied with different canonical inputs or authoritative identity.",
                    existing.IsCommitted);
                return FailWithoutContext(effectiveRequest, requestHash, conflict, initialAuthority);
            }

            var replayKey = new DurableTickReplayKey(_options.SessionId, effectiveRequest.Epoch.Value, effectiveRequest.TickId);
            DurableTickReplayLookup replayLookup = LookupDurableReplay(in replayKey);
            if (replayLookup.Status == DurableTickReplayLookupStatus.Found)
            {
                if (replayLookup.Record is null || !replayLookup.Record.IsWellFormedFor(in replayKey))
                {
                    var failure = new PhaseFailureRecord(
                        request.TickId,
                        TickPhase.IngressCapture,
                        null,
                        "EvidenceDigestMismatch",
                        "The durable committed Tick replay record is corrupt.",
                        false);
                    return FailWithoutContext(effectiveRequest, requestHash, failure, initialAuthority);
                }

                TickRunResult durable = replayLookup.Record.Result;
                if (string.Equals(durable.RequestHashHex, requestHash, StringComparison.Ordinal))
                    return durable.AsIdempotent();
                var conflict = new PhaseFailureRecord(
                    request.TickId,
                    TickPhase.IngressCapture,
                    null,
                    "RevisionConflict",
                    "The same TickId was supplied with different canonical inputs or authoritative identity.",
                    durable.IsCommitted);
                return FailWithoutContext(effectiveRequest, requestHash, conflict, initialAuthority);
            }

            if (replayLookup.Status is DurableTickReplayLookupStatus.Unavailable or DurableTickReplayLookupStatus.Corrupt)
            {
                string errorId = replayLookup.Status == DurableTickReplayLookupStatus.Corrupt
                    ? "EvidenceDigestMismatch"
                    : "EvidenceMissing";
                var failure = new PhaseFailureRecord(
                    request.TickId,
                    TickPhase.IngressCapture,
                    null,
                    errorId,
                    "The durable committed Tick replay lookup is unavailable or corrupt.",
                    false);
                return FailWithoutContext(effectiveRequest, requestHash, failure, initialAuthority);
            }

            if (_faulted) return TickRunResult.Rejected(request.TickId, "ContextDestroyed", "The simulation is faulted and must be rebuilt.");
            if (_running) return TickRunResult.Rejected(request.TickId, "WrongContext", "run_tick is not reentrant.");
            if (request.TickId != _nextTickId) return TickRunResult.Rejected(request.TickId, "RevisionConflict", "TickId is not the next logical tick.");

            _running = true;
            var context = new TickExecutionContext(
                effectiveRequest,
                _options.MaxOutputItems,
                _options.MaxOutputBytes,
                _ownerThreadId,
                _options.SessionId,
                initialAuthority!);
            string stateHash = string.Empty;
            try
            {
                context.Checkpoint();
                foreach (TickPhase phase in PhaseGraph.Default.Phases)
                {
                    EnsureLifecycle(lifecycleGuard);
                    context.ConsumeWork(1);
                    context.EnterPhase(phase);
                    try
                    {
                        PhaseOutcome outcome = _handlers[phase](context);
                        EnsureLifecycle(lifecycleGuard);
                        context.Checkpoint();
                        if (!Enum.IsDefined(typeof(PhaseOutcomeStatus), outcome.Status) || outcome.Status == PhaseOutcomeStatus.Invalid)
                            throw new TickExecutionException("InternalInvariant", "The phase executor did not return a valid outcome.");
                        if (!outcome.Succeeded)
                        {
                            if (PhaseContractTable.Default[phase].FailureClass == PhaseFailureClass.BusinessReject)
                            {
                                var rejection = new PhaseFailureRecord(
                                    request.TickId,
                                    phase,
                                    null,
                                    outcome.GeneratedErrorId ?? "InvalidArgument",
                                    outcome.Detail ?? "The phase rejected the Tick.",
                                    false);
                                context.RecordFailure(rejection);
                                return TickRunResult.Rejected(context, requestHash, rejection);
                            }

                            throw new TickExecutionException(
                                outcome.GeneratedErrorId ?? "InternalInvariant",
                                outcome.Detail ?? "A non-business phase returned a rejection.");
                        }
                    }
                    catch (Exception exception) when (TryGetBusinessRejection(exception, phase, out string rejectionId, out string rejectionDetail))
                    {
                        var rejection = new PhaseFailureRecord(request.TickId, phase, null, rejectionId, rejectionDetail, false);
                        context.RecordFailure(rejection);
                        return TickRunResult.Rejected(context, requestHash, rejection);
                    }

                    context.CompleteCurrentPhase();
                    if (phase == TickPhase.GasAndEventFinalize)
                    {
                        EnsureLifecycle(lifecycleGuard);
                        context.Checkpoint();
                        context.MarkCommitted();
                    }
                }

                if (!TryCaptureAuthority(effectiveRequest, true, out AuthoritativeTickStateSnapshot? committedAuthority, out authorityDetail))
                    throw new TickExecutionException("InternalInvariant", authorityDetail);
                stateHash = context.ComputeStateHash(committedAuthority!);
                TickRunResult success = TickRunResult.Success(context, requestHash, stateHash);
                if (!TryPersistCommittedResult(in replayKey, success, out string replayErrorId, out string replayDetail))
                    throw new TickExecutionException(replayErrorId, replayDetail);
                _cache.Add(success);
                AdvanceAfterCommit(context.IsCommitted);
                return success;
            }
            catch (Exception exception)
            {
                TickPhase phase = context.CurrentPhase;
                string errorId = GetErrorId(exception);
                var failure = new PhaseFailureRecord(request.TickId, phase, null, errorId, exception.Message, context.IsCommitted);
                context.RecordFailure(failure);
                _failStop.FailStop(failure);
                _faulted = true;
                if (context.IsCommitted) _deferredFailure ??= failure;

                AuthoritativeTickStateSnapshot? evidenceAuthority = !context.IsCommitted ||
                    initialAuthority!.Revisions.TickId == effectiveRequest.TickId
                        ? initialAuthority
                        : null;
                if (TryCaptureAuthority(
                        effectiveRequest,
                        context.IsCommitted,
                        out AuthoritativeTickStateSnapshot? capturedFailureAuthority,
                        out _) &&
                    initialAuthority!.HasSameIdentity(capturedFailureAuthority!))
                {
                    evidenceAuthority = capturedFailureAuthority!;
                }
                FailureEvidenceReceipt evidence = PersistFailureEvidence(
                    failure,
                    effectiveRequest.Epoch.Value,
                    FindLastCompletedPhase(context.PhaseRecords),
                    evidenceAuthority);
                TickRunResult result = TickRunResult.Faulted(context, requestHash, failure, stateHash, evidence);
                if (context.IsCommitted)
                {
                    TryPersistCommittedResult(in replayKey, result, out _, out _);
                    AdvanceAfterCommit(true);
                }

                _cache.Add(result);
                return result;
            }
            finally
            {
                context.Close();
                _running = false;
            }
        }
    }

    private bool TryFindCompositionFailure(out TickPhase missingPhase, out string errorId, out string detail)
    {
        if (!_compositionIsExplicit)
        {
            missingPhase = TickPhase.IngressCapture;
            errorId = "InternalInvariant";
            detail = "An explicit TickExecutorComposition is required; raw phase delegates are not executable authority.";
            return true;
        }

        if (_composition.StatePort is null || !_composition.StatePort.IsAvailable)
        {
            missingPhase = TickPhase.IngressCapture;
            errorId = "CapabilityMissing";
            detail = "An available authoritative Tick state contributor capability is required.";
            return true;
        }

        if (_composition.ReplayPort is null || !_composition.ReplayPort.IsAvailable || _composition.ReplayPort.RetentionCapacity <= 0)
        {
            missingPhase = TickPhase.IngressCapture;
            errorId = "CapabilityMissing";
            detail = "An available bounded durable committed Tick replay capability is required.";
            return true;
        }

        if (_composition.FailurePort is null || !_composition.FailurePort.IsAvailable)
        {
            missingPhase = TickPhase.IngressCapture;
            errorId = "CapabilityMissing";
            detail = "An available durable Simulation failure bundle capability is required.";
            return true;
        }

        foreach (TickPhase phase in PhaseGraph.Default.Phases)
        {
            if (!_handlers.TryGetValue(phase, out PhaseHandler? executor) || executor is null)
            {
                missingPhase = phase;
                errorId = "InternalInvariant";
                detail = $"No executor is registered for required phase {phase}.";
                return true;
            }

            TickExecutorCapability required = TickExecutorComposition.CapabilityFor(phase);
            if ((_composition.Capabilities & required) != required)
            {
                missingPhase = phase;
                errorId = "CapabilityMissing";
                detail = $"The executor composition does not declare capability {required}.";
                return true;
            }
        }

        missingPhase = default;
        errorId = string.Empty;
        detail = string.Empty;
        return false;
    }

    private DurableTickReplayLookup LookupDurableReplay(in DurableTickReplayKey key)
    {
        try
        {
            return _composition.ReplayPort!.Lookup(in key);
        }
        catch (Exception)
        {
            return new DurableTickReplayLookup(DurableTickReplayLookupStatus.Unavailable, null);
        }
    }

    private string ComputeAuthoritativeRequestHash(
        string canonicalRequestHash,
        AuthoritativeTickStateSnapshot authority)
    {
        using var stream = new MemoryStream();
        SimulationRevisionSnapshot.WriteString(stream, canonicalRequestHash);
        SimulationRevisionSnapshot.WriteString(stream, _options.SessionId);
        SimulationRevisionSnapshot.WriteString(stream, authority.WorldId);
        SimulationRevisionSnapshot.WriteString(stream, authority.GameReleaseId);
        SimulationRevisionSnapshot.WriteString(stream, authority.ConfigSnapshotId);
        SimulationRevisionSnapshot.WriteString(stream, authority.ManifestHashHex);
        return SimulationHash.Sha256Hex(stream.ToArray());
    }

    private TickRunResult FailWithoutContext(
        in HostTickRequest request,
        string requestHash,
        PhaseFailureRecord failure,
        AuthoritativeTickStateSnapshot? frozenAuthority = null)
    {
        _failStop.FailStop(failure);
        _faulted = true;
        AuthoritativeTickStateSnapshot? authority = frozenAuthority;
        if (authority is null) TryCaptureAuthority(request, false, out authority, out _);
        FailureEvidenceReceipt evidence = PersistFailureEvidence(failure, request.Epoch.Value, null, authority);
        TickRunResult result = TickRunResult.Faulted(request.TickId, requestHash, failure, evidence);
        _cache.Add(result);
        return result;
    }

    private bool TryCaptureAuthority(
        in HostTickRequest request,
        bool committed,
        out AuthoritativeTickStateSnapshot? authority,
        out string detail)
    {
        authority = null;
        if (_composition.StatePort is null || !_composition.StatePort.IsAvailable)
        {
            detail = "The authoritative Tick state contributor capability is unavailable.";
            return false;
        }

        try
        {
            AuthoritativeTickStateSnapshot captured = _composition.StatePort.Capture(request.TickId);
            if (captured is null || !captured.IsWellFormed(request.TickId, request.SchemaEpoch, committed))
            {
                detail = "The authoritative Tick state contributor returned a missing or corrupt snapshot.";
                return false;
            }

            authority = captured.Snapshot();
            detail = string.Empty;
            return true;
        }
        catch (Exception)
        {
            detail = "The authoritative Tick state contributor failed while capturing its immutable snapshot.";
            return false;
        }
    }

    private bool TryPersistCommittedResult(
        in DurableTickReplayKey key,
        TickRunResult result,
        out string errorId,
        out string detail)
    {
        if (_composition.ReplayPort is null || !_composition.ReplayPort.IsAvailable)
        {
            errorId = "EvidenceMissing";
            detail = "The committed Tick replay capability is unavailable.";
            return false;
        }

        var record = new DurableTickReplayRecord(key, result);
        if (!record.IsWellFormedFor(in key))
        {
            errorId = "EvidenceDigestMismatch";
            detail = "The committed Tick replay record is malformed.";
            return false;
        }

        try
        {
            DurableTickReplayWriteStatus write = _composition.ReplayPort.Persist(record);
            if (write != DurableTickReplayWriteStatus.Durable)
            {
                errorId = write == DurableTickReplayWriteStatus.Corrupt
                    ? "EvidenceDigestMismatch"
                    : "EvidenceMissing";
                detail = "The committed Tick replay record did not receive a durable acknowledgement.";
                return false;
            }

            DurableTickReplayLookup readback = _composition.ReplayPort.Lookup(in key);
            if (readback.Status != DurableTickReplayLookupStatus.Found ||
                readback.Record is null ||
                !readback.Record.IsWellFormedFor(in key) ||
                !HasSameCommittedIdentity(result, readback.Record.Result))
            {
                errorId = readback.Status == DurableTickReplayLookupStatus.Missing
                    ? "EvidenceMissing"
                    : "EvidenceDigestMismatch";
                detail = "The durable committed Tick replay readback did not match the committed result.";
                return false;
            }

            errorId = string.Empty;
            detail = string.Empty;
            return true;
        }
        catch (Exception)
        {
            errorId = "EvidenceMissing";
            detail = "The durable committed Tick replay port failed.";
            return false;
        }
    }

    private FailureEvidenceReceipt PersistFailureEvidence(
        PhaseFailureRecord failure,
        ulong epoch,
        TickPhase? lastCompletedPhase,
        AuthoritativeTickStateSnapshot? authority)
    {
        if (_composition.FailurePort is null || !_composition.FailurePort.IsAvailable)
            return FailureEvidenceReceipt.Unavailable;
        if (authority is null)
            return new FailureEvidenceReceipt(DurableFailureEvidenceStatus.Corrupt, null, null);

        SimulationFailureBundle bundle;
        try
        {
            bundle = SimulationFailureBundle.Create(
                _options.SessionId,
                epoch,
                failure,
                lastCompletedPhase,
                authority);
        }
        catch (Exception)
        {
            return new FailureEvidenceReceipt(DurableFailureEvidenceStatus.Corrupt, null, null);
        }

        if (!bundle.IsWellFormed())
            return new FailureEvidenceReceipt(DurableFailureEvidenceStatus.Corrupt, bundle.EvidenceId, bundle);

        try
        {
            FailureBundleWriteStatus write = _composition.FailurePort.Persist(bundle);
            if (write == FailureBundleWriteStatus.Durable)
            {
                FailureBundleReadResult readback = _composition.FailurePort.Read(bundle.EvidenceId);
                if (readback.Status == FailureBundleReadStatus.Found &&
                    readback.Bundle is not null &&
                    string.Equals(readback.Bundle.EvidenceId, bundle.EvidenceId, StringComparison.Ordinal) &&
                    readback.Bundle.IsWellFormed())
                {
                    return new FailureEvidenceReceipt(DurableFailureEvidenceStatus.Durable, bundle.EvidenceId, bundle);
                }

                return new FailureEvidenceReceipt(DurableFailureEvidenceStatus.Corrupt, bundle.EvidenceId, bundle);
            }

            DurableFailureEvidenceStatus status = write switch
            {
                FailureBundleWriteStatus.Unavailable => DurableFailureEvidenceStatus.Unavailable,
                FailureBundleWriteStatus.Corrupt => DurableFailureEvidenceStatus.Corrupt,
                _ => DurableFailureEvidenceStatus.PersistenceFailed
            };
            return new FailureEvidenceReceipt(status, bundle.EvidenceId, bundle);
        }
        catch (Exception)
        {
            return new FailureEvidenceReceipt(DurableFailureEvidenceStatus.PersistenceFailed, bundle.EvidenceId, bundle);
        }
    }

    private static bool HasSameCommittedIdentity(TickRunResult expected, TickRunResult actual)
    {
        if (expected.TickId != actual.TickId ||
            expected.Status != actual.Status ||
            !expected.IsCommitted ||
            !actual.IsCommitted ||
            !string.Equals(expected.RequestHashHex, actual.RequestHashHex, StringComparison.Ordinal) ||
            !string.Equals(expected.StateHashHex, actual.StateHashHex, StringComparison.Ordinal) ||
            !Equals(expected.FirstFailure, actual.FirstFailure) ||
            !string.Equals(expected.FailureEvidenceId, actual.FailureEvidenceId, StringComparison.Ordinal) ||
            expected.PhaseRecords.Count != actual.PhaseRecords.Count ||
            expected.Outputs.Count != actual.Outputs.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.PhaseRecords.Count; index++)
            if (expected.PhaseRecords[index] != actual.PhaseRecords[index]) return false;
        for (var index = 0; index < expected.Outputs.Count; index++)
        {
            OpaqueOutputView left = expected.Outputs[index];
            OpaqueOutputView right = actual.Outputs[index];
            if (!string.Equals(left.Key, right.Key, StringComparison.Ordinal)) return false;
            byte[] leftPayload = left.Payload;
            byte[] rightPayload = right.Payload;
            if (leftPayload.Length != rightPayload.Length) return false;
            for (var offset = 0; offset < leftPayload.Length; offset++)
                if (leftPayload[offset] != rightPayload[offset]) return false;
        }

        return true;
    }

    private static TickPhase? FindLastCompletedPhase(IReadOnlyList<PhaseExecutionRecord> records)
    {
        for (var index = records.Count - 1; index >= 0; index--)
            if (records[index].Completed && records[index].Error is null) return records[index].Phase;
        return null;
    }

    private static bool TryGetBusinessRejection(Exception exception, TickPhase phase, out string errorId, out string detail)
    {
        if (PhaseContractTable.Default[phase].FailureClass != PhaseFailureClass.BusinessReject)
        {
            errorId = string.Empty;
            detail = string.Empty;
            return false;
        }

        if (exception is TickBusinessRejectException businessReject)
        {
            errorId = businessReject.GeneratedErrorId;
            detail = businessReject.Message;
            return true;
        }

        errorId = string.Empty;
        detail = string.Empty;
        return false;
    }

    private static string GetErrorId(Exception exception)
    {
        if (exception is TickBudgetExceededException) return "BudgetExceeded";
        if (exception is TickTimedOutException or TimeoutException) return "TimedOut";
        if (exception is OperationCanceledException) return "Cancelled";
        if (exception is TickExecutionException executionException)
            return executionException.GeneratedErrorId;

        return "PanicBoundary";
    }

    private void AdvanceAfterCommit(bool committed)
    {
        if (committed && _nextTickId != ulong.MaxValue) _nextTickId++;
    }

    private static void EnsureLifecycle(Func<bool>? lifecycleGuard)
    {
        if (lifecycleGuard is not null && !lifecycleGuard())
            throw new TickExecutionException("ContextClosing", "The simulation lifecycle changed during Tick execution.");
    }
}
