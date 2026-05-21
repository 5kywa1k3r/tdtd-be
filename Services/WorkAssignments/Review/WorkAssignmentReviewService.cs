using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Operations;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Statistics;
using tdtd_be.Services.WorkAssignments.Domain;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Queue;
using tdtd_be.Services.WorkAssignments.Runtime;

namespace tdtd_be.Services.WorkAssignments.Review;

public sealed class WorkAssignmentReviewService : IWorkAssignmentReviewService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentQueueService _queueService;
    private readonly IWorkAssignmentStatusSyncService _statusSync;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly IUserActionLogService _userActionLog;
    private readonly IWorkReportLabelStatisticsService _labelStatistics;
    private readonly IWorkReportTableStatisticsService _tableStatistics;
    private readonly IWorkReportFieldStatisticsService _fieldStatistics;
    private readonly IWorkAssignmentReportService _reportService;
    private readonly MeAccessor _me;
    private readonly ILogger<WorkAssignmentReviewService> _log;

    public WorkAssignmentReviewService(
        MongoDbContext ctx,
        IWorkAssignmentQueueService queueService,
        IWorkAssignmentStatusSyncService statusSync,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkStatusOperationLogService statusLog,
        IUserActionLogService userActionLog,
        IWorkReportLabelStatisticsService labelStatistics,
        IWorkReportTableStatisticsService tableStatistics,
        IWorkReportFieldStatisticsService fieldStatistics,
        IWorkAssignmentReportService reportService,
        MeAccessor me,
        ILogger<WorkAssignmentReviewService> log)
    {
        _ctx = ctx;
        _queueService = queueService;
        _statusSync = statusSync;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _statusLog = statusLog;
        _userActionLog = userActionLog;
        _labelStatistics = labelStatistics;
        _tableStatistics = tableStatistics;
        _fieldStatistics = fieldStatistics;
        _reportService = reportService;
        _me = me;
        _log = log;
    }

    public async Task<PagedResult<ReviewChildRowDto>> SearchChildrenForReviewAsync(
        ReviewChildSearchRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        var page = req.Page < 0 ? 0 : req.Page;
        var pageSize = req.PageSize <= 0 ? 20 : req.PageSize;

        var parent = await LoadParentForReviewAsync(req.ParentAssignmentId, me.Id, ct);
        await EnsureReviewChildAssignmentListDocRolesAsync(parent.Id, me.Id, ct);
        await EnsureReviewReportListDocRolesForUserWorkAsync(parent.WorkId, me.Id, ct);

        var fb = Builders<AssignmentListDocRole>.Filter;
        var filter = fb.Eq(x => x.UserId, me.Id)
                     & fb.Eq(x => x.WorkId, parent.WorkId)
                     & fb.Eq(x => x.ParentAssignmentId, parent.Id)
                     & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.Q))
            filter &= BuildReviewChildListTextFilter(req.Q, fb);

        if (req.ProgressStatus.HasValue)
            filter &= fb.Eq(x => x.ProgressStatus, req.ProgressStatus.Value);

        if (req.HasOverdueOnly == true)
            filter &= fb.Eq(x => x.HasOverduePeriod, true);

        var total = await _ctx.AssignmentListDocRoles.CountDocumentsAsync(filter, cancellationToken: ct);

        var children = await _ctx.AssignmentListDocRoles.Find(filter)
            .SortBy(x => x.FirstAssigneeUnitName)
            .ThenBy(x => x.FirstAssigneeName)
            .ThenBy(x => x.DynamicExcelCode)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var childIds = children.Select(x => x.AssignmentId).ToList();
        var periodKeys = children
            .Where(x => !string.IsNullOrWhiteSpace(x.LatestPeriodKey))
            .Select(x => x.LatestPeriodKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var reports = (childIds.Count == 0 || periodKeys.Count == 0)
            ? new List<ReviewReportListDocRole>()
            : await _ctx.ReviewReportListDocRoles
                .Find(x =>
                    x.ReviewerUserId == me.Id &&
                    childIds.Contains(x.AssignmentId) &&
                    periodKeys.Contains(x.PeriodKey) &&
                    !x.IsDeleted)
                .ToListAsync(ct);

        var latestReportMap = reports
            .GroupBy(x => new { x.AssignmentId, x.PeriodKey })
            .ToDictionary(
                g => $"{g.Key.AssignmentId}__{g.Key.PeriodKey}",
                g => g.OrderByDescending(x => x.SortUpdatedAtUtc).First(),
                StringComparer.Ordinal);

        var rows = children
            .Select(x => new
            {
                Assignment = x,
                FirstAssignee = x.Assignees?.FirstOrDefault()
            })
            .OrderBy(x => x.FirstAssignee?.UnitShortName ?? string.Empty)
            .ThenBy(x => x.FirstAssignee?.FullName ?? string.Empty)
            .Select(x =>
            {
                var assignment = x.Assignment;
                var firstAssignee = x.FirstAssignee;

                ReviewReportListDocRole? currentReport = null;
                if (!string.IsNullOrWhiteSpace(assignment.LatestPeriodKey))
                {
                    latestReportMap.TryGetValue(
                        $"{assignment.AssignmentId}__{assignment.LatestPeriodKey}",
                        out currentReport);
                }

                return new ReviewChildRowDto
                {
                    WorkAssignmentId = assignment.AssignmentId,
                    ParentId = assignment.ParentAssignmentId ?? string.Empty,

                    DynamicExcelId = assignment.DynamicExcelId,
                    DynamicExcelCode = assignment.DynamicExcelCode,
                    DynamicExcelName = assignment.DynamicExcelName,

                    AssigneeUserId = firstAssignee?.UserId ?? string.Empty,
                    AssigneeName = firstAssignee?.FullName ?? string.Empty,
                    UnitId = firstAssignee?.UnitId,
                    UnitName = firstAssignee?.UnitName,

                    ProgressStatus = assignment.ProgressStatus,
                    ProgressStatusText = ToProgressStatusText(assignment.ProgressStatus),

                    HasAnyDuePeriod = assignment.HasAnyDuePeriod,
                    HasOverduePeriod = assignment.HasOverduePeriod,
                    LatestPeriodKey = assignment.LatestPeriodKey,
                    LatestDueAtUtc = assignment.LatestDueAtUtc,

                    EvaluationCode = null,
                    EvaluationLabel = null,
                    WorstPeriodStatus = assignment.WorstPeriodStatus,
                    WorstOverdueReasonCode = assignment.WorstOverdueReasonCode,
                    WorstOverdueReasonLabel = assignment.WorstOverdueReasonLabel,

                    CurrentReportId = currentReport?.CurrentReportId,
                    CurrentReportStatus = currentReport?.ReportStatus == null ? null : (int?)currentReport.ReportStatus.Value,
                    CurrentSubmittedAtUtc = currentReport?.SubmittedAtUtc,
                    CurrentApprovedAtUtc = currentReport?.ApprovedAtUtc
                };
            })
            .ToList();

        return new PagedResult<ReviewChildRowDto>(rows, total, page, pageSize);
    }

    private async Task EnsureReviewChildAssignmentListDocRolesAsync(
        string parentAssignmentId,
        string reviewerUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parentAssignmentId) || string.IsNullOrWhiteSpace(reviewerUserId))
            return;

        var hasProjectedRows = await _ctx.AssignmentListDocRoles
            .Find(x =>
                x.ParentAssignmentId == parentAssignmentId &&
                x.UserId == reviewerUserId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (hasProjectedRows)
            return;

        _log.LogWarning(
            "Review child assignment projection missing. parentAssignmentId={parentAssignmentId} reviewerUserId={reviewerUserId}. Returning current projection only; run internal DocRole repair/backfill if source data exists.",
            parentAssignmentId,
            reviewerUserId);
    }

    private static FilterDefinition<AssignmentListDocRole> BuildReviewChildListTextFilter(
        string q,
        FilterDefinitionBuilder<AssignmentListDocRole> fb)
    {
        var qRegex = new BsonRegularExpression(q.Trim(), "i");

        return fb.Or(
            fb.Regex(x => x.DynamicExcelCode, qRegex),
            fb.Regex(x => x.DynamicExcelName, qRegex),
            fb.Regex("assignees.fullName", qRegex),
            fb.Regex("assignees.unitName", qRegex),
            fb.Regex("assignees.unitShortName", qRegex),
            fb.Regex("assignees.userId", qRegex));
    }

    public async Task ApproveReportAsync(string reportId, ApproveReportRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        req ??= new ApproveReportRequest();

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportNotFound(reportId);

        EnsureReportIsActive(report);
        EnsureNotSelfReview(report, me.Id);

        var confirmsAutoApproval = report.Status == WorkAssignmentReportStatus.Approved &&
                                   WorkAssignmentAutoApprovalState.CanReporterWithdraw(report);

        if (report.Status != WorkAssignmentReportStatus.Submitted && !confirmsAutoApproval)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_APPROVE_STATUS_INVALID,
                report,
                WorkAssignmentReportStatus.Submitted);

        WorkReportPayloadConsistency.EnsureReadyForStatisticProjection(report);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(report);

        await EnsureCanReviewReportAsync(assignment, me.Id, ct);

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        await EnsurePreviousReportsApprovedAsync(period, ct);

        var now = DateTime.UtcNow;
        if (confirmsAutoApproval)
        {
            var reviewerComment = string.IsNullOrWhiteSpace(req.Comment)
                ? report.ReviewerComment
                : req.Comment.Trim();

            await _ctx.WorkAssignmentReports.UpdateOneAsync(
                x => x.Id == report.Id,
                Builders<WorkAssignmentReport>.Update
                    .Set(x => x.ReviewerComment, reviewerComment)
                    .Set(x => x.ApprovedAtUtc, now)
                    .Set(x => x.ApprovedByUserId, me.Id)
                    .Set(x => x.AutoApprovalConfirmedAtUtc, now)
                    .Set(x => x.AutoApprovalConfirmedByUserId, me.Id)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, me.Id),
                cancellationToken: ct);

            report.ReviewerComment = reviewerComment;
            report.ApprovedAtUtc = now;
            report.ApprovedByUserId = me.Id;
            report.AutoApprovalConfirmedAtUtc = now;
            report.AutoApprovalConfirmedByUserId = me.Id;
            report.UpdatedAtUtc = now;
            report.UpdatedByUserId = me.Id;

            if (period is not null)
            {
                await _ctx.WorkReportPeriods.UpdateOneAsync(
                    x => x.Id == period.Id && !x.IsDeleted,
                    Builders<WorkReportPeriod>.Update
                        .Set(x => x.LastReviewedAtUtc, now)
                        .Set(x => x.ReviewerComment, reviewerComment)
                        .Set(x => x.UpdatedAtUtc, now)
                        .Set(x => x.UpdatedByUserId, me.Id),
                    cancellationToken: ct);

                period.LastReviewedAtUtc = now;
                period.ReviewerComment = reviewerComment;
                period.UpdatedAtUtc = now;
                period.UpdatedByUserId = me.Id;

                await FinalizeReviewReportStatusOperationAsync(
                    "REVIEW_CONFIRM_AUTO_APPROVE",
                    report,
                    period,
                    WorkAssignmentReportStatus.Approved.ToString(),
                    WorkAssignmentReportStatus.Approved.ToString(),
                    me.Id,
                    upsertQueue: false,
                    disableQueue: true,
                    ct);
            }

            await InsertReportLogAsync(
                report.WorkId,
                report.WorkAssignmentId,
                report.WorkReportPeriodId,
                report.Id,
                "Xác nhận tự duyệt",
                WorkAssignmentReportStatus.Approved.ToString(),
                WorkAssignmentReportStatus.Approved.ToString(),
                me.Id,
                "AUTO_APPROVE_CONFIRMED",
                reviewerComment,
                ct);

            await _userActionLog.RecordAsync(new UserActionLogSeed
            {
                Action = UserActionLogActions.ReportApproved,
                Scope = "report",
                ActorUserId = me.Id,
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                TargetUserId = report.AssigneeUserId,
                Summary = $"Confirmed auto approved report {report.PeriodInstanceKey}",
                Data = new Dictionary<string, string>
                {
                    { "fromStatus", WorkAssignmentReportStatus.Approved.ToString() },
                    { "toStatus", WorkAssignmentReportStatus.Approved.ToString() },
                    { "autoApprovalConfirmed", true.ToString() }
                },
                OccurredAtUtc = now
            }, CancellationToken.None);

            return;
        }

        var isHistoricalApproval = report.IsHistoricalData;
        var confirmsPreviouslyAutoApprovedReport = WorkAssignmentAutoApprovalState.IsAutoApproved(report);

        if (isHistoricalApproval && !req.ConfirmHistoricalDataApproval)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_HISTORICAL_APPROVAL_CONFIRMATION_REQUIRED,
                new { reportId = report.Id, workReportPeriodId = report.WorkReportPeriodId });

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == report.Id,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Approved)
                .Set(x => x.ReviewerComment, req.Comment)
                .Set(x => x.HistoricalDataApproved, isHistoricalApproval ? true : report.HistoricalDataApproved)
                .Set(x => x.HistoricalDataApprovedAtUtc, isHistoricalApproval ? now : report.HistoricalDataApprovedAtUtc)
                .Set(x => x.HistoricalDataApprovedByUserId, isHistoricalApproval ? me.Id : report.HistoricalDataApprovedByUserId)
                .Set(x => x.ApprovedAtUtc, now)
                .Set(x => x.ApprovedByUserId, me.Id)
                .Set(x => x.AutoApprovalConfirmedAtUtc, confirmsPreviouslyAutoApprovedReport ? now : report.AutoApprovalConfirmedAtUtc)
                .Set(x => x.AutoApprovalConfirmedByUserId, confirmsPreviouslyAutoApprovedReport ? me.Id : report.AutoApprovalConfirmedByUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        report.Status = WorkAssignmentReportStatus.Approved;
        report.ReviewerComment = req.Comment;
        if (isHistoricalApproval)
        {
            report.HistoricalDataApproved = true;
            report.HistoricalDataApprovedAtUtc = now;
            report.HistoricalDataApprovedByUserId = me.Id;
        }
        report.ApprovedAtUtc = now;
        report.ApprovedByUserId = me.Id;
        if (confirmsPreviouslyAutoApprovedReport)
        {
            report.AutoApprovalConfirmedAtUtc = now;
            report.AutoApprovalConfirmedByUserId = me.Id;
        }
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = me.Id;

        if (period is not null)
        {
            var nextPeriodStatus = ResolveApprovedPeriodStatus(period, report, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, nextPeriodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                    .Set(x => x.LastReviewedAtUtc, now)
                    .Set(x => x.ReviewerComment, req.Comment)
                    .Set(x => x.HistoricalDataApproved, isHistoricalApproval ? true : period.HistoricalDataApproved)
                    .Set(x => x.HistoricalDataApprovedAtUtc, isHistoricalApproval ? now : period.HistoricalDataApprovedAtUtc)
                    .Set(x => x.HistoricalDataApprovedByUserId, isHistoricalApproval ? me.Id : period.HistoricalDataApprovedByUserId)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, me.Id),
                cancellationToken: ct);

            period.Status = nextPeriodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
            period.LastReviewedAtUtc = now;
            period.ReviewerComment = req.Comment;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = me.Id;

            await FinalizeReviewReportStatusOperationAsync(
                "REVIEW_APPROVE",
                report,
                period,
                WorkAssignmentReportStatus.Submitted.ToString(),
                WorkAssignmentReportStatus.Approved.ToString(),
                me.Id,
                upsertQueue: false,
                disableQueue: true,
                ct);
        }

        await InsertReportLogAsync(
            report.WorkId,
            report.WorkAssignmentId,
            report.WorkReportPeriodId,
            report.Id,
            "Duyệt",
            WorkAssignmentReportStatus.Submitted.ToString(),
            WorkAssignmentReportStatus.Approved.ToString(),
            me.Id,
            null,
            req.Comment,
            ct);

        WorkAssignmentReportLogHelper.AppendApproveLog(report, me.Id, me.FullName ?? string.Empty);

        await _userActionLog.RecordAsync(new UserActionLogSeed
        {
            Action = UserActionLogActions.ReportApproved,
            Scope = "report",
            ActorUserId = me.Id,
            WorkId = report.WorkId,
            WorkAssignmentId = report.WorkAssignmentId,
            WorkReportPeriodId = report.WorkReportPeriodId,
            WorkAssignmentReportId = report.Id,
            TargetUserId = report.AssigneeUserId,
            Summary = $"Approved report {report.PeriodInstanceKey}",
            Data = new Dictionary<string, string>
            {
                { "fromStatus", WorkAssignmentReportStatus.Submitted.ToString() },
                { "toStatus", WorkAssignmentReportStatus.Approved.ToString() }
            },
            OccurredAtUtc = now
        }, CancellationToken.None);

        await RefreshAggregateDependentsAfterReviewAsync(report.Id, me.Id, ct);
    }

    public async Task ReturnReportAsync(string reportId, ReturnReportRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();

        if (string.IsNullOrWhiteSpace(req.Comment))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_RETURN_COMMENT_REQUIRED);

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportNotFound(reportId);

        EnsureReportIsActive(report);
        EnsureNotSelfReview(report, me.Id);

        if (report.Status != WorkAssignmentReportStatus.Submitted)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_RETURN_STATUS_INVALID,
                report,
                WorkAssignmentReportStatus.Submitted);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(report);

        await EnsureCanReviewReportAsync(assignment, me.Id, ct);

        var now = DateTime.UtcNow;

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == report.Id,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Draft)
                .Set(x => x.ReturnReason, req.Comment)
                .Set(x => x.ReturnedAtUtc, now)
                .Set(x => x.ReturnedByUserId, me.Id)
                .Set(x => x.ApprovedAtUtc, (DateTime?)null)
                .Set(x => x.ApprovedByUserId, (string?)null)
                .Set(x => x.AutoApprovedAtUtc, (DateTime?)null)
                .Set(x => x.AutoApprovedByUserId, (string?)null)
                .Set(x => x.AutoApproveConditionSnapshotJson, (string?)null)
                .Set(x => x.AutoApprovalConfirmedAtUtc, (DateTime?)null)
                .Set(x => x.AutoApprovalConfirmedByUserId, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        report.Status = WorkAssignmentReportStatus.Draft;
        report.ReturnReason = req.Comment;
        report.ReturnedAtUtc = now;
        report.ReturnedByUserId = me.Id;
        report.ApprovedAtUtc = null;
        report.ApprovedByUserId = null;
        report.AutoApprovedAtUtc = null;
        report.AutoApprovedByUserId = null;
        report.AutoApproveConditionSnapshotJson = null;
        report.AutoApprovalConfirmedAtUtc = null;
        report.AutoApprovalConfirmedByUserId = null;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = me.Id;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (period is not null)
        {
            var periodStatus = ResolveDraftPeriodStatus(period, report, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, periodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(periodStatus))
                    .Set(x => x.LastReviewedAtUtc, now)
                    .Set(x => x.ReturnReason, req.Comment)
                    .Set(x => x.ReviewerComment, req.Comment)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, me.Id),
                cancellationToken: ct);

            period.Status = periodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(periodStatus);
            period.LastReviewedAtUtc = now;
            period.ReturnReason = req.Comment;
            period.ReviewerComment = req.Comment;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = me.Id;

            await FinalizeReviewReportStatusOperationAsync(
                "REVIEW_RETURN",
                report,
                period,
                WorkAssignmentReportStatus.Submitted.ToString(),
                WorkAssignmentReportStatus.Draft.ToString(),
                me.Id,
                upsertQueue: true,
                disableQueue: false,
                ct);
        }

        await InsertReportLogAsync(
            report.WorkId,
            report.WorkAssignmentId,
            report.WorkReportPeriodId,
            report.Id,
            "Trả lại",
            WorkAssignmentReportStatus.Submitted.ToString(),
            WorkAssignmentReportStatus.Draft.ToString(),
            me.Id,
            req.Comment,
            req.Comment,
            ct);

        WorkAssignmentReportLogHelper.AppendReturnLog(report, me.Id, me.FullName ?? string.Empty, req.Comment);

        await _userActionLog.RecordAsync(new UserActionLogSeed
        {
            Action = UserActionLogActions.ReportReturned,
            Scope = "report",
            ActorUserId = me.Id,
            WorkId = report.WorkId,
            WorkAssignmentId = report.WorkAssignmentId,
            WorkReportPeriodId = report.WorkReportPeriodId,
            WorkAssignmentReportId = report.Id,
            TargetUserId = report.AssigneeUserId,
            Summary = $"Returned report {report.PeriodInstanceKey}",
            Data = new Dictionary<string, string>
            {
                { "fromStatus", WorkAssignmentReportStatus.Submitted.ToString() },
                { "toStatus", WorkAssignmentReportStatus.Draft.ToString() }
            },
            OccurredAtUtc = now
        }, CancellationToken.None);
    }

    public async Task RecallApprovedReportAsync(string reportId, ReturnReportRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();

        if (string.IsNullOrWhiteSpace(req.Comment))
            throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_REPORT_RECALL_COMMENT_REQUIRED);

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportNotFound(reportId);

        if (report.Status != WorkAssignmentReportStatus.Approved)
            throw InvalidReportStatus(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_RECALL_STATUS_INVALID,
                report,
                WorkAssignmentReportStatus.Approved);

        EnsureReportIsActive(report);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(report);

        await EnsureCanReviewReportAsync(assignment, me.Id, ct);

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        await EnsureNoLaterApprovedReportsAsync(period, ct);

        var now = DateTime.UtcNow;
        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == report.Id,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.Status, WorkAssignmentReportStatus.Submitted)
                .Set(x => x.ReturnReason, req.Comment)
                .Set(x => x.ReviewerComment, req.Comment)
                .Set(x => x.ApprovedAtUtc, (DateTime?)null)
                .Set(x => x.ApprovedByUserId, (string?)null)
                .Set(x => x.AutoApprovalConfirmedAtUtc, (DateTime?)null)
                .Set(x => x.AutoApprovalConfirmedByUserId, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        report.Status = WorkAssignmentReportStatus.Submitted;
        report.ReturnReason = req.Comment;
        report.ReviewerComment = req.Comment;
        report.ApprovedAtUtc = null;
        report.ApprovedByUserId = null;
        report.AutoApprovalConfirmedAtUtc = null;
        report.AutoApprovalConfirmedByUserId = null;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = me.Id;

        if (period is not null)
        {
            var nextPeriodStatus = ResolveSubmittedPeriodStatus(period, report, now);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                Builders<WorkReportPeriod>.Update
                    .Set(x => x.Status, nextPeriodStatus)
                    .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                    .Set(x => x.LastReviewedAtUtc, now)
                    .Set(x => x.ReturnReason, req.Comment)
                    .Set(x => x.ReviewerComment, req.Comment)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, me.Id),
                cancellationToken: ct);

            period.Status = nextPeriodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
            period.LastReviewedAtUtc = now;
            period.ReturnReason = req.Comment;
            period.ReviewerComment = req.Comment;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = me.Id;

            await FinalizeReviewReportStatusOperationAsync(
                "REVIEW_RECALL_APPROVED",
                report,
                period,
                WorkAssignmentReportStatus.Approved.ToString(),
                WorkAssignmentReportStatus.Submitted.ToString(),
                me.Id,
                upsertQueue: true,
                disableQueue: false,
                ct);
        }

        await InsertReportLogAsync(
            report.WorkId,
            report.WorkAssignmentId,
            report.WorkReportPeriodId,
            report.Id,
            "Thu hồi duyệt",
            WorkAssignmentReportStatus.Approved.ToString(),
            WorkAssignmentReportStatus.Submitted.ToString(),
            me.Id,
            req.Comment,
            req.Comment,
            ct);

        await RefreshAggregateDependentsAfterReviewAsync(report.Id, me.Id, ct);
    }

    public async Task DeactivateReportAsync(string reportId, ReportActiveRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        req ??= new ReportActiveRequest();

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportNotFound(reportId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(report);

        await EnsureCanReviewReportAsync(assignment, me.Id, ct);

        if (report.IsActive == false)
            return;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (report.Status == WorkAssignmentReportStatus.Approved)
            await EnsureNoLaterApprovedReportsAsync(period, ct);

        var now = DateTime.UtcNow;
        var comment = NormalizeOptionalText(req.Comment);
        var wasCurrent = report.IsCurrent || string.Equals(period?.CurrentReportId, report.Id, StringComparison.Ordinal);

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == report.Id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.IsCurrent, false)
                .Set(x => x.DeactivatedAtUtc, now)
                .Set(x => x.DeactivatedByUserId, me.Id)
                .Set(x => x.DeactivationReason, comment)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        report.IsActive = false;
        report.IsCurrent = false;
        report.DeactivatedAtUtc = now;
        report.DeactivatedByUserId = me.Id;
        report.DeactivationReason = comment;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = me.Id;

        if (period is not null && wasCurrent)
        {
            var nextPeriodStatus = WorkReportPeriodStatusHelper.ResolveInitialStatus(period.DueAtUtc, now);
            var deactivatePeriod = WorkReportPeriodKind.IsUserCreated(period.PeriodKind);
            var update = Builders<WorkReportPeriod>.Update
                .Set(x => x.CurrentReportId, (string?)null)
                .Set(x => x.Status, nextPeriodStatus)
                .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                .Set(x => x.LastDraftSavedAtUtc, (DateTime?)null)
                .Set(x => x.LastSubmittedAtUtc, (DateTime?)null)
                .Set(x => x.LastReviewedAtUtc, (DateTime?)null)
                .Set(x => x.CurrentProgressStatus, null)
                .Set(x => x.ReportReason, null)
                .Set(x => x.Difficulties, null)
                .Set(x => x.ProposedSolution, null)
                .Set(x => x.LateReason, null)
                .Set(x => x.ReviewerComment, null)
                .Set(x => x.ReturnReason, null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id);

            if (deactivatePeriod)
                update = update.Set(x => x.IsActive, false);

            await _ctx.WorkReportPeriods.UpdateOneAsync(
                x => x.Id == period.Id && !x.IsDeleted,
                update,
                cancellationToken: ct);

            period.CurrentReportId = null;
            period.Status = nextPeriodStatus;
            period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
            period.LastDraftSavedAtUtc = null;
            period.LastSubmittedAtUtc = null;
            period.LastReviewedAtUtc = null;
            period.CurrentProgressStatus = null;
            period.ReportReason = null;
            period.Difficulties = null;
            period.ProposedSolution = null;
            period.LateReason = null;
            period.ReviewerComment = null;
            period.ReturnReason = null;
            period.IsActive = !deactivatePeriod;
            period.UpdatedAtUtc = now;
            period.UpdatedByUserId = me.Id;
        }

        if (period is not null)
        {
            await FinalizeReviewReportStatusOperationAsync(
                "REVIEW_DEACTIVATE_REPORT",
                report,
                period,
                "ACTIVE",
                "INACTIVE",
                me.Id,
                upsertQueue: period.IsActive && WorkReportPeriodStatusHelper.ShouldKeepQueueActive(period.Status),
                disableQueue: !period.IsActive || !WorkReportPeriodStatusHelper.ShouldKeepQueueActive(period.Status),
                ct,
                forceRebuildStatistics: true);
        }
        else
        {
            await RebuildReportStatisticsAsync(report, me.Id, ct);
            await _statusSync.SyncFromAssignmentAsync(report.WorkAssignmentId, ct);
        }

        await RefreshAggregateDependentsAfterReviewAsync(report.Id, me.Id, ct);

        await InsertReportLogAsync(
            report.WorkId,
            report.WorkAssignmentId,
            report.WorkReportPeriodId,
            report.Id,
            "Deactivate",
            "ACTIVE",
            "INACTIVE",
            me.Id,
            comment,
            comment,
            ct);

        await _userActionLog.RecordAsync(new UserActionLogSeed
        {
            Action = UserActionLogActions.ReportDeactivated,
            Scope = "report",
            ActorUserId = me.Id,
            WorkId = report.WorkId,
            WorkAssignmentId = report.WorkAssignmentId,
            WorkReportPeriodId = report.WorkReportPeriodId,
            WorkAssignmentReportId = report.Id,
            TargetUserId = report.AssigneeUserId,
            Summary = $"Deactivated report {report.PeriodInstanceKey}",
            Data = new Dictionary<string, string>
            {
                { "fromActive", "true" },
                { "toActive", "false" }
            },
            OccurredAtUtc = now
        }, CancellationToken.None);
    }

    public async Task ReactivateReportAsync(string reportId, ReportActiveRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        req ??= new ReportActiveRequest();

        var report = await _ctx.WorkAssignmentReports
            .Find(x => x.Id == reportId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportNotFound(reportId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportAssignmentNotFound(report);

        await EnsureCanReviewReportAsync(assignment, me.Id, ct);

        if (report.IsActive != false)
            return;

        var period = await _ctx.WorkReportPeriods
            .Find(x => x.Id == report.WorkReportPeriodId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw ReportPeriodNotFound(report);

        if (report.Status == WorkAssignmentReportStatus.Approved)
            await EnsurePreviousReportsApprovedAsync(period, ct);

        var fb = Builders<WorkAssignmentReport>.Filter;
        var activeCurrentConflict = await _ctx.WorkAssignmentReports
            .Find(fb.Eq(x => x.WorkAssignmentId, report.WorkAssignmentId)
                  & fb.Eq(x => x.AssigneeUserId, report.AssigneeUserId)
                  & fb.Eq(x => x.PeriodInstanceKey, report.PeriodInstanceKey)
                  & fb.Eq(x => x.IsCurrent, true)
                  & fb.Ne(x => x.IsActive, false)
                  & fb.Eq(x => x.IsDeleted, false)
                  & fb.Ne(x => x.Id, report.Id))
            .AnyAsync(ct);

        if (activeCurrentConflict)
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_CURRENT_CONFLICT,
                ReportDetails(report));

        var now = DateTime.UtcNow;
        var comment = NormalizeOptionalText(req.Comment);
        var nextPeriodStatus = ResolvePeriodStatusFromReport(period, report, now);

        await _ctx.WorkAssignmentReports.UpdateOneAsync(
            x => x.Id == report.Id && !x.IsDeleted,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.IsActive, true)
                .Set(x => x.IsCurrent, true)
                .Set(x => x.ReactivatedAtUtc, now)
                .Set(x => x.ReactivatedByUserId, me.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        report.IsActive = true;
        report.IsCurrent = true;
        report.ReactivatedAtUtc = now;
        report.ReactivatedByUserId = me.Id;
        report.UpdatedAtUtc = now;
        report.UpdatedByUserId = me.Id;

        await _ctx.WorkReportPeriods.UpdateOneAsync(
            x => x.Id == period.Id && !x.IsDeleted,
            Builders<WorkReportPeriod>.Update
                .Set(x => x.IsActive, true)
                .Set(x => x.CurrentReportId, report.Id)
                .Set(x => x.Status, nextPeriodStatus)
                .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus))
                .Set(x => x.LastDraftSavedAtUtc, report.Status == WorkAssignmentReportStatus.Draft ? (DateTime?)report.UpdatedAtUtc : null)
                .Set(x => x.LastSubmittedAtUtc, report.SubmittedAtUtc)
                .Set(x => x.LastReviewedAtUtc, report.ApprovedAtUtc)
                .Set(x => x.CurrentProgressStatus, report.CurrentProgressStatus)
                .Set(x => x.ReportReason, report.ReportReason)
                .Set(x => x.Difficulties, report.Difficulties)
                .Set(x => x.ProposedSolution, report.ProposedSolution)
                .Set(x => x.LateReason, report.LateReason)
                .Set(x => x.ReviewerComment, report.ReviewerComment)
                .Set(x => x.ReturnReason, report.ReturnReason)
                .Set(x => x.RequiresLateReason, report.IsLateSubmission)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        period.IsActive = true;
        period.CurrentReportId = report.Id;
        period.Status = nextPeriodStatus;
        period.IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(nextPeriodStatus);
        period.LastDraftSavedAtUtc = report.Status == WorkAssignmentReportStatus.Draft ? report.UpdatedAtUtc : null;
        period.LastSubmittedAtUtc = report.SubmittedAtUtc;
        period.LastReviewedAtUtc = report.ApprovedAtUtc;
        period.CurrentProgressStatus = report.CurrentProgressStatus;
        period.ReportReason = report.ReportReason;
        period.Difficulties = report.Difficulties;
        period.ProposedSolution = report.ProposedSolution;
        period.LateReason = report.LateReason;
        period.ReviewerComment = report.ReviewerComment;
        period.ReturnReason = report.ReturnReason;
        period.RequiresLateReason = report.IsLateSubmission;
        period.UpdatedAtUtc = now;
        period.UpdatedByUserId = me.Id;

        await FinalizeReviewReportStatusOperationAsync(
            "REVIEW_REACTIVATE_REPORT",
            report,
            period,
            "INACTIVE",
            "ACTIVE",
            me.Id,
            upsertQueue: WorkReportPeriodStatusHelper.ShouldKeepQueueActive(period.Status),
            disableQueue: !WorkReportPeriodStatusHelper.ShouldKeepQueueActive(period.Status),
            ct,
            forceRebuildStatistics: true);

        await RefreshAggregateDependentsAfterReviewAsync(report.Id, me.Id, ct);

        await InsertReportLogAsync(
            report.WorkId,
            report.WorkAssignmentId,
            report.WorkReportPeriodId,
            report.Id,
            "Reactivate",
            "INACTIVE",
            "ACTIVE",
            me.Id,
            comment,
            comment,
            ct);

        await _userActionLog.RecordAsync(new UserActionLogSeed
        {
            Action = UserActionLogActions.ReportReactivated,
            Scope = "report",
            ActorUserId = me.Id,
            WorkId = report.WorkId,
            WorkAssignmentId = report.WorkAssignmentId,
            WorkReportPeriodId = report.WorkReportPeriodId,
            WorkAssignmentReportId = report.Id,
            TargetUserId = report.AssigneeUserId,
            Summary = $"Reactivated report {report.PeriodInstanceKey}",
            Data = new Dictionary<string, string>
            {
                { "fromActive", "false" },
                { "toActive", "true" }
            },
            OccurredAtUtc = now
        }, CancellationToken.None);
    }

    public async Task<PagedResult<ReviewReportFlatRowDto>> SearchReportsForReviewAsync(
        ReviewReportFlatSearchRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        if (string.IsNullOrWhiteSpace(req.WorkId))
            throw ReviewWorkIdRequired(req.WorkId);

        var page = req.Page < 0 ? 0 : req.Page;
        var pageSize = req.PageSize <= 0 ? 20 : req.PageSize;

        await EnsureReviewReportListDocRolesForUserWorkAsync(req.WorkId, me.Id, ct);

        var reqAssigneeUserIds = GetAssigneeUserIds(req);
        var reqAssigneeUnitIds = GetAssigneeUnitIds(req);
        var fb = Builders<ReviewReportListDocRole>.Filter;
        var filter = fb.Eq(x => x.ReviewerUserId, me.Id)
                     & fb.Eq(x => x.WorkId, req.WorkId)
                     & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.AssignmentId))
            filter &= fb.Eq(x => x.AssignmentId, req.AssignmentId.Trim());

        if (!string.IsNullOrWhiteSpace(req.DynamicExcelId))
            filter &= fb.Eq(x => x.DynamicExcelId, req.DynamicExcelId.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= fb.Eq(x => x.PeriodKey, req.PeriodKey.Trim());

        if (reqAssigneeUserIds.Count > 0)
            filter &= fb.In(x => x.AssigneeUserId, reqAssigneeUserIds);

        if (reqAssigneeUnitIds.Count > 0)
            filter &= fb.In(x => x.AssigneeUnitId, reqAssigneeUnitIds);

        if (!string.IsNullOrWhiteSpace(req.Q))
            filter &= BuildReviewReportListTextFilter(req.Q, fb);

        var userTypeFilter = GetUserTypeFilter(req);
        if (!string.IsNullOrWhiteSpace(userTypeFilter))
            filter &= BuildReviewReportUserTypeFilter(userTypeFilter, fb);

        if (req.WaitingReviewOnly == true)
        {
            filter &= fb.Eq(x => x.WaitingReview, true);
        }
        else if (WorkReportPeriodStatusHelper.ShouldFilterReviewBucket(req.ReviewStatusBucket))
        {
            filter &= fb.Eq(
                x => x.ReviewStatusBucket,
                WorkReportPeriodStatusHelper.NormalizeReviewBucket(req.ReviewStatusBucket));
        }
        else if (req.ReportStatus.HasValue)
        {
            filter &= fb.Eq(x => x.ReportStatus, (WorkAssignmentReportStatus)req.ReportStatus.Value);
        }

        var total = await _ctx.ReviewReportListDocRoles.CountDocumentsAsync(filter, cancellationToken: ct);

        var rows = await _ctx.ReviewReportListDocRoles.Find(filter)
            .SortBy(x => x.SortDueAtUtc)
            .ThenBy(x => x.PeriodKey)
            .ThenBy(x => x.AssigneeUnitShortName)
            .ThenBy(x => x.AssigneeFullName)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(MapToReviewReportFlatRowProjection())
            .ToListAsync(ct);

        return new PagedResult<ReviewReportFlatRowDto>(rows, total, page, pageSize);
    }

    private async Task EnsureReviewReportListDocRolesForUserWorkAsync(
        string workId,
        string reviewerUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId) || string.IsNullOrWhiteSpace(reviewerUserId))
            return;

        var hasProjectedRows = await _ctx.ReviewReportListDocRoles
            .Find(x =>
                x.WorkId == workId &&
                x.ReviewerUserId == reviewerUserId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (hasProjectedRows)
            return;

        _log.LogWarning(
            "Review report list projection missing. workId={workId} reviewerUserId={reviewerUserId}. Returning current projection only; run internal DocRole repair/backfill if source data exists.",
            workId,
            reviewerUserId);
    }

    private async Task EnsureReviewAssignmentSummaryDocRolesForUserWorkAsync(
        string workId,
        string reviewerUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId) || string.IsNullOrWhiteSpace(reviewerUserId))
            return;

        var hasProjectedRows = await _ctx.ReviewAssignmentSummaryDocRoles
            .Find(x =>
                x.WorkId == workId &&
                x.ReviewerUserId == reviewerUserId &&
                !x.IsDeleted)
            .AnyAsync(ct);

        if (hasProjectedRows)
            return;

        _log.LogWarning(
            "Review assignment summary projection missing. workId={workId} reviewerUserId={reviewerUserId}. Returning current projection only; run internal DocRole repair/backfill if source data exists.",
            workId,
            reviewerUserId);
    }

    private static FilterDefinition<ReviewReportListDocRole> BuildReviewReportListTextFilter(
        string q,
        FilterDefinitionBuilder<ReviewReportListDocRole> fb)
    {
        var qRegex = new BsonRegularExpression(q.Trim(), "i");

        return fb.Or(
            fb.Regex(x => x.DynamicExcelCode, qRegex),
            fb.Regex(x => x.DynamicExcelName, qRegex),
            fb.Regex(x => x.AssigneeUserName, qRegex),
            fb.Regex(x => x.AssigneeFullName, qRegex),
            fb.Regex(x => x.AssigneeUnitName, qRegex),
            fb.Regex(x => x.AssigneeUnitShortName, qRegex),
            fb.Regex(x => x.PeriodKey, qRegex));
    }

    private static FilterDefinition<ReviewAssignmentSummaryDocRole> BuildReviewAssignmentSummaryTextFilter(
        string q,
        FilterDefinitionBuilder<ReviewAssignmentSummaryDocRole> fb)
    {
        var qRegex = new BsonRegularExpression(q.Trim(), "i");

        return fb.Or(
            fb.Regex(x => x.DynamicExcelCode, qRegex),
            fb.Regex(x => x.DynamicExcelName, qRegex),
            fb.Regex(x => x.FirstAssigneeUserName, qRegex),
            fb.Regex(x => x.FirstAssigneeFullName, qRegex),
            fb.Regex(x => x.FirstAssigneeUnitShortName, qRegex),
            fb.Regex("assignees.username", qRegex),
            fb.Regex("assignees.fullName", qRegex),
            fb.Regex("assignees.unitName", qRegex),
            fb.Regex("assignees.unitShortName", qRegex),
            fb.Regex("periodKeys", qRegex));
    }

    private static FilterDefinition<ReviewReportListDocRole> BuildReviewReportUserTypeFilter(
        string userTypeFilter,
        FilterDefinitionBuilder<ReviewReportListDocRole> fb)
    {
        var normalized = (userTypeFilter ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "ALL")
            return FilterDefinition<ReviewReportListDocRole>.Empty;

        var privilegedRegex = new BsonRegularExpression("^(mu_|ml_)", "i");
        var privilegedFilter = fb.Regex(x => x.AssigneeUserName, privilegedRegex);

        return normalized switch
        {
            "UNIT_ACCOUNT" or "PRIVILEGED" or "MU" or "ML" => privilegedFilter,
            "NORMAL_USER" or "REGULAR" or "USER" => fb.Not(privilegedFilter),
            _ => FilterDefinition<ReviewReportListDocRole>.Empty
        };
    }

    private static System.Linq.Expressions.Expression<Func<ReviewReportListDocRole, ReviewReportFlatRowDto>>
        MapToReviewReportFlatRowProjection()
        => x => new ReviewReportFlatRowDto
        {
            AssignmentId = x.AssignmentId,
            WorkId = x.WorkId,

            DynamicExcelId = x.DynamicExcelId,
            DynamicExcelCode = x.DynamicExcelCode,
            DynamicExcelName = x.DynamicExcelName,

            AssigneeUserId = x.AssigneeUserId,
            AssigneeUserName = x.AssigneeUserName,
            AssigneeFullName = x.AssigneeFullName,
            AssigneeUnitId = x.AssigneeUnitId,
            AssigneeUnitName = x.AssigneeUnitName,
            AssigneeUnitShortName = x.AssigneeUnitShortName,

            WorkReportPeriodId = x.WorkReportPeriodId,

            PeriodKey = x.PeriodKey,
            StartedDate = x.StartedDate,
            CompletedDate = x.CompletedDate,
            IsHistoricalData = x.IsHistoricalData,
            HistoricalDataApproved = x.HistoricalDataApproved,
            HistoricalDataApprovedAtUtc = x.HistoricalDataApprovedAtUtc,
            HistoricalDataApprovedByUserId = x.HistoricalDataApprovedByUserId,
            PeriodStart = x.PeriodStart,
            PeriodEnd = x.PeriodEnd,
            DueAtUtc = x.DueAtUtc,
            PeriodStatus = (int)x.PeriodStatus,

            ReportId = x.CurrentReportId,
            ReportStatus = x.ReportStatus.HasValue ? (int)x.ReportStatus.Value : null,
            ReportIsActive = x.ReportIsActive,
            ReportDeactivatedAtUtc = x.ReportDeactivatedAtUtc,
            ReportDeactivationReason = x.ReportDeactivationReason,
            SubmittedAtUtc = x.SubmittedAtUtc,
            ApprovedAtUtc = x.ApprovedAtUtc,
            AutoApproved = x.AutoApproved,
            AutoApprovedAtUtc = x.AutoApprovedAtUtc,
            AutoApprovedByUserId = x.AutoApprovedByUserId,
            AutoApprovalLocked = x.AutoApprovalLocked,
            AutoApprovalConfirmedAtUtc = x.AutoApprovalConfirmedAtUtc,
            AutoApprovalConfirmedByUserId = x.AutoApprovalConfirmedByUserId,
            ReturnedAtUtc = x.ReturnedAtUtc,
            ReturnReason = x.ReturnReason,
            ReviewerComment = x.ReviewerComment,

            ProgressStatus = x.ProgressStatus,
            ProgressStatusUpdatedAtUtc = x.ProgressStatusUpdatedAtUtc,
            HasAnyDuePeriod = x.HasAnyDuePeriod,
            HasOverduePeriod = x.HasOverduePeriod,

            EvaluationCode = null,
            EvaluationLabel = null,
            WorstPeriodStatus = x.WorstPeriodStatus,
            WorstOverdueReasonCode = x.WorstOverdueReasonCode,
            WorstOverdueReasonLabel = x.WorstOverdueReasonLabel
        };

    private static ReviewSummaryRowDto MapToReviewSummaryRow(ReviewAssignmentSummaryDocRole x)
        => new()
        {
            AssignmentId = x.AssignmentId,
            WorkId = x.WorkId,

            DynamicExcelId = x.DynamicExcelId,
            DynamicExcelCode = x.DynamicExcelCode,
            DynamicExcelName = x.DynamicExcelName,

            Assignees = (x.Assignees ?? new List<UserRef>())
                .Select(a => new ReviewSummaryAssigneeDto
                {
                    UserId = a.UserId,
                    UserName = a.Username,
                    FullName = a.FullName,
                    UnitId = a.UnitId,
                    UnitName = a.UnitName,
                    UnitShortName = a.UnitShortName
                })
                .ToList(),

            ProgressStatus = x.ProgressStatus,
            ProgressStatusUpdatedAtUtc = x.ProgressStatusUpdatedAtUtc,

            LatestPeriodKey = x.LatestPeriodKey,
            LatestPeriodStatus = x.LatestPeriodStatus.HasValue ? (int)x.LatestPeriodStatus.Value : null,
            LatestDueAtUtc = x.LatestDueAtUtc,
            HasAnyDuePeriod = x.HasAnyDuePeriod,
            HasOverduePeriod = x.HasOverduePeriod,

            EvaluationCode = x.EvaluationCode,
            EvaluationLabel = x.EvaluationLabel,

            WorstPeriodStatus = x.WorstPeriodStatus.HasValue ? (int)x.WorstPeriodStatus.Value : null,
            WorstOverdueReasonCode = x.WorstOverdueReasonCode,
            WorstOverdueReasonLabel = x.WorstOverdueReasonLabel
        };

    private async Task<List<WorkAssignment>> LoadVisibleReviewAssignmentsAsync(
        string workId,
        string actorUserId,
        CancellationToken ct)
    {
        var ownedAssignments = await _ctx.WorkAssignments
            .Find(x =>
                x.WorkId == workId &&
                x.CreatedByUserId == actorUserId &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var assignmentById = ownedAssignments
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

        var bindingAssignmentIds = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkId == workId &&
                x.CreatedByUserId == actorUserId &&
                !x.IsDeleted)
            .Project(x => x.WorkAssignmentId)
            .ToListAsync(ct);

        var missingAssignmentIds = bindingAssignmentIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Where(x => !assignmentById.ContainsKey(x))
            .ToList();

        if (missingAssignmentIds.Count > 0)
        {
            var bindingOwnedAssignments = await _ctx.WorkAssignments
                .Find(x =>
                    x.WorkId == workId &&
                    missingAssignmentIds.Contains(x.Id) &&
                    !x.IsDeleted)
                .ToListAsync(ct);

            foreach (var assignment in bindingOwnedAssignments)
            {
                if (!string.IsNullOrWhiteSpace(assignment.Id))
                    assignmentById[assignment.Id] = assignment;
            }
        }

        return assignmentById.Values
            .OrderBy(x => x.Path ?? string.Empty)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ToList();
    }

    public async Task<PagedResult<ReviewSummaryRowDto>> SearchSummaryForReviewAsync(
        ReviewSummarySearchRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        if (string.IsNullOrWhiteSpace(req.WorkId))
            throw ReviewWorkIdRequired(req.WorkId);

        var page = req.Page < 0 ? 0 : req.Page;
        var pageSize = req.PageSize <= 0 ? 20 : req.PageSize;

        await EnsureReviewAssignmentSummaryDocRolesForUserWorkAsync(req.WorkId, me.Id, ct);

        var reqAssigneeUserIds = GetAssigneeUserIds(req);
        var reqAssigneeUnitIds = GetAssigneeUnitIds(req);
        var fb = Builders<ReviewAssignmentSummaryDocRole>.Filter;
        var filter = fb.Eq(x => x.ReviewerUserId, me.Id)
                     & fb.Eq(x => x.WorkId, req.WorkId)
                     & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(req.DynamicExcelId))
            filter &= fb.Eq(x => x.DynamicExcelId, req.DynamicExcelId.Trim());

        if (!string.IsNullOrWhiteSpace(req.PeriodKey))
            filter &= fb.AnyEq(x => x.PeriodKeys, req.PeriodKey.Trim());

        if (reqAssigneeUserIds.Count > 0)
            filter &= fb.AnyIn(x => x.AssigneeUserIds, reqAssigneeUserIds);

        if (reqAssigneeUnitIds.Count > 0)
            filter &= fb.AnyIn(x => x.AssigneeUnitIds, reqAssigneeUnitIds);

        if (req.WaitingReviewOnly == true)
        {
            filter &= fb.Gt(x => x.WaitingReviewCount, 0);
        }
        else if (WorkReportPeriodStatusHelper.ShouldFilterReviewBucket(req.ReviewStatusBucket))
        {
            filter &= fb.AnyEq(
                x => x.ReviewStatusBuckets,
                WorkReportPeriodStatusHelper.NormalizeReviewBucket(req.ReviewStatusBucket));
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
            filter &= BuildReviewAssignmentSummaryTextFilter(req.Q, fb);

        var total = await _ctx.ReviewAssignmentSummaryDocRoles.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.ReviewAssignmentSummaryDocRoles
            .Find(filter)
            .SortByDescending(x => x.SortHasOverduePeriod)
            .ThenByDescending(x => x.SortLatestDueAtUtc)
            .ThenBy(x => x.FirstAssigneeUnitShortName)
            .ThenBy(x => x.FirstAssigneeFullName)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        var resultRows = rows.Select(MapToReviewSummaryRow).ToList();

        return new PagedResult<ReviewSummaryRowDto>(resultRows, total, page, pageSize);
    }

    private static WorkReportPeriodStatus ResolveApprovedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        return WorkAssignmentReportHistoricalDataHelper.ResolveApprovedPeriodStatus(period, report, now);
    }

    private static WorkReportPeriodStatus ResolveDraftPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        return WorkAssignmentReportHistoricalDataHelper.ResolveDraftPeriodStatus(
            report.IsHistoricalData || period.IsHistoricalData,
            report.CompletedDate ?? period.CompletedDate,
            report.DueAtUtc ?? period.DueAtUtc,
            now);
    }

    private static WorkReportPeriodStatus ResolveSubmittedPeriodStatus(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        return WorkAssignmentReportHistoricalDataHelper.ResolveSubmittedPeriodStatus(period, report, now);
    }

    private static WorkReportPeriodStatus ResolvePeriodStatusFromReport(
        WorkReportPeriod period,
        WorkAssignmentReport report,
        DateTime now)
    {
        return report.Status switch
        {
            WorkAssignmentReportStatus.Approved => ResolveApprovedPeriodStatus(period, report, now),
            WorkAssignmentReportStatus.Submitted => ResolveSubmittedPeriodStatus(period, report, now),
            _ => ResolveDraftPeriodStatus(period, report, now)
        };
    }

    private static void EnsureReportIsActive(WorkAssignmentReport report)
    {
        if (report.IsActive == false)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_INACTIVE,
                ReportDetails(report));
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static AppException ReviewWorkIdRequired(string? workId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_REVIEW_WORK_ID_REQUIRED,
            new { workId });

    private static AppException EvaluationAssignmentIdRequired(string? assignmentId)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.WORK_ASSIGNMENT_EVALUATION_ASSIGNMENT_ID_REQUIRED,
            new { assignmentId });

    private static AppException EvaluationAssignmentNotFound(string? assignmentId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_EVALUATION_ASSIGNMENT_NOT_FOUND,
            new { assignmentId });

    private static object AssignmentDetails(WorkAssignment assignment)
        => new
        {
            assignmentId = assignment.Id,
            assignment.WorkId,
            assignment.ParentAssignmentId,
            assignment.RootAssignmentId,
            assignment.DynamicFormTemplateId,
            assignment.EvaluationTemplateId,
            ownerUserId = assignment.CreatedByUserId
        };

    private static AppException ReportNotFound(string? reportId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_NOT_FOUND,
            new { reportId });

    private static AppException ReportAssignmentNotFound(WorkAssignmentReport report)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_ASSIGNMENT_NOT_FOUND,
            ReportDetails(report));

    private static AppException ReportPeriodNotFound(WorkAssignmentReport report)
        => AppExceptionFactory.NotFound(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PERIOD_NOT_FOUND,
            ReportDetails(report));

    private static AppException InvalidReportStatus(
        AppErrorCode code,
        WorkAssignmentReport report,
        WorkAssignmentReportStatus expectedStatus)
        => AppExceptionFactory.BadRequest(
            code,
            new
            {
                reportId = report.Id,
                report.Status,
                expectedStatus,
                report.WorkAssignmentId,
                report.WorkReportPeriodId,
                report.PeriodInstanceKey
            });

    private static void EnsureNotSelfReview(WorkAssignmentReport report, string actorUserId)
    {
        if (string.Equals(report.AssigneeUserId, actorUserId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_REVIEW_SELF_FORBIDDEN,
                new
                {
                    reportId = report.Id,
                    report.WorkId,
                    report.WorkAssignmentId,
                    report.WorkReportPeriodId,
                    report.PeriodKey,
                    report.PeriodInstanceKey,
                    report.AssigneeUserId,
                    actorUserId
                });
    }

    private static object ReportDetails(WorkAssignmentReport report)
        => new
        {
            reportId = report.Id,
            report.WorkId,
            report.WorkAssignmentId,
            report.WorkReportPeriodId,
            report.PeriodKey,
            report.PeriodInstanceKey,
            report.AssigneeUserId
        };

    private async Task EnsurePreviousReportsApprovedAsync(
        WorkReportPeriod? period,
        CancellationToken ct)
    {
        if (period is null)
            return;

        var fb = Builders<WorkReportPeriod>.Filter;
        var filter = fb.Eq(x => x.WorkAssignmentId, period.WorkAssignmentId)
                     & fb.Eq(x => x.AssigneeUserId, period.AssigneeUserId)
                     & fb.Eq(x => x.PeriodKind, period.PeriodKind)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Ne(x => x.Id, period.Id);

        if (!string.IsNullOrWhiteSpace(period.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, period.DynamicFormTemplateId);
        else if (!string.IsNullOrWhiteSpace(period.DynamicExcelId))
            filter &= fb.Eq(x => x.DynamicExcelId, period.DynamicExcelId);

        var candidates = await _ctx.WorkReportPeriods
            .Find(filter)
            .ToListAsync(ct);

        var previousOpenPeriod = candidates
            .Where(candidate => ComparePeriodOrder(candidate, period) < 0)
            .Where(candidate => !WorkReportPeriodStatusHelper.IsTerminal(candidate.Status))
            .OrderBy(ResolvePeriodOrder)
            .FirstOrDefault();

        if (previousOpenPeriod is not null)
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_PREVIOUS_PERIOD_OPEN,
                new
                {
                    periodId = period.Id,
                    periodInstanceKey = period.PeriodInstanceKey,
                    previousPeriodId = previousOpenPeriod.Id,
                    previousPeriodInstanceKey = previousOpenPeriod.PeriodInstanceKey,
                    previousPeriodStatus = previousOpenPeriod.Status
                });
    }

    private async Task EnsureNoLaterApprovedReportsAsync(
        WorkReportPeriod? period,
        CancellationToken ct)
    {
        if (period is null)
            return;

        var fb = Builders<WorkReportPeriod>.Filter;
        var filter = fb.Eq(x => x.WorkAssignmentId, period.WorkAssignmentId)
                     & fb.Eq(x => x.AssigneeUserId, period.AssigneeUserId)
                     & fb.Eq(x => x.PeriodKind, period.PeriodKind)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Ne(x => x.Id, period.Id);

        if (!string.IsNullOrWhiteSpace(period.DynamicFormTemplateId))
            filter &= fb.Eq(x => x.DynamicFormTemplateId, period.DynamicFormTemplateId);
        else if (!string.IsNullOrWhiteSpace(period.DynamicExcelId))
            filter &= fb.Eq(x => x.DynamicExcelId, period.DynamicExcelId);

        var candidates = await _ctx.WorkReportPeriods
            .Find(filter)
            .ToListAsync(ct);

        var laterApprovedPeriod = candidates
            .Where(candidate => ComparePeriodOrder(candidate, period) > 0)
            .Where(candidate => WorkReportPeriodStatusHelper.IsTerminal(candidate.Status))
            .OrderBy(ResolvePeriodOrder)
            .FirstOrDefault();

        if (laterApprovedPeriod is not null)
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_REPORT_LATER_PERIOD_APPROVED,
                new
                {
                    periodId = period.Id,
                    periodInstanceKey = period.PeriodInstanceKey,
                    laterPeriodId = laterApprovedPeriod.Id,
                    laterPeriodInstanceKey = laterApprovedPeriod.PeriodInstanceKey,
                    laterPeriodStatus = laterApprovedPeriod.Status
                });
    }

    private static int ComparePeriodOrder(WorkReportPeriod left, WorkReportPeriod right)
    {
        var byTime = DateTime.Compare(ResolvePeriodOrder(left), ResolvePeriodOrder(right));
        if (byTime != 0)
            return byTime;

        var byCreated = DateTime.Compare(left.CreatedAtUtc, right.CreatedAtUtc);
        if (byCreated != 0)
            return byCreated;

        return string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static DateTime ResolvePeriodOrder(WorkReportPeriod period)
    {
        if (WorkReportPeriodKind.IsUserCreated(period.PeriodKind))
            return period.ReportDate
                   ?? period.PeriodStart
                   ?? period.PeriodEnd
                   ?? period.DueAtUtc
                   ?? period.CreatedAtUtc;

        return period.PeriodStart
               ?? period.ReportDate
               ?? period.PeriodEnd
               ?? period.DueAtUtc
               ?? period.CreatedAtUtc;
    }

    private static bool MatchUserTypeFilter(string? username, string? filter)
    {
        var normalized = (filter ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "ALL")
            return true;

        var isPrivileged = IsPrivilegedUsername(username);

        return normalized switch
        {
            "UNIT_ACCOUNT" or "PRIVILEGED" or "MU" or "ML" => isPrivileged,
            "NORMAL_USER" or "REGULAR" or "USER" => !isPrivileged,
            _ => true,
        };
    }

    private static bool IsPrivilegedUsername(string? username)
    {
        var value = (username ?? string.Empty).Trim();
        return value.StartsWith("mu_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ml_", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetUserTypeFilter(object req)
    {
        var type = req.GetType();
        foreach (var propName in new[] { "UserTypeFilter", "AccountTypeFilter", "AccountKind" })
        {
            var prop = type.GetProperty(propName);
            if (prop?.GetValue(req) is string value && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static HashSet<string> GetAssigneeUserIds(object req)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var type = req.GetType();

        var listProp = type.GetProperty("AssigneeUserIds");
        if (listProp?.GetValue(req) is IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    result.Add(id.Trim());
            }
        }

        var singleProp = type.GetProperty("AssigneeUserId");
        if (singleProp?.GetValue(req) is string singleId && !string.IsNullOrWhiteSpace(singleId))
        {
            result.Add(singleId.Trim());
        }

        return result;
    }

    private static HashSet<string> GetAssigneeUnitIds(object req)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var type = req.GetType();

        var listProp = type.GetProperty("AssigneeUnitIds");
        if (listProp?.GetValue(req) is IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    result.Add(id.Trim());
            }
        }

        var singleProp = type.GetProperty("AssigneeUnitId");
        if (singleProp?.GetValue(req) is string singleId && !string.IsNullOrWhiteSpace(singleId))
        {
            result.Add(singleId.Trim());
        }

        return result;
    }

    private async Task AppendEvaluationLogAsync(
        WorkAssignment assignment,
        string action,
        string? fromCode,
        string? fromLabel,
        string? toCode,
        string? toLabel,
        string? comment,
        string? reason,
        string actorUserId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var snapshot = new
        {
            assignmentId = assignment.Id,
            progressStatus = assignment.ProgressStatus,
            worstPeriodStatus = assignment.WorstPeriodStatus,
            worstOverdueReasonCode = assignment.WorstOverdueReasonCode,
            worstOverdueReasonLabel = assignment.WorstOverdueReasonLabel,
            evaluationTemplateId = assignment.EvaluationTemplateId,
            evaluationTemplateCode = assignment.EvaluationTemplateCode,
            evaluationTemplateLabel = assignment.EvaluationTemplateLabel,
            evaluationCode = toCode,
            evaluationLabel = toLabel,
            evaluationNote = comment,
            worstEvaluationCode = assignment.WorstEvaluationCode,
            worstEvaluationLabel = assignment.WorstEvaluationLabel,
            evaluatedAssignmentCount = assignment.EvaluatedAssignmentCount,
            hasManualEvaluations = assignment.HasManualEvaluations
        };

        var doc = new WorkAssignmentEvaluationLog
        {
            WorkId = assignment.WorkId,
            WorkAssignmentId = assignment.Id,
            Action = action,
            FromEvaluationCode = fromCode,
            FromEvaluationLabel = fromLabel,
            ToEvaluationCode = toCode,
            ToEvaluationLabel = toLabel,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ActionByUserId = actorUserId,
            ActionAtUtc = now,
            SnapshotJson = JsonSerializer.Serialize(snapshot),
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId
        };

        await _ctx.WorkAssignmentEvaluationLogs.InsertOneAsync(doc, cancellationToken: ct);
    }

    public async Task<bool> EvaluateAssignmentAsync(
        string assignmentId,
        EvaluateAssignmentRequest req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        if (string.IsNullOrWhiteSpace(assignmentId))
            throw EvaluationAssignmentIdRequired(assignmentId);

        if (string.IsNullOrWhiteSpace(req.EvaluationCode))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_EVALUATION_CODE_REQUIRED,
                new { assignmentId, req.EvaluationCode });

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw EvaluationAssignmentNotFound(assignmentId);

        EnsureCanEvaluateAssignment(assignment, me.Id);

        var fb = Builders<WorkReportPeriod>.Filter;
        var terminalFilter =
            fb.Eq(x => x.WorkAssignmentId, assignment.Id) &
            fb.Eq(x => x.IsActive, true) &
            fb.Eq(x => x.IsDeleted, false) &
            fb.In(x => x.Status, WorkReportPeriodStatusHelper.TerminalStatuses);

        var hasApprovedPeriod = await _ctx.WorkReportPeriods
            .Find(terminalFilter)
            .AnyAsync(ct);

        if (!hasApprovedPeriod)
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_ASSIGNMENT_EVALUATION_APPROVED_PERIOD_REQUIRED,
                AssignmentDetails(assignment));

        if (string.IsNullOrWhiteSpace(assignment.EvaluationTemplateId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_EVALUATION_TEMPLATE_REQUIRED,
                AssignmentDetails(assignment));

        var template = await _ctx.EvaluationTemplates
            .Find(x => x.Id == assignment.EvaluationTemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_EVALUATION_TEMPLATE_NOT_FOUND,
                AssignmentDetails(assignment));

        var option = (template.Items ?? new List<EvaluationTemplateItem>())
            .FirstOrDefault(x => string.Equals(x.Code, req.EvaluationCode.Trim(), StringComparison.OrdinalIgnoreCase));

        if (option is null)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_EVALUATION_CODE_INVALID,
                new
                {
                    assignmentId = assignment.Id,
                    assignment.WorkId,
                    evaluationCode = req.EvaluationCode
                });

        var oldCode = assignment.EvaluationCode;
        var oldLabel = assignment.EvaluationLabel;
        var newCode = option.Code;
        var newLabel = option.Label;

        var action = string.IsNullOrWhiteSpace(oldCode) ? "EVALUATE" : "UPDATE_EVALUATION";
        var now = DateTime.UtcNow;

        var rs = await _ctx.WorkAssignments.UpdateOneAsync(
            x => x.Id == assignment.Id && !x.IsDeleted,
            Builders<WorkAssignment>.Update
                .Set(x => x.EvaluationCode, newCode)
                .Set(x => x.EvaluationLabel, newLabel)
                .Set(x => x.EvaluationNote, string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim())
                .Set(x => x.EvaluatedAtUtc, now)
                .Set(x => x.EvaluatedByUserId, me.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, me.Id),
            cancellationToken: ct);

        if (rs.ModifiedCount == 0)
            return false;

        assignment.EvaluationCode = newCode;
        assignment.EvaluationLabel = newLabel;
        assignment.EvaluationNote = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
        assignment.EvaluatedAtUtc = now;
        assignment.EvaluatedByUserId = me.Id;
        assignment.UpdatedAtUtc = now;
        assignment.UpdatedByUserId = me.Id;

        await RebuildManualEvaluationTreeAsync(assignment.WorkId, me.Id, ct);

        var refreshed = await _ctx.WorkAssignments
            .Find(x => x.Id == assignment.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct) ?? assignment;

        await AppendEvaluationLogAsync(
            refreshed,
            action,
            oldCode,
            oldLabel,
            newCode,
            newLabel,
            req.Comment,
            req.Reason,
            me.Id,
            ct);

        return true;
    }

    public async Task<PagedResult<WorkAssignmentEvaluationLogRow>> GetEvaluationLogsAsync(
        string assignmentId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        if (string.IsNullOrWhiteSpace(assignmentId))
            throw EvaluationAssignmentIdRequired(assignmentId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw EvaluationAssignmentNotFound(assignmentId);

        EnsureCanEvaluateAssignment(assignment, me.Id);

        page = page < 0 ? 0 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 100);

        var filter = Builders<WorkAssignmentEvaluationLog>.Filter.And(
            Builders<WorkAssignmentEvaluationLog>.Filter.Eq(x => x.WorkAssignmentId, assignmentId),
            Builders<WorkAssignmentEvaluationLog>.Filter.Eq(x => x.IsDeleted, false));

        var total = await _ctx.WorkAssignmentEvaluationLogs.CountDocumentsAsync(filter, cancellationToken: ct);

        var rows = await _ctx.WorkAssignmentEvaluationLogs.Find(filter)
            .SortByDescending(x => x.ActionAtUtc)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new WorkAssignmentEvaluationLogRow(
                x.Id,
                x.WorkId,
                x.WorkAssignmentId,
                x.Action,
                x.FromEvaluationCode,
                x.FromEvaluationLabel,
                x.ToEvaluationCode,
                x.ToEvaluationLabel,
                x.Comment,
                x.Reason,
                x.ActionByUserId,
                x.ActionAtUtc))
            .ToListAsync(ct);

        return new PagedResult<WorkAssignmentEvaluationLogRow>(rows, total, page, pageSize);
    }

    private async Task InsertReportLogAsync(
        string workId,
        string workAssignmentId,
        string workReportPeriodId,
        string workAssignmentReportId,
        string action,
        string fromStatus,
        string toStatus,
        string actionByUserId,
        string? reason,
        string? comment,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var log = new WorkAssignmentReportLog
        {
            WorkId = workId,
            WorkAssignmentId = workAssignmentId,
            WorkReportPeriodId = workReportPeriodId,
            WorkAssignmentReportId = workAssignmentReportId,
            Action = action,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ActionByUserId = actionByUserId,
            ActionAtUtc = now,
            Reason = reason,
            Comment = comment,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actionByUserId,
            UpdatedByUserId = actionByUserId,
            IsDeleted = false
        };

        await _ctx.WorkAssignmentReportLogs.InsertOneAsync(log, cancellationToken: ct);
    }

    private async Task FinalizeReviewReportStatusOperationAsync(
        string operation,
        WorkAssignmentReport report,
        WorkReportPeriod period,
        string fromStatus,
        string toStatus,
        string actorUserId,
        bool upsertQueue,
        bool disableQueue,
        CancellationToken ct,
        bool forceRebuildStatistics = false)
    {
        var startedAtUtc = DateTime.UtcNow;
        var periodStatus = period.Status.ToString();

        try
        {
            if (disableQueue)
                await _queueService.DisableByPeriodAsync(period.WorkAssignmentId, period.AssigneeUserId, period.PeriodKey, actorUserId, ct);
            else if (upsertQueue)
                await _queueService.UpsertPeriodAsync(period, actorUserId, ct);

            await _statusSync.SyncFromAssignmentAsync(report.WorkAssignmentId, ct);
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, actorUserId, ct);
            if (forceRebuildStatistics || ShouldRebuildApprovedStatistics(fromStatus, toStatus))
            {
                if (string.Equals(toStatus, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase))
                    WorkReportPayloadConsistency.EnsureReadyForStatisticProjection(report);

                await RebuildReportStatisticsAsync(report, actorUserId, ct);
            }

            _log.LogInformation(
                "WorkAssignment review report status operation completed. operation={operation} reportId={reportId} periodId={periodId} assignmentId={assignmentId} workId={workId} fromStatus={fromStatus} toStatus={toStatus}",
                operation,
                report.Id,
                report.WorkReportPeriodId,
                report.WorkAssignmentId,
                report.WorkId,
                fromStatus,
                toStatus);

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = operation,
                Scope = "review-report",
                Result = "SUCCESS",
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                ActorUserId = actorUserId,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                PeriodToStatus = periodStatus,
                Summary = $"upsertQueue={upsertQueue};disableQueue={disableQueue};rebuildProjection=true;syncAssignment=true;forceRebuildStatistics={forceRebuildStatistics}",
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "WorkAssignment review report status operation failed. operation={operation} reportId={reportId} periodId={periodId} assignmentId={assignmentId} workId={workId} actorUserId={actorUserId} fromStatus={fromStatus} toStatus={toStatus} periodStatus={periodStatus} upsertQueue={upsertQueue} disableQueue={disableQueue}",
                operation,
                report.Id,
                report.WorkReportPeriodId,
                report.WorkAssignmentId,
                report.WorkId,
                actorUserId,
                fromStatus,
                toStatus,
                periodStatus,
                upsertQueue,
                disableQueue);

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = operation,
                Scope = "review-report",
                Result = "FAILED",
                WorkId = report.WorkId,
                WorkAssignmentId = report.WorkAssignmentId,
                WorkReportPeriodId = report.WorkReportPeriodId,
                WorkAssignmentReportId = report.Id,
                ActorUserId = actorUserId,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                PeriodToStatus = periodStatus,
                Summary = $"upsertQueue={upsertQueue};disableQueue={disableQueue};rebuildProjection=true;syncAssignment=true;forceRebuildStatistics={forceRebuildStatistics}",
                ErrorType = ex.GetType().FullName,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.ToString(),
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, ct);

            throw;
        }
    }

    private static bool ShouldRebuildApprovedStatistics(string? fromStatus, string? toStatus)
        => string.Equals(fromStatus, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase)
           || string.Equals(toStatus, WorkAssignmentReportStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase);

    private async Task RefreshAggregateDependentsAfterReviewAsync(
        string reportId,
        string actorUserId,
        CancellationToken ct)
    {
        try
        {
            await _reportService.RefreshDynamicFormAggregateDependentsAsync(reportId, actorUserId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Dynamic Form aggregate dependent refresh failed after review action. reportId={reportId} actorUserId={actorUserId}",
                reportId,
                actorUserId);
        }
    }

    private async Task RebuildReportStatisticsAsync(
        WorkAssignmentReport report,
        string actorUserId,
        CancellationToken ct)
    {
        await _labelStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
        await _tableStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
        await _fieldStatistics.RebuildForReportAsync(report.Id, actorUserId, ct);
    }

    private async Task WriteStatusOperationLogAsync(
        WorkStatusOperationLog log,
        DateTime startedAtUtc,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        log.CompletedAtUtc = completedAtUtc;
        log.DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        await _statusLog.WriteAsync(log, ct);
    }

    private static Task<WorkAssignment> EnsureCanReviewReportAsync(
        WorkAssignment assignment,
        string reviewerUserId,
        CancellationToken ct)
    {
        WorkAssignmentReviewPermissionHelper.EnsureCanReviewOnNode(assignment, reviewerUserId);
        return Task.FromResult(assignment);
    }

    private static void EnsureCanEvaluateAssignment(WorkAssignment assignment, string actorUserId)
    {
        if (!string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_EVALUATION_FORBIDDEN,
                new
                {
                    assignmentId = assignment.Id,
                    assignment.WorkId,
                    actorUserId,
                    ownerUserId = assignment.CreatedByUserId
                });
    }

    private async Task<WorkAssignment> LoadParentForReviewAsync(
        string? parentAssignmentId,
        string reviewerUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parentAssignmentId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_REVIEW_PARENT_ID_REQUIRED,
                new { parentAssignmentId });

        var parent = await _ctx.WorkAssignments
            .Find(x => x.Id == parentAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_REVIEW_PARENT_NOT_FOUND,
                new { parentAssignmentId });

        await EnsureCanReviewReportAsync(parent, reviewerUserId, ct);
        return parent;
    }

    private async Task RebuildManualEvaluationTreeAsync(string workId, string byUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return;

        var assignments = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && x.IsActive && !x.IsDeleted)
            .SortByDescending(x => x.Level)
            .ThenBy(x => x.Path)
            .ToListAsync(ct);

        if (assignments.Count == 0)
        {
            await RebuildWorkManualEvaluationAggregateAsync(workId, byUserId, ct);
            return;
        }

        var templateOrderMap = await BuildTemplateOrderMapAsync(assignments, ct);

        foreach (var assignment in assignments)
        {
            var children = assignments
                .Where(x => string.Equals(x.ParentAssignmentId, assignment.Id, StringComparison.Ordinal))
                .ToList();

            var aggregate = BuildAssignmentManualAggregate(assignment, children, templateOrderMap);

            await _ctx.WorkAssignments.UpdateOneAsync(
                x => x.Id == assignment.Id && !x.IsDeleted,
                Builders<WorkAssignment>.Update
                    .Set(x => x.HasManualEvaluations, aggregate.HasManualEvaluations)
                    .Set(x => x.EvaluatedAssignmentCount, aggregate.EvaluatedAssignmentCount)
                    .Set(x => x.WorstEvaluationCode, aggregate.WorstEvaluationCode)
                    .Set(x => x.WorstEvaluationLabel, aggregate.WorstEvaluationLabel),
                cancellationToken: ct);

            assignment.HasManualEvaluations = aggregate.HasManualEvaluations;
            assignment.EvaluatedAssignmentCount = aggregate.EvaluatedAssignmentCount;
            assignment.WorstEvaluationCode = aggregate.WorstEvaluationCode;
            assignment.WorstEvaluationLabel = aggregate.WorstEvaluationLabel;

            await _docRoleReadModelProjection.RebuildAssignmentAsync(assignment.Id, byUserId, ct);
        }

        await RebuildWorkManualEvaluationAggregateAsync(workId, byUserId, ct, assignments);
    }

    private async Task<Dictionary<string, Dictionary<string, int>>> BuildTemplateOrderMapAsync(
        List<WorkAssignment> assignments,
        CancellationToken ct)
    {
        var templateIds = assignments
            .Select(x => x.EvaluationTemplateId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (templateIds.Count == 0)
            return new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        var templates = await _ctx.EvaluationTemplates
            .Find(x => templateIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        return templates.ToDictionary(
            t => t.Id,
            t => (t.Items ?? new List<EvaluationTemplateItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Code))
                .ToDictionary(
                    i => i.Code,
                    i => i.Order,
                    StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
    }

    private ManualEvaluationAggregate BuildAssignmentManualAggregate(
        WorkAssignment assignment,
        List<WorkAssignment> children,
        Dictionary<string, Dictionary<string, int>> templateOrderMap)
    {
        if (children.Count == 0)
        {
            var hasOwn = !string.IsNullOrWhiteSpace(assignment.EvaluationCode);

            return new ManualEvaluationAggregate
            {
                HasManualEvaluations = hasOwn,
                EvaluatedAssignmentCount = hasOwn ? 1 : 0,
                WorstEvaluationCode = hasOwn ? assignment.EvaluationCode : null,
                WorstEvaluationLabel = hasOwn ? assignment.EvaluationLabel : null
            };
        }

        var evaluatedCount = children.Sum(x => x.EvaluatedAssignmentCount);
        var hasManual = evaluatedCount > 0 || children.Any(x => x.HasManualEvaluations);

        var options = children
            .Where(x => !string.IsNullOrWhiteSpace(x.WorstEvaluationCode))
            .Select(x => new ManualEvaluationChoice(
                x.WorstEvaluationCode!,
                x.WorstEvaluationLabel,
                ResolveEvaluationOrder(x.EvaluationTemplateId, x.WorstEvaluationCode!, templateOrderMap)))
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var worst = options.FirstOrDefault();

        return new ManualEvaluationAggregate
        {
            HasManualEvaluations = hasManual,
            EvaluatedAssignmentCount = evaluatedCount,
            WorstEvaluationCode = worst?.Code,
            WorstEvaluationLabel = worst?.Label
        };
    }

    private async Task RebuildWorkManualEvaluationAggregateAsync(
        string workId,
        string byUserId,
        CancellationToken ct,
        List<WorkAssignment>? preloadedAssignments = null)
    {
        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null)
            return;

        var assignments = preloadedAssignments ?? await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        var roots = assignments
            .Where(x => string.IsNullOrWhiteSpace(x.ParentAssignmentId))
            .ToList();

        var evaluatedCount = roots.Sum(x => x.EvaluatedAssignmentCount);
        var hasManual = evaluatedCount > 0 || roots.Any(x => x.HasManualEvaluations);

        Dictionary<string, int> orderMap = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(work.EvaluationTemplateId))
        {
            var template = await _ctx.EvaluationTemplates
                .Find(x => x.Id == work.EvaluationTemplateId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (template is not null)
            {
                orderMap = (template.Items ?? new List<EvaluationTemplateItem>())
                    .Where(i => !string.IsNullOrWhiteSpace(i.Code))
                    .ToDictionary(i => i.Code, i => i.Order, StringComparer.OrdinalIgnoreCase);
            }
        }

        var worst = roots
            .Where(x => !string.IsNullOrWhiteSpace(x.WorstEvaluationCode))
            .Select(x => new ManualEvaluationChoice(
                x.WorstEvaluationCode!,
                x.WorstEvaluationLabel,
                ResolveEvaluationOrder(work.EvaluationTemplateId, x.WorstEvaluationCode!, new Dictionary<string, Dictionary<string, int>>
                {
                    [work.EvaluationTemplateId ?? string.Empty] = orderMap
                })))
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        await _ctx.Works.UpdateOneAsync(
            x => x.Id == workId && !x.IsDeleted,
            Builders<Work>.Update
                .Set(x => x.HasManualEvaluations, hasManual)
                .Set(x => x.EvaluatedAssignmentCount, evaluatedCount)
                .Set(x => x.WorstEvaluationCode, worst?.Code)
                .Set(x => x.WorstEvaluationLabel, worst?.Label),
            cancellationToken: ct);

        await _docRoleReadModelProjection.RebuildWorkAsync(workId, byUserId, ct);
    }

    private static int ResolveEvaluationOrder(
        string? templateId,
        string code,
        Dictionary<string, Dictionary<string, int>> templateOrderMap)
    {
        if (!string.IsNullOrWhiteSpace(templateId) &&
            templateOrderMap.TryGetValue(templateId, out var orderMap) &&
            orderMap.TryGetValue(code, out var order))
            return order;

        return int.MaxValue;
    }

    private static string ToProgressStatusText(int status)
    {
        return status switch
        {
            (int)WorkAssignmentProgressStatus.NotStarted => "Chưa thực hiện",
            (int)WorkAssignmentProgressStatus.InProgress => "Đang thực hiện",
            (int)WorkAssignmentProgressStatus.Completed => "Đã hoàn thành",
            (int)WorkAssignmentProgressStatus.AtRiskOverdue => "Có nguy cơ chậm muộn",
            (int)WorkAssignmentProgressStatus.Overdue => "Chậm muộn",
            _ => "Không xác định"
        };
    }

    private sealed class ManualEvaluationAggregate
    {
        public bool HasManualEvaluations { get; set; }
        public int EvaluatedAssignmentCount { get; set; }
        public string? WorstEvaluationCode { get; set; }
        public string? WorstEvaluationLabel { get; set; }
    }

    private sealed record ManualEvaluationChoice(string Code, string? Label, int Order);
}
