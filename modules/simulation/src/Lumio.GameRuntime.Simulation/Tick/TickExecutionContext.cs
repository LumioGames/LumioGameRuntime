using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Ingress;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Simulation.Tick;

public readonly record struct OpaqueIngressView
{
    private readonly byte[] _payload;
    private readonly bool _payloadWasNull;

    public OpaqueIngressView(string sessionId, ulong clientCommandSequence, ulong targetTickId, ulong generation, byte[] payload)
    {
        SessionId = sessionId;
        ClientCommandSequence = clientCommandSequence;
        TargetTickId = targetTickId;
        Generation = generation;
        _payloadWasNull = payload is null;
        _payload = payload is null ? Array.Empty<byte>() : (byte[])payload.Clone();
    }

    public string SessionId { get; }

    public ulong ClientCommandSequence { get; }

    public ulong TargetTickId { get; }

    public ulong Generation { get; }

    public byte[] Payload => (byte[])(_payload ?? Array.Empty<byte>()).Clone();

    internal bool PayloadWasNull => _payloadWasNull;

    public OpaqueIngress ToIngress() => new(SessionId, ClientCommandSequence, TargetTickId, Generation, Payload);

    public void Deconstruct(out string sessionId, out ulong clientCommandSequence, out ulong targetTickId, out ulong generation, out byte[] payload)
    {
        sessionId = SessionId;
        clientCommandSequence = ClientCommandSequence;
        targetTickId = TargetTickId;
        generation = Generation;
        payload = Payload;
    }
}

public sealed class TickExecutionContext
{
    private readonly object _gate = new();
    private readonly List<TickPhase> _phaseTrace = new();
    private readonly List<PhaseExecutionRecord> _phaseRecords = new();
    private readonly List<OpaqueOutputView> _stagedOutputs = new();
    private readonly List<OpaqueOutputView> _publishedOutputs = new();
    private readonly StateHashCoordinator _hashes = new();
    private readonly int _maxOutputItems;
    private readonly long _maxOutputBytes;
    private readonly int _ownerThreadId;
    private readonly long _executionStartedTimestamp;
    private readonly AuthoritativeTickStateSnapshot _initialAuthority;
    private long _outputBytes;
    private ulong _consumedWorkUnits;
    private ulong _consumedCommands;
    private bool _open = true;
    private bool _phaseActive;
    private bool _isCommitted;

    internal TickExecutionContext(
        HostTickRequest request,
        int maxOutputItems,
        long maxOutputBytes,
        int ownerThreadId,
        string sessionId,
        AuthoritativeTickStateSnapshot initialAuthority)
    {
        if (!SimulationValidation.IsIdentifier(sessionId)) throw new ArgumentException("A valid session ID is required.", nameof(sessionId));
        if (initialAuthority is null || !initialAuthority.IsWellFormed(request.TickId, request.SchemaEpoch, false))
            throw new ArgumentException("A valid authoritative Tick state snapshot is required.", nameof(initialAuthority));
        Request = request;
        _maxOutputItems = maxOutputItems;
        _maxOutputBytes = maxOutputBytes;
        _ownerThreadId = ownerThreadId;
        _executionStartedTimestamp = Stopwatch.GetTimestamp();
        _initialAuthority = initialAuthority.Snapshot();
        ExecutionControl = request.ExecutionControl;
        Determinism = new DeterminismContext(
            _initialAuthority.GameReleaseId,
            sessionId,
            _initialAuthority.WorldId,
            request.TickId,
            request.Seed,
            request.SchemaEpoch,
            _initialAuthority.ConfigSnapshotId);
        CanonicalInputs = request.CanonicalizeInputs();
        _hashes.Register("schemaEpoch", request.SchemaEpoch.ToString(CultureInfo.InvariantCulture));
        _hashes.Register("seed", request.Seed.ToString(CultureInfo.InvariantCulture));
        _hashes.Register("epoch", request.Epoch.Value.ToString(CultureInfo.InvariantCulture));
        _hashes.Register("identity.session", sessionId);
        _hashes.Register("identity.world", _initialAuthority.WorldId);
        _hashes.Register("identity.release", _initialAuthority.GameReleaseId);
        _hashes.Register("identity.manifest", _initialAuthority.ManifestHashHex);
        _hashes.Register("identity.config", _initialAuthority.ConfigSnapshotId);
    }

