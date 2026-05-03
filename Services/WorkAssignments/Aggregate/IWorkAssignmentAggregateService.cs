using tdtd_be.DTOs.WorkAssignments.Aggregate;

namespace tdtd_be.Services.WorkAssignments.Aggregate;

public interface IWorkAssignmentAggregateService
{
    Task<AggregateReportResponse> GetAggregatedViewAsync(
        AggregateReportRequest req,
        CancellationToken ct);
}