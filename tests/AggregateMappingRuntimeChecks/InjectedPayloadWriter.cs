using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

// Fault injection after the real native write, inside the caller's Mongo transaction.
internal sealed class InjectedPayloadWriter(MongoDbContext db) : IWorkReportPayloadWriter
{
    internal bool FailAfterWrite;
    public async Task<WorkReportPayloadWriteResult> SaveReportPayloadAsync(WorkAssignmentReport report, string values1DJson,
        string? fieldValuesJson, string? tableValuesJson, string? summarySourceJson, string? actorUserId, DateTime now,
        CancellationToken ct = default, IClientSessionHandle? session = null)
    {
        var result = await new WorkReportPayloadService(db).SaveReportPayloadAsync(report, values1DJson, fieldValuesJson,
            tableValuesJson, summarySourceJson, actorUserId, now, ct, session);
        if (FailAfterWrite) throw new InvalidOperationException("P05_INJECTED_AFTER_NATIVE_WRITE");
        return result;
    }
}
