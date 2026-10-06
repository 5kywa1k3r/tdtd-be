using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Progress;

namespace tdtd_be.DTOs.WorkAssignments;

public sealed record CompletionRequestCommand(string CommandId, long? ExpectedRevision, string Reason);
public sealed record AssignmentEffectivenessCommand(string Reason);
public sealed record CompletionDecisionCommand(string CommandId, long? ExpectedRevision, bool Approve, string? Reason);
public sealed record CompletionReopenCommand(string CommandId, long? ExpectedRevision, string PeriodId, string Reason);
public sealed record WorkCompletionReopenCommand(string CommandId, long? ExpectedRevision, string? PeriodId, string Reason);
public sealed record AssignmentCompletionState(string AssignmentId, string WorkId, string? Name, long Revision,
    bool Completed, bool CanRequest, bool CanDecide, bool CanReopen, bool ProjectionPending,
    WorkExecutionFacts Progress, IReadOnlyList<WorkAssignmentCompletionRequest> History,
    IReadOnlyList<CompletionPeriodChoice> Periods);
public sealed record CompletionPeriodChoice(string Id, string PeriodKey, string AssigneeUserId, string? AssigneeLabel = null);
public sealed record WorkCompletionState(string WorkId, string Name, long Revision, bool Completed,
    bool CanReopen, bool ReopenHold, bool ProjectionPending, IReadOnlyList<WorkCompletionPeriodChoice> Periods,
    bool RequiresReportSelection);
public sealed record WorkCompletionPeriodChoice(string Id, string AssignmentId, string AssignmentName,
    string PeriodKey, string AssigneeLabel);
