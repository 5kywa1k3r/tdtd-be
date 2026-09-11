using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed record ApiHarnessResponse(
    HttpStatusCode StatusCode,
    JsonNode? Json,
    string Body,
    IReadOnlyDictionary<string, string[]> Headers)
{
    public string? Header(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
}

internal sealed record ApiHarnessBinaryResponse(
    HttpStatusCode StatusCode,
    byte[] Content,
    IReadOnlyDictionary<string, string[]> Headers)
{
    public string? Header(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
}

internal sealed record ApiExchangeEvidence(
    int Sequence,
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? RequestBody,
    int StatusCode,
    IReadOnlyDictionary<string, string[]> ResponseHeaders,
    string? ResponseBody,
    string? CaseId);

internal sealed class ApiHarnessClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly List<ApiExchangeEvidence> _exchanges = [];
    private readonly object _exchangeGate = new();
    private int _sequence;

    public ApiHarnessClient(Uri baseUri)
    {
        _client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public IReadOnlyList<ApiExchangeEvidence> Exchanges => _exchanges;

    public Task<ApiHarnessResponse> GetAsync(
        string path,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, path, null, bearerToken, headers, ct);

    public async Task<ApiHarnessBinaryResponse> GetBytesAsync(
        string path,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (headers is not null)
        {
            foreach (var pair in headers)
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
        using var response = await _client.SendAsync(request, ct);
        var content = await response.Content.ReadAsByteArrayAsync(ct);
        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.SelectMany(v => v.Value).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        lock (_exchangeGate)
        {
            _exchanges.Add(new ApiExchangeEvidence(
                ++_sequence,
                HttpMethod.Get.Method,
                path,
                RedactHeaders(headers),
                null,
                (int)response.StatusCode,
                RedactResponseHeaders(responseHeaders),
                $"<binary bytes={content.Length} sha256={digest}>",
                HarnessCaseRunner.ActiveCaseId));
        }
        return new ApiHarnessBinaryResponse(response.StatusCode, content, responseHeaders);
    }

    public Task<ApiHarnessResponse> PostAsync(
        string path,
        object? body,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, path, body, bearerToken, headers, ct);

    public Task<ApiHarnessResponse> PutAsync(
        string path,
        object? body,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, path, body, bearerToken, headers, ct);

    public Task<ApiHarnessResponse> PatchAsync(
        string path,
        object? body,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Patch, path, body, bearerToken, headers, ct);

    public Task<ApiHarnessResponse> DeleteAsync(
        string path,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, path, null, bearerToken, headers, ct);

    public async Task<string> LoginAsync(string username, string password, CancellationToken ct)
    {
        var response = await PostAsync("api/auth/login", new { username, password }, ct: ct);
        ExpectStatus(response, HttpStatusCode.OK, $"login {username}");
        return RequiredString(response.Json, "accessToken");
    }

    public async Task<ApiHarnessResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string? bearerToken,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        string? requestBody = null;
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (headers is not null)
        {
            foreach (var pair in headers)
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        if (body is not null)
        {
            requestBody = body is JsonNode node
                ? node.ToJsonString(JsonOptions)
                : JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        JsonNode? jsonNode = null;
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                jsonNode = JsonNode.Parse(responseBody);
            }
            catch (JsonException)
            {
                // Preserve non-JSON body for diagnostics.
            }
        }

        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.SelectMany(v => v.Value).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        lock (_exchangeGate)
        {
            _exchanges.Add(new ApiExchangeEvidence(
                ++_sequence,
                method.Method,
                path,
                RedactHeaders(headers),
                RedactJson(requestBody),
                (int)response.StatusCode,
                RedactResponseHeaders(responseHeaders),
                RedactJson(responseBody),
                HarnessCaseRunner.ActiveCaseId));
        }
        return new ApiHarnessResponse(response.StatusCode, jsonNode, responseBody, responseHeaders);
    }

    public static void ExpectStatus(ApiHarnessResponse response, HttpStatusCode expected, string operation)
    {
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException(
                $"{operation} expected HTTP {(int)expected}, got {(int)response.StatusCode}. Body={Trim(response.Body, 1800)}");
        }
    }

    public static string RequiredString(JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj[property] is JsonValue value && value.TryGetValue<string>(out var text) &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new InvalidOperationException($"Response property '{property}' is missing. Body={node?.ToJsonString() ?? "<null>"}");
    }

    public static int RequiredInt(JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj[property] is JsonValue value && value.TryGetValue<int>(out var number))
            return number;

        throw new InvalidOperationException($"Response property '{property}' is missing or not an integer. Body={node?.ToJsonString() ?? "<null>"}");
    }

    public static bool RequiredBool(JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj[property] is JsonValue value && value.TryGetValue<bool>(out var flag))
            return flag;

        throw new InvalidOperationException($"Response property '{property}' is missing or not a boolean. Body={node?.ToJsonString() ?? "<null>"}");
    }

    public static string? FindStringRecursive(JsonNode? node, string property)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (string.Equals(pair.Key, property, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    return text;
                }

                var nested = FindStringRecursive(pair.Value, property);
                if (nested is not null)
                    return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = FindStringRecursive(item, property);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    public static int? FindIntRecursive(JsonNode? node, string property)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (string.Equals(pair.Key, property, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value is JsonValue value && value.TryGetValue<int>(out var number))
                {
                    return number;
                }

                var nested = FindIntRecursive(pair.Value, property);
                if (nested.HasValue)
                    return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = FindIntRecursive(item, property);
                if (nested.HasValue)
                    return nested;
            }
        }

        return null;
    }

    public static JsonObject RequiredObject(JsonNode? node, string context)
        => node as JsonObject
           ?? throw new InvalidOperationException($"{context} must be a JSON object. Body={node?.ToJsonString() ?? "<null>"}");

    public static JsonArray RequiredArray(JsonNode? node, string context)
        => node as JsonArray
           ?? throw new InvalidOperationException($"{context} must be a JSON array. Body={node?.ToJsonString() ?? "<null>"}");

    public void Dispose() => _client.Dispose();

    private static IReadOnlyDictionary<string, string> RedactHeaders(IReadOnlyDictionary<string, string>? headers)
        => headers?.ToDictionary(
               x => x.Key,
               x => IsSecretProperty(x.Key) ? "<redacted>" : x.Value,
               StringComparer.OrdinalIgnoreCase)
           ?? new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string[]> RedactResponseHeaders(
        IReadOnlyDictionary<string, string[]> headers)
        => headers.ToDictionary(
            x => x.Key,
            x => IsSecretProperty(x.Key) || x.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                ? new[] { "<redacted>" }
                : x.Value,
            StringComparer.OrdinalIgnoreCase);

    internal static string? RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var node = JsonNode.Parse(json);
            RedactNode(node);
            return node?.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return Trim(json, 4000);
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (IsSecretProperty(key))
                    obj[key] = "<redacted>";
                else
                    RedactNode(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                RedactNode(item);
        }
    }

    private static bool IsSecretProperty(string name)
        => name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("X-System-Bootstrap-Key", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
