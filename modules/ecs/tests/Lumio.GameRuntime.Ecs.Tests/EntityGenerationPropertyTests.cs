using Lumio.GameRuntime.Ecs;
using Xunit;

namespace Lumio.GameRuntime.Ecs.Tests;

public sealed class EntityGenerationPropertyTests
{
    [Fact]
    public void RetiredSlotNeverResolvesPreviousGeneration()
    {
        var slots = new EntitySlotTable(4);
        LocalEntityId first = slots.Allocate();

        Assert.True(slots.Retire(first));
        LocalEntityId second = slots.Allocate();

        Assert.Equal(first.Index, second.Index);
        Assert.True(second.Generation > first.Generation);
        Assert.False(slots.TryResolve(first, out _));
        Assert.True(slots.TryResolve(second, out EntityLifecycleState state));
        Assert.Equal(EntityLifecycleState.Reserved, state);
    }

    [Fact]
    public void GenerationOverflowIsFatalAndDoesNotWrap()
    {
        var slots = new EntitySlotTable(1, uint.MaxValue);
        LocalEntityId first = slots.Allocate();
        Assert.Equal(uint.MaxValue, first.Generation);
        Assert.True(slots.Retire(first));

        bool allocated = slots.TryAllocate(
            new EntityTypeHandle(new WorldId(1), 1U),
            EntityMode.Local,
            out _,
            out StorageOperationResult result);

        Assert.False(allocated);
        Assert.Equal(StorageOperationStatus.Fatal, result.Status);
        Assert.Equal(EcsErrorCodes.InvalidState, result.Error?.Code);
        Assert.False(slots.TryResolve(first, out _));
    }

    [Fact]
    public void LongCreateDestroySequenceKeepsEveryRetiredIdStale()
    {
        var slots = new EntitySlotTable(2);
        LocalEntityId[] retired = new LocalEntityId[128];

        for (int i = 0; i < retired.Length; i++)
        {
            LocalEntityId current = slots.Allocate();
            retired[i] = current;
            Assert.True(slots.Retire(current));
            Assert.False(slots.TryResolve(current, out _));
        }

        for (int i = 0; i < retired.Length; i++)
            Assert.False(slots.TryResolve(retired[i], out _));
    }
}
