using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.Common;

public interface IDocRoleReadModelProjectionRetryJobService
{
    Task EnqueueRebuildWorkAsync(string workId, string byUserId, string reason, Exception ex, CancellationToken ct = default);
    Task EnqueueRebuildWorkAssignmentsAsync(string workId, string byUserId, string reason, Exception ex, CancellationToken ct = default);
    Task EnqueueRebuildAssignmentAsync(string assignmentId, string byUserId, string reason, Exception ex, CancellationToken ct = default);
    Task EnqueueRebuildWorkReportPeriodsAsync(string workId, string byUserId, string reason, Exception ex, CancellationToken ct = default);
    Task EnqueueRebuildReportPeriodAsync(string workReportPeriodId, string byUserId, string reason, Exception ex, CancellationToken ct = default);
    Task EnqueueRebuildMyReportTemplateAsync(string workId, string dynamicFormTemplateId, string userId, string byUserId, string reason, Exception ex, CancellationToken ct = default);
    Task EnqueueSoftDeleteDocAsync(DocType docType, string docId, string byUserId, string reason, Exception ex, CancellationToken ct = default);
    Task<int> ProcessPendingJobsAsync(int maxJobs = 20, CancellationToken ct = default);
}

public sealed class DocRoleReadModelProjectionRetryJobService : IDocRoleReadModelProjectionRetryJobService
{
    private const int DefaultMaxRetryCount = 10;

    private readonly MongoDbContext _ctx;
    private readonly DocRoleReadModelProjectionService _projection;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly ILogger<DocRoleReadModelProjectionRetryJobService> _log;
    private readonly int _maxRetryCount;

    public DocRoleReadModelProjectionRetryJobService(
        MongoDbContext ctx,
        DocRoleReadModelProjectionService projection,
        IWorkStatusOperationLogService statusLog,
        IConfiguration cfg,
        ILogger<DocRoleReadModelProjectionRetryJobService> log)
    {
        _ctx = ctx;
        _projection = projection;
        _statusLog = statusLog;
        _log = log;
        _maxRetryCount = Math.Clamp(
            cfg.GetValue<int?>("DocRoleProjectionRetry:MaxRetryCount") ?? DefaultMaxRetryCount,
            1,
            50);
    }

    public Task EnqueueRebuildWorkAsync(string workId, string byUserId, string reason, Exception ex, CancellationToken ct = default)
        => EnqueueAsync(new ProjectionRetryJobSeed
        {
            Action = DocRoleProjectionRetryActions.RebuildWork,
            DedupeKey = $"{DocRoleProjectionRetryActions.RebuildWork}:{workId}",
            WorkId = workId,
            ByUserId = byUserId
        }, reason, ex, ct);

    public Task EnqueueRebuildWorkAssignmentsAsync(string workId, string byUserId, string reason, Exception ex, CancellationToken ct = default)
        => EnqueueAsync(new ProjectionRetryJobSeed
        {
            Action = DocRoleProjectionRetryActions.RebuildWorkAssignments,
            DedupeKey = $"{DocRoleProjectionRetryActions.RebuildWorkAssignments}:{workId}",
            WorkId = workId,
            ByUserId = byUserId
        }, reason, ex, ct);

    public Task EnqueueRebuildAssignmentAsync(string assignmentId, string byUserId, string reason, Exception ex, CancellationToken ct = default)
        => EnqueueAsync(new ProjectionRetryJobSeed
        {
            Action = DocRoleProjectionRetryActions.RebuildAssignment,
            DedupeKey = $"{DocRoleProjectionRetryActions.RebuildAssignment}:{assignmentId}",
            AssignmentId = assignmentId,
            ByUserId = byUserId
        }, reason, ex, ct);

    public Task EnqueueRebuildWorkReportPeriodsAsync(string workId, string byUserId, string reason, Exception ex, CancellationToken ct = default)
        => EnqueueAsync(new ProjectionRetryJobSeed
        {
            Action = DocRoleProjectionRetryActions.RebuildWorkReportPeriods,
            DedupeKey = $"{DocRoleProjectionRetryActions.RebuildWorkReportPeriods}:{workId}",
            WorkId = workId,
            ByUserId = byUserId
        }, reason, ex, ct);

