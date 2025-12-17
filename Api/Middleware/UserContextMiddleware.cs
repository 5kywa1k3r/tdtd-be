//Middleware/UserContextMiddleware.cs
using System.Security.Claims;
using tdtd_be.Common;
namespace tdtd_be.Middleware
{
    public sealed class UserContextMiddleware : IMiddleware
    {
        private readonly UserContext _ctx;
        public UserContextMiddleware(UserContext ctx) => _ctx = ctx;

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                _ctx.UserId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
                _ctx.FullName = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name);

                _ctx.JobTitle = user.FindFirstValue(AppClaimTypes.JobTitle);
                _ctx.UnitId = user.FindFirstValue(AppClaimTypes.UnitId);
                _ctx.UnitName = user.FindFirstValue(AppClaimTypes.UnitName);

                _ctx.Roles = user.FindAll(ClaimTypes.Role).Select(x => x.Value).Distinct().ToList();
            }

            await next(context);
        }
    }
}
