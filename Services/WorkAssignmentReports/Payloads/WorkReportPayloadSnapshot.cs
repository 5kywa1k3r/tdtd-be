namespace tdtd_be.Services.WorkAssignmentReports.Payloads;

public sealed record WorkReportPayloadSnapshot(
    string Values1DJson,
    string? FieldValuesJson,
    string? TableValuesJson,
    string? SummarySourceJson,
    int PayloadRevision,
    string? PayloadHash,
    long PayloadSizeBytes,
    string? PayloadStatus,
    bool IsExternalPayload,
    bool PayloadHashVerified);