    public Task EnqueueRebuildReportPeriodAsync(string workReportPeriodId, string byUserId, string reason, Exception ex, CancellationToken ct = default)
        => EnqueueAsync(new ProjectionRetryJobSeed
        {
            Action = DocRoleProjectionRetryActions.RebuildReportPeriod,
            DedupeKey = $"{DocRoleProjectionRetryActions.RebuildReportPeriod}:{workReportPeriodId}",
            WorkReportPeriodId = workReportPeriodId,
            ByUserId = byUserId
        }, reason, ex, ct);

    public Task EnqueueRebuildMyReportTemplateAsync(string workId, string dynamicFormTemplateId, string userId, string byUserId, string reason, Exception ex, CancellationToken ct = default)
        => EnqueueAsync(new ProjectionRetryJobSeed
        {
            Action = DocRoleProjectionRetryActions.RebuildMyReportTemplate,
            DedupeKey = $"{DocRoleProjectionRetryActions.RebuildMyReportTemplate}:{workId}:{dynamicFormTemplateId}:{userId}",
            WorkId = workId,
            DynamicFormTemplateId = dynamicFormTemplateId,
            UserId = userId,
            ByUserId = byUserId
        }, reason, ex, ct);

    public Task EnqueueSoftDeleteDocAsync(DocType docType, string docId, string byUserId, string reason, Exception ex, CancellationToken ct = default)
        => EnqueueAsync(new ProjectionRetryJobSeed
        {
            Action = DocRoleProjectionRetryActions.SoftDeleteDoc,
            DedupeKey = $"{DocRoleProjectionRetryActions.SoftDeleteDoc}:{docType}:{docId}",
            DocType = docType,
            DocId = docId,
            ByUserId = byUserId
        }, reason, ex, ct);

    public async Task<int> ProcessPendingJobsAsync(int maxJobs = 20, CancellationToken ct = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        maxJobs = Math.Clamp(maxJobs, 1, 200);

        var processed = 0;
        var failed = 0;

        for (var i = 0; i < maxJobs; i++)
        {
            var job = await ClaimNextJobAsync(ct);
            if (job is null)
                break;

            try
            {
                await ProcessSingleJobAsync(job, ct);
                await CompleteJobAsync(job.Id, ct);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await MarkRetryAsync(job, ex, ct);
                failed++;
            }
        }

        if (processed > 0 || failed > 0)
        {
            await WriteOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = "DOCROLE_PROJECTION_RETRY_SCAN",
                Scope = "docrole-read-model-projection-retry",
                Result = failed == 0 ? "SUCCESS" : "PARTIAL_FAILED",
                ActorUserId = "system",
                Summary = $"processed={processed};failed={failed};maxJobs={maxJobs}",
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, ct);
        }

