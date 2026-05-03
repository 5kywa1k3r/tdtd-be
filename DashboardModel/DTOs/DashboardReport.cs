namespace tdtd_be.DashboardModel.DTOs
{
    public sealed class DashboardReportSummaryRequest
    {
        public string WorkId { get; set; } = string.Empty;

        // Chỉ truyền một trong hai nếu muốn drill xuống
        public string? RootAssignmentId { get; set; }
        public string? AssignmentId { get; set; }

        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
    }
    public sealed class DashboardReportSummaryResponse
    {
        public DashboardRangeDto Range { get; set; } = new();
        public DashboardNodeReportSummaryDto Summary { get; set; } = new();
        public List<DashboardReportStatusBucketDto> StatusBuckets { get; set; } = new();
    }
    public sealed class DashboardReportStatusBucketDto
    {
        public int Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
