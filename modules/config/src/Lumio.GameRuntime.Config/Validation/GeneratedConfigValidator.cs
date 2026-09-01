using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumio.Gen.ContractTypes;

namespace Lumio.GameRuntime.Config;

/// <summary>
/// Validates a generated config artifact against generated <see cref="ConfigTable"/>
/// column metadata (type, required, min/max, enumValues, refTarget, activation).
/// Returns every issue; never swallows failures into a valid report.
/// Canonical JSON rules come from generated <c>CanonicalForm</c> constants.
/// </summary>
internal sealed class GeneratedConfigValidator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822",
        Justification = "Instance form is the module contract (worker-safe, no static global cache).")]
    public ConfigValidationReport Validate(in GeneratedConfigArtifactView artifact, in ConfigValidationLimits limits)
    {
        var issues = new List<ConfigValidationIssue>();
        if (!artifact.IsWellFormed)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.ArtifactMalformed,
                string.Empty,
                string.Empty,
                string.Empty,
                "artifact id must be non-empty and at least one table must be present"));
            return ConfigValidationReport.From(issues);
        }

        if (artifact.Tables.Count > limits.MaxTablesPerArtifact)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.SizeLimitExceeded,
                string.Empty,
                string.Empty,
                string.Empty,
                $"table count {artifact.Tables.Count} exceeds MaxTablesPerArtifact {limits.MaxTablesPerArtifact}"));
        }

        var tableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in artifact.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.TableId))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.ArtifactMalformed,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "tableId must be non-empty"));
                continue;
            }

            if (!tableIds.Add(table.TableId))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.DuplicateTableId,
                    table.TableId,
                    string.Empty,
                    string.Empty,
                    "tableId declared twice in one artifact"));
            }
        }

        var artifactRowBytesTotal = 0L;
        foreach (var table in artifact.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.TableId))
            {
                continue;
            }

            ValidateTableDeclarations(table, tableIds, limits, issues);
            if (table.Rows.Count > limits.MaxRowsPerTable)
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.SizeLimitExceeded,
                    table.TableId,
                    string.Empty,
                    string.Empty,
                    $"row count {table.Rows.Count} exceeds MaxRowsPerTable {limits.MaxRowsPerTable}"));
            }

            var rowKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in table.Rows)
            {
                ValidateRow(table, row, rowKeys, limits, issues, ref artifactRowBytesTotal);
            }
        }

        return ConfigValidationReport.From(issues);
    }

    private static void ValidateTableDeclarations(
        ConfigTable table,
        HashSet<string> tableIds,
        ConfigValidationLimits limits,
        List<ConfigValidationIssue> issues)
    {
        if (table.Columns.Count > limits.MaxColumnsPerTable)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.SizeLimitExceeded,
                table.TableId,
                string.Empty,
                string.Empty,
                $"column count {table.Columns.Count} exceeds MaxColumnsPerTable {limits.MaxColumnsPerTable}"));
        }

        var columnNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in table.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.Name))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.ArtifactDeclarationInvalid,
                    table.TableId,
                    string.Empty,
                    column.Name,
                    "column name must be non-empty"));
                continue;
            }

            if (!columnNames.Add(column.Name))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.ArtifactDeclarationInvalid,
                    table.TableId,
                    string.Empty,
                    column.Name,
                    "column declared twice in one table"));
            }

            if (column.Type == ConfigTableColumnsItemType.Enum &&
                (column.EnumValues is not { Count: > 0 }))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.ArtifactDeclarationInvalid,
                    table.TableId,
                    string.Empty,
                    column.Name,
                    "enum column must declare a non-empty enumValues set"));
            }

            if (column.Type == ConfigTableColumnsItemType.Ref &&
                string.IsNullOrWhiteSpace(column.RefTarget))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.RefTargetMissing,
                    table.TableId,
                    string.Empty,
                    column.Name,
                    "ref column must declare RefTarget"));
            }

            if ((column.Minimum is not null || column.Maximum is not null) &&
                !IsNumericType(column.Type))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.ArtifactDeclarationInvalid,
                    table.TableId,
                    string.Empty,
                    column.Name,
                    "minimum/maximum are only valid on numeric column types"));
            }

            if (column.RefTarget is not null &&
                !tableIds.Contains(column.RefTarget))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.RefTargetTableUnknown,
                    table.TableId,
                    string.Empty,
                    column.Name,
                    $"RefTarget '{column.RefTarget}' does not name a table in the same artifact"));
            }
        }

        if (!IsSha256Hex(table.SourceHash))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.SourceHashMalformed,
                table.TableId,
                string.Empty,
                string.Empty,
                "sourceHash must be a 64-character lowercase hex SHA-256"));
        }

        if (table.Activation == ConfigTableActivation.ProductionSignedSwitch &&
            string.IsNullOrWhiteSpace(table.Signature))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.SignatureMissing,
                table.TableId,
                string.Empty,
                string.Empty,
                "ProductionSignedSwitch activation requires a signature"));
        }
    }

    private static void ValidateRow(
        ConfigTable table,
        ConfigTableRowsItem row,
        HashSet<string> rowKeys,
        ConfigValidationLimits limits,
        List<ConfigValidationIssue> issues,
        ref long artifactRowBytesTotal)
    {
        if (string.IsNullOrWhiteSpace(row.Key))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.ArtifactMalformed,
                table.TableId,
                row.Key,
                string.Empty,
                "row key must be non-empty"));
            return;
        }

        if (!rowKeys.Add(row.Key))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.DuplicateRowKey,
                table.TableId,
                row.Key,
                string.Empty,
                "row key declared twice in one table"));
        }

        var rowBytes = Encoding.UTF8.GetByteCount(row.Values.Json);
        if (rowBytes > limits.MaxRowValueBytes)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.SizeLimitExceeded,
                table.TableId,
                row.Key,
                string.Empty,
                $"row values bytes {rowBytes} exceed MaxRowValueBytes {limits.MaxRowValueBytes}"));
        }

        artifactRowBytesTotal += rowBytes;
        if (artifactRowBytesTotal > limits.MaxArtifactRowBytesTotal)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.SizeLimitExceeded,
                table.TableId,
                row.Key,
                string.Empty,
                $"artifact row bytes total exceeds MaxArtifactRowBytesTotal {limits.MaxArtifactRowBytesTotal}"));
        }

        var parsed = CanonicalJson.Parse(row.Values.Json);
        if (!parsed.IsOk)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.JsonMalformed,
                table.TableId,
                row.Key,
                string.Empty,
                parsed.Error ?? "row values are not valid JSON"));
            return;
        }

        if (parsed.Value!.Kind != CanonicalJsonKind.Object)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.JsonMalformed,
                table.TableId,
                row.Key,
                string.Empty,
                "row values must be a JSON object mapping column names to values"));
            return;
        }

        foreach (var duplicate in parsed.DuplicateMembers)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.DuplicateJsonMember,
                table.TableId,
                row.Key,
                duplicate,
                "duplicate JSON member in row values"));
        }

        var members = parsed.Value.Members!;
        var presentColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members.Keys)
        {
            ConfigTableColumnsItem? column = null;
            foreach (var candidate in table.Columns)
            {
                if (string.Equals(candidate.Name, member, StringComparison.Ordinal))
                {
                    column = candidate;
                    break;
                }
            }

            if (column is null)
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.UnknownColumn,
                    table.TableId,
                    row.Key,
                    member,
                    "row value member does not match any declared column"));
                continue;
            }

            presentColumns.Add(member);
            ValidateMemberValue(table, row, column, members[member], issues);
        }

        foreach (var column in table.Columns)
        {
            if (column.Required && !presentColumns.Contains(column.Name))
            {
                issues.Add(new ConfigValidationIssue(
                    ConfigValidationIssueCode.MissingRequiredColumn,
                    table.TableId,
                    row.Key,
                    column.Name,
                    "required column absent from row values"));
            }
        }
    }

    private static void ValidateMemberValue(
        ConfigTable table,
        ConfigTableRowsItem row,
        ConfigTableColumnsItem column,
        CanonicalJsonValue value,
        List<ConfigValidationIssue> issues)
    {
        switch (column.Type)
        {
            case ConfigTableColumnsItemType.Bool:
                if (value.Kind is not (CanonicalJsonKind.True or CanonicalJsonKind.False))
                {
                    TypeMismatch(table, row, column, value, issues, "expected bool");
                }

                break;

            case ConfigTableColumnsItemType.String:
            case ConfigTableColumnsItemType.Ref:
                if (value.Kind != CanonicalJsonKind.String)
                {
                    TypeMismatch(table, row, column, value, issues, "expected string");
                }

                break;

            case ConfigTableColumnsItemType.Enum:
                if (value.Kind != CanonicalJsonKind.String)
                {
                    TypeMismatch(table, row, column, value, issues, "expected enum string");
                    break;
                }

                if (column.EnumValues is not null && !ContainsOrdinal(column.EnumValues, value.Text!))
                {
                    issues.Add(new ConfigValidationIssue(
                        ConfigValidationIssueCode.EnumValueNotAllowed,
                        table.TableId,
                        row.Key,
                        column.Name,
                        $"value '{value.Text}' is not in the declared enumValues"));
                }

                break;

            case ConfigTableColumnsItemType.I32:
            case ConfigTableColumnsItemType.I64:
            case ConfigTableColumnsItemType.U32:
            case ConfigTableColumnsItemType.U64:
            case ConfigTableColumnsItemType.F32:
            case ConfigTableColumnsItemType.F64:
                ValidateNumericValue(table, row, column, value, issues);
                break;

            default:
                TypeMismatch(table, row, column, value, issues, "unsupported column type");
                break;
        }
    }

    private static void ValidateNumericValue(
        ConfigTable table,
        ConfigTableRowsItem row,
        ConfigTableColumnsItem column,
        CanonicalJsonValue value,
        List<ConfigValidationIssue> issues)
    {
        if (value.Kind != CanonicalJsonKind.Number)
        {
            TypeMismatch(table, row, column, value, issues, "expected a JSON number");
            return;
        }

        var literal = value.Literal!;
        var isIntegerLiteral = literal.IndexOf('.') < 0 &&
            literal.IndexOf('e') < 0 &&
            literal.IndexOf('E') < 0;
        if (IsIntegerType(column.Type) && !isIntegerLiteral)
        {
            TypeMismatch(table, row, column, value, issues, "expected an integer literal");
            return;
        }

        if (!decimal.TryParse(literal, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric))
        {
            TypeMismatch(table, row, column, value, issues, "number literal out of representable range");
            return;
        }

        var inRange = column.Type switch
        {
            ConfigTableColumnsItemType.I32 => numeric >= int.MinValue && numeric <= int.MaxValue,
            ConfigTableColumnsItemType.I64 => numeric >= long.MinValue && numeric <= long.MaxValue,
            ConfigTableColumnsItemType.U32 => numeric >= 0m && numeric <= uint.MaxValue,
            ConfigTableColumnsItemType.U64 => numeric >= 0m && numeric <= ulong.MaxValue,
            _ => true,
        };
        if (!inRange)
        {
            TypeMismatch(table, row, column, value, issues, "value outside the generated integer type domain");
            return;
        }

        if (column.Minimum is not null && numeric < (decimal)column.Minimum)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.RangeViolation,
                table.TableId,
                row.Key,
                column.Name,
                $"value {literal} below declared minimum {column.Minimum}"));
        }

        if (column.Maximum is not null && numeric > (decimal)column.Maximum)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigValidationIssueCode.RangeViolation,
                table.TableId,
                row.Key,
                column.Name,
                $"value {literal} above declared maximum {column.Maximum}"));
        }
    }

    private static void TypeMismatch(
        ConfigTable table,
        ConfigTableRowsItem row,
        ConfigTableColumnsItem column,
        CanonicalJsonValue value,
        List<ConfigValidationIssue> issues,
        string expectation)
    {
        issues.Add(new ConfigValidationIssue(
            ConfigValidationIssueCode.TypeMismatch,
            table.TableId,
            row.Key,
            column.Name,
            $"{expectation}; got {Describe(value)}"));
    }

    private static string Describe(CanonicalJsonValue value) => value.Kind switch
    {
        CanonicalJsonKind.Object => "object",
        CanonicalJsonKind.Array => "array",
        CanonicalJsonKind.String => "string",
        CanonicalJsonKind.Number => $"number {value.Literal}",
        CanonicalJsonKind.True or CanonicalJsonKind.False => "bool",
        _ => "null",
    };

    private static bool IsNumericType(ConfigTableColumnsItemType type) =>
        type is ConfigTableColumnsItemType.I32
            or ConfigTableColumnsItemType.I64
            or ConfigTableColumnsItemType.U32
            or ConfigTableColumnsItemType.U64
            or ConfigTableColumnsItemType.F32
            or ConfigTableColumnsItemType.F64;

    private static bool IsIntegerType(ConfigTableColumnsItemType type) =>
        type is ConfigTableColumnsItemType.I32
            or ConfigTableColumnsItemType.I64
            or ConfigTableColumnsItemType.U32
            or ConfigTableColumnsItemType.U64;

    private static bool ContainsOrdinal(IReadOnlyList<string> values, string candidate)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isLowerHex = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f');
            if (!isLowerHex)
            {
                return false;
            }
        }

        return true;
    }
}
