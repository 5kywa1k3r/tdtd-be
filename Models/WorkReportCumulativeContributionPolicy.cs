using System.Text.Json;
using tdtd_be.Models.Enums;

namespace tdtd_be.Models;

public sealed class WorkReportCumulativeContributionPolicy
{
    private readonly List<ContributionRule> _rules;

    private WorkReportCumulativeContributionPolicy(string reportMode, string defaultMode, List<ContributionRule> rules)
    {
        ReportMode = WorkReportCumulativeContributionMode.Normalize(reportMode);
        DefaultMode = WorkReportCumulativeContributionMode.Normalize(defaultMode);
        _rules = rules;
    }

    public string ReportMode { get; }
    public string DefaultMode { get; }

    public bool IncludesReport
        => WorkReportCumulativeContributionMode.IsIncluded(ReportMode);

    public static WorkReportCumulativeContributionPolicy FromReport(WorkAssignmentReport report)
        => Parse(report.CumulativeContributionMode, report.CumulativeContributionPolicyJson);

    public static WorkReportCumulativeContributionPolicy Parse(string? reportMode, string? policyJson)
    {
        var defaultMode = WorkReportCumulativeContributionMode.Include;
        var rules = new List<ContributionRule>();

        if (!string.IsNullOrWhiteSpace(policyJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(policyJson);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    defaultMode = ReadString(root, "defaultMode") ?? defaultMode;

                    if (root.TryGetProperty("rules", out var rulesNode) &&
                        rulesNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in rulesNode.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object)
                                continue;

                            var mode = WorkReportCumulativeContributionMode.Normalize(ReadString(item, "mode"));
                            rules.Add(new ContributionRule(
                                NormalizeKey(ReadString(item, "targetKind") ?? ReadString(item, "kind")),
                                NormalizeKey(ReadString(item, "fieldKey") ?? ReadString(item, "targetKey")),
                                NormalizeKey(ReadString(item, "blockId")),
                                NormalizeKey(ReadString(item, "metricKey")),
                                NormalizeKey(ReadString(item, "rowKey")),
                                NormalizeKey(ReadString(item, "columnKey")),
                                NormalizeKey(ReadString(item, "sourceKey") ?? ReadString(item, "source")),
                                NormalizeKey(ReadString(item, "labelCode")),
                                mode));
                        }
                    }
                }
            }
            catch (JsonException)
            {
                rules.Clear();
            }
        }

        return new WorkReportCumulativeContributionPolicy(
            WorkReportCumulativeContributionMode.Normalize(reportMode),
            defaultMode,
            rules);
    }

    public bool ShouldIncludeField(string? fieldKey)
    {
        if (!IncludesReport)
            return false;

        var mode = ResolveMode(rule =>
            IsKind(rule.TargetKind, "FIELD") &&
            Matches(rule.FieldKey, fieldKey));

        return WorkReportCumulativeContributionMode.IsIncluded(mode);
    }

    public bool ShouldIncludeTableMetric(
        string? blockId,
        string? metricKey,
        string? rowKey,
        string? columnKey,
        string? sourceKey)
    {
        if (!IncludesReport)
            return false;

        var mode = ResolveMode(rule =>
            IsKind(rule.TargetKind, "TABLE") &&
            Matches(rule.BlockId, blockId) &&
            Matches(rule.MetricKey, metricKey) &&
            Matches(rule.RowKey, rowKey) &&
            Matches(rule.ColumnKey, columnKey) &&
            Matches(rule.SourceKey, sourceKey));

        return WorkReportCumulativeContributionMode.IsIncluded(mode);
    }

    public bool ShouldIncludeLabel(
        string? blockId,
        string? rowKey,
        string? source,
        string? labelCode)
    {
        if (!IncludesReport)
            return false;

        var mode = ResolveMode(rule =>
            IsKind(rule.TargetKind, "LABEL") &&
            Matches(rule.BlockId, blockId) &&
            Matches(rule.RowKey, rowKey) &&
            Matches(rule.SourceKey, source) &&
            Matches(rule.LabelCode, labelCode));

        return WorkReportCumulativeContributionMode.IsIncluded(mode);
    }

    private string ResolveMode(Func<ContributionRule, bool> predicate)
    {
        var mode = DefaultMode;
        foreach (var rule in _rules)
        {
            if (predicate(rule))
                mode = rule.Mode;
        }

        return mode;
    }

    private static bool IsKind(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;

        if (string.Equals(actual, expected, StringComparison.Ordinal))
            return true;

        return expected switch
        {
            "TABLE" => actual is "TABLE_METRIC" or "METRIC" or "CELL",
            "LABEL" => actual is "ROW_LABEL" or "TABLE_LABEL",
            _ => false
        };
    }

    private static bool Matches(string? expected, string? actual)
        => string.IsNullOrWhiteSpace(expected) ||
           string.Equals(expected, NormalizeKey(actual), StringComparison.Ordinal);

    private static string? NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record ContributionRule(
        string? TargetKind,
        string? FieldKey,
        string? BlockId,
        string? MetricKey,
        string? RowKey,
        string? ColumnKey,
        string? SourceKey,
        string? LabelCode,
        string Mode);
}
