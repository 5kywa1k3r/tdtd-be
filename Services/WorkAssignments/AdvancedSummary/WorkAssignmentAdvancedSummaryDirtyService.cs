using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public sealed class WorkAssignmentAdvancedSummaryDirtyService : IWorkAssignmentAdvancedSummaryDirtyService
{
    private const string OperationName = "ADVANCED_SUMMARY_MARK_DIRTY";
    private const string ScopeName = "advanced-summary-hierarchy";

    private readonly MongoDbContext _ctx;
    private readonly IWorkStatusOperationLogService _operationLogs;

    public WorkAssignmentAdvancedSummaryDirtyService(
        MongoDbContext ctx,
        IWorkStatusOperationLogService operationLogs)
    {
        _ctx = ctx;
        _operationLogs = operationLogs;
    }

    public async Task MarkReportStatusMutationDirtyAsync(
        WorkAssignmentReport report,
        string operation,
        string fromStatus,
        string toStatus,
        string actorUserId,
        CancellationToken ct)
    {
        if (!ShouldDirtyForStatusMutation(report, fromStatus, toStatus))
            return;

        await MarkReportImpactDirtyAsync(
            report,
            $"REPORT_STATUS_MUTATION:{operation}:{fromStatus}->{toStatus}",
            fromStatus,
            toStatus,
            actorUserId,
            ct);
    }

    public async Task MarkApprovedReportPayloadDirtyAsync(
        WorkAssignmentReport report,
        string operation,
        string actorUserId,
        CancellationToken ct)
    {
        if (report.Status != WorkAssignmentReportStatus.Approved)
            return;

        await MarkReportImpactDirtyAsync(
            report,
            $"REPORT_PAYLOAD_MUTATION:{operation}",
            report.Status.ToString(),
            report.Status.ToString(),
            actorUserId,
            ct);
    }

    private async Task MarkReportImpactDirtyAsync(
        WorkAssignmentReport report,
        string reason,
        string fromStatus,
        string toStatus,
        string actorUserId,
        CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var dayKey = AdvancedSummaryReportSourceDayResolver.Resolve(report);
        var monthKey = AdvancedSummaryHierarchyKeyHelper.ToMonthKey(dayKey);
        var yearKey = AdvancedSummaryHierarchyKeyHelper.ToYearKeyFromDay(dayKey);
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        var scopeAssignmentIds = BuildCandidateScopeAssignmentIds(report.WorkAssignmentId, assignment?.ParentAssignmentId);
        var configs = await LoadAffectedLockedConfigsAsync(report, assignment, scopeAssignmentIds, ct);
        var dirtyDayCount = 0L;
        var dirtyMonthCount = 0L;
        var dirtyYearCount = 0L;

        foreach (var config in configs)
        {
            dirtyDayCount += await MarkDayNodeDirtyAsync(config, dayKey, reason, actorUserId, ct);
            dirtyMonthCount += await MarkMonthNodeDirtyAsync(config, monthKey, reason, actorUserId, ct);
            dirtyYearCount += await MarkYearNodeDirtyAsync(config, yearKey, reason, actorUserId, ct);
        }

        var completedAtUtc = DateTime.UtcNow;
        await _operationLogs.WriteAsync(new WorkStatusOperationLog
        {
            Operation = OperationName,
            Scope = ScopeName,
            Result = "SUCCESS",
            WorkId = report.WorkId,
            WorkAssignmentId = report.WorkAssignmentId,
            WorkReportPeriodId = report.WorkReportPeriodId,
            WorkAssignmentReportId = report.Id,
            ActorUserId = actorUserId,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Summary = string.Join(
                ";",
                $"reason={reason}",
                $"dayKey={dayKey}",
                $"monthKey={monthKey}",
                $"yearKey={yearKey}",
                $"configCount={configs.Count}",
                $"dirtyDayCount={dirtyDayCount}",
                $"dirtyMonthCount={dirtyMonthCount}",
                $"dirtyYearCount={dirtyYearCount}"),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds
        }, ct);
    }

    private async Task<List<WorkAssignmentAdvancedSummaryConfig>> LoadAffectedLockedConfigsAsync(
        WorkAssignmentReport report,
        WorkAssignment? assignment,
        IReadOnlyCollection<string> scopeAssignmentIds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(report.DynamicFormTemplateId))
            return new List<WorkAssignmentAdvancedSummaryConfig>();

        var fb = Builders<WorkAssignmentAdvancedSummaryConfig>.Filter;
        var sourceFilters = new List<FilterDefinition<WorkAssignmentAdvancedSummaryConfig>>();
        if (scopeAssignmentIds.Count > 0)
            sourceFilters.Add(fb.In(x => x.AssignmentId, scopeAssignmentIds));
        if (!string.IsNullOrWhiteSpace(assignment?.FlowInstanceId))
            sourceFilters.Add(fb.Eq(x => x.SourceFlowInstanceId, assignment.FlowInstanceId));

        if (sourceFilters.Count == 0)
            return new List<WorkAssignmentAdvancedSummaryConfig>();

        var filter = fb.Eq(x => x.WorkId, report.WorkId)
                     & fb.Eq(x => x.DynamicFormTemplateId, report.DynamicFormTemplateId)
                     & fb.Eq(x => x.Status, WorkAssignmentAdvancedSummaryConfigStatuses.Locked)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Or(sourceFilters);

        var configs = await _ctx.WorkAssignmentAdvancedSummaryConfigs
            .Find(filter)
            .ToListAsync(ct);

        if (assignment is null)
            return configs;

        var configScopeIds = configs
            .Select(x => x.AssignmentId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var configScopes = configScopeIds.Count == 0
            ? new Dictionary<string, WorkAssignment>(StringComparer.Ordinal)
            : (await _ctx.WorkAssignments
                .Find(x => configScopeIds.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync(ct))
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        return configs
            .Where(config =>
                scopeAssignmentIds.Contains(config.AssignmentId) ||
                configScopes.TryGetValue(config.AssignmentId, out var scope) &&
                ConfigSourceMayIncludeAssignment(config, scope, assignment))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();
    }

    private async Task<long> MarkDayNodeDirtyAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string dayKey,
        string reason,
        string actorUserId,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryDayNode>.Filter;
        var filter = BuildNodeScopeFilter(fb, config)
                     & fb.Eq(x => x.DayKey, dayKey);
        return await MarkNodeDirtyAsync(_ctx.WorkAssignmentAdvancedSummaryDayNodes, filter, reason, actorUserId, ct);
    }

    private async Task<long> MarkMonthNodeDirtyAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string monthKey,
        string reason,
        string actorUserId,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryMonthNode>.Filter;
        var filter = BuildNodeScopeFilter(fb, config)
                     & fb.Eq(x => x.MonthKey, monthKey);
        return await MarkNodeDirtyAsync(_ctx.WorkAssignmentAdvancedSummaryMonthNodes, filter, reason, actorUserId, ct);
    }

    private async Task<long> MarkYearNodeDirtyAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        string yearKey,
        string reason,
        string actorUserId,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentAdvancedSummaryYearNode>.Filter;
        var filter = BuildNodeScopeFilter(fb, config)
                     & fb.Eq(x => x.YearKey, yearKey);
        return await MarkNodeDirtyAsync(_ctx.WorkAssignmentAdvancedSummaryYearNodes, filter, reason, actorUserId, ct);
    }

    private static FilterDefinition<T> BuildNodeScopeFilter<T>(
        FilterDefinitionBuilder<T> fb,
        WorkAssignmentAdvancedSummaryConfig config)
        where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
        => fb.Eq(x => x.WorkId, config.WorkId)
           & fb.Eq(x => x.AssignmentId, config.AssignmentId)
           & fb.Eq(x => x.DynamicFormTemplateId, config.DynamicFormTemplateId)
           & fb.Eq(x => x.SectionId, config.SectionId)
           & fb.Eq(x => x.ConfigId, config.Id)
           & fb.Eq(x => x.ConfigVersionNo, config.VersionNo)
           & fb.Eq(x => x.ConfigHash, config.ConfigHash)
           & fb.Eq(x => x.IsDeleted, false);

    private static async Task<long> MarkNodeDirtyAsync<T>(
        IMongoCollection<T> collection,
        FilterDefinition<T> filter,
        string reason,
        string actorUserId,
        CancellationToken ct)
        where T : WorkAssignmentAdvancedSummaryHierarchyNodeBase
    {
        var now = DateTime.UtcNow;
        var result = await collection.UpdateManyAsync(
            filter,
            Builders<T>.Update
                .Set(x => x.Status, WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Dirty)
                .Set(x => x.IsDirty, true)
                .Set(x => x.DirtyReason, TruncateReason(reason))
                .Set(x => x.BuildError, (string?)null)
                .Set(x => x.BuildJobId, (string?)null)
                .Set(x => x.BuildCorrelationId, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        return result.ModifiedCount;
    }

    private static List<string> BuildCandidateScopeAssignmentIds(string reportAssignmentId, string? parentAssignmentId)
    {
        var output = new List<string>();
        if (!string.IsNullOrWhiteSpace(reportAssignmentId))
            output.Add(reportAssignmentId.Trim());
        if (!string.IsNullOrWhiteSpace(parentAssignmentId) &&
            !output.Contains(parentAssignmentId.Trim(), StringComparer.Ordinal))
        {
            output.Add(parentAssignmentId.Trim());
        }

        return output;
    }

    private static bool ConfigSourceMayIncludeAssignment(
        WorkAssignmentAdvancedSummaryConfig config,
        WorkAssignment scope,
        WorkAssignment assignment)
    {
        var sourceScope = WorkAssignmentSummarySourceScope.Normalize(
            scope,
            config.SourceScopeMode,
            config.SourceFlowInstanceId,
            config.SourceFlowStepId,
            config.SourceFlowBranchId,
            config.SourceFlowEffectiveStatus);

        if (sourceScope.Mode == WorkAssignmentSummarySourceScope.DirectChildrenOrSelf ||
            sourceScope.Mode == WorkAssignmentSummarySourceScope.DirectChildren)
        {
            return string.Equals(assignment.ParentAssignmentId, scope.Id, StringComparison.Ordinal);
        }

        if (sourceScope.Mode == WorkAssignmentSummarySourceScope.Self)
            return string.Equals(assignment.Id, scope.Id, StringComparison.Ordinal);

        if (!WorkAssignmentSummarySourceScope.IsFlowMode(sourceScope.Mode) ||
            string.IsNullOrWhiteSpace(sourceScope.FlowInstanceId) ||
            !string.Equals(sourceScope.FlowInstanceId, assignment.FlowInstanceId, StringComparison.Ordinal))
        {
            return false;
        }

        return sourceScope.Mode switch
        {
            WorkAssignmentSummarySourceScope.FlowBranch =>
                string.Equals(sourceScope.FlowBranchId, assignment.FlowBranchId, StringComparison.Ordinal) ||
                string.Equals(scope.Id, assignment.Id, StringComparison.Ordinal) ||
                IsSameOrDescendantAssignment(scope, assignment),
            WorkAssignmentSummarySourceScope.FlowStep =>
                string.Equals(sourceScope.FlowStepId, assignment.FlowStepId, StringComparison.Ordinal),
            WorkAssignmentSummarySourceScope.FlowEffectivePath => true,
            WorkAssignmentSummarySourceScope.FlowFinal => assignment.IsFlowFinalNode == true,
            _ => false
        };
    }

    private static bool IsSameOrDescendantAssignment(WorkAssignment scope, WorkAssignment assignment)
    {
        if (string.Equals(scope.Id, assignment.Id, StringComparison.Ordinal))
            return true;

        var scopePath = scope.Path?.Trim();
        var assignmentPath = assignment.Path?.Trim();
        return !string.IsNullOrWhiteSpace(scopePath) &&
               !string.IsNullOrWhiteSpace(assignmentPath) &&
               assignmentPath.StartsWith($"{scopePath}/", StringComparison.Ordinal);
    }

    private static bool ShouldDirtyForStatusMutation(
        WorkAssignmentReport report,
        string? fromStatus,
        string? toStatus)
    {
        if (IsApprovedStatus(fromStatus) || IsApprovedStatus(toStatus))
            return true;

        if (report.Status != WorkAssignmentReportStatus.Approved)
            return false;

        return IsActiveMutationStatus(fromStatus) || IsActiveMutationStatus(toStatus);
    }

    private static bool IsApprovedStatus(string? value)
        => string.Equals(value, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveMutationStatus(string? value)
        => string.Equals(value, "ACTIVE", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "INACTIVE", StringComparison.OrdinalIgnoreCase);

    private static string TruncateReason(string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "REPORT_MUTATION" : reason.Trim();
        return reason.Length <= 240 ? reason : reason[..240];
    }
}
