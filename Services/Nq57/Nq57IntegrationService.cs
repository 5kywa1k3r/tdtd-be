using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using tdtd_be.DTOs.Nq57;

namespace tdtd_be.Services.Nq57;

public interface INq57IntegrationService
{
    Nq57BuildSavePayloadResponse BuildSavePayload(Nq57BuildSavePayloadRequest request);
    Task<Nq57PushResponse> PushAsync(Nq57PushRequest request, CancellationToken ct);
}

public sealed class Nq57IntegrationService : INq57IntegrationService
{
    private const string DefaultObjIdxDataUrl = "https://theodoinq.dcs.vn/report_api/api/rp_objidx_data";
    private const int BodyPreviewLimit = 20_000;
    private const string CkEditorFontStyle = "font-family: &quot;Times New Roman&quot;;";
    private const string CkEditorParagraphStyle =
        "margin-top: 8px; margin-bottom: 8px; text-align: justify; text-indent: 35.45pt; font-family: &quot;Times New Roman&quot;;";
    private const string CkEditorLineStyle = "line-height: normal; font-family: &quot;Times New Roman&quot;;";
    private const string CkEditorTextStyle = "color: black; font-family: &quot;Times New Roman&quot;;";
    private static readonly Regex ScriptOrStyleRegex = new(
        @"<\s*(script|style)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex EventAttributeRegex = new(
        @"\s+on[a-z0-9_:-]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Transfer-Encoding"
    };

    private readonly HttpClient _http;

