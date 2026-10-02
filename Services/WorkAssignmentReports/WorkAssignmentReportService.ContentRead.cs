using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;

namespace tdtd_be.Services.WorkAssignmentReports;

public sealed partial class WorkAssignmentReportService
{
    // Read-only policy entry; no report materialization or reconciliation.
    public async Task<WorkAssignmentReport> AuthorizeContentReadAsync(string reportId, string actorUserId, CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var report = await _ctx.WorkAssignmentReports.Find(r => r.Id == reportId && !r.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new AggregatePreviewException("AGG_CONTEXT_UNAVAILABLE");
        await EnsureReportAccessAsync(report, actorUserId, ct);
        return report;
    }
}
