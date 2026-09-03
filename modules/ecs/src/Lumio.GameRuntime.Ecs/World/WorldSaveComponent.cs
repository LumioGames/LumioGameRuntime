using System;

namespace Lumio.GameRuntime.Ecs;

/// <summary>
/// Engine-provided world-level component. Games declare a <c>World = true</c> entity type
/// with <c>[Has(typeof(WorldSaveComponent))]</c>. <see cref="Save"/> is a ServerRpc consumed
/// at commit; the host writes bytes through <see cref="ISnapshotSink"/>.
/// </summary>
[EcsComponent]
public sealed class WorldSaveComponent : Component
{
    /// <summary>Requests a snapshot of the current world. Server-only; no-ops on a client world.</summary>
    [ServerRpc]
    public void Save(string slot)
    {
        if (WorldInternal is null) return;
        WorldInternal.RequestSave(slot ?? string.Empty);
    }
}

/// <summary>Host-provided sink that receives snapshot bytes. Runtime does not write files.</summary>
public interface ISnapshotSink
{
    /// <summary>Receives one snapshot for <paramref name="slot"/>.</summary>
    void Write(string slot, ReadOnlyMemory<byte> snapshot);
}
