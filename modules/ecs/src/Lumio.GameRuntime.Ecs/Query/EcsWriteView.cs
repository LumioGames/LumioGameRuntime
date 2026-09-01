using System;

namespace Lumio.GameRuntime.Ecs;

internal readonly ref struct EcsWriteView
{
    private readonly IEcsQueryViewHost _host;
    private readonly ChangeSetBuilder _builder;
    private readonly QuerySpec _spec;
    private readonly WorldId _worldId;
    private readonly TickId _tickId;
    private readonly uint _epoch;

    internal EcsWriteView(IEcsQueryViewHost host, in QuerySpec spec, ChangeSetBuilder builder)
    {
        _host = host;
        _spec = spec;
        _builder = builder;
        _worldId = host.WorldId;
        _tickId = host.TickId;
        _epoch = host.Epoch;
    }

    public WorldId WorldId => _worldId;

    public TickId TickId => _tickId;

    public uint Epoch => _epoch;

    public StorageOperationResult Write(
        LocalEntityId entity,
        ComponentTypeId componentType,
        ComponentFieldId field,
        ReadOnlySpan<byte> canonicalValue)
    {
        StorageOperationResult boundary = ValidateLive();
        if (!boundary.IsSuccess) return boundary;
        if (!_host.IsKnownComponent(componentType))
            return StorageOperationResult.Rejected(EcsErrorCodes.UnknownComponent);
        if (!_host.IsKnownField(componentType, field))
            return StorageOperationResult.Rejected(EcsErrorCodes.UnknownField);
        if (!Contains(_spec.WriteSet.Span, field))
            return StorageOperationResult.Rejected(EcsErrorCodes.QueryBoundary);
        if (_builder.IsPublished)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidState);
        if (_builder.Count >= _host.Budget.MaxChangeEntries)
            return StorageOperationResult.Rejected(EcsErrorCodes.BudgetExceeded);

        var before = new byte[canonicalValue.Length];
        StorageOperationResult read = _host.ReadField(
            entity,
            componentType,
            field,
            before,
            out int readWritten);
        if (!read.IsSuccess) return read;
        if (readWritten != canonicalValue.Length)
            return StorageOperationResult.Rejected(EcsErrorCodes.InvalidArgument);

        StorageOperationResult written = _host.WriteExistingField(
            entity,
            componentType,
            field,
            canonicalValue);
        if (!written.IsSuccess) return written;

        StorageOperationResult appended = _builder.TryAppend(new ChangeEntry(
            entity,
            componentType,
            field,
            before,
            canonicalValue.ToArray()));
        return appended.IsSuccess
            ? written
            : StorageOperationResult.Fatal(EcsErrorCodes.PostWriteFailure);
    }

    private StorageOperationResult ValidateLive()
    {
        if (_host is null || _builder is null)
            return StorageOperationResult.Rejected(EcsErrorCodes.ViewExpired);
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
