using System;
using System.Threading;

namespace Lumio.GameRuntime.Observability;

public interface ITracePort
{
    TraceScope Start(in TraceStartView start);
}

public readonly record struct TraceStartView(string Name, CorrelationView Correlation)
{
    public bool IsWellFormed => !string.IsNullOrWhiteSpace(Name) && Correlation.IsComplete;
}

public sealed class TraceScope : IDisposable
{
    private Action? _onDispose;
    private int _disposed;

    public TraceScope()
    {
    }

    internal TraceScope(Action onDispose)
    {
        _onDispose = onDispose;
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
