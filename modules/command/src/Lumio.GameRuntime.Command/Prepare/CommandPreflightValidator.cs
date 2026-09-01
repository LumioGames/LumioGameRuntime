using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Ecs;
using Lumio.GameRuntime.GeneratedContracts;

namespace Lumio.GameRuntime.Command;

public interface ICommandValidationContext
{
    bool IsKnownComponent(string componentType);

    bool IsKnownField(string componentType, string fieldName);

    bool EntityExists(string entityId);

    bool CanWrite(string processorId, Command command);
}

public sealed class CommandPreflightOptions
{
    public int SchemaEpoch { get; init; } = GeneratedContractManifest.SchemaEpoch;

    public ulong MaxCommands { get; init; } = ulong.MaxValue;

    public ulong MaxBytes { get; init; } = ulong.MaxValue;

    public ulong AvailableEntitySlots { get; init; } = ulong.MaxValue;

    public ulong AvailableChangeEntries { get; init; } = ulong.MaxValue;

    public ICommandValidationContext? Context { get; init; }

    internal static CommandPreflightOptions FromWorld(EcsWorld world)
    {
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(world);
#else
        if (world is null) throw new ArgumentNullException(nameof(world));
#endif
        int availableSlots = world.Budget.MaxEntities - world.ActiveEntityCount;
        return new CommandPreflightOptions
        {
            SchemaEpoch = GeneratedContractManifest.SchemaEpoch,
            AvailableEntitySlots = availableSlots <= 0 ? 0UL : (ulong)availableSlots,
            AvailableChangeEntries = (ulong)world.Budget.MaxChangeEntries,
            Context = new EcsWorldCommandValidationContext(world)
        };
    }
}

public readonly record struct CommandPrepareContext(
    ulong TickId,
    string WorldId,
    int SchemaEpoch,
    CommandBufferBudget Budget,
    ulong AvailableEntitySlots,
    ulong AvailableChangeEntries,
    ICommandValidationContext? ValidationContext = null);

public readonly record struct CommandPrepareResult(
    CommandPreflightStatus Status,
    PreparedGameDelta? Delta,
    CommandFailure? Failure)
{
    public bool IsPrepared => Status == CommandPreflightStatus.Prepared && Delta is not null;
}

public sealed class CommandPreflightValidator
{
    private readonly CommandPreflightOptions _options;

    public CommandPreflightValidator(CommandPreflightOptions? options = null) =>
        _options = options ?? new CommandPreflightOptions();

