using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Operations;
using tdtd_be.Models;
using tdtd_be.Services;

namespace tdtd_be.Services.Common;

public interface IUserActionLogService
{
    Task RecordAsync(UserActionLogSeed seed, CancellationToken ct = default);
    Task<int> ProcessPendingRetriesAsync(int maxJobs = 20, CancellationToken ct = default);
    Task<PagedResult<UserActionLogRow>> SearchAsync(
        UserActionLogSearchRequest request,
        MeResponse me,
        CancellationToken ct = default);
    Task<UserActionLogRow?> GetByIdAsync(string id, MeResponse me, CancellationToken ct = default);
    Task<PagedResult<UserActionLogRetryJobRow>> SearchRetryJobsAsync(
        JobRunSearchRequest request,
        CancellationToken ct = default);
}

public sealed class UserActionLogService : IUserActionLogService
{
    private const int DefaultMaxRetryCount = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly MongoDbContext _ctx;
    private readonly ILogger<UserActionLogService> _log;
    private readonly int _maxRetryCount;

    public UserActionLogService(
        MongoDbContext ctx,
        IConfiguration cfg,
        ILogger<UserActionLogService> log)
    {
        _ctx = ctx;
        _log = log;
        _maxRetryCount = Math.Clamp(
            cfg.GetValue<int?>("UserActionLogRetry:MaxRetryCount") ?? DefaultMaxRetryCount,
            1,
            50);
    }

    public async Task RecordAsync(UserActionLogSeed seed, CancellationToken ct = default)
    {
        if (seed is null || string.IsNullOrWhiteSpace(seed.Action))
            return;

        try
        {
            var log = await BuildLogAsync(seed, ct);
            await _ctx.UserActionLogs.InsertOneAsync(log, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Failed to persist user action log. action={action} workId={workId} assignmentId={assignmentId} periodId={periodId} reportId={reportId}",
                seed.Action,
                seed.WorkId,
                seed.WorkAssignmentId,
                seed.WorkReportPeriodId,
                seed.WorkAssignmentReportId);

            await TryEnqueueRetryAsync(seed, ex, CancellationToken.None);
        }
        catch (OperationCanceledException ex)
        {
            await TryEnqueueRetryAsync(seed, ex, CancellationToken.None);
        }
    }

    public async Task<int> ProcessPendingRetriesAsync(int maxJobs = 20, CancellationToken ct = default)
    {
        maxJobs = Math.Clamp(maxJobs, 1, 200);
        var processed = 0;

        for (var i = 0; i < maxJobs; i++)
        {
            var job = await ClaimNextRetryJobAsync(ct);
            if (job is null)
                break;

            try
            {
                var seed = JsonSerializer.Deserialize<UserActionLogSeed>(job.PayloadJson, JsonOptions)
                           ?? throw AppExceptionFactory.BadRequest(AppErrorCode.OPERATIONS_RETRY_PAYLOAD_INVALID, new { job.Id });

                var log = await BuildLogAsync(seed, ct);
                await _ctx.UserActionLogs.InsertOneAsync(log, cancellationToken: ct);
                await CompleteRetryJobAsync(job.Id, ct);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await MarkRetryJobAsync(job, ex, ct);
            }
        }

        return processed;
    }

