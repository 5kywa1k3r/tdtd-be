using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualCrossViewParityV3Schemas
{
    internal const string Plan = "P10_ACTUAL_CROSS_VIEW_PARITY_PLAN_V3";
    internal const string Proof = "P10_ACTUAL_CROSS_VIEW_PARITY_PROOF_V3";
    internal const string AuthorizationRelation =
        "P10_CROSS_VIEW_AUTHORIZATION_RELATION_V1";
}

internal sealed record
    StatisticReconciliationActualCrossViewAuthorizationRelationV1(
        string SchemaVersion,
        string WorkId,
        string ScopeAssignmentId,
        string? ApiAuthorizationSnapshotSha256,
        string ExportAuthorizationSnapshotSha256,
        string? ApiActorUserId,
        ImmutableArray<string> ApiPermissionCodes,
        long? ApiRowCountBeforeRedaction,
        long? ApiRowCountAfterRedaction,
        string ExportRequestedByUserId,
        string ExportAuthorizationPolicy,
        string ExportCapabilityId,
        string ExportCommandId,
        string ExportReceiptId,
        string ExportOwnerSemanticSha256,
        string SemanticSha256);

internal sealed record StatisticReconciliationActualCrossViewParityV3Plan(
    string SchemaVersion,
    string Family,
    string ApiApplicability,
    string? ApiSurface,
    string ExportResultKind,
    bool ViewsShareOrderedRows,
    string WorkId,
    string ScopeType,
    string ScopeId,
    string DynamicFormTemplateId,
    string PeriodInstanceKey,
    string? ApiAuthorizationSnapshotSha256,
    string ExportAuthorizationSnapshotSha256,
    string AuthorizationRelationSha256,
    string? ApiOwnerResultId,
    string? ApiGenerationId,
    string? ApiGenerationSha256,
    string? DirectPublicationGenerationSha256,
    long? DirectSourceRevision,
    long? DirectPublicationRevision,
    StatisticReconciliationActualCrossViewBasicGenerationPreimage?
        BasicGeneration,
    string? CanonicalApiFilterJson,
    string? ApiFilterSha256,
    long ApiExpectedTotalRows,
    int ApiPageSize,
    int ApiPageCount,
    string ExportId,
    string ExportResultId,
    string ExportResultSha256,
    string ExportConfigSha256,
    string ExportSourceSha256,
    int ExportLifecycleRevision,
    string ExportRequestSha256,
    string ExportContentSha256,
    string ExportColumnManifestSha256,
    string ExportOwnerSemanticSha256,
    string CanonicalExportFilterJson,
    string ExportFilterSha256);

internal sealed record StatisticReconciliationActualCrossViewParityV3Proof(
    string SchemaVersion,
    bool Complete,
    string FailureCode,
    string AuthorizationRelationSha256,
    string CompatibilityProofSha256,
    string OriginalApiSemanticSha256,
    string OriginalExportSemanticSha256,
    string ProofSha256);
