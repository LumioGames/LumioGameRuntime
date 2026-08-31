using System.Collections.Generic;
using Xunit;

namespace Lumio.GameRuntime.Coordination.Tests;

public sealed class SnapshotCutConsistencyTests
{
    [Fact]
    public void SnapshotCutIsAllOrNothingAcrossParticipants()
    {
        SessionRevisionVectorView revisions = new(4UL, 2UL, 2UL, new Dictionary<string, ulong>(), 2UL, 1UL, 1UL);
        var ecs = new Participant("ecs", revisions, true);
        var gas = new Participant("gas", revisions, false);
        var voxel = new Participant("voxel", revisions, true);
        var coordinator = new SnapshotCutCoordinator(new SessionRevisionVectorStore(revisions), new[] { ecs, gas, voxel });

        SnapshotCutOpenResult result = coordinator.TryOpen(new SnapshotCutRequest("snapshot", 4UL, 1UL, true));

        Assert.False(result.Opened);
        Assert.Null(result.Lease);
        Assert.Equal(1, ecs.PinCalls);
        Assert.Equal(1, ecs.ReleaseCalls);
        Assert.Equal(0, voxel.PinCalls);
    }

    [Fact]
    public void SuccessfulLeaseReleasesEveryPinOnceInReverseOrder()
    {
        SessionRevisionVectorView revisions = new(4UL, 2UL, 2UL, new Dictionary<string, ulong>(), 2UL, 1UL, 1UL);
        var first = new Participant("a", revisions, true);
        var second = new Participant("b", revisions, true);
        SnapshotCutOpenResult result = new SnapshotCutCoordinator(new SessionRevisionVectorStore(revisions), new[] { first, second })
            .TryOpen(new SnapshotCutRequest("snapshot", 4UL, 1UL, true));
        Assert.True(result.Opened);
        result.Lease!.Dispose();
        result.Lease.Dispose();
        Assert.Equal(1, first.ReleaseCalls);
        Assert.Equal(1, second.ReleaseCalls);
    }

    private sealed class Participant : ISnapshotCutParticipant
    {
        private readonly SessionRevisionVectorView _revision;
        private readonly bool _pin;
        internal Participant(string name, SessionRevisionVectorView revision, bool pin)
        {
            Name = name;
            _revision = revision;
            _pin = pin;
        }
        public string Name { get; }
        public int PinCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public SessionRevisionVectorView ReadRevision() => _revision;
        public SnapshotPinResult TryPin(SnapshotCutView cut)
        {
            PinCalls++;
            return _pin ? SnapshotPinResult.Success() : SnapshotPinResult.FailureResult("CapacityExceeded", "pin failed");
        }
        public void ReleasePin(SnapshotCutView cut) => ReleaseCalls++;
    }
}
