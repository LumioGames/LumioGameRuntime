using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Lumio.GameRuntime.Ecs;

[SuppressMessage("Design", "CA1512", Justification = "The production target also compiles for netstandard2.1, where the ThrowIf helper is unavailable.")]
internal sealed class EntitySlotTable
{
    private sealed class Slot
    {
        public uint Generation = 1;
        public bool Active;
        public EntityLifecycleState State;
        public EntityMode Mode;
        public EntityTypeHandle Type;
        public long CreationSequence;
    }

    private readonly List<Slot> _slots = new();
    private readonly Queue<uint> _free = new();
    private readonly int _capacity;
    private long _creationSequence;

    public EntitySlotTable(int capacity)
        : this(capacity, 1U)
    {
    }

    internal EntitySlotTable(int capacity, uint initialGeneration)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (initialGeneration == 0U) throw new ArgumentOutOfRangeException(nameof(initialGeneration));
        _capacity = capacity;
        _slots.Add(new Slot { Generation = initialGeneration == 1U ? 0U : initialGeneration, Active = false, State = EntityLifecycleState.Destroyed });
        _initialGeneration = initialGeneration;
    }

    private readonly uint _initialGeneration;

    public int ActiveCount { get; private set; }
    public int Capacity => _capacity;

    public bool TryAllocate(EntityTypeHandle type, EntityMode mode, out LocalEntityId id, out StorageOperationResult result)
    {
        id = default;
        if (type.IsDefault)
        {
            result = StorageOperationResult.Rejected(EcsErrorCodes.InvalidType);
            return false;
        }
        if (ActiveCount >= _capacity)
        {
            result = StorageOperationResult.Rejected(EcsErrorCodes.CapacityExceeded);
            return false;
        }

        uint index;
        Slot slot;
        if (_free.Count > 0)
        {
            if (!_free.TryPeek(out index))
            {
                result = StorageOperationResult.Fatal(EcsErrorCodes.InvalidState);
                return false;
            }
            slot = _slots[checked((int)index)];
            if (slot.Generation == uint.MaxValue)
            {
                result = StorageOperationResult.Fatal(EcsErrorCodes.InvalidState);
                return false;
            }
            _free.Dequeue();
            slot.Generation++;
        }
        else
        {
            if (_slots.Count > _capacity)
            {
                result = StorageOperationResult.Rejected(EcsErrorCodes.CapacityExceeded);
                return false;
            }
            index = checked((uint)_slots.Count);
            slot = new Slot { Generation = _initialGeneration };
            _slots.Add(slot);
        }

        slot.Active = true;
        slot.State = EntityLifecycleState.Reserved;
        slot.Mode = mode;
        slot.Type = type;
        slot.CreationSequence = ++_creationSequence;
        ActiveCount++;
        id = new LocalEntityId(index, slot.Generation);
        result = StorageOperationResult.Accepted();
        return true;
    }

    public bool TryResolve(LocalEntityId id, out EntityLifecycleState state, out EntityTypeHandle type, out long creationSequence)
    {
        state = EntityLifecycleState.Destroyed;
        type = default;
        creationSequence = 0;
        if (id.Index == 0U || id.Index >= _slots.Count) return false;
        Slot slot = _slots[checked((int)id.Index)];
        if (!slot.Active || slot.Generation != id.Generation) return false;
        state = slot.State;
        type = slot.Type;
        creationSequence = slot.CreationSequence;
        return true;
    }

    public bool TrySetState(LocalEntityId id, EntityLifecycleState state)
    {
        if (id.Index == 0U || id.Index >= _slots.Count) return false;
        Slot slot = _slots[checked((int)id.Index)];
        if (!slot.Active || slot.Generation != id.Generation) return false;
        slot.State = state;
        return true;
    }

    public bool TryRetire(LocalEntityId id)
    {
        if (id.Index == 0U || id.Index >= _slots.Count) return false;
        Slot slot = _slots[checked((int)id.Index)];
        if (!slot.Active || slot.Generation != id.Generation) return false;
        slot.Active = false;
        slot.State = EntityLifecycleState.Tombstoned;
        slot.Type = default;
        ActiveCount--;
        _free.Enqueue(id.Index);
        return true;
    }

    internal LocalEntityId Allocate()
    {
        if (!TryAllocate(
                new EntityTypeHandle(new WorldId(ulong.MaxValue), 1U),
                EntityMode.Local,
                out LocalEntityId id,
                out StorageOperationResult result))
            throw new InvalidOperationException(result.Error?.Code ?? EcsErrorCodes.InvalidState);
        return id;
    }

    internal bool Retire(LocalEntityId id) => TryRetire(id);

    internal bool TryResolve(LocalEntityId id, out EntityLifecycleState state) =>
        TryResolve(id, out state, out _, out _);

    public IEnumerable<(LocalEntityId Id, EntityTypeHandle Type, EntityLifecycleState State, long CreationSequence)> EnumerateActiveOrdered()
    {
        var values = new List<(LocalEntityId, EntityTypeHandle, EntityLifecycleState, long)>();
        for (uint index = 1; index < _slots.Count; index++)
        {
            Slot slot = _slots[checked((int)index)];
            if (slot.Active && !slot.Type.IsDefault)
                values.Add((new LocalEntityId(index, slot.Generation), slot.Type, slot.State, slot.CreationSequence));
        }
        values.Sort(static (a, b) => a.Item4.CompareTo(b.Item4));
        return values;
    }
}
