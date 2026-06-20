namespace tdtd_be.DTOs.WorkAssignments.AdvancedSummary;

public sealed class WorkAssignmentAdvancedSummaryConfigDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string? SectionTitle { get; set; }
    public string Status { get; set; } = "DRAFT";
    public int VersionNo { get; set; }
    public int DraftRevision { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string ConfigHash { get; set; } = string.Empty;
    public string PreviewStatus { get; set; } = "NOT_REQUESTED";
    public string? PreviewJobId { get; set; }
    public string? PreviewCorrelationId { get; set; }
    public List<string> PreviewPeriodKeys { get; set; } = new();
    public string? PreviewResultJson { get; set; }
    public string? PreviewError { get; set; }
    public DateTime? PreviewRequestedAtUtc { get; set; }
    public DateTime? PreviewFinishedAtUtc { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public string? LockedByUserId { get; set; }
    public string? LockTokenId { get; set; }
    public bool RequiresPreviewToLock { get; set; }
    public bool RequiresTokenToLock { get; set; }
    public bool CanLock { get; set; }
    public bool CanPreview { get; set; }
    public string FieldGateStatus { get; set; } = "UNKNOWN";
    public string? FieldGateReason { get; set; }
    public bool IsCumulative { get; set; }
    public int SectionFieldCount { get; set; }
    public int TargetFieldCount { get; set; }
    public int FieldLimit { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SaveWorkAssignmentAdvancedSummaryDraftRequest
{
    public string ConfigJson { get; set; } = "{}";
}

public sealed class LockWorkAssignmentAdvancedSummaryConfigRequest
{
    public string? TokenId { get; set; }
}

public sealed class PreviewWorkAssignmentAdvancedSummaryConfigRequest
{
    public bool ForceRefresh { get; set; }
}

public sealed class BuildWorkAssignmentAdvancedSummaryDayNodeRequest
{
    public bool ForceRefresh { get; set; }
}

public sealed class BuildWorkAssignmentAdvancedSummaryMonthNodeRequest
{
    public bool ForceRefresh { get; set; }
}

public sealed class BuildWorkAssignmentAdvancedSummaryYearNodeRequest
{
    public bool ForceRefresh { get; set; }
}

public sealed class QueryWorkAssignmentAdvancedSummaryHierarchyRequest
{
    public string StartDayKey { get; set; } = string.Empty;
    public string EndDayKey { get; set; } = string.Empty;
    public bool EnqueueMissing { get; set; } = true;
}

public sealed class DiagnoseWorkAssignmentAdvancedSummaryDayNodeRequest
{
    public string ConfigId { get; set; } = string.Empty;
    public string DayKey { get; set; } = string.Empty;
    public bool IncludeValueJson { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryDayNodeDiagnosticsResponse
{
    public string ConfigId { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string DayKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Matches { get; set; }
    public string DiagnosticActorUserId { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; set; }
    public List<string> Differences { get; set; } = new();
    public WorkAssignmentAdvancedSummaryDayNodeDiagnosticSnapshot? Cache { get; set; }
    public WorkAssignmentAdvancedSummaryDayNodeDiagnosticSnapshot Direct { get; set; } = new();
}

public sealed class WorkAssignmentAdvancedSummaryDayNodeDiagnosticSnapshot
{
    public string? NodeId { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public long SourceReportCount { get; set; }
    public string? SourceSignatureHash { get; set; }
    public string? ValueHash { get; set; }
    public string? ComparableValueHash { get; set; }
    public string? ComparableValueError { get; set; }
    public DateTime? BuiltAtUtc { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public string? ValueJson { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryHierarchyQueryResponse
{
    public string ConfigId { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string StartDayKey { get; set; } = string.Empty;
    public string EndDayKey { get; set; } = string.Empty;
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public string Status { get; set; } = "MISSING";
    public string? ResultJson { get; set; }
    public string? ResultHash { get; set; }
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> SelectedNodes { get; set; } = new();
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> MissingNodes { get; set; } = new();
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> DirtyNodes { get; set; } = new();
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> BuildingNodes { get; set; } = new();
    public List<WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto> EnqueuedNodes { get; set; } = new();
}

public sealed class WorkAssignmentAdvancedSummaryHierarchyQueryNodeDto
{
    public string Grain { get; set; } = string.Empty;
    public string GrainKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public string? ValueHash { get; set; }
    public long SourceReportCount { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryDayNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string ConfigId { get; set; } = string.Empty;
    public int ConfigVersionNo { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public string Grain { get; set; } = "DAY";
    public string GrainKey { get; set; } = string.Empty;
    public string DayKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public string? DirtyReason { get; set; }
    public long SourceReportCount { get; set; }
    public string? SourceSignatureHash { get; set; }
    public string ValueJson { get; set; } = "{}";
    public string? ValueHash { get; set; }
    public DateTime? BuiltAtUtc { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryMonthNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string ConfigId { get; set; } = string.Empty;
    public int ConfigVersionNo { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public string Grain { get; set; } = "MONTH";
    public string GrainKey { get; set; } = string.Empty;
    public string MonthKey { get; set; } = string.Empty;
    public string YearKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public string? DirtyReason { get; set; }
    public long SourceReportCount { get; set; }
    public string? SourceSignatureHash { get; set; }
    public string ValueJson { get; set; } = "{}";
    public string? ValueHash { get; set; }
    public DateTime? BuiltAtUtc { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WorkAssignmentAdvancedSummaryYearNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string ConfigId { get; set; } = string.Empty;
    public int ConfigVersionNo { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public string Grain { get; set; } = "YEAR";
    public string GrainKey { get; set; } = string.Empty;
    public string YearKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public string? DirtyReason { get; set; }
    public long SourceReportCount { get; set; }
    public string? SourceSignatureHash { get; set; }
    public string ValueJson { get; set; } = "{}";
    public string? ValueHash { get; set; }
    public DateTime? BuiltAtUtc { get; set; }
    public string? BuildJobId { get; set; }
    public string? BuildCorrelationId { get; set; }
    public string? BuildError { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndExclusiveUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
