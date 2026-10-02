using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace tdtd_be.Controllers;

// Explicit retirement boundary. Storage, producers and realtime remain shared with Work Inbox.
[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    [HttpGet("{**path}")]
    [HttpPost("{**path}")]
    public IActionResult Retired() => StatusCode(StatusCodes.Status410Gone,
        new { code = "NOTIFICATION_CENTER_MOVED", message = "Thông báo đã chuyển sang Công việc cần thực hiện.", api = "/api/me/work-inbox" });
}
