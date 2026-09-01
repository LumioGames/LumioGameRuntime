using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumio.GameRuntime.Config;
using Lumio.GameRuntime.GeneratedContracts;
using Xunit;

namespace Lumio.GameRuntime.Config.Tests;

/// <summary>
/// T07.S01 / S04: copy-on-construct snapshot immutability, concurrent identical
/// reads, typed reader surface, and lease disposed contract.
/// </summary>
public sealed class SnapshotReaderPropertyTests
{
    private const int ConcurrentReadCount = 10_000;

    private static readonly string[] MutableCollectionTypeNames =
    {
        "Dictionary", "List", "Array", "HashSet", "SortedDictionary",
    };

    [Fact]
    public void SnapshotIsUnchangedAfterCallerMutatesInputArrays()
    {
        var cells = new[] { new ConfigSnapshotCell("speed", "7") };
        var rows = new[] { new ConfigSnapshotRow("bolt", cells) };
        var tables = new[] { new ConfigSnapshotTable("gameplay", "table-hash-a", rows) };
        var snapshot = new ConfigSnapshot(
            new ConfigSnapshotId(1UL),
            SchemaEpoch.FromGeneratedContracts(),
            "output-hash-a",
            tables);

        cells[0] = new ConfigSnapshotCell("speed", "0");
        rows[0] = new ConfigSnapshotRow("bolt", new[] { new ConfigSnapshotCell("speed", "99") });
        tables[0] = new ConfigSnapshotTable("mutated", "deadbeef", Array.Empty<ConfigSnapshotRow>());

        Assert.True(snapshot.TryOpenTable("gameplay", out ConfigTableReader reader));
        Assert.False(snapshot.TryOpenTable("mutated", out _));
        Assert.True(reader.TryGet("bolt", "speed", out ConfigValueView value));
        Assert.Equal("7", value.CanonicalText);
        Assert.Equal("table-hash-a", reader.CanonicalBytesHex);
        Assert.Equal("output-hash-a", snapshot.OutputHashHex);
        Assert.Equal(1UL, snapshot.SnapshotId.Value);
    }

    [Fact]
    public void ConcurrentReadsOfTheSameKeyReturnIdenticalGeneratedValueAndHash()
    {
        var layers = SixLayersWithValuePerLayer(0, 1, 2, 3, 4, 5);
        var merged = new ConfigLayerMerger().Merge(layers);
        Assert.Equal(ConfigMergeStatus.Merged, merged.Status);
        Assert.False(string.IsNullOrWhiteSpace(merged.OutputHashHex));
        Assert.Equal("5", merged.LookupValue("gameplay", "k", "v"));

        var snapshot = ConfigSnapshot.FromMergeResult(
            new ConfigSnapshotId(11UL),
            SchemaEpoch.FromGeneratedContracts(),
            merged);
        Assert.True(snapshot.TryOpenTable("gameplay", out ConfigTableReader reader));

        var expectedValue = "5";
        var expectedTableHash = merged.Tables[0].CanonicalBytesHex;
        var expectedOutputHash = merged.OutputHashHex!;
        var mismatches = 0;
        Parallel.For(0, ConcurrentReadCount, _ =>
        {
            if (!snapshot.TryOpenTable("gameplay", out ConfigTableReader concurrentReader) ||
                !concurrentReader.TryGet("k", "v", out ConfigValueView value) ||
                !string.Equals(value.CanonicalText, expectedValue, StringComparison.Ordinal) ||
                !string.Equals(concurrentReader.CanonicalBytesHex, expectedTableHash, StringComparison.Ordinal) ||
                !string.Equals(snapshot.OutputHashHex, expectedOutputHash, StringComparison.Ordinal) ||
                snapshot.SnapshotId.Value != 11UL)
            {
                Interlocked.Increment(ref mismatches);
            }
        });

        Assert.Equal(0, mismatches);
        Assert.Equal(expectedOutputHash, snapshot.OutputHashHex);
        Assert.Equal(GeneratedContractManifest.SchemaEpoch, snapshot.SchemaEpoch.Value);
    }

    [Fact]
    public void TryGetMissingRowOrColumnDoesNotReturnADefaultZeroValue()
    {
        var snapshot = SnapshotWithCell("gameplay", "bolt", "speed", "7");
        Assert.True(snapshot.TryOpenTable("gameplay", out ConfigTableReader reader));

        Assert.False(reader.TryGet("missing-row", "speed", out ConfigValueView missingRow));
        Assert.True(string.IsNullOrEmpty(missingRow.CanonicalText) || missingRow.CanonicalText != "0");
        Assert.NotEqual("0", missingRow.CanonicalText);
        Assert.False(reader.TryGet("bolt", "missing-column", out ConfigValueView missingColumn));
        Assert.NotEqual("0", missingColumn.CanonicalText);
        Assert.False(snapshot.TryOpenTable("unknown-table", out _));
    }

