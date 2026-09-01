using System;
using System.Collections.Generic;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Config;

/// <summary>
/// Config module facade: validate generated artifacts, merge fixed layers,
/// stage an immutable snapshot, and activate it only at the Tick Barrier.
/// No compiler, file watcher, or DI container.
/// </summary>
public sealed class ConfigModule : IGeneratedConfigArtifactPort
{
    private readonly object _gate = new();
    private readonly GeneratedConfigValidator _validator = new();
    private readonly ConfigLayerMerger _merger = new();
    private readonly ConfigActivationSlot _slot;
    private readonly ConfigActivator _activator;
    private readonly Dictionary<ConfigLayer, ValidatedConfigLayer> _layers = new();

    private ConfigModule()
    {
        _slot = new ConfigActivationSlot();
        _activator = new ConfigActivator(_slot);
        Services = new ConfigServices(this);
    }

    /// <summary>Create a module bound to the constructing thread as owner.</summary>
    public static ConfigModule Create() => new();

    /// <summary>Read/stage/activation facade.</summary>
    public ConfigServices Services { get; }

    /// <summary>Current Active snapshot. Throws when none has been activated.</summary>
    public IConfigSnapshotView ActiveSnapshot => _slot.Active;

    /// <summary>Owner-thread barrier activator.</summary>
    public ConfigActivator Activator => _activator;

    /// <inheritdoc />
    public ConfigSubmitResult Submit(in GeneratedConfigArtifactView artifact)
    {
        ConfigValidationReport report = _validator.Validate(artifact, ConfigValidationLimits.Default);
        if (!report.IsValid)
        {
            return ConfigSubmitResult.Rejected("ManifestMalformed");
        }

        var tables = new ConfigTable[artifact.Tables.Count];
        for (var index = 0; index < artifact.Tables.Count; index++)
        {
            tables[index] = artifact.Tables[index];
        }

        var copy = new GeneratedConfigArtifactView(artifact.ArtifactId, artifact.DeclaredLayer, tables);
        lock (_gate)
        {
            _layers[copy.DeclaredLayer] = new ValidatedConfigLayer(copy.DeclaredLayer, copy);
        }

        return ConfigSubmitResult.Accepted();
    }

    /// <summary>Validate without storing or staging. Does not touch Active.</summary>
    public ConfigSubmitResult Validate(in GeneratedConfigArtifactView artifact)
    {
        ConfigValidationReport report = _validator.Validate(artifact, ConfigValidationLimits.Default);
        return report.IsValid
            ? ConfigSubmitResult.Accepted()
            : ConfigSubmitResult.Rejected("ManifestMalformed");
    }

    /// <summary>Merge previously submitted validated layers and occupy the staged slot.</summary>
    public ConfigStageResult StageMerged(ConfigSnapshotId snapshotId)
    {
        ValidatedConfigLayer[] layers;
        lock (_gate)
        {
            if (_layers.Count == 0)
            {
                return new ConfigStageResult(false, snapshotId, "InvalidArgument");
            }

            layers = new ValidatedConfigLayer[_layers.Count];
            var index = 0;
            foreach (ValidatedConfigLayer layer in _layers.Values)
            {
                layers[index++] = layer;
            }
        }

        ConfigMergeResult merged = _merger.Merge(layers);
        if (merged.Status != ConfigMergeStatus.Merged)
        {
            return new ConfigStageResult(false, snapshotId, "InvalidArgument");
        }

        ConfigSnapshot snapshot = ConfigSnapshot.FromMergeResult(
            snapshotId,
            SchemaEpoch.FromGeneratedContracts(),
            merged);
        return _slot.Stage(snapshot);
    }

    /// <summary>Stage an already constructed immutable snapshot. Does not activate.</summary>
    public ConfigStageResult Stage(ConfigSnapshot snapshot) => _slot.Stage(snapshot);

    /// <summary>Owner-thread Tick Barrier activation.</summary>
    public ConfigActivationResult ActivateAtBarrier(TickId tickId) =>
        _activator.ActivateAtBarrier(tickId);

    /// <summary>Pin Active to a lease for this tick.</summary>
    public ConfigSnapshotLease AcquireForTick(TickId tickId) =>
        _slot.AcquireForTick(tickId);
}
