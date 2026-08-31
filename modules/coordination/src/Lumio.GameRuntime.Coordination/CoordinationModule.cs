using System;
using Lumio.GameRuntime.Command;
using Lumio.GameRuntime.GeneratedContracts;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lumio.GameRuntime.Coordination.Tests")]

namespace Lumio.GameRuntime.Coordination;

public sealed class CoordinationModule
{
    private readonly object _gate = new();
    private CoordinatorState _state = CoordinatorState.Created;
    private readonly CoordinationServices _services;

    private CoordinationModule(SessionRevisionVectorStore revisions, CrossWorldCoordinator transactions)
    {
        _services = new CoordinationServices(revisions, transactions, new CommandPreflightValidator());
    }

    public static CoordinationModule Create(SessionRevisionVectorView? initialRevision = null)
    {
        initialRevision ??= new SessionRevisionVectorView(0UL, 0UL, 0UL, new System.Collections.Generic.Dictionary<string, ulong>(), 0UL, 0UL, (ulong)GeneratedContractManifest.SchemaEpoch);
        var revisions = new SessionRevisionVectorStore(initialRevision);
        return new CoordinationModule(revisions, new CrossWorldCoordinator(revisions));
    }

    public CoordinatorState State
    {
        get { lock (_gate) return _state; }
    }

    public CoordinationServices Services => _services;

    public SessionRevisionVectorStore Revisions => _services.Revisions;

    public CoordinationLifecycleResult Configure()
    {
        lock (_gate)
        {
            if (_state != CoordinatorState.Created) return Fail("InvalidArgument");
            _state = CoordinatorState.Ready;
            return new CoordinationLifecycleResult(true, _state, null);
        }
    }

    public CoordinationLifecycleResult Start()
    {
        lock (_gate)
        {
            if (_state != CoordinatorState.Ready) return Fail("InvalidArgument");
            _state = CoordinatorState.Running;
            return new CoordinationLifecycleResult(true, _state, null);
        }
    }

    public CoordinationLifecycleResult BeginDrain()
    {
        lock (_gate)
        {
            if (_state != CoordinatorState.Running) return Fail("InvalidArgument");
            _state = CoordinatorState.Draining;
            _services.Transactions.StopAccepting();
            return new CoordinationLifecycleResult(true, _state, null);
        }
    }

    public CoordinationLifecycleResult Dispose()
    {
        lock (_gate)
        {
            if (_state is not (CoordinatorState.Draining or CoordinatorState.Ready)) return Fail("InvalidArgument");
            _state = CoordinatorState.Disposed;
            return new CoordinationLifecycleResult(true, _state, null);
        }
    }

    public CoordinationLifecycleResult Fault(string errorId)
    {
        if (string.IsNullOrWhiteSpace(errorId)) throw new ArgumentException("An error ID is required.", nameof(errorId));
        lock (_gate)
        {
            if (_state is CoordinatorState.Disposed or CoordinatorState.Faulted) return Fail("InvalidArgument");
            _state = CoordinatorState.Faulted;
            return new CoordinationLifecycleResult(true, _state, CoordinationFailure.Fatal(errorId, "Coordinator faulted."));
        }
    }

    private CoordinationLifecycleResult Fail(string errorId) =>
        new(false, _state, CoordinationFailure.Rejected(errorId, "Invalid coordinator lifecycle transition."));
}
