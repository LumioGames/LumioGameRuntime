using System;
using System.Threading;

namespace Lumio.GameRuntime.Command;

internal sealed class CommandOperationLease : IDisposable
{
    private CommandModule? _owner;
    private readonly bool _permitsApply;

    internal CommandOperationLease(CommandModule owner, bool permitsApply)
    {
        _owner = owner;
        _permitsApply = permitsApply;
    }

    internal bool PermitsApply(CommandModule owner) =>
        _permitsApply && ReferenceEquals(Volatile.Read(ref _owner), owner);

    public void Dispose()
    {
        CommandModule? owner = Interlocked.Exchange(ref _owner, null);
        owner?.CompleteOperation(_permitsApply);
    }
}
