using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.StatisticsRun;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunExportFilterRequest
{
    public string? DynamicFormTemplateId { get; init; }
    public string? FieldId { get; init; }
    public string? FieldKey { get; init; }
    public string? BlockId { get; init; }
    public string? MetricKey { get; init; }
    public string? LabelCode { get; init; }
    public string? PeriodKey { get; init; }
    public string? BucketKey { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunExportCreateRequest
{
    public string CommandId { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string ResultKind { get; init; } = string.Empty;
    public string WorkId { get; init; } = string.Empty;
    public string ScopeType { get; init; } = string.Empty;
    public string ScopeId { get; init; } = string.Empty;
    public string? PeriodInstanceKey { get; init; }
    public string ResultId { get; init; } = string.Empty;
    public string ExpectedResultHash { get; init; } = string.Empty;
    public string ExpectedConfigHash { get; init; } = string.Empty;
    public string ExpectedSourceHash { get; init; } = string.Empty;
    public int ExpectedLifecycleRevision { get; init; }
    public StatRunExportFilterRequest Filters { get; init; } = new();
}

public sealed class StatRunExportResponse
{
    public string ExportId { get; init; } = string.Empty;
    public string ReceiptId { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public bool IsReplay { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CapabilityId { get; init; } = string.Empty;
    public string ResultKind { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string WorkId { get; init; } = string.Empty;
    public string ScopeType { get; init; } = string.Empty;
    public string ScopeId { get; init; } = string.Empty;
    public string? PeriodInstanceKey { get; init; }
    public string ResultId { get; init; } = string.Empty;
    public string ResultHash { get; init; } = string.Empty;
    public string ConfigHash { get; init; } = string.Empty;
    public string SourceHash { get; init; } = string.Empty;
    public int LifecycleRevision { get; init; }
    public string CatalogVersion { get; init; } = string.Empty;
    public string CatalogRawSha256 { get; init; } = string.Empty;
    public string CatalogSemanticSha256 { get; init; } = string.Empty;
    public string StageLockSha256 { get; init; } = string.Empty;
    public string CandidateChainId { get; init; } = string.Empty;
    public string CandidatePromptId { get; init; } = string.Empty;
    public int CandidateStage { get; init; }
    public string SchemaVersion { get; init; } = string.Empty;
    public string SemanticHash { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public long ByteCount { get; init; }
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public DateTime CompletedAtUtc { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public string DownloadUrl { get; init; } = string.Empty;
}

public sealed class StatRunExportDownload
{
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunExportCleanupRequest
{
    public int Limit { get; init; } = 100;
    public bool DryRun { get; init; } = true;
    public DateTime? ExpiredBeforeUtc { get; init; }
}

public sealed class StatRunExportCleanupResponse
{
    public bool DryRun { get; init; }
    public DateTime ExpiredBeforeUtc { get; init; }
    public int Selected { get; init; }
    public int Deleted { get; init; }
    public IReadOnlyList<string> ExportIds { get; init; } = Array.Empty<string>();
}
