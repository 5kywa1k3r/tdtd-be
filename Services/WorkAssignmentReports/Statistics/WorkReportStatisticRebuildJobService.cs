using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.Notifications;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed class WorkReportStatisticRebuildJobService : IWorkReportStatisticRebuildJobService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkReportLabelStatisticsService _labelStatistics;
    private readonly IWorkReportTableStatisticsService _tableStatistics;
    private readonly IWorkReportFieldStatisticsService _fieldStatistics;
    private readonly INotificationService _notifications;
    private readonly IAppTimeService _time;
    private readonly int _maxRetryCount;

    public WorkReportStatisticRebuildJobService(
        MongoDbContext ctx,
        IWorkReportLabelStatisticsService labelStatistics,
        IWorkReportTableStatisticsService tableStatistics,
        IWorkReportFieldStatisticsService fieldStatistics,
        INotificationService notifications,
        IAppTimeService time,
        IConfiguration cfg)
    {
        _ctx = ctx;
        _labelStatistics = labelStatistics;
        _tableStatistics = tableStatistics;
        _fieldStatistics = fieldStatistics;
        _notifications = notifications;
        _time = time;
        _maxRetryCount = Math.Clamp(cfg.GetValue<int?>("DynamicFormStatisticRebuild:MaxRetryCount") ?? 5, 1, 20);
    }

    public async Task<StatisticRebuildJobEnqueueResult> EnqueueForTemplateStatisticConfigAsync(
        DynamicFormTemplate template,
        string requestedByUserId,
        bool highPriority,
        CancellationToken ct = default)
    {
        var now = _time.UtcNow;
        var totalReports = await _ctx.WorkAssignmentReports.CountDocumentsAsync(
            Builders<WorkAssignmentReport>.Filter.Eq(x => x.DynamicFormTemplateId, template.Id)
            & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true)
            & Builders<WorkAssignmentReport>.Filter.Ne(x => x.IsActive, false)
            & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false),
            cancellationToken: ct);
        var scheduledAtUtc = highPriority ? now : _time.NextLocalMidnightUtc(now);
        var dedupeKey = $"dynamic-form-statistic-rebuild:{template.Id}";
        var priority = highPriority
            ? WorkReportStatisticRebuildJobPriorities.High
            : WorkReportStatisticRebuildJobPriorities.Normal;

        var filter = Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.DedupeKey, dedupeKey)
            & Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.IsActive, true)
            & Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.IsDeleted, false);

        var update = Builders<WorkReportStatisticRebuildJob>.Update
            .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString())
            .SetOnInsert(x => x.CreatedAtUtc, now)
            .SetOnInsert(x => x.CreatedByUserId, requestedByUserId)
            .Set(x => x.DedupeKey, dedupeKey)
            .Set(x => x.DynamicFormTemplateId, template.Id)
            .Set(x => x.DynamicFormTemplateCode, template.Code)
            .Set(x => x.DynamicFormTemplateName, template.Name)
            .Set(x => x.RequestedByUserId, requestedByUserId)
            .Set(x => x.Priority, priority)
            .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Pending)
            .Set(x => x.TotalReportCount, totalReports)
            .Set(x => x.ProcessedReportCount, 0)
            .Set(x => x.FailedReportCount, 0)
            .Set(x => x.LastReportId, null)
            .Set(x => x.RetryCount, 0)
            .Set(x => x.NextRetryAtUtc, scheduledAtUtc)
            .Set(x => x.LeaseUntilUtc, null)
            .Set(x => x.CompletedAtUtc, null)
            .Set(x => x.LastErrorType, null)
            .Set(x => x.LastError, null)
            .Set(x => x.LastErrorAtUtc, null)
            .Set(x => x.IsActive, true)
            .Set(x => x.IsDeleted, false)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, requestedByUserId);

        await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            filter,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);

        var job = await _ctx.WorkReportStatisticRebuildJobs
            .Find(filter)
            .FirstAsync(ct);

        return new StatisticRebuildJobEnqueueResult(job.Id, totalReports, scheduledAtUtc, highPriority);
    }

    public async Task<IReadOnlyList<StatisticRebuildJobEnqueueResult>> EnqueueForLabelChangeAsync(
        LabelCatalogItem label,
        string requestedByUserId,
        bool highPriority,
        CancellationToken ct = default)
    {
        if (label is null || string.IsNullOrWhiteSpace(label.Code))
            return Array.Empty<StatisticRebuildJobEnqueueResult>();

        var labelCode = label.Code.Trim().ToLowerInvariant();
        var labelLiteralRegex = new BsonRegularExpression(Regex.Escape($"\"{labelCode}\""), "i");
        var templateIds = new HashSet<string>(StringComparer.Ordinal);

        var templateFilter = Builders<DynamicFormTemplate>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<DynamicFormTemplate>.Filter.Or(
                Builders<DynamicFormTemplate>.Filter.AnyEq(x => x.TagCodes, labelCode),
                Builders<DynamicFormTemplate>.Filter.Regex(x => x.SectionsJson, labelLiteralRegex),
                Builders<DynamicFormTemplate>.Filter.Regex(x => x.FieldsJson, labelLiteralRegex),
                Builders<DynamicFormTemplate>.Filter.Regex(x => x.ExcelBlockJson, labelLiteralRegex),
                Builders<DynamicFormTemplate>.Filter.Regex(x => x.BlocksJson, labelLiteralRegex));

        var templateMatches = await _ctx.DynamicFormTemplates
            .Find(templateFilter)
            .Project(x => x.Id)
            .ToListAsync(ct);
        foreach (var id in templateMatches.Where(x => !string.IsNullOrWhiteSpace(x)))
            templateIds.Add(id);

        if (templateIds.Count == 0)
            return Array.Empty<StatisticRebuildJobEnqueueResult>();

        var templates = await _ctx.DynamicFormTemplates
            .Find(x => templateIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        var results = new List<StatisticRebuildJobEnqueueResult>();
        foreach (var template in templates)
        {
            results.Add(await EnqueueForTemplateStatisticConfigAsync(
                template,
                requestedByUserId,
                highPriority,
                ct));
        }

        return results;
    }

    public async Task<int> ProcessPendingJobsAsync(
        int maxJobs = 3,
        int batchSize = 25,
        CancellationToken ct = default)
    {
        maxJobs = Math.Clamp(maxJobs, 1, 20);
        batchSize = Math.Clamp(batchSize, 1, 100);
        var processedJobs = 0;

        for (var i = 0; i < maxJobs; i++)
        {
            var job = await ClaimNextJobAsync(ct);
            if (job is null)
                break;

            try
            {
                await ProcessSingleJobBatchAsync(job, batchSize, ct);
                processedJobs++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await MarkRetryAsync(job, ex, ct);
            }
        }

        return processedJobs;
    }

    private async Task<WorkReportStatisticRebuildJob?> ClaimNextJobAsync(CancellationToken ct)
    {
        var now = _time.UtcNow;
        var leaseUntil = now.AddMinutes(10);
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.IsActive, true)
            & fb.Eq(x => x.IsDeleted, false)
            & (fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Pending)
               | fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.RetryWaiting)
               | (fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running)
                  & fb.Lt(x => x.LeaseUntilUtc, now)))
            & (fb.Eq(x => x.NextRetryAtUtc, null) | fb.Lte(x => x.NextRetryAtUtc, now));

        return await _ctx.WorkReportStatisticRebuildJobs.FindOneAndUpdateAsync(
            filter,
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running)
                .Set(x => x.LeaseUntilUtc, leaseUntil)
                .Set(x => x.LastRunAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            new FindOneAndUpdateOptions<WorkReportStatisticRebuildJob>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<WorkReportStatisticRebuildJob>.Sort
                    .Ascending(x => x.Priority)
                    .Ascending(x => x.NextRetryAtUtc)
                    .Ascending(x => x.CreatedAtUtc)
            },
            ct);
    }

    private async Task ProcessSingleJobBatchAsync(
        WorkReportStatisticRebuildJob job,
        int batchSize,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentReport>.Filter;
        var filter = fb.Eq(x => x.DynamicFormTemplateId, job.DynamicFormTemplateId)
            & fb.Eq(x => x.IsCurrent, true)
            & fb.Ne(x => x.IsActive, false)
            & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(job.LastReportId))
            filter &= fb.Gt(x => x.Id, job.LastReportId);

        var reports = await _ctx.WorkAssignmentReports
            .Find(filter)
            .SortBy(x => x.Id)
            .Limit(batchSize)
            .Project(x => x.Id)
            .ToListAsync(ct);

        if (reports.Count == 0)
        {
            await CompleteJobAsync(job, ct);
            return;
        }

        var aggregateKeys = new HashSet<ReportStatisticAggregateKey>();

        foreach (var reportId in reports)
        {
            AddAggregateKey(
                aggregateKeys,
                await _labelStatistics.RebuildValuesForReportAsync(reportId, job.RequestedByUserId, ct));
            AddAggregateKey(
                aggregateKeys,
                await _tableStatistics.RebuildValuesForReportAsync(reportId, job.RequestedByUserId, ct));
            AddAggregateKey(
                aggregateKeys,
                await _fieldStatistics.RebuildValuesForReportAsync(reportId, job.RequestedByUserId, ct));
        }

        foreach (var key in aggregateKeys)
        {
            await _labelStatistics.RebuildAggregatesForWorkPeriodAsync(
                key.WorkId,
                key.PeriodInstanceKey,
                key.DynamicFormTemplateId,
                job.RequestedByUserId,
                ct);
            await _tableStatistics.RebuildAggregatesForWorkPeriodAsync(
                key.WorkId,
                key.PeriodInstanceKey,
                key.DynamicFormTemplateId,
                job.RequestedByUserId,
                ct);
            await _fieldStatistics.RebuildAggregatesForWorkPeriodAsync(
                key.WorkId,
                key.PeriodInstanceKey,
                key.DynamicFormTemplateId,
                job.RequestedByUserId,
                ct);
        }

        var lastReportId = reports[^1];
        var completed = reports.Count < batchSize;
        var update = Builders<WorkReportStatisticRebuildJob>.Update
            .Inc(x => x.ProcessedReportCount, reports.Count)
            .Set(x => x.LastReportId, lastReportId)
            .Set(x => x.LeaseUntilUtc, null)
            .Set(x => x.UpdatedAtUtc, _time.UtcNow)
            .Set(x => x.UpdatedByUserId, null);

        if (completed)
        {
            update = update
                .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Completed)
                .Set(x => x.IsActive, false)
                .Set(x => x.CompletedAtUtc, _time.UtcNow)
                .Set(x => x.NextRetryAtUtc, null);
        }
        else
        {
            update = update
                .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Pending)
                .Set(x => x.NextRetryAtUtc, _time.UtcNow);
        }

        await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            x => x.Id == job.Id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (completed)
            await NotifyCompletedAsync(job, ct);
    }

    private async Task CompleteJobAsync(WorkReportStatisticRebuildJob job, CancellationToken ct)
    {
        await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            x => x.Id == job.Id && !x.IsDeleted,
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Completed)
                .Set(x => x.IsActive, false)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.CompletedAtUtc, _time.UtcNow)
                .Set(x => x.UpdatedAtUtc, _time.UtcNow)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);

        await NotifyCompletedAsync(job, ct);
    }

    private async Task MarkRetryAsync(
        WorkReportStatisticRebuildJob job,
        Exception ex,
        CancellationToken ct)
    {
        var now = _time.UtcNow;
        var retryCount = job.RetryCount + 1;
        var dead = retryCount >= _maxRetryCount;
        var nextRetryAt = dead ? (DateTime?)null : now.AddMinutes(Math.Min(60, retryCount * 5));

        await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            x => x.Id == job.Id && !x.IsDeleted,
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, dead
                    ? WorkReportStatisticRebuildJobStatuses.DeadLetter
                    : WorkReportStatisticRebuildJobStatuses.RetryWaiting)
                .Set(x => x.IsActive, !dead)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.NextRetryAtUtc, nextRetryAt)
                .Set(x => x.RetryCount, retryCount)
                .Inc(x => x.FailedReportCount, 1)
                .Set(x => x.LastErrorType, ex.GetType().FullName)
                .Set(x => x.LastError, ex.Message)
                .Set(x => x.LastErrorAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);

        if (dead)
            await NotifyFailedAsync(job, ex, ct);
    }

    private Task NotifyCompletedAsync(
        WorkReportStatisticRebuildJob job,
        CancellationToken ct)
        => _notifications.CreateManyAsync(new[]
        {
            new NotificationCommand
            {
                RecipientUserId = job.RequestedByUserId,
                Type = UserNotificationTypes.DynamicFormStatisticRebuild,
                Severity = UserNotificationSeverities.Info,
                Title = "Cập nhật thống kê biểu mẫu động đã hoàn tất",
                Body = $"Biểu mẫu {job.DynamicFormTemplateCode ?? job.DynamicFormTemplateId}: đã cập nhật thống kê cho các báo cáo hiện hành.",
                ActorUserId = job.RequestedByUserId,
                OccurredAtUtc = _time.UtcNow,
                EventKey = $"dynamic-form-stat-rebuild:completed:{job.Id}"
            }
        }, ct);

    private Task NotifyFailedAsync(
        WorkReportStatisticRebuildJob job,
        Exception ex,
        CancellationToken ct)
        => _notifications.CreateManyAsync(new[]
        {
            new NotificationCommand
            {
                RecipientUserId = job.RequestedByUserId,
                Type = UserNotificationTypes.DynamicFormStatisticRebuild,
                Severity = UserNotificationSeverities.Warning,
                Title = "Cập nhật thống kê biểu mẫu động bị lỗi",
                Body = $"Biểu mẫu {job.DynamicFormTemplateCode ?? job.DynamicFormTemplateId}: cập nhật thống kê thất bại sau nhiều lần thử. Lỗi cuối: {ex.Message}",
                ActorUserId = job.RequestedByUserId,
                OccurredAtUtc = _time.UtcNow,
                EventKey = $"dynamic-form-stat-rebuild:failed:{job.Id}"
            }
        }, ct);

    private static void AddAggregateKey(
        HashSet<ReportStatisticAggregateKey> aggregateKeys,
        ReportStatisticAggregateKey? aggregateKey)
    {
        if (aggregateKey is not null && !string.IsNullOrWhiteSpace(aggregateKey.WorkId))
            aggregateKeys.Add(aggregateKey);
    }

}
