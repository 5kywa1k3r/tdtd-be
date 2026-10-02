using System.Text.Json;
using System.Text.Json.Serialization;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

/// <summary>Versioned authoring boundary; never migrates, defaults statistic, or executes jobs.</summary>
internal static class DynamicFormNativeTableDefinition
{
    internal const int Version = 1;
    internal const int ListVersion = 2;
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static bool IsNative(DynamicFormTemplate template)
        => template.NativeTablesVersion.HasValue || template.TablesJson is not null;

    internal static void RequireLegacyConsumer(DynamicFormTemplate template, string action)
    {
        if (IsNative(template))
            Fail("NATIVE_TABLE_CONSUMER_NOT_SUPPORTED", action);
    }

    internal static List<DynamicFormNativeTableDto>? ReadStored(int? version, string? json)
    {
        if (version is null && json is null) return null;
        if (version is not (Version or ListVersion) || string.IsNullOrWhiteSpace(json))
            throw new JsonException("Native Table version/data pair is missing or unsupported.");
        // Never turn JSON null, an invalid array, or unknown properties into [].
        using var document = JsonDocument.Parse(json);
        var tables = StatConfigCanonicalJson.DeserializeStrict<List<DynamicFormNativeTableDto>>(document.RootElement);
        DynamicFormNativeStatisticState.ValidateWirePlans(document.RootElement, metadata: false);
        return tables;
    }

    internal static (int? Version, string? Json) PrepareWrite(
        DynamicFormSchemaDto? schema, DynamicFormTemplate? existing,
        string sectionsJson, string fieldsJson, string blocksJson)
    {
        if (schema?.NativeTablesVersion is null && schema?.TablesSpecified != true)
        {
            if (existing is not null && IsNative(existing))
                Fail("NATIVE_TABLES_EXPLICIT_INPUT_REQUIRED", "schema.tables");
            return (null, null);
        }
        if (schema is null || schema.NativeTablesVersion is not (Version or ListVersion) || schema.Tables is null)
            Fail("NATIVE_TABLE_VERSION_OR_COLLECTION_INVALID", "schema.tables");

        var tables = schema!.Tables!;
        var requestedVersion = schema.NativeTablesVersion.Value;
        Validate(tables, sectionsJson, fieldsJson, blocksJson, requestedVersion);
        DynamicFormNativeStatisticState.ValidateSemantics(tables, sectionsJson, fieldsJson, blocksJson);
        if (existing is not null && !IsNative(existing) && !string.IsNullOrWhiteSpace(existing.StatisticConfigHash))
            Fail("NATIVE_OWNER_CONFIG_MIGRATION_REQUIRED", "schema.tables");
        // P8 owns metadata. A configured Table may be carried through unchanged;
        // changing its structure requires an explicit P8 disable before this write.
        if (existing is not null && IsNative(existing))
        {
            var old = ReadStored(existing.NativeTablesVersion, existing.TablesJson)!;
            Validate(old, existing.SectionsJson, existing.FieldsJson, existing.BlocksJson, existing.NativeTablesVersion);
            foreach (var table in old.Where(t => t.StatisticPlan is not null || t.StatisticTargets!.Any(HasStatisticMetadata)))
            {
                var next = tables.SingleOrDefault(t => t.Id == table.Id);
                if (next is null || StatConfigCanonicalJson.Canonicalize(WithoutOverview(table))
                    != StatConfigCanonicalJson.Canonicalize(WithoutOverview(next)))
                    Fail("NATIVE_CONFIGURED_STATISTIC_DEFINITION_IMMUTABLE", "schema.tables");
            }
            foreach (var table in tables.Where(t => t.StatisticPlan is not null || t.StatisticTargets!.Any(HasStatisticMetadata)))
            {
                var previous = old.SingleOrDefault(t => t.Id == table.Id);
                if (previous is null || StatConfigCanonicalJson.Canonicalize(WithoutOverview(table))
                    != StatConfigCanonicalJson.Canonicalize(WithoutOverview(previous)))
                    Fail("USE_CANONICAL_STATISTICS_PATCH", "schema.tables.statisticTargets");
            }
        }
        else if (DynamicFormNativeStatisticState.HasPlan(tables) || tables.SelectMany(t => t.StatisticTargets!).Any(HasStatisticMetadata))
            Fail("USE_CANONICAL_STATISTICS_PATCH", "schema.tables.statisticTargets");
        return (requestedVersion, JsonSerializer.Serialize(tables, JsonOptions));
    }

