using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Ingress;
using Lumio.GameRuntime.Simulation.Phases;

namespace Lumio.GameRuntime.Simulation.Tick;

public readonly record struct OpaqueIngressView
{
    private readonly byte[] _payload;

    public OpaqueIngressView(string sessionId, ulong clientCommandSequence, ulong targetTickId, ulong generation, byte[] payload)
    {
        SessionId = sessionId;
        ClientCommandSequence = clientCommandSequence;
        TargetTickId = targetTickId;
        Generation = generation;
        _payload = payload is null ? Array.Empty<byte>() : (byte[])payload.Clone();
    }

    public string SessionId { get; }

    public ulong ClientCommandSequence { get; }

    public ulong TargetTickId { get; }

    public ulong Generation { get; }

    public byte[] Payload => (byte[])(_payload ?? Array.Empty<byte>()).Clone();

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
    private readonly List<TickPhase> _phaseTrace = new();
    private readonly List<PhaseExecutionRecord> _phaseRecords = new();
    private readonly List<OpaqueOutputView> _outputs = new();
    private readonly StateHashCoordinator _hashes = new();
    private readonly int _maxOutputItems;
    private readonly long _maxOutputBytes;
    private long _outputBytes;

    internal TickExecutionContext(HostTickRequest request, int maxOutputItems, long maxOutputBytes)
    {
        Request = request;
        _maxOutputItems = maxOutputItems;
        _maxOutputBytes = maxOutputBytes;
        Determinism = new DeterminismContext(request.Seed, request.TickId, request.SchemaEpoch);
        CanonicalInputs = request.CanonicalizeInputs();
        _hashes.Register("schemaEpoch", request.SchemaEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _hashes.Register("seed", request.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _hashes.Register("epoch", request.Epoch.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public HostTickRequest Request { get; }

    public DeterminismContext Determinism { get; }

    public CanonicalInputBatch CanonicalInputs { get; }

    public TickPhase CurrentPhase { get; private set; }

    public bool IsCommitted { get; private set; }

    public IReadOnlyList<TickPhase> PhaseTrace => new ReadOnlyCollection<TickPhase>(_phaseTrace);

    public IReadOnlyList<PhaseExecutionRecord> PhaseRecords => new ReadOnlyCollection<PhaseExecutionRecord>(_phaseRecords);

    public IReadOnlyList<OpaqueOutputView> Outputs => new ReadOnlyCollection<OpaqueOutputView>(_outputs);

    public StateHashCoordinator Hashes => _hashes;

    public void EnterPhase(TickPhase phase)
    {
        CurrentPhase = phase;
        _phaseTrace.Add(phase);
        _phaseRecords.Add(new PhaseExecutionRecord(phase, true, false, phase == TickPhase.GasAndEventFinalize, null));
    }

    internal void CompleteCurrentPhase()
    {
        if (_phaseRecords.Count == 0) throw new InvalidOperationException("No phase is active.");
        PhaseExecutionRecord current = _phaseRecords[_phaseRecords.Count - 1];
        if (current.Phase != CurrentPhase || current.Completed) throw new InvalidOperationException("The active phase cannot be completed twice.");
        _phaseRecords[_phaseRecords.Count - 1] = current with { Completed = true };
    }

    internal void RecordFailure(PhaseFailureRecord failure)
    {
        if (_phaseRecords.Count == 0) return;
        PhaseExecutionRecord current = _phaseRecords[_phaseRecords.Count - 1];
        _phaseRecords[_phaseRecords.Count - 1] = current with { Error = failure };
    }

    public void MarkCommitted()
    {
        if (CurrentPhase != TickPhase.GasAndEventFinalize) throw new TickExecutionException("Only GasAndEventFinalize may commit a tick.");
        if (IsCommitted) throw new TickExecutionException("A tick may only be committed once.");
        IsCommitted = true;
    }

    public bool TryEmitOutput(string key, byte[] payload)
    {
        if (!SimulationValidation.IsIdentifier(key) || payload is null) return false;
        if (_outputs.Count >= _maxOutputItems || payload.Length > _maxOutputBytes - _outputBytes) return false;
        _outputs.Add(new OpaqueOutputView(key, (byte[])payload.Clone()));
        _outputBytes += payload.Length;
        return true;
    }

    internal string ComputeStateHash()
    {
        _hashes.TryRegister("tick", Request.TickId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _hashes.TryRegister("inputs", CanonicalInputs.CanonicalHashHex);
        return _hashes.ComputeHashHex();
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

public sealed class TickExecutionException : Exception
{
    public TickExecutionException(string message) : base(message) { }
}
