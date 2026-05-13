namespace tdtd_be.Common.Errors;

public static class AppExceptionFactory
{
    public static AppException Create(AppErrorCode code, object? details = null, string? message = null)
        => new(code, details, message);

    public static AppException BadRequest(AppErrorCode code, object? details = null, string? message = null)
        => Create(code, details, message);

    public static AppException Unauthorized(AppErrorCode code = AppErrorCode.AUTH_UNAUTHORIZED, object? details = null, string? message = null)
        => Create(code, details, message);

    public static AppException Forbidden(AppErrorCode code = AppErrorCode.AUTH_FORBIDDEN, object? details = null, string? message = null)
        => Create(code, details, message);

    public static AppException NotFound(AppErrorCode code = AppErrorCode.COMMON_NOT_FOUND, object? details = null, string? message = null)
        => Create(code, details, message);
}
