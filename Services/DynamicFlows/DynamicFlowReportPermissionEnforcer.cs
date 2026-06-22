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
        foreach (var permission in permissions.Fields.Values.Where(IsWriteDenied))
        {
            foreach (var key in FieldTargetKeys(permission))
            {
                if (JsonValueChanged(currentFields.GetValueOrDefault(key), nextFields.GetValueOrDefault(key)))
                {
                    result.Add(new DynamicFlowReportPermissionViolation(
                        "FIELD",
                        key,
                        permission.SourcePolicyId,
                        "DYNAMIC_FLOW_FIELD_WRITE_FORBIDDEN"));
                    break;
                }
            }
        }

        var currentTables = ExtractTableColumnValues(currentTableValuesJson);
        var nextTables = ExtractTableColumnValues(nextTableValuesJson);
        var currentBlockValues = ExtractBlockValues(currentTableValuesJson);
        var nextBlockValues = ExtractBlockValues(nextTableValuesJson);
        foreach (var permission in permissions.TableColumns.Values.Where(IsWriteDenied))
        {
            var targetKey = $"{permission.BlockId}:{permission.ColumnKey}";
            if (JsonValueChanged(currentTables.GetValueOrDefault(targetKey), nextTables.GetValueOrDefault(targetKey)) ||
                (!currentTables.ContainsKey(targetKey) &&
                 !nextTables.ContainsKey(targetKey) &&
                 JsonValueChanged(
                     currentBlockValues.GetValueOrDefault(permission.BlockId),
                     nextBlockValues.GetValueOrDefault(permission.BlockId))))
            {
                result.Add(new DynamicFlowReportPermissionViolation(
                    "TABLE_COLUMN",
                    targetKey,
                    permission.SourcePolicyId,
                    "DYNAMIC_FLOW_TABLE_COLUMN_WRITE_FORBIDDEN"));
            }
        }

        return result;
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
            !string.Equals(permission.FieldKey, permission.FieldId, StringComparison.Ordinal))
        {
            yield return permission.FieldKey.Trim();
        }
        if (!string.IsNullOrWhiteSpace(permission.TargetKey) &&
            !string.Equals(permission.TargetKey, permission.FieldId, StringComparison.Ordinal) &&
            !string.Equals(permission.TargetKey, permission.FieldKey, StringComparison.Ordinal))
        {
            yield return permission.TargetKey.Trim();
        }
    }

    private static Dictionary<string, string?> ExtractFieldValues(string? json)
    {
        var root = ParseObject(json);
        var values = root?["values"] as JsonObject ?? root;
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (values is null)
            return result;

        foreach (var item in values)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
                result[item.Key.Trim()] = StableJson(item.Value);
        }

        return result;
    }

    private static Dictionary<string, string?> ExtractTableColumnValues(string? json)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
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

            AddCellArrayColumns(result, blockId, block["cells"] as JsonArray);
            AddRowsColumns(result, blockId, block["rows"] as JsonArray);
            AddColumnArrayColumns(result, blockId, block["columns"] as JsonArray);
        }

        return result;
    }

    private static Dictionary<string, string?> ExtractBlockValues(string? json)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
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
            if (row["cells"] is not JsonObject cells)
                continue;

            foreach (var cell in cells)
                AppendColumnValue(target, blockId, cell.Key, cell.Value);
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
}

internal sealed record DynamicFlowReportPermissionViolation(
    string TargetKind,
    string TargetKey,
    string? SourcePolicyId,
    string Reason);
