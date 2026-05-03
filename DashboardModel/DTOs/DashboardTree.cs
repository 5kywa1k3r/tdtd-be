namespace tdtd_be.DashboardModel.DTOs
{
    public sealed class DashboardWorkTreeRootDto
    {
        public DashboardWorkTreeWorkDto Work { get; set; } = new();
        public List<DashboardTreeNodeDto> RootAssignments { get; set; } = new();
    }

    public sealed class DashboardWorkTreeWorkDto
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        // FE tự map label theo rule động của work
        public int Status { get; set; }

        public int ActiveRootAssignmentCount { get; set; }
        public bool HasOverduePeriod { get; set; }
        public bool HasManualEvaluations { get; set; }
        public string? WorstEvaluationCode { get; set; }
        public string? WorstEvaluationLabel { get; set; }

        public DashboardProgressCountDto RootAssignmentProgressCounts { get; set; } = new();
    }

    public sealed class DashboardTreeNodeDto
    {
        public string Id { get; set; } = string.Empty;
        public string WorkId { get; set; } = string.Empty;
        public string? ParentAssignmentId { get; set; }
        public string RootAssignmentId { get; set; } = string.Empty;
        public int Level { get; set; }

        public string Code { get; set; } = string.Empty;
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
        public bool HasChildren { get; set; }

        public DashboardNodeManualEvaluationDto ManualEvaluation { get; set; } = new();
        public DashboardNodeReportSummaryDto ReportSummary { get; set; } = new();
        public List<DashboardNodeAssigneeDto> Assignees { get; set; } = new();
        public DashboardProgressCountDto ChildProgressCounts { get; set; } = new();
    }

    public sealed class DashboardNodeManualEvaluationDto
    {
        public bool HasManualEvaluations { get; set; }
        public int EvaluatedAssignmentCount { get; set; }
        public string? EvaluationCode { get; set; }
        public string? EvaluationLabel { get; set; }
        public string? WorstEvaluationCode { get; set; }
        public string? WorstEvaluationLabel { get; set; }
    }

    public sealed class DashboardNodeReportSummaryDto
    {
        public int Total { get; set; }

        public int PendingCount { get; set; }
        public int DraftCount { get; set; }
        public int SubmittedCount { get; set; }
        public int ApprovedCount { get; set; }

        public int OverduePendingCount { get; set; }
        public int OverdueDraftCount { get; set; }
        public int OverdueSubmittedCount { get; set; }
        public int OverdueApprovedCount { get; set; }
    }

    public sealed class DashboardNodeAssigneeDto
    {
        public string UserId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;

        public string? UnitId { get; set; }
        public string? UnitName { get; set; }
        public string? UnitSymbol { get; set; }
        public string? UnitShortName { get; set; }
    }
}