    public HostTickRequest Request { get; }

    public DeterminismContext Determinism { get; }

    public CanonicalInputBatch CanonicalInputs { get; }

    public TickExecutionControl ExecutionControl { get; }

    public TickPhase CurrentPhase { get; private set; }

    public bool IsCommitted
    {
        get { lock (_gate) return _isCommitted; }
    }

    public IReadOnlyList<TickPhase> PhaseTrace
    {
        get
        {
            lock (_gate) return new ReadOnlyCollection<TickPhase>(new List<TickPhase>(_phaseTrace));
        }
    }

    public IReadOnlyList<PhaseExecutionRecord> PhaseRecords
    {
        get
        {
            lock (_gate) return new ReadOnlyCollection<PhaseExecutionRecord>(new List<PhaseExecutionRecord>(_phaseRecords));
        }
    }

    public IReadOnlyList<OpaqueOutputView> Outputs
    {
        get
        {
            lock (_gate)
            {
                var copy = new List<OpaqueOutputView>(_publishedOutputs.Count);
                foreach (OpaqueOutputView output in _publishedOutputs) copy.Add(output.Snapshot());
                return new ReadOnlyCollection<OpaqueOutputView>(copy);
            }
        }
    }

    internal StateHashCoordinator Hashes => _hashes;

    public void Checkpoint()
    {
        lock (_gate)
        {
            EnsureOwner();
            CheckpointCore();
        }
    }

    public void ConsumeWork(ulong units)
    {
        lock (_gate)
        {
            EnsureOwner();
            CheckpointCore();
            if (units > ExecutionControl.MaxWorkUnits - _consumedWorkUnits)
                throw new TickBudgetExceededException("The Tick logical work budget was exceeded.");
            _consumedWorkUnits += units;
        }
    }

    public void ConsumeCommands(ulong commands)
    {
        lock (_gate)
        {
            EnsureOwner();
            CheckpointCore();
            if (commands > ExecutionControl.MaxCommands - _consumedCommands)
                throw new TickBudgetExceededException("The Tick command budget was exceeded.");
            _consumedCommands += commands;
        }
    }

    public void CheckProcessorBudget(
        ProcessorDescriptorBudget budget,
        ulong elapsedMicros,
        ulong emittedCommands)
    {
        if (budget is null) throw new TickBudgetExceededException("A processor budget is required.");
        if (budget.MaxMicros == 0 || budget.MaxCommands == 0)
            throw new TickBudgetExceededException("A processor budget cannot be zero.");
        Checkpoint();
        if (elapsedMicros > budget.MaxMicros || emittedCommands > budget.MaxCommands)
            throw new TickBudgetExceededException("The processor budget was exceeded.");
        ConsumeCommands(emittedCommands);
    }

    internal void EnterPhase(TickPhase phase)
    {
        lock (_gate)
        {
            EnsureOwner();
            if (!_open || _phaseActive) throw new TickExecutionException("A phase cannot be entered while the context is closed or already active.");
            CurrentPhase = phase;
            _phaseTrace.Add(phase);
            _phaseRecords.Add(new PhaseExecutionRecord(phase, true, false, phase == TickPhase.GasAndEventFinalize, null));
            _phaseActive = true;
        }
    }

    internal void CompleteCurrentPhase()
    {
        lock (_gate)
        {
            EnsureOwner();
            if (!_open || !_phaseActive || _phaseRecords.Count == 0) throw new InvalidOperationException("No phase is active.");
            PhaseExecutionRecord current = _phaseRecords[_phaseRecords.Count - 1];
            if (current.Phase != CurrentPhase || current.Completed) throw new InvalidOperationException("The active phase cannot be completed twice.");
            _phaseRecords[_phaseRecords.Count - 1] = current with { Completed = true };
            _phaseActive = false;
        }
    }

    internal void RecordFailure(PhaseFailureRecord failure)
    {
        lock (_gate)
        {
            if (_phaseRecords.Count == 0) return;
            PhaseExecutionRecord current = _phaseRecords[_phaseRecords.Count - 1];
            _phaseRecords[_phaseRecords.Count - 1] = current with { Error = failure };
            _phaseActive = false;
        }
    }

