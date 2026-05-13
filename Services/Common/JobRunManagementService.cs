using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Operations;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;

namespace tdtd_be.Services.Common;

public interface IJobRunManagementService
{
    Task<PagedResult<MaterializeJobRow>> SearchMaterializeJobsAsync(
        JobRunSearchRequest request,
        CancellationToken ct = default);
    Task<PagedResult<ProjectionRetryJobRow>> SearchProjectionRetryJobsAsync(
        JobRunSearchRequest request,
        CancellationToken ct = default);
    Task<PagedResult<StatisticRebuildJobRow>> SearchStatisticRebuildJobsAsync(
        JobRunSearchRequest request,
        CancellationToken ct = default);
    Task<int> ProcessMaterializeJobsAsync(int maxJobs, int batchSize, CancellationToken ct = default);
    Task ProcessWorkAssignmentQueueScanAsync(CancellationToken ct = default);
    Task ProcessNotificationDueScanAsync(CancellationToken ct = default);
    Task<int> ProcessProjectionRetryJobsAsync(int maxJobs, CancellationToken ct = default);
    Task<int> ProcessUserActionLogRetriesAsync(int maxJobs, CancellationToken ct = default);
    Task<int> ProcessStatisticRebuildJobsAsync(int maxJobs, int batchSize, CancellationToken ct = default);
}

