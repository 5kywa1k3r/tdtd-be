using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Notifications;
using tdtd_be.Hubs;
using tdtd_be.Models;

namespace tdtd_be.Services.Notifications;

public sealed class NotificationService : INotificationService
{
    private readonly MongoDbContext _ctx;
    private readonly IHubContext<NotificationsHub> _hub;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(
        MongoDbContext ctx,
        IHubContext<NotificationsHub> hub,
        ILogger<NotificationService> log)
    {
        _ctx = ctx;
        _hub = hub;
        _log = log;
    }

    public async Task<List<UserNotification>> CreateManyAsync(
        IEnumerable<NotificationCommand> commands,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var normalized = commands
            .Where(IsValidCommand)
            .GroupBy(x => $"{x.RecipientUserId}:{x.EventKey}", StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();

        if (normalized.Count == 0)
            return new List<UserNotification>();

        try
        {
            var writes = normalized
                .Select(command =>
                {
                    var id = ObjectId.GenerateNewId().ToString();
                    var filter = Builders<UserNotification>.Filter.Eq(x => x.RecipientUserId, command.RecipientUserId)
                                 & Builders<UserNotification>.Filter.Eq(x => x.EventKey, command.EventKey)
                                 & Builders<UserNotification>.Filter.Eq(x => x.IsDeleted, false);

                    var update = Builders<UserNotification>.Update
                        .SetOnInsert(x => x.Id, id)
                        .SetOnInsert(x => x.RecipientUserId, command.RecipientUserId)
                        .SetOnInsert(x => x.Type, command.Type)
                        .SetOnInsert(x => x.Severity, command.Severity)
                        .SetOnInsert(x => x.Title, command.Title)
                        .SetOnInsert(x => x.Body, command.Body)
                        .SetOnInsert(x => x.WorkId, NullIfWhiteSpace(command.WorkId))
                        .SetOnInsert(x => x.WorkType, command.WorkType)
                        .SetOnInsert(x => x.WorkName, command.WorkName)
                        .SetOnInsert(x => x.WorkAssignmentId, NullIfWhiteSpace(command.WorkAssignmentId))
                        .SetOnInsert(x => x.AssignmentCode, command.AssignmentCode)
                        .SetOnInsert(x => x.WorkReportPeriodId, NullIfWhiteSpace(command.WorkReportPeriodId))
                        .SetOnInsert(x => x.WorkAssignmentReportId, NullIfWhiteSpace(command.WorkAssignmentReportId))
                        .SetOnInsert(x => x.Category, NullIfWhiteSpace(command.Category))
                        .SetOnInsert(x => x.RequiresAction, command.RequiresAction)
                        .SetOnInsert(x => x.ActionState, NullIfWhiteSpace(command.ActionState))
                        .SetOnInsert(x => x.SourceEntityType, NullIfWhiteSpace(command.SourceEntityType))
                        .SetOnInsert(x => x.SourceEntityId, NullIfWhiteSpace(command.SourceEntityId))
                        .SetOnInsert(x => x.RequestId, NullIfWhiteSpace(command.RequestId))
                        .SetOnInsert(x => x.ActionUrl, NullIfWhiteSpace(command.ActionUrl))
                        .SetOnInsert(x => x.ResolvedAtUtc, command.ResolvedAtUtc)
                        .SetOnInsert(x => x.ActorUserId, NullIfWhiteSpace(command.ActorUserId))
                        .SetOnInsert(x => x.SourceUserId, NullIfWhiteSpace(command.SourceUserId))
                        .SetOnInsert(x => x.TargetUserId, NullIfWhiteSpace(command.TargetUserId))
                        .SetOnInsert(x => x.DueAtUtc, command.DueAtUtc)
                        .SetOnInsert(x => x.OccurredAtUtc, command.OccurredAtUtc == default ? now : command.OccurredAtUtc)
                        .SetOnInsert(x => x.EventKey, command.EventKey)
                        .SetOnInsert(x => x.IsDeleted, false)
                        .SetOnInsert(x => x.CreatedAtUtc, now)
                        .Set(x => x.UpdatedAtUtc, now);

                    return new UpdateOneModel<UserNotification>(filter, update) { IsUpsert = true };
                })
                .Cast<WriteModel<UserNotification>>()
                .ToList();

            var result = await _ctx.Notifications.BulkWriteAsync(
                writes,
                new BulkWriteOptions { IsOrdered = false },
                ct);

            var createdIds = result.Upserts
                .Select(x => x.Id)
                .Where(x => x is not null)
                .Select(ToObjectIdString)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (createdIds.Count == 0)
                return new List<UserNotification>();

            var created = await _ctx.Notifications
                .Find(x => createdIds.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync(ct);

            await PushRealtimeAsync(created, ct);
            return created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to create notifications. requested={count}", normalized.Count);
            return new List<UserNotification>();
        }
    }

    public async Task<NotificationSearchResponse> SearchAsync(
        NotificationSearchRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var pageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 1, 50);
        var fb = Builders<UserNotification>.Filter;
        var filter = fb.Eq(x => x.RecipientUserId, actorUserId)
                     & fb.Eq(x => x.IsDeleted, false);

        var workId = (request.WorkId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(workId))
            filter &= fb.Eq(x => x.WorkId, workId);

        var workAssignmentId = (request.WorkAssignmentId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(workAssignmentId))
            filter &= fb.Eq(x => x.WorkAssignmentId, workAssignmentId);

        if (request.UnreadOnly == true)
            filter &= fb.Eq(x => x.ReadAtUtc, null);

        var types = (request.Types ?? new List<string>())
            .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (types.Count > 0)
            filter &= fb.In(x => x.Type, types);

        var category = (request.Category ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(category))
            filter &= fb.Eq(x => x.Category, category);

        if (request.RequiresAction.HasValue)
            filter &= fb.Eq(x => x.RequiresAction, request.RequiresAction.Value);

        var actionState = (request.ActionState ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(actionState))
            filter &= fb.Eq(x => x.ActionState, actionState);

        if (request.CursorOccurredAtUtc.HasValue && !string.IsNullOrWhiteSpace(request.CursorId))
        {
            var occurredAt = request.CursorOccurredAtUtc.Value;
            var cursorId = request.CursorId.Trim();
            filter &= fb.Or(
                fb.Lt(x => x.OccurredAtUtc, occurredAt),
                fb.And(
                    fb.Eq(x => x.OccurredAtUtc, occurredAt),
                    fb.Lt(x => x.Id, cursorId)));
        }

        var rows = await _ctx.Notifications
            .Find(filter)
            .Sort(Builders<UserNotification>.Sort
                .Descending(x => x.OccurredAtUtc)
                .Descending(x => x.Id))
            .Limit(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        var pageRows = rows.Take(pageSize).ToList();
        var last = pageRows.LastOrDefault();
        var unreadCount = await GetUnreadCountAsync(actorUserId, ct);

        return new NotificationSearchResponse
        {
            Items = pageRows.Select(ToDto).ToList(),
            HasMore = hasMore,
            NextCursorOccurredAtUtc = hasMore ? last?.OccurredAtUtc : null,
            NextCursorId = hasMore ? last?.Id : null,
            UnreadCount = unreadCount
        };
    }

    public async Task<long> GetUnreadCountAsync(string actorUserId, CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        return await _ctx.Notifications.CountDocumentsAsync(
            x => x.RecipientUserId == actorUserId && !x.IsDeleted && x.ReadAtUtc == null,
            cancellationToken: ct);
    }

    public async Task MarkReadAsync(string notificationId, string actorUserId, bool clicked, CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        if (string.IsNullOrWhiteSpace(notificationId))
            return;

        var now = DateTime.UtcNow;
        var update = Builders<UserNotification>.Update
            .Set(x => x.ReadAtUtc, now)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        if (clicked)
            update = update.Set(x => x.ClickedAtUtc, now);

        var result = await _ctx.Notifications.UpdateOneAsync(
            x => x.Id == notificationId && x.RecipientUserId == actorUserId && !x.IsDeleted && x.ReadAtUtc == null,
            update,
            cancellationToken: ct);

        if (result.ModifiedCount > 0)
        {
            await PushRealtimeAsync(
                actorUserId,
                new NotificationRealtimeMessage
                {
                    NotificationId = notificationId.Trim(),
                    Type = "NOTIFICATION_READ",
                    ChangeKind = NotificationRealtimeChangeKinds.Read,
                    OccurredAtUtc = now
                },
                ct);
        }
    }

    public async Task MarkManyReadAsync(IEnumerable<string> notificationIds, string actorUserId, CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var ids = notificationIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var result = await _ctx.Notifications.UpdateManyAsync(
            x => ids.Contains(x.Id) && x.RecipientUserId == actorUserId && !x.IsDeleted && x.ReadAtUtc == null,
            Builders<UserNotification>.Update
                .Set(x => x.ReadAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        if (result.ModifiedCount > 0)
        {
            await PushRealtimeAsync(
                actorUserId,
                new NotificationRealtimeMessage
                {
                    NotificationId = ids[0],
                    Type = "NOTIFICATIONS_READ",
                    ChangeKind = NotificationRealtimeChangeKinds.ReadMany,
                    OccurredAtUtc = now
                },
                ct);
        }
    }

    public async Task MarkAllReadAsync(string actorUserId, CancellationToken ct = default)
    {
        EnsureActor(actorUserId);

        var now = DateTime.UtcNow;
        var result = await _ctx.Notifications.UpdateManyAsync(
            x => x.RecipientUserId == actorUserId && !x.IsDeleted && x.ReadAtUtc == null,
            Builders<UserNotification>.Update
                .Set(x => x.ReadAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        if (result.ModifiedCount > 0)
        {
            await PushRealtimeAsync(
                actorUserId,
                new NotificationRealtimeMessage
                {
                    NotificationId = string.Empty,
                    Type = "NOTIFICATIONS_READ_ALL",
                    ChangeKind = NotificationRealtimeChangeKinds.ReadAll,
                    OccurredAtUtc = now
                },
                ct);
        }
    }

    public Task NotifyAssignmentAssignedAsync(
        WorkAssignment assignment,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (assignment is null || !assignment.IsActive)
            return Task.CompletedTask;

        var dueTicks = assignment.DueAtUtc?.Ticks.ToString() ?? "none";
        var recipients = (assignment.Assignees ?? new List<UserRef>())
            .Select(x => x.UserId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var commands = recipients.Select(userId => new NotificationCommand
        {
            RecipientUserId = userId,
            Type = UserNotificationTypes.AssignmentAssigned,
            Severity = UserNotificationSeverities.Info,
            Title = "Bạn được giao việc",
            Body = BuildAssignmentBody(assignment),
            WorkId = assignment.WorkId,
            WorkAssignmentId = assignment.Id,
            AssignmentCode = assignment.Code,
            Category = UserNotificationCategories.General,
            RequiresAction = true,
            ActionState = UserNotificationActionStates.Open,
            SourceEntityType = "WORK_ASSIGNMENT",
            SourceEntityId = assignment.Id,
            ActionUrl = BuildWorkDetailUrl(assignment.WorkId, assignment.WorkType, "ASSIGN"),
            ActorUserId = actorUserId,
            TargetUserId = userId,
            DueAtUtc = assignment.DueAtUtc,
            EventKey = $"assignment-assigned:{assignment.Id}:due:{dueTicks}:user:{userId}"
        });

        return CreateManyAsync(commands, ct);
    }

    public Task NotifyAssignmentHandoverAsync(
        WorkAssignment assignment,
        string fromAssigneeUserId,
        string toAssigneeUserId,
        string operationId,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (assignment is null ||
            string.IsNullOrWhiteSpace(fromAssigneeUserId) ||
            string.IsNullOrWhiteSpace(toAssigneeUserId) ||
            string.IsNullOrWhiteSpace(operationId))
        {
            return Task.CompletedTask;
        }

        var commands = new[]
        {
            new NotificationCommand
            {
                RecipientUserId = toAssigneeUserId,
                Type = UserNotificationTypes.AssignmentHandoverReceived,
                Severity = UserNotificationSeverities.Info,
                Title = "Bạn nhận bàn giao phần việc",
                Body = BuildAssignmentBody(assignment),
                WorkId = assignment.WorkId,
                WorkAssignmentId = assignment.Id,
                AssignmentCode = assignment.Code,
                Category = UserNotificationCategories.Handover,
                RequiresAction = true,
                ActionState = UserNotificationActionStates.Open,
                SourceEntityType = "WORK_ASSIGNMENT",
                SourceEntityId = assignment.Id,
                ActionUrl = BuildWorkDetailUrl(assignment.WorkId, assignment.WorkType, "ACTIONS"),
                ActorUserId = actorUserId,
                SourceUserId = fromAssigneeUserId,
                TargetUserId = toAssigneeUserId,
                DueAtUtc = assignment.DueAtUtc,
                EventKey = $"assignment-handover:{operationId}:received:user:{toAssigneeUserId}"
            },
            new NotificationCommand
            {
                RecipientUserId = fromAssigneeUserId,
                Type = UserNotificationTypes.AssignmentHandoverCompleted,
                Severity = UserNotificationSeverities.Info,
                Title = "Bạn đã bàn giao phần việc",
                Body = BuildAssignmentBody(assignment),
                WorkId = assignment.WorkId,
                WorkAssignmentId = assignment.Id,
                AssignmentCode = assignment.Code,
                Category = UserNotificationCategories.Handover,
                RequiresAction = false,
                ActionState = UserNotificationActionStates.Resolved,
                SourceEntityType = "WORK_ASSIGNMENT",
                SourceEntityId = assignment.Id,
                ActionUrl = BuildWorkDetailUrl(assignment.WorkId, assignment.WorkType, "ACTIONS"),
                ResolvedAtUtc = DateTime.UtcNow,
                ActorUserId = actorUserId,
                SourceUserId = fromAssigneeUserId,
                TargetUserId = toAssigneeUserId,
                DueAtUtc = assignment.DueAtUtc,
                EventKey = $"assignment-handover:{operationId}:completed:user:{fromAssigneeUserId}"
            }
        };

        return CreateManyAsync(commands, ct);
    }

    private async Task PushRealtimeAsync(List<UserNotification> notifications, CancellationToken ct)
    {
        foreach (var notification in notifications)
        {
            await PushRealtimeAsync(
                notification.RecipientUserId,
                new NotificationRealtimeMessage
                {
                    NotificationId = notification.Id,
                    Type = notification.Type,
                    ChangeKind = NotificationRealtimeChangeKinds.Created,
                    OccurredAtUtc = notification.OccurredAtUtc
                },
                ct);
        }
    }

    private async Task PushRealtimeAsync(
        string recipientUserId,
        NotificationRealtimeMessage message,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipientUserId))
            return;

        try
        {
            await _hub.Clients
                .Group(NotificationsHub.UserGroup(recipientUserId))
                .SendAsync("notificationChanged", message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(
                ex,
                "Failed to push notification realtime signal. notificationId={notificationId} recipientUserId={recipientUserId} changeKind={changeKind}",
                message.NotificationId,
                recipientUserId,
                message.ChangeKind);
        }
    }

    private static NotificationRowDto ToDto(UserNotification x) => new()
    {
        Id = x.Id,
        Type = x.Type,
        Severity = x.Severity,
        Title = x.Title,
        Body = x.Body,
        WorkId = x.WorkId,
        WorkType = x.WorkType,
        WorkName = x.WorkName,
        WorkAssignmentId = x.WorkAssignmentId,
        AssignmentCode = x.AssignmentCode,
        WorkReportPeriodId = x.WorkReportPeriodId,
        WorkAssignmentReportId = x.WorkAssignmentReportId,
        Category = x.Category,
        RequiresAction = x.RequiresAction,
        ActionState = x.ActionState,
        SourceEntityType = x.SourceEntityType,
        SourceEntityId = x.SourceEntityId,
        RequestId = x.RequestId,
        ActionUrl = x.ActionUrl,
        ResolvedAtUtc = x.ResolvedAtUtc,
        ActorUserId = x.ActorUserId,
        SourceUserId = x.SourceUserId,
        TargetUserId = x.TargetUserId,
        DueAtUtc = x.DueAtUtc,
        OccurredAtUtc = x.OccurredAtUtc,
        ReadAtUtc = x.ReadAtUtc,
        ClickedAtUtc = x.ClickedAtUtc,
        CreatedAtUtc = x.CreatedAtUtc
    };

    private static bool IsValidCommand(NotificationCommand command)
        => command is not null &&
           !string.IsNullOrWhiteSpace(command.RecipientUserId) &&
           !string.IsNullOrWhiteSpace(command.Type) &&
           !string.IsNullOrWhiteSpace(command.EventKey) &&
           !string.IsNullOrWhiteSpace(command.Title);

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ToObjectIdString(BsonValue value)
        => value switch
        {
            BsonObjectId objectId => objectId.Value.ToString(),
            BsonString str => str.Value,
            _ => value.ToString()
        };

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized(AppErrorCode.NOTIFICATION_USER_REQUIRED);
    }

    private static string BuildAssignmentBody(WorkAssignment assignment)
    {
        var template = assignment.DynamicFormTemplateName ?? assignment.DynamicExcelName;
        if (!string.IsNullOrWhiteSpace(template))
            return $"{assignment.Code} - {template}";

        return assignment.Code ?? assignment.Id;
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
