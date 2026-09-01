using System;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Config;
using Xunit;

namespace Lumio.GameRuntime.Config.Tests;

/// <summary>
/// T06.S02 / S05 / S06: fixed six-layer precedence. Later layers override earlier
/// layers at key/column granularity. Canonical bytes do not depend on artifact
/// enumeration order. Hashes are compared across permutations, never copied from
/// the implementation.
/// </summary>
public sealed class SixLayerMergeGoldenTests
{
    private static readonly string[] ExpectedLayerNames =
    {
        "Engine", "Platform", "Server", "Product", "Environment", "UserOrSession",
    };

    private static readonly string[] BetaTableId = { "beta" };

    private static readonly string[] CanonicalRowKeyOrder = { "K9", "k0", "k1", "k10", "k2" };

    private static readonly ConfigLayer[] LayerKinds =
    {
        ConfigLayer.Engine,
        ConfigLayer.Platform,
        ConfigLayer.Server,
        ConfigLayer.Product,
        ConfigLayer.Environment,
        ConfigLayer.UserOrSession,
    };

    [Fact]
    public void ConfigLayerValuesAreExactlyTheFixedSixInOrder()
    {
        Assert.Equal(ExpectedLayerNames, Enum.GetNames<ConfigLayer>());
        for (var index = 0; index < ExpectedLayerNames.Length; index++)
        {
            Assert.Equal(index, (int)Enum.Parse<ConfigLayer>(ExpectedLayerNames[index]));
        }
    }

