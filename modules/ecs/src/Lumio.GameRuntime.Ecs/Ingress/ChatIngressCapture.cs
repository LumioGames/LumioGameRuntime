using System;
using System.Collections.Generic;

namespace Lumio.GameRuntime.Ecs.Ingress;

/// <summary>Bounded per-connection chat ingress status. Overflow is reject, never silent drop.</summary>
public enum ChatIngressEnqueueStatus
{
    /// <summary>Admitted to the bounded queue.</summary>
    Accepted = 0,

    /// <summary>The connection already holds <see cref="ChatIngressCapture.PerConnectionCapacity"/> items.</summary>
    QueueFull = 1,

    /// <summary>The queue is closed.</summary>
    Closed = 2,

    /// <summary>The item is malformed.</summary>
    Invalid = 3
}

/// <summary>One admitted chat.input waiting for IngressCapture.</summary>
public readonly record struct ChatIngressItem(string ConnectionId, string Text);

/// <summary>FIFO batch drained at IngressCapture for one tick.</summary>
public readonly record struct ChatIngressBatch(IReadOnlyList<ChatIngressItem> Items)
{
    /// <summary>Empty captured batch.</summary>
    public static ChatIngressBatch Empty { get; } = new(Array.Empty<ChatIngressItem>());
}

/// <summary>
/// C-1 bounded ingress: per-connection capacity = limits.ingressQueuePerConnection.
/// Capture is the only drain; enqueue never writes component state.
/// </summary>
public sealed class ChatIngressCapture
{
    /// <summary>C-1 <c>limits.ingressQueuePerConnection</c>.</summary>
    public const int PerConnectionCapacity = 64;

    private readonly object _gate = new();
    private readonly Queue<ChatIngressItem> _fifo = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private bool _closed;

    /// <summary>Admits one connection-scoped text item. Safe from a network thread.</summary>
    public ChatIngressEnqueueStatus TryEnqueue(string connectionId, string text)
    {
        if (string.IsNullOrEmpty(connectionId) || text is null)
            return ChatIngressEnqueueStatus.Invalid;

        lock (_gate)
        {
            if (_closed) return ChatIngressEnqueueStatus.Closed;
            _counts.TryGetValue(connectionId, out int queued);
            if (queued >= PerConnectionCapacity)
                return ChatIngressEnqueueStatus.QueueFull;

            _fifo.Enqueue(new ChatIngressItem(connectionId, text));
            _counts[connectionId] = queued + 1;
            return ChatIngressEnqueueStatus.Accepted;
        }
    }

    /// <summary>IngressCapture: drain every currently queued item in enqueue order.</summary>
    public ChatIngressBatch CaptureForTick()
    {
        lock (_gate)
        {
            if (_fifo.Count == 0) return ChatIngressBatch.Empty;
            var items = new ChatIngressItem[_fifo.Count];
            _fifo.CopyTo(items, 0);
            _fifo.Clear();
            _counts.Clear();
            return new ChatIngressBatch(items);
        }
    }

    /// <summary>Rejects further enqueue.</summary>
    public void Complete()
    {
        lock (_gate) _closed = true;
    }
}
