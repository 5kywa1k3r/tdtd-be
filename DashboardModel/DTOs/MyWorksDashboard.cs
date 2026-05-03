namespace tdtd_be.DashboardModel.DTOs
{
    public sealed class MyWorksDashboardRequest
    {
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }

        // Lọc theo đơn vị assignee ở tầng root assignment.
        public List<string> UnitIds { get; set; } = new();

        public string? Keyword { get; set; }

        public bool ForceRefresh { get; set; }
    }

    public sealed class MyWorksDashboardResponse
    {
        public DashboardRangeDto Range { get; set; } = new();
        public MyWorksDashboardSummaryDto Summary { get; set; } = new();
        public List<MyWorkSummaryRowDto> Works { get; set; } = new();
    }

    public sealed class MyWorksDashboardSummaryDto
    {
        public int TotalWorks { get; set; }
        public int ActiveRootAssignmentCount { get; set; }
        public int ManualEvaluatedWorkCount { get; set; }

        // Tổng hợp histogram root assignment của các work trong scope hiện tại.
        public DashboardProgressCountDto RootAssignmentProgressCounts { get; set; } = new();
    }

    public sealed class MyWorkSummaryRowDto
    {
        public string WorkId { get; set; } = string.Empty;
        public string? WorkCode { get; set; }
        public string WorkName { get; set; } = string.Empty;
        public string? WorkType { get; set; }

        // Label để FE tự map theo rule động của work.
        public int Status { get; set; }

        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public DateTime? DueDate { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }

        public int ActiveRootAssignmentCount { get; set; }
        public DashboardProgressCountDto RootAssignmentProgressCounts { get; set; } = new();

        public bool HasManualEvaluations { get; set; }
        public int EvaluatedAssignmentCount { get; set; }
        public string? WorstEvaluationCode { get; set; }
        public string? WorstEvaluationLabel { get; set; }
    }

    public sealed class WorkDashboardDetailRequest
    {
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }

        public List<string> UnitIds { get; set; } = new();

        public bool IncludeRootAssignments { get; set; } = true;
        public bool IncludeReportSummary { get; set; } = true;

        public bool ForceRefresh { get; set; }
    }
}