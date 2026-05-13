// Controllers/AuthController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.Services;

namespace tdtd_be.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public sealed class AuthController : ControllerBase
    {
        private const string RefreshCookieName = "refresh_token";

        private readonly AuthService _svc;
        private readonly JwtService _jwt;

        public AuthController(AuthService svc, JwtService jwt)
        {
            _svc = svc;
            _jwt = jwt;
        }

        private void SetRefreshCookie(string refreshRaw, int days)
        {
            Response.Cookies.Append(RefreshCookieName, refreshRaw, new CookieOptions
            {
                HttpOnly = true,
                Secure = false,                 // dev: false; prod https: true
                SameSite = SameSiteMode.Strict, // nếu cross-site bị chặn: đổi Lax/None(+Secure=true)
                Expires = DateTimeOffset.UtcNow.AddDays(days),
                Path = "/api/auth"
            });
        }

        private void ClearRefreshCookie()
        {
            Response.Cookies.Append(RefreshCookieName, "", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,                 // prod: true
                SameSite = SameSiteMode.Strict,
                Expires = DateTimeOffset.UtcNow.AddDays(-1),
                Path = "/api/auth"
            });
        }

        private string? GetRefreshCookie()
        {
            Request.Cookies.TryGetValue(RefreshCookieName, out var v);
            return v;
        }

        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
        {
            var (resp, refreshRaw) = await _svc.LoginAsync(req, ct);
            SetRefreshCookie(refreshRaw, _jwt.RefreshTokenDays());
            return Ok(resp);
        }

        // FE gọi endpoint này, refresh token tự gửi qua cookie HttpOnly
        [HttpPost("refresh")]
        [AllowAnonymous]
        public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken ct)
        {
            var refreshRaw = GetRefreshCookie();
            if (string.IsNullOrWhiteSpace(refreshRaw))
                throw AppExceptionFactory.Unauthorized(AppErrorCode.AUTH_REFRESH_TOKEN_MISSING);

            var (resp, newRefreshRaw) = await _svc.RefreshAsync(refreshRaw, ct);

            // rotation: set cookie mới
            SetRefreshCookie(newRefreshRaw, _jwt.RefreshTokenDays());
            return Ok(resp);
        }

        [HttpPost("logout")]
        [AllowAnonymous]
        public async Task<IActionResult> Logout(CancellationToken ct)
        {
            var refreshRaw = GetRefreshCookie();
            if (!string.IsNullOrWhiteSpace(refreshRaw))
            {
                await _svc.LogoutAsync(refreshRaw, ct);
            }

            ClearRefreshCookie();
            return NoContent();
        }

        // Trả me dựa trên middleware Redis set HttpContext.Items["me"]
        [HttpGet("me")]
        [Authorize]
        public ActionResult<MeResponse> Me()
        {
            if (HttpContext.Items.TryGetValue("me", out var obj) && obj is MeResponse me)
                return Ok(me);

            // nếu token hợp lệ nhưng middleware chưa set (hiếm)
            throw AppExceptionFactory.Unauthorized(AppErrorCode.AUTH_ME_NOT_AVAILABLE);
        }
    }
}
