using tdtd_be.DTOs.Notifications;
using tdtd_be.Models;

namespace tdtd_be.Services.Notifications;

public interface INotificationService
{
    Task<List<UserNotification>> CreateManyAsync(
        IEnumerable<NotificationCommand> commands,
        CancellationToken ct = default);

    Task<NotificationSearchResponse> SearchAsync(
        NotificationSearchRequest request,
        string actorUserId,
        CancellationToken ct = default);

    Task<long> GetUnreadCountAsync(string actorUserId, CancellationToken ct = default);

    Task MarkReadAsync(string notificationId, string actorUserId, bool clicked, CancellationToken ct = default);

    Task MarkManyReadAsync(IEnumerable<string> notificationIds, string actorUserId, CancellationToken ct = default);

    Task MarkAllReadAsync(string actorUserId, CancellationToken ct = default);

    Task NotifyAssignmentAssignedAsync(WorkAssignment assignment, string actorUserId, CancellationToken ct = default);

    Task NotifyAssignmentHandoverAsync(
        WorkAssignment assignment,
        string fromAssigneeUserId,
        string toAssigneeUserId,
        string operationId,
        string actorUserId,
        CancellationToken ct = default);
}
