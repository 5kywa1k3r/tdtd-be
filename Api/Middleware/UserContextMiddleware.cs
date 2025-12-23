//Middleware/UserContextMiddleware.cs
using System.Security.Claims;
using tdtd_be.Common;
using tdtd_be.DTOs.Auth;
namespace tdtd_be.Middleware
{
    public sealed class UserContextMiddleware : IMiddleware
    {
        private readonly UserContext _ctx;
        public UserContextMiddleware(UserContext ctx) => _ctx = ctx;

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (context.Items.TryGetValue("me", out var obj) && obj is MeResponse me)
            {
                _ctx.UserId = me.Id;
                _ctx.FullName = me.FullName;
                _ctx.UnitId = me.UnitId;
                _ctx.UnitName = me.UnitName;
                _ctx.UnitTypeId = me.UnitTypeId;
                _ctx.Roles = me.Roles ?? new List<string>();
            }

            await next(context);
        }
    }
}
