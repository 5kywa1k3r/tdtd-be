namespace tdtd_be.DashboardModel.DTOs
{

    public enum DashboardMode
    {
        Work = 1,
        Assignment = 2,
        Report = 3
    }

    public enum DashboardWorkTypeFilter
    {
        All = 0,
        Task = 1,
        Indicator = 2
    }

    public sealed class DashboardRangeDto
    {
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public sealed class DashboardSummaryDto
    {
        public int Total { get; set; }
        public int OverdueCount { get; set; }
        public int ManualEvaluatedCount { get; set; }
    }
}
