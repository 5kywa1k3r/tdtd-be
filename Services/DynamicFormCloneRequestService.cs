using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.Users;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.Notifications;

namespace tdtd_be.Services;

public interface IDynamicFormCloneRequestService
{
    Task<DynamicFormCloneRequestRow> CreateAsync(
        string assignmentId,
        CreateDynamicFormCloneRequestReq req,
        string actorUserId,
        CancellationToken ct);

    Task<PagedResult<DynamicFormCloneRequestRow>> SearchMyAsync(
        string workId,
        DynamicFormCloneRequestSearchReq req,
        string actorUserId,
        CancellationToken ct);

    Task<PagedResult<DynamicFormCloneRequestRow>> SearchPendingApprovalAsync(
        string workId,
        DynamicFormCloneRequestSearchReq req,
        string actorUserId,
        CancellationToken ct);

    Task<DynamicFormCloneRequestRow> ApproveAsync(
        string requestId,
        ReviewDynamicFormCloneRequestReq req,
        string actorUserId,
        CancellationToken ct);

    Task<DynamicFormCloneRequestRow> RejectAsync(
        string requestId,
        ReviewDynamicFormCloneRequestReq req,
        string actorUserId,
        CancellationToken ct);
}

public sealed class DynamicFormCloneRequestService : IDynamicFormCloneRequestService
{
    private readonly MongoDbContext _ctx;
    private readonly INotificationService _notifications;

    public DynamicFormCloneRequestService(
        MongoDbContext ctx,
        INotificationService notifications)
    {
        _ctx = ctx;
        _notifications = notifications;
    }

