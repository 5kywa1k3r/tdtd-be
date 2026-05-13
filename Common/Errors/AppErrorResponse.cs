namespace tdtd_be.Common.Errors;

public sealed record AppErrorResponse(
    string ErrorCode,
    string Service,
    string Message,
    object? Details,
    string TraceId)
{
    public static AppErrorResponse From(AppException ex, string traceId)
    {
        var descriptor = ex.Descriptor;
        return new AppErrorResponse(
            descriptor.Code.ToString(),
            descriptor.Service,
            ex.Message,
            ex.Details,
            traceId);
    }
}
