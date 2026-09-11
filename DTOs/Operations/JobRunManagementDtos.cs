namespace tdtd_be.DTOs.Operations;

public sealed class JobRunSearchRequest
{
    public string? Status { get; init; }
    public string? Action { get; init; }
    public string? Grain { get; init; }
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? DynamicFormTemplateId { get; init; }
    public string? FlowInstanceId { get; init; }
    public string? FlowEffectiveStatus { get; init; }
    public string? PeriodInstanceKey { get; init; }
    public string? SectionId { get; init; }
    public string? ConfigId { get; init; }
    public string? ConfigHash { get; init; }
    public string? UserId { get; init; }
    public string? Query { get; init; }
    public bool IncludeInactive { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 50;
}

public sealed class MaterializeJobRow
{
    public string Id { get; init; } = string.Empty;
    public string WorkId { get; init; } = string.Empty;
    public string WorkAssignmentId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public DateTime? LastHeartbeatAtUtc { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? LastError { get; init; }
    public int CursorAssigneeIndex { get; init; }
    public int CursorDueIndex { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class ProjectionRetryJobRow
{
    public string Id { get; init; } = string.Empty;
    public string DedupeKey { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? WorkId { get; init; }
    public string? AssignmentId { get; init; }
    public string? WorkReportPeriodId { get; init; }
    public string? DynamicExcelId { get; init; }
    public string? UserId { get; init; }
    public string? DocType { get; init; }
    public string? DocId { get; init; }
    public string ByUserId { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? LastErrorType { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastErrorAtUtc { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class StatisticRebuildJobRow
{
    public string Id { get; init; } = string.Empty;
    public string DedupeKey { get; init; } = string.Empty;
    public string DynamicFormTemplateId { get; init; } = string.Empty;
    public string? DynamicFormTemplateCode { get; init; }
    public string? DynamicFormTemplateName { get; init; }
    public string ScopeKind { get; init; } = string.Empty;
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? FlowInstanceId { get; init; }
    public string? FlowEffectiveStatus { get; init; }
    public string? PeriodInstanceKey { get; init; }
    public string Status { get; init; } = string.Empty;
    public string RequestedByUserId { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public long TotalReportCount { get; init; }
    public long ProcessedReportCount { get; init; }
    public long FailedReportCount { get; init; }
    public string? LastReportId { get; init; }
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? LastErrorType { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastErrorAtUtc { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class StatisticRebuildJobResetResponse
{
    public bool Ok { get; init; }
    public string JobId { get; init; } = string.Empty;
    public DateTime QueuedAtUtc { get; init; }
    public StatisticRebuildJobRow? Job { get; init; }
}

public sealed class FlowStatisticProjectionDiagnosticsRequest
{
    public string? WorkId { get; init; }
    public string? FlowInstanceId { get; init; }
    public string? DynamicFormTemplateId { get; init; }
    public string? FlowEffectiveStatus { get; init; }
    public string? PeriodInstanceKey { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class FlowStatisticProjectionDiagnosticsResponse
{
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;
    public string WorkId { get; init; } = string.Empty;
    public string FlowInstanceId { get; init; } = string.Empty;
    public string? DynamicFormTemplateId { get; init; }
    public string? FlowEffectiveStatus { get; init; }
    public string? PeriodInstanceKey { get; init; }
    public int Limit { get; init; }
    public long AssignmentCount { get; init; }
    public long MatchingReportCount { get; init; }
    public int ScannedReportCount { get; init; }
    public long FieldProjectionRowCount { get; init; }
    public long TableProjectionRowCount { get; init; }
    public long LabelProjectionRowCount { get; init; }
    public int NoProjectionReportCount { get; init; }
    public int StaleProjectionReportCount { get; init; }
    public int FlowMetadataMismatchReportCount { get; init; }
    public bool Truncated { get; init; }
    public List<FlowStatisticProjectionDiagnosticRow> Rows { get; init; } = new();
    public Dictionary<string, int> IssueCountsByType => Rows
        .SelectMany(x => x.IssueTypes)
        .GroupBy(x => x, StringComparer.Ordinal)
        .OrderBy(x => x.Key, StringComparer.Ordinal)
        .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
}

public sealed class FlowStatisticProjectionDiagnosticRow
{
    public string WorkAssignmentReportId { get; init; } = string.Empty;
    public string WorkAssignmentId { get; init; } = string.Empty;
    public string PeriodKey { get; init; } = string.Empty;
    public string PeriodInstanceKey { get; init; } = string.Empty;
    public int ReportStatus { get; init; }
    public int PayloadRevision { get; init; }
    public string? PayloadHash { get; init; }
    public int FieldProjectionRows { get; init; }
    public int TableProjectionRows { get; init; }
    public int LabelProjectionRows { get; init; }
    public bool FieldProjectionFresh { get; init; }
    public bool TableProjectionFresh { get; init; }
    public bool LabelProjectionFresh { get; init; }
    public bool FlowMetadataMatches { get; init; }
    public List<string> IssueTypes { get; init; } = new();
}

public sealed class BasicSummaryJobRow
{
    public string Id { get; init; } = string.Empty;
    public string WorkId { get; init; } = string.Empty;
    public string ScopeAssignmentId { get; init; } = string.Empty;
    public string DynamicFormTemplateId { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public string? SourceSignatureHash { get; init; }
    public int SourceAssignmentCount { get; init; }
    public int SourceReportCount { get; init; }
    public bool SnapshotDirty { get; init; }
    public DateTime? SnapshotDirtyAtUtc { get; init; }
    public DateTime? SnapshotRefreshedAtUtc { get; init; }
    public string? RefreshStatus { get; init; }
    public string? RefreshJobId { get; init; }
    public string? RefreshCorrelationId { get; init; }
    public string? RefreshRequestedByUserId { get; init; }
    public string? RefreshResetByUserId { get; init; }
    public DateTime? RefreshQueuedAtUtc { get; init; }
    public DateTime? RefreshStartedAtUtc { get; init; }
    public DateTime? RefreshFinishedAtUtc { get; init; }
    public DateTime? RefreshResetAtUtc { get; init; }
    public string? RefreshError { get; init; }
    public bool IsDeleted { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class BasicSummaryJobResetResponse
{
    public bool Ok { get; init; }
    public BasicSummaryJobRow? Job { get; init; }
    public string SnapshotId { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public DateTime QueuedAtUtc { get; init; }
}

public sealed class AdvancedSummaryNodeRow
{
    public string Id { get; init; } = string.Empty;
    public string Grain { get; init; } = string.Empty;
    public string GrainKey { get; init; } = string.Empty;
    public string WorkId { get; init; } = string.Empty;
    public string AssignmentId { get; init; } = string.Empty;
    public string DynamicFormTemplateId { get; init; } = string.Empty;
    public string SectionId { get; init; } = string.Empty;
    public string ConfigId { get; init; } = string.Empty;
    public int ConfigVersionNo { get; init; }
    public string ConfigHash { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool IsDirty { get; init; }
    public string? DirtyReason { get; init; }
    public string? SourceSignatureHash { get; init; }
    public long SourceReportCount { get; init; }
    public string? ValueHash { get; init; }
    public DateTime? BuiltAtUtc { get; init; }
    public string? BuildJobId { get; init; }
    public string? BuildCorrelationId { get; init; }
    public string? BuildError { get; init; }
    public DateTime WindowStartUtc { get; init; }
    public DateTime WindowEndExclusiveUtc { get; init; }
    public bool IsDeleted { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class AdvancedSummaryNodeResetResponse
{
    public bool Ok { get; init; }
    public string Grain { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;
    public string ConfigId { get; init; } = string.Empty;
    public string GrainKey { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public DateTime QueuedAtUtc { get; init; }
    public AdvancedSummaryNodeRow? Node { get; init; }
}

public sealed class AdvancedSummaryNodeCleanupRequest
{
    public string? Grain { get; init; }
    public string? Status { get; init; }
    public string? WorkId { get; init; }
    public string? WorkAssignmentId { get; init; }
    public string? DynamicFormTemplateId { get; init; }
    public string? SectionId { get; init; }
    public string? ConfigId { get; init; }
    public int? ConfigVersionNo { get; init; }
    public string? ConfigHash { get; init; }
    public string? SourceSignatureHash { get; init; }
    public DateTime? UpdatedBeforeUtc { get; init; }
    public DateTime? BuiltBeforeUtc { get; init; }
    public bool DryRun { get; init; } = true;
    public int Limit { get; init; } = 500;
}

public sealed class AdvancedSummaryNodeCleanupResponse
{
    public bool Ok { get; init; }
    public bool DryRun { get; init; }
    public int Limit { get; init; }
    public long MatchedCount { get; init; }
    public int SelectedCount { get; init; }
    public long SoftDeletedCount { get; init; }
    public bool HasMore { get; init; }
    public List<AdvancedSummaryNodeRow> SampleRows { get; init; } = new();
}
