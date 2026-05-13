using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Notifications;
using tdtd_be.Services.Notifications;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationsController(INotificationService notifications)
    {
        _notifications = notifications;
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<NotificationUnreadCountResponse>> GetUnreadCount(CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var count = await _notifications.GetUnreadCountAsync(actorUserId, ct);
        return Ok(new NotificationUnreadCountResponse { UnreadCount = count });
    }

    [HttpPost("search")]
    public async Task<ActionResult<NotificationSearchResponse>> Search(
        [FromBody] NotificationSearchRequest request,
        CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        var rs = await _notifications.SearchAsync(request ?? new NotificationSearchRequest(), actorUserId, ct);
        return Ok(rs);
    }

    [HttpPost("{id}/read")]
    public async Task<IActionResult> MarkRead([FromRoute] string id, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        await _notifications.MarkReadAsync(id, actorUserId, clicked: true, ct);
        return NoContent();
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkManyRead([FromBody] MarkNotificationsReadRequest request, CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        await _notifications.MarkManyReadAsync(request?.Ids ?? new List<string>(), actorUserId, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var actorUserId = GetActorUserId();
        await _notifications.MarkAllReadAsync(actorUserId, ct);
        return NoContent();
    }

    private string GetActorUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? User.FindFirstValue("sub")
           ?? throw AppExceptionFactory.Unauthorized(AppErrorCode.NOTIFICATION_USER_REQUIRED);
}
