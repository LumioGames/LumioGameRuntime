using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Coordination;

public sealed class SnapshotCutLease : IDisposable
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<ISnapshotCutParticipant> _participants;
    private bool _disposed;

    internal SnapshotCutLease(SnapshotCutView view, IReadOnlyList<ISnapshotCutParticipant> participants)
    {
        View = view;
        _participants = participants;
    }

    public SnapshotCutView View { get; }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    public int ReleaseCount { get; private set; }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseCount++;
        }

        for (int index = _participants.Count - 1; index >= 0; index--)
        {
            try { _participants[index].ReleasePin(View); }
            catch (Exception) { /* release is best effort after an all-or-nothing failure */ }
        }
    }
}