    public async Task<PagedResult<UserActionLogRow>> SearchAsync(
        UserActionLogSearchRequest request,
        MeResponse me,
        CancellationToken ct = default)
    {
        request ??= new UserActionLogSearchRequest();

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var filter = await BuildSearchFilterAsync(request, me, ct);
        var sort = Builders<UserActionLog>.Sort
            .Descending(x => x.OccurredAtUtc)
            .Descending(x => x.CreatedAtUtc);

        var total = await _ctx.UserActionLogs.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.UserActionLogs
            .Find(filter)
            .Sort(sort)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<UserActionLogRow>(
            rows.Select(ToRow).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<UserActionLogRow?> GetByIdAsync(string id, MeResponse me, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var request = new UserActionLogSearchRequest { Page = 0, PageSize = 1 };
        var filter = await BuildViewerScopeFilterAsync(request, me, ct);
        filter &= Builders<UserActionLog>.Filter.Eq(x => x.Id, id.Trim());
        filter &= Builders<UserActionLog>.Filter.Eq(x => x.IsDeleted, false);

        var row = await _ctx.UserActionLogs.Find(filter).FirstOrDefaultAsync(ct);
        return row is null ? null : ToRow(row);
    }

    public async Task<PagedResult<UserActionLogRetryJobRow>> SearchRetryJobsAsync(
        JobRunSearchRequest request,
        CancellationToken ct = default)
    {
        request ??= new JobRunSearchRequest();

        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var fb = Builders<UserActionLogRetryJob>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);

        if (!request.IncludeInactive)
            filter &= fb.Eq(x => x.IsActive, true);

        filter &= EqIfNotBlank(fb, x => x.Status, request.Status);
        filter &= EqIfNotBlank(fb, x => x.Action, request.Action);

        var query = NullIfWhiteSpace(request.Query);
        if (query is not null)
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Or(
                fb.Regex(x => x.Action, regex),
                fb.Regex(x => x.Status, regex),
                fb.Regex(x => x.DedupeKey, regex),
                fb.Regex(x => x.LastErrorType, regex),
                fb.Regex(x => x.LastError, regex));
        }

        var total = await _ctx.UserActionLogRetryJobs.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.UserActionLogRetryJobs
            .Find(filter)
            .Sort(Builders<UserActionLogRetryJob>.Sort
                .Ascending(x => x.NextRetryAtUtc)
                .Descending(x => x.CreatedAtUtc))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<UserActionLogRetryJobRow>(
            rows.Select(ToRetryRow).ToList(),
            total,
            page,
            pageSize);
    }