    [Fact]
    public void ConfigTableReaderDoesNotExposeMutableDictionaryOrArray()
    {
        var methods = typeof(ConfigTableReader).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var method in methods)
        {
            Assert.False(IsForbiddenMutableCollection(method.ReturnType), method.Name);
            foreach (var parameter in method.GetParameters())
            {
                var type = parameter.ParameterType.IsByRef
                    ? parameter.ParameterType.GetElementType()!
                    : parameter.ParameterType;
                Assert.False(IsForbiddenMutableCollection(type), parameter.Name);
            }
        }

        Assert.Equal(typeof(ConfigValueView), typeof(ConfigTableReader).GetMethod(nameof(ConfigTableReader.TryGet))!
            .GetParameters()
            .Single(parameter => parameter.IsOut)
            .ParameterType.GetElementType());
    }

    [Fact]
    public void DisposedLeaseRejectsSnapshotAndBoundReaderAccess()
    {
        var slot = ConfigSnapshotFixtures.ActiveSlot(1UL);
        ConfigSnapshotLease lease = slot.AcquireForTick(TickId.FromUInt64(4UL));
        Assert.True(lease.TryOpenTable("gameplay", out ConfigTableReader reader));
        Assert.True(reader.TryGet("k", "v", out ConfigValueView before));
        Assert.Equal("1", before.CanonicalText);

        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.Snapshot);
        Assert.Throws<ObjectDisposedException>(() => lease.TryOpenTable("gameplay", out _));
        Assert.Throws<ObjectDisposedException>(() => reader.TryGet("k", "v", out _));
        Assert.Equal(1UL, slot.Active.SnapshotId.Value);
    }

    [Fact]
    public void MergeResultIsCopiedIntoTheSnapshotSoLaterMergeCallsCannotAliasStorage()
    {
        var firstLayers = SixLayersWithValuePerLayer(0, 1, 2, 3, 4, 5);
        var first = new ConfigLayerMerger().Merge(firstLayers);
        var snapshot = ConfigSnapshot.FromMergeResult(
            new ConfigSnapshotId(2UL),
            SchemaEpoch.FromGeneratedContracts(),
            first);

        var secondLayers = SixLayersWithValuePerLayer(9, 9, 9, 9, 9, 8);
        var second = new ConfigLayerMerger().Merge(secondLayers);
        Assert.NotEqual(first.OutputHashHex, second.OutputHashHex);

        Assert.True(snapshot.TryOpenTable("gameplay", out ConfigTableReader reader));
        Assert.True(reader.TryGet("k", "v", out ConfigValueView value));
        Assert.Equal("5", value.CanonicalText);
        Assert.Equal(first.OutputHashHex, snapshot.OutputHashHex);
        Assert.Equal(first.Tables[0].CanonicalBytesHex, reader.CanonicalBytesHex);
    }

    private static bool IsForbiddenMutableCollection(Type type)
    {
        if (type.IsArray)
        {
            return true;
        }

        var name = type.IsGenericType ? type.GetGenericTypeDefinition().Name : type.Name;
        return MutableCollectionTypeNames.Any(candidate =>
            name.StartsWith(candidate, StringComparison.Ordinal));
    }

    private static ConfigSnapshot SnapshotWithCell(string tableId, string rowKey, string column, string value)
    {
        var rows = new[] { new ConfigSnapshotRow(rowKey, new[] { new ConfigSnapshotCell(column, value) }) };
        var tables = new[] { new ConfigSnapshotTable(tableId, "table-hash", rows) };
        return new ConfigSnapshot(
            new ConfigSnapshotId(1UL),
            SchemaEpoch.FromGeneratedContracts(),
            "output-hash",
            tables);
    }

    private static ValidatedConfigLayer[] SixLayersWithValuePerLayer(params int[] values)
    {
        var layers = new ValidatedConfigLayer[values.Length];
        var kinds = new[]
        {
            ConfigLayer.Engine,
            ConfigLayer.Platform,
            ConfigLayer.Server,
            ConfigLayer.Product,
            ConfigLayer.Environment,
            ConfigLayer.UserOrSession,
        };
        for (var index = 0; index < values.Length; index++)
        {
            layers[index] = new ValidatedConfigLayer(
                kinds[index],
                TableFactory.Artifact(
                    kinds[index],
                    TableFactory.Table(
                        "gameplay",
                        TableFactory.Cols(TableFactory.Column("v", "i32", required: true)),
                        TableFactory.Rows(
                            TableFactory.Row("k", "{\"v\":" + values[index] + "}")))));
        }

        return layers;
    }
}
