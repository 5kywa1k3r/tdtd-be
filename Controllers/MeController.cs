//Controllers/MeController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Middleware;

namespace tdtd_be.Controllers
{
    [ApiController]
    [Route("api/me")]
    public sealed class MeController : ControllerBase
    {
        private readonly UserContext _ctx;
        public MeController(UserContext ctx) => _ctx = ctx;

        [Authorize]
        [HttpGet]
        public IActionResult GetMe() => Ok(_ctx);
    }
}
