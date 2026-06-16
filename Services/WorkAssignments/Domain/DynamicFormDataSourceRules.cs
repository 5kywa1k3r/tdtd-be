using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.WorkAssignments.Domain;

public static class DynamicFormDataSourceRuleTypes
{
    public const string Manual = "MANUAL";
    public const string AggregateChildren = "AGGREGATE_CHILDREN";
    public const string MapChild = "MAP_CHILD";
    public const string Mixed = "MIXED";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            AggregateChildren => Manual,
            MapChild => MapChild,
            Mixed => Mixed,
            Manual or null or "" => Manual,
            _ => throw Invalid("sourceRule không hợp lệ.", new { sourceRule = value })
        };
    }

    public static bool IsManualLike(string? value)
    {
        var normalized = Normalize(value);
        return normalized is Manual or Mixed;
    }

    private static AppException Invalid(string message, object? details = null)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_DATA_SOURCE_RULES_INVALID,
            details,
            message);
}

public sealed class DynamicFormDataSourceRulesDocument
{
    public int Version { get; set; } = 1;
    public List<DynamicFormSectionDataSourceRule> SectionRules { get; set; } = new();
    public List<DynamicFormFieldDataSourceRule> FieldRules { get; set; } = new();
    public List<DynamicFormBlockDataSourceRule> BlockRules { get; set; } = new();
}

public sealed class DynamicFormSectionDataSourceRule
{
    public string SectionId { get; set; } = string.Empty;
    public string SourceRule { get; set; } = DynamicFormDataSourceRuleTypes.Manual;
    public List<string> SourceAssignmentIds { get; set; } = new();
    public string? SourceSectionId { get; set; }
    public string? SourceBlockId { get; set; }
    public string? SourceFieldId { get; set; }
    public string? Note { get; set; }
}

public sealed class DynamicFormFieldDataSourceRule
{
    public string FieldId { get; set; } = string.Empty;
    public string SourceRule { get; set; } = DynamicFormDataSourceRuleTypes.Manual;
    public string? SourceAssignmentId { get; set; }
    public string? SourceFieldId { get; set; }
}

public sealed class DynamicFormBlockDataSourceRule
{
    public string BlockId { get; set; } = string.Empty;
    public string SourceRule { get; set; } = DynamicFormDataSourceRuleTypes.Manual;
    public string? SourceAssignmentId { get; set; }
    public string? SourceBlockId { get; set; }
}

public static class DynamicFormDataSourceRuleNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string NormalizeOrDefault(string? rulesJson, string? sectionsJson)
    {
        var sections = ReadSections(sectionsJson);
        if (sections.Count == 0)
            return Serialize(new DynamicFormDataSourceRulesDocument());

        var bySectionId = sections
            .Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);

        var input = string.IsNullOrWhiteSpace(rulesJson)
            ? new DynamicFormDataSourceRulesDocument()
            : DeserializeRules(rulesJson);

        var byConfiguredSection = (input.SectionRules ?? new List<DynamicFormSectionDataSourceRule>())
            .Where(x => !string.IsNullOrWhiteSpace(x.SectionId))
            .GroupBy(x => x.SectionId.Trim(), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        foreach (var sectionId in byConfiguredSection.Keys)
        {
            if (!bySectionId.Contains(sectionId))
                throw Invalid("sectionId trong cấu hình nguồn dữ liệu không tồn tại trong dynamic form.", new { sectionId });
        }

        var normalized = new DynamicFormDataSourceRulesDocument
        {
            Version = Math.Max(1, input.Version),
            SectionRules = sections
                .Select(section =>
                {
                    byConfiguredSection.TryGetValue(section.Id, out var existing);
                    return NormalizeSectionRule(existing, section.Id);
                })
                .ToList(),
            FieldRules = NormalizeFieldRules(input.FieldRules),
            BlockRules = NormalizeBlockRules(input.BlockRules)
        };

        return Serialize(normalized);
    }

    public static IReadOnlyList<string> ReadSourceAssignmentIds(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return Array.Empty<string>();

        var rules = DeserializeRules(rulesJson);
        return (rules.SectionRules ?? new List<DynamicFormSectionDataSourceRule>())
            .SelectMany(x => x.SourceAssignmentIds ?? new List<string>())
            .Concat((rules.FieldRules ?? new List<DynamicFormFieldDataSourceRule>())
                .Select(x => x.SourceAssignmentId))
            .Concat((rules.BlockRules ?? new List<DynamicFormBlockDataSourceRule>())
                .Select(x => x.SourceAssignmentId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string ResolveDefaultReportDataOrigin(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
            return WorkReportDataOrigin.ManualInput;

        var rules = DeserializeRules(rulesJson);
        var sectionRules = (rules.SectionRules ?? new List<DynamicFormSectionDataSourceRule>())
            .Select(x => DynamicFormDataSourceRuleTypes.Normalize(x.SourceRule))
            .ToList();

        if (sectionRules.Count == 0 || sectionRules.Any(DynamicFormDataSourceRuleTypes.IsManualLike))
            return WorkReportDataOrigin.ManualInput;

        if (sectionRules.All(x => string.Equals(x, DynamicFormDataSourceRuleTypes.AggregateChildren, StringComparison.Ordinal)))
            return WorkReportDataOrigin.AutoSummary;

        return WorkReportDataOrigin.PartialMapping;
    }

    private static DynamicFormSectionDataSourceRule NormalizeSectionRule(
        DynamicFormSectionDataSourceRule? input,
        string sectionId)
    {
        var sourceRule = DynamicFormDataSourceRuleTypes.Normalize(input?.SourceRule);
        return new DynamicFormSectionDataSourceRule
        {
            SectionId = sectionId,
            SourceRule = sourceRule,
            SourceAssignmentIds = sourceRule == DynamicFormDataSourceRuleTypes.Manual
                ? new List<string>()
                : NormalizeStringList(input?.SourceAssignmentIds),
            SourceSectionId = NormalizeOptional(input?.SourceSectionId),
            SourceBlockId = NormalizeOptional(input?.SourceBlockId),
            SourceFieldId = NormalizeOptional(input?.SourceFieldId),
            Note = NormalizeOptional(input?.Note)
        };
    }

    private static List<DynamicFormFieldDataSourceRule> NormalizeFieldRules(
        List<DynamicFormFieldDataSourceRule>? input)
    {
        return (input ?? new List<DynamicFormFieldDataSourceRule>())
            .Where(x => !string.IsNullOrWhiteSpace(x.FieldId))
            .Select(x =>
            {
                var sourceRule = DynamicFormDataSourceRuleTypes.Normalize(x.SourceRule);
                return new DynamicFormFieldDataSourceRule
                {
                    FieldId = x.FieldId.Trim(),
                    SourceRule = sourceRule,
                    SourceAssignmentId = sourceRule == DynamicFormDataSourceRuleTypes.Manual
                        ? null
                        : NormalizeOptional(x.SourceAssignmentId),
                    SourceFieldId = NormalizeOptional(x.SourceFieldId)
                };
            })
            .GroupBy(x => x.FieldId, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();
    }

    private static List<DynamicFormBlockDataSourceRule> NormalizeBlockRules(
        List<DynamicFormBlockDataSourceRule>? input)
    {
        return (input ?? new List<DynamicFormBlockDataSourceRule>())
            .Where(x => !string.IsNullOrWhiteSpace(x.BlockId))
            .Select(x =>
            {
                var sourceRule = DynamicFormDataSourceRuleTypes.Normalize(x.SourceRule);
                return new DynamicFormBlockDataSourceRule
                {
                    BlockId = x.BlockId.Trim(),
                    SourceRule = sourceRule,
                    SourceAssignmentId = sourceRule == DynamicFormDataSourceRuleTypes.Manual
                        ? null
                        : NormalizeOptional(x.SourceAssignmentId),
                    SourceBlockId = NormalizeOptional(x.SourceBlockId)
                };
            })
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();
    }

    private static DynamicFormDataSourceRulesDocument DeserializeRules(string rulesJson)
    {
        try
        {
            var rules = JsonSerializer.Deserialize<DynamicFormDataSourceRulesDocument>(rulesJson, JsonOptions);
            return rules ?? new DynamicFormDataSourceRulesDocument();
        }
        catch (JsonException ex)
        {
            throw Invalid("dynamicFormDataSourceRulesJson phải là JSON object hợp lệ.", new { error = ex.Message });
        }
    }

    private static List<SectionRef> ReadSections(string? sectionsJson)
    {
        if (string.IsNullOrWhiteSpace(sectionsJson))
            return new List<SectionRef>();

        try
        {
            using var document = JsonDocument.Parse(sectionsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw Invalid("sectionsJson của dynamic form phải là JSON array.");

            var result = new List<SectionRef>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var id = ReadString(item, "id") ?? ReadString(item, "Id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                result.Add(new SectionRef(id.Trim()));
            }

            return result
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }
        catch (JsonException ex)
        {
            throw Invalid("sectionsJson của dynamic form không hợp lệ.", new { error = ex.Message });
        }
    }

    private static string? ReadString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? NormalizeOptional(value.GetString())
            : null;

    private static List<string> NormalizeStringList(IEnumerable<string>? values)
        => (values ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Serialize(DynamicFormDataSourceRulesDocument rules)
        => JsonSerializer.Serialize(rules, JsonOptions);

    private static AppException Invalid(string message, object? details = null)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_DATA_SOURCE_RULES_INVALID,
            details,
            message);

    private sealed record SectionRef(string Id);
}