    internal void MarkCommitted()
    {
        lock (_gate)
        {
            EnsureOwner();
            if (!_open || CurrentPhase != TickPhase.GasAndEventFinalize)
                throw new TickExecutionException("Only GasAndEventFinalize may commit a tick.");
            if (_phaseRecords.Count == 0)
                throw new TickExecutionException("GasAndEventFinalize must complete before a tick may commit.");
            PhaseExecutionRecord current = _phaseRecords[_phaseRecords.Count - 1];
            if (current.Phase != TickPhase.GasAndEventFinalize || !current.Entered || !current.AuthoritativeCommitPoint || !current.Completed)
                throw new TickExecutionException("GasAndEventFinalize must complete before a tick may commit.");
            if (_isCommitted) throw new TickExecutionException("A tick may only be committed once.");
            foreach (OpaqueOutputView output in _stagedOutputs) _publishedOutputs.Add(output.Snapshot());
            _stagedOutputs.Clear();
            _isCommitted = true;
        }
    }

    public bool TryEmitOutput(string key, byte[] payload)
    {
        lock (_gate)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId || !_open || !_phaseActive) return false;
            if (!SimulationValidation.IsIdentifier(key) || payload is null) return false;
            if ((long)_stagedOutputs.Count + _publishedOutputs.Count >= _maxOutputItems || payload.Length > _maxOutputBytes - _outputBytes) return false;
            OpaqueOutputView output = new(key, (byte[])payload.Clone());
            if (_isCommitted) _publishedOutputs.Add(output);
            else _stagedOutputs.Add(output);
            _outputBytes += payload.Length;
            return true;
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _open = false;
            _phaseActive = false;
            _stagedOutputs.Clear();
            _hashes.Seal();
        }
    }

    private void EnsureOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId) throw new TickExecutionException("Tick execution belongs to the owner thread.");
    }

    private void CheckpointCore()
    {
        if (!ExecutionControl.CooperativeChecksRequired)
            throw new TickExecutionException("CapabilityMissing", "The executor composition does not support required cooperative checkpoints.");
        ExecutionControl.CancellationToken.ThrowIfCancellationRequested();
        if (Request.TickId > ExecutionControl.DeadlineTickId)
            throw new TickTimedOutException("The logical Tick deadline was exceeded.");
        if (ExecutionControl.Timeout <= TimeSpan.Zero)
            throw new TickTimedOutException("The Tick timeout must be positive.");
        if (ExecutionControl.MaxWorkUnits == 0 || ExecutionControl.MaxCommands == 0)
            throw new TickBudgetExceededException("Tick work and command limits must be positive.");

        long elapsedTicks = Stopwatch.GetTimestamp() - _executionStartedTimestamp;
        double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
        if (elapsedSeconds > ExecutionControl.Timeout.TotalSeconds)
            throw new TickTimedOutException("The Tick elapsed-time budget was exceeded.");
    }

    internal string ComputeStateHash(AuthoritativeTickStateSnapshot committedAuthority)
    {
        lock (_gate)
        {
            EnsureOwner();
            if (!_isCommitted || !_open)
                throw new TickExecutionException("InternalInvariant", "Only a committed open Tick can produce an authoritative state hash.");
            if (committedAuthority is null ||
                !committedAuthority.IsWellFormed(Request.TickId, Request.SchemaEpoch, true) ||
                !_initialAuthority.HasSameIdentity(committedAuthority))
            {
                throw new TickExecutionException("InternalInvariant", "The authoritative state contributor set is missing, corrupt, or changed identity during the Tick.");
            }

            if (_phaseRecords.Count != PhaseGraph.Default.Phases.Count)
                throw new TickExecutionException("InternalInvariant", "The state hash requires all thirteen phase identities.");
            for (var index = 0; index < _phaseRecords.Count; index++)
            {
                PhaseExecutionRecord record = _phaseRecords[index];
                if (record.Phase != PhaseGraph.Default.Phases[index] || !record.Entered || !record.Completed || record.Error is not null)
                    throw new TickExecutionException("InternalInvariant", "The state hash requires a complete canonical phase trace.");
                _hashes.Register(
                    $"phase.{index:D2}",
                    string.Concat(
                        record.Phase.ToString(),
                        "|",
                        record.Entered ? "1" : "0",
                        "|",
                        record.Completed ? "1" : "0",
                        "|",
                        record.AuthoritativeCommitPoint ? "1" : "0"));
            }

            _hashes.Register("tick", Request.TickId.ToString(CultureInfo.InvariantCulture));
            _hashes.Register("inputs", CanonicalInputs.CanonicalHashHex);
            _hashes.Register("revision.vector", committedAuthority.Revisions.CanonicalValue);
            _hashes.Register("state.ecs", committedAuthority.EcsHashHex);
            _hashes.Register("state.command", committedAuthority.CommandHashHex);
            _hashes.Register("state.coordination", committedAuthority.CoordinationHashHex);
            _hashes.Register("state.voxel", committedAuthority.VoxelHashHex);
            _hashes.Register("state.gas", committedAuthority.GasHashHex);
            _hashes.Register("state.replication", committedAuthority.ReplicationHashHex);
            RegisterTokens("tokens.prepared", committedAuthority.PreparedTokens);
            RegisterTokens("tokens.participant", committedAuthority.ParticipantTokens);
            _hashes.Register("outputs.count", _publishedOutputs.Count.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < _publishedOutputs.Count; index++)
            {
                OpaqueOutputView output = _publishedOutputs[index];
                byte[] payload = output.Payload;
                _hashes.Register($"output.{index:D6}.key", output.Key);
                _hashes.Register($"output.{index:D6}.length", payload.Length.ToString(CultureInfo.InvariantCulture));
                _hashes.Register($"output.{index:D6}.payload", SimulationHash.Sha256Hex(payload));
            }

            StateHashSummary summary = _hashes.CaptureAuthoritativeSummary();
            if (!summary.IsComplete)
                throw new TickExecutionException("InternalInvariant", "The authoritative state hash contributor set is incomplete.");
            return summary.HashHex;
        }
    }

    private void RegisterTokens(string prefix, IReadOnlyList<string> tokens)
    {
        _hashes.Register(prefix + ".count", tokens.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < tokens.Count; index++)
            _hashes.Register($"{prefix}.{index:D6}", tokens[index]);
    }
}