    public Nq57IntegrationService(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    public Nq57BuildSavePayloadResponse BuildSavePayload(Nq57BuildSavePayloadRequest request)
    {
        if (request.TemplateRows.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("templateRows must be a JSON array.");

        var sectionPayloads = BuildSectionPayloadLookup(request.Sections);
        var rows = new JsonArray();
        var mapped = 0;

        foreach (var rowElement in request.TemplateRows.EnumerateArray())
        {
            var row = JsonNode.Parse(rowElement.GetRawText()) as JsonObject
                ?? throw new ArgumentException("templateRows contains a non-object row.");

            var idxId = ReadJsonScalar(row, "idxId");
            var idxCode = ReadJsonScalar(row, "idxCode");
            var payload = FindPayload(sectionPayloads, idxId, idxCode);

            if (payload is not null)
            {
                ApplyAggregatedContent(row, payload, request.GeneratedBy);
                mapped++;
            }
            else if (!request.PreserveTemplateWhenEmpty)
            {
                ApplyAggregatedContent(row, AggregatedSectionContent.Empty, request.GeneratedBy);
            }

            rows.Add(row);
        }

        var unitCount = request.Sections
            .SelectMany(x => x.Units)
            .Select(x => NormalizeKey(x.UnitId) ?? NormalizeKey(x.UnitName))
            .Where(x => x is not null)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new Nq57BuildSavePayloadResponse
        {
            Rows = rows,
            RowCount = rows.Count,
            MappedSectionCount = mapped,
            UnitCount = unitCount
        };
    }

    public async Task<Nq57PushResponse> PushAsync(Nq57PushRequest request, CancellationToken ct)
    {
        var method = ResolveMethod(request.Method);
        if (method != HttpMethod.Get && request.Payload.ValueKind is JsonValueKind.Undefined)
            throw new ArgumentException("payload is required.");

        var url = BuildRequestUri(request);
        using var message = new HttpRequestMessage(method, url);
        message.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        message.Headers.TryAddWithoutValidation("x-check-auth", "true");

        if (!string.IsNullOrWhiteSpace(request.Cookie))
            message.Headers.TryAddWithoutValidation("Cookie", request.Cookie.Trim());

        if (!string.IsNullOrWhiteSpace(request.BearerToken))
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {request.BearerToken.Trim()}");

        foreach (var (name, value) in request.Headers ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(name) || BlockedHeaders.Contains(name) || value is null)
                continue;

            message.Headers.TryAddWithoutValidation(name.Trim(), value);
        }

        if (method != HttpMethod.Get)
        {
            message.Content = new StringContent(
                request.Payload.GetRawText(),
                Encoding.UTF8,
                "application/json");
        }

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        return new Nq57PushResponse
        {
            StatusCode = (int)response.StatusCode,
            Success = response.IsSuccessStatusCode,
            ReasonPhrase = response.ReasonPhrase,
            RequestUrl = url.ToString(),
            BodyPreview = body.Length <= BodyPreviewLimit ? body : body[..BodyPreviewLimit],
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private static Dictionary<string, AggregatedSectionContent> BuildSectionPayloadLookup(
        IEnumerable<Nq57SectionAggregationDto> sections)
    {
        var result = new Dictionary<string, AggregatedSectionContent>(StringComparer.Ordinal);

        foreach (var section in sections)
        {
            var content = BuildCkEditorAggregatedContent(section.Units);
            if (content.IsEmpty)
                continue;

            var idxId = NormalizeKey(section.IdxId);
            var idxCode = NormalizeKey(section.IdxCode);

            if (idxId is not null)
                result[$"id:{idxId}"] = content;
            if (idxCode is not null)
                result[$"code:{idxCode}"] = content;
        }

        return result;
    }

    private static AggregatedSectionContent? FindPayload(
        IReadOnlyDictionary<string, AggregatedSectionContent> payloads,
        string? idxId,
        string? idxCode)
    {
        var idKey = NormalizeKey(idxId);
        if (idKey is not null && payloads.TryGetValue($"id:{idKey}", out var byId))
            return byId;

        var codeKey = NormalizeKey(idxCode);
        if (codeKey is not null && payloads.TryGetValue($"code:{codeKey}", out var byCode))
            return byCode;

        return null;
    }

    private static AggregatedSectionContent BuildCkEditorAggregatedContent(IEnumerable<Nq57UnitTextDto> units)
    {
        var htmlBlocks = new List<string>();
        var rawBlocks = new List<string>();
        var order = 1;

        foreach (var unit in units)
        {
            var raw = NormalizeText(unit.Text) ?? HtmlToText(unit.Html ?? "");
            var paragraphs = SplitParagraphs(raw);
            if (paragraphs.Count == 0)
                continue;

            var unitName = NormalizeText(unit.UnitName) ?? NormalizeText(unit.UnitId) ?? $"Đơn vị {order}";
            var block = new StringBuilder();
            block.Append(BuildCkEditorParagraph($"{unitName}:", bold: true));
            foreach (var paragraph in paragraphs)
            {
                block.Append(BuildCkEditorParagraph(paragraph, bold: false));
            }

            htmlBlocks.Add(block.ToString());
            rawBlocks.Add($"{unitName}:{Environment.NewLine}{string.Join(Environment.NewLine, paragraphs)}".Trim());
            order++;
        }

        if (htmlBlocks.Count == 0)
            return AggregatedSectionContent.Empty;

        return new AggregatedSectionContent(
            string.Join("", htmlBlocks).Trim(),
            string.Join($"{Environment.NewLine}{Environment.NewLine}", rawBlocks).Trim(),
            htmlBlocks.Count);
    }

    private static void ApplyAggregatedContent(
        JsonObject row,
        AggregatedSectionContent content,
        string? generatedBy)
    {
        row["idxContent"] = content.Html;
        row["idxContentRaw"] = content.Raw;

        if (row.ContainsKey("idxContentKeyword"))
            row["idxContentKeyword"] = content.Html;

        if (row.ContainsKey("idxContentSynthetic"))
            row["idxContentSynthetic"] = content.Html;

        if (row.ContainsKey("updateTime"))
            row["updateTime"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(generatedBy) && row.ContainsKey("updateUser"))
            row["updateUser"] = generatedBy.Trim();
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var safeHtml = ScriptOrStyleRegex.Replace(html, "");
        safeHtml = EventAttributeRegex.Replace(safeHtml, "");
        safeHtml = Regex.Replace(safeHtml, @"javascript\s*:", "", RegexOptions.IgnoreCase);
        safeHtml = Regex.Replace(safeHtml, @"<\s*br\s*/?\s*>", Environment.NewLine, RegexOptions.IgnoreCase);
        var withBreaks = Regex.Replace(safeHtml, @"</(p|div|li|tr|h[1-6])\s*>", Environment.NewLine, RegexOptions.IgnoreCase);
        var noTags = TagRegex.Replace(withBreaks, " ");
        return NormalizeText(WebUtility.HtmlDecode(noTags)) ?? "";
    }

    private static IReadOnlyList<string> SplitParagraphs(string? text)
    {
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(x => NormalizeText(x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }

    private static string BuildCkEditorParagraph(string text, bool bold)
    {
        var encoded = WebUtility.HtmlEncode(text);
        var textSpan = $"""<span lang="EN-US" style="{CkEditorTextStyle}">{encoded}</span>""";
        if (bold)
            textSpan = $"""<b style="{CkEditorFontStyle}">{textSpan}</b>""";

        return $"""<p style="{CkEditorParagraphStyle}"><span style="{CkEditorFontStyle}"><span style="{CkEditorLineStyle}"><span style="{CkEditorFontStyle}">{textSpan}</span></span></span></p>""";
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Regex.Replace(value.Replace('\u00a0', ' '), @"[ \t]{2,}", " ").Trim();
    }

    private static string? NormalizeKey(string? value)
    {
        var normalized = NormalizeText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? ReadJsonScalar(JsonObject row, string key)
    {
        if (!row.TryGetPropertyValue(key, out var node) || node is null)
            return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s))
                return s;
            if (value.TryGetValue<long>(out var l))
                return l.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<int>(out var i))
                return i.ToString(CultureInfo.InvariantCulture);
        }

        return node.ToJsonString();
    }

    private static HttpMethod ResolveMethod(string? method)
    {
        var normalized = (method ?? "PUT").Trim().ToUpperInvariant();
        return normalized switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            _ => throw new ArgumentException("method must be GET, POST, PUT, or PATCH.")
        };
    }

    private static Uri BuildRequestUri(Nq57PushRequest request)
    {
        var rawUrl = !string.IsNullOrWhiteSpace(request.Url)
            ? request.Url.Trim()
            : !string.IsNullOrWhiteSpace(request.BaseUrl)
                ? request.BaseUrl.Trim()
                : DefaultObjIdxDataUrl;

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("url/baseUrl must be an absolute HTTP(S) URL.");
        }

        var url = rawUrl;
        var query = new Dictionary<string, string?>();
        if (request.GrantId.HasValue && !url.Contains("grant_id=", StringComparison.OrdinalIgnoreCase))
            query["grant_id"] = request.GrantId.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.Action) && !url.Contains("action=", StringComparison.OrdinalIgnoreCase))
            query["action"] = request.Action.Trim();

        if (query.Count > 0)
            url = QueryHelpers.AddQueryString(url, query);

        return new Uri(url);
    }

    private sealed record AggregatedSectionContent(string Html, string Raw, int UnitCount)
    {
        public static readonly AggregatedSectionContent Empty = new("", "", 0);
        public bool IsEmpty => UnitCount <= 0 || (string.IsNullOrWhiteSpace(Html) && string.IsNullOrWhiteSpace(Raw));
    }
}
