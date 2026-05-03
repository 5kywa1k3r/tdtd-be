namespace tdtd_be.Enum
{
    public enum WorkAssignmentProgressStatus
    {
        NotStarted = 0,     // chưa đến kỳ nào
        InProgress = 1,     // đang thực hiện bình thường
        Completed = 2,      // scope node này đã hoàn thành
        AtRiskOverdue = 3,  // có kỳ đến hạn mà chưa approved
        Overdue = 4         // quá endDate mà scope chưa completed
    }
}
