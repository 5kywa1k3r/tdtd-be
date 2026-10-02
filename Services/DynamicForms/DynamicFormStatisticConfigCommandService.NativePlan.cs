using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

public sealed partial class DynamicFormStatisticConfigCommandService
{
    private static DynamicFormNativeStatisticsPayload NormalizeNativeStatisticsSyntax(DynamicFormNativeStatisticsPayload input)
    {
        if (input.Version != 2 || input.Tables is null || input.Tables.Count > 30)
            throw Schema("$.payload.nativeStatistics", "NATIVE_STATISTICS_VERSION_OR_TABLES_INVALID");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in input.Tables)
        {
            if (table is null || string.IsNullOrWhiteSpace(table.TableId) || table.TableId != table.TableId.Trim()
                || !ids.Add(table.TableId) || table.StatisticTargets is null)
                throw Schema("$.payload.nativeStatistics", "NATIVE_STATISTICS_TABLE_INVALID");
            foreach (var target in table.StatisticTargets)
            {
                if (target is null || string.IsNullOrWhiteSpace(target.Id) || target.IsStatistic is null || target.Target is null)
                    throw Schema("$.payload.nativeStatistics", "NATIVE_STATISTICS_SOURCE_INVALID");
                // Retained v1 settings may be type-incompatible after an explicit upgrade,
                // but unknown JSON/settings are never accepted as an opaque archive.
                if (target.Statistic.ValueKind != JsonValueKind.Undefined)
                {
                    var retained = NativeSettingsFromSchema(target.Statistic);
                    if (retained.AggregateOps is null || retained.ShowInDetail is null || retained.ShowInTree is null)
                        throw Schema("$.payload.nativeStatistics", "NATIVE_RETAINED_SETTINGS_SHAPE_INVALID");
                    // An empty method list is a retained incomplete v1 source, not
                    // an enabled v2 configuration. Effective v1 validation follows.
                }
                if (table.StatisticPlan?.Targets?.Any(t => t is not null && t.Id == target.Id) != true
                    && NormalizeLabelCodes(target.StatisticLabelCodes, "$.payload.nativeStatistics").Count != (target.StatisticLabelCodes?.Length ?? 0))
                    throw Schema("$.payload.nativeStatistics", "NATIVE_LABEL_DUPLICATE");
            }
        }
        // Syntax only. Real axes, cross-component IDs and method/type checks follow
        // against the captured owner inside the P8 transaction.
        var syntaxTables = input.Tables.Select(t => new DynamicFormNativeTableDto
        { Id = t.TableId, StatisticTargets = t.StatisticTargets!.ToList(), StatisticPlan = t.StatisticPlan }).ToArray();
        DynamicFormNativeStatisticState.ValidateShapeAndIds(syntaxTables, new HashSet<string>(StringComparer.Ordinal));
        return input;
    }

    private static DynamicFormNativePlanConfigDto? DeserializeNativePlanSection(string? json)
    {
        if (json is null) return null;
        var raw = ParseElement(json);
        var result = StatConfigCanonicalJson.DeserializeStrict<DynamicFormNativePlanConfigDto>(raw);
        if (result.Version != 2 || result.Definition is null || result.Targets is null
            || result.Targets.Any(t => t is null || t.Configuration is null || t.LabelSnapshots is null
                || t.LabelSnapshots.Any(l => l is null) || !IsCanonicalSha256(t.StructureHash)))
            throw Schema("$.nativePlanConfig", "NATIVE_PLAN_SECTION_INVALID");
        _ = NormalizeNativeStatisticsSyntax(result.Definition);
        DynamicFormNativeStatisticState.ValidateWirePlans(raw.GetProperty("definition").GetProperty("tables"), metadata: true);
        _ = DynamicFormNativeStatisticPlan.Read(JsonSerializer.Serialize(new DynamicFormNativeStatisticPlanDto(2,
            result.Targets.Select(t => t.Configuration).ToArray()), StatConfigCanonicalJson.StrictJsonOptions));
        return result;
    }

    private async Task<string?> BuildNativePlanSectionAsync(IClientSessionHandle? session, DynamicFormTemplate owner,
        IReadOnlyList<DynamicFormNativeTableDto> tables, MeResponse me, CancellationToken ct)
    {
        if (!DynamicFormNativeStatisticState.HasPlan(tables)) return null;
        var prepared = DynamicFormNativeStatisticState.Prepare(tables, owner.SectionsJson, owner.FieldsJson, owner.BlocksJson);
        var configs = new List<DynamicFormNativePlanTargetConfigDto>();
        foreach (var target in prepared.Targets)
        {
            IReadOnlyList<DynamicFormStatisticLabelSnapshotDto> labels = Array.Empty<DynamicFormStatisticLabelSnapshotDto>();
            if (target.Target.StatisticLabelCodes!.Count > 0)
            {
                var type = PlanLabelType(target);
                labels = await ResolveLabelsAsync(session, NormalizeLabelCodes(target.Target.StatisticLabelCodes, "$.nativePlanConfig"), type, me, "$.nativePlanConfig", ct);
            }
            configs.Add(new(target.Target, labels, NativeStructureHash(tables.Single(t => t.Id == target.Target.TableId))));
        }
        return StatConfigCanonicalJson.Canonicalize(new DynamicFormNativePlanConfigDto(2,
            DynamicFormNativeStatisticState.Metadata(tables), configs));
    }

    private static string PlanLabelType(DynamicFormNativeStatisticPlan.PreparedTarget target)
    {
        var type = target.Groups.SelectMany(group => group.Cells).Select(cell => cell.Spec.Type).Distinct(StringComparer.Ordinal).Single();
        return MapFieldType(type == "plainText" ? "longText" : type, "$.nativePlanConfig");
    }

    private async Task<string?> ReadNativePlanSectionAsync(IClientSessionHandle? session,
        DynamicFormTemplate owner, MeResponse me, CancellationToken ct)
    {
        var section = owner.StatisticConfigSections?.NativePlanSectionJson;
        if (!DynamicFormNativeTableDefinition.IsNative(owner))
        {
            if (section is not null) throw IntegrityConflict(owner.Id, "NATIVE_PLAN_WITHOUT_SCHEMA");
            return null;
        }
        var tables = NativeTables(owner);
        if (!DynamicFormNativeStatisticState.HasPlan(tables))
        {
            if (section is not null) throw IntegrityConflict(owner.Id, "NATIVE_PLAN_WITHOUT_DEFINITION");
            return null;
        }
        if (section is null)
        {
            if (!string.IsNullOrWhiteSpace(owner.StatisticConfigHash)) throw IntegrityConflict(owner.Id, "NATIVE_PLAN_SECTION_REQUIRED");
            // Clone/version has a new owner/config identity; resolve visible labels
            // through the same P8 boundary, never borrow a previous owner's pins.
            section = await BuildNativePlanSectionAsync(session, owner, tables, me, ct);
        }
        var canonical = ValidateNativePlanSection(owner, tables, DeserializeNativePlanSection(section)!);
        if (owner.StatisticConfigSections?.NativePlanSectionJson is not null)
        {
            var snapshots = owner.StatisticConfigSnapshots?.Where(s => s.VersionId == owner.StatisticConfigVersionId).Take(2).ToArray();
            if (snapshots?.Length != 1 || snapshots[0].Sections?.NativePlanSectionJson is not string saved
                || StatConfigCanonicalJson.Canonicalize(ParseElement(saved)) != canonical)
                throw IntegrityConflict(owner.Id, "NATIVE_PLAN_CURRENT_SNAPSHOT");
        }
        return canonical;
    }

    internal static string ValidateNativePlanSection(DynamicFormTemplate owner,
        IReadOnlyList<DynamicFormNativeTableDto> tables, DynamicFormNativePlanConfigDto section)
    {
        if (StatConfigCanonicalJson.Canonicalize(section.Definition)
            != StatConfigCanonicalJson.Canonicalize(DynamicFormNativeStatisticState.Metadata(tables)))
            throw IntegrityConflict(owner.Id, "NATIVE_PLAN_DEFINITION");
        var prepared = DynamicFormNativeStatisticState.Prepare(tables, owner.SectionsJson, owner.FieldsJson, owner.BlocksJson);
        if (prepared.Targets.Count != section.Targets.Count
            || section.Targets.Select(t => t.Configuration.TargetId).Distinct(StringComparer.Ordinal).Count() != section.Targets.Count)
            throw IntegrityConflict(owner.Id, "NATIVE_PLAN_TARGET_SET");
        foreach (var target in prepared.Targets)
        {
            var config = section.Targets.SingleOrDefault(t => t.Configuration.TargetId == target.Target.TargetId)
                ?? throw IntegrityConflict(owner.Id, "NATIVE_PLAN_TARGET_MISSING");
            var codes = NormalizeLabelCodes(target.Target.StatisticLabelCodes, "$.nativePlanConfig");
            if (StatConfigCanonicalJson.Canonicalize(config.Configuration) != StatConfigCanonicalJson.Canonicalize(target.Target)
                || config.StructureHash != NativeStructureHash(tables.Single(t => t.Id == target.Target.TableId))
                || !config.LabelSnapshots.Select(l => l.Code).SequenceEqual(codes, StringComparer.Ordinal)
                || config.LabelSnapshots.Any(l => !l.IsActive || l.Usage != LabelUsages.Statistic
                    || l.DataType != ExpectedLabelDataType(PlanLabelType(target)) || l.VersionNo < 1
                    || !IsCanonicalObjectId(l.LabelId) || !IsCanonicalObjectId(l.VersionId) || !IsCanonicalSha256(l.ConfigHash)))
                throw IntegrityConflict(owner.Id, "NATIVE_PLAN_METADATA_OR_LABEL_PINS");
        }
        return StatConfigCanonicalJson.Canonicalize(section);
    }

    private async Task<ConfigState> ApplyNativeStatisticsAsync(IClientSessionHandle session, DynamicFormTemplate owner,
        ConfigState current, DynamicFormNativeStatisticsPayload mutation, MeResponse me, CancellationToken ct)
    {
        var tables = NativeTables(owner);
        if (mutation.Tables!.Count != tables.Count) throw Schema("$.payload.nativeStatistics", "NATIVE_TABLE_SET_MISMATCH");
        var nextTables = tables.Select(table =>
        {
            var input = mutation.Tables.SingleOrDefault(t => t.TableId == table.Id)
                ?? throw Schema("$.payload.nativeStatistics", "NATIVE_TABLE_SET_MISMATCH");
            if (StatConfigCanonicalJson.Canonicalize(input.StatisticTargets!.Select(t => new { t.Id, t.Target }))
                != StatConfigCanonicalJson.Canonicalize(table.StatisticTargets!.Select(t => new { t.Id, t.Target })))
                throw Schema("$.payload.nativeStatistics", "NATIVE_TARGET_STRUCTURE_IMMUTABLE");
            return table with { StatisticTargets = input.StatisticTargets.ToList(), StatisticPlan = input.StatisticPlan };
        }).ToList();
        DynamicFormNativeTableDefinition.Validate(nextTables, owner.SectionsJson, owner.FieldsJson, owner.BlocksJson);
        var native = new List<DynamicFormNativeStatisticConfigDto>();
        foreach (var table in nextTables) foreach (var target in table.StatisticTargets!)
        {
            if (DynamicFormNativeStatisticState.IsOverridden(table, target.Id!)) continue;
            if (target.IsStatistic != true)
            {
                if (target.Statistic.ValueKind != JsonValueKind.Undefined || target.StatisticLabelCodes?.Length > 0)
                    throw Schema("$.payload.nativeStatistics", "DISABLED_SETTINGS_MUST_REMAIN_LOCAL");
                continue;
            }
            var type = ResolveNativeTargetType(table, target.Target!);
            var settings = NormalizeExistingSettings(NativeSettingsFromSchema(target.Statistic), type, "$.payload.nativeStatistics");
            ValidateStatisticSettings(type, settings, "$.payload.nativeStatistics");
            var codes = NormalizeLabelCodes(target.StatisticLabelCodes, "$.payload.nativeStatistics");
            var labels = await ResolveLabelsAsync(session, codes, type, me, "$.payload.nativeStatistics", ct);
            native.Add(new(table.Id!, target.Id!, target.Target!, type, settings, codes, labels, NativeStructureHash(table)));
        }
        var nativeSection = ValidateNativeSection(owner, nextTables, native);
        var planSection = await BuildNativePlanSectionAsync(session, owner, nextTables, me, ct);
        if (planSection is not null) _ = ValidateNativePlanSection(owner, nextTables, DeserializeNativePlanSection(planSection)!);
        var tablesJson = JsonSerializer.Serialize(nextTables, StatConfigCanonicalJson.StrictJsonOptions);
        DynamicFormService.EnsureSchemaPayloadBudget(owner.SectionsJson, owner.FieldsJson, owner.BlocksJson, tablesJson);
        return current with
        {
            PreviousVersionId = current.VersionId, VersionId = ObjectId.GenerateNewId().ToString(),
            VersionNo = checked(current.VersionNo + 1), Revision = checked(current.Revision + 1), IsVirtual = false,
            NativeTargetSectionJson = nativeSection, NativePlanSectionJson = planSection,
            NativeTablesJson = tablesJson
        };
    }
}
