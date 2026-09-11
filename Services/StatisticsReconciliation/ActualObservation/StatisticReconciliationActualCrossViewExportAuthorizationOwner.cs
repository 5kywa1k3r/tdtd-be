using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record
    StatisticReconciliationActualCrossViewExportAuthorizationBinding(
        string ExportId,
        string WorkId,
        string ScopeType,
        string ScopeId,
        string ResultKind,
        string RequestedByUserId,
        string AuthorizationSnapshotSha256,
        string AuthorizationPolicy,
        string CapabilityId,
        string CommandId,
        string RequestSha256,
        string ReceiptId,
        string OwnerSemanticSha256,
        string SemanticSha256);

internal sealed record
    StatisticReconciliationActualCrossViewExportAuthorizationRead(
        bool Ready,
        string? FailureCode,
        StatisticReconciliationActualCrossViewExportAuthorizationBinding?
            Binding)
{
    internal static StatisticReconciliationActualCrossViewExportAuthorizationRead
        Success(
            StatisticReconciliationActualCrossViewExportAuthorizationBinding
                binding)
        => new(true, null, binding);

    internal static StatisticReconciliationActualCrossViewExportAuthorizationRead
        Stale(string failure)
        => new(false, failure, null);
}

internal interface
    IStatisticReconciliationActualCrossViewExportAuthorizationOwner
{
    Task<StatisticReconciliationActualCrossViewExportAuthorizationRead>
        ReadAsync(
            StatisticReconciliationActualExportOwnerTarget target,
            CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads only immutable export authorization/receipt metadata. Artifact
/// content remains owned by IStatisticReconciliationActualExportOwnerReader.
/// </summary>
internal sealed class
    StatisticReconciliationActualMongoCrossViewExportAuthorizationOwner(
        MongoDbContext context)
    : IStatisticReconciliationActualCrossViewExportAuthorizationOwner
{
    public async Task<
        StatisticReconciliationActualCrossViewExportAuthorizationRead>
        ReadAsync(
            StatisticReconciliationActualExportOwnerTarget target,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        var collection = target.ResultKind == StatRunExportResultKinds.Diff
            ? context.WorkReportStatisticDiffExports
            : context.WorkReportStatisticExports;
        var artifact = await collection.Find(value =>
                value.Id == target.ExportId && !value.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
            return Stale("CROSS_VIEW_EXPORT_AUTH_NOT_FOUND");
        try
        {
            if (!TargetMatches(target, artifact) ||
                artifact.Status != StatRunExportStatuses.Completed ||
                artifact.ExpiresAtUtc <= DateTime.UtcNow ||
                artifact.AuthorizationPolicy !=
                    StatisticReconciliationActualCrossViewAuthorization
                        .ExportPolicy ||
                artifact.CapabilityId !=
                    StatRunExportContract.CapabilityFor(artifact.ResultKind))
                return Stale("CROSS_VIEW_EXPORT_AUTH_TARGET_MISMATCH");

            var sidecar = StatRunExportColumnManifestContract.Parse(
                artifact.ColumnManifestJson,
                artifact.ColumnManifestSha256);
            var ownerSemantic = StatRunExportColumnManifestContract
                .ComputeOwnerSemanticSha256(
                    artifact.ResultKind,
                    artifact.WorkId,
                    artifact.ScopeType,
                    artifact.ScopeId,
                    artifact.ResultId,
                    artifact.ResultHash,
                    artifact.ConfigHash,
                    artifact.SourceHash,
                    artifact.FilterHash,
                    artifact.LifecycleRevision,
                    artifact.RowCount,
                    artifact.ColumnCount,
                    sidecar.Sha256);
            if (artifact.SemanticHash != ownerSemantic)
                return Stale("CROSS_VIEW_EXPORT_AUTH_OWNER_DIGEST_MISMATCH");

            var receipt = StatRunCanonicalJson.HashObject(new
            {
                actorId = artifact.RequestedByUserId,
                commandId = artifact.CommandId,
                requestHash = artifact.RequestHash
            });
            var exportId = StatRunCanonicalJson.HashText(
                $"{artifact.RequestedByUserId}:{artifact.CommandId}")[..24];
            if (artifact.ReceiptId != receipt || artifact.Id != exportId)
                return Stale("CROSS_VIEW_EXPORT_AUTH_RECEIPT_INVALID");

            var bindingSha = StatisticReconciliationActualCanonical.Hash(
                "P10_CROSS_VIEW_EXPORT_AUTHORIZATION_BINDING_V1",
                artifact.Id,
                artifact.WorkId,
                artifact.ScopeType,
                artifact.ScopeId,
                artifact.ResultKind,
                Required(artifact.RequestedByUserId,
                    "CROSS_VIEW_EXPORT_REQUESTOR"),
                Sha(artifact.AuthorizationSnapshotHash,
                    "CROSS_VIEW_EXPORT_AUTH_SHA"),
                artifact.AuthorizationPolicy,
                artifact.CapabilityId,
                Required(artifact.CommandId, "CROSS_VIEW_EXPORT_COMMAND"),
                Sha(artifact.RequestHash, "CROSS_VIEW_EXPORT_REQUEST_SHA"),
                Sha(artifact.ReceiptId, "CROSS_VIEW_EXPORT_RECEIPT"),
                ownerSemantic);
            return StatisticReconciliationActualCrossViewExportAuthorizationRead
                .Success(new(
                    artifact.Id,
                    artifact.WorkId,
                    artifact.ScopeType,
                    artifact.ScopeId,
                    artifact.ResultKind,
                    artifact.RequestedByUserId,
                    artifact.AuthorizationSnapshotHash,
                    artifact.AuthorizationPolicy,
                    artifact.CapabilityId,
                    artifact.CommandId,
                    artifact.RequestHash,
                    artifact.ReceiptId,
                    ownerSemantic,
                    bindingSha));
        }
        catch (Exception error) when (error is InvalidOperationException or
                                     ArgumentException)
        {
            return Stale("CROSS_VIEW_EXPORT_AUTH_INVALID");
        }
    }

    private static bool TargetMatches(
        StatisticReconciliationActualExportOwnerTarget target,
        StatRunExportArtifact artifact)
        => target.ExportId == artifact.Id &&
           target.ResultKind == artifact.ResultKind &&
           target.WorkId == artifact.WorkId &&
           target.ScopeType == artifact.ScopeType &&
           target.ScopeId == artifact.ScopeId &&
           target.ResultId == artifact.ResultId &&
           target.ExpectedSourceOwnerSha256 == artifact.SourceHash &&
           target.ExpectedConfigOwnerSha256 == artifact.ConfigHash &&
           target.ExpectedRequestSha256 == artifact.RequestHash &&
           target.ExpectedAuthorizationSnapshotSha256 ==
               artifact.AuthorizationSnapshotHash &&
           target.ExpectedContentSha256 == artifact.ContentHash &&
           target.ExpectedColumnManifestSha256 ==
               artifact.ColumnManifestSha256 &&
           target.ExpectedOwnerSemanticSha256 == artifact.SemanticHash &&
           target.ExpectedPeriodInstanceKey == artifact.PeriodInstanceKey &&
           target.ExpectedFilterSha256 == artifact.FilterHash;

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static StatisticReconciliationActualCrossViewExportAuthorizationRead
        Stale(string failure)
        => StatisticReconciliationActualCrossViewExportAuthorizationRead.Stale(
            failure);
}
