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
    public async Task<IActionResult> Summary(CancellationToken ct, [FromQuery] string? workId = null,
        [FromQuery] string? searchText = null, [FromQuery] string? state = null,
        [FromQuery] DateTime? dueFromUtc = null, [FromQuery] DateTime? dueBeforeUtc = null,
        [FromQuery] string? handled = "false", [FromQuery] string? assignmentId = null)
    {
        if (Request.Query.TryGetValue(nameof(handled), out var handledQuery)) handled = handledQuery.ToString();
        bool? handledScope;
        if (string.IsNullOrWhiteSpace(handled) || string.Equals(handled, "all", StringComparison.OrdinalIgnoreCase)) handledScope = null;
        else if (bool.TryParse(handled, out var parsedHandled)) handledScope = parsedHandled;
        else return BadRequest(new { message = "Trạng thái xử lý không hợp lệ." });
        var scope = new WorkInboxSearchRequest { WorkId = workId, AssignmentId = assignmentId, SearchText = searchText, State = state,
            DueFromUtc = dueFromUtc, DueBeforeUtc = dueBeforeUtc, Handled = handledScope };
        if (!Valid(scope)) return BadRequest(new { message = "Bộ lọc công việc không hợp lệ." });
        return Ok(await inbox.SummaryAsync(Actor(), ct, workId, searchText, state, dueFromUtc, dueBeforeUtc, handledScope, assignmentId));
    }

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

    [HttpPost("assignment-groups/search")]
    public async Task<IActionResult> AssignmentGroups([FromBody] WorkInboxSearchRequest request, CancellationToken ct)
    {
        if (!Valid(request) || request.Function is not "REPORT" and not "REVIEW" ||
            (request.CursorId is not null && !ObjectId.TryParse(request.CursorId, out _)))
            return BadRequest(new { message = "Bộ lọc hoặc vị trí trang đầu việc không hợp lệ." });
        return Ok(await inbox.AssignmentGroupsAsync(request, Actor(), ct));
    }

    [HttpGet("items/{id}")]
    public async Task<IActionResult> Item(string id, CancellationToken ct)
    {
        var item = await inbox.ItemAsync(id, Actor(), ct);
        return item is null ? NotFound(new { message = "Việc đã thay đổi hoặc bạn không còn quyền truy cập." }) : Ok(item);
    }

    [HttpGet("notifications/recent")]
    public async Task<IActionResult> Recent(CancellationToken ct) => Ok(await inbox.RecentAsync(Actor(), ct));

    [HttpGet("notifications/groups/recent")]
    public async Task<IActionResult> RecentGroups(CancellationToken ct) => Ok(await inbox.RecentNotificationGroupsAsync(Actor(), ct));

    [HttpPost("notifications/groups/search")]
    public async Task<IActionResult> NotificationGroups([FromBody] NotificationSearchRequest request, CancellationToken ct)
    {
        if (!ValidNotification(request, grouped: true)) return BadRequest(new { message = "Bộ lọc hoặc vị trí trang thông báo không hợp lệ." });
        return Ok(await inbox.NotificationGroupsAsync(request, Actor(), ct));
    }

    [HttpPost("notifications/groups/read")]
    public async Task<IActionResult> ReadGroups([FromBody] WorkInboxNotificationGroupReadRequest request, CancellationToken ct)
    {
        if (request.Groups is null || request.Groups.Count > 20 || request.Groups.Any(x => x is null ||
            !WorkInboxNotificationGroups.ValidGroupKey(x.GroupKey) || x.AsOfUtc == default || x.AsOfUtc.ToUniversalTime() > DateTime.UtcNow)) return BadRequest();
        await inbox.MarkNotificationGroupsReadAsync(request.Groups, Actor(), ct);
        return NoContent();
    }

    [HttpGet("assignments/{id}")]
    public async Task<IActionResult> AssignmentDetail(string id, CancellationToken ct)
    {
        var detail = await inbox.AssignmentDetailAsync(id, Actor(), ct);
        return detail is null ? NotFound(new { message = "Việc đã ngừng hiệu lực hoặc bạn không còn quyền truy cập." }) : Ok(detail);
    }

    [HttpPost("notifications/history/search")]
    public async Task<IActionResult> History([FromBody] NotificationSearchRequest request, CancellationToken ct)
    {
        if (!ValidNotification(request))
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
    private static bool ValidRange(DateTime? from, DateTime? before) => !from.HasValue || !before.HasValue || from.Value < before.Value;
    private static bool ValidPage(int? number, int size, bool hasCursor) => !number.HasValue ||
        (number.Value >= 1 && number.Value - 1 <= int.MaxValue / size && !hasCursor);
    private static bool ValidNotification(NotificationSearchRequest request, bool grouped = false)
    {
        var hasCursor = request.CursorId is not null || request.CursorOccurredAtUtc is not null;
        return ValidId(request.WorkId) && ValidId(request.WorkAssignmentId) && request.PageSize is >= 1 and <= 50 &&
            ValidRange(request.OccurredFromUtc, request.OccurredBeforeUtc) && ValidPage(request.PageNumber, request.PageSize, hasCursor) &&
            (!hasCursor || (request.CursorOccurredAtUtc is not null && (grouped ? WorkInboxNotificationGroups.ValidGroupKey(request.CursorId)
                : ObjectId.TryParse(request.CursorId, out _))));
    }
    private static bool Valid(WorkInboxSearchRequest request, bool group = false)
    {
        if (!ValidId(request.WorkId) || !ValidId(request.AssignmentId) || request.PageSize is < 1 or > 50) return false;
        if (request.SearchText?.Length > 200 || !ValidRange(request.DueFromUtc, request.DueBeforeUtc)) return false;
        if (request.State is not null and not "" and not "OPEN" and not "DUE_SOON" and not "OVERDUE") return false;
        if (request.Function is not null and not "" and not "REPORT" and not "REVIEW" and not "ASSIGNMENT" and not "REQUEST") return false;
        var hasCursor = request.CursorId is not null || request.CursorPriority is not null || request.CursorDueAtUtc is not null;
        if (!ValidPage(request.PageNumber, request.PageSize, hasCursor)) return false;
        if (group) return request.CursorId is null || ObjectId.TryParse(request.CursorId, out _);
        return !hasCursor || (request.CursorId is not null && request.CursorPriority is >= 0 and <= 2 && request.CursorDueAtUtc is not null);
    }
}
