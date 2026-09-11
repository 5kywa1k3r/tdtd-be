using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

internal static class DynamicFormTableStatisticConfigContractTests
{
    private static readonly string EmptyHash =
        StatConfigCanonicalJson.EmptyConfigHash;

    public static void Run()
    {
        StrictPayloadUsesOnlyStableTableIdentities();
        ExactTableModeTypeAndOperationMatrixHasNoFallback();
        ReadbackAndVersionSnapshotsAreTypedAndPinned();
        PublishedStructureStripsOnlyMutableTableStatisticMetadata();
        CanonicalOwnerLegacyAndIsolationBoundariesAreExplicit();
        AlternateWritersCannotBypassTheCanonicalStatisticPatch();
        StableTableStatisticErrorsKeepExactStatusClasses();
    }

    private static void StrictPayloadUsesOnlyStableTableIdentities()
    {
        using var valid = JsonDocument.Parse(
            "{\"commandId\":\"table-stat-1\",\"expectedRevision\":0," +
            "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
            "\"payload\":{\"tables\":[{" +
            "\"blockId\":\"revenue\",\"tableMode\":\"FIXED_GRID\"," +
            "\"statisticsDisabled\":false," +
            "\"metrics\":[{\"metricKey\":\"amount\"," +
            "\"dataType\":\"NUMBER\"," +
            "\"aggregateOps\":[\"COUNT\",\"AVERAGE\"]}]," +
            "\"metricLabelTargets\":[{\"metricKey\":\"amount\"," +
            "\"statisticLabelCode\":\"revenue\"}]," +
            "\"allowedRowLabelCodes\":[\"budget\"]}]}}" );

        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<DynamicFormStatisticConfigPayload>>(
            valid.RootElement);
        var table = envelope.Payload?.Tables?.Single();
        AssertEqual("revenue", table?.BlockId, "stable block identity");
        AssertEqual(
            "amount",
            table?.Metrics?.Single().MetricKey,
            "stable metric identity");
        AssertEqual(
            DynamicFormTableStatisticOperationContract.Average,
            table?.Metrics?.Single().AggregateOps?[1],
            "explicit table operation");

        foreach (var json in new[]
                 {
                     ValidTableEnvelope(
                         "\"columnIndex\":0,"),
                     ValidTableEnvelope(
                         metricExtra: "\"index\":0,"),
                     ValidTableEnvelope(
                         labelExtra:
                         "\"range\":{\"r0\":0,\"c0\":0," +
                         "\"r1\":0,\"c1\":0},"),
                     "{\"commandId\":\"x\",\"expectedRevision\":0," +
                     "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
                     "\"payload\":{\"tables\":[],\"blocksJson\":\"[]\"}}"
                 })
        {
            using var unknown = JsonDocument.Parse(json);
            ExpectError(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                () => StatConfigCanonicalJson.DeserializeStrict<
                    StatConfigMutationEnvelope<
                        DynamicFormStatisticConfigPayload>>(
                    unknown.RootElement));
        }

        AssertFalse(
            typeof(DynamicFormStatisticTablePayload)
                .GetProperties()
                .Any(property => property.Name is
                    "Index" or "ColumnIndex" or "RowKey" or
                    "ColumnKey" or "Range"),
            "new P8 table payload must not expose positional identity");
    }

    private static string ValidTableEnvelope(
        string blockExtra = "",
        string metricExtra = "",
        string labelExtra = "")
        => "{\"commandId\":\"x\",\"expectedRevision\":0," +
           "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
           "\"payload\":{\"tables\":[{" + blockExtra +
           "\"blockId\":\"b\",\"tableMode\":\"FIXED_GRID\"," +
           "\"statisticsDisabled\":false,\"metrics\":[{" +
           metricExtra + "\"metricKey\":\"m\"," +
           "\"dataType\":\"NUMBER\",\"aggregateOps\":[\"COUNT\"]}]," +
           "\"metricLabelTargets\":[{" + labelExtra +
           "\"metricKey\":\"m\",\"statisticLabelCode\":\"metric\"}]," +
           "\"allowedRowLabelCodes\":[]}]}}";

