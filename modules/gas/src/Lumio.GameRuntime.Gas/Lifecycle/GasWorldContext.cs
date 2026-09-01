using System;
using System.Collections.Generic;
using Lumio.GameRuntime.Ecs;

namespace Lumio.GameRuntime.Gas;

/// <summary>World-local GAS framework owner. Holds handle indexes only; ECS remains the authority store.</summary>
public sealed class GasWorldContext : IDisposable
{
    private readonly IGasEcsProjectionPort _projection;
    private readonly GasTypeRegistry _types;
    private readonly Dictionary<ulong, uint> _abilityGenerations = new();
    private readonly HashSet<ulong> _liveAbilities = new();
    private readonly Dictionary<ulong, uint> _effectGenerations = new();
    private readonly HashSet<ulong> _liveEffects = new();
    private GasFrameworkState _state = GasFrameworkState.Unloaded;
    private bool _disposed;

    public GasWorldContext(WorldId worldId, IGasEcsProjectionPort projection)
        : this(worldId, projection, new GasTypeRegistry())
    {
    }

    public GasWorldContext(WorldId worldId, IGasEcsProjectionPort projection, GasTypeRegistry types)
    {
        if (worldId.IsDefault)
            throw new ArgumentOutOfRangeException(nameof(worldId), worldId, "A non-default WorldId is required.");
#if NET10_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(types);
#else
        if (projection is null) throw new ArgumentNullException(nameof(projection));
        if (types is null) throw new ArgumentNullException(nameof(types));
#endif
        WorldId = worldId;
        _projection = projection;
        _types = types;
    }

    public WorldId WorldId { get; }

    public GasFrameworkState State => _state;

    public GasTypeRegistry Types => _types;

    public GasLifecycleResult Register() => Transition(GasFrameworkState.Unloaded, GasFrameworkState.Registered);

    public GasLifecycleResult MarkReady()
    {
        GasLifecycleResult result = Transition(GasFrameworkState.Registered, GasFrameworkState.Ready);
        if (result.Succeeded)
            _types.Freeze();
        return result;
    }

    public GasLifecycleResult Start() => Transition(GasFrameworkState.Ready, GasFrameworkState.Running);

    public GasLifecycleResult BeginDrain() => Transition(GasFrameworkState.Running, GasFrameworkState.Draining);

    public GasLifecycleResult Fault(string generatedErrorId)
    {
        if (string.IsNullOrWhiteSpace(generatedErrorId) || !GasErrorIds.IsGenerated(generatedErrorId))
            throw new ArgumentException("A stable generated error ID is required.", nameof(generatedErrorId));
        if (_disposed)
            return GasLifecycleResult.Rejected(_state, GasErrorIds.ContextDestroyed);
        if (_state is GasFrameworkState.Unloaded)
            return GasLifecycleResult.Rejected(_state, GasErrorIds.InternalInvariant);
        if (_state is GasFrameworkState.Faulted)
            return GasLifecycleResult.Rejected(_state, GasErrorIds.InvalidArgument);

        _state = GasFrameworkState.Faulted;
        return GasLifecycleResult.Accepted(_state);
    }

    public GasLifecycleResult DisposeContext()
    {
        if (_disposed)
            return GasLifecycleResult.Accepted(GasFrameworkState.Unloaded);
        if (_state is GasFrameworkState.Draining or GasFrameworkState.Faulted)
        {
            _disposed = true;
            _state = GasFrameworkState.Unloaded;
            return GasLifecycleResult.Accepted(_state);
        }

        return RejectCurrent();
    }

    public void Dispose() => DisposeContext();

    public GasRegistrationResult RegisterAbility(AbilityTypeId typeId, in GasTypeDescriptor descriptor)
    {
        if (_disposed)
            return GasRegistrationResult.Rejected(GasErrorIds.ContextDestroyed);
        return _types.RegisterAbility(typeId, descriptor);
    }

    public GasRegistrationResult RegisterEffect(EffectTypeId typeId, in GasTypeDescriptor descriptor)
    {
        if (_disposed)
            return GasRegistrationResult.Rejected(GasErrorIds.ContextDestroyed);
        return _types.RegisterEffect(typeId, descriptor);
    }

    public AbilityHandleResult CreateAbilityHandle(AbilityInstanceId instanceId)
    {
        if (!TryBeginHandleWork(out string? errorId))
            return AbilityHandleResult.Failed(errorId!);
        if (instanceId.IsDefault)
            return AbilityHandleResult.Failed(GasErrorIds.InvalidArgument);
        if (_liveAbilities.Contains(instanceId.Value))
            return AbilityHandleResult.Failed(GasErrorIds.InvalidArgument);

        if (!TryNextGeneration(_abilityGenerations, instanceId.Value, out uint generation, out string? overflow))
            return AbilityHandleResult.Failed(overflow!);

        _abilityGenerations[instanceId.Value] = generation;
        _liveAbilities.Add(instanceId.Value);
        return AbilityHandleResult.Issued(new AbilityHandle(WorldId, instanceId, generation));
    }

    public EffectHandleResult CreateEffectHandle(EffectInstanceId instanceId)
    {
        if (!TryBeginHandleWork(out string? errorId))
            return EffectHandleResult.Failed(errorId!);
        if (instanceId.IsDefault)
            return EffectHandleResult.Failed(GasErrorIds.InvalidArgument);
        if (_liveEffects.Contains(instanceId.Value))
            return EffectHandleResult.Failed(GasErrorIds.InvalidArgument);

        if (!TryNextGeneration(_effectGenerations, instanceId.Value, out uint generation, out string? overflow))
            return EffectHandleResult.Failed(overflow!);

        _effectGenerations[instanceId.Value] = generation;
        _liveEffects.Add(instanceId.Value);
        return EffectHandleResult.Issued(new EffectHandle(WorldId, instanceId, generation));
    }

