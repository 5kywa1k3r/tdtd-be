using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.Common;

public interface IWorkStatusOperationLogService
{
    Task WriteAsync(WorkStatusOperationLog log, CancellationToken ct = default);
}

public sealed class WorkStatusOperationLogService : IWorkStatusOperationLogService
{
    private readonly MongoDbContext _ctx;
    private readonly ILogger<WorkStatusOperationLogService> _log;

    public WorkStatusOperationLogService(
        MongoDbContext ctx,
        ILogger<WorkStatusOperationLogService> log)
    {
        _ctx = ctx;
        _log = log;
    }

    public async Task WriteAsync(WorkStatusOperationLog log, CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            log.Id = string.IsNullOrWhiteSpace(log.Id) ? ObjectId.GenerateNewId().ToString() : log.Id;
            log.CreatedAtUtc = log.CreatedAtUtc == default ? now : log.CreatedAtUtc;
            log.UpdatedAtUtc = now;
            log.IsDeleted = false;

            await _ctx.WorkStatusOperationLogs.InsertOneAsync(log, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to persist work status operation log. operation={operation} scope={scope} result={result} workId={workId} assignmentId={assignmentId} periodId={periodId} reportId={reportId}",
                log.Operation,
                log.Scope,
                log.Result,
                log.WorkId,
                log.WorkAssignmentId,
                log.WorkReportPeriodId,
                log.WorkAssignmentReportId);
        }
    }
}
