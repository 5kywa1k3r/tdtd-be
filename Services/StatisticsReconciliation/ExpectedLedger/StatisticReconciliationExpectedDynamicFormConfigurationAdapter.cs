using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed record StatisticReconciliationExpectedLockedP8OwnerCandidate(
    string Kind,
    string OwnerId,
    string ConfigId,
    string ConfigurationJson,
    string ConfigSha256,
    string VersionId,
    int VersionNo,
    long Revision);

/// <summary>
/// Adapts the exact locked P8 Dynamic Form statistic owner into the neutral
/// expected-ledger metric plan. Both the plan and the field-value projection are
/// derived only from the trusted P8 snapshot and verified approved payload bytes.
/// </summary>
internal static class
    StatisticReconciliationExpectedDynamicFormConfigurationAdapter
{
    internal const string OwnerNotExactReason =
        "P8_CONFIG_OWNER_NOT_EXACT";
    internal const string MetricPlanRequiredReason =
        "DYNAMIC_FORM_EXPECTED_METRICS_REQUIRED";
    internal const string MetricUnsupportedReason =
        "DYNAMIC_FORM_EXPECTED_METRIC_UNSUPPORTED";
    internal const string DirectFieldScopeNotExactReason =
        "DYNAMIC_FORM_DIRECT_FIELD_SCOPE_NOT_EXACT";

    private const int MaxFieldConfigurations = 30;
    private static readonly JsonSerializerOptions StrictJson = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static StatisticReconciliationExpectedLockedP8OwnerCandidate?
        TryBuildCandidate(
            DynamicFormTemplate owner,
            StatisticReconciliationRun run)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(run);
        if (owner.IsDeleted || !owner.IsActive || !owner.IsPublished ||
            !StringComparer.Ordinal.Equals(owner.Id, run.P8ConfigOwnerId) ||
            !StringComparer.Ordinal.Equals(owner.Id, run.DynamicFormVersionId))
            return null;

        var trusted = DynamicFormStatisticConfigCommandService
            .GetP804TrustedPersistedView(
                owner,
                run.P8ConfigId,
                run.P8ConfigVersionId,
                run.P8ConfigVersionNo,
                run.P8ConfigRevision,
                run.P8ConfigHash);
        if (trusted is null ||
            !StringComparer.Ordinal.Equals(trusted.Status, "LOCKED") ||
            !StringComparer.Ordinal.Equals(trusted.ConfigId, run.P8ConfigId) ||
            !StringComparer.Ordinal.Equals(
                trusted.VersionId,
                run.P8ConfigVersionId) ||
            trusted.VersionNo != run.P8ConfigVersionNo ||
            trusted.Revision != run.P8ConfigRevision ||
            !StringComparer.Ordinal.Equals(
                trusted.ConfigHash,
                run.P8ConfigHash))
            return null;

        return new StatisticReconciliationExpectedLockedP8OwnerCandidate(
            StatisticReconciliationExpectedLedgerConfigurationKinds.DynamicForm,
            owner.Id,
            trusted.ConfigId,
            BuildExpectedMetricConfiguration(trusted, run),
            trusted.ConfigHash,
            trusted.VersionId,
            trusted.VersionNo,
            trusted.Revision);
    }

    internal static StatisticReconciliationExpectedLockedP8OwnerCandidate
        RequireExact(
            IEnumerable<StatisticReconciliationExpectedLockedP8OwnerCandidate>
                candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var exact = candidates.Take(2).ToArray();
        if (exact.Length != 1)
            throw Fail(OwnerNotExactReason, "$.authoritativeSnapshot.lockedP8Configuration");
        return exact[0];
    }

    internal static string ProjectApprovedRawPayload(
        string canonicalPayloadJson)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(canonicalPayloadJson) as JsonObject ??
                   throw Fail(
                       "APPROVED_RAW_PAYLOAD_OBJECT_REQUIRED",
                       "$.authoritativeSnapshot.payload");
        }
        catch (JsonException exception)
        {
            throw Fail(
                "APPROVED_RAW_PAYLOAD_JSON_INVALID",
                "$.authoritativeSnapshot.payload",
                exception);
        }

        JsonObject fieldValues;
        if (root["fieldValues"] is null)
        {
            fieldValues = new JsonObject();
        }
        else if (root["fieldValues"] is not JsonObject rawFieldValues)
        {
            throw Fail(
                "APPROVED_RAW_FIELD_VALUES_OBJECT_REQUIRED",
                "$.authoritativeSnapshot.payload.fieldValues");
        }
        else if (rawFieldValues["values"] is null)
        {
            fieldValues = (JsonObject)rawFieldValues.DeepClone();
        }
        else if (rawFieldValues["values"] is JsonObject values)
        {
            fieldValues = (JsonObject)values.DeepClone();
        }
        else
        {
            throw Fail(
                "APPROVED_RAW_FIELD_VALUES_ENVELOPE_INVALID",
                "$.authoritativeSnapshot.payload.fieldValues.values");
        }

        root["directFieldValues"] = fieldValues;
        return StatisticReconciliationExpectedLedgerCanonicalizer
            .NormalizeObject(
                root.ToJsonString(),
                16 * 1024 * 1024,
                "$.authoritativeSnapshot.payload",
                StatisticReconciliationExpectedLedgerInputFailureReasons
                    .PayloadJsonInvalid)
            .Value;
    }

    private static string BuildExpectedMetricConfiguration(
        DynamicFormStatisticConfigCommandService.P804TrustedPersistedView
            trusted,
        StatisticReconciliationRun run)
    {
        var fields = DeserializeFields(trusted.FieldSectionJson);
        RequireEmptyTableSection(trusted.TableSectionJson);
        if (fields.Count is < 1 or > MaxFieldConfigurations)
            throw Fail(
                MetricPlanRequiredReason,
                "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig");
        RequireExactDirectFieldConfiguration(fields, run);

        var metrics = new JsonArray();
        foreach (var field in fields
                     .OrderBy(item => item.FieldId, StringComparer.Ordinal))
        {
            if (!field.IsStatistic || field.Statistic is null ||
                string.IsNullOrWhiteSpace(field.FieldId) ||
                field.FieldId != field.FieldId.Trim())
                throw Fail(
                    "DYNAMIC_FORM_FIELD_CONFIG_INVALID",
                    "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig");

            var labels = RequireMetricLabels(field);
            if (labels.Length == 0)
                continue;
            var metric = ResolveMetric(field);
            foreach (var label in labels)
            {
                metrics.Add(new JsonObject
                {
                    ["family"] = StatisticReconciliationExpectedMetricFamilies.Direct,
                    ["kind"] = StatisticReconciliationExpectedMetricKinds.Field,
                    ["metricId"] = label,
                    ["fieldId"] = field.FieldId,
                    ["jsonPointer"] =
                        "/directFieldValues/" + EscapeJsonPointer(field.FieldId),
                    ["valueType"] = metric.ValueType,
                    ["unordered"] = false,
                    ["expandArray"] = metric.ExpandArray,
                    ["operations"] = new JsonArray(
                        metric.Operations
                            .Select(operation => JsonValue.Create(operation))
                            .ToArray())
                });
            }
        }

        if (metrics.Count == 0)
            throw Fail(
                MetricPlanRequiredReason,
                "$.authoritativeSnapshot.lockedP8Configuration.expectedMetrics");
        var canonical = StatisticReconciliationExpectedLedgerCanonicalizer
            .NormalizeObject(
                new JsonObject { ["expectedMetrics"] = metrics }.ToJsonString(),
                4 * 1024 * 1024,
                "$.authoritativeSnapshot.lockedP8Configuration",
                StatisticReconciliationExpectedLedgerInputFailureReasons
                    .ConfigurationInvalid);
        return canonical.Value;
    }

    private static void RequireExactDirectFieldConfiguration(
        IReadOnlyList<DynamicFormStatisticFieldConfigDto> fields,
        StatisticReconciliationRun run)
    {
        if (!StatisticReconciliationActualCapturePlanIntegrity.IsV4(
                run.ActualCapturePlan) ||
            !StringComparer.Ordinal.Equals(
                run.ActualCapturePlan?.Api?.Surface,
                "DIRECT_FIELD"))
        {
            return;
        }

        if (!StatisticReconciliationRunService
                .TryResolveExactDirectFieldExpectedScope(
                    run.CanonicalFilterJson,
                    run.FilterHash,
                    run.PeriodInstanceKey,
                    run.ConceptKey,
                    out var scope) ||
            scope is null)
        {
            throw Fail(
                DirectFieldScopeNotExactReason,
                "$.authoritativeSnapshot.run.canonicalFilterJson");
        }

        var matches = fields.Where(field =>
                StringComparer.Ordinal.Equals(field.FieldId, scope.FieldId))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw Fail(
                DirectFieldScopeNotExactReason,
                "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig");
        }
    }

    private static IReadOnlyList<DynamicFormStatisticFieldConfigDto>
        DeserializeFields(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Field configuration must be an array.");
            return JsonSerializer.Deserialize<
                       List<DynamicFormStatisticFieldConfigDto>>(
                       document.RootElement.GetRawText(),
                       StrictJson) ?? [];
        }
        catch (JsonException exception)
        {
            throw Fail(
                "DYNAMIC_FORM_FIELD_CONFIG_INVALID",
                "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig",
                exception);
        }
    }

    private static void RequireEmptyTableSection(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != 0)
                throw Fail(
                    MetricUnsupportedReason,
                    "$.authoritativeSnapshot.lockedP8Configuration.tableConfig");
        }
        catch (JsonException exception)
        {
            throw Fail(
                "DYNAMIC_FORM_TABLE_CONFIG_INVALID",
                "$.authoritativeSnapshot.lockedP8Configuration.tableConfig",
                exception);
        }
    }

    private static ImmutableArray<string> RequireMetricLabels(
        DynamicFormStatisticFieldConfigDto field)
    {
        var labels = (field.StatisticLabelCodes ?? [])
            .Select(code => code?.Trim())
            .ToArray();
        if (labels.Any(string.IsNullOrWhiteSpace) ||
            labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
            throw Fail(
                "DYNAMIC_FORM_FIELD_LABELS_INVALID",
                "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig");

        foreach (var label in labels)
        {
            var snapshots = (field.LabelSnapshots ?? [])
                .Where(snapshot => StringComparer.Ordinal.Equals(
                    snapshot.Code,
                    label))
                .Take(2)
                .ToArray();
            if (snapshots.Length != 1 || !snapshots[0].IsActive ||
                !StringComparer.Ordinal.Equals(snapshots[0].Usage, "STATISTIC") ||
                string.IsNullOrWhiteSpace(snapshots[0].LabelId) ||
                string.IsNullOrWhiteSpace(snapshots[0].VersionId) ||
                snapshots[0].VersionNo <= 0 ||
                !IsLowerSha256(snapshots[0].ConfigHash))
                throw Fail(
                    "DYNAMIC_FORM_FIELD_LABEL_PIN_NOT_EXACT",
                    "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig");
        }
        return labels.Select(label => label!).ToImmutableArray();
    }

    private static DynamicFormMetric ResolveMetric(
        DynamicFormStatisticFieldConfigDto field)
    {
        var spec = field.FieldType switch
        {
            DynamicFormStatisticFieldTypes.Number =>
                new DynamicFormMetric(
                    StatisticReconciliationExpectedValueTypes.Number,
                    false),
            DynamicFormStatisticFieldTypes.Boolean =>
                new DynamicFormMetric(
                    StatisticReconciliationExpectedValueTypes.Boolean,
                    false),
            DynamicFormStatisticFieldTypes.SingleSelect or
                DynamicFormStatisticFieldTypes.ShortText =>
                new DynamicFormMetric(
                    StatisticReconciliationExpectedValueTypes.Bucket,
                    false),
            DynamicFormStatisticFieldTypes.MultiSelect =>
                new DynamicFormMetric(
                    StatisticReconciliationExpectedValueTypes.Bucket,
                    true),
            DynamicFormStatisticFieldTypes.LongText =>
                new DynamicFormMetric(
                    StatisticReconciliationExpectedValueTypes.Text,
                    false),
            _ => throw Fail(
                MetricUnsupportedReason,
                "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig")
        };

        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in field.Statistic!.AggregateOps ?? [])
        {
            switch (operation)
            {
                case DynamicFormStatisticAggregateOperations.Count:
                    operations.Add(
                        StatisticReconciliationExpectedMetricOperations.Count);
                    break;
                case DynamicFormStatisticAggregateOperations.Sum:
                    operations.Add(
                        StatisticReconciliationExpectedMetricOperations.Sum);
                    break;
                case DynamicFormStatisticAggregateOperations.Average:
                    operations.Add(
                        StatisticReconciliationExpectedMetricOperations.Mean);
                    break;
                case DynamicFormStatisticAggregateOperations.Minimum:
                    operations.Add(
                        StatisticReconciliationExpectedMetricOperations.Min);
                    break;
                case DynamicFormStatisticAggregateOperations.Maximum:
                    operations.Add(
                        StatisticReconciliationExpectedMetricOperations.Max);
                    break;
                case DynamicFormStatisticAggregateOperations.BucketCount:
                case DynamicFormStatisticAggregateOperations.TrueCount:
                case DynamicFormStatisticAggregateOperations.FalseCount:
                    operations.Add(
                        StatisticReconciliationExpectedMetricOperations.Values);
                    break;
                default:
                    throw Fail(
                        MetricUnsupportedReason,
                        "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig");
            }
        }
        if (operations.Count == 0)
            throw Fail(
                MetricUnsupportedReason,
                "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig");
        if ((operations.Contains(
                 StatisticReconciliationExpectedMetricOperations.Sum) ||
             operations.Contains(
                 StatisticReconciliationExpectedMetricOperations.Mean)) &&
            spec.ValueType != StatisticReconciliationExpectedValueTypes.Number)
            throw Fail(
                MetricUnsupportedReason,
                "$.authoritativeSnapshot.lockedP8Configuration.fieldConfig");
        return spec with
        {
            Operations = operations
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToImmutableArray()
        };
    }

    private static string EscapeJsonPointer(string value)
        => value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is >= '0' and <= '9' or
               >= 'a' and <= 'f');

    private static StatisticReconciliationExpectedLedgerInputException Fail(
        string reason,
        string path,
        Exception? innerException = null)
        => new(
            StatisticReconciliationExpectedSourcePlanningFailureReasons
                .SnapshotInvalid,
            path,
            innerException is null ? reason : $"{reason}: {innerException.Message}");

    private sealed record DynamicFormMetric(
        string ValueType,
        bool ExpandArray,
        ImmutableArray<string> Operations = default);
}
