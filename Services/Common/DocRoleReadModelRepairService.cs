using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.Common;

public interface IDocRoleReadModelRepairService
{
    Task<DocRoleReadModelRepairResult> RepairWorkAsync(
        string workId,
        DocRoleReadModelRepairOptions options,
        CancellationToken ct = default);

    Task<DocRoleReadModelRepairResult> RepairAssignmentAsync(
        string assignmentId,
        DocRoleReadModelRepairOptions options,
        CancellationToken ct = default);

    Task<DocRoleReadModelRepairResult> RepairPeriodAsync(
        string workReportPeriodId,
        DocRoleReadModelRepairOptions options,
        CancellationToken ct = default);
}

public sealed class DocRoleReadModelRepairOptions
{
    public bool DryRun { get; init; } = true;
    public int Limit { get; init; } = 100;
    public bool IncludeAssignments { get; init; }
    public bool IncludePeriods { get; init; }
    public string ByUserId { get; init; } = "system";
}

public sealed class DocRoleReadModelRepairResult
{
    public string Scope { get; init; } = string.Empty;
    public string TargetId { get; init; } = string.Empty;
    public bool SourceFound { get; set; }
    public bool DryRun { get; init; }
    public int Limit { get; init; }
    public bool IncludeAssignments { get; init; }
    public bool IncludePeriods { get; init; }
    public bool TruncatedAssignments { get; set; }
    public bool TruncatedPeriods { get; set; }
    public int PlannedWorkCount { get; set; }
    public int PlannedAssignmentCount { get; set; }
    public int PlannedPeriodCount { get; set; }
    public int RebuiltWorkCount { get; set; }
    public int RebuiltAssignmentCount { get; set; }
    public int RebuiltPeriodCount { get; set; }
    public long ExistingWorkListRowCount { get; set; }
    public long ExistingAssignmentListRowCount { get; set; }
    public long ExistingMyReportPeriodListRowCount { get; set; }
    public long ExistingReviewReportListRowCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Errors { get; } = new();
}

