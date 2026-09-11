using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Services.StatisticsReconciliation.IndependentReview;

public static class StatisticReconciliationReviewGates
{
    public const string Form = "FORM";
    public const string Flow = "FLOW";
    public const string Assignment = "ASSIGNMENT";
    public const string Mapping = "MAPPING";
    public const string Statistics = "STATISTICS";

    public static readonly IReadOnlyList<string> All =
        [Form, Flow, Assignment, Mapping, Statistics];
}

public static class StatisticReconciliationReviewDecisions
{
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
}

public static class StatisticReconciliationReviewRecordKinds
{
    public const string Decision = "REVIEW_DECISION";
    public const string Supersession = "REVIEW_SUPERSESSION";
}

public static class StatisticReconciliationReviewStatuses
{
    public const string Active = "ACTIVE";
    public const string Superseded = "SUPERSEDED";
}

public static class StatisticReconciliationIndependentReviewFailureCodes
{
    public const string PermissionDenied = "P10_REVIEW_PERMISSION_DENIED";
    public const string EvidenceInvalid = "P10_REVIEW_EVIDENCE_INVALID";
    public const string SeparationOfDuties = "P10_REVIEW_SEPARATION_OF_DUTIES";
    public const string TargetNotSignable = "P10_REVIEW_TARGET_NOT_SIGNABLE";
    public const string ReplayMismatch = "P10_REVIEW_REPLAY_MISMATCH";
    public const string DuplicateGate = "P10_REVIEW_DUPLICATE_GATE";
    public const string StaleCas = "P10_REVIEW_STALE_CAS";
    public const string PersistenceInvalid = "P10_REVIEW_PERSISTENCE_INVALID";
}

public sealed class StatisticReconciliationIndependentReviewException : Exception
{
    public StatisticReconciliationIndependentReviewException(string code, string detail)
        : base($"{code}:{detail}") => Code = code;

    public string Code { get; }
}

public sealed record StatisticReconciliationEightColumnRecord(
    string Identity,
    string Config,
    string Expected,
    string Actual,
    string Delta,
    string Freshness,
    string Permission,
    string Verdict)
{
    public IReadOnlyList<KeyValuePair<string, string>> Columns =>
    [
        new("Identity", Identity),
        new("Config", Config),
        new("Expected", Expected),
        new("Actual", Actual),
        new("Delta", Delta),
        new("Freshness", Freshness),
        new("Permission", Permission),
        new("Verdict", Verdict)
    ];
}

public sealed record StatisticReconciliationReviewGeneration(
    string ReconciliationId,
    string GenerationId,
    string GenerationSha256,
    string SemanticVerdictSha256,
    StatisticReconciliationEightColumnRecord ReviewRecord,
    string ReviewRecordSha256,
    string Verdict,
    bool CompleteEvidence,
    bool CoherentSnapshot,
    bool UnknownRootCause,
    string RunInitiatorActorId,
    string LatestGenerationWriterActorId,
    long StateRevision);

public sealed record StatisticReconciliationReviewPermission(
    bool ServerDerived,
    bool Authenticated,
    bool ScopeAuthorized,
    bool CanReview,
    bool CanViewOperatorDetail,
    string ActorId,
    string PermissionSnapshotSha256,
    int RowCountBeforeRedaction,
    int RowCountAfterRedaction);

public sealed record StatisticReconciliationReviewCommand(
    string CommandId,
    string RequestSha256,
    string ReconciliationId,
    string GenerationId,
    string GenerationSha256,
    string SemanticVerdictSha256,
    string Gate,
    string Decision,
    long ExpectedStateRevision);

public sealed record StatisticReconciliationReviewSupersessionCommand(
    string CommandId,
    string RequestSha256,
    string ReconciliationId,
    string PreviousGenerationId,
    string NewGenerationId,
    string NewGenerationSha256,
    long ExpectedStateRevision);

