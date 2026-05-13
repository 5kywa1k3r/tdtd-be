using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Authentication
{
    public sealed class CurrentUserContext : ICurrentUserContext
    {
        public const string MeItemKey = "me";

        private readonly IHttpContextAccessor _http;

        public CurrentUserContext(IHttpContextAccessor http) => _http = http;

        public MeResponse Me
        {
            get
            {
                var ctx = _http.HttpContext
                    ?? throw tdtd_be.Common.Errors.AppExceptionFactory.Unauthorized(
                        tdtd_be.Common.Errors.AppErrorCode.AUTH_ME_NOT_AVAILABLE,
                        new { reason = "httpContextMissing" });

                if (!ctx.Items.TryGetValue(MeItemKey, out var obj) || obj is not MeResponse me)
                    throw tdtd_be.Common.Errors.AppExceptionFactory.Unauthorized(
                        tdtd_be.Common.Errors.AppErrorCode.AUTH_ME_NOT_AVAILABLE,
                        new { reason = "meContextMissing", key = MeItemKey });

                if (me.IsDeleted)
                    throw tdtd_be.Common.Errors.AppExceptionFactory.Forbidden(
                        tdtd_be.Common.Errors.AppErrorCode.AUTH_ACCOUNT_LOCKED,
                        new { me.Id });

                return me;
            }
        }
    }
}
