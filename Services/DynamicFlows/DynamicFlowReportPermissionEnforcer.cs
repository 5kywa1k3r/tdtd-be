using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services;

namespace tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowReportPermissionEnforcer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static List<DynamicFlowReportPermissionViolation> FindWriteViolations(
        DynamicFlowPolicyEvaluationResult permissions,
        string? currentFieldValuesJson,
        string? nextFieldValuesJson,
        string? currentTableValuesJson,
        string? nextTableValuesJson)
    {
        var result = new List<DynamicFlowReportPermissionViolation>();
        if (permissions is null)
            return result;

        var currentFields = ExtractFieldValues(currentFieldValuesJson);
        var nextFields = ExtractFieldValues(nextFieldValuesJson);
        foreach (var key in ChangedKeys(currentFields, nextFields))
        {
            var matchingPermissions = permissions.Fields.Values
                .Where(permission => FieldTargetKeys(permission)
                    .Any(targetKey => string.Equals(
                        targetKey,
                        key,
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (permissions.DenyAllFields ||
                matchingPermissions.Count == 0 ||
                matchingPermissions.Any(IsWriteDenied))
            {
                result.Add(new DynamicFlowReportPermissionViolation(
                    "FIELD",
                    key,
                    matchingPermissions
                        .FirstOrDefault(IsWriteDenied)?
                        .SourcePolicyId,
                    "DYNAMIC_FLOW_FIELD_WRITE_FORBIDDEN"));
            }
        }

        var currentTables = ExtractTableColumnValues(currentTableValuesJson);
        var nextTables = ExtractTableColumnValues(nextTableValuesJson);
        var currentBlockValues = ExtractBlockValues(currentTableValuesJson);
        var nextBlockValues = ExtractBlockValues(nextTableValuesJson);
        var changedColumns = ChangedKeys(currentTables, nextTables).ToList();
        foreach (var key in changedColumns)
        {
            var matchingPermissions = permissions.TableColumns.Values
                .Where(permission => string.Equals(
                    $"{permission.BlockId}:{permission.ColumnKey}",
                    key,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (permissions.DenyAllTableColumns ||
                matchingPermissions.Count == 0 ||
                matchingPermissions.Any(IsWriteDenied))
            {
                result.Add(new DynamicFlowReportPermissionViolation(
                    "TABLE_COLUMN",
                    key,
                    matchingPermissions
                        .FirstOrDefault(IsWriteDenied)?
                        .SourcePolicyId,
                    "DYNAMIC_FLOW_TABLE_COLUMN_WRITE_FORBIDDEN"));
            }
        }

        if (changedColumns.Count == 0)
        {
            foreach (var blockId in ChangedKeys(currentBlockValues, nextBlockValues))
            {
                result.Add(new DynamicFlowReportPermissionViolation(
                    "TABLE_COLUMN",
                    $"{blockId}:*",
                    null,
                    "DYNAMIC_FLOW_TABLE_COLUMN_WRITE_FORBIDDEN"));
            }
        }

        return result;
    }

    public static List<DynamicFlowReportPermissionViolation> FindRequiredViolations(
        DynamicFlowPolicyEvaluationResult permissions,
        string? fieldValuesJson,
        string? tableValuesJson)
    {
        var result = new List<DynamicFlowReportPermissionViolation>();
        if (permissions is null)
            return result;

        var fieldRoot = ParseObject(fieldValuesJson);
        var fieldValues = fieldRoot?["values"] as JsonObject ?? fieldRoot;
        foreach (var permission in permissions.Fields.Values.Where(item => item.Required && !item.Hidden))
        {
            var hasValue = false;
            JsonNode? value = null;
            foreach (var key in FieldTargetKeys(permission))
            {
                if (fieldValues is not null && TryGetProperty(fieldValues, key, out _, out value))
                {
                    hasValue = true;
                    break;
                }
            }

            if (hasValue && !IsBlankJson(value))
                continue;

            result.Add(new DynamicFlowReportPermissionViolation(
                "FIELD",
                FirstNonBlank(permission.TargetKey, permission.FieldId, permission.FieldKey) ?? string.Empty,
                permission.SourcePolicyId,
                "DYNAMIC_FLOW_FIELD_REQUIRED"));
        }

        foreach (var permission in permissions.TableColumns.Values.Where(item => item.Required && !item.Hidden))
        {
            var requiredValues = ExtractRequiredTableColumnValues(
                tableValuesJson,
                permission.BlockId,
                permission.ColumnKey);
            if (!requiredValues.Applies || requiredValues.Values.All(value => !IsBlankJson(value)))
                continue;

            result.Add(new DynamicFlowReportPermissionViolation(
                "TABLE_COLUMN",
                $"{permission.BlockId}:{permission.ColumnKey}",
                permission.SourcePolicyId,
                "DYNAMIC_FLOW_TABLE_COLUMN_REQUIRED"));
        }

        return result;
    }

    public static DynamicFlowReportWritablePayload PreserveUnreadableValues(
        DynamicFlowPolicyEvaluationResult permissions,
        string? currentFieldValuesJson,
        string? nextFieldValuesJson,
        string? currentTableValuesJson,
        string? nextTableValuesJson)
    {
        if (permissions is null)
            return new DynamicFlowReportWritablePayload(nextFieldValuesJson, nextTableValuesJson);

        return new DynamicFlowReportWritablePayload(
            RestoreUnreadableFieldValues(permissions, currentFieldValuesJson, nextFieldValuesJson),
            RestoreUnreadableTableValues(permissions, currentTableValuesJson, nextTableValuesJson));
    }

    public static DynamicFlowMappingPreviewResponse ApplyMappingPreviewReadRestrictions(
        DynamicFlowPolicyEvaluationResult permissions,
        DynamicFlowMappingPreviewResponse preview)
    {
        var readable = ApplyReadRestrictions(
            permissions,
            null,
            preview.FieldValuesJson,
            preview.TableValuesJson,
            null);
        preview.FieldValuesJson = readable.FieldValuesJson;
        preview.TableValuesJson = readable.TableValuesJson;
        preview.Changes = preview.Changes
            .Where(change => IsReadableMappingTarget(permissions, change))
            .ToList();
        foreach (var source in preview.Changes.SelectMany(change => change.Sources))
            source.ValueJson = null;
        preview.SummarySourceJson = RedactSummarySourceValues(preview.SummarySourceJson);
        return preview;
    }

    public static string? RedactSummarySourceValues(string? summarySourceJson)
    {
        var root = ParseObject(summarySourceJson);
        if (root is null ||
            !string.Equals(ReadString(root, "kind"), "DYNAMIC_FLOW_MAPPING", StringComparison.OrdinalIgnoreCase))
        {
            return summarySourceJson;
        }

        if (root["changes"] is JsonArray changes)
        {
            foreach (var source in changes
                         .OfType<JsonObject>()
                         .SelectMany(change => (change["sources"] as JsonArray)?.OfType<JsonObject>()
                             ?? Enumerable.Empty<JsonObject>()))
            {
                RemoveProperty(source, "valueJson");
            }
        }

        return root.ToJsonString(JsonOptions);
    }

    public static DynamicFlowReportReadablePayload ApplyReadRestrictions(
        DynamicFlowPolicyEvaluationResult permissions,
        string? values1DJson,
        string? fieldValuesJson,
        string? tableValuesJson,
        string? topLevelDynamicExcelTemplateId)
    {
        if (permissions is null)
            return new DynamicFlowReportReadablePayload(values1DJson, fieldValuesJson, tableValuesJson);

        var readableFieldValuesJson = permissions.DenyAllFields
            ? null
            : RedactFieldValues(permissions, fieldValuesJson);
        var readableTables = permissions.DenyAllTableColumns
            ? new DynamicFlowReportReadableTablePayload("[]", null)
            : RedactTableValues(
                permissions,
                values1DJson,
                tableValuesJson,
                topLevelDynamicExcelTemplateId);

        return new DynamicFlowReportReadablePayload(
            readableTables.Values1DJson,
            readableFieldValuesJson,
            readableTables.TableValuesJson);
    }

    private static bool IsWriteDenied(DynamicFlowFieldPermissionDto permission)
        => !permission.Write || permission.Hidden || permission.Locked;

    private static bool IsWriteDenied(DynamicFlowTableColumnPermissionDto permission)
        => !permission.Write || permission.Hidden || permission.Locked;

    private static IEnumerable<string> FieldTargetKeys(DynamicFlowFieldPermissionDto permission)
    {
        if (!string.IsNullOrWhiteSpace(permission.FieldId))
            yield return permission.FieldId.Trim();
        if (!string.IsNullOrWhiteSpace(permission.FieldKey) &&
            !string.Equals(permission.FieldKey, permission.FieldId, StringComparison.OrdinalIgnoreCase))
        {
            yield return permission.FieldKey.Trim();
        }
        if (!string.IsNullOrWhiteSpace(permission.TargetKey) &&
            !string.Equals(permission.TargetKey, permission.FieldId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(permission.TargetKey, permission.FieldKey, StringComparison.OrdinalIgnoreCase))
        {
            yield return permission.TargetKey.Trim();
        }
    }

    private static Dictionary<string, string?> ExtractFieldValues(string? json)
    {
        var root = ParseObject(json);
        var values = root?["values"] as JsonObject ?? root;
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
            return result;

        foreach (var item in values)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
                result[item.Key.Trim()] = StableJson(item.Value);
        }

        return result;
    }

    private static RequiredTableColumnValues ExtractRequiredTableColumnValues(
        string? json,
        string blockId,
        string columnKey)
    {
        var root = ParseObject(Values1DCompression.ExpandTableValuesJson(json, JsonOptions) ?? json);
        var block = root?["blocks"] is JsonArray blocks
            ? FindBlock(blocks, blockId)
            : null;
        if (block is null)
            return new RequiredTableColumnValues(true, new JsonNode?[] { null });

        var tableMode = ReadString(block, "tableMode")?.Trim().ToUpperInvariant();
        if (tableMode == "APPEND_ROWS")
        {
            if (block["rows"] is JsonArray rows && rows.Count > 0)
            {
                var rowValues = new List<JsonNode?>();
                foreach (var rowNode in rows)
                {
                    if (rowNode is not JsonObject row)
                    {
                        rowValues.Add(null);
                        continue;
                    }

                    if (row["cells"] is JsonObject cells &&
                        TryGetProperty(cells, columnKey, out _, out var cellValue))
                    {
                        rowValues.Add(cellValue);
                    }
                    else if (TryGetProperty(row, columnKey, out _, out var directValue))
                    {
                        rowValues.Add(directValue);
                    }
                    else
                    {
                        rowValues.Add(null);
                    }
                }

                return new RequiredTableColumnValues(true, rowValues);
            }

            return ExtractRequiredAppendRowSlotValues(block, columnKey);
        }

        if (block["values1D"] is JsonArray values && block["valueSlots"] is JsonArray slots)
        {
            var slotValues = slots
                .OfType<JsonObject>()
                .Where(slot => string.Equals(
                    ReadString(slot, "columnKey"),
                    columnKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(slot => ReadInt(slot, "index"))
                .Where(index => index.HasValue)
                .Select(index => index!.Value >= 0 && index.Value < values.Count ? values[index.Value] : null)
                .ToList();
            if (slotValues.Count > 0)
                return new RequiredTableColumnValues(true, slotValues);
        }

        if (block["cells"] is JsonArray matrixCells)
        {
            var cellValues = matrixCells
                .OfType<JsonObject>()
                .Where(cell => string.Equals(
                    ReadString(cell, "columnKey"),
                    columnKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(cell => cell["value"])
                .ToList();
            if (cellValues.Count > 0)
                return new RequiredTableColumnValues(true, cellValues);
        }

        if (block["columns"] is JsonArray columns)
        {
            var column = columns
                .OfType<JsonObject>()
                .FirstOrDefault(item => string.Equals(
                    ReadString(item, "columnKey", "key"),
                    columnKey,
                    StringComparison.OrdinalIgnoreCase));
            if (column?["values"] is JsonArray columnValues)
                return new RequiredTableColumnValues(true, columnValues.ToList());
            if (column?["cells"] is JsonObject columnCells)
                return new RequiredTableColumnValues(true, columnCells.Select(item => item.Value).ToList());
        }

        // Chính sách đã trỏ tới một cột phát hành nhưng dữ liệu chạy không còn đủ tọa độ.
        return new RequiredTableColumnValues(true, new JsonNode?[] { null });
    }

    private static RequiredTableColumnValues ExtractRequiredAppendRowSlotValues(
        JsonObject block,
        string columnKey)
    {
        if (block["values1D"] is not JsonArray values)
            return new RequiredTableColumnValues(false, Array.Empty<JsonNode?>());

        if (block["valueSlots"] is not JsonArray slots)
        {
            return values.Any(value => !IsBlankJson(value))
                ? new RequiredTableColumnValues(true, new JsonNode?[] { null })
                : new RequiredTableColumnValues(false, Array.Empty<JsonNode?>());
        }

        var rows = slots
            .OfType<JsonObject>()
            .Select((slot, order) => new
            {
                Slot = slot,
                Order = order,
                Index = ReadInt(slot, "index"),
                RowKey = FirstNonBlank(
                    ReadString(slot, "rowKey"),
                    ReadInt(slot, "rowOffset")?.ToString(),
                    ReadInt(slot, "row")?.ToString())
            })
            .Where(item => item.Index.HasValue && !string.IsNullOrWhiteSpace(item.RowKey))
            .GroupBy(item => item.RowKey!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(item => item.Order))
            .ToList();

        if (rows.Count == 0)
        {
            return values.Any(value => !IsBlankJson(value))
                ? new RequiredTableColumnValues(true, new JsonNode?[] { null })
                : new RequiredTableColumnValues(false, Array.Empty<JsonNode?>());
        }

        var requiredValues = new List<JsonNode?>();
        foreach (var row in rows)
        {
            var active = row.Any(item =>
                item.Index!.Value >= 0 &&
                item.Index.Value < values.Count &&
                !IsBlankJson(values[item.Index.Value]));
            if (!active)
                continue;

            var targetSlot = row.FirstOrDefault(item => string.Equals(
                ReadString(item.Slot, "columnKey"),
                columnKey,
                StringComparison.OrdinalIgnoreCase));
            requiredValues.Add(
                targetSlot is not null &&
                targetSlot.Index!.Value >= 0 &&
                targetSlot.Index.Value < values.Count
                    ? values[targetSlot.Index.Value]
                    : null);
        }

        return requiredValues.Count == 0
            ? new RequiredTableColumnValues(false, Array.Empty<JsonNode?>())
            : new RequiredTableColumnValues(true, requiredValues);
    }

    private static Dictionary<string, string?> ExtractTableColumnValues(string? json)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var root = ParseObject(Values1DCompression.ExpandTableValuesJson(json, JsonOptions) ?? json);
        if (root?["blocks"] is not JsonArray blocks)
            return result;

        foreach (var item in blocks)
        {
            if (item is not JsonObject block)
                continue;

            var blockId = ReadString(block, "blockId", "id");
            if (string.IsNullOrWhiteSpace(blockId))
                continue;

            AddValueSlotColumns(result, blockId, block);
            AddCellArrayColumns(result, blockId, block["cells"] as JsonArray);
            AddRowsColumns(result, blockId, block["rows"] as JsonArray);
            AddColumnArrayColumns(result, blockId, block["columns"] as JsonArray);
        }

        return result;
    }

    private static bool AddValueSlotColumns(
        Dictionary<string, string?> target,
        string blockId,
        JsonObject block)
    {
        if (block["values1D"] is not JsonArray values ||
            block["valueSlots"] is not JsonArray slots)
        {
            return false;
        }

        var found = false;
        foreach (var slot in slots
                     .OfType<JsonObject>()
                     .Select(item => new
                     {
                         Item = item,
                         Index = ReadInt(item, "index")
                     })
                     .Where(item => item.Index.HasValue)
                     .OrderBy(item => item.Index))
        {
            var columnKey = ReadString(slot.Item, "columnKey");
            if (string.IsNullOrWhiteSpace(columnKey) ||
                slot.Index!.Value < 0 ||
                slot.Index.Value >= values.Count)
            {
                continue;
            }

            AppendColumnValue(target, blockId, columnKey, values[slot.Index.Value]);
            found = true;
        }

        return found;
    }

    private static string? RestoreUnreadableFieldValues(
        DynamicFlowPolicyEvaluationResult permissions,
        string? currentFieldValuesJson,
        string? nextFieldValuesJson)
    {
        var currentRoot = ParseObject(currentFieldValuesJson);
        if (currentRoot is null)
            return nextFieldValuesJson;

        var nextRoot = ParseObject(nextFieldValuesJson);
        if (nextRoot is null && !string.IsNullOrWhiteSpace(nextFieldValuesJson))
            return nextFieldValuesJson;
        nextRoot ??= new JsonObject();

        var currentValues = currentRoot["values"] as JsonObject ?? currentRoot;
        JsonObject nextValues;
        if (currentRoot["values"] is JsonObject)
        {
            nextValues = nextRoot["values"] as JsonObject ?? new JsonObject();
            nextRoot["values"] = nextValues;
        }
        else
        {
            nextValues = nextRoot;
        }

        foreach (var key in currentValues
                     .Select(item => item.Key)
                     .Where(key => !IsReadableFieldTarget(permissions, key))
                     .ToList())
        {
            RestorePropertyIfMissingOrNull(currentValues, nextValues, key);
        }

        return nextRoot.ToJsonString(JsonOptions);
    }

    private static string? RestoreUnreadableTableValues(
        DynamicFlowPolicyEvaluationResult permissions,
        string? currentTableValuesJson,
        string? nextTableValuesJson)
    {
        var currentExpanded = Values1DCompression.ExpandTableValuesJson(currentTableValuesJson, JsonOptions)
                              ?? currentTableValuesJson;
        var currentRoot = ParseObject(currentExpanded);
        if (currentRoot?["blocks"] is not JsonArray currentBlocks)
            return nextTableValuesJson;

        var nextExpanded = Values1DCompression.ExpandTableValuesJson(nextTableValuesJson, JsonOptions)
                           ?? nextTableValuesJson;
        var nextRoot = ParseObject(nextExpanded);
        if (nextRoot is null && !string.IsNullOrWhiteSpace(nextTableValuesJson))
            return nextTableValuesJson;
        if (nextRoot is null)
            return currentRoot.DeepClone().ToJsonString(JsonOptions);

        var nextBlocks = nextRoot["blocks"] as JsonArray;
        if (nextBlocks is null)
        {
            nextBlocks = new JsonArray();
            nextRoot["blocks"] = nextBlocks;
        }

        var deniedByBlock = ExtractTableColumnValues(currentTableValuesJson)
            .Keys
            .Select(SplitTableTargetKey)
            .Where(item =>
                item is not null &&
                !IsReadableTableTarget(
                    permissions,
                    item.Value.BlockId,
                    item.Value.ColumnKey))
            .Select(item => item!.Value)
            .GroupBy(item => item.BlockId, StringComparer.OrdinalIgnoreCase);
        foreach (var group in deniedByBlock)
        {
            var currentBlock = FindBlock(currentBlocks, group.Key);
            if (currentBlock is null)
                continue;

            var nextBlock = FindBlock(nextBlocks, group.Key);
            if (nextBlock is null)
            {
                nextBlocks.Add(currentBlock.DeepClone());
                continue;
            }

            foreach (var columnKey in group
                         .Select(item => item.ColumnKey)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                RestoreValueSlotColumn(currentBlock, nextBlock, columnKey);
                RestoreRowColumn(currentBlock, nextBlock, columnKey);
                RestoreColumnItems(currentBlock, nextBlock, "cells", columnKey);
                RestoreColumnItems(currentBlock, nextBlock, "columns", columnKey);
            }
        }

        return nextRoot.ToJsonString(JsonOptions);
    }

    private static JsonObject? FindBlock(JsonArray blocks, string blockId)
        => blocks
            .OfType<JsonObject>()
            .FirstOrDefault(block => string.Equals(
                ReadString(block, "blockId", "id"),
                blockId,
                StringComparison.OrdinalIgnoreCase));

    private static void RestoreValueSlotColumn(
        JsonObject currentBlock,
        JsonObject nextBlock,
        string columnKey)
    {
        if (currentBlock["values1D"] is not JsonArray currentValues ||
            currentBlock["valueSlots"] is not JsonArray currentSlots ||
            nextBlock["values1D"] is not JsonArray nextValues ||
            nextBlock["valueSlots"] is not JsonArray nextSlots)
        {
            return;
        }

        foreach (var currentSlot in currentSlots.OfType<JsonObject>())
        {
            if (!string.Equals(ReadString(currentSlot, "columnKey"), columnKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var currentIndex = ReadInt(currentSlot, "index");
            if (!currentIndex.HasValue || currentIndex.Value < 0 || currentIndex.Value >= currentValues.Count)
                continue;

            var nextSlot = nextSlots
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    string.Equals(ReadString(candidate, "columnKey"), columnKey, StringComparison.OrdinalIgnoreCase) &&
                    SameSlotRow(currentSlot, candidate));
            var nextIndex = nextSlot is null ? null : ReadInt(nextSlot, "index");
            if (!nextIndex.HasValue || nextIndex.Value < 0 || nextIndex.Value >= nextValues.Count)
                continue;

            if (IsNullJson(nextValues[nextIndex.Value]))
                nextValues[nextIndex.Value] = currentValues[currentIndex.Value]?.DeepClone();
        }
    }

    private static bool SameSlotRow(JsonObject left, JsonObject right)
    {
        var leftRowKey = ReadString(left, "rowKey");
        var rightRowKey = ReadString(right, "rowKey");
        if (leftRowKey is not null || rightRowKey is not null)
            return string.Equals(leftRowKey, rightRowKey, StringComparison.OrdinalIgnoreCase);

        foreach (var property in new[] { "rowOffset", "row" })
        {
            var leftValue = ReadInt(left, property);
            var rightValue = ReadInt(right, property);
            if (leftValue.HasValue || rightValue.HasValue)
                return leftValue == rightValue;
        }

        return ReadInt(left, "index") == ReadInt(right, "index");
    }

    private static void RestoreRowColumn(
        JsonObject currentBlock,
        JsonObject nextBlock,
        string columnKey)
    {
        if (currentBlock["rows"] is not JsonArray currentRows)
            return;
        if (nextBlock["rows"] is not JsonArray nextRows)
        {
            nextBlock["rows"] = currentRows.DeepClone();
            return;
        }

        for (var index = 0; index < currentRows.Count; index++)
        {
            if (currentRows[index] is not JsonObject currentRow)
                continue;

            var currentRowKey = ReadString(currentRow, "rowKey", "id");
            var nextRow = nextRows
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    currentRowKey is not null &&
                    string.Equals(ReadString(candidate, "rowKey", "id"), currentRowKey, StringComparison.OrdinalIgnoreCase))
                ?? (index < nextRows.Count ? nextRows[index] as JsonObject : null);
            if (nextRow is null)
            {
                nextRows.Add(currentRow.DeepClone());
                continue;
            }

            if (currentRow["cells"] is JsonObject currentCells)
            {
                var nextCells = nextRow["cells"] as JsonObject ?? new JsonObject();
                nextRow["cells"] = nextCells;
                RestorePropertyIfMissingOrNull(currentCells, nextCells, columnKey);
            }
            RestorePropertyIfMissingOrNull(currentRow, nextRow, columnKey);
        }
    }

    private static void RestoreColumnItems(
        JsonObject currentBlock,
        JsonObject nextBlock,
        string propertyName,
        string columnKey)
    {
        if (currentBlock[propertyName] is not JsonArray currentItems)
            return;
        if (nextBlock[propertyName] is not JsonArray nextItems)
        {
            nextBlock[propertyName] = currentItems.DeepClone();
            return;
        }

        foreach (var currentItem in currentItems.OfType<JsonObject>())
        {
            if (!string.Equals(ReadString(currentItem, "columnKey"), columnKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var nextItem = nextItems
                .OfType<JsonObject>()
                .FirstOrDefault(candidate => SameColumnItem(currentItem, candidate, columnKey));
            if (nextItem is null)
            {
                nextItems.Add(currentItem.DeepClone());
                continue;
            }

            RestorePropertyIfMissingOrNull(currentItem, nextItem, "value");
            RestorePropertyIfMissingOrNull(currentItem, nextItem, "values");
        }
    }

    private static bool SameColumnItem(JsonObject left, JsonObject right, string columnKey)
    {
        if (!string.Equals(ReadString(right, "columnKey"), columnKey, StringComparison.OrdinalIgnoreCase))
            return false;

        var leftRowKey = ReadString(left, "rowKey", "rowId");
        var rightRowKey = ReadString(right, "rowKey", "rowId");
        if (leftRowKey is not null || rightRowKey is not null)
            return string.Equals(leftRowKey, rightRowKey, StringComparison.OrdinalIgnoreCase);

        foreach (var property in new[] { "rowOffset", "row", "rowIndex", "index" })
        {
            var leftValue = ReadInt(left, property);
            var rightValue = ReadInt(right, property);
            if (leftValue.HasValue || rightValue.HasValue)
                return leftValue == rightValue;
        }

        return true;
    }

    private static void RestorePropertyIfMissingOrNull(JsonObject current, JsonObject next, string key)
    {
        if (!TryGetProperty(current, key, out var currentKey, out var currentValue))
            return;
        if (!TryGetProperty(next, key, out var nextKey, out var nextValue))
        {
            next[currentKey] = currentValue?.DeepClone();
            return;
        }

        if (IsNullJson(nextValue))
            next[nextKey] = currentValue?.DeepClone();
    }

    private static bool TryGetProperty(
        JsonObject root,
        string key,
        out string actualKey,
        out JsonNode? value)
    {
        actualKey = root
            .Select(item => item.Key)
            .FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        if (actualKey.Length == 0)
        {
            value = null;
            return false;
        }

        value = root[actualKey];
        return true;
    }

    private static bool IsNullJson(JsonNode? value)
        => value is null || string.Equals(value.ToJsonString(JsonOptions), "null", StringComparison.Ordinal);

    private static bool IsReadableMappingTarget(
        DynamicFlowPolicyEvaluationResult permissions,
        DynamicFlowMappingChangeDto change)
    {
        if (string.Equals(change.TargetKind, "FIELD", StringComparison.OrdinalIgnoreCase))
        {
            if (permissions.DenyAllFields)
                return false;

            var matches = permissions.Fields.Values
                .Where(permission => FieldTargetKeys(permission)
                    .Any(key => string.Equals(key, change.TargetKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return IsReadableFieldTarget(permissions, change.TargetKey);
        }

        if (change.TargetKind.StartsWith("TABLE", StringComparison.OrdinalIgnoreCase))
        {
            if (permissions.DenyAllTableColumns)
                return false;

            var target = SplitTableTargetKey(change.TargetKey);
            return target is not null &&
                   IsReadableTableTarget(
                       permissions,
                       target.Value.BlockId,
                       target.Value.ColumnKey);
        }

        return false;
    }

    private static string? RedactFieldValues(
        DynamicFlowPolicyEvaluationResult permissions,
        string? fieldValuesJson)
    {
        if (string.IsNullOrWhiteSpace(fieldValuesJson))
            return fieldValuesJson;

        var root = ParseObject(fieldValuesJson);
        if (root is null)
            return null;

        var values = root["values"] as JsonObject ?? root;
        foreach (var key in values
                     .Select(item => item.Key)
                     .ToList())
        {
            if (!IsReadableFieldTarget(permissions, key))
                RemoveProperty(values, key);
        }

        return root.ToJsonString(JsonOptions);
    }

    private static DynamicFlowReportReadableTablePayload RedactTableValues(
        DynamicFlowPolicyEvaluationResult permissions,
        string? values1DJson,
        string? tableValuesJson,
        string? topLevelDynamicExcelTemplateId)
    {
        var readableByBlock = permissions.TableColumns.Values
            .Where(permission => permission.Read && !permission.Hidden)
            .GroupBy(permission => permission.BlockId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(permission => permission.ColumnKey.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var expanded = Values1DCompression.ExpandTableValuesJson(tableValuesJson, JsonOptions) ?? tableValuesJson;
        var root = ParseObject(expanded);
        if (root?["blocks"] is not JsonArray blocks)
            return new DynamicFlowReportReadableTablePayload("[]", null);

        foreach (var item in blocks)
        {
            if (item is not JsonObject block)
                continue;

            var blockId = ReadString(block, "blockId", "id");
            var readableColumns =
                !permissions.DenyAllTableColumns &&
                !string.IsNullOrWhiteSpace(blockId) &&
                readableByBlock.TryGetValue(blockId, out var configuredColumns)
                    ? configuredColumns
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RedactBlockColumnValues(block, readableColumns);
        }

        var topLevelBlock = blocks
            .OfType<JsonObject>()
            .FirstOrDefault(block =>
                !string.IsNullOrWhiteSpace(topLevelDynamicExcelTemplateId) &&
                string.Equals(
                    ReadString(block, "dynamicExcelTemplateId"),
                    topLevelDynamicExcelTemplateId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            ?? blocks.OfType<JsonObject>().FirstOrDefault();
        var readableValues1DJson = topLevelBlock?["values1D"] is JsonArray topLevelValues
            ? SerializeValues(topLevelValues)
            : "[]";
        var readableTableValuesJson = Values1DCompression.CompressTableValuesJson(
            root.ToJsonString(JsonOptions),
            JsonOptions);

        return new DynamicFlowReportReadableTablePayload(
            readableValues1DJson,
            readableTableValuesJson);
    }

    private static void RedactBlockColumnValues(JsonObject block, HashSet<string> readableColumns)
    {
        if (block["values1D"] is JsonArray values)
        {
            if (block["valueSlots"] is JsonArray slots)
            {
                foreach (var slot in slots.OfType<JsonObject>())
                {
                    var columnKey = ReadString(slot, "columnKey");
                    var index = ReadInt(slot, "index");
                    if ((string.IsNullOrWhiteSpace(columnKey) ||
                         !readableColumns.Contains(columnKey)) &&
                        index.HasValue &&
                        index.Value >= 0 &&
                        index.Value < values.Count)
                    {
                        values[index.Value] = null;
                    }
                }
            }
            else
            {
                // Dữ liệu chạy cũ không có valueSlots nên không thể xác định cột an toàn.
                for (var index = 0; index < values.Count; index++)
                    values[index] = null;
            }
        }

        if (block["rows"] is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (row["cells"] is JsonObject rowCells)
                {
                    foreach (var columnKey in rowCells
                                 .Select(item => item.Key)
                                 .Where(columnKey => !readableColumns.Contains(columnKey))
                                 .ToList())
                    {
                        RemoveProperty(rowCells, columnKey);
                    }
                }

                foreach (var columnKey in row
                             .Select(item => item.Key)
                             .Where(columnKey =>
                                 columnKey is not ("cells" or "rowKey" or "rowInstanceId" or
                                     "rowOrder" or "rowIndex" or "joinKey" or "id" or
                                     "rowLabelCodes") &&
                                 !readableColumns.Contains(columnKey))
                             .ToList())
                {
                    RemoveProperty(row, columnKey);
                }
            }
        }

        RemoveUnreadableColumnItems(block["cells"] as JsonArray, readableColumns);
        RemoveUnreadableColumnItems(block["columns"] as JsonArray, readableColumns);
    }

    private static void RemoveUnreadableColumnItems(
        JsonArray? items,
        HashSet<string> readableColumns)
    {
        if (items is null)
            return;

        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index] is JsonObject item &&
                (ReadString(item, "columnKey") is not { } columnKey ||
                 !readableColumns.Contains(columnKey)))
            {
                items.RemoveAt(index);
            }
        }
    }

    private static bool IsReadableFieldTarget(
        DynamicFlowPolicyEvaluationResult permissions,
        string targetKey)
    {
        if (permissions.DenyAllFields)
            return false;

        var matches = permissions.Fields.Values
            .Where(permission => FieldTargetKeys(permission)
                .Any(key => string.Equals(
                    key,
                    targetKey,
                    StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return matches.Count > 0 &&
               matches.All(permission => permission.Read && !permission.Hidden);
    }

    private static bool IsReadableTableTarget(
        DynamicFlowPolicyEvaluationResult permissions,
        string blockId,
        string columnKey)
    {
        if (permissions.DenyAllTableColumns)
            return false;

        var matches = permissions.TableColumns.Values
            .Where(permission =>
                string.Equals(
                    permission.BlockId,
                    blockId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    permission.ColumnKey,
                    columnKey,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count > 0 &&
               matches.All(permission => permission.Read && !permission.Hidden);
    }

    private static (string BlockId, string ColumnKey)? SplitTableTargetKey(
        string? targetKey)
    {
        if (string.IsNullOrWhiteSpace(targetKey))
            return null;

        var separator = targetKey.IndexOf(':');
        if (separator <= 0 || separator >= targetKey.Length - 1)
            return null;

        return (
            targetKey[..separator].Trim(),
            targetKey[(separator + 1)..].Trim());
    }

    private static string SerializeValues(JsonArray values)
    {
        var objects = JsonSerializer.Deserialize<List<object?>>(
                          values.ToJsonString(JsonOptions),
                          JsonOptions)
                      ?? new List<object?>();
        return Values1DCompression.Serialize(objects, JsonOptions);
    }

    private static void RemoveProperty(JsonObject root, string key)
    {
        var actualKey = root
            .Select(item => item.Key)
            .FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
        if (actualKey is not null)
            root.Remove(actualKey);
    }

    private static Dictionary<string, string?> ExtractBlockValues(string? json)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var root = ParseObject(Values1DCompression.ExpandTableValuesJson(json, JsonOptions) ?? json);
        if (root?["blocks"] is not JsonArray blocks)
            return result;

        foreach (var item in blocks)
        {
            if (item is not JsonObject block)
                continue;

            var blockId = ReadString(block, "blockId", "id");
            if (string.IsNullOrWhiteSpace(blockId))
                continue;

            result[blockId] = StableJson(block["values1D"]);
        }

        return result;
    }

    private static void AddCellArrayColumns(Dictionary<string, string?> target, string blockId, JsonArray? cells)
    {
        if (cells is null)
            return;

        foreach (var item in cells.OfType<JsonObject>())
        {
            var columnKey = ReadString(item, "columnKey");
            if (!string.IsNullOrWhiteSpace(columnKey))
                AppendColumnValue(target, blockId, columnKey, item);
        }
    }

    private static void AddRowsColumns(Dictionary<string, string?> target, string blockId, JsonArray? rows)
    {
        if (rows is null)
            return;

        foreach (var row in rows.OfType<JsonObject>())
        {
            if (row["cells"] is JsonObject cells)
            {
                foreach (var cell in cells)
                    AppendColumnValue(target, blockId, cell.Key, cell.Value);
            }

            foreach (var item in row)
            {
                if (item.Key is "cells" or "rowKey" or "rowInstanceId" or "rowOrder" or
                    "rowIndex" or "joinKey" or "id" or "rowLabelCodes")
                {
                    continue;
                }

                AppendColumnValue(target, blockId, item.Key, item.Value);
            }
        }
    }

    private static void AddColumnArrayColumns(Dictionary<string, string?> target, string blockId, JsonArray? columns)
    {
        if (columns is null)
            return;

        foreach (var item in columns.OfType<JsonObject>())
        {
            var columnKey = ReadString(item, "columnKey");
            if (!string.IsNullOrWhiteSpace(columnKey))
                AppendColumnValue(target, blockId, columnKey, item);
        }
    }

    private static void AppendColumnValue(Dictionary<string, string?> target, string blockId, string columnKey, JsonNode? value)
    {
        if (string.IsNullOrWhiteSpace(columnKey))
            return;

        var targetKey = $"{blockId}:{columnKey.Trim()}";
        var encoded = StableJson(value);
        target[targetKey] = target.TryGetValue(targetKey, out var existing)
            ? $"{existing}\n{encoded}"
            : encoded;
    }

    private static bool JsonValueChanged(string? currentValue, string? nextValue)
        => !string.Equals(currentValue ?? "null", nextValue ?? "null", StringComparison.Ordinal);

    private static IEnumerable<string> ChangedKeys(
        IReadOnlyDictionary<string, string?> current,
        IReadOnlyDictionary<string, string?> next)
        => current.Keys
            .Concat(next.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => JsonValueChanged(current.GetValueOrDefault(key), next.GetValueOrDefault(key)));

    private static bool IsBlankJson(JsonNode? value)
    {
        if (IsNullJson(value))
            return true;
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text);
        if (value is JsonArray array)
            return array.Count == 0 || array.All(IsBlankJson);
        return false;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

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

    private static string? StableJson(JsonNode? node)
        => node?.ToJsonString(JsonOptions);

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

    private static int? ReadInt(JsonObject root, string property)
    {
        if (!root.TryGetPropertyValue(property, out var node) || node is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var number))
            return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number)
            ? number
            : null;
    }
}

internal sealed record DynamicFlowReportReadablePayload(
    string? Values1DJson,
    string? FieldValuesJson,
    string? TableValuesJson);

internal sealed record DynamicFlowReportReadableTablePayload(
    string? Values1DJson,
    string? TableValuesJson);

internal sealed record DynamicFlowReportWritablePayload(
    string? FieldValuesJson,
    string? TableValuesJson);

internal sealed record DynamicFlowReportPermissionViolation(
    string TargetKind,
    string TargetKey,
    string? SourcePolicyId,
    string Reason);

internal sealed record RequiredTableColumnValues(
    bool Applies,
    IReadOnlyList<JsonNode?> Values);
