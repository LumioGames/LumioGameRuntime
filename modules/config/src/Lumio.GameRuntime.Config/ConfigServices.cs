namespace Lumio.GameRuntime.Config;

/// <summary>
/// Stable candidate facade: validator, stage/activation, and the active
/// <see cref="IConfigSnapshotView"/>. No compiler, file watcher, or DI container.
/// </summary>
public sealed class ConfigServices
{
    private readonly ConfigModule _module;

    internal ConfigServices(ConfigModule module)
    {
        _module = module;
    }

    /// <summary>Currently activated snapshot. Throws when none has been activated.</summary>
    public IConfigSnapshotView ActiveSnapshot => _module.ActiveSnapshot;

    /// <summary>Validate a generated artifact without staging or activation.</summary>
    public ConfigSubmitResult Validate(in GeneratedConfigArtifactView artifact) =>
        _module.Validate(in artifact);

    /// <summary>Stage a constructed snapshot. Does not switch Active.</summary>
    public ConfigStageResult Stage(ConfigSnapshot snapshot) => _module.Stage(snapshot);

    /// <summary>Merge submitted validated layers and stage the resulting snapshot.</summary>
    public ConfigStageResult StageMerged(ConfigSnapshotId snapshotId) =>
        _module.StageMerged(snapshotId);

    /// <summary>Owner-thread Tick Barrier activation.</summary>
    public ConfigActivationResult ActivateAtBarrier(TickId tickId) =>
        _module.ActivateAtBarrier(tickId);

    /// <summary>Pin Active to a lease for this tick.</summary>
    public ConfigSnapshotLease AcquireForTick(TickId tickId) =>
        _module.AcquireForTick(tickId);
}
