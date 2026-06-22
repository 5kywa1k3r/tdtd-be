using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentSummarySourceScope
{
    public const string DirectChildrenOrSelf = "DIRECT_CHILDREN_OR_SELF";
    public const string DirectChildren = "DIRECT_CHILDREN";
    public const string Self = "SELF";
    public const string FlowBranch = "FLOW_BRANCH";
    public const string FlowStep = "FLOW_STEP";
    public const string FlowEffectivePath = "FLOW_EFFECTIVE_PATH";
    public const string FlowFinal = "FLOW_FINAL";
    public const string AnyFlowEffectiveStatus = "ANY";

    public static NormalizedSummarySourceScope Normalize(
        WorkAssignment scope,
        string? sourceScopeMode,
        string? sourceFlowInstanceId,
        string? sourceFlowStepId,
        string? sourceFlowBranchId,
        string? sourceFlowEffectiveStatus)
    {
        var mode = NormalizeMode(sourceScopeMode);
        if (!IsFlowMode(mode))
        {
            return new NormalizedSummarySourceScope(
                mode,
                FlowInstanceId: null,
                FlowStepId: null,
                FlowBranchId: null,
                FlowEffectiveStatus: null);
        }

        var flowInstanceId = FirstNonBlank(sourceFlowInstanceId, scope.FlowInstanceId);
        if (string.IsNullOrWhiteSpace(flowInstanceId))
            throw InvalidSourceScope("sourceFlowInstanceId", mode, "FLOW_INSTANCE_REQUIRED");

        var flowStepId = mode == FlowStep
            ? FirstNonBlank(sourceFlowStepId, scope.FlowStepId)
            : null;
        if (mode == FlowStep && string.IsNullOrWhiteSpace(flowStepId))
            throw InvalidSourceScope("sourceFlowStepId", mode, "FLOW_STEP_REQUIRED");

        var flowBranchId = mode == FlowBranch
            ? FirstNonBlank(sourceFlowBranchId, scope.FlowBranchId)
            : null;
        if (mode == FlowBranch && string.IsNullOrWhiteSpace(flowBranchId))
            throw InvalidSourceScope("sourceFlowBranchId", mode, "FLOW_BRANCH_REQUIRED");

        return new NormalizedSummarySourceScope(
            mode,
            flowInstanceId,
            flowStepId,
            flowBranchId,
            NormalizeEffectiveStatusForMode(mode, sourceFlowEffectiveStatus));
    }

    public static async Task<List<WorkAssignment>> LoadAssignmentsAsync(
        IMongoCollection<WorkAssignment> assignments,
        WorkAssignment scope,
        string dynamicFormTemplateId,
        IReadOnlyCollection<string> selectedUnitIds,
        NormalizedSummarySourceScope sourceScope,
        CancellationToken ct)
    {
        var sourceAssignmentTypes = ResolveSourceAssignmentTypes(scope.AssignmentType);
        return sourceScope.Mode switch
        {
            DirectChildrenOrSelf => await LoadDirectChildrenOrSelfAsync(
                assignments,
                scope,
                dynamicFormTemplateId,
                sourceAssignmentTypes,
                selectedUnitIds,
                ct),
            DirectChildren => await QuerySourceAssignmentsAsync(
                assignments,
                BuildDirectChildrenFilter(scope, dynamicFormTemplateId, sourceAssignmentTypes, selectedUnitIds),
                ct),
            Self => IsActiveAssignmentForTemplate(scope, dynamicFormTemplateId, sourceAssignmentTypes) &&
                    AssignmentMatchesSelectedUnits(scope, selectedUnitIds)
                ? new List<WorkAssignment> { scope }
                : new List<WorkAssignment>(),
            FlowBranch => await LoadFlowBranchAssignmentsAsync(
                assignments,
                scope,
                dynamicFormTemplateId,
                sourceAssignmentTypes,
                selectedUnitIds,
                sourceScope,
                ct),
            FlowStep => await QuerySourceAssignmentsAsync(
                assignments,
                BuildFlowBaseFilter(scope, dynamicFormTemplateId, sourceAssignmentTypes, selectedUnitIds, sourceScope)
                & Builders<WorkAssignment>.Filter.Eq(x => x.FlowStepId, sourceScope.FlowStepId),
                ct),
            FlowEffectivePath => await QuerySourceAssignmentsAsync(
                assignments,
                BuildFlowBaseFilter(scope, dynamicFormTemplateId, sourceAssignmentTypes, selectedUnitIds, sourceScope),
                ct),
            FlowFinal => await QuerySourceAssignmentsAsync(
                assignments,
                BuildFlowBaseFilter(scope, dynamicFormTemplateId, sourceAssignmentTypes, selectedUnitIds, sourceScope)
                & Builders<WorkAssignment>.Filter.Eq(x => x.IsFlowFinalNode, true),
                ct),
            _ => throw InvalidSourceScope("sourceScopeMode", sourceScope.Mode, "SOURCE_SCOPE_UNSUPPORTED")
        };
    }

    public static bool IsFlowMode(string? mode)
        => string.Equals(mode, FlowBranch, StringComparison.Ordinal) ||
           string.Equals(mode, FlowStep, StringComparison.Ordinal) ||
           string.Equals(mode, FlowEffectivePath, StringComparison.Ordinal) ||
           string.Equals(mode, FlowFinal, StringComparison.Ordinal);

    private static async Task<List<WorkAssignment>> LoadDirectChildrenOrSelfAsync(
        IMongoCollection<WorkAssignment> assignments,
        WorkAssignment scope,
        string dynamicFormTemplateId,
        string[] sourceAssignmentTypes,
        IReadOnlyCollection<string> selectedUnitIds,
        CancellationToken ct)
    {
        var directChildren = await QuerySourceAssignmentsAsync(
            assignments,
            BuildDirectChildrenFilter(scope, dynamicFormTemplateId, sourceAssignmentTypes, selectedUnitIds),
            ct);

        if (directChildren.Count > 0)
            return directChildren;

        return IsActiveAssignmentForTemplate(scope, dynamicFormTemplateId, sourceAssignmentTypes) &&
               AssignmentMatchesSelectedUnits(scope, selectedUnitIds)
            ? new List<WorkAssignment> { scope }
            : new List<WorkAssignment>();
    }

    private static async Task<List<WorkAssignment>> LoadFlowBranchAssignmentsAsync(
        IMongoCollection<WorkAssignment> assignments,
        WorkAssignment scope,
        string dynamicFormTemplateId,
        string[] sourceAssignmentTypes,
        IReadOnlyCollection<string> selectedUnitIds,
        NormalizedSummarySourceScope sourceScope,
        CancellationToken ct)
    {
        var branchRoot = string.Equals(scope.FlowBranchId, sourceScope.FlowBranchId, StringComparison.Ordinal)
            ? scope
            : await assignments
                .Find(x =>
                    x.WorkId == scope.WorkId &&
                    x.FlowInstanceId == sourceScope.FlowInstanceId &&
                    x.FlowBranchId == sourceScope.FlowBranchId &&
                    !x.IsDeleted)
                .SortBy(x => x.Path)
                .FirstOrDefaultAsync(ct);

        if (branchRoot is null || string.IsNullOrWhiteSpace(branchRoot.Path))
            return new List<WorkAssignment>();

        var fb = Builders<WorkAssignment>.Filter;
        var branchPathPrefix = $"{branchRoot.Path.Trim()}/";
        var branchFilter = fb.Or(
            fb.Eq(x => x.Id, branchRoot.Id),
            fb.Regex(x => x.Path, new BsonRegularExpression($"^{Regex.Escape(branchPathPrefix)}")));

        return await QuerySourceAssignmentsAsync(
            assignments,
            BuildFlowBaseFilter(scope, dynamicFormTemplateId, sourceAssignmentTypes, selectedUnitIds, sourceScope) &
            branchFilter,
            ct);
    }

    private static FilterDefinition<WorkAssignment> BuildDirectChildrenFilter(
        WorkAssignment scope,
        string dynamicFormTemplateId,
        string[] sourceAssignmentTypes,
        IReadOnlyCollection<string> selectedUnitIds)
    {
        var fb = Builders<WorkAssignment>.Filter;
        var filter = BuildCommonSourceFilter(scope.WorkId, dynamicFormTemplateId, sourceAssignmentTypes)
                     & fb.Eq(x => x.ParentAssignmentId, scope.Id);

        return AddSelectedUnitFilter(filter, selectedUnitIds);
    }

    private static FilterDefinition<WorkAssignment> BuildFlowBaseFilter(
        WorkAssignment scope,
        string dynamicFormTemplateId,
        string[] sourceAssignmentTypes,
        IReadOnlyCollection<string> selectedUnitIds,
        NormalizedSummarySourceScope sourceScope)
    {
        var fb = Builders<WorkAssignment>.Filter;
        var filter = BuildCommonSourceFilter(scope.WorkId, dynamicFormTemplateId, sourceAssignmentTypes)
                     & fb.Eq(x => x.FlowInstanceId, sourceScope.FlowInstanceId);

        if (!string.IsNullOrWhiteSpace(sourceScope.FlowEffectiveStatus))
            filter &= fb.Eq(x => x.FlowEffectiveStatus, sourceScope.FlowEffectiveStatus);

        return AddSelectedUnitFilter(filter, selectedUnitIds);
    }

    private static FilterDefinition<WorkAssignment> BuildCommonSourceFilter(
        string workId,
        string dynamicFormTemplateId,
        string[] sourceAssignmentTypes)
    {
        var fb = Builders<WorkAssignment>.Filter;
        return fb.Eq(x => x.WorkId, workId)
               & fb.Eq(x => x.DynamicFormTemplateId, dynamicFormTemplateId)
               & fb.In(x => x.AssignmentType, sourceAssignmentTypes)
               & fb.Eq(x => x.IsDeleted, false)
               & fb.Eq(x => x.IsActive, true);
    }

    private static FilterDefinition<WorkAssignment> AddSelectedUnitFilter(
        FilterDefinition<WorkAssignment> filter,
        IReadOnlyCollection<string> selectedUnitIds)
    {
        var selected = selectedUnitIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (selected.Count == 0)
            return filter;

        return filter & Builders<WorkAssignment>.Filter.ElemMatch(
            x => x.Assignees,
            a => a.UnitId != null && selected.Contains(a.UnitId));
    }

    private static async Task<List<WorkAssignment>> QuerySourceAssignmentsAsync(
        IMongoCollection<WorkAssignment> assignments,
        FilterDefinition<WorkAssignment> filter,
        CancellationToken ct)
        => await assignments
            .Find(filter)
            .SortBy(x => x.Path)
            .ThenBy(x => x.Code)
            .ToListAsync(ct);

    private static string NormalizeMode(string? value)
    {
        var mode = string.IsNullOrWhiteSpace(value)
            ? DirectChildrenOrSelf
            : value.Trim().ToUpperInvariant();

        return mode switch
        {
            DirectChildrenOrSelf => DirectChildrenOrSelf,
            DirectChildren => DirectChildren,
            Self => Self,
            FlowBranch => FlowBranch,
            FlowStep => FlowStep,
            FlowEffectivePath => FlowEffectivePath,
            FlowFinal => FlowFinal,
            _ => throw InvalidSourceScope("sourceScopeMode", mode, "SOURCE_SCOPE_MODE_UNSUPPORTED")
        };
    }

    private static string? NormalizeEffectiveStatusForMode(string mode, string? value)
    {
        if (!IsFlowMode(mode))
            return null;

        if (string.IsNullOrWhiteSpace(value))
            return DynamicFlowEffectiveStatuses.Effective;

        var normalized = value.Trim().ToUpperInvariant();
        return normalized switch
        {
            AnyFlowEffectiveStatus => null,
            DynamicFlowEffectiveStatuses.Effective => DynamicFlowEffectiveStatuses.Effective,
            DynamicFlowEffectiveStatuses.Invalidated => DynamicFlowEffectiveStatuses.Invalidated,
            DynamicFlowEffectiveStatuses.Terminated => DynamicFlowEffectiveStatuses.Terminated,
            _ => throw InvalidSourceScope("sourceFlowEffectiveStatus", normalized, "FLOW_EFFECTIVE_STATUS_UNSUPPORTED")
        };
    }

    private static string[] ResolveSourceAssignmentTypes(string? scopeAssignmentType)
        => string.Equals(scopeAssignmentType, WorkAssignmentTypes.PeriodicReport, StringComparison.OrdinalIgnoreCase)
            ? new[] { WorkAssignmentTypes.PeriodicReport }
            : new[] { WorkAssignmentTypes.Once };

    private static bool IsActiveAssignmentForTemplate(
        WorkAssignment assignment,
        string dynamicFormTemplateId,
        string[] supportedAssignmentTypes)
        => assignment.IsActive &&
           !assignment.IsDeleted &&
           supportedAssignmentTypes.Contains(assignment.AssignmentType, StringComparer.OrdinalIgnoreCase) &&
           string.Equals(assignment.DynamicFormTemplateId?.Trim(), dynamicFormTemplateId, StringComparison.Ordinal);

    private static bool AssignmentMatchesSelectedUnits(
        WorkAssignment assignment,
        IReadOnlyCollection<string> selectedUnitIds)
        => selectedUnitIds.Count == 0 ||
           assignment.Assignees.Any(a =>
               !string.IsNullOrWhiteSpace(a.UnitId) &&
               selectedUnitIds.Contains(a.UnitId));

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static AppException InvalidSourceScope(string field, string? value, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                field,
                value,
                reason
            });
}

internal sealed record NormalizedSummarySourceScope(
    string Mode,
    string? FlowInstanceId,
    string? FlowStepId,
    string? FlowBranchId,
    string? FlowEffectiveStatus);