public readonly record struct OpaqueOutputView
{
    private readonly byte[] _payload;

    public OpaqueOutputView(string key, byte[] payload)
    {
        Key = key;
        _payload = payload is null ? Array.Empty<byte>() : (byte[])payload.Clone();
    }

    public string Key { get; }

    public byte[] Payload => (byte[])(_payload ?? Array.Empty<byte>()).Clone();

    public OpaqueOutputView Snapshot() => new(Key, Payload);

    public void Deconstruct(out string key, out byte[] payload)
    {
        key = Key;
        payload = Payload;
    }
}

public class TickExecutionException : Exception
{
    public TickExecutionException(string message) : this("InternalInvariant", message) { }

    public TickExecutionException(string generatedErrorId, string message) : base(message)
    {
        if (!SimulationValidation.IsStableErrorId(generatedErrorId)) throw new ArgumentException("A generated stable error ID is required.", nameof(generatedErrorId));
        GeneratedErrorId = generatedErrorId;
    }

    public string GeneratedErrorId { get; }
}

public sealed class TickBudgetExceededException : TickExecutionException
{
    public TickBudgetExceededException(string message = "The tick budget was exceeded.") : base("BudgetExceeded", message) { }
}

public sealed class TickTimedOutException : TickExecutionException
{
    public TickTimedOutException(string message = "The tick timed out.") : base("TimedOut", message) { }
}

public sealed class TickBusinessRejectException : Exception
{
    public TickBusinessRejectException(string message, string generatedErrorId = "InvalidArgument") : base(message)
    {
        if (!SimulationValidation.IsStableErrorId(generatedErrorId)) throw new ArgumentException("A generated stable error ID is required.", nameof(generatedErrorId));
        GeneratedErrorId = generatedErrorId;
    }

    public string GeneratedErrorId { get; }
}
