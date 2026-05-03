namespace tdtd_be.DashboardModel.DTOs
{
    public sealed class AssignmentDashboardDetailDto
    {
        public AssignmentDashboardInfoDto Assignment { get; set; } = new();
        public DashboardNodeManualEvaluationDto ManualEvaluation { get; set; } = new();
        public DashboardNodeReportSummaryDto ReportSummary { get; set; } = new();
        public List<AssignmentDashboardReportRowDto> Reports { get; set; } = new();
    }

    public sealed class AssignmentDashboardInfoDto
    {
        public string Id { get; set; } = string.Empty;
        public string WorkId { get; set; } = string.Empty;

        public string DynamicExcelId { get; set; } = string.Empty;
        public string DynamicExcelCode { get; set; } = string.Empty;
        public string DynamicExcelName { get; set; } = string.Empty;

        public string AssignmentType { get; set; } = string.Empty;
        public string AggregationType { get; set; } = string.Empty;
        public string? Description { get; set; }

        public bool IsActive { get; set; }

        // FE tự map label theo rule động của work
        public int ProgressStatus { get; set; }

        public bool HasOverduePeriod { get; set; }
        public int? WorstPeriodStatus { get; set; }
        public DateTime? LatestDueAtUtc { get; set; }

        public DashboardProgressCountDto ChildProgressCounts { get; set; } = new();

        public bool HasManualEvaluations { get; set; }
        public int EvaluatedAssignmentCount { get; set; }
        public string? EvaluationCode { get; set; }
        public string? EvaluationLabel { get; set; }
        public string? WorstEvaluationCode { get; set; }
        public string? WorstEvaluationLabel { get; set; }
    }

    public sealed class AssignmentDashboardReportRowDto
    {
        public string WorkReportPeriodId { get; set; } = string.Empty;
        public string? WorkAssignmentReportId { get; set; }

        public string PeriodKey { get; set; } = string.Empty;
        public DateTime? PeriodStartUtc { get; set; }
        public DateTime? PeriodEndUtc { get; set; }
        public DateTime? DueAtUtc { get; set; }

        // FE tự map label theo rule động của work
        public int PeriodStatus { get; set; }
        public bool IsOverdue { get; set; }

        // Report document thực tế nếu đã có
        public int? ReportStatus { get; set; }

        public int ReportVersionCount { get; set; }

        public DateTime? LastSavedAtUtc { get; set; }
        public DateTime? SubmittedAtUtc { get; set; }
        public DateTime? ApprovedAtUtc { get; set; }
        public DateTime? ReturnedAtUtc { get; set; }

        public string? SubmitterUserId { get; set; }
        public string? SubmitterUsername { get; set; }
        public string? SubmitterFullName { get; set; }

        public string? ReviewerUserId { get; set; }
        public string? ReviewerUsername { get; set; }
        public string? ReviewerFullName { get; set; }

        public bool IsLateSubmission { get; set; }
        public string? LateReason { get; set; }

        public string? ReviewerComment { get; set; }
        public string? ReturnReason { get; set; }
    }
}