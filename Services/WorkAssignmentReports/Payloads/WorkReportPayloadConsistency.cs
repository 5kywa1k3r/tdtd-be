using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Payloads;

public static class WorkReportPayloadConsistency
{
    public static bool IsReadyForStatisticProjection(WorkAssignmentReport? report)
        => report is not null
           && report.PayloadRevision > 0
           && !string.IsNullOrWhiteSpace(report.PayloadHash)
           && string.Equals(report.PayloadStatus, WorkReportPayloadStatus.Ready, StringComparison.Ordinal);

    public static void EnsureReadyForStatisticProjection(WorkAssignmentReport report)
    {
        if (!IsReadyForStatisticProjection(report))
            throw PayloadNotReady(report);
    }

    public static void EnsureSnapshotFreshForStatisticProjection(
        WorkAssignmentReport report,
        WorkReportPayloadSnapshot payload)
    {
        EnsureReadyForStatisticProjection(report);

        if (!payload.IsExternalPayload ||
            !payload.PayloadHashVerified ||
            payload.PayloadRevision != report.PayloadRevision ||
            !string.Equals(payload.PayloadHash, report.PayloadHash, StringComparison.Ordinal) ||
            !string.Equals(payload.PayloadStatus, WorkReportPayloadStatus.Ready, StringComparison.Ordinal))
        {
            throw PayloadNotReady(report, payload);
        }
    }

    private static AppException PayloadNotReady(
        WorkAssignmentReport report,
        WorkReportPayloadSnapshot? payload = null)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_NOT_READY,
            new
            {
                reportId = report.Id,
                workAssignmentId = report.WorkAssignmentId,
                workReportPeriodId = report.WorkReportPeriodId,
                payloadRevision = report.PayloadRevision,
                payloadHash = report.PayloadHash,
                payloadStatus = report.PayloadStatus,
                loadedPayloadRevision = payload?.PayloadRevision,
                loadedPayloadHash = payload?.PayloadHash,
                loadedPayloadStatus = payload?.PayloadStatus,
                loadedPayloadIsExternal = payload?.IsExternalPayload,
                loadedPayloadHashVerified = payload?.PayloadHashVerified
            });
}
