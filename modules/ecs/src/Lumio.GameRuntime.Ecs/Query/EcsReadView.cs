using System;

namespace Lumio.GameRuntime.Ecs;

internal readonly ref struct EcsReadView
{
    private readonly IEcsQueryViewHost _host;
    private readonly QuerySpec _spec;
    private readonly WorldId _worldId;
    private readonly TickId _tickId;
    private readonly uint _epoch;

    internal EcsReadView(IEcsQueryViewHost host, in QuerySpec spec)
    {
        _host = host;
        _spec = spec;
        _worldId = host.WorldId;
        _tickId = host.TickId;
        _epoch = host.Epoch;
    }

    public WorldId WorldId => _worldId;

    public TickId TickId => _tickId;

    public uint Epoch => _epoch;

    public StorageOperationResult TryRead(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        StorageOperationResult boundary = ValidateLive();
        if (!boundary.IsSuccess) return boundary;
        if (!_host.IsKnownComponent(componentType))
            return StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent);
        if (!_host.IsKnownField(componentType, field))
            return StorageOperationResult.Rejected(EcsErrorCodes.UnknownField);
        if (!Contains(_spec.ReadSet.Span, field))
            return StorageOperationResult.Rejected(EcsErrorCodes.QueryBoundary);
        return _host.ReadField(entity, componentType, field, destination, out written);
    }

    private StorageOperationResult ValidateLive()
    {
        if (_host is null) return StorageOperationResult.Rejected(EcsErrorCodes.ViewExpired);
        if (_host.WorldId != _worldId) return StorageOperationResult.Rejected(EcsErrorCodes.CrossWorld);
        if (_host.TickId != _tickId || _host.Epoch != _epoch)
            return StorageOperationResult.Rejected(EcsErrorCodes.ViewExpired);
        return StorageOperationResult.Accepted();
    }

    private static bool Contains(ReadOnlySpan<ComponentFieldId> set, ComponentFieldId field)
    {
        for (int index = 0; index < set.Length; index++)
        {
            if (set[index] == field) return true;
        }

        return false;
    }
}
