using System;
using System.Threading;

namespace Lumio.GameRuntime.Ecs;

internal sealed class OwnerThreadGuard
{
    private int _ownerThreadId;

    public bool IsBound => Volatile.Read(ref _ownerThreadId) != 0;
    public int OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    public StorageOperationResult BindOrValidate()
    {
        int current = Environment.CurrentManagedThreadId;
        int owner = Volatile.Read(ref _ownerThreadId);
        if (owner == 0)
        {
            Interlocked.CompareExchange(ref _ownerThreadId, current, 0);
            owner = Volatile.Read(ref _ownerThreadId);
        }
        return owner == current
            ? StorageOperationResult.Accepted()
            : StorageOperationResult.Rejected(EcsErrorCodes.OwnerThreadViolation);
    }

    public void Reset() => Volatile.Write(ref _ownerThreadId, 0);
}
