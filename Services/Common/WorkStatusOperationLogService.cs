using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Operations;
using tdtd_be.Models;
using tdtd_be.Common.Time;
using System.Text.RegularExpressions;

namespace tdtd_be.Services.Common;

public interface IWorkStatusOperationLogService
{
    Task WriteAsync(WorkStatusOperationLog log, CancellationToken ct = default);
    Task<PagedResult<WorkStatusOperationLogRow>> SearchAsync(
        WorkStatusOperationLogSearchRequest request,
        CancellationToken ct = default);
    Task<WorkStatusOperationLogRow?> GetByIdAsync(
        string id,
        bool includeStackTrace,
        CancellationToken ct = default);
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

    public async Task<PagedResult<WorkStatusOperationLogRow>> SearchAsync(
        WorkStatusOperationLogSearchRequest request,
        CancellationToken ct = default)
    {
        request ??= new WorkStatusOperationLogSearchRequest();

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var filter = BuildFilter(request);
        var sort = Builders<WorkStatusOperationLog>.Sort
            .Descending(x => x.CompletedAtUtc)
            .Descending(x => x.CreatedAtUtc);

        var total = await _ctx.WorkStatusOperationLogs.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.WorkStatusOperationLogs
            .Find(filter)
            .Sort(sort)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<WorkStatusOperationLogRow>(
            rows.Select(x => ToRow(x, request.IncludeStackTrace)).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<WorkStatusOperationLogRow?> GetByIdAsync(
        string id,
        bool includeStackTrace,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var row = await _ctx.WorkStatusOperationLogs
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        return row is null ? null : ToRow(row, includeStackTrace);
    }

    private static FilterDefinition<WorkStatusOperationLog> BuildFilter(
        WorkStatusOperationLogSearchRequest request)
    {
        var fb = Builders<WorkStatusOperationLog>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);

        filter &= EqIfNotBlank(fb, x => x.Operation, request.Operation);
        filter &= EqIfNotBlank(fb, x => x.Scope, request.Scope);
        filter &= EqIfNotBlank(fb, x => x.Result, request.Result);
        filter &= EqIfNotBlank(fb, x => x.WorkId, request.WorkId);
        filter &= EqIfNotBlank(fb, x => x.WorkAssignmentId, request.WorkAssignmentId);
        filter &= EqIfNotBlank(fb, x => x.WorkReportPeriodId, request.WorkReportPeriodId);
        filter &= EqIfNotBlank(fb, x => x.WorkAssignmentReportId, request.WorkAssignmentReportId);
        filter &= EqIfNotBlank(fb, x => x.ActorUserId, request.ActorUserId);

        var fromUtc = AppTimeRangeHelper.ToUtc(request.FromUtc);
        if (fromUtc.HasValue)
            filter &= fb.Gte(x => x.CompletedAtUtc, fromUtc.Value);

        var toUtc = AppTimeRangeHelper.ToUtc(request.ToUtc);
        if (toUtc.HasValue)
            filter &= fb.Lte(x => x.CompletedAtUtc, toUtc.Value);

        var query = NullIfWhiteSpace(request.Query);
        if (query is not null)
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Or(
                fb.Regex(x => x.Operation, regex),
                fb.Regex(x => x.Scope, regex),
                fb.Regex(x => x.Result, regex),
                fb.Regex(x => x.Summary, regex),
                fb.Regex(x => x.ErrorType, regex),
                fb.Regex(x => x.ErrorMessage, regex));
        }

        return filter;
    }

    private static FilterDefinition<WorkStatusOperationLog> EqIfNotBlank(
        FilterDefinitionBuilder<WorkStatusOperationLog> fb,
        System.Linq.Expressions.Expression<Func<WorkStatusOperationLog, string?>> field,
        string? value)
    {
        var normalized = NullIfWhiteSpace(value);
        return normalized is null ? FilterDefinition<WorkStatusOperationLog>.Empty : fb.Eq(field, normalized);
    }

    private static WorkStatusOperationLogRow ToRow(
        WorkStatusOperationLog log,
        bool includeStackTrace)
        => new()
        {
            Id = log.Id,
            Operation = log.Operation,
            Scope = log.Scope,
            Result = log.Result,
            WorkId = log.WorkId,
            WorkAssignmentId = log.WorkAssignmentId,
            WorkReportPeriodId = log.WorkReportPeriodId,
            WorkAssignmentReportId = log.WorkAssignmentReportId,
            ActorUserId = log.ActorUserId,
            FromStatus = log.FromStatus,
            ToStatus = log.ToStatus,
            PeriodFromStatus = log.PeriodFromStatus,
            PeriodToStatus = log.PeriodToStatus,
            AssignmentFromStatus = log.AssignmentFromStatus,
            AssignmentToStatus = log.AssignmentToStatus,
            WorkFromStatus = log.WorkFromStatus,
            WorkToStatus = log.WorkToStatus,
            Summary = log.Summary,
            ErrorType = log.ErrorType,
            ErrorMessage = log.ErrorMessage,
            ErrorStackTrace = includeStackTrace ? log.ErrorStackTrace : null,
            StartedAtUtc = log.StartedAtUtc,
            CompletedAtUtc = log.CompletedAtUtc,
            DurationMs = log.DurationMs,
            CreatedAtUtc = log.CreatedAtUtc
        };

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
