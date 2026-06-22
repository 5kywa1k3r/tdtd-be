using System.Text.Json;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

internal sealed class WorkReportStatisticConceptMap
{
    public static WorkReportStatisticConceptMap Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, string> _fieldConcepts;
    private readonly IReadOnlyDictionary<string, string> _tableConcepts;

    public WorkReportStatisticConceptMap(
        IReadOnlyDictionary<string, string> fieldConcepts,
        IReadOnlyDictionary<string, string> tableConcepts)
    {
        _fieldConcepts = fieldConcepts;
        _tableConcepts = tableConcepts;
    }

    public string? ResolveField(string? fieldId, string? fieldKey)
        => FirstMatch(_fieldConcepts, NormalizeKey(fieldId), NormalizeKey(fieldKey));

    public string? ResolveTable(string? blockId, string? columnKey, string? metricKey)
        => FirstMatch(_tableConcepts, BuildTableKey(blockId, columnKey), BuildTableKey(blockId, metricKey), NormalizeKey(metricKey));

    private static string? FirstMatch(IReadOnlyDictionary<string, string> source, params string?[] keys)
    {
        foreach (var key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key) && source.TryGetValue(key, out var concept))
                return concept;
        }

        return null;
    }

    internal static string? NormalizeConceptCode(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value.ToUpperInvariant();
    }

    internal static string? BuildTableKey(string? blockId, string? columnOrMetricKey)
    {
        blockId = NormalizeKey(blockId);
        columnOrMetricKey = NormalizeKey(columnOrMetricKey);
        return string.IsNullOrWhiteSpace(blockId) || string.IsNullOrWhiteSpace(columnOrMetricKey)
            ? null
            : $"{blockId}:{columnOrMetricKey}";
    }

    internal static string? NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class WorkReportStatisticConceptMapBuilder
{
    public static WorkReportStatisticConceptMap From(WorkAssignmentReport? report)
    {
        if (string.IsNullOrWhiteSpace(report?.SummarySourceJson))
            return WorkReportStatisticConceptMap.Empty;

        try
        {
            using var doc = JsonDocument.Parse(report.SummarySourceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return WorkReportStatisticConceptMap.Empty;

            var fieldConcepts = new Dictionary<string, string>(StringComparer.Ordinal);
            var tableConcepts = new Dictionary<string, string>(StringComparer.Ordinal);
            ReadArray(doc.RootElement, "mappingRules", fieldConcepts, tableConcepts);
            ReadArray(doc.RootElement, "changes", fieldConcepts, tableConcepts);

            return fieldConcepts.Count == 0 && tableConcepts.Count == 0
                ? WorkReportStatisticConceptMap.Empty
                : new WorkReportStatisticConceptMap(fieldConcepts, tableConcepts);
        }
        catch (JsonException)
        {
            return WorkReportStatisticConceptMap.Empty;
        }
    }

    private static void ReadArray(
        JsonElement root,
        string propertyName,
        Dictionary<string, string> fieldConcepts,
        Dictionary<string, string> tableConcepts)
    {
        if (!TryGetProperty(root, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            AddItem(item, fieldConcepts, tableConcepts);
        }
    }

    private static void AddItem(
        JsonElement item,
        Dictionary<string, string> fieldConcepts,
        Dictionary<string, string> tableConcepts)
    {
        var conceptCode = WorkReportStatisticConceptMap.NormalizeConceptCode(ReadString(item, "conceptCode"));
        if (conceptCode is null)
            return;

        var targetKind = ReadString(item, "targetKind")?.Trim().ToUpperInvariant();
        var targetKey = ReadString(item, "targetKey");
        var targetFieldId = ReadString(item, "targetFieldId");
        var targetFieldKey = ReadString(item, "targetFieldKey");
        var targetBlockId = ReadString(item, "targetBlockId");
        var targetColumnKey = ReadString(item, "targetColumnKey");

        if (targetKind == "FIELD" || !string.IsNullOrWhiteSpace(targetFieldId) || !string.IsNullOrWhiteSpace(targetFieldKey))
        {
            Add(fieldConcepts, targetKey, conceptCode);
            Add(fieldConcepts, targetFieldId, conceptCode);
            Add(fieldConcepts, targetFieldKey, conceptCode);
            return;
        }

        if (targetKind == "TABLE" || !string.IsNullOrWhiteSpace(targetBlockId) || !string.IsNullOrWhiteSpace(targetColumnKey))
        {
            Add(tableConcepts, targetKey, conceptCode);
            Add(tableConcepts, WorkReportStatisticConceptMap.BuildTableKey(targetBlockId, targetColumnKey), conceptCode);
        }
    }

    private static void Add(Dictionary<string, string> target, string? key, string conceptCode)
    {
        key = WorkReportStatisticConceptMap.NormalizeKey(key);
        if (string.IsNullOrWhiteSpace(key) || target.ContainsKey(key))
            return;

        target[key] = conceptCode;
    }

    private static string? ReadString(JsonElement item, string propertyName)
    {
        if (!TryGetProperty(item, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool TryGetProperty(JsonElement item, string propertyName, out JsonElement value)
    {
        if (item.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