    private static void ExactTableModeTypeAndOperationMatrixHasNoFallback()
    {
        AssertSetEqual(
            ["FIXED_GRID", "APPEND_ROWS", "APPEND_COLUMNS", "MATRIX"],
            DynamicFormTableStatisticOperationContract.AllowedInputModes,
            "table input modes");
        AssertFalse(
            DynamicFormTableStatisticOperationContract.IsInputMode(
                DynamicFormTableStatisticOperationContract.SummaryTemplate),
            "SUMMARY_TEMPLATE is output-only");

        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [DynamicFormTableStatisticOperationContract.Number] =
                ["COUNT", "SUM", "MIN", "MAX", "AVERAGE"],
            [DynamicFormTableStatisticOperationContract.ShortText] =
                ["COUNT", "BUCKET_COUNT"],
            [DynamicFormTableStatisticOperationContract.MultiSelect] =
                ["COUNT", "BUCKET_COUNT"],
            [DynamicFormTableStatisticOperationContract.Boolean] =
                ["COUNT", "TRUE_COUNT", "FALSE_COUNT"],
            [DynamicFormTableStatisticOperationContract.Date] =
                ["COUNT", "EARLIEST", "LATEST"],
            [DynamicFormTableStatisticOperationContract.FullDate] =
                ["COUNT", "EARLIEST", "LATEST"]
        };

        AssertEqual(
            expected.Count,
            DynamicFormTableStatisticOperationContract
                .AllowedOperationsByDataType.Count,
            "supported table datatype count");
        foreach (var (dataType, operations) in expected)
        {
            AssertSetEqual(
                operations,
                DynamicFormTableStatisticOperationContract
                    .AllowedOperationsByDataType[dataType],
                $"operations for table datatype {dataType}");
        }

        foreach (var forbiddenType in new[]
                 {
                     DynamicFormTableStatisticOperationContract.Ignore,
                     DynamicFormStatisticFieldTypes.StringList,
                     DynamicFormStatisticFieldTypes.LongText
                 })
        {
            AssertFalse(
                DynamicFormTableStatisticOperationContract
                    .IsSupportedDataType(forbiddenType),
                $"unsupported table datatype {forbiddenType}");
        }

        foreach (var (dataType, operation) in new[]
                 {
                     ("NUMBER", "AVG"),
                     ("NUMBER", "LATEST"),
                     ("NUMBER", "PERIOD_SUM"),
                     ("NUMBER", "CUMULATIVE_SUM"),
                     ("SHORT_TEXT", "CONCAT"),
                     ("DATE", "MIN")
                 })
        {
            AssertFalse(
                DynamicFormTableStatisticOperationContract
                    .IsOperationAllowed(dataType, operation),
                $"forbidden table operation {dataType}/{operation}");
        }