    private async Task<UserActionLog> BuildLogAsync(UserActionLogSeed seed, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var workId = NullIfWhiteSpace(seed.WorkId);
        var assignmentId = NullIfWhiteSpace(seed.WorkAssignmentId);
        var periodId = NullIfWhiteSpace(seed.WorkReportPeriodId);
        var reportId = NullIfWhiteSpace(seed.WorkAssignmentReportId);

        WorkAssignmentReport? report = null;
        if (reportId is not null)
        {
            report = await _ctx.WorkAssignmentReports
                .Find(x => x.Id == reportId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            workId ??= NullIfWhiteSpace(report?.WorkId);
            assignmentId ??= NullIfWhiteSpace(report?.WorkAssignmentId);
            periodId ??= NullIfWhiteSpace(report?.WorkReportPeriodId);
        }

        WorkReportPeriod? period = null;
        if (periodId is not null)
        {
            period = await _ctx.WorkReportPeriods
                .Find(x => x.Id == periodId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            workId ??= NullIfWhiteSpace(period?.WorkId);
            assignmentId ??= NullIfWhiteSpace(period?.WorkAssignmentId);
        }

        WorkAssignment? assignment = null;
        if (assignmentId is not null)
        {
            assignment = await _ctx.WorkAssignments
                .Find(x => x.Id == assignmentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
            workId ??= NullIfWhiteSpace(assignment?.WorkId);
        }

        Work? work = null;
        if (workId is not null)
        {
            work = await _ctx.Works
                .Find(x => x.Id == workId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);
        }

        var allUserIds = new HashSet<string>(StringComparer.Ordinal);
        AddIfNotBlank(allUserIds, seed.ActorUserId);
        AddIfNotBlank(allUserIds, seed.TargetUserId);
        AddIfNotBlank(allUserIds, seed.FromUserId);
        AddIfNotBlank(allUserIds, seed.ToUserId);

        foreach (var userId in seed.TargetUserIds)
            AddIfNotBlank(allUserIds, userId);

        if (assignment?.Assignees is not null)
        {
            foreach (var assignee in assignment.Assignees)
                AddIfNotBlank(allUserIds, assignee.UserId);
        }

        AddIfNotBlank(allUserIds, report?.AssigneeUserId);
        AddIfNotBlank(allUserIds, report?.SubmittedByUserId);
        AddIfNotBlank(allUserIds, report?.ApprovedByUserId);
        AddIfNotBlank(allUserIds, report?.ReturnedByUserId);
        AddIfNotBlank(allUserIds, work?.CreatedByUserId);

        var userSnapshots = await BuildUserSnapshotsAsync(allUserIds.ToList(), ct);
        var usersById = userSnapshots.ToDictionary(x => x.UserId, StringComparer.Ordinal);

        var unitScopes = new Dictionary<string, UserActionLogUnitScope>(StringComparer.Ordinal);
        foreach (var snapshot in userSnapshots)
            AddUnitScope(unitScopes, snapshot);

        if (assignment?.Assignees is not null)
        {
            foreach (var assignee in assignment.Assignees)
                await AddUnitScopeAsync(unitScopes, assignee.UnitId, ct);
        }

        await AddUnitScopeAsync(unitScopes, period?.AssigneeUnitId, ct);

        var targetUserId = NullIfWhiteSpace(seed.TargetUserId)
                           ?? seed.TargetUserIds.Select(NullIfWhiteSpace).FirstOrDefault(x => x is not null)
                           ?? report?.AssigneeUserId;

        var log = new UserActionLog
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Action = NormalizeUpper(seed.Action),
            Scope = string.IsNullOrWhiteSpace(seed.Scope) ? ResolveScope(seed.Action) : NormalizeKebab(seed.Scope),
            Result = UserActionLogResults.Success,
            OccurredAtUtc = AppTimeRangeHelper.ToUtc(seed.OccurredAtUtc) ?? now,
            ActorUserId = NullIfWhiteSpace(seed.ActorUserId),
            Actor = LookupUser(usersById, seed.ActorUserId),
            TargetUserId = NullIfWhiteSpace(targetUserId),
            TargetUser = LookupUser(usersById, targetUserId),
            FromUserId = NullIfWhiteSpace(seed.FromUserId),
            FromUser = LookupUser(usersById, seed.FromUserId),
            ToUserId = NullIfWhiteSpace(seed.ToUserId),
            ToUser = LookupUser(usersById, seed.ToUserId),
            UserIds = allUserIds.ToList(),
            Users = userSnapshots,
            UnitIds = unitScopes.Keys.ToList(),
            UnitScopes = unitScopes.Values.OrderBy(x => x.UnitCode).ToList(),
            WorkId = work?.Id ?? workId,
            WorkAutoCode = work?.AutoCode,
            WorkCode = work?.Code,
            WorkName = work?.Name,
            WorkType = work?.Type.ToString(),
            WorkAssignmentId = assignment?.Id ?? assignmentId,
            WorkAssignmentCode = assignment?.Code,
            RootAssignmentId = assignment?.RootAssignmentId,
            DynamicFormTemplateId = assignment?.DynamicFormTemplateId ?? period?.DynamicFormTemplateId ?? report?.DynamicFormTemplateId,
            DynamicFormTemplateCode = assignment?.DynamicFormTemplateCode ?? period?.DynamicFormTemplateCode ?? report?.DynamicFormTemplateCode,
            DynamicFormTemplateName = assignment?.DynamicFormTemplateName ?? period?.DynamicFormTemplateName ?? report?.DynamicFormTemplateName,
            WorkReportPeriodId = period?.Id ?? periodId,
            PeriodKey = period?.PeriodKey ?? report?.PeriodKey,
            PeriodInstanceKey = period?.PeriodInstanceKey ?? report?.PeriodInstanceKey,
            PeriodStatus = period?.Status.ToString(),
            WorkAssignmentReportId = report?.Id ?? reportId,
            ReportStatus = report?.Status.ToString(),
            Summary = Truncate(seed.Summary, 500),
            Data = seed.Data,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = NullIfWhiteSpace(seed.ActorUserId),
            UpdatedByUserId = NullIfWhiteSpace(seed.ActorUserId)
        };

        return log;
    }

    private async Task<List<UserActionLogUserSnapshot>> BuildUserSnapshotsAsync(
        List<string> userIds,
        CancellationToken ct)
    {
        userIds = userIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (userIds.Count == 0)
            return new List<UserActionLogUserSnapshot>();

        var users = await _ctx.Users
            .Find(x => userIds.Contains(x.Id))
            .ToListAsync(ct);

        var unitIds = users
            .Select(x => x.UnitId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var units = unitIds.Count == 0
            ? new List<Unit>()
            : await _ctx.Units.Find(x => unitIds.Contains(x.Id)).ToListAsync(ct);

        var unitById = units.ToDictionary(x => x.Id, StringComparer.Ordinal);

        return users
            .Select(user =>
            {
                unitById.TryGetValue(user.UnitId ?? string.Empty, out var unit);
                return new UserActionLogUserSnapshot
                {
                    UserId = user.Id,
                    Username = user.Username,
                    FullName = user.FullName,
                    UnitId = user.UnitId,
                    UnitCode = unit?.Code,
                    UnitName = BuildUnitName(unit),
                    UnitLevel = unit?.Level
                };
            })
            .OrderBy(x => x.Username)
            .ToList();
    }

    private async Task AddUnitScopeAsync(
        Dictionary<string, UserActionLogUnitScope> target,
        string? unitId,
        CancellationToken ct)
    {
        unitId = NullIfWhiteSpace(unitId);
        if (unitId is null || target.ContainsKey(unitId))
            return;

        var unit = await _ctx.Units.Find(x => x.Id == unitId).FirstOrDefaultAsync(ct);
        if (unit is null)
            return;

        target[unit.Id] = new UserActionLogUnitScope
        {
            UnitId = unit.Id,
            UnitCode = unit.Code,
            UnitName = BuildUnitName(unit),
            UnitLevel = unit.Level
        };
    }

    private static void AddUnitScope(
        Dictionary<string, UserActionLogUnitScope> target,
        UserActionLogUserSnapshot snapshot)
    {
        var unitId = NullIfWhiteSpace(snapshot.UnitId);
        if (unitId is null || target.ContainsKey(unitId))
            return;

        target[unitId] = new UserActionLogUnitScope
        {
            UnitId = unitId,
            UnitCode = snapshot.UnitCode,
            UnitName = snapshot.UnitName,
            UnitLevel = snapshot.UnitLevel ?? 0
        };
    }

    private async Task EnqueueRetryAsync(
        UserActionLogSeed seed,
        Exception ex,
        CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            var payloadJson = JsonSerializer.Serialize(seed, JsonOptions);
            var dedupeKey = BuildDedupeKey(seed);

            var filter = Builders<UserActionLogRetryJob>.Filter.Eq(x => x.DedupeKey, dedupeKey)
                         & Builders<UserActionLogRetryJob>.Filter.Eq(x => x.IsActive, true)
                         & Builders<UserActionLogRetryJob>.Filter.Eq(x => x.IsDeleted, false);

            var update = Builders<UserActionLogRetryJob>.Update
                .SetOnInsert(x => x.Id, ObjectId.GenerateNewId().ToString())
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .Set(x => x.DedupeKey, dedupeKey)
                .Set(x => x.Action, NormalizeUpper(seed.Action))
                .Set(x => x.Status, UserActionLogRetryJobStatuses.Pending)
                .Set(x => x.PayloadJson, payloadJson)
                .Set(x => x.NextRetryAtUtc, now)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.CompletedAtUtc, null)
                .Set(x => x.IsActive, true)
                .Set(x => x.IsDeleted, false)
                .Set(x => x.LastErrorType, ex.GetType().FullName)
                .Set(x => x.LastError, Truncate(ex.ToString(), 4000))
                .Set(x => x.LastErrorAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now);

            await _ctx.UserActionLogRetryJobs.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                ct);
        }
        catch (Exception enqueueEx) when (enqueueEx is not OperationCanceledException)
        {
            _log.LogError(
                enqueueEx,
                "Failed to enqueue user action log retry. action={action}",
                seed.Action);
        }
    }

