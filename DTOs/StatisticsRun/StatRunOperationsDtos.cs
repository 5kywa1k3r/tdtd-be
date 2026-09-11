using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.StatisticsRun;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunOperationCommandRequest
{
    public string CommandId { get; init; } = string.Empty;
    public long ExpectedStateRevision { get; init; }
    public string ExpectedStateHash { get; init; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunCleanupPreviewRequest
{
    public DateTime OlderThanUtc { get; init; }
    public int MaxJobs { get; init; } = 50;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StatRunCleanupApplyRequest
{
    public string CommandId { get; init; } = string.Empty;
    public DateTime OlderThanUtc { get; init; }
    public int MaxJobs { get; init; } = 50;
    public string ExpectedCandidateHash { get; init; } = string.Empty;
}

public sealed class StatRunOperationsJobResponse
{
    public string JobId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public string ReceiptId { get; init; } = string.Empty;
    public string CapabilityId { get; init; } = string.Empty;
    public string RunKind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long StateRevision { get; init; }
    public string StateHash { get; init; } = string.Empty;
    public bool IsReplay { get; init; }
    public string WorkId { get; init; } = string.Empty;
    public string ScopeType { get; init; } = string.Empty;
    public string? ScopeId { get; init; }
    public string? SourceReportId { get; init; }
    public int SourceRevision { get; init; }
    public int LifecycleRevision { get; init; }
    public int RetryCount { get; init; }
    public DateTime? NextRetryAtUtc { get; init; }
    public string FreshnessState { get; init; } = string.Empty;
    public string? StaleReason { get; init; }
    public string? DiagnosticCode { get; init; }
    public bool IsCurrentPublication { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class StatRunOperationsJobPageResponse
{
    public IReadOnlyList<StatRunOperationsJobResponse> Items { get; init; } = [];
    public int Count { get; init; }
    public int Limit { get; init; }
}

public sealed class StatRunOperationReceiptResponse
{
    public string ReceiptId { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public long AcceptedStateRevision { get; init; }
    public string AcceptedStateHash { get; init; } = string.Empty;
    public string AcceptedStatus { get; init; } = string.Empty;
    public DateTime AcceptedAtUtc { get; init; }
    public DateTime? AcceptedNextRetryAtUtc { get; init; }
    public bool IsReplay { get; init; }
}

public sealed class StatRunOperationMutationResponse
{
    public StatRunOperationsJobResponse Job { get; init; } = new();
    public StatRunOperationReceiptResponse Receipt { get; init; } = new();
}

public sealed class StatRunJobDiagnosticResponse
{
    public string JobId { get; init; } = string.Empty;
    public string RunKind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long StateRevision { get; init; }
    public string StateHash { get; init; } = string.Empty;
    public string StateIntegrity { get; init; } = string.Empty;
    public string HeaderIntegrity { get; init; } = string.Empty;
    public string ReceiptIntegrity { get; init; } = string.Empty;
    public string LeaseState { get; init; } = string.Empty;
    public int RetryCount { get; init; }
    public int MaxRetryCount { get; init; }
    public string? DiagnosticCode { get; init; }
    public bool IsCurrentPublication { get; init; }
    public long TotalReportCount { get; init; }
    public long ProcessedReportCount { get; init; }
    public long FailedReportCount { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class StatRunCleanupCandidateResponse
{
    public string JobId { get; init; } = string.Empty;
    public string RunKind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class StatRunCleanupPreviewResponse
{
    public DateTime OlderThanUtc { get; init; }
    public int MaxJobs { get; init; }
    public int ScannedCount { get; init; }
    public int ProtectedCurrentCount { get; init; }
    public int ProtectedReferencedCount { get; init; }
    public string CandidateHash { get; init; } = string.Empty;
    public IReadOnlyList<StatRunCleanupCandidateResponse> Candidates { get; init; } = [];
}

public sealed class StatRunCleanupApplyResponse
{
    public string CommandId { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public string CandidateHash { get; init; } = string.Empty;
    public int AppliedCount { get; init; }
    public IReadOnlyList<string> JobIds { get; init; } = [];
    public IReadOnlyList<string> ReceiptIds { get; init; } = [];
    public bool IsReplay { get; init; }
}
