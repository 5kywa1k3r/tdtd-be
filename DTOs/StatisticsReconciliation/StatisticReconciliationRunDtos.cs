using System.Text.Json;
using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.StatisticsReconciliation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationActivationEvaluateRequest
{
    public string CapabilityId { get; init; } = string.Empty;
    public string RouteId { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationCreateRequest
{
    public string CommandId { get; init; } = string.Empty;
    public string P9ResultKind { get; init; } = string.Empty;
    public string P9ResultId { get; init; } = string.Empty;
    public string P9RunId { get; init; } = string.Empty;
    public string ConceptKey { get; init; } = string.Empty;
    public string Grain { get; init; } = string.Empty;
    public JsonElement? Filter { get; init; }
    public StatisticReconciliationActualCapturePlanRequest? ActualCapturePlan { get; init; }
    public string? CapturePlanToken { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationCapturePlanPreflightRequest
{
    public string P9ResultKind { get; init; } = string.Empty;
    public string P9ResultId { get; init; } = string.Empty;
    public string P9RunId { get; init; } = string.Empty;
    public string ConceptKey { get; init; } = string.Empty;
    public string Grain { get; init; } = string.Empty;
    public JsonElement? Filter { get; init; }
    public string ExportId { get; init; } = string.Empty;
}

public sealed class StatisticReconciliationCapturePlanPreflightResponse
{
    public string SchemaVersion { get; init; } =
        "P10_CAPTURE_PLAN_PREFLIGHT_V1";
    public string CapturePlanToken { get; init; } = string.Empty;
    public string PlanSha256 { get; init; } = string.Empty;
    public DateTime ExpiresAtUtc { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationActualCapturePlanRequest
{
    public string BoundaryRegistryVersion { get; init; } = string.Empty;
    public string BasicSnapshotId { get; init; } = string.Empty;
    public string BasicMode { get; init; } = string.Empty;
    public string AdvancedSectionId { get; init; } = string.Empty;
    public IReadOnlyList<string> AdvancedDayNodeIds { get; init; } = [];
    public IReadOnlyList<string> AdvancedMonthNodeIds { get; init; } = [];
    public IReadOnlyList<string> AdvancedYearNodeIds { get; init; } = [];
    public string DiffResultId { get; init; } = string.Empty;
    public string DiffRunId { get; init; } = string.Empty;
    public string ApiSurface { get; init; } = string.Empty;
    public string? ApiOwnerResultId { get; init; }
    public string ExportId { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationCancelRequest
{
    public string CommandId { get; init; } = string.Empty;
    public long ExpectedStateRevision { get; init; }
    public string ExpectedStateHash { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationWorkerClaimRequest
{
    public string WorkerId { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationWorkerFenceRequest
{
    public string WorkerId { get; init; } = string.Empty;
    public string ClaimToken { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationWorkerFailRequest
{
    public string WorkerId { get; init; } = string.Empty;
    public string ClaimToken { get; init; } = string.Empty;
    public string FailureCode { get; init; } = string.Empty;
    public bool Transient { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatisticReconciliationPendingPublishRequest
{
    public string WorkerId { get; init; } = string.Empty;
    public string ClaimToken { get; init; } = string.Empty;
    public string GenerationId { get; init; } = string.Empty;
    public string GenerationHash { get; init; } = string.Empty;
}

public class StatisticReconciliationSummaryResponse
{
    public string ReconciliationId { get; init; } = string.Empty;
    public string ReceiptId { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public bool IsReplay { get; init; }
    public string WorkId { get; init; } = string.Empty;
    public string ScopeAssignmentId { get; init; } = string.Empty;
    public string P9ResultKind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long StateRevision { get; init; }
    public string StateHash { get; init; } = string.Empty;
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public DateTime DeadlineAtUtc { get; init; }
    public string? DiagnosticCode { get; init; }
    public bool HasPendingGeneration { get; init; }
    public bool HasCurrentGeneration { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public StatisticReconciliationPresentationResponse? Presentation { get; init; }
}

public sealed class StatisticReconciliationPresentationResponse
{
    public string SchemaVersion { get; init; } =
        "P10_RECONCILIATION_PRESENTATION_V1";
    public string DetailLevel { get; init; } = "REDACTED";
    public StatisticReconciliationRecheckPresentationResponse Recheck { get; init; } = new();
    public IReadOnlyList<StatisticReconciliationPresentationRowResponse> Rows { get; init; } = [];
}

public sealed class StatisticReconciliationRecheckPresentationResponse
{
    // Current-base hint only. POST still validates successor ownership and CAS.
    public bool BaseEligible { get; init; }
    public bool InProgress { get; init; }
}

public sealed class StatisticReconciliationPresentationRowResponse
{
    public string Id { get; init; } = string.Empty;
    public StatisticReconciliationPresentationColumnsResponse Columns { get; init; } = new();
    public StatisticReconciliationPresentationMetadataResponse Metadata { get; init; } = new();
}

public sealed class StatisticReconciliationPresentationColumnsResponse
{
    [JsonPropertyName("Identity")]
    public string Identity { get; init; } = string.Empty;

    [JsonPropertyName("Config")]
    public string Config { get; init; } = string.Empty;

    [JsonPropertyName("Expected")]
    public string Expected { get; init; } = string.Empty;

    [JsonPropertyName("Actual")]
    public string Actual { get; init; } = string.Empty;

    [JsonPropertyName("Delta")]
    public string Delta { get; init; } = string.Empty;

    [JsonPropertyName("Freshness")]
    public string Freshness { get; init; } = string.Empty;

    [JsonPropertyName("Permission")]
    public string Permission { get; init; } = string.Empty;

    [JsonPropertyName("Verdict")]
    public string Verdict { get; init; } = string.Empty;
}

public sealed class StatisticReconciliationPresentationMetadataResponse
{
    public long Total { get; init; }
    public string RootCause { get; init; } = string.Empty;
    public int RowCountBeforeRedaction { get; init; }
    public int RowCountAfterRedaction { get; init; }
    public IReadOnlyList<string> PermissionCodes { get; init; } = [];
    public IReadOnlyList<StatisticReconciliationPresentationLinkResponse> EvidenceLinks { get; init; } = [];
    public IReadOnlyList<StatisticReconciliationPresentationLinkResponse> SourceLinks { get; init; } = [];
}

public sealed class StatisticReconciliationPresentationLinkResponse
{
    public string Rel { get; init; } = string.Empty;
    public string Href { get; init; } = string.Empty;
    public string Method { get; init; } = "GET";
}

public sealed class StatisticReconciliationDetailResponse : StatisticReconciliationSummaryResponse
{
    public string ActorUserId { get; init; } = string.Empty;
    public string? TenantUnitId { get; init; }
    public IReadOnlyList<string> PermissionCodes { get; init; } = [];
    public string AuthorizationSnapshotHash { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public string ImmutableIdentityHash { get; init; } = string.Empty;
    public string ImmutableHeaderHash { get; init; } = string.Empty;
    public string P9ResultId { get; init; } = string.Empty;
    public string P9RunId { get; init; } = string.Empty;
    public string P9GenerationId { get; init; } = string.Empty;
    public string P9GenerationHash { get; init; } = string.Empty;
    public string P9CapabilityId { get; init; } = string.Empty;
    public string P9RouteId { get; init; } = string.Empty;
    public string P9CandidateChainId { get; init; } = string.Empty;
    public string P9CandidatePromptId { get; init; } = string.Empty;
    public string P9CatalogVersion { get; init; } = string.Empty;
    public string P9CatalogRawSha256 { get; init; } = string.Empty;
    public string P9CatalogSemanticSha256 { get; init; } = string.Empty;
    public string P9SchemaRawSha256 { get; init; } = string.Empty;
    public string P9SchemaSemanticSha256 { get; init; } = string.Empty;
    public string P9StageLockSha256 { get; init; } = string.Empty;
    public string SourceReportId { get; init; } = string.Empty;
    public int SourcePayloadRevision { get; init; }
    public string SourcePayloadHash { get; init; } = string.Empty;
    public int SourceLifecycleRevision { get; init; }
    public string? SourceLifecycleEventKey { get; init; }
    public string SourceLifecycleHash { get; init; } = string.Empty;
    public string DynamicFormVersionId { get; init; } = string.Empty;
    public string? DynamicFormFamilyId { get; init; }
    public int? DynamicFormVersionNo { get; init; }
    public string? DynamicFormSchemaHash { get; init; }
    public string? FlowTemplateId { get; init; }
    public string? FlowTemplateVersionId { get; init; }
    public string? FlowPayloadHash { get; init; }
    public string? FlowInstanceId { get; init; }
    public int? FlowExecutionEpoch { get; init; }
    public string? FlowStepInstanceId { get; init; }
    public string? FlowStepId { get; init; }
    public string? FlowBranchId { get; init; }
    public string? FlowEffectiveStatus { get; init; }
    public string? FlowContributionPolicyHash { get; init; }
    public string? FlowContributionProvenanceHash { get; init; }
    public string? P8ConfigOwnerId { get; init; }
    public string? P8ConfigId { get; init; }
    public string? P8ConfigVersionId { get; init; }
    public long? P8ConfigRevision { get; init; }
    public string? P8ConfigHash { get; init; }
    public string P8ConfigBundleHash { get; init; } = string.Empty;
    public string CandidateChainId { get; init; } = string.Empty;
    public string CandidatePromptId { get; init; } = string.Empty;
    public int CandidateStage { get; init; }
    public string CandidateCatalogVersion { get; init; } = string.Empty;
    public string CandidateCatalogRawSha256 { get; init; } = string.Empty;
    public string CandidateCatalogSemanticSha256 { get; init; } = string.Empty;
    public string CandidateSchemaRawSha256 { get; init; } = string.Empty;
    public string CandidateSchemaSemanticSha256 { get; init; } = string.Empty;
    public string CandidateStageLockSha256 { get; init; } = string.Empty;
    public string PeriodKey { get; init; } = string.Empty;
    public string PeriodInstanceKey { get; init; } = string.Empty;
    public string PeriodKind { get; init; } = string.Empty;
    public string ConceptKey { get; init; } = string.Empty;
    public string Grain { get; init; } = string.Empty;
    public string TimeAxis { get; init; } = string.Empty;
    public string PageContractHash { get; init; } = string.Empty;
    public string FilterHash { get; init; } = string.Empty;
    public string? SourceSetSha256 { get; init; }
    public string? ExpectedAlgorithmSha256 { get; init; }
    public string? ExpectedAlgorithmRevision { get; init; }
    public string? PendingGenerationId { get; init; }
    public string? PendingGenerationHash { get; init; }
    public string? CurrentGenerationId { get; init; }
    public string? CurrentGenerationHash { get; init; }
    public long GenerationPublishRevision { get; init; }
}

public sealed class StatisticReconciliationPageResponse
{
    public IReadOnlyList<StatisticReconciliationSummaryResponse> Rows { get; init; } = [];
    public long Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public sealed class StatisticReconciliationWorkerLeaseResponse
{
    public string WorkerId { get; init; } = string.Empty;
    public string ClaimToken { get; init; } = string.Empty;
    public DateTime LeaseUntilUtc { get; init; }
    public StatisticReconciliationDetailResponse Run { get; init; } = new();
}