        AssertEqual(
            30,
            DynamicFormTableStatisticOperationContract.MaximumBlocks,
            "30/31 table block boundary");
        AssertEqual(
            30,
            DynamicFormTableStatisticOperationContract.MaximumTargets,
            "30/31 table metric boundary");
    }

    private static void ReadbackAndVersionSnapshotsAreTypedAndPinned()
    {
        AssertEqual(
            typeof(IReadOnlyList<DynamicFormStatisticTableConfigDto>),
            typeof(DynamicFormStatisticConfigResult)
                .GetProperty("TableConfig")?.PropertyType,
            "typed current table readback");
        AssertEqual(
            typeof(IReadOnlyList<DynamicFormStatisticTableConfigDto>),
            typeof(DynamicFormStatisticConfigVersionSnapshotDto)
                .GetProperty("TableConfig")?.PropertyType,
            "typed version table readback");

        foreach (var property in new[]
                 {
                     "BlockId",
                     "TableMode",
                     "StatisticsDisabled",
                     "StatisticsInputCellCount",
                     "StatisticsInputCellLimit",
                     "StatisticsDisabledReason",
                     "RowLabelDataType",
                     "Metrics",
                     "MetricLabelTargets",
                     "AllowedRowLabelCodes",
                     "RowLabelSnapshots",
                     "StructureHash",
                     "SchemaSource"
                 })
        {
            AssertTrue(
                typeof(DynamicFormStatisticTableConfigDto)
                    .GetProperty(property) is not null,
                $"table readback property {property}");
        }

        AssertEqual(
            typeof(DynamicFormStatisticLabelSnapshotDto),
            typeof(DynamicFormStatisticMetricLabelTargetConfigDto)
                .GetProperty("LabelSnapshot")?.PropertyType,
            "metric label carries one pinned snapshot");
        AssertEqual(
            typeof(IReadOnlyList<DynamicFormStatisticLabelSnapshotDto>),
            typeof(DynamicFormStatisticTableConfigDto)
                .GetProperty("RowLabelSnapshots")?.PropertyType,
            "row allowlist carries pinned snapshots");
        AssertTrue(
            typeof(DynamicFormStatisticConfigResult)
                .GetProperty("TableSectionHash") is not null,
            "current table section hash");
        AssertTrue(
            typeof(DynamicFormStatisticConfigVersionSnapshotDto)
                .GetProperty("TableSectionHash") is not null,
            "version table section hash");
    }

    private static void
        PublishedStructureStripsOnlyMutableTableStatisticMetadata()
    {
        const string blocks =
            "[{\"blockId\":\"b\",\"tableMode\":\"FIXED_GRID\"," +
            "\"statisticsDisabled\":true," +
            "\"statisticsDisabledReason\":\"threshold\"," +
            "\"statisticsInputCellCount\":251," +
            "\"statisticsInputCellLimit\":250," +
            "\"rowLabelDataType\":\"NUMBER\"," +
            "\"allowedRowLabelCodes\":[\"budget\"]," +
            "\"metricLabelTargets\":[{\"metricKey\":\"m\"," +
            "\"statisticLabelCode\":\"revenue\"}]," +
            "\"metricRules\":[{\"metricKey\":\"m\"," +
            "\"sourceType\":\"TABLE_CELL\",\"rowKey\":\"r1\"," +
            "\"columnKey\":\"c1\",\"dataType\":\"NUMBER\"," +
            "\"aggregateOps\":[\"SUM\"]}]}]";

        var projection =
            DynamicFormPublishedSchemaSnapshotBuilder
                .CanonicalizeStructureForComparison(
                    blocks,
                    stripStatisticConfig: true,
                    removeFieldStatisticLabels: false);
        using var document = JsonDocument.Parse(projection);
        var block = document.RootElement[0];
        foreach (var property in new[]
                 {
                     "metricLabelTargets",
                     "allowedRowLabelCodes",
                     "statisticsDisabled",
                     "statisticsDisabledReason"
                 })
        {
            AssertFalse(
                block.TryGetProperty(property, out _),
                $"mutable table property stripped: {property}");
        }

        AssertEqual(
            251,
            block.GetProperty("statisticsInputCellCount").GetInt32(),
            "input count remains structural");
        AssertEqual(
            250,
            block.GetProperty("statisticsInputCellLimit").GetInt32(),
            "input limit remains structural");
        AssertEqual(
            "FIXED_GRID",
            block.GetProperty("tableMode").GetString(),
            "table mode remains structural");

        var rule = block.GetProperty("metricRules")[0];
        AssertFalse(
            rule.TryGetProperty("aggregateOps", out _),
            "only metric operation metadata is stripped from rule");
        foreach (var property in new[]
                 {
                     "metricKey",
                     "sourceType",
                     "rowKey",
                     "columnKey",
                     "dataType"
                 })
        {
            AssertTrue(
                rule.TryGetProperty(property, out _),
                $"metric structure retained: {property}");
        }
    }

    private static void
        CanonicalOwnerLegacyAndIsolationBoundariesAreExplicit()
    {
        var commandMain = ReadBackendSource(
            "Services/DynamicForms/" +
            "DynamicFormStatisticConfigCommandService.cs");
        var commandTables = ReadBackendSource(
            "Services/DynamicForms/DynamicFormStatisticConfigCommandService.P803.Tables.cs");
        var command = commandMain + Environment.NewLine + commandTables;

        foreach (var required in new[]
                 {
                     "TABLE_AND_FIELD_MUTATIONS_CONFLICT",
                     "TABLE_STATISTIC_TARGET_LIMIT_30",
                     "TABLE_BLOCK_LIMIT_30",
                     "SUMMARY_TEMPLATE_STATISTIC_INPUT_FORBIDDEN",
                     "TABLE_STATISTICS_DISABLED_REACTIVATION_FORBIDDEN",
                     "LEGACY_TABLE_STATISTIC_CONFIG_READ_ONLY",
                     "owner.BlocksJson = next.BlocksJson;",
                     "StatConfigIsolationGuard.EnterConfigurationMutation"
                 })
        {
            AssertContains(command, required, $"table command guard {required}");
        }

        AssertNotContains(
            commandTables,
            "LabelUsages.Statistic",
            "table metric labels never use the field-statistic usage");
        AssertMatches(
            commandTables,
            @"ResolveTableLabelSnapshot\(\s*labels,\s*" +
            @"target\.StatisticLabelCode,\s*metric\.DataType,\s*" +
            @"LabelUsages\.TableTarget,\s*""TABLE_METRIC_LABEL""",
            "table metric label resolver uses TABLE_TARGET");
        AssertMatches(
            commandTables,
            @"target\.LabelSnapshot\.Usage,\s*" +
            @"LabelUsages\.TableTarget,\s*StringComparison\.Ordinal",
            "persisted table metric label validator pins TABLE_TARGET");

        AssertNotContains(
            command,
            "owner.ExcelBlockJson = next.ExcelBlockJson;",
            "legacy ExcelBlockJson remains read-only");
        foreach (var forbidden in new[]
                 {
                     "WorkReportStatisticRebuildJob",
                     "WorkReportFieldStatValues",
                     "WorkReportFieldStatAggregates",
                     "WorkReportTableStatValues",
                     "WorkReportTableStatAggregates",
                     "WorkReportLabelStatValues",
                     "WorkReportLabelStatAggregates"
                 })
        {
            AssertNotContains(
                command,
                forbidden,
                $"P8-03 must not reference {forbidden}");
        }
    }

    private static void
        AlternateWritersCannotBypassTheCanonicalStatisticPatch()
    {
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        foreach (var required in new[]
                 {
                     "EnsureNoAlternateTableStatisticCreate",
                     "EnsureNoAlternateTableStatisticUpdate",
                     "metricRules",
                     "metricLabelTargets",
                     "allowedRowLabelCodes",
                     "statisticsDisabled",
                     "USE_CANONICAL_STATISTICS_PATCH",
                     "CONFIGURED_STATISTIC_TARGET_STRUCTURE_IMMUTABLE"
                 })
        {
            AssertContains(service, required, $"alternate table writer guard {required}");
        }

        AssertOccurrenceCountAtLeast(
            service,
            "EnsureNoAlternateTableStatisticCreate(",
            2,
            "alternate table create guard is declared and invoked");
        AssertOccurrenceCountAtLeast(
            service,
            "EnsureNoAlternateTableStatisticUpdate(",
            2,
            "alternate table update guard is declared and invoked");
    }

    private static void StableTableStatisticErrorsKeepExactStatusClasses()
    {
        foreach (var code in new[]
                 {
                     AppErrorCode.DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID,
                     AppErrorCode.DYNAMIC_FORM_STATISTIC_OPERATION_INVALID,
                     AppErrorCode.DYNAMIC_FORM_STATISTIC_TARGET_LIMIT_EXCEEDED,
                     AppErrorCode.DYNAMIC_FORM_TABLE_MODE_INVALID,
                     AppErrorCode.DYNAMIC_FORM_TABLE_MODE_MISMATCH,
                     AppErrorCode.DYNAMIC_FORM_METRIC_KEY_INVALID,
                     AppErrorCode.DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID
                 })
        {
            AssertEqual(
                StatusCodes.Status400BadRequest,
                AppErrorCatalog.Get(code).HttpStatus,
                $"{code} status");
        }

        AssertEqual(
            StatusCodes.Status409Conflict,
            AppErrorCatalog.Get(AppErrorCode.STAT_CONFIG_CAS_CONFLICT)
                .HttpStatus,
            "persisted table drift/disabled reactivation conflict status");
    }

    private static AppException ExpectError(
        AppErrorCode expected,
        Action action)
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            AssertEqual(expected, error.Code, "stable error code");
            return error;
        }

        throw new InvalidOperationException(
            $"Expected {expected}, but no AppException was thrown.");
    }

    private static string ReadBackendSource(string relativePath)
    {
        var root = FindBackendRoot();
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Backend source file was not found: {path}");
        }
        return File.ReadAllText(path);
    }

    private static string FindBackendRoot()
    {
        var seeds = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            for (var directory = new DirectoryInfo(seed);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(
                        Path.Combine(directory.FullName, "tdtd-be.csproj")))
                {
                    return directory.FullName;
                }
                var nested = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nested, "tdtd-be.csproj")))
                {
                    return nested;
                }
            }
        }
        throw new InvalidOperationException(
            "Could not locate the tdtd-be source root.");
    }

    private static void AssertSetEqual(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string context)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        if (!expectedSet.SetEquals(actualSet))
        {
            throw new InvalidOperationException(
                $"{context}: expected [{string.Join(',', expectedSet)}], " +
                $"got [{string.Join(',', actualSet)}].");
        }
    }

    private static void AssertContains(
        string source,
        string expected,
        string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{expected}'.");
        }
    }

    private static void AssertNotContains(
        string source,
        string forbidden,
        string context)
    {
        if (source.Contains(forbidden, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{context}: forbidden '{forbidden}'.");
        }
    }

    private static void AssertOccurrenceCountAtLeast(
        string source,
        string expected,
        int minimum,
        string context)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(
                   expected,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        if (count < minimum)
        {
            throw new InvalidOperationException(
                $"{context}: expected at least {minimum}, got {count}.");
        }
    }

    private static void AssertMatches(
        string source,
        string pattern,
        string context)
    {
        if (!Regex.IsMatch(
                source,
                pattern,
                RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                $"{context}: expected source pattern '{pattern}'.");
        }
    }

    private static void AssertTrue(bool condition, string context)
    {
        if (!condition)
            throw new InvalidOperationException($"{context}: expected true.");
    }

    private static void AssertFalse(bool condition, string context)
        => AssertTrue(!condition, context);

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{expected}', got '{actual}'.");
        }
    }
}