[BsonIgnoreExtraElements]
public sealed record StatisticReconciliationIndependentReviewAuditRecord(
    [property: BsonId] string Id,
    [property: BsonElement("recordKind")] string RecordKind,
    [property: BsonElement("status")] string Status,
    [property: BsonElement("operationCommandId")] string OperationCommandId,
    [property: BsonElement("requestSha256")] string RequestSha256,
    [property: BsonElement("reconciliationId")] string ReconciliationId,
    [property: BsonElement("generationId")] string GenerationId,
    [property: BsonElement("generationSha256")] string GenerationSha256,
    [property: BsonElement("semanticVerdictSha256")] string SemanticVerdictSha256,
    [property: BsonElement("reviewRecordSha256")] string ReviewRecordSha256,
    [property: BsonElement("gate")] string Gate,
    [property: BsonElement("decision")] string? Decision,
    [property: BsonElement("reviewerActorId")] string ReviewerActorId,
    [property: BsonElement("permissionSnapshotSha256")] string PermissionSnapshotSha256,
    [property: BsonElement("rowCountBeforeRedaction")] int RowCountBeforeRedaction,
    [property: BsonElement("rowCountAfterRedaction")] int RowCountAfterRedaction,
    [property: BsonElement("stateRevision")] long StateRevision,
    [property: BsonElement("supersedesDecisionId")] string? SupersedesDecisionId,
    [property: BsonElement("supersededByGenerationId")] string? SupersededByGenerationId,
    [property: BsonElement("documentSha256")] string DocumentSha256,
    [property: BsonElement("createdAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    DateTime CreatedAtUtc);

public sealed record StatisticReconciliationReviewSubmissionResult(
    bool Replayed,
    StatisticReconciliationIndependentReviewAuditRecord Decision);

public sealed record StatisticReconciliationReviewFinalApproval(
    string ReconciliationId,
    string GenerationId,
    bool Approved,
    int ApprovedGateCount,
    string FinalApprovalSha256);

public sealed record StatisticReconciliationReviewBusinessSummaryDto(
    string ReconciliationId,
    string GenerationId,
    bool Approved,
    int ApprovedGateCount,
    int RejectedGateCount,
    IReadOnlyDictionary<string, string> GateStates);

public sealed record StatisticReconciliationReviewOperatorDetailDto(
    StatisticReconciliationReviewBusinessSummaryDto Summary,
    IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord> AuditRecords);

public sealed record StatisticReconciliationReviewActionsDto(
    bool CanSubmit,
    long ExpectedStateRevision,
    IReadOnlyList<string> AvailableGates)
{
    public bool CanSupersede { get; init; }
    public IReadOnlyList<string> SupersessionGenerationIds { get; init; } = [];
}

public sealed record StatisticReconciliationReviewReadResult(
    StatisticReconciliationReviewBusinessSummaryDto Summary,
    StatisticReconciliationReviewOperatorDetailDto? OperatorDetail)
{
    public StatisticReconciliationReviewActionsDto Actions { get; init; } =
        new(false, 0, []);
}

public enum StatisticReconciliationReviewAppendOutcome
{
    Appended,
    DuplicateId,
    DuplicateGate,
    StateFenceLost
}

public interface IStatisticReconciliationIndependentReviewBackend
{
    Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>> ReadLineageAsync(
        string reconciliationId,
        CancellationToken ct = default);

    Task<IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord>> ReadOperationAsync(
        string operationCommandId,
        CancellationToken ct = default);

    Task<StatisticReconciliationReviewAppendOutcome> TryAppendDecisionAsync(
        StatisticReconciliationIndependentReviewAuditRecord record,
        CancellationToken ct = default);

    Task<StatisticReconciliationReviewAppendOutcome> TryAppendSupersessionsAsync(
        IReadOnlyList<StatisticReconciliationIndependentReviewAuditRecord> records,
        CancellationToken ct = default);
}
