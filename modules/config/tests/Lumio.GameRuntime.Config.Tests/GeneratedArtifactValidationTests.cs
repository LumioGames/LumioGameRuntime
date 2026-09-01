using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Lumio.GameRuntime.Config;
using Lumio.Gen.ContractTypes;
using Xunit;

namespace Lumio.GameRuntime.Config.Tests;

/// <summary>
/// T06.S01 / S04 / S06: generated artifact validation and public-surface constraints.
/// Row values are toolchain canonical JSON (<see cref="OpaqueJson"/>), not human source.
/// Validator applies generated <see cref="ConfigTable"/> column metadata; it does not
/// invent defaults or import. Ref cells resolve to a row key of RefTarget within the
/// same artifact (ADR-033 missing-ref); cross-layer refs are not resolved here.
/// </summary>
public sealed class GeneratedArtifactValidationTests
{
    private static readonly string[] Grades = { "common", "rare" };

    [Fact]
    public void RuntimeConfigSurfaceHasNoCompileApi()
    {
        var offenders = typeof(ConfigLayer).Assembly.ExportedTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(method =>
                method.Name.Contains("Compile", StringComparison.OrdinalIgnoreCase) ||
                method.GetParameters().Any(IsSourceMaterialParameter))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void PublicSurfaceDoesNotExportNewValidationIssueCodes()
    {
        Assert.DoesNotContain(
            typeof(ConfigLayer).Assembly.ExportedTypes,
            type => type.Name is "ConfigValidationIssueCode" or "ConfigValidationIssue");
    }

    [Fact]
    public void ArtifactPortSubmitAcceptsGeneratedViewOnly()
    {
        var method = typeof(IGeneratedConfigArtifactPort).GetMethod(nameof(IGeneratedConfigArtifactPort.Submit));
        Assert.NotNull(method);
        var parameter = Assert.Single(method!.GetParameters());
        var parameterType = parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()
            : parameter.ParameterType;
        Assert.Equal(typeof(GeneratedConfigArtifactView), parameterType);
        Assert.False(
            string.Equals(parameter.Name, "path", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Name, "source", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Name, "stream", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidArtifactValidatesWithNoIssues()
    {
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "gameplay",
                TableFactory.Cols(
                    TableFactory.Column("name", "string", required: true),
                    TableFactory.Column("speed", "i32", required: true, minimum: 0, maximum: 100)),
                TableFactory.Rows(
                    TableFactory.Row("k1", "{\"name\":\"bolt\",\"speed\":7}"),
                    TableFactory.Row("k2", "{\"name\":\"anchor\",\"speed\":0}"))));

        var report = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void AllIssuesAreReportedTogetherInDeterministicOrder()
    {
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("count", "i32", required: true, minimum: 0, maximum: 10),
                    TableFactory.Column("tag", "string", required: false),
                    TableFactory.Column("label", "string", required: true)),
                TableFactory.Rows(
                    TableFactory.Row("a", "{\"unknown_col\":1,\"count\":\"not_a_number\"}"))));

        var report = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);

