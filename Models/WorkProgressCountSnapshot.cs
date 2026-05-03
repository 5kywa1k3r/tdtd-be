using tdtd_be.Enum;

namespace tdtd_be.Models;

public sealed class WorkProgressCountSnapshot
{
    public int NotStarted { get; set; }
    public int InProgress { get; set; }
    public int Completed { get; set; }
    public int AtRiskOverdue { get; set; }
    public int Overdue { get; set; }

    public int TotalActive =>
        NotStarted + InProgress + Completed + AtRiskOverdue + Overdue;

    public void Add(WorkAssignmentProgressStatus status, int amount = 1)
    {
        switch (status)
        {
            case WorkAssignmentProgressStatus.NotStarted:
                NotStarted += amount;
                break;
            case WorkAssignmentProgressStatus.InProgress:
                InProgress += amount;
                break;
            case WorkAssignmentProgressStatus.Completed:
                Completed += amount;
                break;
            case WorkAssignmentProgressStatus.AtRiskOverdue:
                AtRiskOverdue += amount;
                break;
            case WorkAssignmentProgressStatus.Overdue:
                Overdue += amount;
                break;
        }
    }

    public void Remove(WorkAssignmentProgressStatus status, int amount = 1)
    {
        Add(status, -amount);
        Normalize();
    }

    public void Normalize()
    {
        if (NotStarted < 0) NotStarted = 0;
        if (InProgress < 0) InProgress = 0;
        if (Completed < 0) Completed = 0;
        if (AtRiskOverdue < 0) AtRiskOverdue = 0;
        if (Overdue < 0) Overdue = 0;
    }

    public WorkAssignmentProgressStatus GetWorstStatus()
    {
        if (Overdue > 0) return WorkAssignmentProgressStatus.Overdue;
        if (AtRiskOverdue > 0) return WorkAssignmentProgressStatus.AtRiskOverdue;
        if (InProgress > 0) return WorkAssignmentProgressStatus.InProgress;
        if (NotStarted > 0) return WorkAssignmentProgressStatus.NotStarted;
        if (Completed > 0) return WorkAssignmentProgressStatus.Completed;
        return WorkAssignmentProgressStatus.NotStarted;
    }

    public WorkProgressCountSnapshot Clone() =>
        new()
        {
            NotStarted = NotStarted,
            InProgress = InProgress,
            Completed = Completed,
            AtRiskOverdue = AtRiskOverdue,
            Overdue = Overdue
        };
}
