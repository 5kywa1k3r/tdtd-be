namespace tdtd_be.DTOs.WorkAssignmentReports;

/// <summary>
/// Snapshot nhẹ của template tại thời điểm khởi tạo report.
/// DTO này chủ yếu dùng nội bộ service để serialize sang TemplateSnapshotJson.
/// </summary>
public sealed class TemplateSnapshotDTO
{
    /// <summary>
    /// Id template gốc.
    /// </summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>
    /// Mã template.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Tên template.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Spec JSON của template.
    /// </summary>
    public string SpecJson { get; set; } = string.Empty;

    /// <summary>
    /// Hàng bắt đầu của dataRect.
    /// </summary>
    public int DataRectR0 { get; set; }

    /// <summary>
    /// Cột bắt đầu của dataRect.
    /// </summary>
    public int DataRectC0 { get; set; }

    /// <summary>
    /// Hàng kết thúc của dataRect.
    /// </summary>
    public int DataRectR1 { get; set; }

    /// <summary>
    /// Cột kết thúc của dataRect.
    /// </summary>
    public int DataRectC1 { get; set; }

    /// <summary>
    /// Số cột vùng dữ liệu.
    /// </summary>
    public int W { get; set; }

    /// <summary>
    /// Số hàng vùng dữ liệu.
    /// </summary>
    public int H { get; set; }
}
