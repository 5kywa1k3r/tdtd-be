namespace tdtd_be.DTOs.WorkAssignments.Review;

public sealed record EvaluateAssignmentRequest(
    string EvaluationCode,
    string? Comment,
    string? Reason
);
