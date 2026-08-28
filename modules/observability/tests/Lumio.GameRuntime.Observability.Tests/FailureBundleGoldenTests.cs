using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Observability;
using Xunit;

namespace Lumio.GameRuntime.Observability.Tests;

public sealed class FailureBundleGoldenTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void SnapshotBundleUsesSnapshotIdWithoutNoSnapshotReason()
    {
        var context = Context("snapshot-1", null);
        var result = FailureBundleAssembler.Assemble(in context);

        Assert.True(result.IsAssembled);
        Assert.Equal("snapshot-1", result.Bundle!.SnapshotId);
        Assert.Null(result.Bundle.NoSnapshotReason);
        Assert.Null(FailureBundleAssembler.Verify(result.Bundle).GeneratedErrorId);
    }

    [Fact]
    public void PreSnapshotBundleUsesReasonAndBootstrapContext()
    {
        var context = Context(null, "PreFirstSnapshot");
        var result = FailureBundleAssembler.Assemble(in context);

        Assert.True(result.IsAssembled);
        Assert.Null(result.Bundle!.SnapshotId);
        Assert.Equal("PreFirstSnapshot", result.Bundle.NoSnapshotReason);
        Assert.Equal("Bootstrap", result.Bundle.BootstrapPhase);
        Assert.Equal("revision-1", result.Bundle.LastKnownRevision);
    }

    [Fact]
    public void SnapshotAndNoSnapshotReasonTogetherAreRejected()
    {
        var context = Context("snapshot-1", "BootstrapFault");

        var result = FailureBundleAssembler.Assemble(in context);

        Assert.Equal(FailureAssemblyStatus.Rejected, result.Status);
        Assert.Equal("ManifestMalformed", result.GeneratedErrorId);
    }

    private static FailureContextSnapshot Context(string? snapshotId, string? noSnapshotReason) => new(
        "failure-1",
        "QUEUE_FULL",
        "Simulation",
        DateTimeOffset.UnixEpoch,
        new CorrelationView("Session", "product", "release", "session", "world", "trace", "producer", 1UL),
        Hash,
        snapshotId,
        noSnapshotReason,
        noSnapshotReason is null ? null : "Bootstrap",
        noSnapshotReason is null ? null : "revision-1",
        noSnapshotReason is null ? null : Hash,
        new List<FailureArtifactView> { new("manifest", Hash, 12) },
        true,
        "lumio replay --failure failure-1");
}