    public GasResolveResult TryResolveAbility(in AbilityHandle handle, out AbilityInstanceId instanceId)
    {
        instanceId = default;
        GasResolveResult result = Resolve(
            handle.IsDefault,
            handle.WorldId,
            handle.InstanceId.Value,
            handle.Generation,
            _abilityGenerations,
            _liveAbilities);
        if (result.Resolved)
            instanceId = handle.InstanceId;
        return result;
    }

    public GasResolveResult TryResolveEffect(in EffectHandle handle, out EffectInstanceId instanceId)
    {
        instanceId = default;
        GasResolveResult result = Resolve(
            handle.IsDefault,
            handle.WorldId,
            handle.InstanceId.Value,
            handle.Generation,
            _effectGenerations,
            _liveEffects);
        if (result.Resolved)
            instanceId = handle.InstanceId;
        return result;
    }

    public GasRetireResult RetireAbility(in AbilityHandle handle)
    {
        GasResolveResult resolved = TryResolveAbility(handle, out AbilityInstanceId instanceId);
        if (!resolved.Resolved)
            return GasRetireResult.Failed(resolved.GeneratedErrorId ?? GasErrorIds.InvalidHandle);
        _liveAbilities.Remove(instanceId.Value);
        return GasRetireResult.Retired();
    }

    public GasRetireResult RetireEffect(in EffectHandle handle)
    {
        GasResolveResult resolved = TryResolveEffect(handle, out EffectInstanceId instanceId);
        if (!resolved.Resolved)
            return GasRetireResult.Failed(resolved.GeneratedErrorId ?? GasErrorIds.InvalidHandle);
        _liveEffects.Remove(instanceId.Value);
        return GasRetireResult.Retired();
    }

    public GasProjectionReadResult ReadAuthoritative(LocalEntityId entity, in GasAuthoritativeField field)
    {
        if (!TryBeginProjection(out string? errorId))
            return GasProjectionReadResult.Failed(errorId!);
        if (entity.IsDefault || string.IsNullOrWhiteSpace(field.FieldName) || !field.IsAllowedComponent)
            return GasProjectionReadResult.Failed(GasErrorIds.InvalidArgument);
        return _projection.ReadAuthoritative(entity, in field);
    }

    public GasProjectionWriteResult WriteAuthoritative(
        LocalEntityId entity,
        in GasAuthoritativeField field,
        ReadOnlySpan<byte> canonicalValue)
    {
        if (!TryBeginProjection(out string? errorId))
            return GasProjectionWriteResult.Rejected(errorId!);
        if (entity.IsDefault || string.IsNullOrWhiteSpace(field.FieldName) || !field.IsAllowedComponent)
            return GasProjectionWriteResult.Rejected(GasErrorIds.InvalidArgument);
        return _projection.WriteAuthoritative(entity, in field, canonicalValue);
    }

    private GasLifecycleResult Transition(GasFrameworkState expected, GasFrameworkState next)
    {
        if (_disposed)
            return GasLifecycleResult.Rejected(_state, GasErrorIds.ContextDestroyed);
        if (_state != expected)
            return RejectCurrent();
        _state = next;
        return GasLifecycleResult.Accepted(_state);
    }

    private GasLifecycleResult RejectCurrent()
    {
        string code = _disposed
            ? GasErrorIds.ContextDestroyed
            : _state == GasFrameworkState.Draining
                ? GasErrorIds.ContextClosing
                : GasErrorIds.InternalInvariant;
        return GasLifecycleResult.Rejected(_state, code);
    }

    private bool TryBeginHandleWork(out string? errorId)
    {
        if (_disposed || _state == GasFrameworkState.Unloaded)
        {
            errorId = GasErrorIds.ContextDestroyed;
            return false;
        }

        if (_state == GasFrameworkState.Faulted)
        {
            errorId = GasErrorIds.InternalInvariant;
            return false;
        }

        if (_state == GasFrameworkState.Draining)
        {
            errorId = GasErrorIds.ContextClosing;
            return false;
        }

        if (_state != GasFrameworkState.Running)
        {
            errorId = GasErrorIds.InvalidArgument;
            return false;
        }

        errorId = null;
        return true;
    }

    private bool TryBeginProjection(out string? errorId) => TryBeginHandleWork(out errorId);

    private bool TryNextGeneration(
        Dictionary<ulong, uint> generations,
        ulong instance,
        out uint generation,
        out string? errorId)
    {
        generation = 1U;
        errorId = null;
        if (!generations.TryGetValue(instance, out uint previous))
            return true;

        try
        {
            generation = checked(previous + 1U);
            return true;
        }
        catch (OverflowException)
        {
            _state = GasFrameworkState.Faulted;
            errorId = GasErrorIds.InternalInvariant;
            generation = previous;
            return false;
        }
    }

    private GasResolveResult Resolve(
        bool isDefault,
        WorldId worldId,
        ulong instance,
        uint generation,
        Dictionary<ulong, uint> generations,
        HashSet<ulong> live)
    {
        if (!TryBeginHandleWork(out string? errorId))
            return GasResolveResult.Failed(errorId!);
        if (isDefault)
            return GasResolveResult.Failed(GasErrorIds.InvalidHandle);
        if (worldId != WorldId)
            return GasResolveResult.Failed(GasErrorIds.WrongContext);
        if (!live.Contains(instance) ||
            !generations.TryGetValue(instance, out uint current) ||
            current != generation)
        {
            return GasResolveResult.Failed(GasErrorIds.InvalidHandle);
        }

        return GasResolveResult.Ok();
    }
}
