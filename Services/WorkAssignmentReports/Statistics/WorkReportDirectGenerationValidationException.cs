namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

/// <summary>
/// Closed, producer-owned validation outcome for deterministic failures while
/// staging an immutable P9 Direct generation. Infrastructure and cancellation
/// exceptions must never be wrapped in this type.
/// </summary>
internal sealed class WorkReportDirectGenerationValidationException
    : InvalidOperationException
{
    internal const string RowConflictCode =
        "P9_DIRECT_GENERATION_ROW_CONFLICT";
    internal const string SourceDriftCode =
        "P9_DIRECT_GENERATION_SOURCE_DRIFT";
    internal const string PinConflictCode =
        "P9_DIRECT_GENERATION_PIN_CONFLICT";
    internal const string PolicyDriftCode =
        "P9_DIRECT_GENERATION_POLICY_DRIFT";
    internal const string PayloadNotReadyCode =
        "P9_DIRECT_GENERATION_PAYLOAD_NOT_READY";

    private WorkReportDirectGenerationValidationException(string diagnosticCode)
        : base(diagnosticCode)
        => DiagnosticCode = diagnosticCode;

    internal string DiagnosticCode { get; }

    internal static WorkReportDirectGenerationValidationException RowConflict()
        => new(RowConflictCode);

    internal static WorkReportDirectGenerationValidationException SourceDrift()
        => new(SourceDriftCode);

    internal static WorkReportDirectGenerationValidationException PinConflict()
        => new(PinConflictCode);

    internal static WorkReportDirectGenerationValidationException PolicyDrift()
        => new(PolicyDriftCode);
}