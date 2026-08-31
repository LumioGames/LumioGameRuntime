using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Simulation.Determinism;
using Lumio.GameRuntime.Simulation.Phases;
using Lumio.GameRuntime.Simulation.Tick;

namespace Lumio.GameRuntime.Simulation.Tests;

internal static class TickTestExecutors
{
    internal static Dictionary<TickPhase, PhaseHandler> Complete(Action<TickPhase, TickExecutionContext>? execute = null)
    {
        var executors = new Dictionary<TickPhase, PhaseHandler>();
        foreach (TickPhase phase in PhaseGraph.Default.Phases)
        {
            TickPhase capturedPhase = phase;
            executors.Add(phase, context =>
            {
                execute?.Invoke(capturedPhase, context);
                return PhaseOutcome.Success();
            });
        }

        return executors;
    }

    internal static TickExecutorComposition CompleteComposition(
        Action<TickPhase, TickExecutionContext>? execute = null,
        TestTickInfrastructure? infrastructure = null)
    {
        Dictionary<TickPhase, PhaseHandler> handlers = Complete(execute);
        return Composition(handlers, TickExecutorCapability.All, infrastructure);
    }

    internal static TickExecutorComposition Composition(
        IReadOnlyDictionary<TickPhase, PhaseHandler> handlers,
        TickExecutorCapability capabilities = TickExecutorCapability.All,
        TestTickInfrastructure? infrastructure = null)
    {
        infrastructure ??= new TestTickInfrastructure();
        return TickExecutorComposition.ForHandlers(
            handlers,
            capabilities,
            infrastructure.StatePort,
            infrastructure.ReplayPort,
            infrastructure.FailurePort);
    }

    internal static AuthoritativeTickStateSnapshot State(
        ulong tickId,
        string gameReleaseId = "release-1",
        string worldId = "world-1",
        string configSnapshotId = "config-1",
        string? manifestHashHex = null,
        SimulationRevisionSnapshot? revisions = null,
        string? ecsHashHex = null,
        string? commandHashHex = null,
        string? coordinationHashHex = null,
        string? voxelHashHex = null,
        string? gasHashHex = null,
        string? replicationHashHex = null,
        IReadOnlyList<string>? preparedTokens = null,
        IReadOnlyList<string>? participantTokens = null,
        string? snapshotId = "snapshot-1",
        string? noSnapshotReason = null) =>
        new(
            gameReleaseId,
            worldId,
            configSnapshotId,
            manifestHashHex ?? Digest('0'),
            revisions ?? new SimulationRevisionSnapshot(
                tickId,
                tickId,
                tickId,
                new Dictionary<string, ulong>(StringComparer.Ordinal) { ["chunk:0:0:0"] = tickId },
                tickId,
                1,
                1),
            ecsHashHex ?? Digest('a'),
            commandHashHex ?? Digest('b'),
            coordinationHashHex ?? Digest('c'),
            voxelHashHex ?? Digest('d'),
            gasHashHex ?? Digest('e'),
            replicationHashHex ?? Digest('f'),
            preparedTokens ?? new[] { "prepared-1" },
            participantTokens ?? new[] { "participant-1" },
            snapshotId,
            noSnapshotReason);

    internal static string Digest(char value) => new(value, 64);
}

internal sealed class TestTickInfrastructure
{
    internal TestTickInfrastructure(
        TestAuthoritativeTickStatePort? statePort = null,
        TestDurableTickReplayPort? replayPort = null,
        TestSimulationFailureBundlePort? failurePort = null)
    {
        StatePort = statePort ?? new TestAuthoritativeTickStatePort();
        ReplayPort = replayPort ?? new TestDurableTickReplayPort();
        FailurePort = failurePort ?? new TestSimulationFailureBundlePort();
    }

    internal TestAuthoritativeTickStatePort StatePort { get; }

    internal TestDurableTickReplayPort ReplayPort { get; }

    internal TestSimulationFailureBundlePort FailurePort { get; }
}

internal sealed class TestAuthoritativeTickStatePort : IAuthoritativeTickStatePort
{
    private readonly Func<ulong, int, AuthoritativeTickStateSnapshot> _capture;
    private int _captureCount;

    internal TestAuthoritativeTickStatePort(
        Func<ulong, int, AuthoritativeTickStateSnapshot>? capture = null,
        bool isAvailable = true)
    {
        _capture = capture ?? ((tickId, _) => TickTestExecutors.State(tickId));
        IsAvailable = isAvailable;
    }

    public bool IsAvailable { get; }

    public AuthoritativeTickStateSnapshot Capture(ulong tickId) => _capture(tickId, ++_captureCount);
}

internal sealed class TestDurableTickReplayPort : IDurableTickReplayPort
{
    private readonly int _capacity;
    private readonly Dictionary<DurableTickReplayKey, DurableTickReplayRecord> _records = new();
    private readonly Queue<DurableTickReplayKey> _order = new();

    internal TestDurableTickReplayPort(int capacity = 1024)
    {
        _capacity = capacity;
    }

    internal DurableTickReplayWriteStatus WriteStatus { get; set; } = DurableTickReplayWriteStatus.Durable;

    internal DurableTickReplayLookupStatus? LookupOverride { get; set; }

    public bool IsAvailable { get; set; } = true;

    public int RetentionCapacity => _capacity;

    public DurableTickReplayLookup Lookup(in DurableTickReplayKey key)
    {
        if (LookupOverride is DurableTickReplayLookupStatus status)
            return new DurableTickReplayLookup(status, null);
        return _records.TryGetValue(key, out DurableTickReplayRecord? record)
            ? new DurableTickReplayLookup(DurableTickReplayLookupStatus.Found, record)
            : new DurableTickReplayLookup(DurableTickReplayLookupStatus.Missing, null);
    }

    public DurableTickReplayWriteStatus Persist(DurableTickReplayRecord record)
    {
        if (WriteStatus != DurableTickReplayWriteStatus.Durable) return WriteStatus;
        if (_records.TryAdd(record.Key, record))
        {
            _order.Enqueue(record.Key);
        }

        while (_order.Count > _capacity) _records.Remove(_order.Dequeue());
        return DurableTickReplayWriteStatus.Durable;
    }
}

internal sealed class TestSimulationFailureBundlePort : ISimulationFailureBundlePort
{
    private readonly Dictionary<string, SimulationFailureBundle> _bundles = new(StringComparer.Ordinal);

    internal FailureBundleWriteStatus WriteStatus { get; set; } = FailureBundleWriteStatus.Durable;

    public bool IsAvailable { get; set; } = true;

    public FailureBundleWriteStatus Persist(SimulationFailureBundle bundle)
    {
        if (WriteStatus != FailureBundleWriteStatus.Durable) return WriteStatus;
        _bundles.TryAdd(bundle.EvidenceId, bundle);
        return FailureBundleWriteStatus.Durable;
    }

    public FailureBundleReadResult Read(string evidenceId) =>
        _bundles.TryGetValue(evidenceId, out SimulationFailureBundle? bundle)
            ? new FailureBundleReadResult(FailureBundleReadStatus.Found, bundle)
            : new FailureBundleReadResult(FailureBundleReadStatus.Missing, null);
}
