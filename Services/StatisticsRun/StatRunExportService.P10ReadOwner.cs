using System.Collections.Immutable;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

namespace tdtd_be.Services.StatisticsRun;

/// <summary>
/// Trusted read-only P10 owner binding. It reads the immutable artifact and
/// its future-only column sidecar without invoking DownloadAsync or updating
/// download telemetry.
/// </summary>
public sealed partial class StatRunExportService :
    IStatisticReconciliationActualExportOwnerReader
{
    async Task<StatisticReconciliationActualExportOwnerRead>
        IStatisticReconciliationActualExportOwnerReader.ReadArtifactAsync(
            StatisticReconciliationActualExportOwnerTarget target,
            CancellationToken cancellationToken)
    {
        if (!ValidTarget(target))
            return Stale("EXPORT_TARGET_INVALID");

        var collection = ExportCollection(target.ResultKind);
        var artifact = await collection.Find(item =>
                item.Id == target.ExportId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
            return Stale("EXPORT_ARTIFACT_NOT_FOUND");
        StatRunCandidateBinding currentBinding;
        try
        {
            var capability = StatRunExportContract.CapabilityFor(
                artifact.ResultKind);
            currentBinding = _activation.RequireCapability(
                capability,
                StatRunExportContract.RouteForCapability(capability));
        }
        catch (AppException)
        {
            return Stale("EXPORT_CANDIDATE_ACTIVATION_INVALID");
        }
        if (!CandidateBindingMatches(currentBinding, artifact))
            return Stale("EXPORT_CANDIDATE_BINDING_DRIFT");
        if (string.IsNullOrWhiteSpace(artifact.CanonicalFilterJson) ||
            !string.Equals(
                StatRunCanonicalJson.HashText(artifact.CanonicalFilterJson),
                artifact.FilterHash,
                StringComparison.Ordinal))
            return Stale("EXPORT_FILTER_PREIMAGE_INVALID");
        if (!TargetMatches(target, artifact))
            return Stale("EXPORT_TARGET_MISMATCH");
        if (!string.Equals(
                artifact.Status,
                StatRunExportStatuses.Completed,
                StringComparison.Ordinal) ||
            artifact.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return Stale("EXPORT_ARTIFACT_NOT_CURRENT");
        }
        if (!string.Equals(
                artifact.SchemaVersion,
                StatisticReconciliationActualExportParser.RequiredSchemaVersion,
                StringComparison.Ordinal) ||
            artifact.ByteCount < 0 ||
            artifact.ByteCount > StatisticReconciliationActualExportParser.MaxBytes ||
            artifact.RowCount < 0 ||
            artifact.RowCount > StatisticReconciliationActualExportParser.MaxRows ||
            artifact.ColumnCount <= 0 ||
            artifact.ColumnCount > StatisticReconciliationActualExportParser.MaxColumns)
        {
            return Stale("EXPORT_ARTIFACT_BOUNDS_INVALID");
        }

        StatRunExportColumnManifestSidecar sidecar;
        try
        {
            sidecar = StatRunExportColumnManifestContract.Parse(
                artifact.ColumnManifestJson,
                artifact.ColumnManifestSha256);
        }
        catch (InvalidOperationException exception)
        {
            return Stale(exception.Message);
        }
        if (sidecar.Manifest.Columns.Count != artifact.ColumnCount)
            return Stale("EXPORT_COLUMN_MANIFEST_COUNT_MISMATCH");

        var recomputedOwnerSemanticSha256 =
            StatRunExportColumnManifestContract.ComputeOwnerSemanticSha256(
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
        if (!string.Equals(
                artifact.SemanticHash,
                recomputedOwnerSemanticSha256,
                StringComparison.Ordinal))
        {
            return Stale("EXPORT_OWNER_SEMANTIC_DIGEST_MISMATCH");
        }

        byte[] content;
        try
        {
            content = await ReadArtifactGuardedAsync(
                    artifact.StorageKey,
                    artifact.ContentHash,
                    artifact.ByteCount,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AppException)
        {
            return Stale("EXPORT_ARTIFACT_INTEGRITY_FAILED");
        }

        var columns = sidecar.Manifest.Columns
            .Select(column =>
                new StatisticReconciliationActualExportColumnContract(
                    column.Ordinal,
                    column.Name,
                    column.ValueType,
                    column.BlankPolicy,
                    column.IsFullFilterTotal))
            .ToImmutableArray();
        var manifest = new StatisticReconciliationActualExportManifest(
            artifact.SchemaVersion,
            artifact.Id,
            artifact.RequestHash,
            artifact.AuthorizationSnapshotHash,
            artifact.Format,
            artifact.ResultKind,
            artifact.ContentType,
            artifact.FileName,
            artifact.ContentHash,
            artifact.ByteCount,
            artifact.RowCount,
            artifact.ColumnCount,
            artifact.WorkId,
            artifact.ScopeType,
            artifact.ScopeId,
            artifact.ResultId,
            artifact.ResultHash,
            artifact.ConfigHash,
            artifact.SourceHash,
            artifact.FilterHash,
            artifact.LifecycleRevision,
            artifact.CatalogVersion,
            artifact.CatalogRawSha256,
            artifact.CatalogSemanticSha256,
            artifact.StageLockSha256,
            artifact.CandidateChainId,
            artifact.CandidatePromptId,
            artifact.CandidateStage,
            artifact.SemanticHash,
            artifact.CompletedAtUtc,
            columns,
            artifact.PeriodInstanceKey,
            artifact.CanonicalFilterJson);
        var parser = new StatisticReconciliationActualExportParser();
        var mapped = new StatisticReconciliationActualExportArtifact(
            manifest,
            parser.ComputeManifestSha256(manifest),
            content);
        try
        {
            _ = parser.Parse(mapped);
        }
        catch (StatisticReconciliationActualObservationException)
        {
            return Stale("EXPORT_T23_PARSE_FAILED");
        }
        return StatisticReconciliationActualExportOwnerRead.Ready(mapped);
    }

    private static bool ValidTarget(
        StatisticReconciliationActualExportOwnerTarget? target)
        => target is not null &&
           Required(target.ExportId) &&
           StatRunExportResultKinds.All.Contains(target.ResultKind) &&
           Required(target.WorkId) &&
           Required(target.ScopeType) &&
           Required(target.ScopeId) &&
           Required(target.ResultId) &&
           StatRunCanonicalJson.IsCanonicalSha256(
               target.ExpectedSourceOwnerSha256) &&
           StatRunCanonicalJson.IsCanonicalSha256(
               target.ExpectedConfigOwnerSha256) &&
           StatRunCanonicalJson.IsCanonicalSha256(target.ExpectedRequestSha256) &&
           StatRunCanonicalJson.IsCanonicalSha256(
               target.ExpectedAuthorizationSnapshotSha256) &&
           StatRunCanonicalJson.IsCanonicalSha256(target.ExpectedContentSha256) &&
           StatRunCanonicalJson.IsCanonicalSha256(
               target.ExpectedColumnManifestSha256) &&
           StatRunCanonicalJson.IsCanonicalSha256(
               target.ExpectedOwnerSemanticSha256) &&
           Required(target.ExpectedPeriodInstanceKey) &&
           StatRunCanonicalJson.IsCanonicalSha256(
               target.ExpectedFilterSha256);

    private static bool TargetMatches(
        StatisticReconciliationActualExportOwnerTarget target,
        StatRunExportArtifact artifact)
        => Eq(target.ExportId, artifact.Id) &&
           Eq(target.ResultKind, artifact.ResultKind) &&
           Eq(target.WorkId, artifact.WorkId) &&
           Eq(target.ScopeType, artifact.ScopeType) &&
           Eq(target.ScopeId, artifact.ScopeId) &&
           Eq(target.ResultId, artifact.ResultId) &&
           Eq(target.ExpectedPeriodInstanceKey, artifact.PeriodInstanceKey) &&
           Eq(target.ExpectedFilterSha256, artifact.FilterHash) &&
           Eq(target.ExpectedSourceOwnerSha256, artifact.SourceHash) &&
           Eq(target.ExpectedConfigOwnerSha256, artifact.ConfigHash) &&
           Eq(target.ExpectedRequestSha256, artifact.RequestHash) &&
           Eq(
               target.ExpectedAuthorizationSnapshotSha256,
               artifact.AuthorizationSnapshotHash) &&
           Eq(target.ExpectedContentSha256, artifact.ContentHash) &&
           Eq(
               target.ExpectedColumnManifestSha256,
               artifact.ColumnManifestSha256) &&
           Eq(target.ExpectedOwnerSemanticSha256, artifact.SemanticHash);

    private static bool CandidateBindingMatches(
        StatRunCandidateBinding binding,
        StatRunExportArtifact artifact)
        => Eq(binding.ChainId, artifact.CandidateChainId) &&
           Eq(binding.PromptId, artifact.CandidatePromptId) &&
           binding.Stage == artifact.CandidateStage &&
           Eq(binding.CatalogVersion, artifact.CatalogVersion) &&
           Eq(binding.CatalogRawSha256, artifact.CatalogRawSha256) &&
           Eq(binding.CatalogSemanticSha256,
               artifact.CatalogSemanticSha256) &&
           Eq(binding.StageLockSha256, artifact.StageLockSha256);

    private static bool Required(string? value)
        => !string.IsNullOrWhiteSpace(value) && value == value.Trim();

    private static bool Eq(string? left, string? right)
        => string.Equals(left, right, StringComparison.Ordinal);

    private static StatisticReconciliationActualExportOwnerRead Stale(
        string reason)
        => StatisticReconciliationActualExportOwnerRead.Stale(reason);
}
