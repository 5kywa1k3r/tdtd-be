using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed partial class WorkAssignmentReportService
{
    public async Task<WorkAssignmentReport> AuthorizeEvidenceAsync(string reportId, string fieldId, string actorUserId, bool write, CancellationToken ct)
    {
        var report = await AuthorizeContentReadAsync(reportId, actorUserId, ct);
        var access = await EnsureReportAccessAsync(report, actorUserId, ct);
        EnsureReportIsActive(report);
        await HydrateReportPayloadAsync(report, ct);
        if (write)
        {
            if (!access.isAssignee || report.AssigneeUserId != actorUserId || report.Status != WorkAssignmentReportStatus.Draft)
                throw AppExceptionFactory.Forbidden(AppErrorCode.WORK_ASSIGNMENT_REPORT_SAVE_FORBIDDEN);
            await EnsureReportMutationScopeOpenAsync(access.assignment, actorUserId, ct);
            await EnsureDynamicFlowMappingProvenanceIntegrityAsync(report, ct);
            await ValidateDynamicFlowMappingLifecycleBoundaryAsync(report, ct);
            await _dynamicFlowTransactions.ExecuteAsync((session, token) =>
                tdtd_be.Services.AggregateMapping.Persistence.AggregateHostIntegration.GuardReportAsync(_ctx, session, report, token), ct);
        }
        // Flow field-level permissions are not enabled for this release.
        if (tdtd_be.Services.DynamicFlows.DynamicFlowBranchVisibility.IsFlowAssignment(access.assignment))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN);
        var form = await _ctx.DynamicFormTemplates.Find(f => f.Id == report.DynamicFormTemplateId && !f.IsDeleted).FirstOrDefaultAsync(ct);
        if (form is null || !ReadRuntimeFields(form.FieldsJson).Any(f => f.Id == fieldId && f.FieldType == "evidence"))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID);
        return report;
    }

    private async Task ValidateEvidenceReferencesAsync(WorkAssignmentReport report, string? canonicalJson, IEnumerable<string> fieldIds, CancellationToken ct)
    {
        using var parsed = JsonDocument.Parse(canonicalJson ?? "{}");
        var values = parsed.RootElement.TryGetProperty("values", out var nested) ? nested : parsed.RootElement;
        foreach (var fieldId in fieldIds)
        {
            if (!values.TryGetProperty(fieldId, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            var ids = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
            foreach (var id in ids)
            {
                if (!MongoDB.Bson.ObjectId.TryParse(id, out _))
                    throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID);
            }
            if (ids.Length == 0) continue;
            var count = await _ctx.Files.CountDocumentsAsync(f => ids.Contains(f.Id) && !f.IsDeleted
                && f.SourceType == "REPORT_EVIDENCE" && f.SourceId == report.Id + ":" + fieldId
                && f.Size > 0 && f.Size <= 5 * 1024 * 1024, cancellationToken: ct);
            if (count != ids.Length)
                throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_VALUES_INVALID,
                    new { fieldId, reason = "EVIDENCE_FILE_INVALID" });
        }
    }
}
