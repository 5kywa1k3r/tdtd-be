namespace tdtd_be.DTOs.Statistics;

public static class P9ResultStates
{
    public const string Empty = "EMPTY";
    public const string Pending = "PENDING";
    public const string Building = "BUILDING";
    public const string Ready = "READY";
    public const string Stale = "STALE";
    public const string Failed = "FAILED";
}

public sealed class P9DirectResultMetadata
{
    public string CapabilityId { get; set; } = "DIRECT_FIELD_TABLE_LABEL";
    public string State { get; set; } = P9ResultStates.Empty;
    public string Freshness { get; set; } = "EMPTY";
    public string WorkId { get; set; } = default!;
    public string PeriodInstanceKey { get; set; } = default!;
    public string? StaleReason { get; set; }
    public string? FailureCode { get; set; }
    public DateTime? ComputedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public long AuthoritativeSourceRevision { get; set; }
    public IReadOnlyList<P9DirectPublicationPin> Publications { get; set; } =
        Array.Empty<P9DirectPublicationPin>();
    public P9DirectReconciliationIdentity? ReconciliationIdentity { get; set; }
}

public sealed class P9DirectReconciliationIdentity
{
    public string P9RunId { get; set; } = string.Empty;
    public string P9ResultId { get; set; } = string.Empty;
    public string GenerationId { get; set; } = string.Empty;
    public string GenerationHash { get; set; } = string.Empty;
    public string ConceptKey { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public string Grain { get; set; } = string.Empty;
}

public sealed class P9DirectPublicationPin
{
    public string RunId { get; set; } = default!;
    public string GenerationId { get; set; } = default!;
    public string GenerationHash { get; set; } = default!;
    public long DirectSourceRevision { get; set; }
    public long DirectPublicationRevision { get; set; }
    public string SourceLifecycleEventKey { get; set; } = default!;
    public int SourcePayloadRevision { get; set; }
    public string SourcePayloadHash { get; set; } = default!;
    public int SourceLifecycleRevision { get; set; }
    public string SourceMembershipSignature { get; set; } = default!;
    public string ConfigId { get; set; } = default!;
    public string ConfigVersionId { get; set; } = default!;
    public int ConfigVersionNo { get; set; }
    public long ConfigRevision { get; set; }
    public string ConfigHash { get; set; } = default!;
    public string CatalogVersion { get; set; } = default!;
    public string CatalogRawSha256 { get; set; } = default!;
    public string CatalogSemanticSha256 { get; set; } = default!;
    public string SchemaRawSha256 { get; set; } = default!;
    public string SchemaSemanticSha256 { get; set; } = default!;
    public string StageLockSha256 { get; set; } = default!;
    public string CandidateChainId { get; set; } = default!;
}

public sealed class P9DirectDrilldownRow
{
    public string SourceKind { get; set; } = default!;
    public string WorkAssignmentReportId { get; set; } = default!;
    public string WorkReportPeriodId { get; set; } = default!;
    public string WorkAssignmentId { get; set; } = default!;
    public string PeriodKey { get; set; } = default!;
    public string PeriodInstanceKey { get; set; } = default!;
    public string ValueState { get; set; } = default!;
    public string StableIdentity { get; set; } = default!;
    public string? FieldId { get; set; }
    public string? FieldKey { get; set; }
    public string? MetricKey { get; set; }
    public string? RowKey { get; set; }
    public string? ColumnKey { get; set; }
    public string? LabelCode { get; set; }
    public string? BucketKey { get; set; }
    public decimal? NumericValue { get; set; }
    public bool? BooleanValue { get; set; }
    public DateTime? DateValueUtc { get; set; }
    public string? TextValue { get; set; }
    public bool Redacted { get; set; }
}
