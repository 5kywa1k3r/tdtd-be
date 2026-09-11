using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.StatisticsRun;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunActivationEvaluateRequest
{
    public string CapabilityId { get; init; } = string.Empty;
    public string RouteId { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunPeriodSelectorRequest
{
    public string PeriodKey { get; init; } = string.Empty;
    public string PeriodInstanceKey { get; init; } = string.Empty;
    public string PeriodKind { get; init; } = string.Empty;
    public DateTime? PeriodStart { get; init; }
    public DateTime? PeriodEnd { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunCreateRequest
{
    public string CommandId { get; init; } = string.Empty;
    public string WorkId { get; init; } = string.Empty;
    public string ScopeType { get; init; } = string.Empty;
    public string? ScopeId { get; init; }
    public string SourceReportId { get; init; } = string.Empty;
    public string DynamicFormTemplateId { get; init; } = string.Empty;
    public long ExpectedConfigRevision { get; init; }
    public string ExpectedConfigHash { get; init; } = string.Empty;
    public int ExpectedSourceRevision { get; init; }
    public string ExpectedSourceHash { get; init; } = string.Empty;
    public int ExpectedLifecycleRevision { get; init; }
    public StatRunPeriodSelectorRequest Period { get; init; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunWorkerClaimRequest
{
    public string WorkerId { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunWorkerFenceRequest
{
    public string WorkerId { get; init; } = string.Empty;
    public string ClaimToken { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunWorkerRetryRequest
{
    public string WorkerId { get; init; } = string.Empty;
    public string ClaimToken { get; init; } = string.Empty;
    public string FailureCode { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunResetRequest
{
    public string CommandId { get; init; } = string.Empty;
    public long ExpectedStateRevision { get; init; }
    public string ExpectedStateHash { get; init; } = string.Empty;
}

public sealed class StatRunJobResponse
{
    public string JobId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public string ReceiptId { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public string CapabilityId { get; init; } = string.Empty;
    public string RunKind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long StateRevision { get; init; }
    public string StateHash { get; init; } = string.Empty;
    public bool IsReplay { get; init; }
    public string WorkId { get; init; } = string.Empty;
    public string ScopeType { get; init; } = string.Empty;
    public string? ScopeId { get; init; }
    public string SourceReportId { get; init; } = string.Empty;
    public int SourceRevision { get; init; }
    public string SourceHash { get; init; } = string.Empty;
    public int LifecycleRevision { get; init; }
    public string DynamicFormTemplateId { get; init; } = string.Empty;
    public string ConfigId { get; init; } = string.Empty;
    public string ConfigVersionId { get; init; } = string.Empty;
    public int ConfigVersionNo { get; init; }
    public long ConfigRevision { get; init; }
    public string ConfigHash { get; init; } = string.Empty;
    public string CatalogVersion { get; init; } = string.Empty;
    public string CatalogRawSha256 { get; init; } = string.Empty;
    public string CatalogSemanticSha256 { get; init; } = string.Empty;
    public string SchemaRawSha256 { get; init; } = string.Empty;
    public string SchemaSemanticSha256 { get; init; } = string.Empty;
    public string StageLockSha256 { get; init; } = string.Empty;
    public string CandidateChainId { get; init; } = string.Empty;
    public string? FlowTemplateId { get; init; }
    public int? FlowFamilyRevision { get; init; }
    public int? FlowTemplateVersionNo { get; init; }
    public string? FlowTemplateVersionId { get; init; }
    public string? FlowPayloadHash { get; init; }
    public string? FlowCatalogVersion { get; init; }
    public string? FlowCatalogSemanticHash { get; init; }
    public string? FlowInstanceId { get; init; }
    public long? FlowInstanceRevision { get; init; }
    public string? FlowInstanceState { get; init; }
    public int? FlowExecutionEpoch { get; init; }
    public string? FlowExecutionEpochId { get; init; }
    public long? FlowExecutionEpochRevision { get; init; }
    public string? FlowExecutionEpochState { get; init; }
    public string? FlowBranchId { get; init; }
    public string? FlowStepId { get; init; }
    public int? FlowAttemptNo { get; init; }
    public string? FlowStepInstanceId { get; init; }
    public long? FlowStepInstanceRevision { get; init; }
    public string? FlowStepInstanceState { get; init; }
    public string PeriodKey { get; init; } = string.Empty;
    public string PeriodInstanceKey { get; init; } = string.Empty;
    public string PeriodKind { get; init; } = string.Empty;
    public DateTime? PeriodStartUtc { get; init; }
    public DateTime? PeriodEndUtc { get; init; }
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public DateTime? LastHeartbeatAtUtc { get; init; }
    public DateTime? DeadlineAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime? ComputedAtUtc { get; init; }
    public string? GenerationId { get; init; }
    public string? GenerationHash { get; init; }
    public string? ProjectionRunId { get; init; }
    public string FreshnessState { get; init; } = string.Empty;
    public string? StaleReason { get; init; }
    public string? DiagnosticCode { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class StatRunWorkerLeaseResponse
{
    public string WorkerId { get; init; } = string.Empty;
    public string ClaimToken { get; init; } = string.Empty;
    public DateTime LeaseUntilUtc { get; init; }
    public StatRunJobResponse Job { get; init; } = new();
}
