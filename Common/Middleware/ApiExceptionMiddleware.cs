using System.Text.Json;
using tdtd_be.Common.Errors;

namespace tdtd_be.Common.Middleware;

public sealed class ApiExceptionMiddleware : IMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<ApiExceptionMiddleware> _log;

    public ApiExceptionMiddleware(ILogger<ApiExceptionMiddleware> log, IHostEnvironment env)
    {
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        try
        {
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.Response.StatusCode = 499;
        }
        catch (AppException ex)
        {
            await WriteAppExceptionAsync(ctx, ex);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled exception: {Path}", ctx.Request.Path);
            await WriteAppExceptionAsync(ctx, MapLegacy(ex));
        }
    }

    private async Task WriteAppExceptionAsync(HttpContext ctx, AppException ex)
    {
        var descriptor = ex.Descriptor;
        if (descriptor.HttpStatus >= StatusCodes.Status500InternalServerError)
        {
            _log.LogError(ex, "Application exception: {Path} {ErrorCode}", ctx.Request.Path, descriptor.Code);
        }
        else
        {
            _log.LogWarning(
                "Application exception: {Path} {ErrorCode} {Message}",
                ctx.Request.Path,
                descriptor.Code,
                ex.Message);
        }

        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.StatusCode = descriptor.HttpStatus;

        var payload = AppErrorResponse.From(ex, ctx.TraceIdentifier);
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static AppException MapLegacy(Exception ex)
    {
        if (ex is InvalidOperationException)
            return AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, message: ex.Message);

        if (ex is ArgumentException)
            return AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, message: ex.Message);

        if (ex is UnauthorizedAccessException)
            return AppExceptionFactory.Unauthorized();

        if (ex is BadHttpRequestException)
            return AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, message: ex.Message);

        return AppExceptionFactory.Create(AppErrorCode.COMMON_INTERNAL_ERROR);
    }
}
