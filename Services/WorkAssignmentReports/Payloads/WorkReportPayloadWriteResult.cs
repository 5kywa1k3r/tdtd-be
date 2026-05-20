namespace tdtd_be.Services.WorkAssignmentReports.Payloads;

public sealed record WorkReportPayloadWriteResult(
    int PayloadRevision,
    string PayloadHash,
    long PayloadSizeBytes,
    string PayloadStatus);
