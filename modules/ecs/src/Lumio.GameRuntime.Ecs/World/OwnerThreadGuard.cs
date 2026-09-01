using System;
using System.Threading;

namespace Lumio.GameRuntime.Ecs;

internal interface IOwnerThreadTokenProvider
{
    int CurrentToken { get; }
}

internal sealed class ManagedOwnerThreadTokenProvider : IOwnerThreadTokenProvider
{
    public static ManagedOwnerThreadTokenProvider Instance { get; } = new();

    public int CurrentToken => Environment.CurrentManagedThreadId;
}

internal sealed class OwnerThreadGuard
{
    private readonly IOwnerThreadTokenProvider _tokens;
    private int _ownerToken;

    public OwnerThreadGuard(IOwnerThreadTokenProvider? tokens = null)
    {
        _tokens = tokens ?? ManagedOwnerThreadTokenProvider.Instance;
    }

    public bool IsBound => Volatile.Read(ref _ownerToken) != 0;
    public int OwnerThreadId => Volatile.Read(ref _ownerToken);

    public StorageOperationResult BindCurrentThread()
    {
        int current = _tokens.CurrentToken;
        if (current == 0)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

        Interlocked.CompareExchange(ref _ownerToken, current, 0);
        int owner = Volatile.Read(ref _ownerToken);
        return owner == current
            ? StorageOperationResult.Accepted()
            : StorageOperationResult.Rejected(EcsErrorCodes.OwnerThreadViolation);
    }

    public StorageOperationResult ValidateCurrentThread() =>
        Volatile.Read(ref _ownerToken) == _tokens.CurrentToken
            ? StorageOperationResult.Accepted()
            : StorageOperationResult.Rejected(EcsErrorCodes.OwnerThreadViolation);
}
