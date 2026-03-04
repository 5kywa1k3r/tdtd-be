using System.Text.Json;

namespace tdtd_be.Common.Middleware;

public sealed class ApiExceptionMiddleware : IMiddleware
{
    private readonly ILogger<ApiExceptionMiddleware> _log;
    private readonly IHostEnvironment _env;

    public ApiExceptionMiddleware(ILogger<ApiExceptionMiddleware> log, IHostEnvironment env)
    {
        _log = log;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        try
        {
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client hủy request -> không cần trả JSON (hoặc trả 499 nếu muốn)
            ctx.Response.StatusCode = 499; // nginx style; optional
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled exception: {Path}", ctx.Request.Path);

            var (status, message) = Map(ex);

            // luôn trả JSON chuẩn
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.StatusCode = status;

            // prod: không lộ stack trace
            var payload = new ApiErrorResponse(message);

            // dev: có thể cho thêm traceId để debug
            // (không đưa stack)
            payload = payload with { TraceId = ctx.TraceIdentifier };

            await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload));
        }
    }

    private static (int status, string message) Map(Exception ex)
    {
        // ✅ Lỗi nghiệp vụ / validation: trả 400 + message sạch
        if (ex is InvalidOperationException)
            return (StatusCodes.Status400BadRequest, ex.Message);

        if (ex is ArgumentException)
            return (StatusCodes.Status400BadRequest, ex.Message);

        // ✅ Auth-related
        if (ex is UnauthorizedAccessException)
            return (StatusCodes.Status401Unauthorized, "Bạn không có quyền thực hiện thao tác này.");

        // ✅ Còn lại: 500
        return (StatusCodes.Status500InternalServerError, "Có lỗi hệ thống. Vui lòng thử lại.");
    }

    private record ApiErrorResponse(string Message)
    {
        public string? TraceId { get; init; }
    }
}