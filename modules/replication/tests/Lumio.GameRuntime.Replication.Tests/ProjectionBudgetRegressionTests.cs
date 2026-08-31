using System;
using System.Collections.Generic;
using System.Text;
using Lumio.GameRuntime.Replication.History;
using Lumio.GameRuntime.Replication.Mapping;
using Lumio.GameRuntime.Replication.Projection;
using Xunit;

namespace Lumio.GameRuntime.Replication.Tests;

public sealed class ProjectionBudgetRegressionTests
{
    [Fact]
    public void DeltaRangeMustEndAtTheAuthoritativeReplicationRevision()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        MappingSetView mappings = CreateMappings();
        var revision = new RevisionVector(2, 2, 2, 2, 2, 2, 1);

        DeltaProjectionResult result = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 999, 1000, 1,
            revision, mappings, Array.Empty<TombstoneView>());

        Assert.Equal(ProjectionStatus.Rejected, result.Status);
        Assert.Equal("RevisionConflict", result.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void DeltaRangeMaySpanHistoryButMustEndAtTheAuthorityEndpoint()
    {
        var projection = new ReplicationProjection(new ReplicationBudget(8, 4096, 8, 4096));
        MappingSetView mappings = CreateMappings();
        var revision = new RevisionVector(2, 2, 2, 2, 2, 2, 1);

        DeltaProjectionResult valid = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 0, 2, 1,
            revision, mappings, Array.Empty<TombstoneView>());
        DeltaProjectionResult stale = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 1, 2,
            revision, mappings, Array.Empty<TombstoneView>());
        DeltaProjectionResult future = projection.BuildDelta(
            "session-1", "product", "release-1", "snap-1", 1, 3, 3,
            revision, mappings, Array.Empty<TombstoneView>());

        Assert.True(valid.Succeeded);
        Assert.Equal(ProjectionStatus.Rejected, stale.Status);
        Assert.Equal("RevisionConflict", stale.Failure?.GeneratedErrorId);
        Assert.Equal(ProjectionStatus.Rejected, future.Status);
        Assert.Equal("RevisionConflict", future.Failure?.GeneratedErrorId);
    }

    [Fact]
    public void ProjectionBatchBytesMatchTheSerializedBlockEquation()
    {
        byte[] payload = { 1, 2, 3 };
        ProjectionBlock block = ProjectionBlock.Create("mapping-a", payload);
        long expected = SerializedBatchBytes(block);
        var batch = new ProjectionBatch(1, expected);

        Assert.Equal(ProjectionBatchStatus.Accepted, batch.Add(block));
        Assert.Equal(expected, batch.Bytes);
    }

    [Fact]
    public void LongMappingIdCannotFitABytesAndHashOnlyBudget()
    {
        byte[] payload = { 1, 2, 3 };
        ProjectionBlock block = ProjectionBlock.Create(new string('m', 128), payload);
        long payloadAndHashOnly = payload.Length + Encoding.UTF8.GetByteCount(block.PayloadHash);
        var batch = new ProjectionBatch(1, payloadAndHashOnly);

        Assert.Equal(ProjectionBatchStatus.QueueFull, batch.Add(block));
        Assert.Equal(0, batch.Bytes);
    }

    [Fact]
    public void ProjectionBatchPreservesOwnershipAtTheExactBoundary()
    {
        byte[] payload = { 1, 2, 3 };
        ProjectionBlock block = ProjectionBlock.Create("mapping-a", payload);
        long expected = SerializedBatchBytes(block);
        var batch = new ProjectionBatch(1, expected);

        Assert.Equal(ProjectionBatchStatus.Accepted, batch.Add(block));
        payload[0] = 99;
        IReadOnlyList<ProjectionBlock> view = batch.Blocks;
        view[0].Payload[0] = 88;

        Assert.Equal(expected, batch.Bytes);
        Assert.Equal(1, batch.Blocks[0].Payload[0]);
    }

    private static MappingSetView CreateMappings()
    {
        var registry = new MappingRegistry();
        Assert.True(registry.Register(MappingDescriptor.Create("mapping-a", "Health", "current")).Succeeded);
        return registry.View;
    }

    private static long SerializedBatchBytes(ProjectionBlock block)
    {
        long mappingBytes = Encoding.UTF8.GetByteCount(block.MappingId);
        long payloadHashBytes = Encoding.UTF8.GetByteCount(block.PayloadHash);
        return checked(
            sizeof(uint) +
            sizeof(uint) + mappingBytes +
            sizeof(uint) + block.Payload.Length +
            sizeof(uint) + payloadHashBytes);
    }
}
