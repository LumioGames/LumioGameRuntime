using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Command;

/// <summary>Applies a prepared delta at the ECS barrier and fails closed on infrastructure errors.</summary>
public sealed class EcsCommandCommitExecutor
{
    private readonly object _gate = new();
    // Apply is a commit barrier operation. Serializing the whole operation
    // closes the check-then-apply race between concurrent replays.
    private readonly object _applyGate = new();
    private readonly IEcsCommandCommitPort _port;
    private readonly Action<string>? _faultSink;
    private readonly Dictionary<string, CommandApplyReceipt> _receipts = new(StringComparer.Ordinal);
    private bool _faulted;

    public EcsCommandCommitExecutor(IEcsCommandCommitPort? port = null, Action<string>? faultSink = null)
    {
        _port = port ?? new FailClosedEcsCommandCommitPort();
        _faultSink = faultSink;
    }

    public bool IsFaulted
    {
        get { lock (_gate) return _faulted; }
    }

    internal CommandApplyReceipt Apply(
        PreparedGameDelta prepared,
        CommandOperationLease operation,
        CommandModule owner)
    {
        if (operation is null || owner is null || !operation.PermitsApply(owner))
            return FaultReceipt(prepared?.TickId ?? 0UL, prepared?.CanonicalDigest ?? Array.Empty<byte>(), "InvalidArgument");
        lock (_applyGate)
        {
            return ApplyCore(prepared);
        }
    }

