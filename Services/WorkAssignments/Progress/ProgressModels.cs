namespace tdtd_be.Services.WorkAssignments.Progress;

internal sealed class LeafProgressFacts
{
    public DateTime NowUtc { get; set; }
    public bool HasSchedule { get; set; }
    public bool IsEnded { get; set; }

    public bool HasAnyDuePeriod { get; set; }
    public bool HasAnyOpenPeriod { get; set; }
    public bool HasMaterializedPeriods { get; set; }
    public bool HasDueButNotApproved { get; set; }
    public bool HasOverduePeriod { get; set; }
    public bool AreAllPeriodsApprovedWithinScope { get; set; }

    public string? LatestPeriodKey { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }
}

public sealed class ProgressComputeResult
{
    public int ProgressStatus { get; set; }
    public bool HasAnyDuePeriod { get; set; }
    public bool HasOverduePeriod { get; set; }
    public string? LatestPeriodKey { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }
}

public sealed class ProgressRecomputeResult
{
    public string WorkAssignmentId { get; set; } = default!;
    public int OldStatus { get; set; }
    public int NewStatus { get; set; }
    public bool Changed { get; set; }

    public bool HasAnyDuePeriod { get; set; }
    public bool HasOverduePeriod { get; set; }
    public string? LatestPeriodKey { get; set; }
    public DateTime? LatestDueAtUtc { get; set; }
}
