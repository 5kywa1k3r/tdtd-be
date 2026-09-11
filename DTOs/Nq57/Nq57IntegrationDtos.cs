using System.Text.Json;
using System.Text.Json.Nodes;

namespace tdtd_be.DTOs.Nq57;

public sealed class Nq57BuildSavePayloadRequest
{
    public JsonElement TemplateRows { get; init; }
    public List<Nq57SectionAggregationDto> Sections { get; init; } = new();
    public bool PreserveTemplateWhenEmpty { get; init; } = true;
    public string? GeneratedBy { get; init; }
}

public sealed class Nq57SectionAggregationDto
{
    public string? IdxId { get; init; }
    public string? IdxCode { get; init; }
    public List<Nq57UnitTextDto> Units { get; init; } = new();
}

public sealed class Nq57UnitTextDto
{
    public string? UnitId { get; init; }
    public string? UnitName { get; init; }
    public string? Html { get; init; }
    public string? Text { get; init; }
}

public sealed class Nq57BuildSavePayloadResponse
{
    public JsonArray Rows { get; init; } = new();
    public int RowCount { get; init; }
    public int MappedSectionCount { get; init; }
    public int UnitCount { get; init; }
}

public sealed class Nq57PushRequest
{
    public string? Url { get; init; }
    public string? BaseUrl { get; init; }
    public string? Method { get; init; } = "PUT";
    public long? GrantId { get; init; }
    public string? Action { get; init; } = "input";
    public string? Cookie { get; init; }
    public string? BearerToken { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public JsonElement Payload { get; init; }
}

public sealed class Nq57PushResponse
{
    public int StatusCode { get; init; }
    public bool Success { get; init; }
    public string? ReasonPhrase { get; init; }
    public string RequestUrl { get; init; } = "";
    public string BodyPreview { get; init; } = "";
    public long ElapsedMs { get; init; }
}
