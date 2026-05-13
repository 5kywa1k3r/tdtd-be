using Microsoft.AspNetCore.Http;

namespace tdtd_be.Uploads;

public static class UploadEndpointBuilder
{
    public static string BuildUploadsEndpoint(HttpRequest request, UploadOptions options)
    {
        var publicBaseUrl = NormalizePublicBaseUrl(options.PublicBaseUrl);
        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
            return $"{publicBaseUrl}/api/uploads";

        return $"{request.Scheme}://{request.Host.Value}/api/uploads";
    }

    private static string? NormalizePublicBaseUrl(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        const string apiSuffix = "/api";
        if (trimmed.EndsWith(apiSuffix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^apiSuffix.Length];

        return trimmed;
    }
}
