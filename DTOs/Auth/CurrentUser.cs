namespace tdtd_be.DTOs.Auth
{
    public interface ICurrentUser
    {
        string? UserId { get; }
        string? Username { get; }
        MeResponse? Me { get; }
    }
    public sealed class CurrentUser : ICurrentUser
    {
        private readonly IHttpContextAccessor _http;

        public CurrentUser(IHttpContextAccessor http)
        {
            _http = http;
        }

        public MeResponse? Me
        {
            get
            {
                var ctx = _http.HttpContext;
                if (ctx is null) return null;

                return ctx.Items.TryGetValue("me", out var obj) ? obj as MeResponse : null;
            }
        }

        public string? UserId => Me?.Id;
        public string? Username => Me?.Username;
    }
}
