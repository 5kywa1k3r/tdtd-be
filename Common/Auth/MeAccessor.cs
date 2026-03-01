using tdtd_be.DTOs.Auth;

namespace tdtd_be.Common.Auth;

public sealed class MeAccessor
{
    public const string MeItemKey = "me";
    private readonly IHttpContextAccessor _http;
    public MeAccessor(IHttpContextAccessor http) => _http = http;

    public MeResponse RequireMe()
        => _http.HttpContext?.Items[MeItemKey] as MeResponse
           ?? throw new UnauthorizedAccessException("Missing me context.");
}