        return processed;
    }

    private async Task EnqueueAsync(
        ProjectionRetryJobSeed seed,
        string reason,
        Exception ex,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seed.DedupeKey))
            return;

        try
        {
            var now = DateTime.UtcNow;
            var filter = Builders<DocRoleReadModelProjectionRetryJob>.Filter.Eq(x => x.DedupeKey, seed.DedupeKey)
                         & Builders<DocRoleReadModelProjectionRetryJob>.Filter.Eq(x => x.IsActive, true)
                         & Builders<DocRoleReadModelProjectionRetryJob>.Filter.Eq(x => x.IsDeleted, false);

            var update = Builders<DocRoleReadModelProjectionRetryJob>.Update
                .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString())
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .Set(x => x.DedupeKey, seed.DedupeKey)
                .Set(x => x.Action, seed.Action)
                .Set(x => x.Status, DocRoleProjectionRetryJobStatuses.Pending)
                .Set(x => x.WorkId, seed.WorkId)
                .Set(x => x.AssignmentId, seed.AssignmentId)
                .Set(x => x.WorkReportPeriodId, seed.WorkReportPeriodId)
                .Set(x => x.DynamicExcelId, seed.DynamicExcelId)
                .Set(x => x.DynamicFormTemplateId, seed.DynamicFormTemplateId)
                .Set(x => x.UserId, seed.UserId)
                .Set(x => x.DocType, seed.DocType)
                .Set(x => x.DocId, seed.DocId)
                .Set(x => x.ByUserId, string.IsNullOrWhiteSpace(seed.ByUserId) ? "system" : seed.ByUserId)
                .Set(x => x.Reason, reason)
                .Set(x => x.NextRetryAtUtc, now)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.CompletedAtUtc, null)
                .Set(x => x.IsActive, true)
                .Set(x => x.IsDeleted, false)
                .Set(x => x.LastErrorType, ex.GetType().FullName)
                .Set(x => x.LastError, Truncate(ex.ToString(), 4000))
                .Set(x => x.LastErrorAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now);

            await _ctx.DocRoleReadModelProjectionRetryJobs.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                ct);
        }
        catch (Exception enqueueEx) when (enqueueEx is not OperationCanceledException)
        {
            _log.LogError(
                enqueueEx,
                "Failed to enqueue DocRole read-model projection retry. action={action} dedupeKey={dedupeKey}",
                seed.Action,
                seed.DedupeKey);
        }
    }

    private async Task<DocRoleReadModelProjectionRetryJob?> ClaimNextJobAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseUntil = now.AddMinutes(3);
        var fb = Builders<DocRoleReadModelProjectionRetryJob>.Filter;
        var runnableStatus = fb.In(x => x.Status, new[]
                             {
                                 DocRoleProjectionRetryJobStatuses.Pending,
                                 DocRoleProjectionRetryJobStatuses.RetryWaiting
                             })
                             | (fb.Eq(x => x.Status, DocRoleProjectionRetryJobStatuses.Running)
                                & (fb.Lt(x => x.LeaseUntilUtc, now) | fb.Eq(x => x.LeaseUntilUtc, null)));

        var filter = fb.Eq(x => x.IsActive, true)
                     & fb.Eq(x => x.IsDeleted, false)
                     & runnableStatus
                     & (fb.Eq(x => x.NextRetryAtUtc, null) | fb.Lte(x => x.NextRetryAtUtc, now));

        return await _ctx.DocRoleReadModelProjectionRetryJobs.FindOneAndUpdateAsync(
            filter,
            Builders<DocRoleReadModelProjectionRetryJob>.Update
                .Set(x => x.Status, DocRoleProjectionRetryJobStatuses.Running)
                .Set(x => x.LeaseUntilUtc, leaseUntil)
                .Set(x => x.LastRunAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<DocRoleReadModelProjectionRetryJob>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<DocRoleReadModelProjectionRetryJob>.Sort
                    .Ascending(x => x.NextRetryAtUtc)
                    .Ascending(x => x.CreatedAtUtc)
            },
            ct);
    }

    private async Task ProcessSingleJobAsync(
        DocRoleReadModelProjectionRetryJob job,
        CancellationToken ct)
    {
        switch (job.Action)
        {
            case DocRoleProjectionRetryActions.RebuildWork:
                Require(job.WorkId, nameof(job.WorkId));
                await _projection.RebuildWorkAsync(job.WorkId!, job.ByUserId, ct);
                break;
            case DocRoleProjectionRetryActions.RebuildWorkAssignments:
                Require(job.WorkId, nameof(job.WorkId));
                await _projection.RebuildWorkAssignmentsAsync(job.WorkId!, job.ByUserId, ct);
                break;
            case DocRoleProjectionRetryActions.RebuildAssignment:
                Require(job.AssignmentId, nameof(job.AssignmentId));
                await _projection.RebuildAssignmentAsync(job.AssignmentId!, job.ByUserId, ct);
                break;
            case DocRoleProjectionRetryActions.RebuildWorkReportPeriods:
                Require(job.WorkId, nameof(job.WorkId));
                await _projection.RebuildWorkReportPeriodsAsync(job.WorkId!, job.ByUserId, ct);
                break;
            case DocRoleProjectionRetryActions.RebuildReportPeriod:
                Require(job.WorkReportPeriodId, nameof(job.WorkReportPeriodId));
                await _projection.RebuildReportPeriodAsync(job.WorkReportPeriodId!, job.ByUserId, ct);
                break;
            case DocRoleProjectionRetryActions.RebuildMyReportTemplate:
                Require(job.WorkId, nameof(job.WorkId));
                Require(job.DynamicFormTemplateId, nameof(job.DynamicFormTemplateId));
                Require(job.UserId, nameof(job.UserId));
                await _projection.RebuildMyReportTemplateAsync(job.WorkId!, job.DynamicFormTemplateId!, job.UserId!, job.ByUserId, ct);
                break;
            case DocRoleProjectionRetryActions.SoftDeleteDoc:
                if (!job.DocType.HasValue)
                    throw AppExceptionFactory.BadRequest(AppErrorCode.OPERATIONS_RETRY_JOB_FIELD_REQUIRED, new { job.Id, field = nameof(job.DocType) });
                Require(job.DocId, nameof(job.DocId));
                await _projection.SoftDeleteByDocAsync(job.DocType.Value, job.DocId!, job.ByUserId, ct);
                break;
            default:
                throw AppExceptionFactory.BadRequest(AppErrorCode.OPERATIONS_RETRY_ACTION_UNSUPPORTED, new { job.Id, job.Action });
        }
    }

    private async Task CompleteJobAsync(string jobId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.DocRoleReadModelProjectionRetryJobs.UpdateOneAsync(
            x => x.Id == jobId,
            Builders<DocRoleReadModelProjectionRetryJob>.Update
                .Set(x => x.Status, DocRoleProjectionRetryJobStatuses.Completed)
                .Set(x => x.IsActive, false)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.CompletedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);
    }

    private async Task MarkRetryAsync(
        DocRoleReadModelProjectionRetryJob job,
        Exception ex,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nextRetryCount = job.RetryCount + 1;
        var delayMinutes = Math.Min(2 * Math.Pow(2, Math.Max(0, nextRetryCount - 1)), 360);
        var nextRetryAt = now.AddMinutes(delayMinutes);
        var deadLetter = nextRetryCount >= _maxRetryCount;
        var nextStatus = deadLetter
            ? DocRoleProjectionRetryJobStatuses.DeadLetter
            : DocRoleProjectionRetryJobStatuses.RetryWaiting;

        await _ctx.DocRoleReadModelProjectionRetryJobs.UpdateOneAsync(
            x => x.Id == job.Id,
            Builders<DocRoleReadModelProjectionRetryJob>.Update
                .Set(x => x.Status, nextStatus)
                .Set(x => x.IsActive, !deadLetter)
                .Set(x => x.RetryCount, nextRetryCount)
                .Set(x => x.NextRetryAtUtc, deadLetter ? null : nextRetryAt)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastErrorType, ex.GetType().FullName)
                .Set(x => x.LastError, Truncate(ex.ToString(), 4000))
                .Set(x => x.LastErrorAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);

        _log.LogWarning(
            ex,
            "DocRole read-model projection retry failed. jobId={jobId} action={action} retryCount={retryCount} nextStatus={nextStatus} nextRetryAtUtc={nextRetryAtUtc}",
            job.Id,
            job.Action,
            nextRetryCount,
            nextStatus,
            deadLetter ? null : nextRetryAt);
    }

    private async Task WriteOperationLogAsync(
        WorkStatusOperationLog log,
        DateTime startedAtUtc,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        log.CompletedAtUtc = completedAtUtc;
        log.DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        await _statusLog.WriteAsync(log, ct);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw AppExceptionFactory.BadRequest(AppErrorCode.OPERATIONS_RETRY_JOB_FIELD_REQUIRED, new { field = name });
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private sealed class ProjectionRetryJobSeed
    {
        public string DedupeKey { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string? WorkId { get; init; }
        public string? AssignmentId { get; init; }
        public string? WorkReportPeriodId { get; init; }
        public string? DynamicExcelId { get; init; }
        public string? DynamicFormTemplateId { get; init; }
        public string? UserId { get; init; }
        public DocType? DocType { get; init; }
        public string? DocId { get; init; }
        public string ByUserId { get; init; } = "system";
    }
}