    private async Task<UserActionLogRetryJob?> ClaimNextRetryJobAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseUntil = now.AddMinutes(3);
        var fb = Builders<UserActionLogRetryJob>.Filter;
        var runnable = fb.In(x => x.Status, new[]
                       {
                           UserActionLogRetryJobStatuses.Pending,
                           UserActionLogRetryJobStatuses.RetryWaiting
                       })
                       | (fb.Eq(x => x.Status, UserActionLogRetryJobStatuses.Running)
                          & (fb.Lt(x => x.LeaseUntilUtc, now) | fb.Eq(x => x.LeaseUntilUtc, null)));

        var filter = fb.Eq(x => x.IsActive, true)
                     & fb.Eq(x => x.IsDeleted, false)
                     & runnable
                     & (fb.Eq(x => x.NextRetryAtUtc, null) | fb.Lte(x => x.NextRetryAtUtc, now));

        return await _ctx.UserActionLogRetryJobs.FindOneAndUpdateAsync(
            filter,
            Builders<UserActionLogRetryJob>.Update
                .Set(x => x.Status, UserActionLogRetryJobStatuses.Running)
                .Set(x => x.LeaseUntilUtc, leaseUntil)
                .Set(x => x.LastRunAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<UserActionLogRetryJob>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<UserActionLogRetryJob>.Sort
                    .Ascending(x => x.NextRetryAtUtc)
                    .Ascending(x => x.CreatedAtUtc)
            },
            ct);
    }

    private async Task TryEnqueueRetryAsync(
        UserActionLogSeed seed,
        Exception sourceException,
        CancellationToken ct)
    {
        try
        {
            await EnqueueRetryAsync(seed, sourceException, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(
                ex,
                "Failed to enqueue user action log retry. action={action} workId={workId} assignmentId={assignmentId}",
                seed.Action,
                seed.WorkId,
                seed.WorkAssignmentId);
        }
    }

    private async Task CompleteRetryJobAsync(string jobId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _ctx.UserActionLogRetryJobs.UpdateOneAsync(
            x => x.Id == jobId,
            Builders<UserActionLogRetryJob>.Update
                .Set(x => x.Status, UserActionLogRetryJobStatuses.Completed)
                .Set(x => x.IsActive, false)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.CompletedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);
    }

    private async Task MarkRetryJobAsync(
        UserActionLogRetryJob job,
        Exception ex,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var retryCount = job.RetryCount + 1;
        var deadLetter = retryCount >= _maxRetryCount;
        var delayMinutes = Math.Min(2 * Math.Pow(2, Math.Max(0, retryCount - 1)), 360);
        var nextRetryAt = now.AddMinutes(delayMinutes);

        await _ctx.UserActionLogRetryJobs.UpdateOneAsync(
            x => x.Id == job.Id,
            Builders<UserActionLogRetryJob>.Update
                .Set(x => x.Status, deadLetter
                    ? UserActionLogRetryJobStatuses.DeadLetter
                    : UserActionLogRetryJobStatuses.RetryWaiting)
                .Set(x => x.IsActive, !deadLetter)
                .Set(x => x.RetryCount, retryCount)
                .Set(x => x.NextRetryAtUtc, deadLetter ? null : nextRetryAt)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastErrorType, ex.GetType().FullName)
                .Set(x => x.LastError, Truncate(ex.ToString(), 4000))
                .Set(x => x.LastErrorAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now),
            cancellationToken: ct);

        _log.LogWarning(
            ex,
            "User action log retry failed. jobId={jobId} action={action} retryCount={retryCount}",
            job.Id,
            job.Action,
            retryCount);
    }

    private async Task<FilterDefinition<UserActionLog>> BuildSearchFilterAsync(
        UserActionLogSearchRequest request,
        MeResponse me,
        CancellationToken ct)
    {
        var fb = Builders<UserActionLog>.Filter;
        var filter = await BuildViewerScopeFilterAsync(request, me, ct);

        filter &= fb.Eq(x => x.IsDeleted, false);
        filter &= EqIfNotBlank(fb, x => x.Action, NormalizeUpperOrNull(request.Action));
        filter &= EqIfNotBlank(fb, x => x.Scope, NormalizeKebabOrNull(request.Scope));
        filter &= EqIfNotBlank(fb, x => x.Result, NormalizeUpperOrNull(request.Result));
        filter &= EqIfNotBlank(fb, x => x.WorkId, request.WorkId);
        filter &= EqIfNotBlank(fb, x => x.WorkAssignmentId, request.WorkAssignmentId);
        filter &= EqIfNotBlank(fb, x => x.WorkReportPeriodId, request.WorkReportPeriodId);
        filter &= EqIfNotBlank(fb, x => x.WorkAssignmentReportId, request.WorkAssignmentReportId);
        filter &= EqIfNotBlank(fb, x => x.ActorUserId, request.ActorUserId);

        var userId = NullIfWhiteSpace(request.UserId);
        if (userId is not null)
            filter &= fb.AnyEq(x => x.UserIds, userId);

        var fromUtc = AppTimeRangeHelper.ToUtc(request.FromUtc);
        if (fromUtc.HasValue)
            filter &= fb.Gte(x => x.OccurredAtUtc, fromUtc.Value);

        var toUtc = AppTimeRangeHelper.ToUtc(request.ToUtc);
        if (toUtc.HasValue)
            filter &= fb.Lte(x => x.OccurredAtUtc, toUtc.Value);

        var query = NullIfWhiteSpace(request.Query);
        if (query is not null)
        {
            var regex = new BsonRegularExpression(Regex.Escape(query), "i");
            filter &= fb.Or(
                fb.Regex(x => x.Action, regex),
                fb.Regex(x => x.Scope, regex),
                fb.Regex(x => x.Summary, regex),
                fb.Regex(x => x.WorkAutoCode, regex),
                fb.Regex(x => x.WorkCode, regex),
                fb.Regex(x => x.WorkName, regex),
                fb.Regex(x => x.WorkAssignmentCode, regex),
                fb.Regex(x => x.PeriodKey, regex),
                fb.Regex(x => x.PeriodInstanceKey, regex));
        }

        return filter;
    }

    private async Task<FilterDefinition<UserActionLog>> BuildViewerScopeFilterAsync(
        UserActionLogSearchRequest request,
        MeResponse me,
        CancellationToken ct)
    {
        var fb = Builders<UserActionLog>.Filter;

        if (RoleGuard.IsSystemAdmin(me))
        {
            var requestedUnit = NullIfWhiteSpace(request.UnitId);
            return requestedUnit is null
                ? FilterDefinition<UserActionLog>.Empty
                : fb.ElemMatch(x => x.UnitScopes, x => x.UnitId == requestedUnit);
        }

        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
        {
            var requestedUnit = NullIfWhiteSpace(request.UnitId);
            if (requestedUnit is not null &&
                !string.Equals(requestedUnit, managedUnitId, StringComparison.Ordinal))
            {
                throw AppExceptionFactory.Forbidden(AppErrorCode.OPERATIONS_ACTION_LOG_SCOPE_FORBIDDEN, new { requestedUnit, managedUnitId });
            }

            return fb.ElemMatch(x => x.UnitScopes, x => x.UnitId == managedUnitId);
        }

        if (RoleGuard.IsManagerLevel(me))
        {
            var scope = await ResolveManagerLevelScopeAsync(me, ct);
            var requestedUnitId = NullIfWhiteSpace(request.UnitId);

            if (requestedUnitId is not null)
            {
                var requestedUnit = await _ctx.Units
                    .Find(x => x.Id == requestedUnitId && !x.IsDeleted)
                    .Project(x => new { x.Id, x.Code, x.Level })
                    .FirstOrDefaultAsync(ct)
                    ?? throw AppExceptionFactory.NotFound(AppErrorCode.OPERATIONS_ACTION_LOG_UNIT_NOT_FOUND, new { unitId = requestedUnitId });

                EnsureManagerLevelScope(scope, requestedUnit.Code, requestedUnit.Level);
                return fb.ElemMatch(x => x.UnitScopes, x => x.UnitId == requestedUnit.Id);
            }

            if (scope.IsLevelWide)
            {
                return fb.ElemMatch(x => x.UnitScopes, x => x.UnitLevel >= scope.Level);
            }

            var unitScopeFb = Builders<UserActionLogUnitScope>.Filter;
            var escaped = Regex.Escape(scope.UnitCode ?? string.Empty);
            var unitScopeFilter = unitScopeFb.Gte(x => x.UnitLevel, scope.Level)
                                  & unitScopeFb.Regex(x => x.UnitCode, new BsonRegularExpression("^" + escaped));
            return fb.ElemMatch(x => x.UnitScopes, unitScopeFilter);
        }

        throw AppExceptionFactory.Forbidden(AppErrorCode.OPERATIONS_ACTION_LOG_ROLE_REQUIRED, new { me.Id, me.Roles });
    }

    private async Task<ManagerLevelScope> ResolveManagerLevelScopeAsync(MeResponse me, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(me.UnitId))
        {
            var meUnit = await _ctx.Units
                .Find(x => x.Id == me.UnitId)
                .Project(x => new { x.Code, x.Level })
                .FirstOrDefaultAsync(ct);

            if (meUnit is not null)
                return new ManagerLevelScope(meUnit.Code, meUnit.Level, IsLevelWide: false);
        }

        if (TryParseGeneratedLevelManager(me, out var generatedLevel))
            return new ManagerLevelScope(null, generatedLevel, IsLevelWide: true);

        throw AppExceptionFactory.NotFound(AppErrorCode.OPERATIONS_ACTION_LOG_UNIT_NOT_FOUND, new { unitId = me.UnitId, actorUserId = me.Id });
    }

    private static bool TryParseGeneratedLevelManager(MeResponse me, out int level)
        => RoleGuard.TryGetGeneratedLevelManager(me, out level);

    private static void EnsureManagerLevelScope(ManagerLevelScope scope, string? targetCode, int targetLevel)
    {
        if (targetLevel < scope.Level)
            throw AppExceptionFactory.Forbidden(AppErrorCode.OPERATIONS_ACTION_LOG_SCOPE_FORBIDDEN, new { reason = "upperLevel", scope.Level, targetLevel });

        if (!scope.IsLevelWide &&
            (string.IsNullOrWhiteSpace(scope.UnitCode) ||
             string.IsNullOrWhiteSpace(targetCode) ||
             !targetCode.StartsWith(scope.UnitCode, StringComparison.Ordinal)))
        {
            throw AppExceptionFactory.Forbidden(AppErrorCode.OPERATIONS_ACTION_LOG_SCOPE_FORBIDDEN, new { reason = "outsideManagerSubtree", scope.UnitCode, targetCode });
        }
    }

    private static UserActionLogRow ToRow(UserActionLog log)
        => new()
        {
            Id = log.Id,
            Action = log.Action,
            Scope = log.Scope,
            Result = log.Result,
            OccurredAtUtc = log.OccurredAtUtc,
            Actor = ToUserDto(log.Actor),
            TargetUser = ToUserDto(log.TargetUser),
            FromUser = ToUserDto(log.FromUser),
            ToUser = ToUserDto(log.ToUser),
            Users = (log.Users ?? new List<UserActionLogUserSnapshot>()).Select(ToUserDto).Where(x => x is not null).Select(x => x!).ToList(),
            UnitScopes = (log.UnitScopes ?? new List<UserActionLogUnitScope>()).Select(ToUnitDto).ToList(),
            WorkId = log.WorkId,
            WorkAutoCode = log.WorkAutoCode,
            WorkCode = log.WorkCode,
            WorkName = log.WorkName,
            WorkType = log.WorkType,
            WorkAssignmentId = log.WorkAssignmentId,
            WorkAssignmentCode = log.WorkAssignmentCode,
            DynamicFormTemplateId = log.DynamicFormTemplateId,
            DynamicFormTemplateCode = log.DynamicFormTemplateCode,
            DynamicFormTemplateName = log.DynamicFormTemplateName,
            WorkReportPeriodId = log.WorkReportPeriodId,
            PeriodKey = log.PeriodKey,
            PeriodInstanceKey = log.PeriodInstanceKey,
            PeriodStatus = log.PeriodStatus,
            WorkAssignmentReportId = log.WorkAssignmentReportId,
            ReportStatus = log.ReportStatus,
            Summary = log.Summary,
            Data = log.Data,
            CreatedAtUtc = log.CreatedAtUtc
        };

    private static UserActionLogUserDto? ToUserDto(UserActionLogUserSnapshot? x)
        => x is null
            ? null
            : new UserActionLogUserDto
            {
                UserId = x.UserId,
                Username = x.Username,
                FullName = x.FullName,
                UnitId = x.UnitId,
                UnitCode = x.UnitCode,
                UnitName = x.UnitName,
                UnitLevel = x.UnitLevel
            };

    private static UserActionLogUnitDto ToUnitDto(UserActionLogUnitScope x)
        => new()
        {
            UnitId = x.UnitId,
            UnitCode = x.UnitCode,
            UnitName = x.UnitName,
            UnitLevel = x.UnitLevel
        };

    private static UserActionLogRetryJobRow ToRetryRow(UserActionLogRetryJob x)
        => new()
        {
            Id = x.Id,
            DedupeKey = x.DedupeKey,
            Action = x.Action,
            Status = x.Status,
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

    private static FilterDefinition<UserActionLog> EqIfNotBlank(
        FilterDefinitionBuilder<UserActionLog> fb,
        System.Linq.Expressions.Expression<Func<UserActionLog, string?>> field,
        string? value)
    {
        value = NullIfWhiteSpace(value);
        return value is null ? FilterDefinition<UserActionLog>.Empty : fb.Eq(field, value);
    }

    private static FilterDefinition<UserActionLogRetryJob> EqIfNotBlank(
        FilterDefinitionBuilder<UserActionLogRetryJob> fb,
        System.Linq.Expressions.Expression<Func<UserActionLogRetryJob, string?>> field,
        string? value)
    {
        value = NullIfWhiteSpace(value);
        return value is null ? FilterDefinition<UserActionLogRetryJob>.Empty : fb.Eq(field, value);
    }

    private static void AddIfNotBlank(ISet<string> target, string? value)
    {
        value = NullIfWhiteSpace(value);
        if (value is not null)
            target.Add(value);
    }

    private static UserActionLogUserSnapshot? LookupUser(
        Dictionary<string, UserActionLogUserSnapshot> usersById,
        string? userId)
    {
        userId = NullIfWhiteSpace(userId);
        return userId is not null && usersById.TryGetValue(userId, out var user) ? user : null;
    }

    private static string BuildDedupeKey(UserActionLogSeed seed)
        => string.Join(
            ":",
            new[]
            {
                NormalizeUpper(seed.Action),
                NullIfWhiteSpace(seed.ActorUserId) ?? "_",
                NullIfWhiteSpace(seed.WorkId) ?? "_",
                NullIfWhiteSpace(seed.WorkAssignmentId) ?? "_",
                NullIfWhiteSpace(seed.WorkReportPeriodId) ?? "_",
                NullIfWhiteSpace(seed.WorkAssignmentReportId) ?? "_",
                AppTimeRangeHelper.ToUtc(seed.OccurredAtUtc)?.ToString("O") ?? "_"
            });

    private static string ResolveScope(string action)
    {
        action = NormalizeUpper(action);
        if (action.StartsWith("REPORT_", StringComparison.Ordinal)) return "report";
        if (action.StartsWith("ASSIGNMENT_", StringComparison.Ordinal)) return "assignment";
        if (action.StartsWith("WORK_", StringComparison.Ordinal)) return "work";
        return "workflow";
    }

    private static string NormalizeUpper(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? NormalizeUpperOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizeUpper(value);

    private static string NormalizeKebab(string value)
        => value.Trim().ToLowerInvariant().Replace('_', '-');

    private static string? NormalizeKebabOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizeKebab(value);

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private static string? BuildUnitName(Unit? unit)
        => unit is null
            ? null
            : NullIfWhiteSpace(unit.ShortName) ?? NullIfWhiteSpace(unit.Symbol) ?? NullIfWhiteSpace(unit.FullName);

    private sealed record ManagerLevelScope(string? UnitCode, int Level, bool IsLevelWide);
}
