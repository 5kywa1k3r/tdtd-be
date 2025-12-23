//Common/Exceptions/AppException.cs
namespace tdtd_be.Common.Exceptions
{
    public sealed class ApiException : Exception
    {
        public int StatusCode { get; }
        public string ErrorCode { get; }

        public ApiException(int statusCode, string errorCode, string message) : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }

        public static ApiException NotFound(string msg = "Not found") => new(404, "NOT_FOUND", msg);
        public static ApiException BadRequest(string msg = "Bad request") => new(400, "BAD_REQUEST", msg);
        public static ApiException Conflict(string msg = "Conflict") => new(409, "CONFLICT", msg);
        public static ApiException Forbidden(string msg = "Forbidden") => new(403, "FORBIDDEN", msg);
    }
}