    [Fact]
    public void MergerDoesNotAcceptACustomPrecedenceComparer()
    {
        var merge = typeof(ConfigLayerMerger).GetMethod(
            nameof(ConfigLayerMerger.Merge),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(merge);
        Assert.DoesNotContain(
            merge!.GetParameters(),
            parameter =>
                parameter.ParameterType.Name.Contains("Comparer", StringComparison.Ordinal) ||
                (parameter.Name ?? string.Empty).Contains("comparer", StringComparison.OrdinalIgnoreCase) ||
                (parameter.Name ?? string.Empty).Contains("precedence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LastLayerWinsForSameKeyAndColumn()
    {
        var layers = SixLayersWithValuePerLayer(0, 1, 2, 3, 4, 5);

        var result = new ConfigLayerMerger().Merge(layers);

        Assert.Equal(ConfigMergeStatus.Merged, result.Status);
        Assert.Equal("5", result.LookupValue("gameplay", "k", "v"));
    }

    [Fact]
    public void RemovingUserOrSessionFallsBackToEnvironment()
    {
        var layers = SixLayersWithValuePerLayer(0, 1, 2, 3, 4, 5).Take(5).ToArray();

        var result = new ConfigLayerMerger().Merge(layers);

        Assert.Equal(ConfigMergeStatus.Merged, result.Status);
        Assert.Equal("4", result.LookupValue("gameplay", "k", "v"));
    }

    [Fact]
    public void EnumerationOrderDoesNotChangeCanonicalBytes()
    {
        var layers = SixLayersWithValuePerLayer(0, 1, 2, 3, 4, 5);

        var inOrder = new ConfigLayerMerger().Merge(layers);
        var reversed = new ConfigLayerMerger().Merge(layers.Reverse().ToArray());
        var rotated = new ConfigLayerMerger().Merge(new[]
        {
            layers[3], layers[0], layers[5], layers[1], layers[4], layers[2],
        });

        Assert.Equal(ConfigMergeStatus.Merged, inOrder.Status);
        Assert.False(string.IsNullOrWhiteSpace(inOrder.OutputHashHex));
        Assert.Equal(inOrder.OutputHashHex, reversed.OutputHashHex);
        Assert.Equal(inOrder.OutputHashHex, rotated.OutputHashHex);
        Assert.All(
            new[] { reversed, rotated },
            other =>
            {
                Assert.Equal(
                    inOrder.Tables.Select(table => table.CanonicalBytesHex).ToArray(),
                    other.Tables.Select(table => table.CanonicalBytesHex).ToArray());
                Assert.Equal(
                    inOrder.Tables.SelectMany(table => table.Rows.Select(row => row.CanonicalValuesJson)).ToArray(),
                    other.Tables.SelectMany(table => table.Rows.Select(row => row.CanonicalValuesJson)).ToArray());
            });
    }

    [Fact]
    public void RepeatedMergeOfTheSameLayersIsIdempotent()
    {
        var layers = SixLayersWithValuePerLayer(0, 1, 2, 3, 4, 5);
        var merger = new ConfigLayerMerger();

        var first = merger.Merge(layers);
        var second = merger.Merge(layers);

        Assert.Equal(first.OutputHashHex, second.OutputHashHex);
        Assert.Equal(
            first.Tables.SelectMany(table => table.Rows.Select(row => row.CanonicalValuesJson)).ToArray(),
            second.Tables.SelectMany(table => table.Rows.Select(row => row.CanonicalValuesJson)).ToArray());
    }

    [Fact]
    public void RowOrderWithinALayerDoesNotChangeCanonicalBytes()
    {
        var first = LayerWithValue(ConfigLayer.Engine, ("a", "{\"v\":1}"), ("b", "{\"v\":2}"));
        var second = LayerWithValue(ConfigLayer.Engine, ("b", "{\"v\":2}"), ("a", "{\"v\":1}"));

        var firstResult = new ConfigLayerMerger().Merge(new[] { first });
        var secondResult = new ConfigLayerMerger().Merge(new[] { second });

        Assert.Equal(firstResult.OutputHashHex, secondResult.OutputHashHex);
        Assert.Equal(firstResult.Tables[0].CanonicalBytesHex, secondResult.Tables[0].CanonicalBytesHex);
    }

    [Fact]
    public void ColumnLevelOverrideMergesDisjointColumnsFromDifferentLayers()
    {
        var engine = new ValidatedConfigLayer(
            ConfigLayer.Engine,
            TableFactory.Artifact(
                TableFactory.Table(
                    "gameplay",
                    TableFactory.Cols(
                        TableFactory.Column("v", "i32", required: true),
                        TableFactory.Column("w", "i32", required: true)),
                    TableFactory.Rows(
                        TableFactory.Row("k", "{\"v\":1,\"w\":7}")))));
        var user = new ValidatedConfigLayer(
            ConfigLayer.UserOrSession,
            TableFactory.Artifact(
                ConfigLayer.UserOrSession,
                TableFactory.Table(
                    "gameplay",
                    TableFactory.Cols(
                        TableFactory.Column("v", "i32", required: true),
                        TableFactory.Column("w", "i32", required: true)),
                    TableFactory.Rows(
                        TableFactory.Row("k", "{\"v\":9}")))));

        var result = new ConfigLayerMerger().Merge(new[] { engine, user });

        Assert.Equal(ConfigMergeStatus.Merged, result.Status);
        Assert.Equal("9", result.LookupValue("gameplay", "k", "v"));
        Assert.Equal("7", result.LookupValue("gameplay", "k", "w"));
    }

    [Fact]
    public void ConflictingColumnDeclarationsFailTheMerge()
    {
        var engine = new ValidatedConfigLayer(
            ConfigLayer.Engine,
            TableFactory.Artifact(
                TableFactory.Table(
                    "gameplay",
                    TableFactory.Cols(
                        TableFactory.Column("v", "i32", required: true)),
                    TableFactory.Rows(
                        TableFactory.Row("k", "{\"v\":1}")))));
        var platform = new ValidatedConfigLayer(
            ConfigLayer.Platform,
            TableFactory.Artifact(
                ConfigLayer.Platform,
                TableFactory.Table(
                    "gameplay",
                    TableFactory.Cols(
                        TableFactory.Column("v", "string", required: true)),
                    TableFactory.Rows(
                        TableFactory.Row("k", "{\"v\":\"one\"}")))));

        var result = new ConfigLayerMerger().Merge(new[] { engine, platform });

        Assert.Equal(ConfigMergeStatus.Failed, result.Status);
        Assert.Contains(result.Errors, error => error.Contains("gameplay", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateLayerInInputFailsInsteadOfDoubleApplying()
    {
        var layer = LayerWithValue(ConfigLayer.Engine, ("k", "{\"v\":1}"));

        var result = new ConfigLayerMerger().Merge(new[] { layer, layer });

        Assert.Equal(ConfigMergeStatus.Failed, result.Status);
        Assert.Contains(
            result.Errors,
            error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                && error.Contains("Engine", StringComparison.Ordinal));
    }

    [Fact]
    public void MergedTablesAndRowsAreEmittedInCanonicalOrder()
    {
        var layer = new ValidatedConfigLayer(
            ConfigLayer.Engine,
            TableFactory.Artifact(
                TableFactory.Table(
                    "beta",
                    TableFactory.Cols(
                        TableFactory.Column("v", "i32", required: true)),
                    TableFactory.Rows(
                        TableFactory.Row("k2", "{\"v\":2}"),
                        TableFactory.Row("k1", "{\"v\":1}"),
                        TableFactory.Row("k10", "{\"v\":10}"),
                        TableFactory.Row("k0", "{\"v\":0}"),
                        TableFactory.Row("K9", "{\"v\":9}")))));

        var result = new ConfigLayerMerger().Merge(new[] { layer });

        Assert.Equal(ConfigMergeStatus.Merged, result.Status);
        Assert.Equal(BetaTableId, result.Tables.Select(table => table.TableId).ToArray());
        Assert.Equal(CanonicalRowKeyOrder, result.Tables[0].Rows.Select(row => row.Key).ToArray());
    }

    private static ValidatedConfigLayer[] SixLayersWithValuePerLayer(params int[] values)
    {
        var layers = new ValidatedConfigLayer[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            layers[index] = LayerWithValue(LayerKinds[index], ("k", $"{{\"v\":{values[index]}}}"));
        }

        return layers;
    }

    private static ValidatedConfigLayer LayerWithValue(ConfigLayer layer, params (string Key, string Values)[] rows) =>
        new(
            layer,
            TableFactory.Artifact(
                layer,
                TableFactory.Table(
                    "gameplay",
                    TableFactory.Cols(
                        TableFactory.Column("v", "i32", required: true)),
                    TableFactory.Rows(
                        rows.Select(row => TableFactory.Row(row.Key, row.Values)).ToArray()))));
}