    internal static Func<string, string?, DynamicFormNativeCellSpecDto> CompileCellTypes(DynamicFormNativeTableDto table)
    {
        var rules = table.TypeConfig!.Rules!.ToDictionary(rule => (rule.Target!.Scope, rule.Target.RowId, rule.Target.FieldId));
        var plain = new DynamicFormNativeCellSpecDto { Type = "plainText", Required = false };
        return (fieldId, rowId) =>
        {
            rules.TryGetValue(("column", null, fieldId), out var latest);
            if (rowId is not null)
            {
                foreach (var key in new[] { ("row", rowId, (string?)null), ("cell", rowId, fieldId) })
                    if (rules.TryGetValue(key, out var candidate) && (latest is null || candidate.Order > latest.Order)) latest = candidate;
            }
            return latest?.Spec ?? plain;
        };
    }

    internal static void ValidateStored(DynamicFormTemplate template)
    {
        var tables = ReadStored(template.NativeTablesVersion, template.TablesJson);
        if (tables is not null)
        {
            Validate(tables, template.SectionsJson, template.FieldsJson, template.BlocksJson, template.NativeTablesVersion);
            DynamicFormNativeStatisticState.ValidateSemantics(tables, template.SectionsJson, template.FieldsJson, template.BlocksJson);
        }
    }

    private static bool HasStatisticMetadata(DynamicFormNativeStatisticTargetDto target)
        => target.IsStatistic == true || target.Statistic.ValueKind != JsonValueKind.Undefined || target.StatisticLabelCodes?.Length > 0;

    private static DynamicFormNativeTableDto WithoutOverview(DynamicFormNativeTableDto table)
        => table with { OverviewSelections = null, OverviewPlanSelections = null, StatisticTargets = table.StatisticTargets!
            .Select(target => target with { ShowOnOverview = null }).ToList() };

    internal static void Validate(IReadOnlyList<DynamicFormNativeTableDto> tables,
        string sectionsJson, string fieldsJson, string blocksJson, int? version = null)
    {
        version ??= tables.Any(IsList) ? ListVersion : Version;
        if (version is not (Version or ListVersion)) Fail("NATIVE_TABLE_VERSION_OR_COLLECTION_INVALID", "schema.nativeTablesVersion");
        using var sections = JsonDocument.Parse(sectionsJson);
        using var fields = JsonDocument.Parse(fieldsJson);
        using var blocks = JsonDocument.Parse(blocksJson);
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections.RootElement.EnumerateArray())
        {
            var id = PropertyText(section, "id", "sections.id");
            AddId(allIds, id, "sections.id");
            sectionIds.Add(id!);
            // A native owner must not silently accept nested sections through extension data.
            if (section.TryGetProperty("sections", out _) || section.TryGetProperty("parentSectionId", out _))
                Fail("NESTED_SECTIONS_NOT_SUPPORTED", "sections");
        }
        if (sectionIds.Count == 0) Fail("NATIVE_SECTION_REQUIRED", "sections");
        foreach (var field in fields.RootElement.EnumerateArray())
        {
            AddId(allIds, PropertyText(field, "id", "fields.id"), "fields.id");
            if (!sectionIds.Contains(PropertyText(field, "sectionId", "fields.sectionId")))
                Fail("NATIVE_SECTION_REFERENCE_INVALID", "fields.sectionId");
        }
        foreach (var block in blocks.RootElement.EnumerateArray())
            AddId(allIds, PropertyText(block, "blockId", "blocks.blockId"), "blocks.blockId");

