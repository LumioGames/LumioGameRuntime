using System;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class QueryViewBoundaryTests
{
    [Fact]
    public void QuerySpecCopiesDedupesAndSortsGeneratedIdsOnConstruction()
    {
        var required = new[] { new ComponentTypeId(30), new ComponentTypeId(10), new ComponentTypeId(30), new ComponentTypeId(20) };
        var excluded = new[] { new ComponentTypeId(50), new ComponentTypeId(40), new ComponentTypeId(40) };
        var readSet = new[] { new ComponentFieldId(9), new ComponentFieldId(2), new ComponentFieldId(2) };
        var writeSet = new[] { new ComponentFieldId(4), new ComponentFieldId(4), new ComponentFieldId(1) };

        var spec = new QuerySpec(required, excluded, readSet, writeSet);
        required[0] = new ComponentTypeId(99);
        excluded[0] = new ComponentTypeId(99);
        readSet[0] = new ComponentFieldId(99);
        writeSet[0] = new ComponentFieldId(99);

        Assert.Equal(new[] { new ComponentTypeId(10), new ComponentTypeId(20), new ComponentTypeId(30) }, spec.Required.ToArray());
        Assert.Equal(new[] { new ComponentTypeId(40), new ComponentTypeId(50) }, spec.Excluded.ToArray());
        Assert.Equal(new[] { new ComponentFieldId(2), new ComponentFieldId(9) }, spec.ReadSet.ToArray());
        Assert.Equal(new[] { new ComponentFieldId(1), new ComponentFieldId(4) }, spec.WriteSet.ToArray());
        Assert.True(spec.IsWellFormed);
    }

    [Fact]
    public void QuerySpecRejectsRequiredExcludedConflictAtConstruction()
    {
        EcsFailure failure = Assert.Throws<EcsFailure>(() => _ = new QuerySpec(
            new[] { new ComponentTypeId(10), new ComponentTypeId(20) },
            new[] { new ComponentTypeId(20) },
            Array.Empty<ComponentFieldId>(),
            Array.Empty<ComponentFieldId>()));

        Assert.Equal(EcsFailureClass.Rejected, failure.Class);
        Assert.Equal(EcsErrorCodes.QueryBoundary, failure.Code);
    }

    [Fact]
    public void UnknownComponentIsRejectedBeforeWrite()
    {
        using EcsQuerySession session = SeededSession(out LocalEntityId entity, out QuerySpec spec);
        var builder = new ChangeSetBuilder(session.WorldId, session.TickId, session.Budget.MaxChangeEntries);
        EcsWriteView view = session.OpenWrite(in spec, builder);
        byte[] original = ReadLive(session, entity, PositionId, PositionField);
        byte[] destination = original.ToArray();

        StorageOperationResult result = view.Write(
            entity,
            new ComponentTypeId(99),
            PositionField,
            new byte[] { 9, 9, 9, 9 });

        AssertRejected(result, EcsErrorCodes.UnknownComponent);
        Assert.Equal(original, ReadLive(session, entity, PositionId, PositionField));
        Assert.Equal(0, builder.Count);
        Assert.Equal(original, destination);
    }

    [Fact]
    public void UnknownFieldIsRejectedBeforeWrite()
    {
        using EcsQuerySession session = SeededSession(out LocalEntityId entity, out QuerySpec spec);
        var builder = new ChangeSetBuilder(session.WorldId, session.TickId, session.Budget.MaxChangeEntries);
        EcsWriteView view = session.OpenWrite(in spec, builder);
        byte[] original = ReadLive(session, entity, PositionId, PositionField);

        StorageOperationResult result = view.Write(
            entity,
            PositionId,
            new ComponentFieldId(99),
            new byte[] { 9, 9, 9, 9 });

        AssertRejected(result, EcsErrorCodes.UnknownField);
        Assert.Equal(original, ReadLive(session, entity, PositionId, PositionField));
        Assert.Equal(0, builder.Count);
    }

    [Fact]
    public void ReadOutsideReadSetIsRejectedBeforePublishingBytes()
    {
        using EcsQuerySession session = SeededSession(out LocalEntityId entity, out _);
        var spec = new QuerySpec(
            new[] { PositionId },
            Array.Empty<ComponentTypeId>(),
            new[] { PositionField },
            Array.Empty<ComponentFieldId>());
        EcsReadView view = session.OpenRead(in spec);
        var destination = new byte[] { 7, 7, 7, 7 };

        StorageOperationResult result = view.TryRead(
            entity,
            VelocityId,
            VelocityField,
            destination,
            out int written);

        AssertRejected(result, EcsErrorCodes.QueryBoundary);
        Assert.Equal(0, written);
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, destination);
    }

    [Fact]
    public void WriteOutsideWriteSetIsRejectedBeforeMutation()
    {
        using EcsQuerySession session = SeededSession(out LocalEntityId entity, out QuerySpec spec);
        var builder = new ChangeSetBuilder(session.WorldId, session.TickId, session.Budget.MaxChangeEntries);
        EcsWriteView view = session.OpenWrite(in spec, builder);
        byte[] originalVelocity = ReadLive(session, entity, VelocityId, VelocityField);

        StorageOperationResult result = view.Write(
            entity,
            VelocityId,
            VelocityField,
            new byte[] { 9, 8, 7, 6 });

        AssertRejected(result, EcsErrorCodes.QueryBoundary);
        Assert.Equal(originalVelocity, ReadLive(session, entity, VelocityId, VelocityField));
        Assert.Equal(0, builder.Count);
    }

    [Fact]
    public void StaleEpochIsRejectedBeforeWrite()
    {
        using EcsQuerySession session = SeededSession(out LocalEntityId entity, out QuerySpec spec);
        var builder = new ChangeSetBuilder(session.WorldId, session.TickId, session.Budget.MaxChangeEntries);
        EcsWriteView view = session.OpenWrite(in spec, builder);
        byte[] original = ReadLive(session, entity, PositionId, PositionField);
        session.AdvanceEpoch();

        StorageOperationResult result = view.Write(
            entity,
            PositionId,
            PositionField,
            new byte[] { 9, 9, 9, 9 });

        AssertRejected(result, EcsErrorCodes.ViewExpired);
        Assert.Equal(original, ReadLive(session, entity, PositionId, PositionField));
        Assert.Equal(0, builder.Count);
    }

    [Fact]
    public void CrossTickViewIsRejectedBeforeWrite()
    {
        using EcsQuerySession session = SeededSession(out LocalEntityId entity, out QuerySpec spec);
        var builder = new ChangeSetBuilder(session.WorldId, session.TickId, session.Budget.MaxChangeEntries);
        EcsWriteView view = session.OpenWrite(in spec, builder);
        EcsReadView read = session.OpenRead(in spec);
        byte[] original = ReadLive(session, entity, PositionId, PositionField);
        var destination = new byte[] { 3, 3, 3, 3 };
        session.AdvanceTick();

        StorageOperationResult write = view.Write(
            entity,
            PositionId,
            PositionField,
            new byte[] { 9, 9, 9, 9 });
        StorageOperationResult readResult = read.TryRead(
            entity,
            PositionId,
            PositionField,
            destination,
            out int written);

        AssertRejected(write, EcsErrorCodes.ViewExpired);
        AssertRejected(readResult, EcsErrorCodes.ViewExpired);
        Assert.Equal(0, written);
        Assert.Equal(new byte[] { 3, 3, 3, 3 }, destination);
        Assert.Equal(original, ReadLive(session, entity, PositionId, PositionField));
        Assert.Equal(0, builder.Count);
    }

    [Fact]
    public void QueryBudgetExceededIsRejectedWithoutPartialBatch()
    {
        using EcsQuerySession session = SeededSession(out _, out QuerySpec spec);
        var destination = new[] { new LocalEntityId(9, 9), new LocalEntityId(8, 8) };

        StorageOperationResult result = session.TryQuery(
            in spec,
            new QueryBudget(1, 64),
            out QueryBatch? batch);

        AssertRejected(result, EcsErrorCodes.BudgetExceeded);
        Assert.Null(batch);
        Assert.Equal(new[] { new LocalEntityId(9, 9), new LocalEntityId(8, 8) }, destination);
    }

    [Fact]
    public void QueryBatchEnumeratesCanonicalEntityKeysNotCreationOrder()
    {
        using EcsQuerySession session = SeededSession(out _, out QuerySpec spec);

        StorageOperationResult result = session.TryQuery(
            in spec,
            QueryBudget.Default,
            out QueryBatch? batch);

        Assert.Equal(StorageOperationStatus.Accepted, result.Status);
        Assert.NotNull(batch);
        Assert.Equal(session.WorldId, batch!.WorldId);
        Assert.Equal(session.TickId, batch.TickId);
        Assert.Equal(session.Epoch, batch.Epoch);
        Assert.Equal(new[] { new LocalEntityId(1, 1), new LocalEntityId(2, 1) }, batch.Entities.ToArray());
    }

    [Fact]
    public void SuccessfulWriteCallsWriteExistingFieldAndAppendsChangeSetImmediately()
    {
        using EcsQuerySession session = SeededSession(out LocalEntityId entity, out QuerySpec spec);
        var builder = new ChangeSetBuilder(session.WorldId, session.TickId, session.Budget.MaxChangeEntries);
        EcsWriteView view = session.OpenWrite(in spec, builder);
        byte[] before = ReadLive(session, entity, PositionId, PositionField);
        byte[] after = { 4, 3, 2, 1 };

        StorageOperationResult result = view.Write(entity, PositionId, PositionField, after);
        ChangeSet published = builder.Build();

        Assert.Equal(StorageOperationStatus.Accepted, result.Status);
        Assert.Equal(after, ReadLive(session, entity, PositionId, PositionField));
        Assert.Equal(1, published.Entries.Length);
        Assert.Equal(entity, published.Entries.Span[0].Entity);
        Assert.Equal(PositionId, published.Entries.Span[0].ComponentType);
        Assert.Equal(PositionField, published.Entries.Span[0].Field);
        Assert.Equal(before, published.Entries.Span[0].CanonicalBefore.ToArray());
        Assert.Equal(after, published.Entries.Span[0].CanonicalAfter.ToArray());
        Assert.True(builder.IsPublished);
        Assert.Equal(StorageOperationStatus.Rejected, builder.TryAppend(published.Entries.Span[0]).Status);
    }

    [Fact]
    public void WriteViewHasNoPublicStructuralMutationEntry()
    {
        Assert.True(typeof(EcsReadView).IsByRefLike);
        Assert.True(typeof(EcsWriteView).IsByRefLike);
        string[] forbidden = { "Create", "Destroy", "Add", "Remove", "AddComponent", "RemoveComponent" };
        MethodInfo[] writeMethods = typeof(EcsWriteView).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo[] readMethods = typeof(EcsReadView).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (string name in forbidden)
        {
            Assert.DoesNotContain(writeMethods, method => string.Equals(method.Name, name, StringComparison.Ordinal));
            Assert.DoesNotContain(readMethods, method => string.Equals(method.Name, name, StringComparison.Ordinal));
        }
    }

    private static readonly ComponentTypeId PositionId = new(10);
    private static readonly ComponentTypeId VelocityId = new(11);
    private static readonly ComponentFieldId PositionField = new(1);
    private static readonly ComponentFieldId VelocityField = new(2);

    private static EcsQuerySession SeededSession(out LocalEntityId first, out QuerySpec spec)
    {
        var storage = new ReferenceWorldStorageAdapter(new WorldId(810), 8, 4096);
        var session = new EcsQuerySession(
            new WorldId(810),
            storage,
            new EcsBudget(8, 8, 8, 4096),
            new TickId(7));
        Assert.Equal(StorageOperationStatus.Accepted, session.Register(new ComponentTypeDefinition(
            PositionId,
            "Position",
            new[] { new ComponentFieldDefinition(PositionField, 4) })).Status);
        Assert.Equal(StorageOperationStatus.Accepted, session.Register(new ComponentTypeDefinition(
            VelocityId,
            "Velocity",
            new[] { new ComponentFieldDefinition(VelocityField, 4) })).Status);

        first = new LocalEntityId(2, 1);
        var second = new LocalEntityId(1, 1);
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(first, PositionAndVelocity(1, 2, 3, 4)).Status);
        Assert.Equal(StorageOperationStatus.Accepted, storage.Create(second, PositionAndVelocity(5, 6, 7, 8)).Status);
        spec = new QuerySpec(
            new[] { PositionId },
            Array.Empty<ComponentTypeId>(),
            new[] { PositionField },
            new[] { PositionField });
        return session;
    }

    private static ComponentInitBatch PositionAndVelocity(byte a, byte b, byte c, byte d) => new(new[]
    {
        new ComponentInitValue(PositionId, PositionField, new[] { a, b, c, d }),
        new ComponentInitValue(VelocityId, VelocityField, new byte[] { 11, 12, 13, 14 })
    });

    private static byte[] ReadLive(
        EcsQuerySession session,
        LocalEntityId entity,
        ComponentTypeId component,
        ComponentFieldId field)
    {
        var destination = new byte[4];
        StorageOperationResult result = session.ReadField(entity, component, field, destination, out int written);
        Assert.Equal(StorageOperationStatus.Accepted, result.Status);
        Assert.Equal(4, written);
        return destination;
    }

    private static void AssertRejected(StorageOperationResult result, string code)
    {
        Assert.Equal(StorageOperationStatus.Rejected, result.Status);
        Assert.Equal(code, result.Error?.Code);
    }
}
