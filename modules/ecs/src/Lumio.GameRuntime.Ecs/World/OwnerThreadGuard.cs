using System;
using System.Threading;

namespace Lumio.GameRuntime.Ecs;

internal sealed class OwnerThreadGuard
{
    private int _ownerThreadId;

    public bool IsBound => Volatile.Read(ref _ownerThreadId) != 0;
    public int OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    public StorageOperationResult BindCurrentThread()
    {
        int current = Environment.CurrentManagedThreadId;
        Interlocked.CompareExchange(ref _ownerThreadId, current, 0);
        int owner = Volatile.Read(ref _ownerThreadId);
        return owner == current
            ? StorageOperationResult.Accepted()
            : StorageOperationResult.Rejected(EcsErrorCodes.OwnerThreadViolation);
    }

    public StorageOperationResult ValidateCurrentThread() =>
        Volatile.Read(ref _ownerThreadId) == Environment.CurrentManagedThreadId
            ? StorageOperationResult.Accepted()
            : StorageOperationResult.Rejected(EcsErrorCodes.OwnerThreadViolation);
}
