namespace tdtd_be.DashboardModel.DTOs;

public sealed class DashboardProgressCountDto
{
    public int NotStarted { get; set; }
    public int InProgress { get; set; }
    public int Completed { get; set; }
    public int AtRiskOverdue { get; set; }
    public int Overdue { get; set; }
    public int Total { get; set; }
}