    public CommandPreflightResult TryPrepare(MergedCommandBatch batch)
    {
        if (batch is null)
        {
            return Failure(CommandPreflightStatus.Rejected, "InvalidArgument", "Batch is required.");
        }

        if (batch.State != CommandBufferState.Merged)
        {
            return Failure(CommandPreflightStatus.Rejected, "InvalidArgument", "Batch must be merged exactly once.");
        }

        if ((ulong)batch.Commands.Count > _options.MaxCommands)
        {
            return Failure(CommandPreflightStatus.Rejected, "BudgetExceeded", "Command budget exceeded.");
        }

        var reservations = new CommandReservationSet(0UL, 0UL, 0UL);
        ulong generation = 0UL;
        bool haveGeneration = false;
        bool commonGeneration = true;
        var bufferScopes = new Dictionary<string, BufferScope>(StringComparer.Ordinal);
        foreach (SealedCommandBuffer buffer in batch.Buffers)
        {
            if (buffer is null)
            {
                return RejectAndRelease(reservations, "InvalidArgument", "Merged batch contains a null buffer.");
            }

            string scopeKey = ScopeKey(buffer.Phase, buffer.ProcessorId);
            if (bufferScopes.ContainsKey(scopeKey))
            {
                return RejectAndRelease(reservations, "InvalidArgument", "Merged batch contains a duplicate processor scope.");
            }

            bufferScopes.Add(scopeKey, new BufferScope(buffer.Phase, buffer.ProcessorId, buffer.BufferGeneration, buffer.MayEmitStructuralCommands));
            if (!haveGeneration)
            {
                generation = buffer.BufferGeneration;
                haveGeneration = true;
            }
            else if (generation != buffer.BufferGeneration)
            {
                commonGeneration = false;
            }
        }

        if (!commonGeneration) generation = 0UL;
        var resolutionPlan = new DeferredEntityMap(batch.TickId, batch.WorldId, generation);
        var destroyed = new HashSet<string>(StringComparer.Ordinal);
        var created = new Dictionary<DeferredEntityToken, BufferScope>();
        var writtenFields = new Dictionary<string, Command>(StringComparer.Ordinal);
        ulong entitySlots = 0UL;
        ulong bytes = 0UL;
        ulong changes = 0UL;
        ulong tokenReferences = 0UL;
        ICommandValidationContext context = _options.Context ?? FailClosedCommandValidationContext.Instance;

        try
        {
            foreach (Command command in batch.Commands)
            {
                if (command is null || command.SortKey.ProcessorId is null || command.SortKey.LocalSequence == 0UL)
                {
                    return RejectAndRelease(reservations, "InvalidArgument", "Malformed command.");
                }

                string scopeKey = ScopeKey(command.SortKey.Phase, command.SortKey.ProcessorId);
                if (!bufferScopes.TryGetValue(scopeKey, out BufferScope scope))
                {
                    return RejectAndRelease(reservations, "WrongContext", "Command does not belong to a merged buffer.");
                }

                if (!CommandValidation.IsIdentifier(command.SortKey.ProcessorId) ||
                    (int)command.SortKey.Phase < (int)Lumio.Gen.ContractTypes.ProcessorDescriptorPhase.IngressCapture ||
                    (int)command.SortKey.Phase > (int)Lumio.Gen.ContractTypes.ProcessorDescriptorPhase.EgressPublish)
                {
                    return RejectAndRelease(reservations, "ManifestMalformed", "Invalid processor identifier.");
                }

                if (command.Kind is not (CommandKind.Create or CommandKind.Write or CommandKind.Destroy))
                {
                    return RejectAndRelease(reservations, "ManifestMalformed", "Unknown command kind.");
                }

                if (command.CommandId is string commandId && !CommandValidation.IsIdentifier(commandId))
                {
                    return RejectAndRelease(reservations, "ManifestMalformed", "Invalid command identifier.");
                }

                if (command.TargetEntityId is string targetEntityId && !CommandValidation.IsIdentifier(targetEntityId))
                {
                    return RejectAndRelease(reservations, "ManifestMalformed", "Invalid target entity identifier.");
                }

                if (command.TargetEntityId is not null && command.DeferredTarget is not null)
                {
                    return RejectAndRelease(reservations, "InvalidArgument", "A command cannot contain both direct and deferred targets.");
                }

                if (command.IsStructural && (!scope.MayEmitStructuralCommands || !CommandValidation.IsStructuralPhase(scope.Phase)))
                {
                    return RejectAndRelease(reservations, "MessagePermissionDenied", "Structural commands are not permitted in this processor scope.");
                }

                if (command.EstimatedBytes == 0UL)
                {
                    return RejectAndRelease(reservations, "BudgetExceeded", "Command byte estimate must be positive.");
                }

                if (command.DeferredTarget is DeferredEntityToken token)
                {
                    if (token.TickId != batch.TickId || !string.Equals(token.WorldId, batch.WorldId, StringComparison.Ordinal) ||
                        !CommandValidation.IsIdentifier(token.ProcessorId) ||
                        (token.BufferGeneration != 0UL && scope.BufferGeneration != 0UL &&
                            token.BufferGeneration != scope.BufferGeneration))
                    {
                        return RejectAndRelease(reservations, "WrongContext", "Deferred target belongs to another tick.");
                    }

                    tokenReferences = checked(tokenReferences + 1UL);
                }

                if (!context.CanWrite(command.SortKey.ProcessorId, command))
                {
                    return RejectAndRelease(reservations, "MessagePermissionDenied", "Processor is not permitted to write command target.");
                }

                switch (command.Kind)
                {
                    case CommandKind.Create:
                        if (command.DeferredTarget is not DeferredEntityToken createToken ||
                            string.IsNullOrWhiteSpace(command.ComponentType) ||
                            !CommandValidation.IsIdentifier(command.ComponentType) ||
                            !IsKnownCreateType(context, command.ComponentType))
                        {
                            return RejectAndRelease(reservations, "ManifestMalformed", "Create command has an unknown component type.");
                        }

                        if (!created.TryAdd(createToken, scope))
                        {
                            return RejectAndRelease(reservations, "InvalidArgument", "Duplicate create token.");
                        }

                        entitySlots = checked(entitySlots + 1UL);
                        break;

                    case CommandKind.Write:
                        if (!HasTarget(command) || string.IsNullOrWhiteSpace(command.ComponentType) ||
                            string.IsNullOrWhiteSpace(command.FieldName) ||
                            !CommandValidation.IsIdentifier(command.ComponentType) ||
                            !CommandValidation.IsIdentifier(command.FieldName) ||
                            !context.IsKnownComponent(command.ComponentType) ||
                            !context.IsKnownField(command.ComponentType, command.FieldName))
                        {
                            return RejectAndRelease(reservations, "ManifestMalformed", "Write command has an unknown component or field.");
                        }

                        string targetKey = TargetKey(command);
                        if (command.DeferredTarget is DeferredEntityToken writeToken &&
                            (!created.TryGetValue(writeToken, out BufferScope createScope) ||
                                !ScopesMatch(createScope, writeToken)))
                        {
                            return RejectAndRelease(reservations, "WrongContext", "Deferred target was not created earlier in this batch.", writeToken.CanonicalKey);
                        }
                        if (destroyed.Contains(targetKey))
                        {
                            return RejectAndRelease(reservations, "InvalidArgument", "Write follows destroy in the same tick.");
                        }

                        if (command.TargetEntityId is string targetId && !context.EntityExists(targetId))
                        {
                            return RejectAndRelease(reservations, "RevisionConflict", "Write target is stale or missing.");
                        }

                        string fieldKey = string.Concat(targetKey, "|", command.ComponentType, "|", command.FieldName);
                        if (writtenFields.TryGetValue(fieldKey, out Command? firstWrite))
                        {
                            return RejectAndRelease(
                                reservations,
                                CommandFailure.Rejected("InvalidArgument", "Conflicting writes target the same field.", fieldKey)
                                    .WithConflict(firstWrite, command));
                        }

                        writtenFields.Add(fieldKey, command);

                        break;

                    case CommandKind.Destroy:
                        if (!HasTarget(command))
                        {
                            return RejectAndRelease(reservations, "ManifestMalformed", "Destroy command has no target.");
                        }

                        string destroyKey = TargetKey(command);
                        if (command.DeferredTarget is DeferredEntityToken destroyToken &&
                            (!created.TryGetValue(destroyToken, out BufferScope destroyScope) ||
                                !ScopesMatch(destroyScope, destroyToken)))
                        {
                            return RejectAndRelease(reservations, "WrongContext", "Deferred target was not created earlier in this batch.", destroyToken.CanonicalKey);
                        }
                        if (!destroyed.Add(destroyKey))
                        {
                            return RejectAndRelease(reservations, "InvalidArgument", "Duplicate destroy command.", destroyKey);
                        }

                        if (command.TargetEntityId is string existingId && !context.EntityExists(existingId))
                        {
                            return RejectAndRelease(reservations, "RevisionConflict", "Destroy target is stale or missing.");
                        }

                        break;

                    default:
                        return RejectAndRelease(reservations, "ManifestMalformed", "Unknown command kind.");
                }

                bytes = checked(bytes + command.EstimatedBytes);
                changes = checked(changes + 1UL);
                if (bytes > _options.MaxBytes || changes > _options.AvailableChangeEntries || entitySlots > _options.AvailableEntitySlots)
                {
                    return RejectAndRelease(reservations, "BudgetExceeded", "Command reservation budget exceeded.");
                }

                if (command.Kind == CommandKind.Create && command.DeferredTarget is DeferredEntityToken createTokenForPlan)
                {
                    // The actual LocalEntityId is assigned by the ECS participant at Apply;
                    // retaining the token proves its scope without fabricating an ID.
                    _ = createTokenForPlan;
                }
            }

            reservations = new CommandReservationSet(entitySlots, changes, bytes)
            {
                TokenReferences = tokenReferences
            };
            batch.MarkPrepared();
            return new CommandPreflightResult(CommandPreflightStatus.Prepared,
                new PreparedGameDelta(batch, reservations, _options.SchemaEpoch, resolutionPlan), null);
        }
        catch (OverflowException)
        {
            reservations.Release();
            return Failure(CommandPreflightStatus.Rejected, "CapacityExceeded", "Command reservation arithmetic overflowed.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            reservations.Release();
            return Failure(CommandPreflightStatus.Fatal, "InternalInvariant", ex.Message);
        }
    }

    public PreparedGameDelta Prepare(MergedCommandBatch batch)
    {
        CommandPreflightResult result = TryPrepare(batch);
        if (!result.IsPrepared || result.Delta is null) throw new CommandPreflightException(result.Failure);
        return result.Delta;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822")]
    public CommandPrepareResult Prepare(in MergedCommandBatch batch, in CommandPrepareContext context)
    {
        if (batch is null || batch.TickId != context.TickId || !string.Equals(batch.WorldId, context.WorldId, StringComparison.Ordinal))
        {
            return new CommandPrepareResult(CommandPreflightStatus.Rejected, null,
                CommandFailure.Rejected("WrongContext", "Prepare context does not match the merged batch."));
        }

        var validator = new CommandPreflightValidator(new CommandPreflightOptions
        {
            SchemaEpoch = context.SchemaEpoch,
            MaxCommands = context.Budget.MaxCommands,
            MaxBytes = context.Budget.MaxBytes,
            AvailableEntitySlots = context.AvailableEntitySlots,
            AvailableChangeEntries = context.AvailableChangeEntries,
            Context = context.ValidationContext
        });
        CommandPreflightResult result = validator.TryPrepare(batch);
        return new CommandPrepareResult(result.Status, result.Delta, result.Failure);
    }

    public bool TryPrepare(MergedCommandBatch batch, out PreparedGameDelta? delta, out CommandFailure? failure)
    {
        CommandPreflightResult result = TryPrepare(batch);
        delta = result.Delta;
        failure = result.Failure;
        return result.IsPrepared;
    }

    private static bool IsKnownCreateType(ICommandValidationContext context, string componentType) =>
        context is EcsWorldCommandValidationContext worldContext
            ? worldContext.IsKnownEntityType(componentType)
            : context.IsKnownComponent(componentType);

    private static bool HasTarget(Command command) =>
        command.TargetEntityId is not null || command.DeferredTarget is not null;

    private static string ScopeKey(Lumio.Gen.ContractTypes.ProcessorDescriptorPhase phase, string processorId) =>
        string.Concat(((int)phase).ToString(System.Globalization.CultureInfo.InvariantCulture), ":", processorId);

    private static bool ScopesMatch(BufferScope scope, DeferredEntityToken token) =>
        string.Equals(token.ProcessorId, scope.ProcessorId, StringComparison.Ordinal) &&
        (token.BufferGeneration == 0UL || scope.BufferGeneration == 0UL || token.BufferGeneration == scope.BufferGeneration);

    private static string TargetKey(Command command) => command.DeferredTarget is DeferredEntityToken token
        ? string.Concat("token:", token.CanonicalKey)
        : string.Concat("entity:", command.TargetEntityId);

    private static CommandPreflightResult RejectAndRelease(CommandReservationSet reservations, string errorId, string detail, string? evidence = null)
    {
        reservations.Release();
        return Failure(CommandPreflightStatus.Rejected, errorId, detail, evidence);
    }

    private static CommandPreflightResult RejectAndRelease(CommandReservationSet reservations, CommandFailure failure)
    {
        reservations.Release();
        return new CommandPreflightResult(CommandPreflightStatus.Rejected, null, failure);
    }

    private static CommandPreflightResult Failure(CommandPreflightStatus status, string errorId, string detail, string? evidence = null) =>
        new(status, null, new CommandFailure(
            status == CommandPreflightStatus.Retryable ? CommandFailureClass.Retryable :
            status == CommandPreflightStatus.Fatal ? CommandFailureClass.Fatal : CommandFailureClass.Rejected,
            errorId, detail, evidence));

    private readonly record struct BufferScope(
        Lumio.Gen.ContractTypes.ProcessorDescriptorPhase Phase,
        string ProcessorId,
        ulong BufferGeneration,
        bool MayEmitStructuralCommands);
}

public sealed class CommandPreflightException : InvalidOperationException
{
    public CommandPreflightException(CommandFailure? failure)
        : base(failure?.Detail ?? "Command preflight failed")
    {
        Failure = failure;
    }

    public CommandFailure? Failure { get; }
}

internal sealed class FailClosedCommandValidationContext : ICommandValidationContext
{
    internal static readonly FailClosedCommandValidationContext Instance = new();

    public bool IsKnownComponent(string componentType) => false;

    public bool IsKnownField(string componentType, string fieldName) => false;

    public bool EntityExists(string entityId) => false;

    public bool CanWrite(string processorId, Command command) => false;
}

internal sealed class EcsWorldCommandValidationContext : ICommandValidationContext
{
    private readonly EcsWorld _world;

    internal EcsWorldCommandValidationContext(EcsWorld world) =>
        _world = world ?? throw new ArgumentNullException(nameof(world));

    public bool IsKnownComponent(string componentType) =>
        _world.TryGetRegisteredComponent(componentType, out _);

    public bool IsKnownField(string componentType, string fieldName) =>
        _world.TryGetRegisteredField(componentType, fieldName, out _, out _);

    public bool EntityExists(string entityId) =>
        LocalEntityId.TryParse(entityId, out LocalEntityId id) && _world.EntityIsAlive(id);

    public bool CanWrite(string processorId, Command command)
    {
        if (string.IsNullOrWhiteSpace(processorId) || command is null) return false;
        switch (command.Kind)
        {
            case CommandKind.Create:
                return IsKnownEntityType(command.ComponentType);
            case CommandKind.Write:
                if (string.IsNullOrWhiteSpace(command.ComponentType) ||
                    string.IsNullOrWhiteSpace(command.FieldName) ||
                    !IsKnownComponent(command.ComponentType) ||
                    !IsKnownField(command.ComponentType, command.FieldName))
                {
                    return false;
                }

                if (command.DeferredTarget is not null) return true;
                return command.TargetEntityId is string targetId && EntityExists(targetId);
            case CommandKind.Destroy:
                if (command.DeferredTarget is not null) return true;
                return command.TargetEntityId is string destroyId && EntityExists(destroyId);
            default:
                return false;
        }
    }

    internal bool IsKnownEntityType(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _world.TryGetRegisteredEntityType(name, out _, out _);
}
