namespace tdtd_be.DashboardModel.DTOs;

public sealed class DashboardLeadershipItemsRequest
{
    public DashboardOverviewRequest Context { get; set; } = new() { View = "LEADERSHIP" };
    public string Selection { get; set; } = "OPEN";
    public string? WorkId { get; set; }
    public int? Status { get; set; }
    public int? Priority { get; set; }
    public string? BucketFromDate { get; set; }
    public string? BucketToDate { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;
}

public sealed class DashboardLeadershipDto
{
    public string Section { get; set; } = "CURRENT";
    public string ScopeLabel { get; set; } = "";
    public DateTime GeneratedAtUtc { get; set; }
    public string AsOfDate { get; set; } = "";
    public string FromDate { get; set; } = "";
    public string ToDate { get; set; } = "";
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";
    public string EffectiveBucket { get; set; } = "DAY";
    public string CohortKind { get; set; } = "CURRENT_OPEN";
    public string AssignmentBasis { get; set; } = "READABLE_BRANCH_ENTRIES";
    public string ReportBasis { get; set; } = "MATERIALIZED_PERIODS_DUE_RANGE";
    public int TotalOpenWorkCount { get; set; }
    public int TotalWorkCount { get; set; }
    public int TotalAssignmentCount { get; set; }
    public int InProgressWorkCount { get; set; }
    public int OverdueWorkCount { get; set; }
    public int NoDeadlineWorkCount { get; set; }
    public int OverdueBeforeWindowCount { get; set; }
    public int TimelineWorkCount { get; set; }
    public int MissingWorkCompletionDateCount { get; set; }
    public int MissingAssignmentCompletionDateCount { get; set; }
    public List<DashboardLeadershipCountDto> WorkStatus { get; set; } = new();
    public List<DashboardLeadershipCountDto> AssignmentStatus { get; set; } = new();
    public List<DashboardLeadershipTimingDto> Timeliness { get; set; } = new();
    public List<DashboardLeadershipMatrixDto> PriorityStatus { get; set; } = new();
    // CURRENT open Work partitioned by actual business-day deadline, independent of source status.
    public List<DashboardLeadershipCountDto> DeadlineBuckets { get; set; } = new();
    public List<DashboardLeadershipItemDto> WorkProgress { get; set; } = new();
    public List<DashboardLeadershipItemDto> Timeline { get; set; } = new();
    public List<DashboardLeadershipActivityDto> WorkActivity { get; set; } = new();
    public List<DashboardLeadershipActivityDto> AssignmentActivity { get; set; } = new();
    public List<DashboardLeadershipCountDto> ReportStatus { get; set; } = new();
    // Review status and timeliness are independent dimensions; do not add these together.
    public int ReportTotal { get; set; }
    public int DuePeriodTotal { get; set; }
    public int MissingReportCount { get; set; }
    public int UnavailableReportPeriodCount { get; set; }
    public int ApprovedReportCount { get; set; }
    public int ReturnedReportCount { get; set; }
    public int LateReportCount { get; set; }
    public int OnTimeReportCount { get; set; }
    public int UnassessedReportCount { get; set; }
}

public sealed class DashboardLeadershipCountDto
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
}
public sealed class DashboardLeadershipTimingDto
{
    public string ObjectKind { get; set; } = "";
    public string Key { get; set; } = "";
    public int Count { get; set; }
}
public sealed class DashboardLeadershipMatrixDto
{
    public int Priority { get; set; }
    public int Status { get; set; }
    public int Count { get; set; }
}
public sealed class DashboardLeadershipActivityDto
{
    public string FromDate { get; set; } = "";
    public string ToDate { get; set; } = "";
    public int CreatedCount { get; set; }
    public int CompletedCount { get; set; }
}
public sealed record DashboardLeadershipItemDto
{
    public int WorkStatus { get; set; }
    public string? ReportPeriodId { get; set; }
    public string? ReadState { get; set; }
    public int? SourceReportStatus { get; set; }
    public string Id { get; set; } = "";
    public string ObjectKind { get; set; } = "WORK";
    public string WorkId { get; set; } = "";
    public int WorkType { get; set; }
    public string WorkCode { get; set; } = "";
    public string WorkName { get; set; } = "";
    public string? AssignmentId { get; set; }
    public string? AssignmentName { get; set; }
    public string? ReportId { get; set; }
    public string? AssigneeName { get; set; }
    public string? PeriodKey { get; set; }
    public int? Status { get; set; }
    public int Priority { get; set; }
    public string? StartDate { get; set; }
    public string? DueDate { get; set; }
    public string? CompletedDate { get; set; }
    public string? ReportStatus { get; set; }
    public string? Timeliness { get; set; }
    public int AssignmentCount { get; set; }
    public int CompletedAssignmentCount { get; set; }
}
public sealed class DashboardLeadershipItemsDto
{
    public List<DashboardLeadershipItemDto> Rows { get; set; } = new();
    public int TotalRows { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public string Selection { get; set; } = "";
    public string ScopeLabel { get; set; } = "";
    public DateTime GeneratedAtUtc { get; set; }
}
