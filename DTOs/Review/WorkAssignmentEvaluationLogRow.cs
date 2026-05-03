namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed record WorkAssignmentEvaluationLogRow(
    string Id,
    string WorkId,
    string WorkAssignmentId,
    string Action,
    string? FromEvaluationCode,
    string? FromEvaluationLabel,
    string? ToEvaluationCode,
    string? ToEvaluationLabel,
    string? Comment,
    string? Reason,
    string ActionByUserId,
    DateTime ActionAtUtc
);
