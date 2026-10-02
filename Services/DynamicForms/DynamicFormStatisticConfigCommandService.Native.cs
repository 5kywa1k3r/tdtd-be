using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.DynamicForms;

public sealed partial class DynamicFormStatisticConfigCommandService
{
    // Native targets use the FIELD operation vocabulary (AVG, date MIN/MAX),
    // never the Excel metric vocabulary (AVERAGE, EARLIEST).
    private static readonly IReadOnlyDictionary<string, string> NativeOperations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["count"] = "COUNT", ["sum"] = "SUM", ["avg"] = "AVG",
            ["min"] = "MIN", ["max"] = "MAX", ["latest"] = "LATEST",
            ["trueCount"] = "TRUE_COUNT", ["falseCount"] = "FALSE_COUNT",
            ["bucketCount"] = "BUCKET_COUNT", ["concat"] = "CONCAT"
        };

    private static IReadOnlyList<DynamicFormNativeStatisticMutation> NormalizeNativeMutationSyntax(
        IReadOnlyList<DynamicFormNativeStatisticMutation> input)
    {
        if (input.Count is < 1 or > 30) throw Schema("$.payload.nativeTargets", "NATIVE_MUTATION_LIMIT_30");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DynamicFormNativeStatisticMutation>();
        foreach (var item in input)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.TableId) || item.TableId != item.TableId.Trim()
                || string.IsNullOrWhiteSpace(item.TargetId) || item.TargetId != item.TargetId.Trim()
                || !ids.Add(item.TargetId)) throw Schema("$.payload.nativeTargets", "NATIVE_TARGET_ID_INVALID");
            if (item.StatisticLabelCodes is { Count: > 30 }
                || item.StatisticLabelCodes is not null && NormalizeLabelCodes(item.StatisticLabelCodes, "$.payload.nativeTargets").Count != item.StatisticLabelCodes.Count)
                throw Schema("$.payload.nativeTargets", "NATIVE_LABEL_LIMIT_OR_DUPLICATE");
            // Reuse explicit-config/disabled/operation syntax checks; fieldId is
            // only an input to validation, never a persisted root field identity.
            var normalized = NormalizeFieldMutationSyntax(new DynamicFormStatisticConfigPayload(
                new[] { new DynamicFormStatisticFieldPayload(item.TargetId, item.IsStatistic, item.Statistic, item.StatisticLabelCodes) }, null))[0];
            result.Add(item with { IsStatistic = normalized.IsStatistic, Statistic = normalized.Statistic,
                StatisticLabelCodes = normalized.StatisticLabelCodes });
        }
        return result;
    }

    private static List<DynamicFormNativeStatisticConfigDto>? DeserializeNativeSection(string? json)
    {
        if (json is null) return null;
        var result = StatConfigCanonicalJson.DeserializeStrict<List<DynamicFormNativeStatisticConfigDto>>(ParseElement(json));
        if (result.Any(item => item is null || string.IsNullOrWhiteSpace(item.TargetId) || string.IsNullOrWhiteSpace(item.TableId)
            || item.Statistic is null || item.Target is null || item.StatisticLabelCodes is null || item.LabelSnapshots is null
            || item.LabelSnapshots.Any(label => label is null)))
            throw Schema("$.nativeTargetConfig", "NATIVE_SECTION_INVALID");
        return result;
    }

    private static List<DynamicFormNativeTableDto> NativeTables(DynamicFormTemplate owner)
    {
        DynamicFormNativeTableDefinition.ValidateStored(owner);
        return DynamicFormNativeTableDefinition.ReadStored(owner.NativeTablesVersion, owner.TablesJson)
            ?? throw Schema("$.payload.nativeTargets", "NATIVE_OWNER_REQUIRED");
    }

    private async Task<string?> ReadNativeSectionAsync(IClientSessionHandle? session, DynamicFormTemplate owner, MeResponse me, CancellationToken ct)
    {
        var section = owner.StatisticConfigSections?.NativeTargetSectionJson;
        if (!DynamicFormNativeTableDefinition.IsNative(owner))
        {
            if (section is not null) throw IntegrityConflict(owner.Id, "NATIVE_SECTION_WITHOUT_SCHEMA");
            return null;
        }
        var tables = NativeTables(owner);
        if (section is null && !string.IsNullOrWhiteSpace(owner.StatisticConfigHash))
            throw IntegrityConflict(owner.Id, "NATIVE_SECTION_REQUIRED");
        var configs = DeserializeNativeSection(section ?? "[]")!;
        var enabled = tables.SelectMany(table => table.StatisticTargets!
            .Where(target => target.IsStatistic == true && !DynamicFormNativeStatisticState.IsOverridden(table, target.Id!)).Select(target => (table, target))).ToArray();
        if (section is null)
        {
            // Clone/version retains schema metadata, but receives a new owner/config
            // identity. Resolve visible label versions exactly as legacy field intake.
            foreach (var (table, target) in enabled)
            {
                var type = ResolveNativeTargetType(table, target.Target!);
                var settings = NormalizeExistingSettings(NativeSettingsFromSchema(target.Statistic), type, "$.nativeTargetConfig");
                var codes = NormalizeLabelCodes(target.StatisticLabelCodes, "$.nativeTargetConfig.statisticLabelCodes");
                var labels = await ResolveLabelsAsync(session, codes, type, me, "$.nativeTargetConfig", ct);
                configs.Add(new DynamicFormNativeStatisticConfigDto(table.Id!, target.Id!, target.Target!, type,
                    settings, codes, labels, NativeStructureHash(table)));
            }
        }
        var canonical = ValidateNativeSection(owner, tables, configs);
        var snapshots = owner.StatisticConfigSnapshots?.Where(s => s.VersionId == owner.StatisticConfigVersionId).Take(2).ToArray();
        if (section is not null && (snapshots?.Length != 1 || snapshots[0].Sections?.NativeTargetSectionJson is not string saved
            || StatConfigCanonicalJson.Canonicalize(ParseElement(saved)) != canonical))
            throw IntegrityConflict(owner.Id, "NATIVE_CURRENT_SNAPSHOT");
        return canonical;
    }

    // Shared by P8 readback and the explicit native job intake. This path only
    // validates persisted configuration; it never resolves labels or defaults.
    private static string ValidateNativeSection(DynamicFormTemplate owner,
        IReadOnlyList<DynamicFormNativeTableDto> tables,
        IReadOnlyList<DynamicFormNativeStatisticConfigDto> configs)
    {
        var enabled = tables.SelectMany(table => table.StatisticTargets!
            .Where(target => target.IsStatistic == true && !DynamicFormNativeStatisticState.IsOverridden(table, target.Id!)).Select(target => (table, target))).ToArray();
        if (configs.Count != enabled.Length || configs.Select(c => c.TargetId).Distinct(StringComparer.Ordinal).Count() != configs.Count)
            throw IntegrityConflict(owner.Id, "NATIVE_TARGET_SET");
        foreach (var (table, target) in enabled)
        {
            var config = configs.SingleOrDefault(c => c.TableId == table.Id && c.TargetId == target.Id)
                ?? throw IntegrityConflict(owner.Id, "NATIVE_TARGET_CONFIG_MISSING");
            var type = ResolveNativeTargetType(table, target.Target!);
            var settings = NativeSettingsFromSchema(target.Statistic);
            ValidateStatisticSettings(type, NormalizeExistingSettings(config.Statistic, type, "$.nativeTargetConfig"), "$.nativeTargetConfig");
            if (config.FieldType != type || config.StructureHash != NativeStructureHash(table)
                || StatConfigCanonicalJson.Canonicalize(config.Target) != StatConfigCanonicalJson.Canonicalize(target.Target)
                || StatConfigCanonicalJson.Canonicalize(settings) != StatConfigCanonicalJson.Canonicalize(config.Statistic)
                || !NormalizeLabelCodes(target.StatisticLabelCodes, "$.nativeTargetConfig").SequenceEqual(config.StatisticLabelCodes, StringComparer.Ordinal)
                || !config.LabelSnapshots.Select(l => l.Code).SequenceEqual(config.StatisticLabelCodes, StringComparer.Ordinal)
                || config.LabelSnapshots.Any(l => !l.IsActive || l.Usage != LabelUsages.Statistic || l.DataType != ExpectedLabelDataType(type)
                    || l.VersionNo < 1 || !IsCanonicalObjectId(l.LabelId) || !IsCanonicalObjectId(l.VersionId) || !IsCanonicalSha256(l.ConfigHash)))
                throw IntegrityConflict(owner.Id, "NATIVE_TARGET_STRUCTURE_OR_METADATA");
        }
        return StatConfigCanonicalJson.Canonicalize(configs);
    }

    private async Task<ConfigState> ApplyNativeMutationsAsync(IClientSessionHandle session,
        DynamicFormTemplate owner, ConfigState current, IReadOnlyList<DynamicFormNativeStatisticMutation> mutations,
        MeResponse me, CancellationToken ct)
    {
        var tables = NativeTables(owner);
        if (DynamicFormNativeStatisticState.HasPlan(tables))
            throw Schema("$.payload.nativeTargets", "NATIVE_STATISTICS_V2_COMMAND_REQUIRED");
        var next = (DeserializeNativeSection(current.NativeTargetSectionJson) ?? new()).ToDictionary(c => c.TargetId, StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            var table = tables.SingleOrDefault(t => t.Id == mutation.TableId)
                ?? throw Schema("$.payload.nativeTargets.tableId", "NATIVE_TABLE_NOT_FOUND");
            var index = table.StatisticTargets!.FindIndex(t => t.Id == mutation.TargetId);
            if (index < 0) throw Schema("$.payload.nativeTargets.targetId", "NATIVE_TARGET_NOT_FOUND");
            var target = table.StatisticTargets[index];
            if (mutation.IsStatistic == true)
            {
                var type = ResolveNativeTargetType(table, target.Target!);
                ValidateStatisticSettings(type, mutation.Statistic!, "$.payload.nativeTargets");
                var labels = await ResolveLabelsAsync(session, mutation.StatisticLabelCodes!, type, me, "$.payload.nativeTargets", ct);
                next[target.Id!] = new DynamicFormNativeStatisticConfigDto(table.Id!, target.Id!, target.Target!, type,
                    mutation.Statistic!, mutation.StatisticLabelCodes!, labels, NativeStructureHash(table));
            }
            else next.Remove(target.Id!);
            table.StatisticTargets[index] = target with
            {
                IsStatistic = mutation.IsStatistic,
                Statistic = mutation.IsStatistic == true ? NativeSettingsToSchema(mutation.Statistic!) : default,
                StatisticLabelCodes = mutation.StatisticLabelCodes!.ToArray()
            };
        }
        return current with
        {
            PreviousVersionId = current.VersionId, VersionId = ObjectId.GenerateNewId().ToString(),
            VersionNo = checked(current.VersionNo + 1), Revision = checked(current.Revision + 1), IsVirtual = false,
            NativeTargetSectionJson = StatConfigCanonicalJson.Canonicalize(next.Values.OrderBy(c => c.TargetId, StringComparer.Ordinal)),
            NativeTablesJson = JsonSerializer.Serialize(tables, StatConfigCanonicalJson.StrictJsonOptions)
        };
    }

    private static ConfigState CompleteNativeState(DynamicFormTemplate owner, ConfigState next, string? section, string? tablesJson, string? planSection)
    {
        if (section is null) return next;
        ValidateNativeTargetLimits(next.Fields, next.Tables, section, planSection);
        var pins = NativeDependencyPins(BuildDependencyPins(next.Fields, next.Tables), section, planSection);
        return next with { NativeTargetSectionJson = section, NativeTablesJson = tablesJson, NativePlanSectionJson = planSection, DependencyPins = pins,
            ConfigHash = ComputeConfigHash(owner.Id, next.FieldSectionJson, next.TableSectionJson, pins, section, planSection) };
    }

    private static void ValidateNativeTargetLimits(IReadOnlyList<PersistedFieldConfig> fields, IReadOnlyList<PersistedTableConfig> tables, string? section, string? planSection = null)
    {
        if (section is null) return;
        var native = DeserializeNativeSection(section)!;
        var plan = DeserializeNativePlanSection(planSection);
        if (fields.Count + native.Count + (plan?.Targets.Count ?? 0) + tables.Where(t => !t.StatisticsDisabled).Sum(t => t.Metrics.Count) > 30)
            throw Schema("$.payload", "FORM_STATISTIC_TARGET_LIMIT_30");
        var labels = fields.SelectMany(f => f.StatisticLabelCodes)
            .Concat(tables.SelectMany(t => t.MetricLabelTargets.Select(m => m.StatisticLabelCode)))
            .Concat(native.SelectMany(n => n.StatisticLabelCodes))
            .Concat(plan?.Targets.SelectMany(t => t.Configuration.StatisticLabelCodes!) ?? Array.Empty<string>()).ToArray();
        if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Length)
            throw Schema("$.payload", "DUPLICATE_STATISTIC_LABEL_TARGET");
    }

    private static List<string> NativeDependencyPins(IEnumerable<string> pins, string? section, string? planSection = null)
        => pins.Concat((DeserializeNativeSection(section) ?? new()).SelectMany(c => c.LabelSnapshots)
                .Select(l => $"LABEL:{l.LabelId}:{l.VersionId}:{l.VersionNo}:{l.ConfigHash}"))
            .Concat((DeserializeNativePlanSection(planSection)?.Targets.SelectMany(t => t.LabelSnapshots)
                ?? Array.Empty<DynamicFormStatisticLabelSnapshotDto>()).Select(l => $"LABEL:{l.LabelId}:{l.VersionId}:{l.VersionNo}:{l.ConfigHash}"))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

    private static string NativeStructureHash(DynamicFormNativeTableDto table)
        => StatConfigCanonicalJson.HashObject(new { table.Id, table.SectionId, table.Name, table.Order,
            table.Layout, table.Fields, table.Rows, table.TypeConfig,
            targets = table.StatisticTargets!.Select(t => new { t.Id, t.Target }) });

    private static string ResolveNativeTargetType(DynamicFormNativeTableDto table, DynamicFormNativeTargetDto target)
    {
        var rows = target.Scope == "column" ? (table.Layout == "matrix"
            ? table.Rows!.Select(r => r.Id).ToArray() : new string?[] { null }) : new[] { target.RowId };
        var columns = target.Scope == "row" ? table.Fields!.Select(f => f.Id).ToArray() : new[] { target.FieldId };
        var resolve = DynamicFormNativeTableDefinition.CompileCellTypes(table);
        var specs = rows.SelectMany(row => columns.Select(column => resolve(column!, row))).ToArray();
        if (specs.Length == 0 || specs.Select(s => s.Type).Distinct(StringComparer.Ordinal).Count() != 1)
            throw Schema("$.payload.nativeTargets", "NATIVE_TARGET_MIXED_TYPES");
        if (specs[0].Type is "singleSelect" or "multiSelect"
            && specs.Select(s => StatConfigCanonicalJson.Canonicalize(s.Options!.OrderBy(o => o.Code, StringComparer.Ordinal)))
                .Distinct(StringComparer.Ordinal).Count() != 1)
            throw Schema("$.payload.nativeTargets", "NATIVE_TARGET_MIXED_OPTIONS");
        return MapFieldType(specs[0].Type == "plainText" ? "longText" : specs[0].Type, "$.payload.nativeTargets");
    }

    private static DynamicFormStatisticSettingsPayload NativeSettingsFromSchema(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Schema("$.statistic", "STATISTIC_CONFIG_REQUIRED");
        var settings = JsonSerializer.Deserialize<DynamicFormStatisticSettingsPayload>(element, StatConfigCanonicalJson.StrictJsonOptions)
            ?? throw Schema("$.statistic", "STATISTIC_CONFIG_REQUIRED");
        return settings with
        {
            AggregateOps = settings.AggregateOps?.Select(op => NativeOperations.TryGetValue(op, out var value)
                ? value : throw Schema("$.statistic.aggregateOps", "NATIVE_OPERATION_UNKNOWN")).ToArray(),
            BucketMode = settings.BucketMode switch { "none" => "NONE", "option" => "OPTION", "date" => "DATE",
                _ => throw Schema("$.statistic.bucketMode", "NATIVE_BUCKET_UNKNOWN") }
        };
    }

    private static JsonElement NativeSettingsToSchema(DynamicFormStatisticSettingsPayload settings)
        => JsonSerializer.SerializeToElement(new
        {
            aggregateOps = settings.AggregateOps!.Select(op => NativeOperations.Single(pair => pair.Value == op).Key),
            bucketMode = settings.BucketMode!.ToLowerInvariant(), settings.ShowInDetail, settings.ShowInTree
        }, StatConfigCanonicalJson.StrictJsonOptions);
}
