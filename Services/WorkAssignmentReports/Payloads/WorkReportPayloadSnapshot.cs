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
    bool PayloadHashVerified)
{
    // Captured from the report accompanying this payload read. Missing stays missing.
    public DateTime? SourcePayloadUpdatedAtUtc { get; init; }
}