        var fieldCount = fields.RootElement.GetArrayLength();
        if (fieldCount > 200) Fail("NATIVE_AXIS_BUDGET_EXCEEDED", "fields");
        if (tables.Count + blocks.RootElement.GetArrayLength() > 30)
            Fail("NATIVE_TABLE_BUDGET_EXCEEDED", "tables");
        foreach (var table in tables)
        {
            if (table is null) Fail("NATIVE_TABLE_INVALID", "tables");
            AddId(allIds, table!.Id, "tables.id");
            Text(table.Name, "tables.name");
            if (!sectionIds.Contains(table.SectionId ?? "") || table.Order is null or < 0
                || table.Layout is not ("horizontal" or "vertical" or "matrix")
                || table.Fields is not { Count: > 0 } || table.Rows is null || table.StatisticTargets is null)
                Fail("NATIVE_TABLE_SHAPE_INVALID", table.Id!);
            fieldCount += table.Fields!.Count;
            if (fieldCount > 200 || table.Rows!.Count > 200)
                Fail("NATIVE_AXIS_BUDGET_EXCEEDED", table.Id!);
            if (table.Layout == "matrix" ? table.Rows.Count == 0 : table.Rows.Count != 0)
                Fail("NATIVE_ROWS_LAYOUT_MISMATCH", table.Id!);
            ValidatePresentation(table, version.Value);
            var columns = new HashSet<string>(StringComparer.Ordinal);
            var rows = new HashSet<string>(StringComparer.Ordinal);
            var fieldOrders = new HashSet<int>();
            foreach (var field in table.Fields)
            {
                if (field is null) Fail("NATIVE_COLUMN_INVALID", table.Id!);
                AddId(allIds, field!.Id, "tables.fields.id");
                columns.Add(field.Id!);
                Text(field.Name, "tables.fields.name");
                if (field.Order is null or < 0 || !fieldOrders.Add(field.Order.Value))
                    Fail("NATIVE_COLUMN_ORDER_INVALID", field.Id!);
            }
            foreach (var row in table.Rows)
            {
                if (row is null) Fail("NATIVE_ROW_INVALID", table.Id!);
                AddId(allIds, row!.Id, "tables.rows.id");
                rows.Add(row.Id!);
                Text(row.Name, "tables.rows.name");
            }
            var config = table.TypeConfig;
            if (config is null || config.Version != Version || config.Sequence is null or < 0 or > MaxSafeInteger || config.Rules is null)
                Fail("NATIVE_TYPE_CONFIG_REQUIRED", table.Id!);
            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            var orders = new HashSet<long>();
            foreach (var rule in config!.Rules!)
            {
                if (rule is null) Fail("NATIVE_RULE_INVALID", table.Id!);
                var key = Target(rule!.Target, columns, rows);
                if (!targetKeys.Add(key) || rule.Order is null or <= 0 || rule.Order > config.Sequence
                    || !orders.Add(rule.Order.Value))
                    Fail("NATIVE_RULE_ORDER_OR_TARGET_DUPLICATE", table.Id!);
                Spec(rule.Spec, version.Value, IsList(table));
            }
            targetKeys.Clear();
            foreach (var statistic in table.StatisticTargets!)
            {
                if (statistic is null) Fail("NATIVE_STATISTIC_TARGET_INVALID", table.Id!);
                AddId(allIds, statistic!.Id, "statisticTargets.id");
                if (statistic.IsStatistic is null || !targetKeys.Add(Target(statistic.Target, columns, rows)))
                    Fail("NATIVE_STATISTIC_TARGET_INVALID", statistic.Id!);
            }
            targetKeys.Clear();
            if (table.OverviewSelections?.Count > table.Fields.Count + table.Rows.Count * (table.Fields.Count + 1))
                Fail("NATIVE_OVERVIEW_SELECTION_BUDGET", table.Id!);
            foreach (var selection in table.OverviewSelections ?? [])
            {
                if (selection is null || selection.ShowOnOverview is null
                    || !targetKeys.Add(Target(selection.Target, columns, rows)))
                    Fail("NATIVE_OVERVIEW_SELECTION_INVALID", table.Id!);
            }
            var planIds = table.StatisticPlan?.Targets?.Select(target => target?.Id)
                .Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            if (table.OverviewPlanSelections?.Count > planIds.Count)
                Fail("NATIVE_OVERVIEW_PLAN_SELECTION_BUDGET", table.Id!);
            var selectedPlanIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var selection in table.OverviewPlanSelections ?? [])
            {
                if (selection is null || string.IsNullOrWhiteSpace(selection.TargetId)
                    || selection.ShowOnOverview is null || !planIds.Contains(selection.TargetId)
                    || !selectedPlanIds.Add(selection.TargetId))
                    Fail("NATIVE_OVERVIEW_PLAN_SELECTION_INVALID", table.Id!);
            }
        }
        DynamicFormNativeStatisticState.ValidateShapeAndIds(tables, allIds);
    }

    private static bool IsList(DynamicFormNativeTableDto table)
        => string.Equals(table.Presentation?.Kind, "LIST", StringComparison.Ordinal);

    private static void ValidatePresentation(DynamicFormNativeTableDto table, int version)
    {
        if (version == Version)
        {
            if (table.Presentation is not null || table.ItemConstraints is not null)
                Fail("NATIVE_LIST_REQUIRES_V2", table.Id!);
            return;
        }
        if (table.Presentation is null)
        {
            if (table.ItemConstraints is not null) Fail("NATIVE_LIST_PRESENTATION_REQUIRED", table.Id!);
            return; // v2 may carry ordinary Tables beside a List without changing their semantics.
        }
        if (!IsList(table) || table.Layout == "matrix") Fail("NATIVE_LIST_PRESENTATION_INVALID", table.Id!);
        if (table.StatisticPlan is not null || table.StatisticTargets is not { Count: 0 }
            || table.OverviewSelections is not null || table.OverviewPlanSelections is not null)
            Fail("NATIVE_LIST_AGGREGATE_METADATA_NOT_SUPPORTED", table.Id!);
        Text(table.Presentation.ItemLabel, "tables.presentation.itemLabel");
        Text(table.Presentation.AddLabel, "tables.presentation.addLabel");
        if (table.Presentation.ItemLabel!.Length > 80 || table.Presentation.AddLabel!.Length > 120)
            Fail("NATIVE_LIST_LABEL_LIMIT", table.Id!);
        var fields = table.Fields!.Select(field => field.Id!).ToHashSet(StringComparer.Ordinal);
        var summaries = table.Presentation.SummaryFieldIds ?? [];
        if (summaries.Count > 4 || summaries.Count != summaries.Distinct(StringComparer.Ordinal).Count()
            || summaries.Any(fieldId => !fields.Contains(fieldId)))
            Fail("NATIVE_LIST_SUMMARY_FIELDS_INVALID", table.Id!);
        if (table.ItemConstraints is null || table.ItemConstraints.MinItems is null or < 0
            || table.ItemConstraints.MaxItems is null or < 1 or > 200
            || table.ItemConstraints.MinItems > table.ItemConstraints.MaxItems)
            Fail("NATIVE_LIST_ITEM_CONSTRAINTS_INVALID", table.Id!);
    }

    private static string Target(DynamicFormNativeTargetDto? target, HashSet<string> columns, HashSet<string> rows)
    {
        if (target is null || target.Scope is not ("row" or "column" or "cell")
            || (target.Scope == "row" ? target.FieldId is not null : !columns.Contains(target.FieldId ?? ""))
            || (target.Scope == "column" ? target.RowId is not null : !rows.Contains(target.RowId ?? "")))
            Fail("NATIVE_TARGET_REFERENCE_INVALID", "target");
        return JsonSerializer.Serialize(new[] { target!.Scope, target.RowId, target.FieldId });
    }

    private static void Spec(DynamicFormNativeCellSpecDto? spec, int version, bool isList)
    {
        if (spec is null || spec.Type is not ("plainText" or "number" or "date" or "fullDate" or "boolean"
            or "singleSelect" or "multiSelect" or "richText" or "stringList") || spec.Required is null)
            Fail("NATIVE_CELL_SPEC_INVALID", "spec");
        if (spec!.MaxLength.HasValue && (spec.Type != "plainText" || spec.MaxLength is <= 0 or > MaxSafeInteger))
            Fail("NATIVE_MAX_LENGTH_INVALID", "spec.maxLength");
        if (version == Version && (spec.MinLength.HasValue || spec.Minimum.HasValue || spec.Maximum.HasValue
            || spec.IntegerOnly.HasValue || spec.MinDate is not null || spec.MaxDate is not null
            || spec.MinSelected.HasValue || spec.MaxSelected.HasValue || spec.ValueSource is not null))
            Fail("NATIVE_LIST_CELL_CONSTRAINT_REQUIRES_V2", "spec");
        if (spec.MinLength.HasValue && (spec.Type != "plainText" || spec.MinLength is < 0 || spec.MinLength > spec.MaxLength.GetValueOrDefault(MaxSafeInteger)))
            Fail("NATIVE_MIN_LENGTH_INVALID", "spec.minLength");
        if ((spec.Minimum.HasValue || spec.Maximum.HasValue || spec.IntegerOnly.HasValue) && spec.Type != "number"
            || spec.Minimum > spec.Maximum) Fail("NATIVE_NUMBER_CONSTRAINT_INVALID", "spec");
        if ((spec.MinDate is not null || spec.MaxDate is not null) && spec.Type != "fullDate"
            || spec.MinDate is not null && !DateOnly.TryParseExact(spec.MinDate, "yyyy-MM-dd", out _)
            || spec.MaxDate is not null && !DateOnly.TryParseExact(spec.MaxDate, "yyyy-MM-dd", out _)
            || spec.MinDate is not null && spec.MaxDate is not null && string.CompareOrdinal(spec.MinDate, spec.MaxDate) > 0)
            Fail("NATIVE_DATE_CONSTRAINT_INVALID", "spec");
        if ((spec.MinSelected.HasValue || spec.MaxSelected.HasValue) && spec.Type != "multiSelect"
            || spec.MinSelected is < 0 || spec.MaxSelected is < 1 || spec.MinSelected > spec.MaxSelected)
            Fail("NATIVE_SELECTION_CONSTRAINT_INVALID", "spec");
        if (spec.Type is "singleSelect" or "multiSelect")
        {
            var catalog = spec.ValueSource?.SourceType == "ENUM_CATALOG" && !string.IsNullOrWhiteSpace(spec.ValueSource.CatalogId);
            var system = isList && (spec.ValueSource?.SourceType is "SYSTEM_UNIT" or "SYSTEM_LOCALITY")
                && spec.ValueSource.CatalogId is null && spec.ValueSource.CatalogCode is null && spec.ValueSource.CatalogName is null;
            if (!catalog && !system && spec.Options is not { Count: > 0 and <= 100 }) Fail("NATIVE_OPTIONS_REQUIRED", "spec.options");
            if ((catalog || system) && spec.Options is not null) Fail("NATIVE_CHOICE_SOURCE_AMBIGUOUS", "spec");
            if (spec.ValueSource is not null && !catalog && !system) Fail("NATIVE_CHOICE_SOURCE_INVALID", "spec.valueSource");
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in spec.Options ?? [])
            {
                if (option is null) Fail("NATIVE_OPTION_INVALID", "spec.options");
                Text(option!.Code, "options.code");
                Text(option.Label, "options.label");
                if (!codes.Add(option.Code!.Trim().Normalize()) || !labels.Add(option.Label!.Trim().Normalize()))
                    Fail("NATIVE_OPTION_DUPLICATE", "spec.options");
            }
        }
        else if (spec.Options is not null || spec.ValueSource is not null) Fail("NATIVE_OPTIONS_TYPE_MISMATCH", "spec.options");
    }

    private static string PropertyText(JsonElement value, string property, string path)
    {
        JsonElement text = default;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out text)
            || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
            Fail("NATIVE_TEXT_REQUIRED", path);
        return text.GetString()!;
    }

    private static void AddId(HashSet<string> ids, string? id, string path)
    {
        Text(id, path);
        if (id != id!.Trim() || !ids.Add(id)) Fail("NATIVE_ID_INVALID_OR_DUPLICATE", path);
    }
    private static void Text(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)) Fail("NATIVE_TEXT_REQUIRED", path);
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string reason, string path)
        => throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason, path });
}
