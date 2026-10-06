namespace tdtd_be.DashboardModel.DTOs.MindMap;

public sealed class DashboardMindMapCardSummariesRequest : DashboardMindMapScopeRequest
{
    public List<string> AssignmentIds { get; set; } = new();
}

public sealed class DashboardMindMapCardSummariesResponse
{
    public string CountBasis { get; set; } = "MATERIALIZED_PERIODS";
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<DashboardMindMapCardSummaryDto> Rows { get; set; } = new();
}

public sealed class DashboardMindMapCardSummaryDto
{
    public string AssignmentId { get; set; } = string.Empty;
    public string ScheduleKind { get; set; } = "UNKNOWN";
    public string ScheduleLabel { get; set; } = "Chưa rõ lịch";
    public int RecipientUnitCount { get; set; }
    public int RecipientUserCount { get; set; }
    public int UnknownRecipientUnitCount { get; set; }
    public string ReadState { get; set; } = "READY";
    public List<DashboardMindMapCardFormSummaryDto> Forms { get; set; } = new();
}

public sealed class DashboardMindMapCardFormSummaryDto
{
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string DynamicFormTemplateCode { get; set; } = string.Empty;
    public string DynamicFormTemplateName { get; set; } = string.Empty;
    public int RecipientUnitCount { get; set; }
    public int RecipientUserCount { get; set; }
    public int UnknownRecipientUnitCount { get; set; }
    public int PeriodCount { get; set; }
    public int ReportedCount { get; set; }
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int ReturnedCount { get; set; }
    public int MissingCount { get; set; }
    public int OverdueCount { get; set; }
    public int NoDueDateCount { get; set; }
    public int UnavailableCurrentReportCount { get; set; }
    public string ReadState { get; set; } = "READY";
}
