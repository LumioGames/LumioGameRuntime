using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Replication;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Lumio.GameRuntime.Replication.Validation;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ProjectionValidationTests
{
    [Fact]
    public void FullSnapshotAndDeltaCarryRequiredGeneratedMetadata()
    {
        var mapping = new MappingRegistry();
        mapping.Register(MappingDescriptor.Create("mapping-a", "Health", "current"));
        var revision = new RevisionVector(4, 8, 2, 1, 3, 1, 1);
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        var full = projection.BuildFullSnapshot("session-1", "product", "release-1", "snap-4", revision, mapping.View);
        Assert.True(full.Succeeded);
        Assert.Equal("snap-4", full.Snapshot!.SnapshotId);
        Assert.Equal(mapping.View.MappingSetHash, full.Snapshot.MappingSetHash);

        var delta = projection.BuildDelta("session-1", "product", "release-1", "snap-4", 4, 5, 7, revision, mapping.View, Array.Empty<TombstoneView>());
        Assert.True(delta.Succeeded);
        Assert.Equal(4UL, delta.Delta!.FromRevision);
        Assert.Equal(5UL, delta.Delta.ToRevision);
    }

    [Fact]
    public void SequenceGapAndStaleGenerationAreRejectedWithResyncSignals()
    {
        var validator = new ReplicationEnvelopeValidator();
        Assert.Equal(ReplicationValidationCode.Gap, validator.ValidateSequence(3, 1).Code);
        Assert.Equal(ReplicationValidationCode.StaleConnectionGeneration, validator.ValidateGeneration(2, 1).Code);
        Assert.True(validator.ValidateSequence(2, 1).RequiresResync);
    }
}
