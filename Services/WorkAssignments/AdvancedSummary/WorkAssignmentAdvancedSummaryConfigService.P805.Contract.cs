using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed partial class WorkAssignmentAdvancedSummaryConfigService
{
    internal const string P805PutCommandKind =
        "UPSERT_ADVANCED_SUMMARY_CONFIG";
    internal const string P805LockCommandKind =
        "LOCK_ADVANCED_SUMMARY_CONFIG";
    internal const string P805NextDraftCommandKind =
        "CREATE_ADVANCED_SUMMARY_DRAFT";
    internal const string P805ArchiveCommandKind =
        "ARCHIVE_ADVANCED_SUMMARY_CONFIG";

    private static readonly IReadOnlySet<string> P805SourceModes =
        P805Set(
            "DIRECT_CHILDREN_OR_SELF",
            "DIRECT_CHILDREN",
            "SELF",
            "FLOW_BRANCH",
            "FLOW_STEP",
            "FLOW_EFFECTIVE_PATH",
            "FLOW_FINAL");

    private static readonly IReadOnlySet<string> P805FlowModes =
        P805Set(
            "FLOW_BRANCH",
            "FLOW_STEP",
            "FLOW_EFFECTIVE_PATH",
            "FLOW_FINAL");

    private static readonly IReadOnlySet<string> P805FlowStatuses =
        P805Set(
            "EFFECTIVE",
            "INVALIDATED",
            "TERMINATED",
            "ANY");

    private static readonly IReadOnlySet<string> P805GroupingValues =
        P805Set("UNIT", "ASSIGNMENT", "PERIOD");

    private static readonly IReadOnlySet<string> P805OrderingDirections =
        P805Set("ASC", "DESC");

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        P805OperationsByDataType =
            new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.Ordinal)
            {
                ["NUMBER"] = P805Set(
                    "SUM",
                    "MIN",
                    "MAX",
                    "MEAN",
                    "COUNT"),
                ["DATE"] = P805Set(
                    "MIN_DATE",
                    "MAX_DATE",
                    "COUNT"),
                ["BOOLEAN"] = P805Set(
                    "TRUE_COUNT",
                    "FALSE_COUNT",
                    "COUNT"),
                ["CHOICE"] = P805Set(
                    "BUCKET_COUNT",
                    "COUNT"),
                ["TEXT"] = P805Set(
                    "JOIN",
                    "COUNT")
            };

    private static WorkAssignmentAdvancedSummaryConfigPayload
        P805VirtualEmptyPayload(string sectionId)
        => new(
            new WorkAssignmentAdvancedSummarySourceScopePayload(
                "DIRECT_CHILDREN_OR_SELF",
                null,
                null,
                null,
                null),
            new[]
            {
                new WorkAssignmentAdvancedSummarySectionPayload(
                    sectionId,
                    false,
                    Array.Empty<
                        WorkAssignmentAdvancedSummaryTargetPayload>())
            },
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<WorkAssignmentAdvancedSummaryOrderingPayload>(),
            null);

    private static NormalizedStatConfigCommand<
        WorkAssignmentAdvancedSummaryConfigPayload>
        NormalizeP805PutCommand(
            JsonElement body,
            string routeSectionId)
    {
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkAssignmentAdvancedSummaryConfigPayload>>(body);
        var normalizedPayload =
            NormalizeP805Payload(envelope.Payload, routeSectionId);
        EnsureP805PayloadSize(normalizedPayload);
        return StatConfigCanonicalJson.NormalizeCommand(
            new StatConfigMutationEnvelope<
                WorkAssignmentAdvancedSummaryConfigPayload>(
                envelope.CommandId,
                envelope.ExpectedRevision,
                envelope.ExpectedConfigHash,
                normalizedPayload),
            P805PutCommandKind);
    }

    private static NormalizedStatConfigCommand<
        WorkAssignmentAdvancedSummaryEmptyCommandPayload>
        NormalizeP805EmptyCommand(
            JsonElement body,
            string commandKind)
    {
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkAssignmentAdvancedSummaryEmptyCommandPayload>>(body);
        return StatConfigCanonicalJson.NormalizeCommand(
            envelope,
            commandKind);
    }

    internal static WorkAssignmentAdvancedSummaryConfigPayload
        NormalizeP805Payload(
            WorkAssignmentAdvancedSummaryConfigPayload? input,
            string routeSectionId)
    {
        if (input is null)
            throw P805Schema("$.payload", "PAYLOAD_REQUIRED");

        var sectionId = P805RequiredIdentity(
            routeSectionId,
            "$.route.sectionId",
            "SECTION_ID_REQUIRED");

        return new WorkAssignmentAdvancedSummaryConfigPayload(
            NormalizeP805SourceScope(input.SourceScope),
            NormalizeP805Sections(input.Sections, sectionId),
            NormalizeP805HierarchyGrains(input.HierarchyGrains),
            NormalizeP805Grouping(input.Grouping),
            NormalizeP805Ordering(
                input.Ordering,
                input.Sections,
                sectionId),
            input.Description);
    }

    private static WorkAssignmentAdvancedSummarySourceScopePayload
        NormalizeP805SourceScope(
            WorkAssignmentAdvancedSummarySourceScopePayload? input)
    {
        if (input is null)
        {
            throw P805Schema(
                "$.payload.sourceScope",
                "SOURCE_SCOPE_REQUIRED");
        }

        var mode = P805RequiredEnum(
            input.Mode,
            "$.payload.sourceScope.mode",
            "SOURCE_SCOPE_MODE_REQUIRED",
            "SOURCE_SCOPE_MODE_UNSUPPORTED",
            P805SourceModes);
        if (!P805FlowModes.Contains(mode))
        {
            P805RejectPresent(
                input.FlowInstanceId,
                "$.payload.sourceScope.flowInstanceId");
            P805RejectPresent(
                input.FlowStepId,
                "$.payload.sourceScope.flowStepId");
            P805RejectPresent(
                input.FlowBranchId,
                "$.payload.sourceScope.flowBranchId");
            P805RejectPresent(
                input.FlowEffectiveStatus,
                "$.payload.sourceScope.flowEffectiveStatus");
            return new WorkAssignmentAdvancedSummarySourceScopePayload(
                mode,
                null,
                null,
                null,
                null);
        }

        var flowInstanceId = P805RequiredObjectId(
            input.FlowInstanceId,
            "$.payload.sourceScope.flowInstanceId",
            "FLOW_INSTANCE_ID_REQUIRED");
        var flowStatus = P805RequiredEnum(
            input.FlowEffectiveStatus,
            "$.payload.sourceScope.flowEffectiveStatus",
            "FLOW_EFFECTIVE_STATUS_REQUIRED",
            "FLOW_EFFECTIVE_STATUS_UNSUPPORTED",
            P805FlowStatuses);

        string? flowStepId = null;
        string? flowBranchId = null;
        if (mode == "FLOW_STEP")
        {
            flowStepId = P805RequiredIdentity(
                input.FlowStepId,
                "$.payload.sourceScope.flowStepId",
                "FLOW_STEP_ID_REQUIRED");
            P805RejectPresent(
                input.FlowBranchId,
                "$.payload.sourceScope.flowBranchId");
        }
        else if (mode == "FLOW_BRANCH")
        {
            flowBranchId = P805RequiredObjectId(
                input.FlowBranchId,
                "$.payload.sourceScope.flowBranchId",
                "FLOW_BRANCH_ID_REQUIRED");
            P805RejectPresent(
                input.FlowStepId,
                "$.payload.sourceScope.flowStepId");
        }
        else
        {
            P805RejectPresent(
                input.FlowStepId,
                "$.payload.sourceScope.flowStepId");
            P805RejectPresent(
                input.FlowBranchId,
                "$.payload.sourceScope.flowBranchId");
        }

        return new WorkAssignmentAdvancedSummarySourceScopePayload(
            mode,
            flowInstanceId,
            flowStepId,
            flowBranchId,
            flowStatus);
    }

    private static IReadOnlyList<WorkAssignmentAdvancedSummarySectionPayload>
        NormalizeP805Sections(
            IReadOnlyList<WorkAssignmentAdvancedSummarySectionPayload>? input,
            string routeSectionId)
    {
        if (input is null)
            throw P805Schema("$.payload.sections", "SECTIONS_REQUIRED");
        if (input.Count != 1)
        {
            throw P805Schema(
                "$.payload.sections",
                "EXACTLY_ONE_SECTION_REQUIRED");
        }

        var section = input[0] ??
                      throw P805Schema(
                          "$.payload.sections[0]",
                          "SECTION_OBJECT_REQUIRED");
        var sectionId = P805RequiredIdentity(
            section.SectionId,
            "$.payload.sections[0].sectionId",
            "SECTION_ID_REQUIRED");
        if (!string.Equals(
                routeSectionId,
                sectionId,
                StringComparison.Ordinal))
        {
            throw P805Schema(
                "$.payload.sections[0].sectionId",
                "SECTION_ROUTE_MISMATCH");
        }
        if (!section.IsCumulative.HasValue)
        {
            throw P805Schema(
                "$.payload.sections[0].isCumulative",
                "IS_CUMULATIVE_REQUIRED");
        }

        var targets = NormalizeP805Targets(
            section.Targets,
            section.IsCumulative.Value);
        var nativeTargets = NormalizeP805NativeTargets(section.NativeTargets);
        var limit = section.IsCumulative.Value
            ? WorkAssignmentAdvancedSummaryConfigContract.MaxCumulativeTargets
            : WorkAssignmentAdvancedSummaryConfigContract.MaxNonCumulativeTargets;
        if (targets.Count + (nativeTargets?.Count ?? 0) > limit)
            throw P805Schema("$.payload.sections[0]", "TARGET_LIMIT_EXCEEDED");
        return new[]
        {
            new WorkAssignmentAdvancedSummarySectionPayload(
                sectionId,
                section.IsCumulative.Value,
                targets,
                nativeTargets)
        };
    }

    private static IReadOnlyList<WorkAssignmentAdvancedSummaryTargetPayload>
        NormalizeP805Targets(
            IReadOnlyList<WorkAssignmentAdvancedSummaryTargetPayload>? input,
            bool isCumulative)
    {
        const string root = "$.payload.sections[0].targets";
        if (input is null)
            throw P805Schema(root, "TARGETS_REQUIRED");

        var limit = isCumulative
            ? WorkAssignmentAdvancedSummaryConfigContract
                .MaxCumulativeTargets
            : WorkAssignmentAdvancedSummaryConfigContract
                .MaxNonCumulativeTargets;
        if (input.Count > limit)
            throw P805Schema(root, "TARGET_LIMIT_EXCEEDED");

        var result =
            new List<WorkAssignmentAdvancedSummaryTargetPayload>(
                input.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < input.Count; index++)
        {
            var path = $"{root}[{index}]";
            var target = input[index] ??
                         throw P805Schema(
                             path,
                             "TARGET_OBJECT_REQUIRED");
            var fieldId = P805RequiredIdentity(
                target.FieldId,
                $"{path}.fieldId",
                "FIELD_ID_REQUIRED");
            if (!seen.Add(fieldId))
            {
                throw P805Schema(
                    $"{path}.fieldId",
                    "DUPLICATE_TARGET_FIELD");
            }

            var dataType = P805RequiredEnum(
                target.DataType,
                $"{path}.dataType",
                "DATA_TYPE_REQUIRED",
                "DATA_TYPE_UNSUPPORTED",
                P805OperationsByDataType.Keys.ToHashSet(
                    StringComparer.Ordinal));
            var operation = P805RequiredEnum(
                target.Operation,
                $"{path}.operation",
                "OPERATION_REQUIRED",
                "OPERATION_UNSUPPORTED",
                P805OperationsByDataType[dataType]);
            result.Add(
                new WorkAssignmentAdvancedSummaryTargetPayload(
                    fieldId,
                    dataType,
                    operation));
        }
        return result;
    }

    private static IReadOnlyList<string> NormalizeP805HierarchyGrains(
        IReadOnlyList<string>? input)
    {
        const string root = "$.payload.hierarchyGrains";
        if (input is null)
            throw P805Schema(root, "HIERARCHY_GRAINS_REQUIRED");
        if (input.Count >
            WorkAssignmentAdvancedSummaryConfigContract.MaxHierarchyDepth)
        {
            throw P805Schema(root, "HIERARCHY_DEPTH_EXCEEDED");
        }

        var result = new List<string>(input.Count);
        for (var index = 0; index < input.Count; index++)
        {
            var value = P805RequiredIdentity(
                    input[index],
                    $"{root}[{index}]",
                    "HIERARCHY_GRAIN_REQUIRED")
                .ToUpperInvariant();
            var expected =
                WorkAssignmentAdvancedSummaryConfigContract
                    .HierarchyPrefix[index];
            if (!string.Equals(value, expected, StringComparison.Ordinal))
            {
                throw P805Schema(
                    $"{root}[{index}]",
                    "HIERARCHY_MUST_BE_DAY_MONTH_YEAR_PREFIX");
            }
            result.Add(value);
        }
        return result;
    }

    private static IReadOnlyList<string> NormalizeP805Grouping(
        IReadOnlyList<string>? input)
    {
        const string root = "$.payload.grouping";
        if (input is null)
            throw P805Schema(root, "GROUPING_REQUIRED");

        var result = new List<string>(input.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < input.Count; index++)
        {
            var value = P805RequiredEnum(
                input[index],
                $"{root}[{index}]",
                "GROUPING_VALUE_REQUIRED",
                "GROUPING_VALUE_UNSUPPORTED",
                P805GroupingValues);
            if (!seen.Add(value))
                throw P805Schema($"{root}[{index}]", "DUPLICATE_GROUPING");
            result.Add(value);
        }
        return result;
    }

    private static IReadOnlyList<WorkAssignmentAdvancedSummaryOrderingPayload>
        NormalizeP805Ordering(
            IReadOnlyList<WorkAssignmentAdvancedSummaryOrderingPayload>? input,
            IReadOnlyList<WorkAssignmentAdvancedSummarySectionPayload>? sections,
            string routeSectionId)
    {
        const string root = "$.payload.ordering";
        if (input is null)
            throw P805Schema(root, "ORDERING_REQUIRED");

        var normalizedSections =
            NormalizeP805Sections(sections, routeSectionId);
        var targets = normalizedSections[0].Targets!
            .Select(item => item.FieldId!)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result =
            new List<WorkAssignmentAdvancedSummaryOrderingPayload>(
                input.Count);
        for (var index = 0; index < input.Count; index++)
        {
            var path = $"{root}[{index}]";
            var item = input[index] ??
                       throw P805Schema(
                           path,
                           "ORDERING_OBJECT_REQUIRED");
            var fieldId = P805RequiredIdentity(
                item.FieldId,
                $"{path}.fieldId",
                "FIELD_ID_REQUIRED");
            if (!seen.Add(fieldId))
                throw P805Schema($"{path}.fieldId", "DUPLICATE_ORDERING_FIELD");
            if (!targets.Contains(fieldId))
            {
                throw P805Schema(
                    $"{path}.fieldId",
                    "ORDERING_FIELD_NOT_TARGETED");
            }
            var direction = P805RequiredEnum(
                item.Direction,
                $"{path}.direction",
                "ORDERING_DIRECTION_REQUIRED",
                "ORDERING_DIRECTION_UNSUPPORTED",
                P805OrderingDirections);
            result.Add(
                new WorkAssignmentAdvancedSummaryOrderingPayload(
                    fieldId,
                    direction));
        }
        return result;
    }

    private static string P805RequiredEnum(
        string? input,
        string path,
        string missingReason,
        string unsupportedReason,
        IReadOnlySet<string> allowed)
    {
        var value = P805RequiredIdentity(input, path, missingReason)
            .ToUpperInvariant();
        if (!allowed.Contains(value))
            throw P805Schema(path, unsupportedReason);
        return value;
    }

    private static string P805RequiredIdentity(
        string? input,
        string path,
        string missingReason)
    {
        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw P805Schema(path, missingReason);
        if (value.Length > 256 || value.Any(char.IsControl))
            throw P805Schema(path, "IDENTITY_INVALID");
        return value;
    }

    private static string P805RequiredObjectId(
        string? input,
        string path,
        string missingReason)
    {
        var value = P805RequiredIdentity(input, path, missingReason);
        if (!ObjectId.TryParse(value, out var parsed) ||
            !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw P805Schema(path, "OBJECT_ID_NOT_CANONICAL");
        }
        return value;
    }

    private static void P805RejectPresent(string? value, string path)
    {
        if (value is not null)
            throw P805Schema(path, "SOURCE_SCOPE_FIELD_NOT_APPLICABLE");
    }

    private static IReadOnlySet<string> P805Set(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

    private static AppException P805Schema(string path, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new { path, reason });

    private static string P805CanonicalPayload(
        WorkAssignmentAdvancedSummaryConfigPayload payload)
        => StatConfigCanonicalJson.Canonicalize(payload);

    internal static int P805CanonicalPayloadBytes(
        WorkAssignmentAdvancedSummaryConfigPayload payload)
        => Encoding.UTF8.GetByteCount(P805CanonicalPayload(payload));

    internal static void EnsureP805PayloadSize(
        WorkAssignmentAdvancedSummaryConfigPayload payload)
    {
        if (P805CanonicalPayloadBytes(payload) >
            WorkAssignmentAdvancedSummaryConfigContract
                .MaxCanonicalPayloadBytes)
        {
            throw P805Schema(
                "$.payload",
                "CANONICAL_PAYLOAD_TOO_LARGE");
        }
    }

    private static string P805ComputeConfigHash(
        WorkAssignmentAdvancedSummaryConfigPayload payload,
        IReadOnlyList<string> dependencyPins)
        => StatConfigCanonicalJson.HashObject(
            new
            {
                payload,
                dependencyPins
            });

    private static WorkAssignmentAdvancedSummaryConfigPayload
        P805DeserializeStoredPayload(
            string configJson,
            string routeSectionId)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            var payload =
                StatConfigCanonicalJson.DeserializeStrict<
                    WorkAssignmentAdvancedSummaryConfigPayload>(
                    document.RootElement);
            var normalized =
                NormalizeP805Payload(payload, routeSectionId);
            EnsureP805PayloadSize(normalized);
            return normalized;
        }
        catch (AppException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw P805Schema(
                "$.configJson",
                "STORED_CONFIG_JSON_INVALID");
        }
    }
}
