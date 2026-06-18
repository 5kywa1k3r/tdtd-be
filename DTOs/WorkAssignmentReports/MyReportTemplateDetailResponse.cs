namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Detail của một template report trong phạm vi 1 work theo góc nhìn user hiện tại.
/// Màn này sẽ:
/// - load bảng/template trước
/// - sau đó trả danh sách các kỳ cần báo cáo
/// </summary>
public sealed class MyReportTemplateDetailResponse
{
    public string WorkId { get; set; } = string.Empty;

    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string DynamicFormTemplateCode { get; set; } = string.Empty;
    public string DynamicFormTemplateName { get; set; } = string.Empty;

    public string? DynamicExcelId { get; set; }
    public string DynamicExcelCode { get; set; } = string.Empty;
    public string DynamicExcelName { get; set; } = string.Empty;

    /// <summary>
    /// Binding runtime hiện hành đại diện.
    /// </summary>
    public string? WorkTemplateAssigneeId { get; set; }

    /// <summary>
    /// Assignment hiện hành đại diện.
    /// </summary>
    public string? WorkAssignmentId { get; set; }

    /// <summary>
    /// Spec gốc của template để FE render vùng header/data đúng ngay từ detail.
    /// </summary>
    public string SpecJson { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot nhẹ của template, không bao gồm workbook JSON nặng.
    /// Workbook được tải riêng khi người dùng mở bảng.
    /// </summary>
    public string TemplateSnapshotJson { get; set; } = string.Empty;

    /// <summary>
    /// Danh sách kỳ phải báo cáo của template này.
    /// </summary>
    public List<WorkReportPeriodRow> Periods { get; set; } = new();

    /// <summary>
    /// Danh sách phân công/binding hiện hành của user cho biểu mẫu này.
    /// </summary>
    public List<MyReportTemplateAssignmentOption> AssignmentOptions { get; set; } = new();
}

public sealed class MyReportTemplateAssignmentOption
{
    public string WorkAssignmentId { get; set; } = string.Empty;
    public string WorkTemplateAssigneeId { get; set; } = string.Empty;

    public string? AssignmentCode { get; set; }
    public string? AssignmentType { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public DateTime? DueAtUtc { get; set; }

    public bool IsActive { get; set; } = true;
}
