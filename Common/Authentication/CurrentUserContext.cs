using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Authentication
{
    public sealed class CurrentUserContext : ICurrentUserContext
    {
        public const string MeItemKey = "me"; // <<< nếu middleware dùng key khác, sửa tại đây

        private readonly IHttpContextAccessor _http;

        public CurrentUserContext(IHttpContextAccessor http) => _http = http;

        public MeResponse Me
        {
            get
            {
                var ctx = _http.HttpContext ?? throw new InvalidOperationException("No HttpContext.");

                if (!ctx.Items.TryGetValue(MeItemKey, out var obj) || obj is not MeResponse me)
                    throw new InvalidOperationException($"MeResponse not found in HttpContext.Items['{MeItemKey}'].");

                if (!me.IsActive)
                    throw new InvalidOperationException("User is inactive.");

                return me;
            }
        }
    }
}
