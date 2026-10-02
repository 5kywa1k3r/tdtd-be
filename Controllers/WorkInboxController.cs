using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using System.Security.Claims;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Notifications;
using tdtd_be.DTOs.WorkInbox;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkInbox;

namespace tdtd_be.Controllers;

[ApiController]
[Authorize]
[Route("api/me/work-inbox")]
public sealed class WorkInboxController(WorkInboxService inbox, INotificationService notifications) : ControllerBase
{
    private string Actor()
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!ObjectId.TryParse(actor, out _)) throw AppExceptionFactory.Unauthorized(AppErrorCode.NOTIFICATION_USER_REQUIRED);
        return actor!;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await inbox.SummaryAsync(Actor(), ct));

    [HttpPost("items/search")]
    public async Task<IActionResult> Items([FromBody] WorkInboxSearchRequest request, CancellationToken ct)
    {
        if (!Valid(request)) return BadRequest(new { message = "Bộ lọc hoặc vị trí trang không hợp lệ." });
        return Ok(await inbox.SearchAsync(request, Actor(), ct));
    }

    [HttpPost("groups/search")]
    public async Task<IActionResult> Groups([FromBody] WorkInboxSearchRequest request, CancellationToken ct)
    {
        if (!Valid(request, group: true)) return BadRequest(new { message = "Bộ lọc không hợp lệ." });
        return Ok(await inbox.GroupsAsync(request, Actor(), ct));
    }

    [HttpGet("items/{id}")]
    public async Task<IActionResult> Item(string id, CancellationToken ct)
    {
        var item = await inbox.ItemAsync(id, Actor(), ct);
        return item is null ? NotFound(new { message = "Việc đã thay đổi hoặc bạn không còn quyền truy cập." }) : Ok(item);
    }

    [HttpGet("notifications/recent")]
    public async Task<IActionResult> Recent(CancellationToken ct) => Ok(await inbox.RecentAsync(Actor(), ct));

    [HttpPost("notifications/history/search")]
    public async Task<IActionResult> History([FromBody] NotificationSearchRequest request, CancellationToken ct)
    {
        if (!ValidId(request.WorkId) || !ValidId(request.WorkAssignmentId) ||
            (request.CursorId is not null && (!ObjectId.TryParse(request.CursorId, out _) || request.CursorOccurredAtUtc is null)))
            return BadRequest(new { message = "Bộ lọc thông báo không hợp lệ." });
        return Ok(await inbox.HistoryAsync(request, Actor(), ct));
    }

    [HttpPost("notifications/read")]
    public async Task<IActionResult> Read([FromBody] MarkNotificationsReadRequest request, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count > 50 || request.Ids.Any(id => !ObjectId.TryParse(id, out _))) return BadRequest();
        await notifications.MarkManyReadAsync(request.Ids, Actor(), ct);
        return NoContent();
    }

    private static bool ValidId(string? id) => string.IsNullOrWhiteSpace(id) || ObjectId.TryParse(id, out _);
    private static bool Valid(WorkInboxSearchRequest request, bool group = false)
    {
        if (!ValidId(request.WorkId) || !ValidId(request.AssignmentId) || request.PageSize is < 1 or > 50) return false;
        if (request.Function is not null and not "" and not "REPORT" and not "REVIEW" and not "ASSIGNMENT" and not "REQUEST") return false;
        if (group) return request.CursorId is null || ObjectId.TryParse(request.CursorId, out _);
        var hasCursor = request.CursorId is not null || request.CursorPriority is not null || request.CursorDueAtUtc is not null;
        return !hasCursor || (request.CursorId is not null && request.CursorPriority is >= 0 and <= 2 && request.CursorDueAtUtc is not null);
    }
}
