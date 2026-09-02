using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs;

internal sealed partial class EntitySlotTable
{
    internal bool TryAdopt(
        LocalEntityId id,
        EntityTypeHandle type,
        EntityMode mode,
        out StorageOperationResult result)
    {
        if (id.Index == 0U || id.Generation == 0U)
        {
            result = StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);
            return false;
        }

        if (type.IsDefault)
        {
            result = StorageOperationResult.Rejected(EcsErrorCodes.InvalidType);
            return false;
        }

        if (id.Index > (uint)_capacity)
        {
            result = StorageOperationResult.Rejected(EcsErrorCodes.CapacityExceeded);
            return false;
        }

        while (_slots.Count <= id.Index)
        {
            if (_slots.Count > _capacity)
            {
                result = StorageOperationResult.Rejected(EcsErrorCodes.CapacityExceeded);
                return false;
            }

            _slots.Add(new Slot
            {
                Generation = 0,
                Active = false,
                State = EntityLifecycleState.Destroyed
            });
        }

        Slot slot = _slots[checked((int)id.Index)];
        if (slot.Active)
        {
            result = slot.Generation == id.Generation
                ? StorageOperationResult.Rejected(EcsErrorCodes.DuplicateRegistration)
                : StorageOperationResult.Rejected(EcsErrorCodes.StaleEntity);
            return false;
        }

        if (ActiveCount >= _capacity)
        {
            result = StorageOperationResult.Rejected(EcsErrorCodes.CapacityExceeded);
            return false;
        }

        RemoveFromFree(id.Index);
        slot.Generation = id.Generation;
        slot.Active = true;
        slot.State = EntityLifecycleState.Reserved;
        slot.Mode = mode;
        slot.Type = type;
        slot.CreationSequence = ++_creationSequence;
        ActiveCount++;
        result = StorageOperationResult.Accepted();
        return true;
    }

    private void RemoveFromFree(uint index)
    {
        int count = _free.Count;
        if (count == 0) return;
        var kept = new Queue<uint>(count);
        for (int i = 0; i < count; i++)
        {
            uint value = _free.Dequeue();
            if (value != index) kept.Enqueue(value);
        }

        while (kept.Count > 0) _free.Enqueue(kept.Dequeue());
    }
}
