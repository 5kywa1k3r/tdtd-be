namespace tdtd_be.Common.Errors;

public sealed class AppException : Exception
{
    public AppErrorCode Code { get; }
    public object? Details { get; }
    public AppErrorDescriptor Descriptor => AppErrorCatalog.Get(Code);

    public AppException(AppErrorCode code, object? details = null, string? message = null, Exception? innerException = null)
        : base(message ?? AppErrorCatalog.Get(code).Message, innerException)
    {
        Code = code;
        Details = details;
    }
}
