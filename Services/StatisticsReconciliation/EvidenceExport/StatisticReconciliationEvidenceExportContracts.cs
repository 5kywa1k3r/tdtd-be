using System.Collections.Immutable;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

namespace tdtd_be.Services.StatisticsReconciliation.EvidenceExport;

public static class StatisticReconciliationEvidenceFormats
{
    public const string Json = "JSON";
    public const string Csv = "CSV";
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([Json, Csv], StringComparer.Ordinal);
}

public static class StatisticReconciliationEvidenceDetailLevels
{
    public const string Redacted = "REDACTED";
    public const string Operator = "OPERATOR";
}

public static class StatisticReconciliationEvidenceFailureCodes
{
    public const string PermissionDenied = "P10_EVIDENCE_PERMISSION_DENIED";
    public const string InvalidRequest = "P10_EVIDENCE_REQUEST_INVALID";
    public const string TargetNotExportable = "P10_EVIDENCE_TARGET_NOT_EXPORTABLE";
    public const string ScopeLimit = "P10_EVIDENCE_SCOPE_LIMIT";
    public const string Expired = "P10_EVIDENCE_EXPIRED";
    public const string Drift = "P10_EVIDENCE_DRIFT";
}

public sealed class StatisticReconciliationEvidenceException(string code, string detail)
    : Exception($"{code}:{detail}")
{
    public string Code { get; } = code;
}

public sealed record StatisticReconciliationEvidenceCell(
    string ValueType,
    string ValueState,
    string CanonicalJson);

public sealed record StatisticReconciliationEvidenceRow(
    string Identity,
    string Config,
    StatisticReconciliationEvidenceCell Expected,
    StatisticReconciliationEvidenceCell Actual,
    StatisticReconciliationEvidenceCell Delta,
    string Freshness,
    string Permission,
    string Verdict,
    ImmutableArray<string> SourceStableIds);

public sealed record StatisticReconciliationEvidenceSnapshot(
    string WorkId,
    string ScopeAssignmentId,
    string ReconciliationId,
    string GenerationId,
    string GenerationSha256,
    string SemanticVerdictSha256,
    string ReviewSignatureSha256,
    string FinalApprovalSha256,
    DateTime SnapshotAtUtc,
    ImmutableArray<StatisticReconciliationEvidenceRow> Rows);

public sealed record StatisticReconciliationEvidenceCompileCommand(
    string CommandId,
    string Format,
    bool IncludeOperatorDetail,
    string ActorId,
    string PermissionSnapshotSha256,
    bool CanViewOperatorDetail,
    DateTime RequestedAtUtc,
    TimeSpan Retention);

public sealed record StatisticReconciliationEvidenceArtifactDto(
    string Id,
    string ReconciliationId,
    string GenerationId,
    string Format,
    string DetailLevel,
    string FileName,
    string ContentType,
    string ManifestSha256,
    string ContentSha256,
    long ContentLength,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc)
{
    public IReadOnlyList<StatisticReconciliationEvidenceArtifactLinkDto> Links
        { get; init; } = [];
}

public sealed record StatisticReconciliationEvidenceArtifactLinkDto(
    string Rel,
    string Href,
    string Method);

public sealed record StatisticReconciliationEvidenceArtifactPageDto(
    IReadOnlyList<StatisticReconciliationEvidenceArtifactDto> Rows,
    long Total,
    int Page,
    int PageSize);

public sealed record StatisticReconciliationEvidenceDownload(
    StatisticReconciliationEvidenceArtifactDto Artifact,
    byte[] Content);

public interface IStatisticReconciliationEvidenceStore
{
    Task<StatisticReconciliationEvidenceExport?> FindByIdAsync(string id,
        CancellationToken ct = default);
    Task<StatisticReconciliationEvidenceExport?> FindByCommandAsync(
        string workId, string scopeAssignmentId, string reconciliationId,
        string commandId, CancellationToken ct = default);
    Task<(IReadOnlyList<StatisticReconciliationEvidenceExport> Rows, long Total)>
        ListReadableAsync(
            string workId, string scopeAssignmentId, string reconciliationId,
            string permissionSnapshotSha256, DateTime nowUtc,
            int page, int pageSize, CancellationToken ct = default);
    Task<bool> TryAppendAsync(StatisticReconciliationEvidenceExport artifact,
        CancellationToken ct = default);
    Task<long> CleanupExpiredAsync(string workId, string scopeAssignmentId,
        string reconciliationId, DateTime nowUtc, CancellationToken ct = default);
}

public interface IStatisticReconciliationEvidenceOwner
{
    Task<StatisticReconciliationReviewScopeAuthorization?> AuthorizeScopeAsync(
        string? workId, string? scopeAssignmentId,
        tdtd_be.DTOs.Auth.MeResponse actor, CancellationToken ct = default);
    Task<StatisticReconciliationEvidenceArtifactDto> CreateAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId,
        StatisticReconciliationEvidenceCompileCommand command,
        CancellationToken ct = default);
    Task<StatisticReconciliationEvidenceArtifactPageDto> ListAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId, int page, int pageSize,
        CancellationToken ct = default);
    Task<StatisticReconciliationEvidenceArtifactDto> ReadAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId, string exportId,
        CancellationToken ct = default);
    Task<StatisticReconciliationEvidenceDownload> DownloadAsync(
        StatisticReconciliationReviewScopeAuthorization authorization,
        string reconciliationId, string exportId, CancellationToken ct = default);
}
