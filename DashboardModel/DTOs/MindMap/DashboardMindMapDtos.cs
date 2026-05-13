using tdtd_be.DTOs.Common;

namespace tdtd_be.DashboardModel.DTOs.MindMap;

public class DashboardMindMapScopeRequest
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public List<string> UnitIds { get; set; } = new();
}

public sealed class DashboardMindMapWorkResponse
{
    public DashboardWorkTreeWorkDto Work { get; set; } = new();
    public PagedResult<DashboardTreeNodeDto> RootAssignments { get; set; } =
        new(new List<DashboardTreeNodeDto>(), 0, 0, 0);
}

public sealed class DashboardMindMapCursorResult<T>
{
    public List<T> Rows { get; set; } = new();
    public long TotalRows { get; set; }
    public int Limit { get; set; }
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

public sealed class DashboardMindMapTemplateGroupDto
{
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string DynamicFormTemplateCode { get; set; } = string.Empty;
    public string DynamicFormTemplateName { get; set; } = string.Empty;
    public string? DynamicExcelId { get; set; }
    public string? DynamicExcelCode { get; set; }
    public string? DynamicExcelName { get; set; }
    public int UserCount { get; set; }
    public int ReportCount { get; set; }
    public int OverdueCount { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }
    public DashboardStackedBarDto ReportBar { get; set; } = new();
}

public sealed class DashboardMindMapTemplateUserDto
{
    public string AssignmentId { get; set; } = string.Empty;
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string DynamicFormTemplateCode { get; set; } = string.Empty;
    public string DynamicFormTemplateName { get; set; } = string.Empty;
    public string? DynamicExcelId { get; set; }
    public string AssigneeUserId { get; set; } = string.Empty;
    public string AssigneeUsername { get; set; } = string.Empty;
    public string AssigneeFullName { get; set; } = string.Empty;
    public string? UnitId { get; set; }
    public string? UnitLabel { get; set; }
    public int TotalReports { get; set; }
    public int OverdueCount { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }
    public DashboardStackedBarDto ReportBar { get; set; } = new();
}

public sealed class DashboardMindMapTemplateReportsSearchRequest
{
    public List<string> AssigneeUserIds { get; set; } = new();
    public List<string> StatusBuckets { get; set; } = new();
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? Q { get; set; }
    public string? Cursor { get; set; }
    public int Limit { get; set; } = 5;
}

public sealed class DashboardMindMapNodeChildrenSearchRequest : DashboardMindMapScopeRequest
{
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 20;
}

public sealed class DashboardMindMapNodeUnitsSearchRequest : DashboardMindMapScopeRequest
{
    public string? Bucket { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 20;
}

public sealed class DashboardMindMapNodeReportsSearchRequest : DashboardMindMapScopeRequest
{
    public string? Bucket { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 20;
}

public sealed class DashboardMindMapTableMetricReportsSearchRequest : DashboardMindMapScopeRequest
{
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string? BlockId { get; set; }
    public string? TableMode { get; set; }
    public string MetricKey { get; set; } = string.Empty;
    public int? ReportStatus { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 20;
}

public sealed class DashboardMindMapFieldMetricReportsSearchRequest : DashboardMindMapScopeRequest
{
    public string? DynamicFormTemplateId { get; set; }
    public string FieldId { get; set; } = string.Empty;
    public string? BucketKey { get; set; }
    public int? ReportStatus { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 20;
}

public sealed class DashboardMindMapLabelReportsSearchRequest : DashboardMindMapScopeRequest
{
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string? BlockId { get; set; }
    public string LabelCode { get; set; } = string.Empty;
    public int? ReportStatus { get; set; }
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 20;
}

public sealed class DashboardStackedBarDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Total { get; set; }
    public List<DashboardStackedBarSegmentDto> Segments { get; set; } = new();
}

public sealed class DashboardStackedBarSegmentDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
    public string Color { get; set; } = string.Empty;
}

public sealed class DashboardMindMapNodeSummaryDto
{
    public DashboardTreeNodeDto Node { get; set; } = new();
    public int DescendantAssignmentCount { get; set; }
    public int ActiveAssignmentCount { get; set; }
    public int TotalAssigneeCount { get; set; }
    public DashboardNodeReportSummaryDto ReportSummary { get; set; } = new();
    public DashboardStackedBarDto UnitBar { get; set; } = new();
    public DashboardStackedBarDto ReportBar { get; set; } = new();
    public List<DashboardMindMapLabelSummaryDto> LabelSummaries { get; set; } = new();
    public List<DashboardMindMapTableSummaryDto> TableSummaries { get; set; } = new();
    public List<DashboardMindMapFieldSummaryDto> FieldSummaries { get; set; } = new();
}

public sealed class DashboardMindMapLabelSummaryDto
{
    public string LabelCode { get; set; } = string.Empty;
    public string? LabelName { get; set; }
    public string? LabelColor { get; set; }
    public long RowCount { get; set; }
    public long ReportCount { get; set; }
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string BlockId { get; set; } = string.Empty;
}

public sealed class DashboardMindMapTableSummaryDto
{
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string BlockId { get; set; } = string.Empty;
    public string TableMode { get; set; } = string.Empty;
    public string MetricKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public string ColumnKey { get; set; } = string.Empty;
    public long ValueCount { get; set; }
    public decimal Sum { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Average { get; set; }
    public long ReportCount { get; set; }
}

public sealed class DashboardMindMapFieldSummaryDto
{
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string FieldId { get; set; } = string.Empty;
    public string FieldKey { get; set; } = string.Empty;
    public string FieldLabel { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public string? BucketKey { get; set; }
    public string? BucketLabel { get; set; }
    public long ValueCount { get; set; }
    public long NumericValueCount { get; set; }
    public decimal? Sum { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Average { get; set; }
    public long TrueCount { get; set; }
    public long FalseCount { get; set; }
    public DateTime? LatestDateUtc { get; set; }
    public long ReportCount { get; set; }
}

public sealed class DashboardMindMapUnitRowDto
{
    public string? AssigneeUserId { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? AssigneeFullName { get; set; }
    public string? UnitId { get; set; }
    public string? UnitLabel { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public int TotalReports { get; set; }
    public int TodoCount { get; set; }
    public int DoneCount { get; set; }
    public int OverdueCount { get; set; }
    public string? LatestPeriodKey { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }
    public string? CurrentProgressStatus { get; set; }
    public string? Difficulties { get; set; }
    public string? LateReason { get; set; }
    public string? ReturnReason { get; set; }
    public string? ReviewerComment { get; set; }
    public string? WorstOverdueReasonCode { get; set; }
    public string? WorstOverdueReasonLabel { get; set; }
}

public sealed class DashboardMindMapReportRowDto
{
    public string WorkReportPeriodId { get; set; } = string.Empty;
    public string? ReportId { get; set; }
    public string AssignmentId { get; set; } = string.Empty;
    public string? AssignmentCode { get; set; }
    public string AssignmentName { get; set; } = string.Empty;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeFullName { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? UnitId { get; set; }
    public string? UnitLabel { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public int PeriodStatus { get; set; }
    public int? ReportStatus { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? CurrentProgressStatus { get; set; }
    public string? Difficulties { get; set; }
    public string? ReportReason { get; set; }
    public string? ProposedSolution { get; set; }
    public string? LateReason { get; set; }
    public string? ReturnReason { get; set; }
    public string? ReviewerComment { get; set; }
    public string? ReviewerEvaluation { get; set; }
}

public sealed class DashboardMindMapTableMetricReportRowDto
{
    public string WorkAssignmentReportId { get; set; } = string.Empty;
    public string WorkReportPeriodId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string? AssignmentCode { get; set; }
    public string AssignmentName { get; set; } = string.Empty;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeFullName { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? UnitId { get; set; }
    public string? UnitLabel { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodInstanceKey { get; set; } = string.Empty;
    public string PeriodKind { get; set; } = string.Empty;
    public int ReportStatus { get; set; }
    public string BlockId { get; set; } = string.Empty;
    public string TableMode { get; set; } = string.Empty;
    public string MetricKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public string ColumnKey { get; set; } = string.Empty;
    public long ValueCount { get; set; }
    public decimal Sum { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Average { get; set; }
    public List<string> SourceKeys { get; set; } = new();
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
}

public sealed class DashboardMindMapFieldMetricReportRowDto
{
    public string WorkAssignmentReportId { get; set; } = string.Empty;
    public string WorkReportPeriodId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string? AssignmentCode { get; set; }
    public string AssignmentName { get; set; } = string.Empty;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeFullName { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? UnitId { get; set; }
    public string? UnitLabel { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodInstanceKey { get; set; } = string.Empty;
    public string PeriodKind { get; set; } = string.Empty;
    public int ReportStatus { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string FieldId { get; set; } = string.Empty;
    public string FieldKey { get; set; } = string.Empty;
    public string FieldLabel { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public string? BucketKey { get; set; }
    public string? BucketLabel { get; set; }
    public long ValueCount { get; set; }
    public long NumericValueCount { get; set; }
    public decimal? Sum { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Average { get; set; }
    public long TrueCount { get; set; }
    public long FalseCount { get; set; }
    public DateTime? LatestDateUtc { get; set; }
    public List<string> SourceKeys { get; set; } = new();
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
}

public sealed class DashboardMindMapLabelReportRowDto
{
    public string WorkAssignmentReportId { get; set; } = string.Empty;
    public string WorkReportPeriodId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public string? AssignmentCode { get; set; }
    public string AssignmentName { get; set; } = string.Empty;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeFullName { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? UnitId { get; set; }
    public string? UnitLabel { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public string PeriodInstanceKey { get; set; } = string.Empty;
    public string PeriodKind { get; set; } = string.Empty;
    public int ReportStatus { get; set; }
    public string? DynamicFormTemplateId { get; set; }
    public string? DynamicFormTemplateName { get; set; }
    public string? DynamicExcelTemplateId { get; set; }
    public string LabelCode { get; set; } = string.Empty;
    public string? LabelName { get; set; }
    public string? LabelColor { get; set; }
    public long RowCount { get; set; }
    public List<string> BlockIds { get; set; } = new();
    public List<string> RowKeys { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
}
