using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services;

namespace tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowMappingEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static List<DynamicFlowMappingRuleDto> ReadRulesFromPayloadJson(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new List<DynamicFlowMappingRuleDto>();

        var normalizedPayload = DynamicFlowTemplateService.NormalizePayloadJson(payloadJson, requireLockable: false);
        var root = JsonNode.Parse(normalizedPayload) as JsonObject;
        if (root?["mappingRules"] is not JsonArray rules)
            return new List<DynamicFlowMappingRuleDto>();

        return JsonSerializer.Deserialize<List<DynamicFlowMappingRuleDto>>(rules.ToJsonString(JsonOptions), JsonOptions)
               ?? new List<DynamicFlowMappingRuleDto>();
    }

    public static List<DynamicFlowMappingRuleDto> ReadRulesFromJson(string? mappingRulesJson)
    {
        if (string.IsNullOrWhiteSpace(mappingRulesJson))
            return new List<DynamicFlowMappingRuleDto>();

        var node = JsonNode.Parse(mappingRulesJson);
        if (node is JsonObject root && root["mappingRules"] is JsonArray nested)
            node = nested;

        return node is JsonArray array
            ? JsonSerializer.Deserialize<List<DynamicFlowMappingRuleDto>>(array.ToJsonString(JsonOptions), JsonOptions)
              ?? new List<DynamicFlowMappingRuleDto>()
            : new List<DynamicFlowMappingRuleDto>();
    }

    public static DynamicFlowMappingPreviewResponse Preview(
        WorkAssignmentReport targetReport,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        IReadOnlyList<DynamicFlowMappingRuleDto> rules,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DateTime nowUtc)
    {
        var targetFields = ParseObject(targetReport.FieldValuesJson) ?? new JsonObject();
        var targetValues = EnsureValuesObject(targetFields);
        var targetTables = ParseTableValuesRoot(targetReport.TableValuesJson) ?? new JsonObject();
        EnsureBlocksArray(targetTables);

        var response = new DynamicFlowMappingPreviewResponse
        {
            TargetReportId = targetReport.Id,
            TargetAssignmentId = targetReport.WorkAssignmentId,
            DataOrigin = WorkReportDataOrigin.PartialMapping,
            CumulativeContributionMode = WorkReportCumulativeContributionMode.Include,
            SourceReports = sourceReports.Select(x => new DynamicFlowMappingSourceReportDto
            {
                ReportId = x.Report.Id,
                WorkAssignmentId = x.Report.WorkAssignmentId,
                FlowStepId = x.FlowStepId,
                FlowStepCode = x.FlowStepCode,
                PeriodInstanceKey = x.Report.PeriodInstanceKey
            }).ToList()
        };

        foreach (var rule in rules)
        {
            var normalizedRule = NormalizeRule(rule);
            var matchingSources = sourceReports
                .Where(source => SourceStepMatches(normalizedRule, source))
                .ToList();
            if (matchingSources.Count == 0)
            {
                response.Changes.Add(NoSourceChange(normalizedRule, "DYNAMIC_FLOW_MAPPING_SOURCE_REPORT_NOT_FOUND"));
                continue;
            }

            var sourceValues = matchingSources
                .SelectMany(source => ExtractSourceValues(normalizedRule, source))
                .Where(value => !IsBlank(value.Value))
                .ToList();
            if (sourceValues.Count == 0)
            {
                response.Changes.Add(NoSourceChange(normalizedRule, "DYNAMIC_FLOW_MAPPING_SOURCE_VALUE_EMPTY"));
                continue;
            }

            var targetKind = ResolveTargetKind(normalizedRule);
            if (targetKind == "FIELD")
            {
                var nextValue = TransformForField(sourceValues, normalizedRule);
                var firstSource = sourceValues[0];
                ApplyFieldTarget(
                    targetValues,
                    normalizedRule,
                    nextValue,
                    firstSource,
                    requestConflictPolicy,
                    requestContributionPolicy,
                    response);
                continue;
            }

            var tableValues = TransformForTable(sourceValues, normalizedRule);
            ApplyTableTarget(
                targetTables,
                normalizedRule,
                tableValues,
                requestConflictPolicy,
                requestContributionPolicy,
                response);
        }

        response.FieldValuesJson = targetFields.ToJsonString(JsonOptions);
        response.TableValuesJson = targetTables.ToJsonString(JsonOptions);
        response.CumulativeContributionPolicyJson = BuildContributionPolicyJson(response.Changes, requestContributionPolicy);
        response.SummarySourceJson = BuildSummarySourceJson(targetReport, sourceReports, rules, response.Changes, nowUtc);
        response.HasBlockingConflicts = response.Changes.Any(x => string.Equals(x.Status, "CONFLICT", StringComparison.Ordinal));
        return response;
    }

    private static DynamicFlowMappingRuleDto NormalizeRule(DynamicFlowMappingRuleDto rule)
        => new()
        {
            MappingId = NormalizeText(rule.MappingId) ?? string.Empty,
            MappingVersion = rule.MappingVersion <= 0 ? 1 : rule.MappingVersion,
            SourceStepId = NormalizeText(rule.SourceStepId),
            SourceStepCode = NormalizeText(rule.SourceStepCode),
            SourceFieldId = NormalizeText(rule.SourceFieldId),
            SourceFieldKey = NormalizeText(rule.SourceFieldKey),
            SourceBlockId = NormalizeText(rule.SourceBlockId),
            SourceColumnKey = NormalizeText(rule.SourceColumnKey),
            TargetStepId = NormalizeText(rule.TargetStepId),
            TargetStepCode = NormalizeText(rule.TargetStepCode),
            TargetFieldId = NormalizeText(rule.TargetFieldId),
            TargetFieldKey = NormalizeText(rule.TargetFieldKey),
            TargetBlockId = NormalizeText(rule.TargetBlockId),
            TargetColumnKey = NormalizeText(rule.TargetColumnKey),
            ConceptCode = NormalizeText(rule.ConceptCode),
            DataType = NormalizeUpper(rule.DataType),
            JoinKey = NormalizeText(rule.JoinKey),
            ValueTransform = NormalizeUpper(rule.ValueTransform),
            ConflictPolicy = NormalizeUpper(rule.ConflictPolicy),
            ContributionPolicy = NormalizeUpper(rule.ContributionPolicy)
        };

    private static bool SourceStepMatches(DynamicFlowMappingRuleDto rule, DynamicFlowMappingSourceReport source)
    {
        if (!string.IsNullOrWhiteSpace(rule.SourceStepId) &&
            !string.Equals(rule.SourceStepId, "*", StringComparison.Ordinal) &&
            !string.Equals(rule.SourceStepId, source.FlowStepId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.SourceStepCode) &&
            !string.Equals(rule.SourceStepCode, "*", StringComparison.Ordinal) &&
            !string.Equals(rule.SourceStepCode, source.FlowStepCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<SourceValue> ExtractSourceValues(
        DynamicFlowMappingRuleDto rule,
        DynamicFlowMappingSourceReport source)
    {
        if (!string.IsNullOrWhiteSpace(rule.SourceFieldId) || !string.IsNullOrWhiteSpace(rule.SourceFieldKey))
        {
            var values = ExtractFieldValues(source.FieldValuesJson);
            foreach (var key in DistinctKeys(rule.SourceFieldId, rule.SourceFieldKey))
            {
                if (values.TryGetValue(key, out var value))
                {
                    yield return new SourceValue(source.Report.Id, key, null, null, value?.DeepClone());
                    yield break;
                }
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(rule.SourceBlockId) || string.IsNullOrWhiteSpace(rule.SourceColumnKey))
            yield break;

        foreach (var value in ExtractTableColumnValues(
                     source.Report.Id,
                     source.TableValuesJson,
                     rule.SourceBlockId,
                     rule.SourceColumnKey))
        {
            yield return value;
        }
    }

    private static void ApplyFieldTarget(
        JsonObject targetValues,
        DynamicFlowMappingRuleDto rule,
        JsonNode? nextValue,
        SourceValue source,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response)
    {
        var targetKey = FirstNonBlank(rule.TargetFieldId, rule.TargetFieldKey) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetKey))
            return;

        targetValues.TryGetPropertyValue(targetKey, out var previousValue);
        var conflictPolicy = ResolveConflictPolicy(rule, requestConflictPolicy);
        var status = ResolveWriteStatus(previousValue, nextValue, conflictPolicy, out var reason);
        if (status == "APPLIED")
            targetValues[targetKey] = nextValue?.DeepClone();

        response.Changes.Add(new DynamicFlowMappingChangeDto
        {
            MappingId = rule.MappingId,
            MappingVersion = rule.MappingVersion,
            TargetKind = "FIELD",
            TargetKey = targetKey,
            SourceReportId = source.SourceReportId,
            SourceKey = source.SourceKey,
            PreviousValueJson = StableJson(previousValue),
            NextValueJson = StableJson(nextValue),
            Status = status,
            Reason = reason,
            ConceptCode = rule.ConceptCode,
            ContributionPolicy = ResolveContributionPolicy(rule, requestContributionPolicy)
        });
    }

    private static void ApplyTableTarget(
        JsonObject targetTables,
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<SourceValue> values,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response)
    {
        var blockId = rule.TargetBlockId ?? string.Empty;
        var columnKey = rule.TargetColumnKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(blockId) || string.IsNullOrWhiteSpace(columnKey))
            return;

        var block = GetOrCreateTableBlock(targetTables, blockId);
        var useFixedGrid = block["indexMap"] is JsonArray && block["values1D"] is JsonArray &&
                           !string.Equals(ReadString(block, "tableMode"), "APPEND_ROWS", StringComparison.OrdinalIgnoreCase);
        if (useFixedGrid)
        {
            ApplyFixedGridTarget(block, rule, values, requestConflictPolicy, requestContributionPolicy, response);
            return;
        }

        ApplyAppendRowsTarget(block, rule, values, requestConflictPolicy, requestContributionPolicy, response);
    }

    private static void ApplyAppendRowsTarget(
        JsonObject block,
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<SourceValue> values,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response)
    {
        var blockId = rule.TargetBlockId ?? string.Empty;
        var columnKey = rule.TargetColumnKey ?? string.Empty;
        var rows = EnsureArray(block, "rows");
        var append = string.Equals(ResolveConflictPolicy(rule, requestConflictPolicy), "APPEND", StringComparison.Ordinal);

        for (var index = 0; index < values.Count; index++)
        {
            var source = values[index];
            var row = append
                ? AddAppendRow(rows, source.RowKey)
                : ResolveAppendRow(rows, source.RowKey, index);
            var cells = EnsureObject(row, "cells");
            cells.TryGetPropertyValue(columnKey, out var previousValue);

            var conflictPolicy = ResolveConflictPolicy(rule, requestConflictPolicy);
            var status = ResolveWriteStatus(previousValue, source.Value, conflictPolicy, out var reason);
            if (status == "APPLIED")
                cells[columnKey] = source.Value?.DeepClone();

            response.Changes.Add(new DynamicFlowMappingChangeDto
            {
                MappingId = rule.MappingId,
                MappingVersion = rule.MappingVersion,
                TargetKind = "TABLE_COLUMN",
                TargetKey = $"{blockId}:{columnKey}",
                SourceReportId = source.SourceReportId,
                SourceKey = source.SourceKey,
                PreviousValueJson = StableJson(previousValue),
                NextValueJson = StableJson(source.Value),
                Status = status,
                Reason = reason,
                ConceptCode = rule.ConceptCode,
                ContributionPolicy = ResolveContributionPolicy(rule, requestContributionPolicy)
            });
        }
    }

    private static void ApplyFixedGridTarget(
        JsonObject block,
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<SourceValue> values,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response)
    {
        var blockId = rule.TargetBlockId ?? string.Empty;
        var columnKey = rule.TargetColumnKey ?? string.Empty;
        var valueArray = EnsureArray(block, "values1D");
        var targetIndexes = ResolveIndexMapIndexes(block, columnKey);
        if (targetIndexes.Count == 0)
            targetIndexes.Add(0);

        for (var index = 0; index < values.Count; index++)
        {
            var source = values[index];
            var targetIndex = targetIndexes[Math.Min(index, targetIndexes.Count - 1)];
            while (valueArray.Count <= targetIndex)
                valueArray.Add(null);

            var previousValue = valueArray[targetIndex];
            var conflictPolicy = ResolveConflictPolicy(rule, requestConflictPolicy);
            var status = ResolveWriteStatus(previousValue, source.Value, conflictPolicy, out var reason);
            if (status == "APPLIED")
                valueArray[targetIndex] = source.Value?.DeepClone();

            response.Changes.Add(new DynamicFlowMappingChangeDto
            {
                MappingId = rule.MappingId,
                MappingVersion = rule.MappingVersion,
                TargetKind = "TABLE_COLUMN",
                TargetKey = $"{blockId}:{columnKey}",
                SourceReportId = source.SourceReportId,
                SourceKey = source.SourceKey,
                PreviousValueJson = StableJson(previousValue),
                NextValueJson = StableJson(source.Value),
                Status = status,
                Reason = reason,
                ConceptCode = rule.ConceptCode,
                ContributionPolicy = ResolveContributionPolicy(rule, requestContributionPolicy)
            });
        }
    }

    private static JsonNode? TransformForField(List<SourceValue> values, DynamicFlowMappingRuleDto rule)
    {
        var transform = ResolveTransform(rule);
        if (transform == "SUM")
            return JsonValue.Create(values.Sum(x => ToDecimal(x.Value) ?? 0m));
        if (transform == "COUNT")
            return JsonValue.Create(values.Count(x => !IsBlank(x.Value)));
        if (transform == "TEXT_JOIN")
            return JsonValue.Create(string.Join(", ", values.Select(x => ToDisplayText(x.Value)).Where(x => !string.IsNullOrWhiteSpace(x))));
        if (transform == "JSON")
            return new JsonArray(values.Select(x => x.Value?.DeepClone()).ToArray());

        var first = values.FirstOrDefault(x => !IsBlank(x.Value));
        return first?.Value?.DeepClone();
    }

    private static IReadOnlyList<SourceValue> TransformForTable(List<SourceValue> values, DynamicFlowMappingRuleDto rule)
    {
        var transform = ResolveTransform(rule);
        if (transform is "SUM" or "COUNT" or "TEXT_JOIN" or "JSON")
        {
            return new List<SourceValue>
            {
                values[0] with { Value = TransformForField(values, rule) }
            };
        }

        return values.Select(x => x with { Value = x.Value?.DeepClone() }).ToList();
    }

    private static Dictionary<string, JsonNode?> ExtractFieldValues(string? json)
    {
        var root = ParseObject(json);
        var values = root?["values"] as JsonObject ?? root;
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (values is null)
            return result;

        foreach (var item in values)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
                result[item.Key.Trim()] = item.Value?.DeepClone();
        }

        return result;
    }

    private static IEnumerable<SourceValue> ExtractTableColumnValues(
        string sourceReportId,
        string? tableValuesJson,
        string blockId,
        string columnKey)
    {
        var root = ParseTableValuesRoot(tableValuesJson);
        if (root?["blocks"] is not JsonArray blocks)
            yield break;

        foreach (var blockNode in blocks.OfType<JsonObject>())
        {
            var itemBlockId = ReadString(blockNode, "blockId", "id");
            if (!string.Equals(itemBlockId, blockId, StringComparison.Ordinal))
                continue;

            foreach (var value in ExtractFixedGridValues(sourceReportId, blockNode, blockId, columnKey))
                yield return value;
            foreach (var value in ExtractAppendRowValues(sourceReportId, blockNode, blockId, columnKey))
                yield return value;
            foreach (var value in ExtractAppendColumnValues(sourceReportId, blockNode, blockId, columnKey))
                yield return value;
            foreach (var value in ExtractMatrixCellValues(sourceReportId, blockNode, blockId, columnKey))
                yield return value;
        }
    }

    private static IEnumerable<SourceValue> ExtractFixedGridValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey)
    {
        if (block["values1D"] is not JsonArray values)
            yield break;

        foreach (var item in ReadIndexMap(block).Where(x => string.Equals(x.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase)))
        {
            if (item.Index < 0 || item.Index >= values.Count)
                continue;

            yield return new SourceValue(
                sourceReportId,
                $"{blockId}:{columnKey}",
                item.RowKey,
                item.Index,
                values[item.Index]?.DeepClone());
        }
    }

    private static IEnumerable<SourceValue> ExtractAppendRowValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey)
    {
        if (block["rows"] is not JsonArray rows)
            yield break;

        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is not JsonObject row || row["cells"] is not JsonObject cells)
                continue;

            if (!TryGetPropertyCaseInsensitive(cells, columnKey, out var value))
                continue;

            yield return new SourceValue(
                sourceReportId,
                $"{blockId}:{columnKey}",
                ReadString(row, "rowKey", "rowInstanceId") ?? index.ToString(CultureInfo.InvariantCulture),
                index,
                value?.DeepClone());
        }
    }

    private static IEnumerable<SourceValue> ExtractAppendColumnValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey)
    {
        if (block["columns"] is not JsonArray columns)
            yield break;

        foreach (var column in columns.OfType<JsonObject>())
        {
            var itemColumnKey = ReadString(column, "columnKey", "key", "columnInstanceId");
            if (!string.Equals(itemColumnKey, columnKey, StringComparison.OrdinalIgnoreCase) ||
                column["cells"] is not JsonObject cells)
            {
                continue;
            }

            var rowIndex = 0;
            foreach (var cell in cells)
            {
                yield return new SourceValue(
                    sourceReportId,
                    $"{blockId}:{columnKey}",
                    cell.Key,
                    rowIndex++,
                    cell.Value?.DeepClone());
            }
        }
    }

    private static IEnumerable<SourceValue> ExtractMatrixCellValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey)
    {
        if (block["cells"] is not JsonArray cells)
            yield break;

        var index = 0;
        foreach (var cell in cells.OfType<JsonObject>())
        {
            var itemColumnKey = ReadString(cell, "columnKey");
            if (!string.Equals(itemColumnKey, columnKey, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            yield return new SourceValue(
                sourceReportId,
                $"{blockId}:{columnKey}",
                ReadString(cell, "rowKey") ?? index.ToString(CultureInfo.InvariantCulture),
                index++,
                cell["value"]?.DeepClone() ?? cell.DeepClone());
        }
    }

    private static JsonObject? ParseTableValuesRoot(string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return null;

        var expanded = Values1DCompression.ExpandTableValuesJson(tableValuesJson, JsonOptions) ?? tableValuesJson;
        return ParseObject(expanded);
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject EnsureValuesObject(JsonObject root)
    {
        if (root["values"] is JsonObject values)
            return values;

        var next = new JsonObject();
        foreach (var item in root.ToList())
        {
            root.Remove(item.Key);
            next[item.Key] = item.Value;
        }

        root["values"] = next;
        return next;
    }

    private static JsonArray EnsureBlocksArray(JsonObject root)
        => EnsureArray(root, "blocks");

    private static JsonArray EnsureArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonArray array)
            return array;

        array = new JsonArray();
        root[propertyName] = array;
        return array;
    }

    private static JsonObject EnsureObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject obj)
            return obj;

        obj = new JsonObject();
        root[propertyName] = obj;
        return obj;
    }

    private static JsonObject GetOrCreateTableBlock(JsonObject root, string blockId)
    {
        var blocks = EnsureBlocksArray(root);
        foreach (var block in blocks.OfType<JsonObject>())
        {
            if (string.Equals(ReadString(block, "blockId", "id"), blockId, StringComparison.Ordinal))
                return block;
        }

        var next = new JsonObject
        {
            ["blockId"] = blockId,
            ["tableMode"] = "APPEND_ROWS",
            ["rows"] = new JsonArray()
        };
        blocks.Add(next);
        return next;
    }

    private static JsonObject ResolveAppendRow(JsonArray rows, string? rowKey, int rowIndex)
    {
        if (!string.IsNullOrWhiteSpace(rowKey))
        {
            foreach (var row in rows.OfType<JsonObject>())
            {
                var existing = ReadString(row, "rowKey", "rowInstanceId");
                if (string.Equals(existing, rowKey, StringComparison.Ordinal))
                    return row;
            }
        }

        if (rowIndex >= 0 && rowIndex < rows.Count && rows[rowIndex] is JsonObject byIndex)
            return byIndex;

        return AddAppendRow(rows, rowKey);
    }

    private static JsonObject AddAppendRow(JsonArray rows, string? rowKey)
    {
        var row = new JsonObject
        {
            ["rowOrder"] = rows.Count,
            ["cells"] = new JsonObject()
        };
        if (!string.IsNullOrWhiteSpace(rowKey))
            row["rowKey"] = rowKey;
        rows.Add(row);
        return row;
    }

    private static List<int> ResolveIndexMapIndexes(JsonObject block, string columnKey)
        => ReadIndexMap(block)
            .Where(x => string.Equals(x.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Index)
            .Where(x => x >= 0)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

    private static List<TableIndexMapItem> ReadIndexMap(JsonObject block)
    {
        var result = new List<TableIndexMapItem>();
        if (block["indexMap"] is not JsonArray indexMap)
            return result;

        foreach (var item in indexMap.OfType<JsonObject>())
        {
            var index = ReadInt(item, "index", "i") ?? -1;
            result.Add(new TableIndexMapItem(
                index,
                ReadString(item, "rowKey", "row", "r"),
                ReadString(item, "columnKey", "column", "col", "c"),
                ReadString(item, "metricKey", "metric")));
        }

        return result;
    }

    private static string ResolveTargetKind(DynamicFlowMappingRuleDto rule)
        => !string.IsNullOrWhiteSpace(rule.TargetFieldId) || !string.IsNullOrWhiteSpace(rule.TargetFieldKey)
            ? "FIELD"
            : "TABLE_COLUMN";

    private static string ResolveTransform(DynamicFlowMappingRuleDto rule)
        => NormalizeUpper(rule.ValueTransform) switch
        {
            "FIRST" => "FIRST_NON_BLANK",
            "SUM" => "SUM",
            "COUNT" => "COUNT",
            "TEXT_JOIN" => "TEXT_JOIN",
            "JSON" => "JSON",
            _ => "COPY"
        };

    private static string ResolveConflictPolicy(DynamicFlowMappingRuleDto rule, string? requestConflictPolicy)
    {
        var policy = NormalizeUpper(rule.ConflictPolicy) ?? NormalizeUpper(requestConflictPolicy);
        return policy switch
        {
            "TARGET_WINS" => "TARGET_WINS",
            "KEEP_TARGET" => "TARGET_WINS",
            "ERROR" => "ERROR_ON_CONFLICT",
            "ERROR_ON_CONFLICT" => "ERROR_ON_CONFLICT",
            "APPEND" => "APPEND",
            "MERGE" => "MERGE",
            "SOURCE_WINS" => "OVERWRITE",
            _ => "OVERWRITE"
        };
    }

    private static string ResolveContributionPolicy(DynamicFlowMappingRuleDto rule, string? requestContributionPolicy)
        => NormalizeUpper(rule.ContributionPolicy) ?? NormalizeUpper(requestContributionPolicy) ?? "EXCLUDE";

    private static string ResolveWriteStatus(
        JsonNode? previousValue,
        JsonNode? nextValue,
        string conflictPolicy,
        out string? reason)
    {
        reason = null;
        if (StableJson(previousValue) == StableJson(nextValue))
            return "UNCHANGED";

        if (!IsBlank(previousValue) && !IsBlank(nextValue))
        {
            if (conflictPolicy == "TARGET_WINS")
            {
                reason = "DYNAMIC_FLOW_MAPPING_TARGET_VALUE_KEPT";
                return "SKIPPED";
            }

            if (conflictPolicy == "ERROR_ON_CONFLICT")
            {
                reason = "DYNAMIC_FLOW_MAPPING_TARGET_VALUE_CONFLICT";
                return "CONFLICT";
            }
        }

        return "APPLIED";
    }

    private static DynamicFlowMappingChangeDto NoSourceChange(DynamicFlowMappingRuleDto rule, string reason)
        => new()
        {
            MappingId = rule.MappingId,
            MappingVersion = rule.MappingVersion,
            TargetKind = ResolveTargetKind(rule),
            TargetKey = ResolveTargetKind(rule) == "FIELD"
                ? FirstNonBlank(rule.TargetFieldId, rule.TargetFieldKey) ?? string.Empty
                : $"{rule.TargetBlockId}:{rule.TargetColumnKey}",
            Status = "SKIPPED",
            Reason = reason,
            ConceptCode = rule.ConceptCode,
            ContributionPolicy = ResolveContributionPolicy(rule, null)
        };

    private static string? BuildContributionPolicyJson(
        IReadOnlyList<DynamicFlowMappingChangeDto> changes,
        string? requestContributionPolicy)
    {
        var rules = new JsonArray();
        foreach (var change in changes.Where(x => x.Status is "APPLIED" or "UNCHANGED"))
        {
            var policy = NormalizeUpper(change.ContributionPolicy) ?? NormalizeUpper(requestContributionPolicy) ?? "EXCLUDE";
            var mode = policy == "INCLUDE"
                ? WorkReportCumulativeContributionMode.Include
                : WorkReportCumulativeContributionMode.Exclude;
            if (mode == WorkReportCumulativeContributionMode.Include)
                continue;

            if (change.TargetKind == "FIELD")
            {
                rules.Add(new JsonObject
                {
                    ["targetKind"] = "FIELD",
                    ["targetKey"] = change.TargetKey,
                    ["mode"] = mode,
                    ["source"] = "DYNAMIC_FLOW_MAPPING",
                    ["mappingId"] = change.MappingId
                });
                continue;
            }

            var parts = change.TargetKey.Split(':', 2);
            rules.Add(new JsonObject
            {
                ["targetKind"] = "TABLE",
                ["blockId"] = parts.Length > 0 ? parts[0] : null,
                ["columnKey"] = parts.Length > 1 ? parts[1] : null,
                ["mode"] = mode,
                ["source"] = "DYNAMIC_FLOW_MAPPING",
                ["mappingId"] = change.MappingId
            });
        }

        if (rules.Count == 0)
            return null;

        return new JsonObject
        {
            ["defaultMode"] = WorkReportCumulativeContributionMode.Include,
            ["rules"] = rules
        }.ToJsonString(JsonOptions);
    }

    private static string BuildSummarySourceJson(
        WorkAssignmentReport targetReport,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        IReadOnlyList<DynamicFlowMappingRuleDto> rules,
        IReadOnlyList<DynamicFlowMappingChangeDto> changes,
        DateTime nowUtc)
        => JsonSerializer.Serialize(new
        {
            kind = "DYNAMIC_FLOW_MAPPING",
            mapKind = "DYNAMIC_FLOW_MAPPING",
            appliedAtUtc = nowUtc,
            targetReportId = targetReport.Id,
            targetAssignmentId = targetReport.WorkAssignmentId,
            sourceReportIds = sourceReports.Select(x => x.Report.Id).Distinct(StringComparer.Ordinal).ToList(),
            sourceAssignmentIds = sourceReports.Select(x => x.Report.WorkAssignmentId).Distinct(StringComparer.Ordinal).ToList(),
            mappingRules = rules.Select(x => new
            {
                x.MappingId,
                x.MappingVersion,
                x.SourceStepId,
                x.SourceStepCode,
                x.SourceFieldId,
                x.SourceFieldKey,
                x.SourceBlockId,
                x.SourceColumnKey,
                x.TargetFieldId,
                x.TargetFieldKey,
                x.TargetBlockId,
                x.TargetColumnKey,
                x.ConceptCode,
                x.DataType,
                x.JoinKey,
                x.ValueTransform,
                x.ConflictPolicy,
                x.ContributionPolicy
            }).ToList(),
            changes = changes.Select(x => new
            {
                x.MappingId,
                x.MappingVersion,
                x.TargetKind,
                x.TargetKey,
                x.SourceReportId,
                x.SourceKey,
                x.Status,
                x.Reason,
                x.ConceptCode,
                x.ContributionPolicy
            }).ToList()
        }, JsonOptions);

    private static bool TryGetPropertyCaseInsensitive(JsonObject obj, string key, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(key, out value))
            return true;

        foreach (var item in obj)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static IEnumerable<string> DistinctKeys(params string?[] keys)
        => keys
            .Select(NormalizeText)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal);

    private static bool IsBlank(JsonNode? value)
    {
        if (value is null)
            return true;

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var text))
                return string.IsNullOrWhiteSpace(text);
        }

        if (value is JsonArray array)
            return array.Count == 0;

        return string.Equals(value.ToJsonString(JsonOptions), "null", StringComparison.Ordinal);
    }

    private static decimal? ToDecimal(JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
            return null;

        if (jsonValue.TryGetValue<decimal>(out var decimalValue))
            return decimalValue;
        if (jsonValue.TryGetValue<double>(out var doubleValue))
            return (decimal)doubleValue;
        if (jsonValue.TryGetValue<string>(out var text) &&
            decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ToDisplayText(JsonNode? value)
    {
        if (value is null)
            return null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return text;
        return value.ToJsonString(JsonOptions);
    }

    private static string? StableJson(JsonNode? node)
        => node?.ToJsonString(JsonOptions);

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeUpper(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? ReadString(JsonObject root, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (root.TryGetPropertyValue(property, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonObject root, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (root.TryGetPropertyValue(property, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<int>(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private sealed record SourceValue(
        string SourceReportId,
        string SourceKey,
        string? RowKey,
        int? RowIndex,
        JsonNode? Value);

    private sealed record TableIndexMapItem(
        int Index,
        string? RowKey,
        string? ColumnKey,
        string? MetricKey);
}

internal sealed record DynamicFlowMappingSourceReport(
    WorkAssignmentReport Report,
    string? FlowStepId,
    string? FlowStepCode,
    string? FieldValuesJson,
    string? TableValuesJson);
