using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models.Enums;

namespace tdtd_be.Models;

/// <summary>
/// Dữ liệu báo cáo thực tế của một WorkAssignment tại một kỳ cụ thể.
/// 
/// Hiểu ngắn gọn:
/// - WorkAssignment = yêu cầu báo cáo hiện hành
/// - WorkAssignmentReport = một lần nhập/nộp báo cáo theo kỳ
/// 
/// Report luôn snapshot lại template + schedule tại thời điểm phát sinh,
/// để sau này assignment đổi template/schedule thì report cũ vẫn giữ nguyên lịch sử.
/// </summary>
[BsonIgnoreExtraElements]
[BsonCollection("work_assignment_report")]
public sealed class WorkAssignmentReport : BaseEntity
{
    /// <summary>
    /// Id của report.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    /// <summary>
    /// Id của Work gốc để query nhanh theo root work.
    /// </summary>
    [BsonElement("workId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkId { get; set; } = default!;

    /// <summary>
    /// Id của WorkAssignment sinh ra report này.
    /// Đây là khóa nghiệp vụ chính.
    /// </summary>
    [BsonElement("workAssignmentId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string WorkAssignmentId { get; set; } = default!;

    /// <summary>
    /// Kỳ báo cáo thực tế của report.
    /// Ví dụ:
    /// - 2026-03
    /// - 2026-Q1
    /// - 2026-W10
    /// - ONCE
    /// 
    /// Lưu tách riêng để report cũ không phụ thuộc schedule hiện tại của assignment.
    /// </summary>
    [BsonElement("periodKey")]
    public string PeriodKey { get; set; } = default!;

    /// <summary>
    /// Thời gian bắt đầu của kỳ báo cáo.
    /// Có thể null nếu một số loại kỳ chỉ dùng periodKey mà không cần range cụ thể.
    /// </summary>
    [BsonElement("periodStart")]
    public DateTime? PeriodStart { get; set; }

    /// <summary>
    /// Thời gian kết thúc của kỳ báo cáo.
    /// Có thể null nếu không cần.
    /// </summary>
    [BsonElement("periodEnd")]
    public DateTime? PeriodEnd { get; set; }

    /// <summary>
    /// Trạng thái hiện tại của report.
    /// Phase 1 chủ yếu dùng Draft.
    /// </summary>
    [BsonElement("status")]
    public WorkAssignmentReportStatus Status { get; set; } = WorkAssignmentReportStatus.Draft;

    /// <summary>
    /// Snapshot template tại thời điểm khởi tạo report.
    /// Nên chứa các thông tin tối thiểu như:
    /// - templateId
    /// - code
    /// - name
    /// - specJson
    /// - dataRect
    /// - workbook gốc
    /// 
    /// Lưu JSON string để đỡ cứng schema và giữ lịch sử chính xác.
    /// </summary>
    [BsonElement("templateSnapshotJson")]
    public string TemplateSnapshotJson { get; set; } = default!;

    /// <summary>
    /// Snapshot schedule của assignment tại thời điểm khởi tạo report.
    /// Dùng để lưu lịch sử cấu hình kỳ báo cáo lúc bản này được tạo.
    /// </summary>
    [BsonElement("scheduleSnapshotJson")]
    public string ScheduleSnapshotJson { get; set; } = default!;

    /// <summary>
    /// Id template đang được dùng tại thời điểm tạo report.
    /// Lưu riêng để search/filter nhanh, không phải parse templateSnapshotJson.
    /// </summary>
    [BsonElement("dynamicExcelTemplateId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string DynamicExcelTemplateId { get; set; } = default!;

    /// <summary>
    /// Code template để hiển thị nhanh ngoài list.
    /// </summary>
    [BsonElement("dynamicExcelTemplateCode")]
    public string DynamicExcelTemplateCode { get; set; } = default!;

    /// <summary>
    /// Tên template để hiển thị nhanh ngoài list.
    /// </summary>
    [BsonElement("dynamicExcelTemplateName")]
    public string DynamicExcelTemplateName { get; set; } = default!;

    /// <summary>
    /// FortuneSheet workbook JSON của bản report này sau khi user nhập dữ liệu.
    /// Đây là dữ liệu UI để mở lại đúng giao diện đã nhập.
    /// </summary>
    [BsonElement("rawWorkbookDataJson")]
    public string RawWorkbookDataJson { get; set; } = default!;

    /// <summary>
    /// Spec JSON của template/report.
    /// Lưu riêng để render/validate mà không phải parse snapshot lớn.
    /// </summary>
    [BsonElement("specJson")]
    public string SpecJson { get; set; } = default!;

    /// <summary>
    /// Góc trên-trái của vùng dữ liệu trong workbook.
    /// </summary>
    [BsonElement("dataRectR0")]
    public int DataRectR0 { get; set; }

    /// <summary>
    /// Cột bắt đầu của vùng dữ liệu trong workbook.
    /// </summary>
    [BsonElement("dataRectC0")]
    public int DataRectC0 { get; set; }

    /// <summary>
    /// Hàng kết thúc của vùng dữ liệu trong workbook.
    /// </summary>
    [BsonElement("dataRectR1")]
    public int DataRectR1 { get; set; }

    /// <summary>
    /// Cột kết thúc của vùng dữ liệu trong workbook.
    /// </summary>
    [BsonElement("dataRectC1")]
    public int DataRectC1 { get; set; }

    /// <summary>
    /// Số cột của vùng dữ liệu.
    /// Dùng validate values1D.Length == W * H
    /// </summary>
    [BsonElement("w")]
    public int W { get; set; }

    /// <summary>
    /// Số hàng của vùng dữ liệu.
    /// Dùng validate values1D.Length == W * H
    /// </summary>
    [BsonElement("h")]
    public int H { get; set; }

    /// <summary>
    /// Dữ liệu 1D đã trải phẳng từ dataRect.
    /// Đây là dữ liệu chuẩn hóa để tổng hợp/so sánh/query nhanh.
    /// 
    /// Khuyến nghị lưu theo thứ tự row-major:
    /// đi từ trái sang phải, từ trên xuống dưới.
    /// </summary>
    [BsonElement("values1DJson")]
    public string Values1DJson { get; set; } = default!;

    /// <summary>
    /// Thời điểm report được submit.
    /// Null nếu vẫn đang draft.
    /// </summary>
    [BsonElement("submittedAtUtc")]
    public DateTime? SubmittedAtUtc { get; set; }

    /// <summary>
    /// User thực hiện submit report.
    /// </summary>
    [BsonElement("submittedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? SubmittedByUserId { get; set; }

    /// <summary>
    /// Thời điểm report được approve.
    /// </summary>
    [BsonElement("approvedAtUtc")]
    public DateTime? ApprovedAtUtc { get; set; }

    /// <summary>
    /// User thực hiện approve report.
    /// </summary>
    [BsonElement("approvedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ApprovedByUserId { get; set; }

    /// <summary>
    /// Số phiên bản trong cùng 1 kỳ.
    /// Phase 1 có thể luôn là 1, nhưng nên có từ đầu để không phải migrate.
    /// </summary>
    [BsonElement("versionNo")]
    public int VersionNo { get; set; } = 1;

    /// <summary>
    /// Đánh dấu đây có phải bản hiện hành của cùng kỳ hay không.
    /// Dùng cho lịch sử version sau này.
    /// </summary>
    [BsonElement("isCurrent")]
    public bool IsCurrent { get; set; } = true;
}