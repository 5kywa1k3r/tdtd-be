//Common/Exceptions/AppException.cs
namespace tdtd_be.Common.Exceptions
{
    public sealed class AppException : Exception
    {
        public int StatusCode { get; }
        public string Code { get; }

        public AppException(int statusCode, string code, string message) : base(message)
        {
            StatusCode = statusCode;
            Code = code;
        }

        public static AppException Unauthorized(string message = "Unauthorized")
            => new(401, "UNAUTHORIZED", message);

        public static AppException BadRequest(string message = "Bad request")
            => new(400, "BAD_REQUEST", message);
    }
}