public sealed class JobRunManagementService : IJobRunManagementService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentMaterializeJobService _materializeJobs;
    private readonly IWorkAssignmentQueueJobService _queueScan;
    private readonly INotificationDueScanJobService _notificationDueScan;
    private readonly IDocRoleReadModelProjectionRetryJobService _projectionRetry;
    private readonly IUserActionLogService _userActionLog;
    private readonly IWorkReportStatisticRebuildJobService _statisticRebuildJobs;

    public JobRunManagementService(
        MongoDbContext ctx,
        IWorkAssignmentMaterializeJobService materializeJobs,
        IWorkAssignmentQueueJobService queueScan,
        INotificationDueScanJobService notificationDueScan,
        IDocRoleReadModelProjectionRetryJobService projectionRetry,
        IUserActionLogService userActionLog,
        IWorkReportStatisticRebuildJobService statisticRebuildJobs)
    {
        _ctx = ctx;
        _materializeJobs = materializeJobs;
        _queueScan = queueScan;
        _notificationDueScan = notificationDueScan;
        _projectionRetry = projectionRetry;
        _userActionLog = userActionLog;
        _statisticRebuildJobs = statisticRebuildJobs;
    }

    public async Task<PagedResult<MaterializeJobRow>> SearchMaterializeJobsAsync(
        JobRunSearchRequest request,
        CancellationToken ct = default)
    {
        request ??= new JobRunSearchRequest();

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var fb = Builders<WorkAssignmentMaterializeJobs>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);

        if (!request.IncludeInactive)
            filter &= fb.Eq(x => x.IsActive, true);

        filter &= EqIfNotBlank(fb, x => x.Status, request.Status);
        filter &= EqIfNotBlank(fb, x => x.WorkId, request.WorkId);
        filter &= EqIfNotBlank(fb, x => x.WorkAssignmentId, request.WorkAssignmentId);

        var query = NullIfWhiteSpace(request.Query);
        if (query is not null)
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Or(
                fb.Regex(x => x.Status, regex),
                fb.Regex(x => x.LastError, regex));
        }

        var total = await _ctx.WorkAssignmentMaterializeJobs.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.WorkAssignmentMaterializeJobs
            .Find(filter)
            .Sort(Builders<WorkAssignmentMaterializeJobs>.Sort
                .Ascending(x => x.NextRetryAtUtc)
                .Descending(x => x.UpdatedAtUtc))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<MaterializeJobRow>(
            rows.Select(ToMaterializeRow).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<PagedResult<ProjectionRetryJobRow>> SearchProjectionRetryJobsAsync(
        JobRunSearchRequest request,
        CancellationToken ct = default)
    {
        request ??= new JobRunSearchRequest();

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var fb = Builders<DocRoleReadModelProjectionRetryJob>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);

        if (!request.IncludeInactive)
            filter &= fb.Eq(x => x.IsActive, true);

        filter &= EqIfNotBlank(fb, x => x.Status, request.Status);
        filter &= EqIfNotBlank(fb, x => x.Action, request.Action);
        filter &= EqIfNotBlank(fb, x => x.WorkId, request.WorkId);
        filter &= EqIfNotBlank(fb, x => x.AssignmentId, request.WorkAssignmentId);
        filter &= EqIfNotBlank(fb, x => x.WorkReportPeriodId, request.WorkReportPeriodId);
        filter &= EqIfNotBlank(fb, x => x.UserId, request.UserId);

        var query = NullIfWhiteSpace(request.Query);
        if (query is not null)
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Or(
                fb.Regex(x => x.Action, regex),
                fb.Regex(x => x.Status, regex),
                fb.Regex(x => x.DedupeKey, regex),
                fb.Regex(x => x.Reason, regex),
                fb.Regex(x => x.LastErrorType, regex),
                fb.Regex(x => x.LastError, regex));
        }

        var total = await _ctx.DocRoleReadModelProjectionRetryJobs.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.DocRoleReadModelProjectionRetryJobs
            .Find(filter)
            .Sort(Builders<DocRoleReadModelProjectionRetryJob>.Sort
                .Ascending(x => x.NextRetryAtUtc)
                .Descending(x => x.CreatedAtUtc))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<ProjectionRetryJobRow>(
            rows.Select(ToProjectionRetryRow).ToList(),
            total,
            page,
            pageSize);
    }

    public Task<int> ProcessMaterializeJobsAsync(int maxJobs, int batchSize, CancellationToken ct = default)
        => _materializeJobs.ProcessPendingJobsAsync(
            Math.Clamp(maxJobs, 1, 50),
            Math.Clamp(batchSize, 1, 200),
            ct);

    public Task ProcessWorkAssignmentQueueScanAsync(CancellationToken ct = default)
        => _queueScan.ScanDuePeriodsAsync(ct);

    public Task ProcessNotificationDueScanAsync(CancellationToken ct = default)
        => _notificationDueScan.ScanDueNotificationsAsync(ct);

    public Task<int> ProcessProjectionRetryJobsAsync(int maxJobs, CancellationToken ct = default)
        => _projectionRetry.ProcessPendingJobsAsync(Math.Clamp(maxJobs, 1, 200), ct);

    public Task<int> ProcessUserActionLogRetriesAsync(int maxJobs, CancellationToken ct = default)
        => _userActionLog.ProcessPendingRetriesAsync(Math.Clamp(maxJobs, 1, 200), ct);

    public async Task<PagedResult<StatisticRebuildJobRow>> SearchStatisticRebuildJobsAsync(
        JobRunSearchRequest request,
        CancellationToken ct = default)
    {
        request ??= new JobRunSearchRequest();

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);

        if (!request.IncludeInactive)
            filter &= fb.Eq(x => x.IsActive, true);

        filter &= EqIfNotBlank(fb, x => x.Status, request.Status);
        filter &= EqIfNotBlank(fb, x => x.DynamicFormTemplateId, request.DynamicFormTemplateId);
        filter &= EqIfNotBlank(fb, x => x.RequestedByUserId, request.UserId);

        var query = NullIfWhiteSpace(request.Query);
        if (query is not null)
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Or(
                fb.Regex(x => x.Status, regex),
                fb.Regex(x => x.DedupeKey, regex),
                fb.Regex(x => x.DynamicFormTemplateCode, regex),
                fb.Regex(x => x.DynamicFormTemplateName, regex),
                fb.Regex(x => x.LastErrorType, regex),
                fb.Regex(x => x.LastError, regex));
        }

        var total = await _ctx.WorkReportStatisticRebuildJobs.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.WorkReportStatisticRebuildJobs
            .Find(filter)
            .Sort(Builders<WorkReportStatisticRebuildJob>.Sort
                .Ascending(x => x.NextRetryAtUtc)
                .Descending(x => x.CreatedAtUtc))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<StatisticRebuildJobRow>(
            rows.Select(ToStatisticRebuildJobRow).ToList(),
            total,
            page,
            pageSize);
    }

    public Task<int> ProcessStatisticRebuildJobsAsync(int maxJobs, int batchSize, CancellationToken ct = default)
        => _statisticRebuildJobs.ProcessPendingJobsAsync(
            Math.Clamp(maxJobs, 1, 20),
            Math.Clamp(batchSize, 1, 100),
            ct);

    private static MaterializeJobRow ToMaterializeRow(WorkAssignmentMaterializeJobs x)
        => new()
        {
            Id = x.Id,
            WorkId = x.WorkId,
            WorkAssignmentId = x.WorkAssignmentId,
            Status = x.Status,
            RetryCount = x.RetryCount,
            NextRetryAtUtc = x.NextRetryAtUtc,
            LeaseUntilUtc = x.LeaseUntilUtc,
            LastHeartbeatAtUtc = x.LastHeartbeatAtUtc,
            LastRunAtUtc = x.LastRunAtUtc,
            CompletedAtUtc = x.CompletedAtUtc,
            LastError = x.LastError,
            CursorAssigneeIndex = x.CursorAssigneeIndex,
            CursorDueIndex = x.CursorDueIndex,
            IsActive = x.IsActive,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static ProjectionRetryJobRow ToProjectionRetryRow(DocRoleReadModelProjectionRetryJob x)
        => new()
        {
            Id = x.Id,
            DedupeKey = x.DedupeKey,
            Action = x.Action,
            Status = x.Status,
            WorkId = x.WorkId,
            AssignmentId = x.AssignmentId,
            WorkReportPeriodId = x.WorkReportPeriodId,
            DynamicExcelId = x.DynamicExcelId,
            UserId = x.UserId,
            DocType = x.DocType?.ToString(),
            DocId = x.DocId,
            ByUserId = x.ByUserId,
            Reason = x.Reason,
            RetryCount = x.RetryCount,
            NextRetryAtUtc = x.NextRetryAtUtc,
            LeaseUntilUtc = x.LeaseUntilUtc,
            LastRunAtUtc = x.LastRunAtUtc,
            CompletedAtUtc = x.CompletedAtUtc,
            LastErrorType = x.LastErrorType,
            LastError = x.LastError,
            LastErrorAtUtc = x.LastErrorAtUtc,
            IsActive = x.IsActive,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static StatisticRebuildJobRow ToStatisticRebuildJobRow(WorkReportStatisticRebuildJob x)
        => new()
        {
            Id = x.Id,
            DedupeKey = x.DedupeKey,
            DynamicFormTemplateId = x.DynamicFormTemplateId,
            DynamicFormTemplateCode = x.DynamicFormTemplateCode,
            DynamicFormTemplateName = x.DynamicFormTemplateName,
            Status = x.Status,
            RequestedByUserId = x.RequestedByUserId,
            Priority = x.Priority,
            TotalReportCount = x.TotalReportCount,
            ProcessedReportCount = x.ProcessedReportCount,
            FailedReportCount = x.FailedReportCount,
            LastReportId = x.LastReportId,
            RetryCount = x.RetryCount,
            NextRetryAtUtc = x.NextRetryAtUtc,
            LeaseUntilUtc = x.LeaseUntilUtc,
            LastRunAtUtc = x.LastRunAtUtc,
            CompletedAtUtc = x.CompletedAtUtc,
            LastErrorType = x.LastErrorType,
            LastError = x.LastError,
            LastErrorAtUtc = x.LastErrorAtUtc,
            IsActive = x.IsActive,
            CreatedAtUtc = x.CreatedAtUtc,
            UpdatedAtUtc = x.UpdatedAtUtc
        };

    private static FilterDefinition<T> EqIfNotBlank<T>(
        FilterDefinitionBuilder<T> fb,
        System.Linq.Expressions.Expression<Func<T, string?>> field,
        string? value)
    {
        value = NullIfWhiteSpace(value);
        return value is null ? FilterDefinition<T>.Empty : fb.Eq(field, value);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
