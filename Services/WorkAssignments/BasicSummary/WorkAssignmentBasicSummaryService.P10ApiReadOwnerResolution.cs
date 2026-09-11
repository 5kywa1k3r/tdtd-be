using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public sealed partial class WorkAssignmentBasicSummaryService
{
    private async Task<(
        List<string> AssignmentIds,
        List<string> ReportIds,
        List<WorkAssignment> Assignments,
        List<WorkAssignmentReport> Reports)> P10ApiResolveExactSourcesAsync(
            WorkAssignmentBasicSummarySnapshot snapshot,
            string workId,
            string scopeAssignmentId,
            string dynamicFormTemplateId,
            CancellationToken ct)
    {
        var frozenAssignmentIds = ExactPinnedObjectIds(
            snapshot.SourceAssignmentIds,
            "API_BASIC_SOURCE_ASSIGNMENT_IDS");
        var frozenReportIds = ExactPinnedObjectIds(
            snapshot.SourceReportIds,
            "API_BASIC_SOURCE_REPORT_IDS");

        try
        {
            P10ApiRequireValidSnapshotRequestJson(snapshot.RequestJson);
            var request = BuildBasicSummaryRequestFromSnapshotJson(
                snapshot.RequestJson);
            var normalized = await NormalizeRequestAsync(request, ct)
                .ConfigureAwait(false);
            if (!string.Equals(
                    normalized.ScopeAssignmentId,
                    scopeAssignmentId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    normalized.DynamicFormTemplateId,
                    dynamicFormTemplateId,
                    StringComparison.Ordinal))
            {
                throw BasicApiSnapshotStale(
                    "API_BASIC_REQUEST_TARGET_DRIFT");
            }

            var scope = await LoadScopeAssignmentAsync(scopeAssignmentId, ct)
                .ConfigureAwait(false);
            if (!scope.IsActive ||
                !string.Equals(scope.WorkId, workId, StringComparison.Ordinal) ||
                !string.Equals(
                    scope.DynamicFormTemplateId,
                    dynamicFormTemplateId,
                    StringComparison.Ordinal))
            {
                throw BasicApiSnapshotStale("API_BASIC_SCOPE_DRIFT");
            }

            normalized = AttachSourceScope(scope, normalized);
            var sourceScope = normalized.SourceScope
                ?? throw BasicApiSnapshotStale(
                    "API_BASIC_SOURCE_SCOPE_INVALID");
            if (!string.Equals(
                    sourceScope.Mode,
                    snapshot.SourceScopeMode,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sourceScope.FlowInstanceId,
                    snapshot.SourceFlowInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sourceScope.FlowStepId,
                    snapshot.SourceFlowStepId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sourceScope.FlowBranchId,
                    snapshot.SourceFlowBranchId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sourceScope.FlowEffectiveStatus,
                    snapshot.SourceFlowEffectiveStatus,
                    StringComparison.Ordinal))
            {
                throw BasicApiSnapshotStale(
                    "API_BASIC_SOURCE_SCOPE_DRIFT");
            }

            var assignments = await LoadSourceAssignmentsAsync(
                    scope,
                    dynamicFormTemplateId,
                    normalized,
                    ct)
                .ConfigureAwait(false);
            assignments = assignments
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            var reports = await LoadSourceReportsAsync(
                    assignments.Select(item => item.Id).ToList(),
                    dynamicFormTemplateId,
                    normalized,
                    ct)
                .ConfigureAwait(false);
            reports = reports
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToList();

            P10ApiRequireExactSourceMembership(
                frozenAssignmentIds,
                frozenReportIds,
                assignments.Select(item => item.Id),
                reports.Select(item => item.Id));
            return (
                frozenAssignmentIds,
                frozenReportIds,
                assignments,
                reports);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (StatisticReconciliationActualApiEndpointException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is AppException or JsonException or
                InvalidOperationException or ArgumentException)
        {
            throw BasicApiSnapshotStale(
                "API_BASIC_REQUEST_JSON_INVALID");
        }
    }
}
