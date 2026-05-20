using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Payloads;

public interface IWorkReportPayloadWriter
{
    Task<WorkReportPayloadWriteResult> SaveReportPayloadAsync(
        WorkAssignmentReport report,
        string values1DJson,
        string? fieldValuesJson,
        string? tableValuesJson,
        string? summarySourceJson,
        string? actorUserId,
        DateTime now,
        CancellationToken ct = default);
}
