namespace tdtd_be.Common.Errors;

public sealed record AppErrorDescriptor(
    AppErrorCode Code,
    string Service,
    int HttpStatus,
    string Message,
    string Description,
    string? DetailTemplate = null);