        Assert.False(report.IsValid);
        var codes = report.Issues.Select(issue => issue.Code).ToArray();
        Assert.Contains(ConfigValidationIssueCode.UnknownColumn, codes);
        Assert.Contains(ConfigValidationIssueCode.MissingRequiredColumn, codes);
        Assert.Contains(ConfigValidationIssueCode.TypeMismatch, codes);
        var ordered = report.Issues
            .OrderBy(issue => issue.TableId, StringComparer.Ordinal)
            .ThenBy(issue => issue.RowKey, StringComparer.Ordinal)
            .ThenBy(issue => issue.Column, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code)
            .ToArray();
        Assert.Equal(ordered, report.Issues);
    }

    [Fact]
    public void RangeAndEnumViolationsAreRejected()
    {
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("speed", "i32", required: true, minimum: 0, maximum: 10),
                    TableFactory.Column("grade", "enum", required: true, enumValues: Grades)),
                TableFactory.Rows(
                    TableFactory.Row("k", "{\"speed\":11,\"grade\":\"legendary\"}"))));

        var report = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);

        Assert.False(report.IsValid);
        Assert.Contains(
            report.Issues,
            issue => issue.Code == ConfigValidationIssueCode.RangeViolation && issue.Column == "speed");
        Assert.Contains(
            report.Issues,
            issue => issue.Code == ConfigValidationIssueCode.EnumValueNotAllowed && issue.Column == "grade");
    }

    [Fact]
    public void DuplicateRowKeysAndDuplicateJsonMembersAreRejected()
    {
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("v", "i32", required: true)),
                TableFactory.Rows(
                    TableFactory.Row("dup", "{\"v\":1}"),
                    TableFactory.Row("dup", "{\"v\":2,\"v\":3}"))));

        var report = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == ConfigValidationIssueCode.DuplicateRowKey);
        Assert.Contains(report.Issues, issue => issue.Code == ConfigValidationIssueCode.DuplicateJsonMember);
    }

    [Fact]
    public void UnsignedIntegerTypesRejectNegativeAndOverflowValues()
    {
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("u", "u32", required: true)),
                TableFactory.Rows(
                    TableFactory.Row("neg", "{\"u\":-1}"),
                    TableFactory.Row("over", "{\"u\":4294967296}"))));

        var report = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);

        Assert.False(report.IsValid);
        Assert.Equal(2, report.Issues.Count(issue => issue.Code == ConfigValidationIssueCode.TypeMismatch));
    }

    [Fact]
    public void RefColumnsRequireDeclaredAndKnownRefTarget()
    {
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("reward", "ref", required: true, refTarget: "items")),
                TableFactory.Rows(
                    TableFactory.Row("orphan", "{\"reward\":\"sword\"}"))),
            TableFactory.Table(
                "items",
                TableFactory.Cols(
                    TableFactory.Column("name", "string", required: true)),
                TableFactory.Rows(
                    TableFactory.Row("sword", "{\"name\":\"sword\"}"))));
        var noTargetArtifact = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("reward", "ref", required: true, refTarget: "missing_table")),
                TableFactory.Rows(
                    TableFactory.Row("k", "{\"reward\":\"x\"}"))));

        var validReport = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);
        var missingTargetReport = new GeneratedConfigValidator().Validate(noTargetArtifact, ConfigValidationLimits.Default);

        Assert.True(validReport.IsValid);
        Assert.Contains(
            missingTargetReport.Issues,
            issue => issue.Code == ConfigValidationIssueCode.RefTargetTableUnknown);
    }

    [Fact]
    public void RefCellMustResolveToARowKeyInTheRefTargetTable()
    {
        // Homomorphic to architecture fixture config/missing-ref: table build-costs
        // declares next: ref → build-costs; row stone.next = "obsidian"; no obsidian row.
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "build-costs",
                TableFactory.Cols(
                    TableFactory.Column("next", "ref", required: true, refTarget: "build-costs")),
                TableFactory.Rows(
                    TableFactory.Row("stone", "{\"next\":\"obsidian\"}"))));

        var report = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);

        Assert.False(report.IsValid);
        Assert.Contains(
            report.Issues,
            issue => issue.Code == ConfigValidationIssueCode.MissingRef
                && issue.TableId == "build-costs"
                && issue.RowKey == "stone"
                && issue.Column == "next");
    }

    [Fact]
    public void EnumColumnDeclarationRequiresEnumValues()
    {
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("grade", "enum", required: true)),
                TableFactory.Rows(
                    TableFactory.Row("k", "{\"grade\":\"common\"}"))));

        var report = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == ConfigValidationIssueCode.ArtifactDeclarationInvalid);
    }

    [Fact]
    public void ProductionSignedSwitchRequiresSignature()
    {
        var production = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("v", "i32", required: true)),
                TableFactory.Rows(
                    TableFactory.Row("k", "{\"v\":1}")),
                "ProductionSignedSwitch"));
        var development = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("v", "i32", required: true)),
                TableFactory.Rows(
                    TableFactory.Row("k", "{\"v\":1}"))));

        var productionReport = new GeneratedConfigValidator().Validate(production, ConfigValidationLimits.Default);
        var developmentReport = new GeneratedConfigValidator().Validate(development, ConfigValidationLimits.Default);

        Assert.Contains(productionReport.Issues, issue => issue.Code == ConfigValidationIssueCode.SignatureMissing);
        Assert.True(developmentReport.IsValid);
    }

    [Fact]
    public void SizeLimitsAreEnforced()
    {
        var artifact = TableFactory.Artifact(
            TableFactory.Table(
                "t",
                TableFactory.Cols(
                    TableFactory.Column("v", "i32", required: true)),
                TableFactory.Rows(
                    TableFactory.Row("k0", "{\"v\":1}"),
                    TableFactory.Row("k1", "{\"v\":1}"),
                    TableFactory.Row("k2", "{\"v\":1}"),
                    TableFactory.Row("k3", "{\"v\":1}"))));

        var report = new GeneratedConfigValidator().Validate(
            artifact,
            new ConfigValidationLimits(8, 3, 16, 4096, 65536));

        Assert.Contains(report.Issues, issue => issue.Code == ConfigValidationIssueCode.SizeLimitExceeded);
    }

    [Fact]
    public void MalformedSourceHashIsRejected()
    {
        var artifact = TableFactory.Artifact(
            new ConfigTable(
                "t",
                1UL,
                1UL,
                "2026-01-01T00:00:00Z",
                "not-a-sha256",
                TableFactory.Cols(TableFactory.Column("v", "i32", required: true)),
                TableFactory.Rows(TableFactory.Row("k", "{\"v\":1}")),
                ConfigTableActivation.DevelopmentHotLoad,
                null));

        var report = new GeneratedConfigValidator().Validate(artifact, ConfigValidationLimits.Default);

        Assert.Contains(report.Issues, issue => issue.Code == ConfigValidationIssueCode.SourceHashMalformed);
    }

    private static bool IsSourceMaterialParameter(ParameterInfo parameter)
    {
        var name = parameter.Name ?? string.Empty;
        return parameter.ParameterType == typeof(Stream)
            || parameter.ParameterType == typeof(TextReader)
            || name.Contains("path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("source", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Test-only constructors for generated <see cref="ConfigTable"/> DTOs.</summary>
internal static class TableFactory
{
    internal static GeneratedConfigArtifactView Artifact(ConfigLayer layer, params ConfigTable[] tables) =>
        new("artifact-" + layer, layer, tables);

    internal static GeneratedConfigArtifactView Artifact(params ConfigTable[] tables) =>
        new("artifact-test", ConfigLayer.Engine, tables);

    internal static ConfigTableColumnsItem[] Cols(params ConfigTableColumnsItem[] columns) => columns;

    internal static ConfigTableRowsItem[] Rows(params ConfigTableRowsItem[] rows) => rows;

    internal static ConfigTable Table(
        string tableId,
        ConfigTableColumnsItem[] columns,
        ConfigTableRowsItem[] rows,
        string activation = "DevelopmentHotLoad",
        string? signature = null) =>
        new(
            tableId,
            1UL,
            1UL,
            "2026-01-01T00:00:00Z",
            new string('a', 64),
            columns,
            rows,
            ParseActivation(activation),
            signature);

    internal static ConfigTable Table(string tableId, params ConfigTableColumnsItem[] columns) =>
        Table(tableId, columns, Array.Empty<ConfigTableRowsItem>());

    internal static ConfigTableColumnsItem Column(
        string name,
        string type,
        bool required,
        double? minimum = null,
        double? maximum = null,
        string[]? enumValues = null,
        string? refTarget = null) =>
        new(name, ParseType(type), required, minimum, maximum, enumValues, refTarget, null);

    internal static ConfigTableRowsItem Row(string key, string canonicalValuesJson) =>
        new(key, new OpaqueJson(canonicalValuesJson));

    internal static ConfigTableActivation ParseActivation(string value) =>
        value switch
        {
            "ProductionSignedSwitch" => ConfigTableActivation.ProductionSignedSwitch,
            _ => ConfigTableActivation.DevelopmentHotLoad,
        };

    private static ConfigTableColumnsItemType ParseType(string value) =>
        value switch
        {
            "bool" => ConfigTableColumnsItemType.Bool,
            "i32" => ConfigTableColumnsItemType.I32,
            "i64" => ConfigTableColumnsItemType.I64,
            "u32" => ConfigTableColumnsItemType.U32,
            "u64" => ConfigTableColumnsItemType.U64,
            "f32" => ConfigTableColumnsItemType.F32,
            "f64" => ConfigTableColumnsItemType.F64,
            "string" => ConfigTableColumnsItemType.String,
            "enum" => ConfigTableColumnsItemType.Enum,
            "ref" => ConfigTableColumnsItemType.Ref,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown generated column type"),
        };
}