    private CommandApplyReceipt ApplyCore(PreparedGameDelta prepared)
    {
        if (prepared is null)
        {
            return FaultReceipt(0UL, Array.Empty<byte>(), "InvalidArgument");
        }

        string key = string.Concat(prepared.TickId.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", prepared.CanonicalDigestHex);
        lock (_gate)
        {
            if (_receipts.TryGetValue(key, out CommandApplyReceipt existing))
            {
                return existing with { Status = existing.Status == CommandApplyStatus.Applied ? CommandApplyStatus.AlreadyApplied : existing.Status };
            }

            if (_faulted)
            {
                return FaultReceipt(prepared.TickId, prepared.CanonicalDigest.ToArray(), "PanicBoundary");
            }
        }

        if (!prepared.VerifyForApply())
        {
            return FaultPrepared(prepared, key, "InternalInvariant");
        }

        int applied = 0;
        try
        {
            foreach (Command command in prepared.Commands)
            {
                string? resolved = null;
                if (command.Kind != CommandKind.Create && command.DeferredTarget is DeferredEntityToken token)
                {
                    if (!prepared.ResolutionPlan.TryResolve(token, prepared.TickId, out resolved))
                    {
                        return FaultPrepared(prepared, key, "InternalInvariant");
                    }
                }

                EcsCommandPortResult result = _port.Apply(command, resolved);
                if (result.Status is EcsCommandPortStatus.Rejected or
                    EcsCommandPortStatus.InfrastructureFault or
                    EcsCommandPortStatus.Indeterminate or
                    EcsCommandPortStatus.Faulted)
                {
                    return FaultPrepared(prepared, key, result.GeneratedErrorId ?? "PanicBoundary", result.Status);
                }

                if (command.Kind == CommandKind.Create && command.DeferredTarget is DeferredEntityToken createToken &&
                    result.ResolvedEntityId is string resolvedEntityId)
                {
                    if (!prepared.ResolutionPlan.TrySet(createToken, resolvedEntityId, out _))
                        return FaultPrepared(prepared, key, "InternalInvariant");
                }
                else if (command.Kind == CommandKind.Create && command.DeferredTarget is DeferredEntityToken)
                {
                    // A create must publish its mapping before any following
                    // command can legally consume the deferred target.
                    return FaultPrepared(prepared, key, "InternalInvariant");
                }

                applied++;
            }

            prepared.Reservations.Commit();
            prepared.Batch.MarkApplied();
            var receipt = new CommandApplyReceipt(
                CommandApplyStatus.Applied,
                prepared.TickId,
                prepared.CanonicalDigest.ToArray(),
                applied,
                null,
                new CommandChangeSet(prepared.CanonicalDigest, prepared.Commands));
            lock (_gate) _receipts[key] = receipt;
            return receipt;
        }
        catch (Exception)
        {
            return FaultPrepared(prepared, key, "PanicBoundary");
        }
    }

    private CommandApplyReceipt FaultReceipt(ulong tickId, ReadOnlyMemory<byte> digest, string errorId) =>
        FaultReceipt(string.Concat(tickId.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", CommandHashing.ToHex(digest.ToArray())), tickId, digest, errorId);

    private CommandApplyReceipt FaultReceipt(string key, ulong tickId, ReadOnlyMemory<byte> digest, string errorId)
    {
        var receipt = new CommandApplyReceipt(CommandApplyStatus.InfrastructureFault, tickId, digest.ToArray(), 0, errorId);
        lock (_gate)
        {
            _faulted = true;
            if (!_receipts.ContainsKey(key)) _receipts[key] = receipt;
        }
        try { _faultSink?.Invoke(errorId); }
        catch (Exception) { }
        return receipt;
    }

    private CommandApplyReceipt FaultPrepared(
        PreparedGameDelta prepared,
        string key,
        string errorId,
        EcsCommandPortStatus? portStatus = null)
    {
        prepared.Reservations.Release();
        prepared.Batch.MarkFaulted();
        CommandApplyReceipt receipt = FaultReceipt(key, prepared.TickId, prepared.CanonicalDigest.ToArray(), errorId);
        if (portStatus is EcsCommandPortStatus.Indeterminate or EcsCommandPortStatus.Faulted)
        {
            receipt = receipt with
            {
                Status = portStatus == EcsCommandPortStatus.Indeterminate
                    ? CommandApplyStatus.Indeterminate
                    : CommandApplyStatus.Faulted
            };
            lock (_gate) _receipts[key] = receipt;
        }

        return receipt;
    }

    private sealed class FailClosedEcsCommandCommitPort : IEcsCommandCommitPort
    {
        public EcsCommandPortResult Apply(Command command, string? resolvedEntityId) =>
            EcsCommandPortResult.Fault("CapabilityMissing");
    }
}

internal sealed class EcsWorldCommandCommitPort : IEcsCommandCommitPort
{
    private readonly EcsWorld _world;
    private readonly IEcsChangeSetAppend _changeSet = new DiscardingChangeSetAppend();

    public EcsWorldCommandCommitPort(EcsWorld world) =>
        _world = world ?? throw new ArgumentNullException(nameof(world));

    public EcsCommandPortResult Apply(Command command, string? resolvedEntityId)
    {
        try
        {
            return command.Kind switch
            {
                CommandKind.Create => ApplyCreate(command),
                CommandKind.Write => ApplyWrite(command, resolvedEntityId),
                CommandKind.Destroy => ApplyDestroy(command, resolvedEntityId),
                _ => EcsCommandPortResult.Rejected("InvalidArgument")
            };
        }
        catch (Exception)
        {
            _world.FaultFromParticipant("PanicBoundary");
            return EcsCommandPortResult.Fault("PanicBoundary");
        }
    }

    private EcsCommandPortResult ApplyCreate(Command command)
    {
        if (string.IsNullOrWhiteSpace(command.ComponentType) ||
            !_world.TryGetRegisteredEntityType(command.ComponentType, out EntityTypeHandle handle, out _))
        {
            return EcsCommandPortResult.Rejected("InvalidArgument");
        }

        EntityCreateResult created = _world.CreateEntityForCommit(
            _world.Context,
            new EntityCreateRequest(handle));
        return created.Created
            ? EcsCommandPortResult.Applied(created.Entity.ToString())
            : MapStorage(created.Result);
    }

    private EcsCommandPortResult ApplyWrite(Command command, string? resolvedEntityId)
    {
        if (!TryResolveEntity(command, resolvedEntityId, out LocalEntityId entity) ||
            string.IsNullOrWhiteSpace(command.ComponentType) ||
            string.IsNullOrWhiteSpace(command.FieldName) ||
            !_world.TryGetRegisteredField(
                command.ComponentType,
                command.FieldName,
                out ComponentTypeDefinition component,
                out ComponentFieldDefinition field))
        {
            return EcsCommandPortResult.Rejected("InvalidArgument");
        }

        var write = new EcsFieldWrite(entity, component.Id, field.Id, command.Payload);
        return MapStorage(_world.WriteExistingField(in write, _changeSet));
    }

    private EcsCommandPortResult ApplyDestroy(Command command, string? resolvedEntityId)
    {
        if (!TryResolveEntity(command, resolvedEntityId, out LocalEntityId entity) ||
            !_world.TryGetCommitTarget(entity, out EcsWorld.WorldEntityTarget target))
        {
            return EcsCommandPortResult.Rejected("InvalidHandle");
        }

        EntityDestroyResult destroyed = _world.DestroyEntityForCommit(target);
        return destroyed.Destroyed ? EcsCommandPortResult.Applied() : MapStorage(destroyed.Result);
    }

    private static bool TryResolveEntity(Command command, string? resolvedEntityId, out LocalEntityId entity) =>
        LocalEntityId.TryParse(resolvedEntityId ?? command.TargetEntityId, out entity);

    private static EcsCommandPortResult MapStorage(StorageOperationResult result)
    {
        string code = result.Error?.Code ?? "PanicBoundary";
        return result.Status switch
        {
            StorageOperationStatus.Accepted or StorageOperationStatus.AlreadyApplied => EcsCommandPortResult.Applied(),
            StorageOperationStatus.Rejected => EcsCommandPortResult.Rejected(code),
            StorageOperationStatus.Indeterminate => EcsCommandPortResult.Indeterminate(code),
            StorageOperationStatus.Fatal => EcsCommandPortResult.Fault(code),
            _ => EcsCommandPortResult.Fault(code)
        };
    }

    private sealed class DiscardingChangeSetAppend : IEcsChangeSetAppend
    {
        public StorageOperationResult TryAppend(in ChangeEntry entry) => StorageOperationResult.Accepted();
    }
}
