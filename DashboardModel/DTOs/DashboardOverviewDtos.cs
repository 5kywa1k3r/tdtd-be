using tdtd_be.DashboardModel.DTOs;

namespace tdtd_be.DashboardModel.DTOs;

public sealed class DashboardOverviewRequest
{
    public string? Mode { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public List<string> UnitIds { get; set; } = new();
    public string? AssignmentId { get; set; }
    public int TopUnitCount { get; set; } = 3;
    public bool ForceRefresh { get; set; }
}

public sealed class DashboardOverviewResponse
{
    public string Mode { get; set; } = string.Empty;
    public DashboardRangeDto Range { get; set; } = new();
    public List<DashboardOverviewMetricDto> Cards { get; set; } = new();
    public List<DashboardOverviewPieSliceDto> Pie { get; set; } = new();
    public Dictionary<string, List<DashboardUnitBarRowDto>> UnitCharts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<DashboardOverviewTableRowDto> Rows { get; set; } = new();
}

public sealed class DashboardOverviewMetricDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
    public string? ValueColor { get; set; }
    public string? SecondaryLabel { get; set; }
    public string? SecondaryValue { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
}

public sealed class DashboardOverviewPieSliceDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
    public string Color { get; set; } = string.Empty;
}

public sealed class DashboardUnitBarRowDto
{
    public string? UnitId { get; set; }
    public string UnitLabel { get; set; } = string.Empty;
    public int Total { get; set; }
    public List<DashboardUnitBarSegmentDto> Segments { get; set; } = new();
}

public sealed class DashboardUnitBarSegmentDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
    public string Color { get; set; } = string.Empty;
}

public sealed class DashboardOverviewTableRowDto
{
    public string Id { get; set; } = string.Empty;
    public string? WorkId { get; set; }
    public string? WorkCode { get; set; }
    public string? WorkName { get; set; }
    public string? WorkType { get; set; }
    public int? WorkStatus { get; set; }

    public string? AssignmentId { get; set; }
    public string? AssignmentCode { get; set; }
    public string? AssignmentName { get; set; }
    public int? AssignmentProgressStatus { get; set; }

    public string? FirstAssigneeName { get; set; }
    public string? FirstAssigneeUsername { get; set; }
    public string? UnitId { get; set; }
    public string? UnitLabel { get; set; }

    public int ReportTotal { get; set; }
    public int PendingCount { get; set; }
    public int DraftCount { get; set; }
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int OverdueCount { get; set; }

    public string? PeriodKey { get; set; }
    public string? ReportStatusKey { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class DashboardReportAssignmentOptionDto
{
    public string AssignmentId { get; set; } = string.Empty;
    public string? WorkId { get; set; }
    public string? WorkName { get; set; }
    public string? AssignmentCode { get; set; }
    public string? AssignmentName { get; set; }
    public string Label { get; set; } = string.Empty;
}

public sealed class DashboardReportAssignmentOptionsRequest
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public List<string> UnitIds { get; set; } = new();
}