public sealed class DocRoleReadModelRepairService : IDocRoleReadModelRepairService
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly MongoDbContext _ctx;
    private readonly IDocRoleReadModelProjectionService _projection;
    private readonly ILogger<DocRoleReadModelRepairService> _log;

    public DocRoleReadModelRepairService(
        MongoDbContext ctx,
        IDocRoleReadModelProjectionService projection,
        ILogger<DocRoleReadModelRepairService> log)
    {
        _ctx = ctx;
        _projection = projection;
        _log = log;
    }

    public async Task<DocRoleReadModelRepairResult> RepairWorkAsync(
        string workId,
        DocRoleReadModelRepairOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workId))
            throw new ArgumentException("workId is required.", nameof(workId));

        var normalized = Normalize(options);
        var result = CreateResult("work", workId, normalized);

        _log.LogInformation(
            "DocRole read-model bounded repair started. scope=work workId={workId} dryRun={dryRun} limit={limit} includeAssignments={includeAssignments} includePeriods={includePeriods}",
            workId,
            normalized.DryRun,
            normalized.Limit,
            normalized.IncludeAssignments,
            normalized.IncludePeriods);

        result.SourceFound = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .AnyAsync(ct);

        result.PlannedWorkCount = 1;
        result.ExistingWorkListRowCount = await _ctx.WorkListDocRoles
            .CountDocumentsAsync(x => x.WorkId == workId && !x.IsDeleted, cancellationToken: ct);

        if (normalized.IncludeAssignments)
        {
            var assignmentFilter = Builders<WorkAssignment>.Filter.Eq(x => x.WorkId, workId)
                                   & Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false);

            var assignmentIds = await LoadBoundedAssignmentIdsAsync(
                assignmentFilter,
                normalized.Limit,
                result,
                ct);

            result.PlannedAssignmentCount = assignmentIds.Count;
            result.ExistingAssignmentListRowCount = await CountAssignmentRowsAsync(assignmentIds, ct);

            await RebuildAssignmentsAsync(assignmentIds, normalized, result, ct);
        }

        if (normalized.IncludePeriods)
        {
            var periodFilter = Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkId, workId)
                               & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsDeleted, false);

            var periodIds = await LoadBoundedPeriodIdsAsync(
                periodFilter,
                normalized.Limit,
                result,
                ct);

            result.PlannedPeriodCount = periodIds.Count;
            await CountPeriodRowsAsync(periodIds, result, ct);
            await RebuildPeriodsAsync(periodIds, normalized, result, ct);
        }

        await RebuildWorkAsync(workId, normalized, result, ct);
        LogCompleted(result);
        return result;
    }

    public async Task<DocRoleReadModelRepairResult> RepairAssignmentAsync(
        string assignmentId,
        DocRoleReadModelRepairOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assignmentId))
            throw new ArgumentException("assignmentId is required.", nameof(assignmentId));

        var normalized = Normalize(options);
        var result = CreateResult("assignment", assignmentId, normalized);

        _log.LogInformation(
            "DocRole read-model bounded repair started. scope=assignment assignmentId={assignmentId} dryRun={dryRun} limit={limit} includePeriods={includePeriods}",
            assignmentId,
            normalized.DryRun,
            normalized.Limit,
            normalized.IncludePeriods);

        result.SourceFound = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .AnyAsync(ct);

        result.PlannedAssignmentCount = 1;
        result.ExistingAssignmentListRowCount = await CountAssignmentRowsAsync(new[] { assignmentId }, ct);

        if (normalized.IncludePeriods)
        {
            var periodFilter = Builders<WorkReportPeriod>.Filter.Eq(x => x.WorkAssignmentId, assignmentId)
                               & Builders<WorkReportPeriod>.Filter.Eq(x => x.IsDeleted, false);

            var periodIds = await LoadBoundedPeriodIdsAsync(
                periodFilter,
                normalized.Limit,
                result,
                ct);

            result.PlannedPeriodCount = periodIds.Count;
            await CountPeriodRowsAsync(periodIds, result, ct);
            await RebuildPeriodsAsync(periodIds, normalized, result, ct);
        }

        await RebuildAssignmentsAsync(new[] { assignmentId }, normalized, result, ct);
        LogCompleted(result);
        return result;
    }

    public async Task<DocRoleReadModelRepairResult> RepairPeriodAsync(
        string workReportPeriodId,
        DocRoleReadModelRepairOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workReportPeriodId))
            throw new ArgumentException("workReportPeriodId is required.", nameof(workReportPeriodId));

        var normalized = Normalize(options);
        var result = CreateResult("period", workReportPeriodId, normalized);

        _log.LogInformation(
            "DocRole read-model bounded repair started. scope=period periodId={periodId} dryRun={dryRun}",
            workReportPeriodId,
            normalized.DryRun);

        result.SourceFound = await _ctx.WorkReportPeriods
            .Find(x => x.Id == workReportPeriodId && !x.IsDeleted)
            .AnyAsync(ct);

        result.PlannedPeriodCount = 1;
        await CountPeriodRowsAsync(new[] { workReportPeriodId }, result, ct);
        await RebuildPeriodsAsync(new[] { workReportPeriodId }, normalized, result, ct);
        LogCompleted(result);
        return result;
    }

    private async Task RebuildWorkAsync(
        string workId,
        DocRoleReadModelRepairOptions options,
        DocRoleReadModelRepairResult result,
        CancellationToken ct)
    {
        if (options.DryRun)
            return;

        try
        {
            await _projection.RebuildWorkAsync(workId, options.ByUserId, ct);
            result.RebuiltWorkCount++;
        }
        catch (Exception ex)
        {
            AddError(result, $"work:{workId}: {ex.Message}");
            _log.LogWarning(ex, "DocRole read-model bounded repair failed. scope=work workId={workId}", workId);
        }
    }

    private async Task RebuildAssignmentsAsync(
        IEnumerable<string> assignmentIds,
        DocRoleReadModelRepairOptions options,
        DocRoleReadModelRepairResult result,
        CancellationToken ct)
    {
        if (options.DryRun)
            return;

        foreach (var assignmentId in CleanIds(assignmentIds))
        {
            try
            {
                await _projection.RebuildAssignmentAsync(assignmentId, options.ByUserId, ct);
                result.RebuiltAssignmentCount++;
            }
            catch (Exception ex)
            {
                AddError(result, $"assignment:{assignmentId}: {ex.Message}");
                _log.LogWarning(
                    ex,
                    "DocRole read-model bounded repair failed. scope=assignment assignmentId={assignmentId}",
                    assignmentId);
            }
        }
    }

    private async Task RebuildPeriodsAsync(
        IEnumerable<string> periodIds,
        DocRoleReadModelRepairOptions options,
        DocRoleReadModelRepairResult result,
        CancellationToken ct)
    {
        if (options.DryRun)
            return;

        foreach (var periodId in CleanIds(periodIds))
        {
            try
            {
                await _projection.RebuildReportPeriodAsync(periodId, options.ByUserId, ct);
                result.RebuiltPeriodCount++;
            }
            catch (Exception ex)
            {
                AddError(result, $"period:{periodId}: {ex.Message}");
                _log.LogWarning(
                    ex,
                    "DocRole read-model bounded repair failed. scope=period periodId={periodId}",
                    periodId);
            }
        }
    }

    private async Task<List<string>> LoadBoundedAssignmentIdsAsync(
        FilterDefinition<WorkAssignment> filter,
        int limit,
        DocRoleReadModelRepairResult result,
        CancellationToken ct)
    {
        var ids = await _ctx.WorkAssignments
            .Find(filter)
            .SortBy(x => x.Id)
            .Limit(limit + 1)
            .Project(x => x.Id)
            .ToListAsync(ct);

        var clean = CleanIds(ids);
        if (clean.Count <= limit)
            return clean;

        result.TruncatedAssignments = true;
        return clean.Take(limit).ToList();
    }

    private async Task<List<string>> LoadBoundedPeriodIdsAsync(
        FilterDefinition<WorkReportPeriod> filter,
        int limit,
        DocRoleReadModelRepairResult result,
        CancellationToken ct)
    {
        var ids = await _ctx.WorkReportPeriods
            .Find(filter)
            .SortBy(x => x.Id)
            .Limit(limit + 1)
            .Project(x => x.Id)
            .ToListAsync(ct);

        var clean = CleanIds(ids);
        if (clean.Count <= limit)
            return clean;

        result.TruncatedPeriods = true;
        return clean.Take(limit).ToList();
    }

    private async Task<long> CountAssignmentRowsAsync(IEnumerable<string> assignmentIds, CancellationToken ct)
    {
        var ids = CleanIds(assignmentIds);
        if (ids.Count == 0)
            return 0;

        return await _ctx.AssignmentListDocRoles
            .CountDocumentsAsync(x => ids.Contains(x.AssignmentId) && !x.IsDeleted, cancellationToken: ct);
    }

    private async Task CountPeriodRowsAsync(
        IEnumerable<string> periodIds,
        DocRoleReadModelRepairResult result,
        CancellationToken ct)
    {
        var ids = CleanIds(periodIds);
        if (ids.Count == 0)
            return;

        result.ExistingMyReportPeriodListRowCount = await _ctx.MyReportPeriodListDocRoles
            .CountDocumentsAsync(x => ids.Contains(x.WorkReportPeriodId) && !x.IsDeleted, cancellationToken: ct);

        result.ExistingReviewReportListRowCount = await _ctx.ReviewReportListDocRoles
            .CountDocumentsAsync(x => ids.Contains(x.WorkReportPeriodId) && !x.IsDeleted, cancellationToken: ct);
    }

    private static DocRoleReadModelRepairOptions Normalize(DocRoleReadModelRepairOptions? options)
    {
        options ??= new DocRoleReadModelRepairOptions();
        var limit = options.Limit <= 0 ? DefaultLimit : Math.Min(options.Limit, MaxLimit);

        return new DocRoleReadModelRepairOptions
        {
            DryRun = options.DryRun,
            Limit = limit,
            IncludeAssignments = options.IncludeAssignments,
            IncludePeriods = options.IncludePeriods,
            ByUserId = string.IsNullOrWhiteSpace(options.ByUserId) ? "system" : options.ByUserId
        };
    }

    private static DocRoleReadModelRepairResult CreateResult(
        string scope,
        string targetId,
        DocRoleReadModelRepairOptions options)
    {
        return new DocRoleReadModelRepairResult
        {
            Scope = scope,
            TargetId = targetId,
            DryRun = options.DryRun,
            Limit = options.Limit,
            IncludeAssignments = options.IncludeAssignments,
            IncludePeriods = options.IncludePeriods
        };
    }

    private void LogCompleted(DocRoleReadModelRepairResult result)
    {
        _log.LogInformation(
            "DocRole read-model bounded repair completed. scope={scope} targetId={targetId} dryRun={dryRun} plannedWork={plannedWork} plannedAssignments={plannedAssignments} plannedPeriods={plannedPeriods} rebuiltWork={rebuiltWork} rebuiltAssignments={rebuiltAssignments} rebuiltPeriods={rebuiltPeriods} failed={failed} truncatedAssignments={truncatedAssignments} truncatedPeriods={truncatedPeriods}",
            result.Scope,
            result.TargetId,
            result.DryRun,
            result.PlannedWorkCount,
            result.PlannedAssignmentCount,
            result.PlannedPeriodCount,
            result.RebuiltWorkCount,
            result.RebuiltAssignmentCount,
            result.RebuiltPeriodCount,
            result.FailedCount,
            result.TruncatedAssignments,
            result.TruncatedPeriods);
    }

    private static List<string> CleanIds(IEnumerable<string?> ids)
    {
        return ids
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void AddError(DocRoleReadModelRepairResult result, string error)
    {
        result.FailedCount++;
        if (result.Errors.Count < 20)
            result.Errors.Add(error);
    }
}
