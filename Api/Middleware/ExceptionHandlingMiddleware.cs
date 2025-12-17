//Middleware/ExceptionHandlingMiddleware.cs
using System.Text.Json;
using tdtd_be.Common.Exceptions;

namespace tdtd_be.Middleware
{
    public sealed class ExceptionHandlingMiddleware : IMiddleware
    {
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            try { await next(context); }
            catch (AppException ex)
            {
                context.Response.StatusCode = ex.StatusCode;
                context.Response.ContentType = "application/json";

                var payload = new { code = ex.Code, message = ex.Message };
                await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                var payload = new { code = "INTERNAL_ERROR", message = "Có lỗi hệ thống.", detail = ex.Message };
                await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
            }
        }
    }
}
