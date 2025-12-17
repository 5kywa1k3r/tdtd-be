//Controllers/AuthController.cs
using Microsoft.AspNetCore.Mvc;
using tdtd_be.DTOs.Auth;
using tdtd_be.Services;

namespace tdtd_be.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public sealed class AuthController : ControllerBase
    {
        private readonly AuthService _svc;
        public AuthController(AuthService svc) => _svc = svc;

        [HttpPost("login")]
        public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
            => Ok(await _svc.LoginAsync(req, ct));

        [HttpPost("refresh")]
        public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshRequest req, CancellationToken ct)
            => Ok(await _svc.RefreshAsync(req, ct));
    }
}
