using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed partial class WorkReportStatisticDiffService
{
    internal const string P806PutCommandKind = "UPSERT_DIFF_CONFIG";
    internal const string P806LockCommandKind = "LOCK_DIFF_CONFIG";
    internal const string P806NextDraftCommandKind = "CREATE_DIFF_DRAFT";

    private static readonly IReadOnlySet<string> P806ConceptKinds =
        P806Set(
            WorkReportStatisticDiffConfigContract.Field,
            WorkReportStatisticDiffConfigContract.TableMetric,
            WorkReportStatisticDiffConfigContract.RowLabel);

    private static readonly IReadOnlySet<string> P806DataTypes =
        P806Set(
            WorkReportStatisticDiffConfigContract.Number,
            WorkReportStatisticDiffConfigContract.Date,
            WorkReportStatisticDiffConfigContract.Boolean,
            WorkReportStatisticDiffConfigContract.Choice,
            WorkReportStatisticDiffConfigContract.Text);

    private static readonly IReadOnlySet<string> P806PeriodModes =
        P806Set(
            WorkReportStatisticDiffConfigContract.Exact,
            WorkReportStatisticDiffConfigContract.Range);

    private static readonly IReadOnlySet<string> P806Directions =
        P806Set(
            WorkReportStatisticDiffConfigContract.LeftToRight,
            WorkReportStatisticDiffConfigContract.RightToLeft);

    private static readonly IReadOnlySet<string> P806MissingPolicies =
        P806Set(
            WorkReportStatisticDiffConfigContract.Reject,
            WorkReportStatisticDiffConfigContract.Include,
            WorkReportStatisticDiffConfigContract.AsZero);

    private static readonly IReadOnlySet<string> P806EmptyPolicies =
        P806Set(
            WorkReportStatisticDiffConfigContract.Reject,
            WorkReportStatisticDiffConfigContract.Include,
            WorkReportStatisticDiffConfigContract.AsMissing);

    private static readonly IReadOnlySet<string> P806SourceScopeModes =
        P806Set(
            WorkReportStatisticDiffConfigContract.DirectChildrenOrSelf,
            WorkReportStatisticDiffConfigContract.DirectChildren,
            WorkReportStatisticDiffConfigContract.Self,
            WorkReportStatisticDiffConfigContract.FlowBranch,
            WorkReportStatisticDiffConfigContract.FlowStep,
            WorkReportStatisticDiffConfigContract.FlowEffectivePath,
            WorkReportStatisticDiffConfigContract.FlowFinal);

    private static readonly IReadOnlySet<string> P806FlowStatuses =
        P806Set(
            WorkReportStatisticDiffConfigContract.Effective,
            WorkReportStatisticDiffConfigContract.Invalidated,
            WorkReportStatisticDiffConfigContract.Terminated,
            WorkReportStatisticDiffConfigContract.Any);

    private static NormalizedStatConfigCommand<
        WorkReportStatisticDiffConfigPayload> NormalizeP806PutCommand(
        JsonElement body)
    {
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkReportStatisticDiffConfigPayload>>(body);
        var payload = P806NormalizePayload(envelope.Payload);
        return StatConfigCanonicalJson.NormalizeCommand(
            new StatConfigMutationEnvelope<
                WorkReportStatisticDiffConfigPayload>(
                envelope.CommandId,
                envelope.ExpectedRevision,
                envelope.ExpectedConfigHash,
                payload),
            P806PutCommandKind);
    }

    private static NormalizedStatConfigCommand<
        WorkReportStatisticDiffEmptyCommandPayload>
        NormalizeP806EmptyCommand(
            JsonElement body,
            string commandKind)
    {
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkReportStatisticDiffEmptyCommandPayload>>(body);
        return StatConfigCanonicalJson.NormalizeCommand(
            envelope,
            commandKind);
    }

    private static WorkReportStatisticDiffConfigPayload
        P806NormalizePayload(
            WorkReportStatisticDiffConfigPayload? input)
    {
        if (input is null)
            throw P806Schema("$.payload", "PAYLOAD_REQUIRED");
        var name = P806RequiredIdentity(
            input.Name,
            "$.payload.name",
            "NAME_REQUIRED",
            200);
        var left = P806NormalizeSide(
            input.Left,
            "$.payload.left");
        var right = P806NormalizeSide(
            input.Right,
            "$.payload.right");
        var direction = P806RequiredEnum(
            input.Direction,
            "$.payload.direction",
            "DIRECTION_REQUIRED",
            "DIRECTION_UNSUPPORTED",
            P806Directions);
        var missingPolicy = P806RequiredEnum(
            input.MissingPolicy,
            "$.payload.missingPolicy",
            "MISSING_POLICY_REQUIRED",
            "MISSING_POLICY_UNSUPPORTED",
            P806MissingPolicies);
        var emptyPolicy = P806RequiredEnum(
            input.EmptyPolicy,
            "$.payload.emptyPolicy",
            "EMPTY_POLICY_REQUIRED",
            "EMPTY_POLICY_UNSUPPORTED",
            P806EmptyPolicies);

        if (!string.Equals(
                left.Selector!.ConceptKind,
                right.Selector!.ConceptKind,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                "$.payload.right.selector.conceptKind",
                "DIFF_CONCEPT_KIND_MISMATCH");
        }
        if (!string.Equals(
                left.Selector.ConceptCode,
                right.Selector.ConceptCode,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                "$.payload.right.selector.conceptCode",
                "DIFF_CONCEPT_MISMATCH");
        }
        if (!string.Equals(
                left.Selector.DataType,
                right.Selector.DataType,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                "$.payload.right.selector.dataType",
                "DIFF_DATA_TYPE_MISMATCH");
        }
        if (!string.Equals(
                left.Period!.Mode,
                right.Period!.Mode,
                StringComparison.Ordinal))
        {
            throw P806Schema(
                "$.payload.right.period.mode",
                "DIFF_PERIOD_MODE_MISMATCH");
        }
        if (!Equals(left.SourceScope, right.SourceScope))
        {
            throw P806Schema(
                "$.payload.right.sourceScope",
                "DIFF_SOURCE_SCOPE_MISMATCH");
        }
        if (missingPolicy ==
                WorkReportStatisticDiffConfigContract.AsZero &&
            left.Selector.DataType !=
                WorkReportStatisticDiffConfigContract.Number)
        {
            throw P806Schema(
                "$.payload.missingPolicy",
                "DIFF_MISSING_POLICY_INCOMPATIBLE");
        }

        return new WorkReportStatisticDiffConfigPayload(
            name,
            left,
            right,
            direction,
            missingPolicy,
            emptyPolicy);
    }

    private static WorkReportStatisticDiffSidePayload P806NormalizeSide(
        WorkReportStatisticDiffSidePayload? input,
        string path)
    {
        if (input is null)
            throw P806Schema(path, "DIFF_SIDE_REQUIRED");
        return new WorkReportStatisticDiffSidePayload(
            P806NormalizeSelector(input.Selector, $"{path}.selector"),
            P806NormalizePeriod(input.Period, $"{path}.period"),
            P806NormalizeSourceScope(
                input.SourceScope,
                $"{path}.sourceScope"));
    }

    private static WorkReportStatisticDiffSelectorPayload
        P806NormalizeSelector(
            WorkReportStatisticDiffSelectorPayload? input,
            string path)
    {
        if (input is null)
            throw P806Schema(path, "SELECTOR_REQUIRED");
        var kind = P806RequiredEnum(
            input.ConceptKind,
            $"{path}.conceptKind",
            "CONCEPT_KIND_REQUIRED",
            "CONCEPT_KIND_UNSUPPORTED",
            P806ConceptKinds);
        var key = P806RequiredIdentity(
            input.ConceptKey,
            $"{path}.conceptKey",
            "CONCEPT_KEY_REQUIRED");
        if (kind is WorkReportStatisticDiffConfigContract.TableMetric or
            WorkReportStatisticDiffConfigContract.RowLabel)
        {
            var separator = key.IndexOf(':');
            if (separator <= 0 || separator == key.Length - 1 ||
                kind == WorkReportStatisticDiffConfigContract.RowLabel &&
                separator != key.LastIndexOf(':'))
            {
                throw P806Schema(
                    $"{path}.conceptKey",
                    kind == WorkReportStatisticDiffConfigContract.TableMetric
                        ? "TABLE_METRIC_CONCEPT_KEY_INVALID"
                        : "ROW_LABEL_CONCEPT_KEY_INVALID");
            }
            if (kind == WorkReportStatisticDiffConfigContract.RowLabel)
                key = key[..(separator + 1)] + key[(separator + 1)..].ToLowerInvariant();
        }
        var conceptCode = P806RequiredIdentity(
                input.ConceptCode,
                $"{path}.conceptCode",
                "CONCEPT_CODE_REQUIRED",
                64)
            .ToLowerInvariant();
        if (!P806IsCode(conceptCode))
            throw P806Schema($"{path}.conceptCode", "CONCEPT_CODE_INVALID");
        var dataType = P806RequiredEnum(
            input.DataType,
            $"{path}.dataType",
            "DATA_TYPE_REQUIRED",
            "DATA_TYPE_UNSUPPORTED",
            P806DataTypes);
        return new WorkReportStatisticDiffSelectorPayload(
            kind,
            key,
            conceptCode,
            dataType);
    }

    private static WorkReportStatisticDiffPeriodPayload
        P806NormalizePeriod(
            WorkReportStatisticDiffPeriodPayload? input,
            string path)
    {
        if (input is null)
            throw P806Schema(path, "PERIOD_REQUIRED");
        var mode = P806RequiredEnum(
            input.Mode,
            $"{path}.mode",
            "PERIOD_MODE_REQUIRED",
            "PERIOD_MODE_UNSUPPORTED",
            P806PeriodModes);
        if (mode == WorkReportStatisticDiffConfigContract.Exact)
        {
            var key = P806RequiredIdentity(
                input.PeriodKey,
                $"{path}.periodKey",
                "PERIOD_KEY_REQUIRED",
                128);
            P806RejectPresent(input.PeriodKeyFrom, $"{path}.periodKeyFrom");
            P806RejectPresent(input.PeriodKeyTo, $"{path}.periodKeyTo");
            return new WorkReportStatisticDiffPeriodPayload(
                mode, key, null, null);
        }
        P806RejectPresent(input.PeriodKey, $"{path}.periodKey");
        var from = P806RequiredIdentity(
            input.PeriodKeyFrom,
            $"{path}.periodKeyFrom",
            "PERIOD_KEY_FROM_REQUIRED",
            128);
        var to = P806RequiredIdentity(
            input.PeriodKeyTo,
            $"{path}.periodKeyTo",
            "PERIOD_KEY_TO_REQUIRED",
            128);
        if (string.CompareOrdinal(from, to) > 0)
            throw P806Schema($"{path}.periodKeyTo", "PERIOD_RANGE_INVALID");
        return new WorkReportStatisticDiffPeriodPayload(
            mode, null, from, to);
    }

    private static WorkReportStatisticDiffSourceScopePayload
        P806NormalizeSourceScope(
            WorkReportStatisticDiffSourceScopePayload? input,
            string path)
    {
        if (input is null)
            throw P806Schema(path, "SOURCE_SCOPE_REQUIRED");
        var mode = P806RequiredEnum(
            input.Mode,
            $"{path}.mode",
            "SOURCE_SCOPE_MODE_REQUIRED",
            "SOURCE_SCOPE_MODE_UNSUPPORTED",
            P806SourceScopeModes);
        if (mode is WorkReportStatisticDiffConfigContract.DirectChildrenOrSelf or
            WorkReportStatisticDiffConfigContract.DirectChildren or
            WorkReportStatisticDiffConfigContract.Self)
        {
            P806RejectFlowFields(input, path);
            return new WorkReportStatisticDiffSourceScopePayload(
                mode, null, null, null, null);
        }
        var flowInstanceId = P806RequiredObjectId(
            input.FlowInstanceId,
            $"{path}.flowInstanceId",
            "FLOW_INSTANCE_ID_REQUIRED");
        if (mode == WorkReportStatisticDiffConfigContract.FlowBranch)
        {
            var branchId = P806RequiredObjectId(
                input.FlowBranchId,
                $"{path}.flowBranchId",
                "FLOW_BRANCH_ID_REQUIRED");
            P806RejectPresent(input.FlowStepId, $"{path}.flowStepId");
            var status = P806RequiredFlowStatus(input.FlowEffectiveStatus, path);
            return new WorkReportStatisticDiffSourceScopePayload(
                mode, flowInstanceId, null, branchId, status);
        }
        if (mode == WorkReportStatisticDiffConfigContract.FlowStep)
        {
            var stepId = P806RequiredIdentity(
                input.FlowStepId,
                $"{path}.flowStepId",
                "FLOW_STEP_ID_REQUIRED");
            P806RejectPresent(input.FlowBranchId, $"{path}.flowBranchId");
            var status = P806RequiredFlowStatus(input.FlowEffectiveStatus, path);
            return new WorkReportStatisticDiffSourceScopePayload(
                mode, flowInstanceId, stepId, null, status);
        }
        P806RejectPresent(input.FlowStepId, $"{path}.flowStepId");
        P806RejectPresent(input.FlowBranchId, $"{path}.flowBranchId");
        if (mode == WorkReportStatisticDiffConfigContract.FlowFinal)
        {
            P806RejectPresent(
                input.FlowEffectiveStatus,
                $"{path}.flowEffectiveStatus");
            return new WorkReportStatisticDiffSourceScopePayload(
                mode, flowInstanceId, null, null, null);
        }
        return new WorkReportStatisticDiffSourceScopePayload(
            mode,
            flowInstanceId,
            null,
            null,
            P806RequiredFlowStatus(input.FlowEffectiveStatus, path));
    }

    private static string P806RequiredFlowStatus(
        string? input,
        string path)
        => P806RequiredEnum(
            input,
            $"{path}.flowEffectiveStatus",
            "FLOW_EFFECTIVE_STATUS_REQUIRED",
            "FLOW_EFFECTIVE_STATUS_UNSUPPORTED",
            P806FlowStatuses);

    private static void P806RejectFlowFields(
        WorkReportStatisticDiffSourceScopePayload input,
        string path)
    {
        P806RejectPresent(input.FlowInstanceId, $"{path}.flowInstanceId");
        P806RejectPresent(input.FlowStepId, $"{path}.flowStepId");
        P806RejectPresent(input.FlowBranchId, $"{path}.flowBranchId");
        P806RejectPresent(input.FlowEffectiveStatus, $"{path}.flowEffectiveStatus");
    }

    private static void P806RejectPresent(string? input, string path)
    {
        if (input is not null)
            throw P806Schema(path, "FIELD_NOT_ALLOWED_FOR_MODE");
    }

    private static string P806RequiredObjectId(
        string? input,
        string path,
        string missingReason)
    {
        var value = P806RequiredIdentity(input, path, missingReason);
        if (!ObjectId.TryParse(value, out var parsed) ||
            !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
            throw P806Schema(path, "OBJECT_ID_INVALID");
        return value;
    }

    private static string P806RequiredEnum(
        string? input,
        string path,
        string missingReason,
        string unsupportedReason,
        IReadOnlySet<string> allowed)
    {
        var value = P806RequiredIdentity(input, path, missingReason)
            .ToUpperInvariant();
        if (!allowed.Contains(value))
            throw P806Schema(path, unsupportedReason);
        return value;
    }

    private static string P806RequiredIdentity(
        string? input,
        string path,
        string missingReason,
        int maxLength = 256)
    {
        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw P806Schema(path, missingReason);
        if (value.Length > maxLength || value.Any(char.IsControl))
            throw P806Schema(path, "IDENTITY_INVALID");
        return value;
    }

    private static bool P806IsCode(string value)
        => value.Length is > 0 and <= 64 &&
           char.IsAsciiLetterOrDigit(value[0]) &&
           value.All(character =>
               char.IsAsciiLetterOrDigit(character) ||
               character is '_' or '.' or '-');

    private static WorkReportStatisticDiffConfigPayload
        P806VirtualEmptyPayload()
        => new(null, null, null, null, null, null);

    private static IReadOnlySet<string> P806Set(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

    private static AppException P806Schema(string path, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new { path, reason });

    private static AppException P806LegacyBlocked(string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new { reason });
}
