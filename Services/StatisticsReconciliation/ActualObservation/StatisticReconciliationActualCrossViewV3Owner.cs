using System.Collections.Immutable;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualCrossViewV3Owner(
    StatisticReconciliationActualCrossViewV3DirectParityProjector direct,
    StatisticReconciliationActualCrossViewV3BasicProjector basic,
    StatisticReconciliationActualCrossViewV3AdvancedProjector advanced,
    StatisticReconciliationActualCrossViewV3DiffProjector diff,
    IStatisticReconciliationActualExportOwnerReader exportOwner,
    IStatisticReconciliationActualCrossViewExportAuthorizationOwner exportAuth)
    : IStatisticReconciliationActualCrossViewV3Owner
{
    private const string ResolutionSchema =
        StatisticReconciliationActualCrossViewV3OwnerSchemas.Resolution;
    private readonly StatisticReconciliationActualExportParser _export = new();
    private readonly StatisticReconciliationActualCrossViewParityV3 _proof =
        new();

    public async Task<
        StatisticReconciliationActualCrossViewV3OwnerResolution> ResolveAsync(
            StatisticReconciliationActualCrossViewV3OwnerCommand command,
            CancellationToken cancellationToken = default)
    {
        if (command is null || command.Material is null)
            return Incomplete("CROSS_VIEW_OWNER_INPUT_REQUIRED", []);
        if (command.SchemaVersion !=
            StatisticReconciliationActualCrossViewV3OwnerSchemas.Command)
            return Incomplete("CROSS_VIEW_OWNER_SCHEMA_UNSUPPORTED", []);
        var projectionCommand = command.ToProjectionCarrier();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exportRead = await exportOwner.ReadArtifactAsync(
                    command.Material.Export,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exportRead?.State !=
                    StatisticReconciliationActualExportOwnerReadStates.Ready ||
                exportRead.Artifact is null)
                return Incomplete(
                    "CROSS_VIEW_OWNER_EXPORT_OWNER_UNAVAILABLE",
                    ["exportArtifact.content"]);
            var artifact = exportRead.Artifact;
            var currentExport = _export.Parse(artifact);
            if (command.Export is null ||
                command.Export.CaptureSemanticSha256 !=
                    currentExport.CaptureSemanticSha256 ||
                command.Export.ManifestSha256 !=
                    currentExport.ManifestSha256 ||
                command.Export.ContentSha256 !=
                    currentExport.ContentSha256)
                return Incomplete(
                    "CROSS_VIEW_OWNER_EXPORT_CAPTURE_DRIFT",
                    ["finalCaptures.exportCapture"]);

            var authRead = await exportAuth.ReadAsync(
                    command.Material.Export,
                    cancellationToken)
                .ConfigureAwait(false);
            if (authRead?.Ready != true || authRead.Binding is null)
                return Incomplete(
                    authRead?.FailureCode ??
                    "CROSS_VIEW_OWNER_EXPORT_AUTH_UNAVAILABLE",
                    ["exportArtifact.authorizationBinding"]);
            StatisticReconciliationActualCrossViewAuthorizationV3Integrity
                .RequireExportBinding(authRead.Binding);
            var apiRequired = artifact.Manifest.ResultKind !=
                StatRunExportResultKinds.Advanced;
            if (apiRequired && command.Api is null ||
                !apiRequired && command.Api is not null)
                return Incomplete(
                    "CROSS_VIEW_OWNER_API_APPLICABILITY_INVALID",
                    ["finalCaptures.apiCapture"]);
            var relation = StatisticReconciliationActualCrossViewAuthorization
                .Create(
                    artifact.Manifest.WorkId,
                    artifact.Manifest.ScopeId,
                    command.Api?.Authorization,
                    authRead.Binding);
            var family = await ProjectFamilyAsync(
                    projectionCommand,
                    artifact.Manifest.ResultKind,
                    artifact.Manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireFamily(family.Family, artifact.Manifest.ResultKind);
            var projected =
                StatisticReconciliationActualCrossViewV2ExportBaseProjector
                    .ProjectJson(
                        family.ExportSourceJson,
                        artifact.Manifest);
            var exportBase =
                StatisticReconciliationActualCrossViewV2ExportBaseProjector
                    .BindTemplate(
                        projected.Base,
                        command.Material.Run.DynamicFormVersionId);
            if (projected.ColumnManifestSha256 !=
                command.Material.Export.ExpectedColumnManifestSha256)
                return Incomplete(
                    "CROSS_VIEW_OWNER_EXPORT_PROJECTION_DRIFT",
                    ["exportArtifact.columnManifest"]);
            var baseline = new
                StatisticReconciliationActualCrossViewParityV2Base(
                    StatisticReconciliationActualCrossViewParityV2Schemas.Base,
                    family.ApiBase,
                    exportBase);
            var actual = new
                StatisticReconciliationActualCrossViewParityV2Actual(
                    StatisticReconciliationActualCrossViewParityV2Schemas.Actual,
                    command.Api,
                    artifact.Manifest,
                    currentExport);
            var plan = Plan(
                projectionCommand,
                family,
                relation,
                artifact.Manifest);
            var proof = _proof.Prove(plan, relation, baseline, actual);
            if (!proof.Complete)
                return Incomplete(
                    $"CROSS_VIEW_OWNER_PROOF:{proof.FailureCode}",
                    []);
            return Complete(relation, plan, baseline, actual, proof);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is
            StatisticReconciliationActualObservationException or
            InvalidOperationException or ArgumentException or
            OverflowException or FormatException or
            System.Text.Json.JsonException)
        {
            return Incomplete(
                $"CROSS_VIEW_OWNER_INVALID:{error.Message}",
                []);
        }
    }

    private Task<StatisticReconciliationActualCrossViewV3FamilyProjection>
        ProjectFamilyAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            string resultKind,
            StatisticReconciliationActualExportManifest manifest,
            CancellationToken cancellationToken)
        => resultKind switch
        {
            StatRunExportResultKinds.DirectField or
            StatRunExportResultKinds.DirectTable or
            StatRunExportResultKinds.DirectLabel =>
                direct.ProjectAsync(command, manifest, cancellationToken),
            StatRunExportResultKinds.Basic or StatRunExportResultKinds.Flow =>
                basic.ProjectAsync(command, cancellationToken),
            StatRunExportResultKinds.Advanced =>
                advanced.ProjectAsync(command, cancellationToken),
            StatRunExportResultKinds.Diff =>
                diff.ProjectAsync(command, cancellationToken),
            _ => throw Invalid("CROSS_VIEW_OWNER_RESULT_KIND_UNSUPPORTED")
        };

    private static StatisticReconciliationActualCrossViewParityV3Plan Plan(
        StatisticReconciliationActualCrossViewV2OwnerCommand command,
        StatisticReconciliationActualCrossViewV3FamilyProjection family,
        StatisticReconciliationActualCrossViewAuthorizationRelationV1 relation,
        StatisticReconciliationActualExportManifest manifest)
    {
        var api = command.Api;
        var apiRequired = api is not null;
        var generation = apiRequired
            ? StatisticReconciliationActualCrossViewV3OwnerCommon.Generation(
                api!)
            : default;
        var directFamily = family.Family ==
            StatisticReconciliationActualCrossViewFamilies.Direct;
        var pages = command.Material.Api.Pages;
        var pageSize = apiRequired
            ? pages.Select(value => value.PageSize).Distinct().Single()
            : 0;
        return new(
            StatisticReconciliationActualCrossViewParityV3Schemas.Plan,
            family.Family,
            apiRequired
                ? StatisticReconciliationActualCrossViewApiApplicability.Required
                : StatisticReconciliationActualCrossViewApiApplicability
                    .NoProductionApi,
            api?.Surface,
            manifest.ResultKind,
            family.Family is
                StatisticReconciliationActualCrossViewFamilies.Direct or
                StatisticReconciliationActualCrossViewFamilies.Diff,
            manifest.WorkId,
            manifest.ScopeType,
            manifest.ScopeId,
            command.Material.Run.DynamicFormVersionId,
            Required(manifest.PeriodInstanceKey,
                "CROSS_VIEW_OWNER_PERIOD_INSTANCE_KEY"),
            relation.ApiAuthorizationSnapshotSha256,
            relation.ExportAuthorizationSnapshotSha256,
            relation.SemanticSha256,
            api?.OwnerResultId,
            apiRequired ? generation.GenerationId : null,
            apiRequired ? generation.GenerationSha256 : null,
            directFamily
                ? command.Material.Direct.OwnerGenerationSha256
                : null,
            directFamily
                ? command.Material.Direct.DirectSourceRevision
                : null,
            directFamily
                ? command.Material.Aggregate.DirectPublicationRevision
                : null,
            family.BasicGeneration,
            api?.CanonicalFilterJson,
            api?.FilterSha256,
            apiRequired ? command.Material.Api.ExpectedTotalRows : 0,
            pageSize,
            apiRequired ? pages.Length : 0,
            manifest.ExportId,
            manifest.ResultId,
            manifest.ResultSha256,
            manifest.ConfigSha256,
            manifest.SourceSha256,
            manifest.LifecycleRevision,
            manifest.RequestSha256,
            manifest.ContentSha256,
            command.Material.Export.ExpectedColumnManifestSha256,
            manifest.OwnerSemanticSha256,
            Required(manifest.CanonicalFilterJson,
                "CROSS_VIEW_OWNER_EXPORT_FILTER_JSON"),
            manifest.FilterSha256);
    }

    private static void RequireFamily(string family, string resultKind)
    {
        var expected = resultKind switch
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
        if (family != expected)
            throw Invalid("CROSS_VIEW_OWNER_FAMILY_RESULT_MISMATCH");
    }

    private static StatisticReconciliationActualCrossViewV3OwnerResolution
        Complete(
            StatisticReconciliationActualCrossViewAuthorizationRelationV1
                authorization,
            StatisticReconciliationActualCrossViewParityV3Plan plan,
            StatisticReconciliationActualCrossViewParityV2Base baseline,
            StatisticReconciliationActualCrossViewParityV2Actual actual,
            StatisticReconciliationActualCrossViewParityV3Proof proof)
    {
        var sha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_V3_OWNER_RESOLUTION_V1",
            ResolutionSchema,
            StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
            authorization.SemanticSha256,
            proof.ProofSha256);
        return new(
            ResolutionSchema,
            StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
            StatisticReconciliationActualCrossViewV2OwnerFailures.None,
            [],
            authorization,
            plan,
            baseline,
            actual,
            proof,
            sha);
    }

    private static StatisticReconciliationActualCrossViewV3OwnerResolution
        Incomplete(string failure, ImmutableArray<string> required)
    {
        if (required.IsDefault)
            required = [];
        var normalized = required.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        var sha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_V3_OWNER_RESOLUTION_INCOMPLETE_V1",
            ResolutionSchema,
            failure,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_CROSS_VIEW_V3_OWNER_REQUIRED_FIELDS_V1",
                normalized));
        return new(
            ResolutionSchema,
            StatisticReconciliationActualCrossViewV2OwnerStates.Incomplete,
            failure,
            normalized,
            null,
            null,
            null,
            null,
            null,
            sha);
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}
