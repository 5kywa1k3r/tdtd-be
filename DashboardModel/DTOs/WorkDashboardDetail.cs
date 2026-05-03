namespace tdtd_be.DashboardModel.DTOs
{
    public sealed class WorkDashboardDetailDto
    {
        public MyWorkSummaryRowDto Work { get; set; } = new();
        public List<WorkDashboardRootAssignmentRowDto> RootAssignments { get; set; } = new();
        public DashboardNodeReportSummaryDto ReportSummary { get; set; } = new();
    }

    public sealed class WorkDashboardRootAssignmentRowDto
    {
        public string AssignmentId { get; set; } = string.Empty;
        public string WorkId { get; set; } = string.Empty;

        public string? Code { get; set; }
        public string DynamicExcelId { get; set; } = string.Empty;
        public string DynamicExcelCode { get; set; } = string.Empty;
        public string DynamicExcelName { get; set; } = string.Empty;
        public string? Description { get; set; }

        public bool IsActive { get; set; }

        // FE tự map label theo rule động của work
        public int ProgressStatus { get; set; }

        public bool HasAnyDuePeriod { get; set; }
        public bool HasOverduePeriod { get; set; }
        public int? WorstPeriodStatus { get; set; }
        public string? WorstOverdueReasonCode { get; set; }
        public string? WorstOverdueReasonLabel { get; set; }

        public DateTime? LatestDueAtUtc { get; set; }

        public int ActiveChildCount { get; set; }
        public DashboardProgressCountDto ChildProgressCounts { get; set; } = new();

        public bool HasManualEvaluations { get; set; }
        public int EvaluatedAssignmentCount { get; set; }
        public string? EvaluationCode { get; set; }
        public string? EvaluationLabel { get; set; }
        public string? WorstEvaluationCode { get; set; }
        public string? WorstEvaluationLabel { get; set; }

        public DashboardNodeReportSummaryDto ReportSummary { get; set; } = new();

        public List<DashboardNodeAssigneeDto> Assignees { get; set; } = new();
    }
}