    public async Task<DynamicFormCloneRequestRow> CreateAsync(
        string assignmentId,
        CreateDynamicFormCloneRequestReq req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted && x.IsActive)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_ASSIGNMENT_NOT_FOUND,
                new { assignmentId });

        if (string.IsNullOrWhiteSpace(assignment.DynamicFormTemplateId))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_TEMPLATE_MISSING,
                new { assignmentId });

        var isAssignee = assignment.Assignees.Any(x =>
            string.Equals(x.UserId, actorUserId, StringComparison.Ordinal));

        if (!isAssignee)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_ASSIGNEE_FORBIDDEN,
                new { assignmentId, actorUserId });

        var ownerUserId = NullIfWhiteSpace(assignment.CreatedByUserId);
        if (ownerUserId is null)
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_REVIEW_FORBIDDEN,
                new { assignmentId, actorUserId });

        if (string.Equals(ownerUserId, actorUserId, StringComparison.Ordinal))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_STATUS_INVALID,
                new { assignmentId, actorUserId, reason = "owner-cannot-request-own-approval" });

        var duplicatePending = await _ctx.DynamicFormCloneRequests
            .Find(x =>
                x.WorkAssignmentId == assignment.Id &&
                x.DynamicFormTemplateId == assignment.DynamicFormTemplateId &&
                x.RequesterUserId == actorUserId &&
                x.Status == DynamicFormCloneRequestStatus.Pending &&
                !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);

        if (duplicatePending)
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_DUPLICATE,
                new
                {
                    assignmentId,
                    assignment.DynamicFormTemplateId,
                    requesterUserId = actorUserId
                });

        var refs = await UserRefSnapshotHelper.LoadUserRefMapAsync(
            _ctx,
            new[] { actorUserId, ownerUserId },
            ct);

        refs.TryGetValue(actorUserId, out var requester);
        refs.TryGetValue(ownerUserId, out var owner);

        var now = DateTime.UtcNow;
        var doc = new DynamicFormCloneRequest
        {
            WorkId = assignment.WorkId,
            WorkAssignmentId = assignment.Id,
            AssignmentCode = assignment.Code,
            DynamicFormTemplateId = assignment.DynamicFormTemplateId!,
            DynamicFormTemplateCode = assignment.DynamicFormTemplateCode,
            DynamicFormTemplateName = assignment.DynamicFormTemplateName,
            RequesterUserId = actorUserId,
            AssignmentOwnerUserId = ownerUserId,
            Requester = requester,
            AssignmentOwner = owner,
            Status = DynamicFormCloneRequestStatus.Pending,
            RequestReason = TrimOptional(req.Reason),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        await _ctx.DynamicFormCloneRequests.InsertOneAsync(doc, cancellationToken: ct);
        await NotifyCloneRequestedAsync(doc, assignment.WorkType, actorUserId, ct);
        return ToRow(doc);
    }

    public async Task<PagedResult<DynamicFormCloneRequestRow>> SearchMyAsync(
        string workId,
        DynamicFormCloneRequestSearchReq req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var filter = BuildBaseFilter(workId, req)
                     & Builders<DynamicFormCloneRequest>.Filter.Eq(x => x.RequesterUserId, actorUserId);
        return await SearchAsync(filter, req, ct);
    }

    public async Task<PagedResult<DynamicFormCloneRequestRow>> SearchPendingApprovalAsync(
        string workId,
        DynamicFormCloneRequestSearchReq req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        var filter = BuildBaseFilter(workId, req)
                     & Builders<DynamicFormCloneRequest>.Filter.Eq(x => x.AssignmentOwnerUserId, actorUserId);
        return await SearchAsync(filter, req, ct);
    }

    public Task<DynamicFormCloneRequestRow> ApproveAsync(
        string requestId,
        ReviewDynamicFormCloneRequestReq req,
        string actorUserId,
        CancellationToken ct)
        => ReviewAsync(requestId, req, actorUserId, DynamicFormCloneRequestStatus.Approved, ct);

    public Task<DynamicFormCloneRequestRow> RejectAsync(
        string requestId,
        ReviewDynamicFormCloneRequestReq req,
        string actorUserId,
        CancellationToken ct)
        => ReviewAsync(requestId, req, actorUserId, DynamicFormCloneRequestStatus.Rejected, ct);

    private async Task<DynamicFormCloneRequestRow> ReviewAsync(
        string requestId,
        ReviewDynamicFormCloneRequestReq req,
        string actorUserId,
        string status,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        if (string.IsNullOrWhiteSpace(requestId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { requestId });

        var doc = await _ctx.DynamicFormCloneRequests
            .Find(x => x.Id == requestId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_NOT_FOUND,
                new { requestId });

        if (!string.Equals(doc.AssignmentOwnerUserId, actorUserId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_REVIEW_FORBIDDEN,
                new { requestId, actorUserId, ownerUserId = doc.AssignmentOwnerUserId });

        if (!string.Equals(doc.Status, DynamicFormCloneRequestStatus.Pending, StringComparison.Ordinal))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_STATUS_INVALID,
                new { requestId, doc.Status });

        var now = DateTime.UtcNow;
        var update = Builders<DynamicFormCloneRequest>.Update
            .Set(x => x.Status, status)
            .Set(x => x.ReviewComment, TrimOptional(req.Comment))
            .Set(x => x.ReviewedAtUtc, now)
            .Set(x => x.ReviewedByUserId, actorUserId)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        var result = await _ctx.DynamicFormCloneRequests.UpdateOneAsync(
            x => x.Id == requestId &&
                 x.AssignmentOwnerUserId == actorUserId &&
                 x.Status == DynamicFormCloneRequestStatus.Pending &&
                 !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (result.MatchedCount == 0)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_STATUS_INVALID,
                new { requestId });

        var updated = await _ctx.DynamicFormCloneRequests
            .Find(x => x.Id == requestId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.DYNAMIC_FORM_CLONE_REQUEST_NOT_FOUND,
                new { requestId });

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == updated.WorkAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await NotifyCloneReviewedAsync(updated, assignment?.WorkType, actorUserId, ct);
        return ToRow(updated);
    }

    private async Task<PagedResult<DynamicFormCloneRequestRow>> SearchAsync(
        FilterDefinition<DynamicFormCloneRequest> filter,
        DynamicFormCloneRequestSearchReq req,
        CancellationToken ct)
    {
        var page = Math.Max(0, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);

        var total = await _ctx.DynamicFormCloneRequests.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.DynamicFormCloneRequests
            .Find(filter)
            .Sort(Builders<DynamicFormCloneRequest>.Sort.Descending(x => x.CreatedAtUtc))
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);

        return new PagedResult<DynamicFormCloneRequestRow>(
            rows.Select(ToRow).ToList(),
            total,
            page,
            pageSize);
    }

    private static FilterDefinition<DynamicFormCloneRequest> BuildBaseFilter(
        string workId,
        DynamicFormCloneRequestSearchReq req)
    {
        var f = Builders<DynamicFormCloneRequest>.Filter;
        var filter = f.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(workId))
            filter &= f.Eq(x => x.WorkId, workId.Trim());

        var status = NullIfWhiteSpace(req.Status);
        if (status is not null)
            filter &= f.Eq(x => x.Status, status);

        return filter;
    }

    private static DynamicFormCloneRequestRow ToRow(DynamicFormCloneRequest x)
        => new(
            x.Id,
            x.WorkId,
            x.WorkAssignmentId,
            x.AssignmentCode,
            x.DynamicFormTemplateId,
            x.DynamicFormTemplateCode,
            x.DynamicFormTemplateName,
            ToDto(x.Requester),
            ToDto(x.AssignmentOwner),
            x.Status,
            x.RequestReason,
            x.ReviewComment,
            x.ReviewedAtUtc,
            x.ReviewedByUserId,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);

    private static UserRefDTO? ToDto(UserRef? x)
        => x is null
            ? null
            : new UserRefDTO(
                x.UserId,
                x.Username,
                x.FullName,
                x.UnitId,
                x.UnitSymbol,
                x.UnitShortName,
                x.UnitName,
                x.PositionCode,
                x.PositionName);

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized(AppErrorCode.AUTH_ME_NOT_AVAILABLE);
    }

    private static string? TrimOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task NotifyCloneRequestedAsync(
        DynamicFormCloneRequest doc,
        string? workType,
        string actorUserId,
        CancellationToken ct)
        => _notifications.CreateManyAsync(
            new[]
            {
                new NotificationCommand
                {
                    RecipientUserId = doc.AssignmentOwnerUserId,
                    Type = UserNotificationTypes.DynamicFormCloneRequested,
                    Severity = UserNotificationSeverities.Info,
                    Title = "Có yêu cầu sao chép biểu mẫu động",
                    Body = BuildCloneNotificationBody(doc),
                    WorkId = doc.WorkId,
                    WorkAssignmentId = doc.WorkAssignmentId,
                    AssignmentCode = doc.AssignmentCode,
                    Category = UserNotificationCategories.Approval,
                    RequiresAction = true,
                    ActionState = UserNotificationActionStates.Open,
                    SourceEntityType = "DYNAMIC_FORM_CLONE_REQUEST",
                    SourceEntityId = doc.Id,
                    RequestId = doc.Id,
                    ActionUrl = BuildWorkDetailUrl(doc.WorkId, workType, "ACTIONS"),
                    ActorUserId = actorUserId,
                    SourceUserId = doc.RequesterUserId,
                    TargetUserId = doc.AssignmentOwnerUserId,
                    EventKey = $"dynamic-form-clone:{doc.Id}:requested:user:{doc.AssignmentOwnerUserId}"
                }
            },
            ct);

    private Task NotifyCloneReviewedAsync(
        DynamicFormCloneRequest doc,
        string? workType,
        string actorUserId,
        CancellationToken ct)
    {
        var approved = string.Equals(doc.Status, DynamicFormCloneRequestStatus.Approved, StringComparison.Ordinal);
        return _notifications.CreateManyAsync(
            new[]
            {
                new NotificationCommand
                {
                    RecipientUserId = doc.RequesterUserId,
                    Type = approved
                        ? UserNotificationTypes.DynamicFormCloneApproved
                        : UserNotificationTypes.DynamicFormCloneRejected,
                    Severity = approved ? UserNotificationSeverities.Info : UserNotificationSeverities.Warning,
                    Title = approved
                        ? "Yêu cầu sao chép biểu mẫu động đã được duyệt"
                        : "Yêu cầu sao chép biểu mẫu động bị từ chối",
                    Body = BuildCloneNotificationBody(doc),
                    WorkId = doc.WorkId,
                    WorkAssignmentId = doc.WorkAssignmentId,
                    AssignmentCode = doc.AssignmentCode,
                    Category = UserNotificationCategories.Approval,
                    RequiresAction = false,
                    ActionState = UserNotificationActionStates.Resolved,
                    SourceEntityType = "DYNAMIC_FORM_CLONE_REQUEST",
                    SourceEntityId = doc.Id,
                    RequestId = doc.Id,
                    ActionUrl = BuildWorkDetailUrl(doc.WorkId, workType, "ACTIONS"),
                    ResolvedAtUtc = doc.ReviewedAtUtc ?? DateTime.UtcNow,
                    ActorUserId = actorUserId,
                    SourceUserId = doc.AssignmentOwnerUserId,
                    TargetUserId = doc.RequesterUserId,
                    EventKey = $"dynamic-form-clone:{doc.Id}:{doc.Status.ToLowerInvariant()}:user:{doc.RequesterUserId}"
                }
            },
            ct);
    }

    private static string BuildCloneNotificationBody(DynamicFormCloneRequest doc)
    {
        var template = new[] { doc.DynamicFormTemplateCode, doc.DynamicFormTemplateName }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var label = template.Count > 0 ? string.Join(" - ", template) : doc.DynamicFormTemplateId;
        return $"{doc.AssignmentCode} - {label}";
    }

    private static string? BuildWorkDetailUrl(string? workId, string? workType, string tab)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return null;

        var prefix = string.Equals(workType, "INDICATOR", StringComparison.OrdinalIgnoreCase)
            ? "/indicators"
            : "/tasks";

        return string.Equals(tab, "ACTIONS", StringComparison.OrdinalIgnoreCase)
            ? $"{prefix}/{workId}?tab=ASSIGN&section=ACTIONS"
            : $"{prefix}/{workId}?tab={tab}";
    }
}
