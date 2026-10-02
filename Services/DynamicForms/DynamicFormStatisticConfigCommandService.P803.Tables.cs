using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

public sealed partial class DynamicFormStatisticConfigCommandService
{
    private const string CanonicalBlockSchemaSource = "BLOCKS_JSON";
    private const string LegacyBlockSchemaSource =
        "LEGACY_EXCEL_BLOCK_ADAPTER";
    private const string ConfigDisabledReason =
        "P8_STATISTIC_CONFIG_DISABLED";

    private static NormalizedMutationBatch NormalizeMutationSyntax(
        DynamicFormStatisticConfigPayload payload)
    {
        if (payload.NativeStatistics is not null)
        {
            if (payload.Fields is not null || payload.Tables is not null || payload.NativeTargets is not null)
                throw Schema("$.payload", "NATIVE_AND_LEGACY_MUTATIONS_CONFLICT");
            return new NormalizedMutationBatch(null, null, null, NormalizeNativeStatisticsSyntax(payload.NativeStatistics));
        }
        if (payload.NativeTargets is not null)
        {
            if (payload.Fields is not null || payload.Tables is not null)
                throw Schema("$.payload", "NATIVE_AND_LEGACY_MUTATIONS_CONFLICT");
            return new NormalizedMutationBatch(null, null, NormalizeNativeMutationSyntax(payload.NativeTargets));
        }
        var hasFields = payload.Fields is { Count: > 0 };
        var hasTables = payload.Tables is { Count: > 0 };
        if (hasFields && hasTables)
        {
            throw Schema(
                "$.payload",
                "TABLE_AND_FIELD_MUTATIONS_CONFLICT");
        }

        if (hasTables)
        {
            return new NormalizedMutationBatch(
                null,
                NormalizeTableMutationSyntax(payload.Tables!));
        }

        return new NormalizedMutationBatch(
            NormalizeFieldMutationSyntax(payload),
            null);
    }

    private static IReadOnlyList<NormalizedTableMutation>
        NormalizeTableMutationSyntax(
            IReadOnlyList<DynamicFormStatisticTablePayload> inputs)
    {
        var seenBlocks = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<NormalizedTableMutation>(inputs.Count);
        for (var blockIndex = 0;
             blockIndex < inputs.Count;
             blockIndex++)
        {
            var path = $"$.payload.tables[{blockIndex}]";
            var input = inputs[blockIndex];
            var blockId = NormalizeIdentity(
                input.BlockId,
                $"{path}.blockId",
                "BLOCK_ID_REQUIRED");
            if (!seenBlocks.Add(blockId))
            {
                throw Schema(
                    $"{path}.blockId",
                    "DUPLICATE_BLOCK_ID");
            }

            var tableMode =
                input.TableMode?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(tableMode))
            {
                throw TableModeError(
                    $"{path}.tableMode",
                    "TABLE_MODE_REQUIRED",
                    tableMode,
                    null);
            }
            if (!input.StatisticsDisabled.HasValue)
            {
                throw Schema(
                    $"{path}.statisticsDisabled",
                    "STATISTICS_DISABLED_REQUIRED");
            }
            if (input.Metrics is null)
            {
                throw Schema(
                    $"{path}.metrics",
                    "TABLE_METRICS_REQUIRED");
            }
            if (input.MetricLabelTargets is null)
            {
                throw Schema(
                    $"{path}.metricLabelTargets",
                    "METRIC_LABEL_TARGETS_REQUIRED");
            }
            if (input.AllowedRowLabelCodes is null)
            {
                throw Schema(
                    $"{path}.allowedRowLabelCodes",
                    "ALLOWED_ROW_LABEL_CODES_REQUIRED");
            }

            var metrics =
                NormalizeTableMetrics(input.Metrics, path);
            var targets = NormalizeMetricLabelTargets(
                input.MetricLabelTargets,
                path);
            var rowLabelCodes = NormalizeTableLabelCodeSet(
                input.AllowedRowLabelCodes,
                $"{path}.allowedRowLabelCodes",
                "DUPLICATE_ALLOWED_ROW_LABEL_CODE");
            result.Add(
                new NormalizedTableMutation(
                    blockId,
                    tableMode,
                    input.StatisticsDisabled.Value,
                    metrics,
                    targets,
                    rowLabelCodes,
                    blockIndex));
        }

