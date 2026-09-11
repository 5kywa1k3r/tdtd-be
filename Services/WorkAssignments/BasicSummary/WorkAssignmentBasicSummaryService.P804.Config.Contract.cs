using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed partial class WorkAssignmentBasicSummaryService
{
    internal const string P804PutCommandKind =
        StatConfigCommandKinds.UpsertBasicSummaryConfig;
    internal const string P804LockCommandKind =
        StatConfigCommandKinds.LockBasicSummaryConfig;
    internal const string P804NextDraftCommandKind =
        StatConfigCommandKinds.CreateBasicSummaryDraft;

    private static readonly IReadOnlySet<string> P804SourceModes =
        P804Set(
            WorkAssignmentBasicSummaryConfigContract.DirectChildrenOrSelf,
            WorkAssignmentBasicSummaryConfigContract.DirectChildren,
            WorkAssignmentBasicSummaryConfigContract.Self,
            WorkAssignmentBasicSummaryConfigContract.FlowBranch,
            WorkAssignmentBasicSummaryConfigContract.FlowStep,
            WorkAssignmentBasicSummaryConfigContract.FlowEffectivePath,
            WorkAssignmentBasicSummaryConfigContract.FlowFinal);

    private static readonly IReadOnlySet<string> P804FlowStatuses =
        P804Set(
            WorkAssignmentBasicSummaryConfigContract.Effective,
            WorkAssignmentBasicSummaryConfigContract.Invalidated,
            WorkAssignmentBasicSummaryConfigContract.Terminated,
            WorkAssignmentBasicSummaryConfigContract.Any);

    private static readonly IReadOnlySet<string> P804PeriodModes =
        P804Set(
            WorkAssignmentBasicSummaryConfigContract.AllPeriods,
            WorkAssignmentBasicSummaryConfigContract.SinglePeriod,
            WorkAssignmentBasicSummaryConfigContract.PeriodRange);

    private static readonly IReadOnlySet<string> P804GroupingHints =
        P804Set(
            WorkAssignmentBasicSummaryConfigContract.Unit,
            WorkAssignmentBasicSummaryConfigContract.Assignment,
            WorkAssignmentBasicSummaryConfigContract.Period);

    private static readonly IReadOnlySet<string> P804ConceptKinds =
        P804Set(
            WorkAssignmentBasicSummaryConfigContract.Field,
            WorkAssignmentBasicSummaryConfigContract.TableMetric,
            WorkAssignmentBasicSummaryConfigContract.RowLabel);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        P804OperationsByDataType =
            new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.Ordinal)
            {
                [WorkAssignmentBasicSummaryConfigContract.Number] =
                    P804Set(
                        WorkAssignmentBasicSummaryConfigContract.Sum,
                        WorkAssignmentBasicSummaryConfigContract.Minimum,
                        WorkAssignmentBasicSummaryConfigContract.Maximum,
                        WorkAssignmentBasicSummaryConfigContract.Mean,
                        WorkAssignmentBasicSummaryConfigContract.Count),
                [WorkAssignmentBasicSummaryConfigContract.Date] =
                    P804Set(
                        WorkAssignmentBasicSummaryConfigContract.MinimumDate,
                        WorkAssignmentBasicSummaryConfigContract.MaximumDate,
                        WorkAssignmentBasicSummaryConfigContract.Count),
                [WorkAssignmentBasicSummaryConfigContract.Boolean] =
                    P804Set(
                        WorkAssignmentBasicSummaryConfigContract.TrueCount,
                        WorkAssignmentBasicSummaryConfigContract.FalseCount,
                        WorkAssignmentBasicSummaryConfigContract.Count),
                [WorkAssignmentBasicSummaryConfigContract.Choice] =
                    P804Set(
                        WorkAssignmentBasicSummaryConfigContract.BucketCount,
                        WorkAssignmentBasicSummaryConfigContract.Count),
                [WorkAssignmentBasicSummaryConfigContract.Text] =
                    P804Set(
                        WorkAssignmentBasicSummaryConfigContract.Join,
                        WorkAssignmentBasicSummaryConfigContract.Count)
            };

    private static readonly WorkAssignmentBasicSummaryConfigPayload
        P804VirtualEmptyPayload = new(
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract
                    .DirectChildrenOrSelf,
                null,
                null,
                null,
                null),
            new WorkAssignmentBasicSummaryPeriodRulePayload(
                WorkAssignmentBasicSummaryConfigContract.AllPeriods,
                null,
                null,
                null),
            Array.Empty<string>(),
            new WorkAssignmentBasicSummaryDetailHintsPayload(
                false,
                DefaultMaxTextChars),
            Array.Empty<WorkAssignmentBasicSummaryTargetPayload>());

    private static NormalizedStatConfigCommand<
        WorkAssignmentBasicSummaryConfigPayload>
        NormalizeP804PutCommand(JsonElement body)
    {
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkAssignmentBasicSummaryConfigPayload>>(body);
        var normalizedPayload =
            NormalizeP804Payload(envelope.Payload);
        return StatConfigCanonicalJson.NormalizeCommand(
            new StatConfigMutationEnvelope<
                WorkAssignmentBasicSummaryConfigPayload>(
                envelope.CommandId,
                envelope.ExpectedRevision,
                envelope.ExpectedConfigHash,
                normalizedPayload),
            P804PutCommandKind);
    }

    private static NormalizedStatConfigCommand<
        WorkAssignmentBasicSummaryEmptyCommandPayload>
        NormalizeP804EmptyCommand(
            JsonElement body,
            string commandKind)
    {
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkAssignmentBasicSummaryEmptyCommandPayload>>(body);
        return StatConfigCanonicalJson.NormalizeCommand(
            envelope,
            commandKind);
    }

    internal static WorkAssignmentBasicSummaryConfigPayload
        NormalizeP804Payload(
            WorkAssignmentBasicSummaryConfigPayload? input)
    {
        if (input is null)
            throw P804Schema("$.payload", "PAYLOAD_REQUIRED");

        return new WorkAssignmentBasicSummaryConfigPayload(
            NormalizeP804SourceScope(input.SourceScope),
            NormalizeP804PeriodRule(input.PeriodRule),
            NormalizeP804GroupingHints(input.GroupingHints),
            NormalizeP804DetailHints(input.DetailHints),
            NormalizeP804Targets(input.Targets));
    }

    private static WorkAssignmentBasicSummarySourceScopePayload
        NormalizeP804SourceScope(
            WorkAssignmentBasicSummarySourceScopePayload? input)
    {
        if (input is null)
            throw P804Schema(
                "$.payload.sourceScope",
                "SOURCE_SCOPE_REQUIRED");

        var mode = P804RequiredEnum(
            input.Mode,
            "$.payload.sourceScope.mode",
            "SOURCE_SCOPE_MODE_REQUIRED",
            "SOURCE_SCOPE_MODE_UNSUPPORTED",
            P804SourceModes);
        var isFlow = mode is
            WorkAssignmentBasicSummaryConfigContract.FlowBranch or
            WorkAssignmentBasicSummaryConfigContract.FlowStep or
            WorkAssignmentBasicSummaryConfigContract.FlowEffectivePath or
            WorkAssignmentBasicSummaryConfigContract.FlowFinal;
        if (!isFlow)
        {
            P804RejectPresent(
                input.FlowInstanceId,
                "$.payload.sourceScope.flowInstanceId");
            P804RejectPresent(
                input.FlowStepId,
                "$.payload.sourceScope.flowStepId");
            P804RejectPresent(
                input.FlowBranchId,
                "$.payload.sourceScope.flowBranchId");
            P804RejectPresent(
                input.FlowEffectiveStatus,
                "$.payload.sourceScope.flowEffectiveStatus");
            return new WorkAssignmentBasicSummarySourceScopePayload(
                mode,
                null,
                null,
                null,
                null);
        }

        var flowInstanceId = P804RequiredIdentity(
            input.FlowInstanceId,
            "$.payload.sourceScope.flowInstanceId",
            "FLOW_INSTANCE_ID_REQUIRED");
        var flowStatus = P804RequiredEnum(
            input.FlowEffectiveStatus,
            "$.payload.sourceScope.flowEffectiveStatus",
            "FLOW_EFFECTIVE_STATUS_REQUIRED",
            "FLOW_EFFECTIVE_STATUS_UNSUPPORTED",
            P804FlowStatuses);

        string? flowStepId = null;
        string? flowBranchId = null;
        if (mode == WorkAssignmentBasicSummaryConfigContract.FlowStep)
        {
            flowStepId = P804RequiredIdentity(
                input.FlowStepId,
                "$.payload.sourceScope.flowStepId",
                "FLOW_STEP_ID_REQUIRED");
            P804RejectPresent(
                input.FlowBranchId,
                "$.payload.sourceScope.flowBranchId");
        }
        else if (
            mode == WorkAssignmentBasicSummaryConfigContract.FlowBranch)
        {
            flowBranchId = P804RequiredIdentity(
                input.FlowBranchId,
                "$.payload.sourceScope.flowBranchId",
                "FLOW_BRANCH_ID_REQUIRED");
            P804RejectPresent(
                input.FlowStepId,
                "$.payload.sourceScope.flowStepId");
        }
        else
        {
            P804RejectPresent(
                input.FlowStepId,
                "$.payload.sourceScope.flowStepId");
            P804RejectPresent(
                input.FlowBranchId,
                "$.payload.sourceScope.flowBranchId");
        }

        return new WorkAssignmentBasicSummarySourceScopePayload(
            mode,
            flowInstanceId,
            flowStepId,
            flowBranchId,
            flowStatus);
    }

    private static WorkAssignmentBasicSummaryPeriodRulePayload
        NormalizeP804PeriodRule(
            WorkAssignmentBasicSummaryPeriodRulePayload? input)
    {
        if (input is null)
            throw P804Schema(
                "$.payload.periodRule",
                "PERIOD_RULE_REQUIRED");
        var mode = P804RequiredEnum(
            input.Mode,
            "$.payload.periodRule.mode",
            "PERIOD_MODE_REQUIRED",
            "PERIOD_MODE_UNSUPPORTED",
            P804PeriodModes);

        if (mode == WorkAssignmentBasicSummaryConfigContract.AllPeriods)
        {
            P804RejectPeriodKey(input.PeriodKey, "periodKey");
            P804RejectPeriodKey(input.PeriodKeyFrom, "periodKeyFrom");
            P804RejectPeriodKey(input.PeriodKeyTo, "periodKeyTo");
            return new WorkAssignmentBasicSummaryPeriodRulePayload(
                mode,
                null,
                null,
                null);
        }
        if (mode == WorkAssignmentBasicSummaryConfigContract.SinglePeriod)
        {
            var key = P804RequiredIdentity(
                input.PeriodKey,
                "$.payload.periodRule.periodKey",
                "PERIOD_KEY_REQUIRED");
            P804RejectPeriodKey(input.PeriodKeyFrom, "periodKeyFrom");
            P804RejectPeriodKey(input.PeriodKeyTo, "periodKeyTo");
            return new WorkAssignmentBasicSummaryPeriodRulePayload(
                mode,
                key,
                null,
                null);
        }

        P804RejectPeriodKey(input.PeriodKey, "periodKey");
        var from = P804RequiredIdentity(
            input.PeriodKeyFrom,
            "$.payload.periodRule.periodKeyFrom",
            "PERIOD_KEY_FROM_REQUIRED");
        var to = P804RequiredIdentity(
            input.PeriodKeyTo,
            "$.payload.periodRule.periodKeyTo",
            "PERIOD_KEY_TO_REQUIRED");
        if (string.CompareOrdinal(from, to) > 0)
        {
            throw P804Schema(
                "$.payload.periodRule.periodKeyFrom",
                "PERIOD_RANGE_REVERSED");
        }
        return new WorkAssignmentBasicSummaryPeriodRulePayload(
            mode,
            null,
            from,
            to);
    }

    private static IReadOnlyList<string> NormalizeP804GroupingHints(
        IReadOnlyList<string>? input)
    {
        if (input is null)
            throw P804Schema(
                "$.payload.groupingHints",
                "GROUPING_HINTS_REQUIRED");
        var result = new List<string>(input.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < input.Count; index++)
        {
            var path = $"$.payload.groupingHints[{index}]";
            var value = P804RequiredEnum(
                input[index],
                path,
                "GROUPING_HINT_REQUIRED",
                "GROUPING_HINT_UNSUPPORTED",
                P804GroupingHints);
            if (!seen.Add(value))
                throw P804Schema(path, "DUPLICATE_GROUPING_HINT");
            result.Add(value);
        }
        return result;
    }

    private static WorkAssignmentBasicSummaryDetailHintsPayload
        NormalizeP804DetailHints(
            WorkAssignmentBasicSummaryDetailHintsPayload? input)
    {
        if (input is null)
            throw P804Schema(
                "$.payload.detailHints",
                "DETAIL_HINTS_REQUIRED");
        if (!input.IncludeSourceRows.HasValue)
        {
            throw P804Schema(
                "$.payload.detailHints.includeSourceRows",
                "INCLUDE_SOURCE_ROWS_REQUIRED");
        }
        if (!input.MaxTextChars.HasValue)
        {
            throw P804Schema(
                "$.payload.detailHints.maxTextChars",
                "MAX_TEXT_CHARS_REQUIRED");
        }
        if (input.MaxTextChars.Value <
                WorkAssignmentBasicSummaryConfigContract
                    .MinimumMaxTextChars ||
            input.MaxTextChars.Value >
                WorkAssignmentBasicSummaryConfigContract
                    .MaximumMaxTextChars)
        {
            throw P804Schema(
                "$.payload.detailHints.maxTextChars",
                "MAX_TEXT_CHARS_OUT_OF_RANGE");
        }
        return new WorkAssignmentBasicSummaryDetailHintsPayload(
            input.IncludeSourceRows.Value,
            input.MaxTextChars.Value);
    }

    private static IReadOnlyList<WorkAssignmentBasicSummaryTargetPayload>
        NormalizeP804Targets(
            IReadOnlyList<WorkAssignmentBasicSummaryTargetPayload>? input)
    {
        if (input is null)
            throw P804Schema(
                "$.payload.targets",
                "TARGETS_REQUIRED");
        var result =
            new List<WorkAssignmentBasicSummaryTargetPayload>(
                input.Count);
        var seen =
            new HashSet<(string Kind, string Key)>();
        for (var index = 0; index < input.Count; index++)
        {
            var path = $"$.payload.targets[{index}]";
            var target = input[index] ??
                         throw P804Schema(
                             path,
                             "TARGET_OBJECT_REQUIRED");
            var kind = P804RequiredEnum(
                target.ConceptKind,
                $"{path}.conceptKind",
                "CONCEPT_KIND_REQUIRED",
                "CONCEPT_KIND_UNSUPPORTED",
                P804ConceptKinds);
            var key = P804RequiredIdentity(
                target.ConceptKey,
                $"{path}.conceptKey",
                "CONCEPT_KEY_REQUIRED");
            if (kind ==
                WorkAssignmentBasicSummaryConfigContract.RowLabel)
            {
                key = key.ToLowerInvariant();
            }
            if (!seen.Add((kind, key)))
            {
                throw P804Schema(
                    $"{path}.conceptKey",
                    "DUPLICATE_TARGET_CONCEPT");
            }

            var dataType = P804RequiredEnum(
                target.DataType,
                $"{path}.dataType",
                "DATA_TYPE_REQUIRED",
                "DATA_TYPE_UNSUPPORTED",
                P804OperationsByDataType.Keys.ToHashSet(
                    StringComparer.Ordinal));
            var operation = P804RequiredIdentity(
                    target.Operation,
                    $"{path}.operation",
                    "OPERATION_REQUIRED")
                .ToUpperInvariant();
            if (!P804OperationsByDataType[dataType]
                    .Contains(operation))
            {
                throw P804Schema(
                    $"{path}.operation",
                    "BASIC_SUMMARY_OPERATION_INCOMPATIBLE");
            }
            result.Add(
                new WorkAssignmentBasicSummaryTargetPayload(
                    kind,
                    key,
                    dataType,
                    operation));
        }
        return result;
    }

    private static string P804RequiredEnum(
        string? input,
        string path,
        string missingReason,
        string unsupportedReason,
        IReadOnlySet<string> allowed)
    {
        var value = P804RequiredIdentity(
                input,
                path,
                missingReason)
            .ToUpperInvariant();
        if (!allowed.Contains(value))
            throw P804Schema(path, unsupportedReason);
        return value;
    }

    private static string P804RequiredIdentity(
        string? input,
        string path,
        string missingReason)
    {
        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw P804Schema(path, missingReason);
        if (value.Length > 256 || value.Any(char.IsControl))
            throw P804Schema(path, "IDENTITY_INVALID");
        return value;
    }

    private static void P804RejectPresent(
        string? value,
        string path)
    {
        if (value is not null)
            throw P804Schema(path, "SOURCE_SCOPE_FIELD_NOT_APPLICABLE");
    }

    private static void P804RejectPeriodKey(
        string? value,
        string propertyName)
    {
        if (value is not null)
        {
            throw P804Schema(
                $"$.payload.periodRule.{propertyName}",
                "PERIOD_KEY_NOT_APPLICABLE");
        }
    }

    private static IReadOnlySet<string> P804Set(
        params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

    private static AppException P804Schema(
        string path,
        string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new { path, reason });

    private static string P804CanonicalPayload(
        WorkAssignmentBasicSummaryConfigPayload payload)
        => StatConfigCanonicalJson.Canonicalize(payload);

    private static string P804ComputeConfigHash(
        WorkAssignmentBasicSummaryConfigPayload payload,
        IReadOnlyList<string> dependencyPins)
        => StatConfigCanonicalJson.HashObject(
            new
            {
                payload,
                dependencyPins
            });

    private static WorkAssignmentBasicSummaryConfigPayload
        P804DeserializeStoredPayload(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return NormalizeP804Payload(
                StatConfigCanonicalJson.DeserializeStrict<
                    WorkAssignmentBasicSummaryConfigPayload>(
                    document.RootElement));
        }
        catch (AppException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw P804Schema(
                "$.configJson",
                "STORED_CONFIG_JSON_INVALID");
        }
    }

    private sealed record P804ConfigState(
        WorkAssignmentBasicSummaryConfig? Entity,
        string ConfigId,
        string VersionId,
        string? PreviousVersionId,
        int VersionNo,
        long Revision,
        string Status,
        string ConfigHash,
        IReadOnlyList<string> DependencyPins,
        WorkAssignmentBasicSummaryConfigPayload Payload,
        IReadOnlyList<WorkAssignmentBasicSummaryConfigVersion> Versions,
        bool IsVirtual);
}
