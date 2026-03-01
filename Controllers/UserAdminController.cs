using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Users.Admin;
using tdtd_be.Services;
using static tdtd_be.Services.UnitService;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize]
[Produces("application/json")]
public sealed class UsersAdminController : ControllerBase
{
    private readonly IUserAdminService _svc;
    public UsersAdminController(IUserAdminService svc) => _svc = svc;

    // ===================== CREATE =====================
    [HttpPost]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        var created = await _svc.CreateAsync(req, ct);

        // Location header: /api/admin/users/{id}
        return CreatedAtAction(nameof(GetById), new { userId = created.Id }, created);
    }

    // ===================== READ (optional but useful for CreatedAtAction) =====================
    // chưa expose thì vẫn nên giữ private route để CreatedAtAction không fail.
    // Cách khác: Created(string.Empty, created) — nhưng mất Location.
    [HttpGet("{userId}")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<UserResponse> GetById(string userId, CancellationToken ct)
        => _svc.GetByIdAsync(userId, ct); // NOTE: cần thêm method này trong service (nhỏ, sạch)

    // ===================== UPDATE =====================
    [HttpPut("{userId}")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<UserResponse> Update(string userId, [FromBody] UpdateUserRequest req, CancellationToken ct)
        => _svc.UpdateAsync(userId, req, ct);

    // ===================== SOFT DELETE =====================
    [HttpDelete("{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SoftDelete(string userId, CancellationToken ct)
    {
        await _svc.SoftDeleteAsync(userId, ct);
        return NoContent();
    }

    // ===================== RESET PASSWORD =====================
    [HttpPost("{userId}/reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetPassword(string userId, [FromBody] ResetPasswordRequest req, CancellationToken ct)
    {
        await _svc.ResetPasswordAsync(userId, req, ct);
        return NoContent();
    }

    // ===================== SEARCH (AppTable server mode) =====================
    [HttpGet("search")]
    [ProducesResponseType(typeof(PagedResult<UserSearchRow>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public Task<PagedResult<UserSearchRow>> Search(
        [FromQuery] string? q,
        [FromQuery] bool? isDeleted,
        [FromQuery] string? unitCodePrefix,
        [FromQuery] string? positionCode,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? sortField = null,
        [FromQuery] string? sortDirection = null,
        CancellationToken ct = default)
        => _svc.SearchUsersAsync(q, isDeleted, unitCodePrefix, positionCode, page, pageSize, sortField, sortDirection, ct);
}