        return result;
    }

    private static IReadOnlyList<NormalizedTableMetricMutation>
        NormalizeTableMetrics(
            IReadOnlyList<DynamicFormStatisticTableMetricPayload>
                inputs,
            string blockPath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result =
            new List<NormalizedTableMetricMutation>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var path = $"{blockPath}.metrics[{index}]";
            var input = inputs[index];
            var metricKey = NormalizeIdentity(
                input.MetricKey,
                $"{path}.metricKey",
                "METRIC_KEY_REQUIRED");
            if (!seen.Add(metricKey))
            {
                throw Schema(
                    $"{path}.metricKey",
                    "DUPLICATE_METRIC_KEY");
            }

            var dataType =
                input.DataType?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(dataType))
            {
                throw TableDataTypeError(
                    $"{path}.dataType",
                    "TABLE_METRIC_DATATYPE_REQUIRED",
                    dataType,
                    null);
            }
            if (!DynamicFormTableStatisticOperationContract
                    .IsSupportedDataType(dataType))
            {
                throw TableDataTypeError(
                    $"{path}.dataType",
                    "TABLE_METRIC_DATATYPE_UNSUPPORTED",
                    dataType,
                    null);
            }
            if (input.AggregateOps is null ||
                input.AggregateOps.Count == 0)
            {
                throw Schema(
                    $"{path}.aggregateOps",
                    "AGGREGATE_OPS_REQUIRED");
            }

            var operations = new List<string>(
                input.AggregateOps.Count);
            var seenOperations =
                new HashSet<string>(StringComparer.Ordinal);
            for (var operationIndex = 0;
                 operationIndex < input.AggregateOps.Count;
                 operationIndex++)
            {
                var operation =
                    input.AggregateOps[operationIndex]?
                        .Trim()
                        .ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(operation))
                {
                    throw TableOperationError(
                        $"{path}.aggregateOps[{operationIndex}]",
                        "OPERATION_REQUIRED",
                        dataType,
                        operation);
                }
                if (!seenOperations.Add(operation))
                {
                    throw Schema(
                        $"{path}.aggregateOps[{operationIndex}]",
                        "DUPLICATE_OPERATION");
                }
                operations.Add(operation);
            }

            result.Add(
                new NormalizedTableMetricMutation(
                    metricKey,
                    dataType,
                    operations,
                    index));
        }

        return result;
    }

    private static IReadOnlyList<
        NormalizedMetricLabelTargetMutation>
        NormalizeMetricLabelTargets(
            IReadOnlyList<
                DynamicFormStatisticMetricLabelTargetPayload> inputs,
            string blockPath)
    {
        var seenPairs =
            new HashSet<(string MetricKey, string Code)>();
        var result =
            new List<NormalizedMetricLabelTargetMutation>(
                inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var path =
                $"{blockPath}.metricLabelTargets[{index}]";
            var input = inputs[index];
            var metricKey = NormalizeIdentity(
                input.MetricKey,
                $"{path}.metricKey",
                "METRIC_KEY_REQUIRED");
            var code = LabelTaxonomyContract.NormalizeCode(
                input.StatisticLabelCode,
                $"{path}.statisticLabelCode");
            if (!seenPairs.Add((metricKey, code)))
            {
                throw Schema(
                    path,
                    "DUPLICATE_METRIC_LABEL_TARGET");
            }
            result.Add(
                new NormalizedMetricLabelTargetMutation(
                    metricKey,
                    code,
                    index));
        }

        return result
            .OrderBy(target => target.MetricKey, StringComparer.Ordinal)
            .ThenBy(
                target => target.StatisticLabelCode,
                StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> NormalizeTableLabelCodeSet(
        IReadOnlyList<string> inputs,
        string path,
        string duplicateReason)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var code = LabelTaxonomyContract.NormalizeCode(
                inputs[index],
                $"{path}[{index}]");
            if (!seen.Add(code))
            {
                throw Schema(
                    $"{path}[{index}]",
                    duplicateReason);
            }
            result.Add(code);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private async Task<ConfigState> ApplyMutationsAsync(
        IClientSessionHandle session,
        DynamicFormTemplate owner,
        ConfigState current,
        NormalizedMutationBatch mutations,
        MeResponse me,
        CancellationToken ct)
    {
        if (mutations.Fields is not null)
        {
            return await ApplyFieldMutationsAsync(
                session,
                owner,
                current,
                mutations.Fields,
                me,
                ct);
        }

        return await ApplyTableMutationsAsync(
            session,
            owner,
            current,
            mutations.Tables!,
            me,
            ct);
    }

    private async Task<ConfigState> ApplyTableMutationsAsync(
        IClientSessionHandle session,
        DynamicFormTemplate owner,
        ConfigState current,
        IReadOnlyList<NormalizedTableMutation> mutations,
        MeResponse me,
        CancellationToken ct)
    {
        var schema = ParseSchemaBlocks(owner);
        if (schema.IsLegacyAdapter)
        {
            throw StructureError(
                "$.schema.blocks",
                "LEGACY_TABLE_STATISTIC_CONFIG_READ_ONLY");
        }

        var nextTables = current.Tables.ToDictionary(
            table => table.BlockId,
            table => table,
            StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            var path =
                $"$.payload.tables[{mutation.InputIndex}]";
            if (!schema.Blocks.TryGetValue(
                    mutation.BlockId,
                    out var block))
            {
                throw Schema(
                    $"{path}.blockId",
                    "TABLE_BLOCK_NOT_FOUND");
            }

            ValidateTableModeAssertion(block, mutation, path);
            if (block.StatisticsDisabled &&
                !mutation.StatisticsDisabled)
            {
                throw StructureError(
                    $"{path}.statisticsDisabled",
                    "TABLE_STATISTICS_DISABLED_REACTIVATION_FORBIDDEN");
            }

            var catalog = BuildMetricCatalog(block, path);
            var selectedMetrics =
                new List<PersistedTableMetricConfig>(
                    mutation.Metrics.Count);
            foreach (var metric in mutation.Metrics)
            {
                var metricPath =
                    $"{path}.metrics[{metric.InputIndex}]";
                if (!catalog.TryGetValue(
                        metric.MetricKey,
                        out var schemaMetric))
                {
                    throw Schema(
                        $"{metricPath}.metricKey",
                        "TABLE_METRIC_NOT_FOUND");
                }

                var resolvedDataType =
                    ResolveMetricDataType(block, schemaMetric);
                if (!string.Equals(
                        resolvedDataType,
                        metric.DataType,
                        StringComparison.Ordinal))
                {
                    throw TableDataTypeError(
                        $"{metricPath}.dataType",
                        "TABLE_METRIC_DATATYPE_ASSERTION_MISMATCH",
                        metric.DataType,
                        resolvedDataType);
                }
                ValidateTableOperations(metric, metricPath);
                selectedMetrics.Add(
                    new PersistedTableMetricConfig(
                        metric.MetricKey,
                        resolvedDataType,
                        metric.AggregateOps));
            }

            var selectedByKey = selectedMetrics.ToDictionary(
                metric => metric.MetricKey,
                metric => metric,
                StringComparer.Ordinal);
            foreach (var target in mutation.MetricLabelTargets)
            {
                if (!selectedByKey.ContainsKey(target.MetricKey))
                {
                    throw Schema(
                        $"{path}.metricLabelTargets[" +
                        $"{target.InputIndex}].metricKey",
                        "TABLE_METRIC_LABEL_TARGET_NOT_CONFIGURED");
                }
            }

            var metricLabels = await ResolveMetricLabelTargetsAsync(
                session,
                mutation.MetricLabelTargets,
                selectedByKey,
                me,
                path,
                ct);
            var rowLabelDataType =
                ResolveRowLabelDataType(block.Node, path);
            var rowLabels = await ResolveTableLabelSnapshotsAsync(
                session,
                mutation.AllowedRowLabelCodes,
                rowLabelDataType,
                LabelUsages.TableTarget,
                "TABLE_ROW_LABEL",
                $"{path}.allowedRowLabelCodes",
                me,
                ct);

            ApplyTableMetadata(
                block,
                mutation,
                selectedMetrics);
            nextTables[mutation.BlockId] =
                new PersistedTableConfig(
                    mutation.BlockId,
                    block.TableMode,
                    mutation.StatisticsDisabled,
                    block.StatisticsInputCellCount,
                    block.StatisticsInputCellLimit,
                    ReadNodeString(
                        block.Node,
                        "statisticsDisabledReason"),
                    rowLabelDataType,
                    selectedMetrics
                        .OrderBy(
                            metric => metric.MetricKey,
                            StringComparer.Ordinal)
                        .ToList(),
                    metricLabels,
                    mutation.AllowedRowLabelCodes,
                    rowLabels,
                    ComputeTableStructureHash(block.Node),
                    CanonicalBlockSchemaSource);
        }

        var configuredTargetCount = nextTables.Values
            .Sum(table => table.Metrics.Count);
        if (configuredTargetCount >
            DynamicFormTableStatisticOperationContract.MaximumTargets)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_STATISTIC_TARGET_LIMIT_EXCEEDED,
                new
                {
                    path = "$.payload.tables",
                    reason = "TABLE_STATISTIC_TARGET_LIMIT_30",
                    limit =
                        DynamicFormTableStatisticOperationContract
                            .MaximumTargets,
                    actual = configuredTargetCount
                });
        }

        var orderedTables = nextTables.Values
            .OrderBy(table => table.BlockId, StringComparer.Ordinal)
            .ToList();
        EnsureUniqueStatisticLabelTargets(
            current.Fields,
            orderedTables);
        var tableSectionJson =
            StatConfigCanonicalJson.Canonicalize(orderedTables);
        var dependencyPins = BuildDependencyPins(
            current.Fields,
            orderedTables);
        var configHash = ComputeConfigHash(
            owner.Id,
            current.FieldSectionJson,
            tableSectionJson,
            dependencyPins);
        return new ConfigState(
            current.ConfigId,
            ObjectId.GenerateNewId().ToString(),
            current.VersionId,
            current.VersionNo + 1,
            current.Revision + 1,
            CurrentStatus(owner),
            configHash,
            dependencyPins,
            current.FieldSectionJson,
            tableSectionJson,
            current.Fields,
            orderedTables,
            current.FieldsJson,
            CanonicalNode(schema.Array),
            IsVirtual: false);
    }

    private static void ValidateTableOperations(
        NormalizedTableMetricMutation metric,
        string path)
    {
        for (var index = 0;
             index < metric.AggregateOps.Count;
             index++)
        {
            var operation = metric.AggregateOps[index];
            if (!DynamicFormTableStatisticOperationContract
                    .IsOperationAllowed(
                        metric.DataType,
                        operation))
            {
                throw TableOperationError(
                    $"{path}.aggregateOps[{index}]",
                    "TABLE_METRIC_OPERATION_INCOMPATIBLE",
                    metric.DataType,
                    operation);
            }
        }
    }

    private static void ValidateTableModeAssertion(
        SchemaBlock block,
        NormalizedTableMutation mutation,
        string path)
    {
        if (string.Equals(
                block.TableMode,
                DynamicFormTableStatisticOperationContract
                    .SummaryTemplate,
                StringComparison.Ordinal))
        {
            throw TableModeError(
                $"{path}.tableMode",
                "SUMMARY_TEMPLATE_STATISTIC_INPUT_FORBIDDEN",
                mutation.TableMode,
                block.TableMode);
        }
        if (!DynamicFormTableStatisticOperationContract
                .IsInputMode(block.TableMode))
        {
            throw TableModeError(
                $"{path}.tableMode",
                "TABLE_MODE_UNSUPPORTED",
                mutation.TableMode,
                block.TableMode);
        }
        if (!string.Equals(
                mutation.TableMode,
                block.TableMode,
                StringComparison.Ordinal))
        {
            throw TableModeError(
                $"{path}.tableMode",
                "TABLE_MODE_ASSERTION_MISMATCH",
                mutation.TableMode,
                block.TableMode);
        }
    }

    private async Task<List<PersistedTableConfig>>
        DeriveInitialTablesAsync(
            IClientSessionHandle? session,
            DynamicFormTemplate owner,
            MeResponse me,
            CancellationToken ct)
    {
        var schema = ParseSchemaBlocks(owner);
        var result = new List<PersistedTableConfig>();
        foreach (var block in schema.Blocks.Values
                     .OrderBy(
                         item => item.BlockId,
                         StringComparer.Ordinal))
        {
            var path = $"$.schema.blocks[{block.Index}]";
            var catalog = BuildMetricCatalog(block, path);
            var metrics = new List<PersistedTableMetricConfig>();
            foreach (var metric in catalog.Values)
            {
                var operations = ReadAggregateOperations(
                    metric.Node,
                    $"{path}.metricRules[{metric.Index}]" +
                    ".aggregateOps");
                if (operations.Count == 0)
                    continue;
                var dataType = ResolveMetricDataType(block, metric);
                var normalized =
                    new NormalizedTableMetricMutation(
                        metric.MetricKey,
                        dataType,
                        operations,
                        metric.Index);
                ValidateTableOperations(
                    normalized,
                    $"{path}.metricRules[{metric.Index}]");
                metrics.Add(
                    new PersistedTableMetricConfig(
                        metric.MetricKey,
                        dataType,
                        operations));
            }

            var targets =
                ReadExistingMetricLabelTargets(block, path);
            var allowedRowLabelCodes =
                ReadExistingRowLabelCodes(block, path);
            var hasOwnedMetadata =
                metrics.Count > 0 ||
                targets.Count > 0 ||
                allowedRowLabelCodes.Count > 0 ||
                block.StatisticsDisabled;
            if (!hasOwnedMetadata)
                continue;

            if (string.Equals(
                    block.TableMode,
                    DynamicFormTableStatisticOperationContract
                        .SummaryTemplate,
                    StringComparison.Ordinal))
            {
                throw TableModeError(
                    $"{path}.tableMode",
                    "SUMMARY_TEMPLATE_STATISTIC_INPUT_FORBIDDEN",
                    block.TableMode,
                    block.TableMode);
            }
            if (!DynamicFormTableStatisticOperationContract
                    .IsInputMode(block.TableMode))
            {
                throw TableModeError(
                    $"{path}.tableMode",
                    "TABLE_MODE_UNSUPPORTED",
                    block.TableMode,
                    block.TableMode);
            }

            var metricMap = metrics.ToDictionary(
                metric => metric.MetricKey,
                metric => metric,
                StringComparer.Ordinal);
            foreach (var target in targets)
            {
                if (!metricMap.ContainsKey(target.MetricKey))
                {
                    throw Schema(
                        $"{path}.metricLabelTargets[" +
                        $"{target.InputIndex}].metricKey",
                        "TABLE_METRIC_LABEL_TARGET_NOT_CONFIGURED");
                }
            }
            var metricLabels = await ResolveMetricLabelTargetsAsync(
                session,
                targets,
                metricMap,
                me,
                path,
                ct);
            var rowLabelDataType =
                ResolveRowLabelDataType(block.Node, path);
            var rowLabels = await ResolveTableLabelSnapshotsAsync(
                session,
                allowedRowLabelCodes,
                rowLabelDataType,
                LabelUsages.TableTarget,
                "TABLE_ROW_LABEL",
                $"{path}.allowedRowLabelCodes",
                me,
                ct);

            result.Add(
                new PersistedTableConfig(
                    block.BlockId,
                    block.TableMode,
                    block.StatisticsDisabled,
                    block.StatisticsInputCellCount,
                    block.StatisticsInputCellLimit,
                    ReadNodeString(
                        block.Node,
                        "statisticsDisabledReason"),
                    rowLabelDataType,
                    metrics
                        .OrderBy(
                            metric => metric.MetricKey,
                            StringComparer.Ordinal)
                        .ToList(),
                    metricLabels,
                    allowedRowLabelCodes,
                    rowLabels,
                    ComputeTableStructureHash(block.Node),
                    block.SchemaSource));
        }

        EnsureTableTargetLimit(result, "$.schema.blocks");
        return result;
    }

    private static void ValidatePersistedTableStructure(
        IReadOnlyList<PersistedTableConfig> tables,
        DynamicFormTemplate owner)
    {
        var schema = ParseSchemaBlocks(owner);
        var seenBlocks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            if (!seenBlocks.Add(table.BlockId))
            {
                throw IntegrityConflict(
                    owner.Id,
                    "DUPLICATE_CONFIGURED_TABLE");
            }
            if (!schema.Blocks.TryGetValue(
                    table.BlockId,
                    out var block))
            {
                throw IntegrityConflict(
                    owner.Id,
                    "CONFIGURED_TABLE_MISSING");
            }
            if (!string.Equals(
                    table.SchemaSource,
                    block.SchemaSource,
                    StringComparison.Ordinal))
            {
                throw IntegrityConflict(
                    owner.Id,
                    "CONFIGURED_TABLE_SCHEMA_SOURCE");
            }
            if (!string.Equals(
                    table.TableMode,
                    block.TableMode,
                    StringComparison.Ordinal) ||
                table.StatisticsDisabled !=
                block.StatisticsDisabled ||
                table.StatisticsInputCellCount !=
                block.StatisticsInputCellCount ||
                table.StatisticsInputCellLimit !=
                block.StatisticsInputCellLimit ||
                !string.Equals(
                    table.StatisticsDisabledReason,
                    ReadNodeString(
                        block.Node,
                        "statisticsDisabledReason"),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    table.StructureHash,
                    ComputeTableStructureHash(block.Node),
                    StringComparison.Ordinal))
            {
                throw IntegrityConflict(
                    owner.Id,
                    "CONFIGURED_TABLE_STRUCTURE");
            }

            var path =
                $"$.schema.blocks[{block.Index}]";
            var rowLabelDataType =
                ResolveRowLabelDataType(block.Node, path);
            if (!string.Equals(
                    table.RowLabelDataType,
                    rowLabelDataType,
                    StringComparison.Ordinal))
            {
                throw IntegrityConflict(
                    owner.Id,
                    "CONFIGURED_TABLE_ROW_LABEL_TYPE");
            }

            var catalog = BuildMetricCatalog(block, path);
            var configuredMetrics =
                new Dictionary<string, PersistedTableMetricConfig>(
                    StringComparer.Ordinal);
            foreach (var metric in table.Metrics)
            {
                if (!configuredMetrics.TryAdd(
                        metric.MetricKey,
                        metric))
                {
                    throw IntegrityConflict(
                        owner.Id,
                        "DUPLICATE_CONFIGURED_TABLE_METRIC");
                }
                if (!catalog.TryGetValue(
                        metric.MetricKey,
                        out var schemaMetric))
                {
                    throw IntegrityConflict(
                        owner.Id,
                        "CONFIGURED_TABLE_METRIC_MISSING");
                }
                var dataType =
                    ResolveMetricDataType(block, schemaMetric);
                var operations = ReadAggregateOperations(
                    schemaMetric.Node,
                    $"{path}.metricRules[" +
                    $"{schemaMetric.Index}].aggregateOps");
                if (!string.Equals(
                        metric.DataType,
                        dataType,
                        StringComparison.Ordinal) ||
                    !metric.AggregateOps.SequenceEqual(
                        operations,
                        StringComparer.Ordinal))
                {
                    throw IntegrityConflict(
                        owner.Id,
                        "CONFIGURED_TABLE_METRIC_STRUCTURE");
                }
                ValidateTableOperations(
                    new NormalizedTableMetricMutation(
                        metric.MetricKey,
                        metric.DataType,
                        metric.AggregateOps,
                        schemaMetric.Index),
                    $"{path}.metricRules[{schemaMetric.Index}]");
            }

            var currentConfiguredKeys = catalog.Values
                .Where(metric =>
                    ReadAggregateOperations(
                        metric.Node,
                        $"{path}.metricRules[{metric.Index}]" +
                        ".aggregateOps").Count > 0)
                .Select(metric => metric.MetricKey)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var snapshotKeys = configuredMetrics.Keys
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!currentConfiguredKeys.SequenceEqual(
                    snapshotKeys,
                    StringComparer.Ordinal))
            {
                throw IntegrityConflict(
                    owner.Id,
                    "CONFIGURED_TABLE_METRIC_SET");
            }

            ValidatePersistedMetricLabels(
                table,
                ReadExistingMetricLabelTargets(block, path),
                configuredMetrics,
                owner.Id);
            var currentRowLabels =
                ReadExistingRowLabelCodes(block, path);
            if (!table.AllowedRowLabelCodes.SequenceEqual(
                    currentRowLabels,
                    StringComparer.Ordinal))
            {
                throw IntegrityConflict(
                    owner.Id,
                    "CONFIGURED_TABLE_ROW_LABEL_SET");
            }
            ValidatePersistedRowLabelSnapshots(
                table,
                owner.Id);
        }

        EnsureTableTargetLimit(tables, "$.tableConfig");
    }

    private static void ValidatePersistedMetricLabels(
        PersistedTableConfig table,
        IReadOnlyList<NormalizedMetricLabelTargetMutation>
            currentTargets,
        IReadOnlyDictionary<
            string,
            PersistedTableMetricConfig> metrics,
        string ownerId)
    {
        var current = currentTargets
            .Select(target =>
                (target.MetricKey, target.StatisticLabelCode))
            .OrderBy(target => target.MetricKey, StringComparer.Ordinal)
            .ThenBy(
                target => target.StatisticLabelCode,
                StringComparer.Ordinal)
            .ToArray();
        var snapshot = table.MetricLabelTargets
            .Select(target =>
                (target.MetricKey, target.StatisticLabelCode))
            .OrderBy(target => target.MetricKey, StringComparer.Ordinal)
            .ThenBy(
                target => target.StatisticLabelCode,
                StringComparer.Ordinal)
            .ToArray();
        if (!current.SequenceEqual(snapshot))
        {
            throw IntegrityConflict(
                ownerId,
                "CONFIGURED_TABLE_METRIC_LABEL_SET");
        }

        foreach (var target in table.MetricLabelTargets)
        {
            if (!metrics.TryGetValue(
                    target.MetricKey,
                    out var metric) ||
                !string.Equals(
                    target.DataType,
                    metric.DataType,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    target.LabelSnapshot.Code,
                    target.StatisticLabelCode,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    target.LabelSnapshot.Usage,
                    LabelUsages.TableTarget,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    target.LabelSnapshot.DataType,
                    ToLabelDataType(metric.DataType),
                    StringComparison.Ordinal))
            {
                throw IntegrityConflict(
                    ownerId,
                    "CONFIGURED_TABLE_METRIC_LABEL");
            }
        }
    }

    private static void ValidatePersistedRowLabelSnapshots(
        PersistedTableConfig table,
        string ownerId)
    {
        var codes = table.RowLabelSnapshots
            .Select(snapshot =>
                LabelTaxonomyContract.NormalizeCode(
                    snapshot.Code,
                    "$.tableConfig.rowLabelSnapshots"))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        if (!codes.SequenceEqual(
                table.AllowedRowLabelCodes,
                StringComparer.Ordinal))
        {
            throw IntegrityConflict(
                ownerId,
                "CONFIGURED_TABLE_ROW_LABEL_SNAPSHOTS");
        }
        var expectedDataType =
            ToLabelDataType(table.RowLabelDataType);
        if (table.RowLabelSnapshots.Any(snapshot =>
                !string.Equals(
                    snapshot.Usage,
                    LabelUsages.TableTarget,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    snapshot.DataType,
                    expectedDataType,
                    StringComparison.Ordinal)))
        {
            throw IntegrityConflict(
                ownerId,
                "CONFIGURED_TABLE_ROW_LABEL");
        }
    }

    private static void EnsureTableTargetLimit(
        IEnumerable<PersistedTableConfig> tables,
        string path)
    {
        var actual = tables.Sum(table => table.Metrics.Count);
        if (actual <=
            DynamicFormTableStatisticOperationContract.MaximumTargets)
        {
            return;
        }
        throw AppExceptionFactory.BadRequest(
            AppErrorCode
                .DYNAMIC_FORM_STATISTIC_TARGET_LIMIT_EXCEEDED,
            new
            {
                path,
                reason = "TABLE_STATISTIC_TARGET_LIMIT_30",
                limit =
                    DynamicFormTableStatisticOperationContract
                        .MaximumTargets,
                actual
            });
    }

    private static IReadOnlyList<string> ReadAggregateOperations(
        JsonObject metric,
        string path)
    {
        if (!metric.TryGetPropertyValue(
                "aggregateOps",
                out var operationsNode) ||
            operationsNode is null)
        {
            return Array.Empty<string>();
        }
        if (operationsNode is not JsonArray operations)
            throw Schema(path, "AGGREGATE_OPS_ARRAY_REQUIRED");

        var result = new List<string>(operations.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index] is not JsonValue value ||
                !value.TryGetValue<string>(out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                throw Schema(
                    $"{path}[{index}]",
                    "OPERATION_REQUIRED");
            }
            var normalized =
                text.Trim().ToUpperInvariant();
            if (!seen.Add(normalized))
            {
                throw Schema(
                    $"{path}[{index}]",
                    "DUPLICATE_OPERATION");
            }
            result.Add(normalized);
        }
        return result;
    }

    private static IReadOnlyList<
        NormalizedMetricLabelTargetMutation>
        ReadExistingMetricLabelTargets(
            SchemaBlock block,
            string path)
    {
        if (!block.Node.TryGetPropertyValue(
                "metricLabelTargets",
                out var targetsNode) ||
            targetsNode is null)
        {
            return Array.Empty<
                NormalizedMetricLabelTargetMutation>();
        }
        if (targetsNode is not JsonArray targets)
        {
            throw Schema(
                $"{path}.metricLabelTargets",
                "METRIC_LABEL_TARGETS_ARRAY_REQUIRED");
        }

        var payloads = new List<
            DynamicFormStatisticMetricLabelTargetPayload>(
                targets.Count);
        for (var index = 0; index < targets.Count; index++)
        {
            if (targets[index] is not JsonObject target)
            {
                throw Schema(
                    $"{path}.metricLabelTargets[{index}]",
                    "METRIC_LABEL_TARGET_OBJECT_REQUIRED");
            }
            if (target.ContainsKey("range") ||
                target.ContainsKey("columnKey") ||
                target.ContainsKey("columnIndex") ||
                target.ContainsKey("rowKey") ||
                string.Equals(
                    ReadNodeString(target, "targetKind"),
                    "RANGE",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Schema(
                    $"{path}.metricLabelTargets[{index}]",
                    "POSITIONAL_METRIC_LABEL_TARGET_FORBIDDEN");
            }
            payloads.Add(
                new DynamicFormStatisticMetricLabelTargetPayload(
                    ReadNodeString(target, "metricKey"),
                    ReadNodeString(
                        target,
                        "statisticLabelCode")));
        }
        return NormalizeMetricLabelTargets(payloads, path);
    }

    private static IReadOnlyList<string>
        ReadExistingRowLabelCodes(
            SchemaBlock block,
            string path)
    {
        if (!block.Node.TryGetPropertyValue(
                "allowedRowLabelCodes",
                out var codesNode) ||
            codesNode is null)
        {
            return Array.Empty<string>();
        }
        if (codesNode is not JsonArray codes)
        {
            throw Schema(
                $"{path}.allowedRowLabelCodes",
                "ALLOWED_ROW_LABEL_CODES_ARRAY_REQUIRED");
        }
        var values = new List<string>(codes.Count);
        foreach (var node in codes)
        {
            if (node is not JsonValue value ||
                !value.TryGetValue<string>(out var code))
            {
                throw Schema(
                    $"{path}.allowedRowLabelCodes",
                    "STRING_ARRAY_REQUIRED");
            }
            values.Add(code);
        }
        return NormalizeTableLabelCodeSet(
            values,
            $"{path}.allowedRowLabelCodes",
            "DUPLICATE_ALLOWED_ROW_LABEL_CODE");
    }

    private async Task<List<PersistedMetricLabelTargetConfig>>
        ResolveMetricLabelTargetsAsync(
            IClientSessionHandle? session,
            IReadOnlyList<
                NormalizedMetricLabelTargetMutation> targets,
            IReadOnlyDictionary<
                string,
                PersistedTableMetricConfig> metrics,
            MeResponse me,
            string blockPath,
            CancellationToken ct)
    {
        if (targets.Count == 0)
            return new List<PersistedMetricLabelTargetConfig>();
        var labels = await LoadVisibleTableLabelsAsync(
            session,
            targets.Select(target => target.StatisticLabelCode),
            me,
            ct);
        var result =
            new List<PersistedMetricLabelTargetConfig>(
                targets.Count);
        foreach (var target in targets)
        {
            var metric = metrics[target.MetricKey];
            var snapshot = ResolveTableLabelSnapshot(
                labels,
                target.StatisticLabelCode,
                metric.DataType,
                LabelUsages.TableTarget,
                "TABLE_METRIC_LABEL",
                $"{blockPath}.metricLabelTargets[" +
                $"{target.InputIndex}].statisticLabelCode");
            result.Add(
                new PersistedMetricLabelTargetConfig(
                    target.MetricKey,
                    target.StatisticLabelCode,
                    metric.DataType,
                    snapshot));
        }

        return result
            .OrderBy(
                target => target.MetricKey,
                StringComparer.Ordinal)
            .ThenBy(
                target => target.StatisticLabelCode,
                StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<
        DynamicFormStatisticLabelSnapshotDto>>
        ResolveTableLabelSnapshotsAsync(
            IClientSessionHandle? session,
            IReadOnlyList<string> codes,
            string tableDataType,
            string requiredUsage,
            string reasonPrefix,
            string path,
            MeResponse me,
            CancellationToken ct)
    {
        if (codes.Count == 0)
        {
            return new List<
                DynamicFormStatisticLabelSnapshotDto>();
        }
        var labels = await LoadVisibleTableLabelsAsync(
            session,
            codes,
            me,
            ct);
        var result = new List<
            DynamicFormStatisticLabelSnapshotDto>(codes.Count);
        for (var index = 0; index < codes.Count; index++)
        {
            result.Add(
                ResolveTableLabelSnapshot(
                    labels,
                    codes[index],
                    tableDataType,
                    requiredUsage,
                    reasonPrefix,
                    $"{path}[{index}]"));
        }
        return result;
    }

    private async Task<List<LabelCatalogItem>>
        LoadVisibleTableLabelsAsync(
            IClientSessionHandle? session,
            IEnumerable<string> codes,
            MeResponse me,
            CancellationToken ct)
    {
        var distinctCodes = codes
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var filter =
            Builders<LabelCatalogItem>.Filter.In(
                label => label.Code,
                distinctCodes) &
            Builders<LabelCatalogItem>.Filter.Eq(
                label => label.IsDeleted,
                false) &
            BuildLabelVisibilityFilter(me);
        return session is null
            ? await _ctx.Labels.Find(filter).ToListAsync(ct)
            : await _ctx.Labels
                .Find(session, filter)
                .ToListAsync(ct);
    }

    private static DynamicFormStatisticLabelSnapshotDto
        ResolveTableLabelSnapshot(
            IReadOnlyList<LabelCatalogItem> labels,
            string code,
            string tableDataType,
            string requiredUsage,
            string reasonPrefix,
            string path)
    {
        var matches = labels
            .Where(label =>
                string.Equals(
                    label.Code,
                    code,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0 ||
            matches.All(label => !label.IsActive))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_LABEL_NOT_FOUND_OR_INACTIVE,
                new
                {
                    path,
                    reason =
                        $"{reasonPrefix}_NOT_FOUND_OR_INACTIVE",
                    code
                });
        }
        matches = matches
            .Where(label => label.IsActive)
            .ToList();
        if (matches.Count != 1)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                new
                {
                    path,
                    reason = $"{reasonPrefix}_AMBIGUOUS",
                    code
                });
        }

        var label = matches[0];
        if (!string.Equals(
                label.Usage,
                requiredUsage,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                new
                {
                    path,
                    reason =
                        $"{reasonPrefix}_USAGE_INCOMPATIBLE",
                    code,
                    expectedUsage = requiredUsage,
                    actualUsage = label.Usage
                });
        }
        var expectedDataType = ToLabelDataType(tableDataType);
        if (!string.Equals(
                label.DataType,
                expectedDataType,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                new
                {
                    path,
                    reason =
                        $"{reasonPrefix}_TYPE_INCOMPATIBLE",
                    code,
                    expectedDataType,
                    actualDataType = label.DataType
                });
        }
        if (string.IsNullOrWhiteSpace(label.VersionId) ||
            string.IsNullOrWhiteSpace(label.ConfigHash) ||
            label.VersionNo < 1)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID,
                new
                {
                    path,
                    reason =
                        $"{reasonPrefix}_IDENTITY_INVALID",
                    code
                });
        }

        return new DynamicFormStatisticLabelSnapshotDto(
            label.Id,
            label.Code,
            label.DataType,
            label.Usage,
            label.ScopeType,
            label.ScopeId,
            label.IsActive,
            label.VersionNo,
            label.VersionId,
            label.ConfigHash);
    }

    private static void EnsureUniqueStatisticLabelTargets(
        IEnumerable<PersistedFieldConfig> fields,
        IEnumerable<PersistedTableConfig> tables)
    {
        var targets =
            new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            foreach (var code in field.StatisticLabelCodes)
            {
                AddUniqueStatisticLabelTarget(
                    targets,
                    code,
                    $"field:{field.FieldId}");
            }
        }
        foreach (var table in tables)
        {
            foreach (var target in table.MetricLabelTargets)
            {
                AddUniqueStatisticLabelTarget(
                    targets,
                    target.StatisticLabelCode,
                    $"table:{table.BlockId}.metric:" +
                    target.MetricKey);
            }
        }
    }

    private static void AddUniqueStatisticLabelTarget(
        IDictionary<string, string> targets,
        string code,
        string identity)
    {
        if (targets.TryGetValue(code, out var existing) &&
            !string.Equals(
                existing,
                identity,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode
                    .DYNAMIC_FORM_LABEL_STATISTIC_TARGET_CONFLICT,
                new
                {
                    path = "$.payload",
                    reason =
                        "TABLE_STATISTIC_LABEL_TARGET_CONFLICT",
                    code,
                    firstTarget = existing,
                    secondTarget = identity
                });
        }
        targets[code] = identity;
    }

    private static List<string> BuildDependencyPins(
        IEnumerable<PersistedFieldConfig> fields,
        IEnumerable<PersistedTableConfig> tables)
        => fields
            .SelectMany(field => field.LabelSnapshots)
            .Concat(
                tables.SelectMany(
                    table => table.MetricLabelTargets
                        .Select(target => target.LabelSnapshot)
                        .Concat(table.RowLabelSnapshots)))
            .Select(label =>
                $"LABEL:{label.LabelId}:{label.VersionId}:" +
                $"{label.VersionNo}:{label.ConfigHash}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(pin => pin, StringComparer.Ordinal)
            .ToList();

    private static List<PersistedTableConfig>
        DeserializeTableSection(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<
                       List<PersistedTableConfig>>(
                       json,
                       StatConfigCanonicalJson.StrictJsonOptions) ??
                   new List<PersistedTableConfig>();
        }
        catch (JsonException)
        {
            throw Schema(
                "$.tableConfig",
                "TABLE_SECTION_SCHEMA_INVALID");
        }
    }

    private static DynamicFormStatisticTableConfigDto ToTableDto(
        PersistedTableConfig table)
        => new(
            table.BlockId,
            table.TableMode,
            table.StatisticsDisabled,
            table.StatisticsInputCellCount,
            table.StatisticsInputCellLimit,
            table.StatisticsDisabledReason,
            table.RowLabelDataType,
            table.Metrics
                .Select(metric =>
                    new DynamicFormStatisticTableMetricConfigDto(
                        metric.MetricKey,
                        metric.DataType,
                        metric.AggregateOps))
                .ToList(),
            table.MetricLabelTargets
                .Select(target =>
                    new
                        DynamicFormStatisticMetricLabelTargetConfigDto(
                            target.MetricKey,
                            target.StatisticLabelCode,
                            target.DataType,
                            target.LabelSnapshot))
                .ToList(),
            table.AllowedRowLabelCodes,
            table.RowLabelSnapshots,
            table.StructureHash,
            table.SchemaSource);

    private static string NormalizeIdentity(
        string? value,
        string path,
        string missingReason)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw Schema(path, missingReason);
        if (normalized.Length > 256 ||
            normalized.Any(char.IsControl))
        {
            throw Schema(path, "IDENTITY_INVALID");
        }
        return normalized;
    }

    private static SchemaBlockSet ParseSchemaBlocks(
        DynamicFormTemplate owner)
    {
        var canonical = ParseCanonicalBlockArray(owner.BlocksJson);
        if (canonical.Count > 0)
        {
            return BuildSchemaBlockSet(
                canonical,
                CanonicalBlockSchemaSource,
                isLegacyAdapter: false);
        }

        if (string.IsNullOrWhiteSpace(owner.ExcelBlockJson))
        {
            return BuildSchemaBlockSet(
                canonical,
                CanonicalBlockSchemaSource,
                isLegacyAdapter: false);
        }

        try
        {
            var legacy = JsonNode.Parse(owner.ExcelBlockJson);
            if (legacy is null)
            {
                return BuildSchemaBlockSet(
                    canonical,
                    CanonicalBlockSchemaSource,
                    isLegacyAdapter: false);
            }
            if (legacy is not JsonObject legacyBlock)
            {
                throw Schema(
                    "$.schema.excelBlock",
                    "LEGACY_TABLE_BLOCK_OBJECT_REQUIRED");
            }
            return BuildSchemaBlockSet(
                new JsonArray(legacyBlock.DeepClone()),
                LegacyBlockSchemaSource,
                isLegacyAdapter: true);
        }
        catch (JsonException)
        {
            throw Schema(
                "$.schema.excelBlock",
                "LEGACY_TABLE_BLOCK_JSON_INVALID");
        }
    }

    private static JsonArray ParseCanonicalBlockArray(string? json)
    {
        try
        {
            var node = JsonNode.Parse(json ?? "[]");
            return node as JsonArray ??
                   throw Schema(
                       "$.schema.blocks",
                       "TABLE_BLOCKS_ARRAY_REQUIRED");
        }
        catch (JsonException)
        {
            throw Schema(
                "$.schema.blocks",
                "TABLE_BLOCKS_JSON_INVALID");
        }
    }

    private static SchemaBlockSet BuildSchemaBlockSet(
        JsonArray array,
        string schemaSource,
        bool isLegacyAdapter)
    {
        if (array.Count >
            DynamicFormTableStatisticOperationContract.MaximumBlocks)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_LIMIT_EXCEEDED,
                new
                {
                    path = "$.schema.blocks",
                    reason = "TABLE_BLOCK_LIMIT_30",
                    limit =
                        DynamicFormTableStatisticOperationContract
                            .MaximumBlocks,
                    actual = array.Count
                });
        }

        var blocks =
            new Dictionary<string, SchemaBlock>(
                StringComparer.Ordinal);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject block)
            {
                throw Schema(
                    $"$.schema.blocks[{index}]",
                    "TABLE_BLOCK_OBJECT_REQUIRED");
            }
            var blockId = NormalizeIdentity(
                ReadNodeString(block, "blockId"),
                $"$.schema.blocks[{index}].blockId",
                "BLOCK_ID_REQUIRED");
            if (blocks.ContainsKey(blockId))
            {
                throw Schema(
                    $"$.schema.blocks[{index}].blockId",
                    "DUPLICATE_SCHEMA_BLOCK_ID");
            }

            var tableMode =
                ReadNodeString(block, "tableMode")?
                    .ToUpperInvariant() ?? string.Empty;
            blocks.Add(
                blockId,
                new SchemaBlock(
                    blockId,
                    tableMode,
                    ReadNodeBoolean(block, "statisticsDisabled"),
                    ReadNodeInt(
                        block,
                        "statisticsInputCellCount"),
                    ReadNodeInt(
                        block,
                        "statisticsInputCellLimit"),
                    index,
                    block,
                    schemaSource));
        }

        return new SchemaBlockSet(
            array,
            blocks,
            isLegacyAdapter);
    }

    private static IReadOnlyDictionary<string, SchemaMetric>
        BuildMetricCatalog(
            SchemaBlock block,
            string blockPath)
    {
        var result =
            new Dictionary<string, SchemaMetric>(
                StringComparer.Ordinal);
        if (!block.Node.TryGetPropertyValue(
                "metricRules",
                out var metricRulesNode) ||
            metricRulesNode is null)
        {
            return result;
        }
        if (metricRulesNode is not JsonArray rules)
        {
            throw Schema(
                $"{blockPath}.metricRules",
                "METRIC_RULES_ARRAY_REQUIRED");
        }

        for (var index = 0; index < rules.Count; index++)
        {
            if (rules[index] is not JsonObject rule)
            {
                throw Schema(
                    $"{blockPath}.metricRules[{index}]",
                    "METRIC_RULE_OBJECT_REQUIRED");
            }
            var metricKey = NormalizeIdentity(
                ReadNodeString(rule, "metricKey"),
                $"{blockPath}.metricRules[{index}].metricKey",
                "METRIC_KEY_REQUIRED");
            if (!result.TryAdd(
                    metricKey,
                    new SchemaMetric(metricKey, index, rule)))
            {
                throw Schema(
                    $"{blockPath}.metricRules[{index}].metricKey",
                    "DUPLICATE_SCHEMA_METRIC_KEY");
            }
        }

        return result;
    }

    private static string ResolveMetricDataType(
        SchemaBlock block,
        SchemaMetric metric)
    {
        var explicitType =
            ReadNodeString(metric.Node, "dataType") ??
            ReadNodeString(metric.Node, "targetDataType");
        if (!string.IsNullOrWhiteSpace(explicitType))
        {
            return NormalizeTableDataType(
                explicitType,
                $"$.schema.blocks[{block.Index}].metricRules[" +
                $"{metric.Index}].dataType");
        }

        var indexMatches =
            ReadMetricIndexMatches(block.Node, metric.MetricKey);
        if (indexMatches.Count > 1)
        {
            throw Schema(
                $"$.schema.blocks[{block.Index}].indexMap",
                "DUPLICATE_INDEX_MAP_METRIC_KEY");
        }
        if (indexMatches.Count == 1)
        {
            var indexItem = indexMatches[0];
            var indexType =
                ReadNodeString(indexItem, "dataType") ??
                ReadNodeString(indexItem, "targetDataType");
            if (!string.IsNullOrWhiteSpace(indexType))
            {
                return NormalizeTableDataType(
                    indexType,
                    $"$.schema.blocks[{block.Index}].indexMap" +
                    ".dataType");
            }
            if (TryResolveIndexMapCoordinates(
                    block.Node,
                    indexItem,
                    out var row,
                    out var column))
            {
                return ResolveBlockCellDataType(
                    block.Node,
                    row,
                    column,
                    $"$.schema.blocks[{block.Index}]");
            }
        }

        var defaultType =
            ReadNodeString(block.Node, "defaultDataType") ??
            ReadNodeString(block.Node, "dataType");
        if (string.IsNullOrWhiteSpace(defaultType))
        {
            throw TableDataTypeError(
                $"$.schema.blocks[{block.Index}].metricRules[" +
                $"{metric.Index}].dataType",
                "TABLE_METRIC_DATATYPE_UNRESOLVED",
                null,
                null);
        }
        return NormalizeTableDataType(
            defaultType,
            $"$.schema.blocks[{block.Index}].defaultDataType");
    }

    private static List<JsonObject> ReadMetricIndexMatches(
        JsonObject block,
        string metricKey)
    {
        var result = new List<JsonObject>();
        if (block["indexMap"] is not JsonArray indexMap)
            return result;
        foreach (var node in indexMap)
        {
            if (node is JsonObject item &&
                string.Equals(
                    ReadNodeString(item, "metricKey"),
                    metricKey,
                    StringComparison.Ordinal))
            {
                result.Add(item);
            }
        }
        return result;
    }

    private static bool TryResolveIndexMapCoordinates(
        JsonObject block,
        JsonObject indexItem,
        out int row,
        out int column)
    {
        row = ReadNodeInt(indexItem, "r") ??
              ReadNodeInt(indexItem, "row") ??
              ReadNodeInt(indexItem, "rowIndex") ?? -1;
        column = ReadNodeInt(indexItem, "c") ??
                 ReadNodeInt(indexItem, "column") ??
                 ReadNodeInt(indexItem, "columnIndex") ?? -1;
        if (row >= 0 && column >= 0)
            return true;

        var index = ReadNodeInt(indexItem, "index");
        if (!index.HasValue ||
            block["dataRect"] is not JsonObject dataRect)
        {
            return false;
        }
        var r0 = ReadNodeInt(dataRect, "r0");
        var c0 = ReadNodeInt(dataRect, "c0");
        var c1 = ReadNodeInt(dataRect, "c1");
        var width = ReadNodeInt(block, "w") ??
                    (c0.HasValue && c1.HasValue
                        ? c1.Value - c0.Value + 1
                        : 0);
        if (!r0.HasValue ||
            !c0.HasValue ||
            width <= 0 ||
            index.Value < 0)
        {
            return false;
        }

        row = r0.Value + index.Value / width;
        column = c0.Value + index.Value % width;
        return true;
    }

    private static string ResolveBlockCellDataType(
        JsonObject block,
        int row,
        int column,
        string path)
    {
        var defaultType =
            ReadNodeString(block, "defaultDataType") ??
            ReadNodeString(block, "dataType");
        if (string.IsNullOrWhiteSpace(defaultType))
        {
            throw TableDataTypeError(
                $"{path}.defaultDataType",
                "TABLE_METRIC_DATATYPE_UNRESOLVED",
                null,
                null);
        }
        var result = NormalizeTableDataType(
            defaultType,
            $"{path}.defaultDataType");
        if (block["dataTypeOverrides"] is not JsonArray overrides)
            return result;

        for (var index = 0; index < overrides.Count; index++)
        {
            if (overrides[index] is not JsonObject item)
                continue;
            var range = item["range"] as JsonObject ?? item;
            var r0 = ReadNodeInt(range, "r0");
            var c0 = ReadNodeInt(range, "c0");
            var r1 = ReadNodeInt(range, "r1") ?? r0;
            var c1 = ReadNodeInt(range, "c1") ?? c0;
            if (!r0.HasValue ||
                !c0.HasValue ||
                !r1.HasValue ||
                !c1.HasValue ||
                row < r0.Value ||
                row > r1.Value ||
                column < c0.Value ||
                column > c1.Value)
            {
                continue;
            }
            var overrideType =
                ReadNodeString(item, "dataType") ??
                ReadNodeString(item, "targetDataType");
            if (!string.IsNullOrWhiteSpace(overrideType))
            {
                result = NormalizeTableDataType(
                    overrideType,
                    $"{path}.dataTypeOverrides[{index}].dataType");
            }
        }
        return result;
    }

    private static string ResolveRowLabelDataType(
        JsonObject block,
        string path)
    {
        var value =
            ReadNodeString(block, "rowLabelDataType") ??
            ReadNodeString(block, "rowLabelTargetDataType") ??
            ReadNodeString(block, "targetDataType") ??
            ReadNodeString(block, "labelDataType") ??
            ReadNodeString(block, "defaultDataType") ??
            ReadNodeString(block, "dataType") ??
            DynamicFormTableStatisticOperationContract.Number;
        return NormalizeTableDataType(
            value,
            $"{path}.rowLabelDataType");
    }

    private static string NormalizeTableDataType(
        string? value,
        string path)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized) ||
            !DynamicFormTableStatisticOperationContract
                .IsSupportedDataType(normalized))
        {
            throw TableDataTypeError(
                path,
                "TABLE_METRIC_DATATYPE_UNSUPPORTED",
                normalized,
                null);
        }
        return normalized;
    }

    private static string ToLabelDataType(string tableDataType)
        => tableDataType switch
        {
            DynamicFormTableStatisticOperationContract.Number =>
                LabelDataTypes.Number,
            DynamicFormTableStatisticOperationContract.ShortText or
                DynamicFormTableStatisticOperationContract.MultiSelect =>
                LabelDataTypes.ShortText,
            DynamicFormTableStatisticOperationContract.Boolean =>
                LabelDataTypes.Boolean,
            DynamicFormTableStatisticOperationContract.Date or
                DynamicFormTableStatisticOperationContract.FullDate =>
                LabelDataTypes.Date,
            _ => throw TableDataTypeError(
                "$.tableConfig",
                "TABLE_METRIC_DATATYPE_UNSUPPORTED",
                tableDataType,
                null)
        };

    private static void ApplyTableMetadata(
        SchemaBlock block,
        NormalizedTableMutation mutation,
        IReadOnlyList<PersistedTableMetricConfig> selectedMetrics)
    {
        if (block.Node["metricRules"] is not JsonArray rules)
        {
            throw Schema(
                $"$.schema.blocks[{block.Index}].metricRules",
                "METRIC_RULES_ARRAY_REQUIRED");
        }
        var selected = selectedMetrics.ToDictionary(
            metric => metric.MetricKey,
            metric => metric,
            StringComparer.Ordinal);
        foreach (var node in rules)
        {
            if (node is not JsonObject rule)
                continue;
            var metricKey = ReadNodeString(rule, "metricKey");
            if (metricKey is not null &&
                selected.TryGetValue(metricKey, out var metric))
            {
                rule["aggregateOps"] =
                    JsonSerializer.SerializeToNode(
                        metric.AggregateOps,
                        StatConfigCanonicalJson.StrictJsonOptions);
            }
            else
            {
                rule.Remove("aggregateOps");
            }
        }

        var targets = new JsonArray();
        foreach (var target in mutation.MetricLabelTargets)
        {
            var metric = selected[target.MetricKey];
            targets.Add(
                new JsonObject
                {
                    ["targetKind"] = "METRIC",
                    ["metricKey"] = target.MetricKey,
                    ["statisticLabelCode"] =
                        target.StatisticLabelCode,
                    ["dataType"] = metric.DataType
                });
        }
        block.Node["metricLabelTargets"] = targets;
        block.Node["allowedRowLabelCodes"] =
            JsonSerializer.SerializeToNode(
                mutation.AllowedRowLabelCodes,
                StatConfigCanonicalJson.StrictJsonOptions);

        var wasDisabled =
            ReadNodeBoolean(block.Node, "statisticsDisabled");
        block.Node["statisticsDisabled"] =
            mutation.StatisticsDisabled;
        if (mutation.StatisticsDisabled && !wasDisabled)
        {
            block.Node["statisticsDisabledReason"] =
                ConfigDisabledReason;
        }
        else if (!mutation.StatisticsDisabled &&
                 string.Equals(
                     ReadNodeString(
                         block.Node,
                         "statisticsDisabledReason"),
                     ConfigDisabledReason,
                     StringComparison.Ordinal))
        {
            block.Node.Remove("statisticsDisabledReason");
        }
    }

    private static string ComputeTableStructureHash(
        JsonObject block)
    {
        var structure = (JsonObject)block.DeepClone();
        structure.Remove("metricLabelTargets");
        structure.Remove("allowedRowLabelCodes");
        structure.Remove("statisticsDisabled");
        structure.Remove("statisticsDisabledReason");
        structure.Remove("MetricLabelTargets");
        structure.Remove("AllowedRowLabelCodes");
        structure.Remove("StatisticsDisabled");
        structure.Remove("StatisticsDisabledReason");
        if (structure["metricRules"] is JsonArray rules)
        {
            foreach (var node in rules)
            {
                if (node is JsonObject rule)
                {
                    rule.Remove("aggregateOps");
                    rule.Remove("AggregateOps");
                }
            }
        }
        return StatConfigCanonicalJson.HashUtf8(
            CanonicalNode(structure));
    }

    private static string? ReadNodeString(
        JsonObject owner,
        string propertyName)
        => owner[propertyName] is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text?.Trim()
            : null;

    private static bool ReadNodeBoolean(
        JsonObject owner,
        string propertyName)
        => owner[propertyName] is JsonValue value &&
           value.TryGetValue<bool>(out var result) &&
           result;

    private static int? ReadNodeInt(
        JsonObject owner,
        string propertyName)
        => owner[propertyName] is JsonValue value &&
           value.TryGetValue<int>(out var result)
            ? result
            : null;

    private static AppException TableModeError(
        string path,
        string reason,
        string? assertedMode,
        string? canonicalMode)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.DYNAMIC_FORM_TABLE_MODE_INVALID,
            new
            {
                path,
                reason,
                assertedMode,
                canonicalMode,
                allowedModes =
                    DynamicFormTableStatisticOperationContract
                        .AllowedInputModes
                        .OrderBy(
                            mode => mode,
                            StringComparer.Ordinal)
                        .ToArray()
            });

    private static AppException TableDataTypeError(
        string path,
        string reason,
        string? assertedDataType,
        string? canonicalDataType)
        => AppExceptionFactory.BadRequest(
            AppErrorCode
                .DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID,
            new
            {
                path,
                reason,
                assertedDataType,
                canonicalDataType,
                allowedDataTypes =
                    DynamicFormTableStatisticOperationContract
                        .AllowedOperationsByDataType
                        .Keys
                        .OrderBy(
                            value => value,
                            StringComparer.Ordinal)
                        .ToArray()
            });

    private static AppException TableOperationError(
        string path,
        string reason,
        string? dataType,
        string? operation)
        => AppExceptionFactory.BadRequest(
            AppErrorCode
                .DYNAMIC_FORM_STATISTIC_OPERATION_INVALID,
            new
            {
                path,
                reason,
                dataType,
                operation,
                allowedOperations =
                    dataType is not null &&
                    DynamicFormTableStatisticOperationContract
                        .AllowedOperationsByDataType
                        .TryGetValue(dataType, out var allowed)
                        ? allowed.OrderBy(
                                value => value,
                                StringComparer.Ordinal)
                            .ToArray()
                        : Array.Empty<string>()
            });

    private static AppException StructureError(
        string path,
        string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode
                .DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID,
            new { path, reason });

    private sealed record NormalizedMutationBatch(
        IReadOnlyList<NormalizedMutation>? Fields,
        IReadOnlyList<NormalizedTableMutation>? Tables,
        IReadOnlyList<DynamicFormNativeStatisticMutation>? NativeTargets = null,
        DynamicFormNativeStatisticsPayload? NativeStatistics = null);

    private sealed record NormalizedTableMutation(
        string BlockId,
        string TableMode,
        bool StatisticsDisabled,
        IReadOnlyList<NormalizedTableMetricMutation> Metrics,
        IReadOnlyList<NormalizedMetricLabelTargetMutation>
            MetricLabelTargets,
        IReadOnlyList<string> AllowedRowLabelCodes,
        int InputIndex);

    private sealed record NormalizedTableMetricMutation(
        string MetricKey,
        string DataType,
        IReadOnlyList<string> AggregateOps,
        int InputIndex);

    private sealed record NormalizedMetricLabelTargetMutation(
        string MetricKey,
        string StatisticLabelCode,
        int InputIndex);

    private sealed record SchemaBlockSet(
        JsonArray Array,
        IReadOnlyDictionary<string, SchemaBlock> Blocks,
        bool IsLegacyAdapter);

    private sealed record SchemaBlock(
        string BlockId,
        string TableMode,
        bool StatisticsDisabled,
        int? StatisticsInputCellCount,
        int? StatisticsInputCellLimit,
        int Index,
        JsonObject Node,
        string SchemaSource);

    private sealed record SchemaMetric(
        string MetricKey,
        int Index,
        JsonObject Node);

    private sealed record PersistedTableConfig(
        string BlockId,
        string TableMode,
        bool StatisticsDisabled,
        int? StatisticsInputCellCount,
        int? StatisticsInputCellLimit,
        string? StatisticsDisabledReason,
        string RowLabelDataType,
        IReadOnlyList<PersistedTableMetricConfig> Metrics,
        IReadOnlyList<PersistedMetricLabelTargetConfig>
            MetricLabelTargets,
        IReadOnlyList<string> AllowedRowLabelCodes,
        IReadOnlyList<DynamicFormStatisticLabelSnapshotDto>
            RowLabelSnapshots,
        string StructureHash,
        string SchemaSource);

    private sealed record PersistedTableMetricConfig(
        string MetricKey,
        string DataType,
        IReadOnlyList<string> AggregateOps);

    private sealed record PersistedMetricLabelTargetConfig(
        string MetricKey,
        string StatisticLabelCode,
        string DataType,
        DynamicFormStatisticLabelSnapshotDto LabelSnapshot);
}
