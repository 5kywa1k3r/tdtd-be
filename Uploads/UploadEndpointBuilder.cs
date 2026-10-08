using Microsoft.AspNetCore.Http;

namespace tdtd_be.Uploads;

public static class UploadEndpointBuilder
{
    public static string BuildUploadsEndpoint(HttpRequest request, UploadOptions options)
    {
        // The browser resolves this against the API origin it is already using.
        // An absolute configured LAN address may be unreachable from that browser.
        return $"{request.PathBase}/api/uploads";
    }

    public static string SameOriginTusLocation(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            !url.AbsolutePath.StartsWith("/api/uploads/", StringComparison.OrdinalIgnoreCase))
            return location;

        return url.PathAndQuery;
    }
}
