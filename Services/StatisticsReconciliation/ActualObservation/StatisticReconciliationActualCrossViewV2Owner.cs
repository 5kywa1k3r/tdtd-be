using System.Collections.Immutable;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Production read-only owner for the V2 parity input. V2 can sign an
/// export-only Advanced relation. API-bearing families intentionally remain
/// incomplete until their distinct API/export authorization preimages are
/// persisted and related by a later schema; opaque digest coincidence is not
/// treated as evidence.
/// </summary>
internal sealed class StatisticReconciliationActualCrossViewV2Owner(
    IStatisticReconciliationActualAdvancedOwnerReader advancedOwner,
    IStatisticReconciliationActualExportOwnerReader exportOwner)
    : IStatisticReconciliationActualCrossViewV2Owner
{
    private readonly StatisticReconciliationActualExportParser _export = new();

    public async Task<
        StatisticReconciliationActualCrossViewV2OwnerResolution> ResolveAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            CancellationToken cancellationToken = default)
    {
        if (command is null || command.Material is null)
            return Incomplete(
                StatisticReconciliationActualCrossViewV2OwnerFailures
                    .InputRequired,
                []);
        if (!string.Equals(
                command.SchemaVersion,
                StatisticReconciliationActualCrossViewV2OwnerSchemas.Command,
                StringComparison.Ordinal))
        {
            return Incomplete(
                StatisticReconciliationActualCrossViewV2OwnerFailures
                    .SchemaUnsupported,
                []);
        }

        var resultKind = command.Material.Export?.ResultKind;
        var family = Family(resultKind);
        if (family is null)
            return Incomplete(
                StatisticReconciliationActualCrossViewV2OwnerFailures
                    .FamilyUnsupported,
                []);

        if (family != StatisticReconciliationActualCrossViewFamilies.Advanced)
        {
            // API authorization binds actor/scope/redaction rows under
            // P10_ACTUAL_API_AUTHORIZATION_V1. Export binds actor/tenant/scope/
            // policy under P10_RECONCILIATION_AUTHORIZATION_V1. V2 has one
            // scalar and cannot prove these values are isomorphic.
            return Incomplete(
                StatisticReconciliationActualCrossViewV2OwnerFailures
                    .AuthorizationRelationUnavailable,
                StatisticReconciliationActualCrossViewV2RequiredPersistence
                    .AuthorizationRelation);
        }

        if (command.Advanced is null)
            return Incomplete(
                StatisticReconciliationActualCrossViewV2OwnerFailures
                    .AdvancedCaptureRequired,
                StatisticReconciliationActualCrossViewV2RequiredPersistence
                    .AdvancedCapture);
        if (command.Export is null)
            return Incomplete(
                StatisticReconciliationActualCrossViewV2OwnerFailures
                    .ExportCaptureRequired,
                StatisticReconciliationActualCrossViewV2RequiredPersistence
                    .ExportCapture);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawNode = await ReadAdvancedNodeAsync(
                    command.Material.Advanced,
                    command.Material.Export.ResultId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (rawNode is null)
                return Incomplete(
                    StatisticReconciliationActualCrossViewV2OwnerFailures
                        .AdvancedOwnerUnavailable,
                    StatisticReconciliationActualCrossViewV2RequiredPersistence
                        .AdvancedOwner);
            if (!AdvancedMatches(
                    command.Material.Advanced,
                    command.Advanced,
                    rawNode))
            {
                return Incomplete(
                    StatisticReconciliationActualCrossViewV2OwnerFailures
                        .AdvancedOwnerDrift,
                    StatisticReconciliationActualCrossViewV2RequiredPersistence
                        .AdvancedOwner);
            }

            var exportRead = await exportOwner.ReadArtifactAsync(
                    command.Material.Export,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exportRead is null ||
                exportRead.State !=
                    StatisticReconciliationActualExportOwnerReadStates.Ready ||
                exportRead.Artifact is null)
            {
                return Incomplete(
                    StatisticReconciliationActualCrossViewV2OwnerFailures
                        .ExportOwnerUnavailable,
                    StatisticReconciliationActualCrossViewV2RequiredPersistence
                        .ExportOwner);
            }
            var artifact = exportRead.Artifact;
            var parsedExport = _export.Parse(artifact);
            if (!ExportMatches(
                    command.Material.Export,
                    command.Export,
                    parsedExport,
                    artifact.Manifest,
                    rawNode))
            {
                return Incomplete(
                    StatisticReconciliationActualCrossViewV2OwnerFailures
                        .ExportOwnerDrift,
                    StatisticReconciliationActualCrossViewV2RequiredPersistence
                        .ExportOwner);
            }

            var projected =
                StatisticReconciliationActualCrossViewV2ExportBaseProjector
                    .ProjectJson(rawNode.ValueJson, artifact.Manifest);
            var exportBase =
                StatisticReconciliationActualCrossViewV2ExportBaseProjector
                    .BindTemplate(
                        projected.Base,
                        command.Material.Advanced.DynamicFormTemplateId);
            if (!string.Equals(
                    projected.ColumnManifestSha256,
                    command.Material.Export.ExpectedColumnManifestSha256,
                    StringComparison.Ordinal))
            {
                return Incomplete(
                    StatisticReconciliationActualCrossViewV2OwnerFailures
                        .ExportProjectionInvalid,
                    StatisticReconciliationActualCrossViewV2RequiredPersistence
                        .ExportOwner);
            }

            var plan = Plan(command.Material, artifact.Manifest);
            var baseline = new
                StatisticReconciliationActualCrossViewParityV2Base(
                    StatisticReconciliationActualCrossViewParityV2Schemas.Base,
                    null,
                    exportBase);
            var actual = new
                StatisticReconciliationActualCrossViewParityV2Actual(
                    StatisticReconciliationActualCrossViewParityV2Schemas.Actual,
                    null,
                    artifact.Manifest,
                    parsedExport);
            var proof = StatisticReconciliationActualCrossViewParityV2.Prove(
                plan,
                baseline,
                actual);
            if (!proof.Complete)
            {
                return Incomplete(
                    StatisticReconciliationActualCrossViewV2OwnerFailures
                        .ProofIncomplete,
                    [$"crossViewProof.failure:{proof.FailureCode}"]);
            }
            return Complete(plan, baseline, actual, proof.ProofSha256);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is
            StatisticReconciliationActualObservationException or
            InvalidOperationException or ArgumentException or
            FormatException or OverflowException or
            System.Text.Json.JsonException)
        {
            return Incomplete(
                StatisticReconciliationActualCrossViewV2OwnerFailures
                    .OwnerReadInvalid,
                StatisticReconciliationActualCrossViewV2RequiredPersistence
                    .ExportOwner);
        }
    }

    private async Task<WorkAssignmentAdvancedSummaryHierarchyNodeBase?>
        ReadAdvancedNodeAsync(
            ActualAdvancedOwnerBoundary boundary,
            string resultId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(advancedOwner);
        var days = await advancedOwner.ReadDayNodesAsync(
                boundary,
                cancellationToken)
            .ConfigureAwait(false);
        var months = await advancedOwner.ReadMonthNodesAsync(
                boundary,
                cancellationToken)
            .ConfigureAwait(false);
        var years = await advancedOwner.ReadYearNodesAsync(
                boundary,
                cancellationToken)
            .ConfigureAwait(false);
        if (days is null || months is null || years is null)
            return null;
        var matches = days.Cast<WorkAssignmentAdvancedSummaryHierarchyNodeBase>()
            .Concat(months)
            .Concat(years)
            .Where(node => string.Equals(
                node.Id,
                resultId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool AdvancedMatches(
        ActualAdvancedOwnerBoundary boundary,
        ActualAdvancedCapture capture,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node)
    {
        var rawSha = StatisticReconciliationActualJson.RawSha256(
            node.ValueJson ?? string.Empty);
        var observations = capture.Nodes.Where(value => string.Equals(
                value.OwnerNodeId,
                node.Id,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (observations.Length != 1)
            return false;
        var observed = observations[0];
        return string.Equals(
                   capture.Boundary.WorkId,
                   boundary.WorkId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   capture.Boundary.AssignmentId,
                   boundary.AssignmentId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   capture.Boundary.DynamicFormTemplateId,
                   boundary.DynamicFormTemplateId,
                   StringComparison.Ordinal) &&
               string.Equals(node.WorkId, boundary.WorkId,
                   StringComparison.Ordinal) &&
               string.Equals(node.AssignmentId, boundary.AssignmentId,
                   StringComparison.Ordinal) &&
               string.Equals(node.DynamicFormTemplateId,
                   boundary.DynamicFormTemplateId,
                   StringComparison.Ordinal) &&
               string.Equals(node.ConfigHash, boundary.ConfigSha256,
                   StringComparison.Ordinal) &&
               !node.IsDeleted && !node.IsDirty &&
               node.Status ==
                   WorkAssignmentAdvancedSummaryHierarchyNodeStatuses.Clean &&
               node.BuiltAtUtc?.Kind == DateTimeKind.Utc &&
               IsSha(node.ValueHash) &&
               string.Equals(node.ValueHash, rawSha, StringComparison.Ordinal) &&
               IsSha(node.SourceSignatureHash) &&
               string.Equals(observed.ValueJson, node.ValueJson,
                   StringComparison.Ordinal) &&
               string.Equals(observed.StoredValueSha256, node.ValueHash,
                   StringComparison.Ordinal) &&
               string.Equals(observed.ObservedValueSha256, rawSha,
                   StringComparison.Ordinal) &&
               string.Equals(observed.SourceSignatureSha256,
                   node.SourceSignatureHash,
                   StringComparison.Ordinal) &&
               observed.OwnerState.IsCleanResult;
    }

    private static bool ExportMatches(
        StatisticReconciliationActualExportOwnerTarget target,
        StatisticReconciliationActualExportCapture finalCapture,
        StatisticReconciliationActualExportCapture currentCapture,
        StatisticReconciliationActualExportManifest manifest,
        WorkAssignmentAdvancedSummaryHierarchyNodeBase node)
        => string.Equals(manifest.ResultKind, StatRunExportResultKinds.Advanced,
               StringComparison.Ordinal) &&
           string.Equals(manifest.ExportId, target.ExportId,
               StringComparison.Ordinal) &&
           string.Equals(manifest.WorkId, node.WorkId,
               StringComparison.Ordinal) &&
           string.Equals(manifest.ScopeId, node.AssignmentId,
               StringComparison.Ordinal) &&
           string.Equals(manifest.ResultId, node.Id,
               StringComparison.Ordinal) &&
           string.Equals(manifest.ResultSha256, node.ValueHash,
               StringComparison.Ordinal) &&
           string.Equals(manifest.ConfigSha256, node.ConfigHash,
               StringComparison.Ordinal) &&
           string.Equals(manifest.SourceSha256, node.SourceSignatureHash,
               StringComparison.Ordinal) &&
           manifest.LifecycleRevision == 0 &&
           string.Equals(finalCapture.CaptureSemanticSha256,
               currentCapture.CaptureSemanticSha256,
               StringComparison.Ordinal) &&
           string.Equals(finalCapture.ManifestSha256,
               currentCapture.ManifestSha256,
               StringComparison.Ordinal) &&
           string.Equals(finalCapture.ContentSha256,
               currentCapture.ContentSha256,
               StringComparison.Ordinal) &&
           string.Equals(finalCapture.PeriodInstanceKey,
               currentCapture.PeriodInstanceKey,
               StringComparison.Ordinal) &&
           string.Equals(finalCapture.CanonicalFilterJson,
               currentCapture.CanonicalFilterJson,
               StringComparison.Ordinal);

    private static StatisticReconciliationActualCrossViewParityV2Plan Plan(
        StatisticReconciliationActualTrustedCaptureMaterial material,
        StatisticReconciliationActualExportManifest manifest)
        => new(
            StatisticReconciliationActualCrossViewParityV2Schemas.Plan,
            StatisticReconciliationActualCrossViewFamilies.Advanced,
            StatisticReconciliationActualCrossViewApiApplicability
                .NoProductionApi,
            null,
            StatRunExportResultKinds.Advanced,
            false,
            manifest.WorkId,
            manifest.ScopeType,
            manifest.ScopeId,
            material.Advanced.DynamicFormTemplateId,
            Required(manifest.PeriodInstanceKey,
                "CROSS_VIEW_OWNER_PERIOD_INSTANCE_KEY"),
            manifest.AuthorizationSnapshotSha256,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            manifest.ExportId,
            manifest.ResultId,
            manifest.ResultSha256,
            manifest.ConfigSha256,
            manifest.SourceSha256,
            manifest.LifecycleRevision,
            manifest.RequestSha256,
            manifest.ContentSha256,
            material.Export.ExpectedColumnManifestSha256,
            manifest.OwnerSemanticSha256,
            Required(manifest.CanonicalFilterJson,
                "CROSS_VIEW_OWNER_FILTER_JSON"),
            manifest.FilterSha256);

    private static StatisticReconciliationActualCrossViewV2OwnerResolution
        Complete(
            StatisticReconciliationActualCrossViewParityV2Plan plan,
            StatisticReconciliationActualCrossViewParityV2Base baseline,
            StatisticReconciliationActualCrossViewParityV2Actual actual,
            string proofSha256)
    {
        var resolutionSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_V2_OWNER_RESOLUTION_V1",
            StatisticReconciliationActualCrossViewV2OwnerSchemas.Resolution,
            StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
            StatisticReconciliationActualCrossViewV2OwnerFailures.None,
            proofSha256);
        return new(
            StatisticReconciliationActualCrossViewV2OwnerSchemas.Resolution,
            StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
            StatisticReconciliationActualCrossViewV2OwnerFailures.None,
            [],
            plan,
            baseline,
            actual,
            resolutionSha);
    }

    private static StatisticReconciliationActualCrossViewV2OwnerResolution
        Incomplete(string failure, ImmutableArray<string> required)
    {
        if (required.IsDefault)
            required = [];
        var normalized = required.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        var resolutionSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_V2_OWNER_RESOLUTION_V1",
            StatisticReconciliationActualCrossViewV2OwnerSchemas.Resolution,
            StatisticReconciliationActualCrossViewV2OwnerStates.Incomplete,
            failure,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_CROSS_VIEW_V2_OWNER_REQUIRED_FIELDS_V1",
                normalized));
        return new(
            StatisticReconciliationActualCrossViewV2OwnerSchemas.Resolution,
            StatisticReconciliationActualCrossViewV2OwnerStates.Incomplete,
            failure,
            normalized,
            null,
            null,
            null,
            resolutionSha);
    }

    private static string? Family(string? resultKind)
        => resultKind switch
        {
            StatRunExportResultKinds.DirectField or
            StatRunExportResultKinds.DirectTable or
            StatRunExportResultKinds.DirectLabel =>
                StatisticReconciliationActualCrossViewFamilies.Direct,
            StatRunExportResultKinds.Basic =>
                StatisticReconciliationActualCrossViewFamilies.Basic,
            StatRunExportResultKinds.Flow =>
                StatisticReconciliationActualCrossViewFamilies.Flow,
            StatRunExportResultKinds.Advanced =>
                StatisticReconciliationActualCrossViewFamilies.Advanced,
            StatRunExportResultKinds.Diff =>
                StatisticReconciliationActualCrossViewFamilies.Diff,
            _ => null
        };

    private static bool IsSha(string? value)
        => value is not null && value.Length == 64 &&
           value.All(character => character is >= '0' and <= '9' or
               >= 'a' and <= 'f');

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
}
