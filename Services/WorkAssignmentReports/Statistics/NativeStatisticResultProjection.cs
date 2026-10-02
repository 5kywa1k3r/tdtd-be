using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

// A read projection of verified immutable inputs, not another persisted result.
// Never resolves current names/labels, calculates totals or defaults old config.
internal static class NativeStatisticResultProjection
{
    internal const int MaxMetadataBytes = 4 * 1024 * 1024;

    internal static (NativeStatisticResultDocument Result, NativeStatisticResultMetadata Metadata) Project(
        string definitionJson, string configurationJson, string? selectedPlanJson, NativeStatisticResultDocument result,
        int maxMetadataBytes = MaxMetadataBytes)
    {
        try { return ProjectCore(definitionJson, configurationJson, selectedPlanJson, result, maxMetadataBytes); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { throw Invalid("NATIVE_RESULT_METADATA_INTEGRITY"); }
    }

    private static (NativeStatisticResultDocument, NativeStatisticResultMetadata) ProjectCore(
        string definitionJson, string configurationJson, string? selectedPlanJson, NativeStatisticResultDocument result, int budget)
    {
        _ = DynamicFormPublishedSchemaSnapshotBuilder.ValidateExisting(definitionJson, result.SchemaHash);
        using var definition = JsonDocument.Parse(definitionJson);
        var root = definition.RootElement;
        var tables = DynamicFormNativeTableDefinition.ReadStored(root.GetProperty("nativeTablesVersion").GetInt32(), root.GetProperty("tables").GetRawText())!;
        using var config = JsonDocument.Parse(configurationJson);
        var sectionJson = config.RootElement.GetProperty("nativePlanSectionJson").GetString();
        DynamicFormNativePlanConfigDto section;
        if (sectionJson is null)
        {
            // Explicitly empty captured configuration has an empty result even
            // when the original schema snapshot predates disabling statistics.
            // Do not restore the snapshot's old methods or invent missing ones.
            if (config.RootElement.GetProperty("nativeTargetSectionJson").GetString() is not string legacy)
                throw Invalid("NATIVE_RESULT_METADATA_REQUIRED");
            using var legacyDocument = JsonDocument.Parse(legacy);
            var emptyPlan = selectedPlanJson is null ? new DynamicFormNativeStatisticPlanDto(2, [])
                : DynamicFormNativeStatisticPlan.Read(selectedPlanJson);
            var emptyPrepared = DynamicFormNativeStatisticPlan.Prepare(Canonical(emptyPlan), tables,
                root.GetProperty("sections").GetRawText(), root.GetProperty("fields").GetRawText(), root.GetProperty("blocks").GetRawText());
            if (Canonical(legacyDocument.RootElement) != "[]" || emptyPrepared.Targets.Count != 0
                || result.Groups is null || result.Groups.Count != 0 || emptyPrepared.ContentDigest != result.PlanContentDigest)
                throw Invalid("NATIVE_RESULT_METADATA_REQUIRED");
            var emptyTargets = Array.Empty<NativeStatisticTargetMetadata>();
            var emptyContent = Canonical(new { version = 1, result.SchemaHash, result.PlanContentDigest, targets = emptyTargets });
            if (budget < 1 || budget > MaxMetadataBytes || Encoding.UTF8.GetByteCount(emptyContent) > budget)
                throw Invalid("NATIVE_RESULT_METADATA_QUOTA");
            return (result, new(1, result.SchemaHash, result.PlanContentDigest, StatRunCanonicalJson.HashText(emptyContent), emptyTargets));
        }
        else
        {
            using var sectionDocument = JsonDocument.Parse(sectionJson);
            section = StatConfigCanonicalJson.DeserializeStrict<DynamicFormNativePlanConfigDto>(sectionDocument.RootElement);
        }
        if (section is null || section.Definition is null) throw Invalid("NATIVE_RESULT_METADATA_REQUIRED");
        tables = DynamicFormNativeStatisticState.BindMetadata(tables, section.Definition);
        if (section.Version != 2 || section.Targets is null
            || section.Targets.Any(t => t is null || t.Configuration is null || t.LabelSnapshots is null || t.LabelSnapshots.Any(l => l is null))
            || Canonical(section.Definition) != Canonical(DynamicFormNativeStatisticState.Metadata(tables)))
            throw Invalid("NATIVE_RESULT_METADATA_DEFINITION_MISMATCH");
        // Reuse the P8 persisted-plan validator for full structure/type/label
        // pins. This temporary schema view has no database identity or writes.
        _ = DynamicFormStatisticConfigCommandService.ValidateNativePlanSection(new DynamicFormTemplate {
            Id = "", SectionsJson = root.GetProperty("sections").GetRawText(),
            FieldsJson = root.GetProperty("fields").GetRawText(), BlocksJson = root.GetProperty("blocks").GetRawText()
        }, tables, section);
        var plan = selectedPlanJson is null ? new DynamicFormNativeStatisticPlanDto(2, section.Targets.Select(t => t.Configuration).ToArray())
            : DynamicFormNativeStatisticPlan.Read(selectedPlanJson);
        var prepared = DynamicFormNativeStatisticPlan.Prepare(Canonical(plan), tables,
            root.GetProperty("sections").GetRawText(), root.GetProperty("fields").GetRawText(), root.GetProperty("blocks").GetRawText());
        if (prepared.ContentDigest != result.PlanContentDigest)
            throw Invalid("NATIVE_RESULT_METADATA_PLAN_MISMATCH");
        var selected = prepared.Targets.ToDictionary(t => (t.Target.TableId!, t.Target.TargetId!));
        if (result.Groups is null) throw Invalid("NATIVE_RESULT_METADATA_OPERATION_MISMATCH");
        foreach (var group in result.Groups)
        {
            if (group is null || group.Address is null || group.Operations is null || group.Operations.Any(o => o is null)
                || !selected.TryGetValue((group.Address.TableId, group.Address.TargetId), out var target)
                || !group.Operations.Select(o => (o.OperationId, o.Method))
                    .SequenceEqual(target.Target.Operations!.Select(o => (o.OperationId!, o.Method!))))
                throw Invalid("NATIVE_RESULT_METADATA_OPERATION_MISMATCH");
        }
        var metadata = new List<NativeStatisticTargetMetadata>();
        foreach (var target in prepared.Targets)
        {
            var settings = target.Target;
            var pinned = section.Targets.Single(t => t.Configuration.TableId == settings.TableId && t.Configuration.TargetId == settings.TargetId);
            var operationIds = settings.Operations!.Select(o => o.OperationId).ToArray();
            var pinnedSelection = pinned.Configuration with { Operations = operationIds.Select(id =>
                pinned.Configuration.Operations!.Single(op => op.OperationId == id)).ToArray() };
            if (Canonical(settings) != Canonical(pinnedSelection)
                || !StatRunCanonicalJson.IsCanonicalSha256(pinned.StructureHash)
                || pinned.LabelSnapshots is null
                || !pinned.LabelSnapshots.Select(l => l.Code).OrderBy(c => c, StringComparer.Ordinal)
                    .SequenceEqual(settings.StatisticLabelCodes!.OrderBy(c => c, StringComparer.Ordinal))
                || pinned.LabelSnapshots.Any(l => l.Usage != LabelUsages.Statistic || !l.IsActive || l.VersionNo < 1
                    || !IsId(l.LabelId) || !IsId(l.VersionId) || !StatRunCanonicalJson.IsCanonicalSha256(l.ConfigHash)))
                throw Invalid("NATIVE_RESULT_METADATA_PIN_MISMATCH");
            if (settings.ShowInDetail != true && settings.ShowInTree != true) continue;
            var table = tables.Single(t => t.Id == settings.TableId);
            var part = root.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("id").GetString() == table.SectionId);
            // Axes follow the exact selector order; bindings retain the compiled
            // type and option labels of selected cells, including input filters.
            var fields = settings.Selector!.FieldIds!.Select(id => table.Fields!.Single(f => f.Id == id)).ToArray();
            var rows = settings.Selector.RowIds?.Select(id => table.Rows!.Single(r => r.Id == id)).ToArray() ?? [];
            var cells = target.Groups.SelectMany(g => g.Cells).DistinctBy(c => (c.FieldId, c.RowId))
                .Select(c => new NativeStatisticCellMetadata(c.FieldId, c.RowId, c.Spec)).ToArray();
            metadata.Add(new(settings, pinned.StructureHash, pinned.LabelSnapshots,
                new(table.SectionId!, part.GetProperty("title").GetString()),
                new(table.Id!, table.Name!, table.Order!.Value, table.Layout!, fields, rows), cells));
        }
        var visible = metadata.Select(t => (t.Configuration.TableId, t.Configuration.TargetId)).ToHashSet();
        var content = new { version = 1, result.SchemaHash, result.PlanContentDigest, targets = metadata };
        var json = Canonical(content);
        if (budget < 1 || budget > MaxMetadataBytes || Encoding.UTF8.GetByteCount(json) > budget)
            throw Invalid("NATIVE_RESULT_METADATA_QUOTA");
        return (result with { Groups = result.Groups.Where(g => visible.Contains((g.Address.TableId, g.Address.TargetId))).ToArray() },
            new(1, result.SchemaHash, result.PlanContentDigest, StatRunCanonicalJson.HashText(json), metadata));
    }

    private static string Canonical(object value) => StatConfigCanonicalJson.Canonicalize(value);
    private static bool IsId(string value) => ObjectId.TryParse(value, out var id) && id.ToString() == value;
    private static Exception Invalid(string reason) => AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { reason });
}
