using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumio.GameRuntime.Config;

/// <summary>Internal classification of generated-schema violations. Not a public contract code.</summary>
internal enum ConfigValidationIssueCode
{
    UnknownColumn,
    MissingRequiredColumn,
    TypeMismatch,
    RangeViolation,
    EnumValueNotAllowed,
    DuplicateRowKey,
    DuplicateTableId,
    DuplicateJsonMember,
    JsonMalformed,
    RefTargetMissing,
    RefTargetTableUnknown,
    ArtifactDeclarationInvalid,
    SignatureMissing,
    SourceHashMalformed,
    SizeLimitExceeded,
    ArtifactMalformed,
}

internal sealed record ConfigValidationIssue(
    ConfigValidationIssueCode Code,
    string TableId,
    string RowKey,
    string Column,
    string Detail);

internal sealed class ConfigValidationReport
{
    public IReadOnlyList<ConfigValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;

    private ConfigValidationReport(IReadOnlyList<ConfigValidationIssue> issues)
    {
        Issues = issues;
    }

    public static ConfigValidationReport Valid() =>
        new(Array.Empty<ConfigValidationIssue>());

    public static ConfigValidationReport From(IEnumerable<ConfigValidationIssue> issues) =>
        new(issues
            .OrderBy(issue => issue.TableId, StringComparer.Ordinal)
            .ThenBy(issue => issue.RowKey, StringComparer.Ordinal)
            .ThenBy(issue => issue.Column, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code)
            .ToArray());
}

internal readonly record struct ConfigValidationLimits(
    int MaxTablesPerArtifact,
    int MaxRowsPerTable,
    int MaxColumnsPerTable,
    int MaxRowValueBytes,
    long MaxArtifactRowBytesTotal)
{
    public static ConfigValidationLimits Default => new(1024, 1_048_576, 1024, 65_536, 67_108_864L);
}
