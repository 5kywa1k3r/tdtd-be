namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// V3 keeps the value/row/cell proof from V2, but validates API and export
/// authorization in their native domains first. A compatibility envelope is
/// used only after both original envelopes and their neutral relation pass;
/// its digest is never reported as an authorization fact.
/// </summary>
internal sealed class StatisticReconciliationActualCrossViewParityV3
{
    internal StatisticReconciliationActualCrossViewParityV3Proof Prove(
        StatisticReconciliationActualCrossViewParityV3Plan? plan,
        StatisticReconciliationActualCrossViewAuthorizationRelationV1?
            authorization,
        StatisticReconciliationActualCrossViewParityV2Base? baseline,
        StatisticReconciliationActualCrossViewParityV2Actual? actual)
    {
        try
        {
            if (plan is null || authorization is null || baseline is null ||
                actual is null ||
                plan.SchemaVersion !=
                    StatisticReconciliationActualCrossViewParityV3Schemas.Plan)
                return Incomplete("CROSS_VIEW_V3_INPUT_INVALID");
            var apiRequired = plan.Family !=
                StatisticReconciliationActualCrossViewFamilies.Advanced;
            StatisticReconciliationActualCrossViewAuthorizationV3Integrity
                .RequireRelation(authorization, apiRequired);
            RequireRelation(plan, authorization, actual, apiRequired);
            RequireOriginalExportEnvelope(actual);

            var compatibilityAuthorization = apiRequired
                ? plan.ApiAuthorizationSnapshotSha256!
                : plan.ExportAuthorizationSnapshotSha256;
            var compatibility = Compatibility(
                plan,
                baseline,
                actual,
                compatibilityAuthorization);
            var v2 = StatisticReconciliationActualCrossViewParityV2.Prove(
                compatibility.Plan,
                compatibility.Base,
                compatibility.Actual);
            if (!v2.Complete)
                return Incomplete($"CROSS_VIEW_V3_COMPAT:{v2.FailureCode}");

            var apiSemantic = actual.Api?.CaptureSemanticSha256 ??
                              "API_NOT_APPLICABLE";
            var exportSemantic = actual.ExportCapture.CaptureSemanticSha256;
            var proofSha = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_CROSS_VIEW_PARITY_PROOF_V3",
                StatisticReconciliationActualCrossViewParityV3Schemas.Proof,
                authorization.SemanticSha256,
                v2.ProofSha256,
                apiSemantic,
                exportSemantic);
            return new(
                StatisticReconciliationActualCrossViewParityV3Schemas.Proof,
                true,
                StatisticReconciliationActualCrossViewParityV2Failures.None,
                authorization.SemanticSha256,
                v2.ProofSha256,
                apiSemantic,
                exportSemantic,
                proofSha);
        }
        catch (Exception error) when (error is
            StatisticReconciliationActualObservationException or
            ArgumentException or InvalidOperationException or
            OverflowException or FormatException or
            System.Text.Json.JsonException)
        {
            return Incomplete("CROSS_VIEW_V3_INVALID");
        }
    }

    private static void RequireRelation(
        StatisticReconciliationActualCrossViewParityV3Plan plan,
        StatisticReconciliationActualCrossViewAuthorizationRelationV1 relation,
        StatisticReconciliationActualCrossViewParityV2Actual actual,
        bool apiRequired)
    {
        if (plan.WorkId != relation.WorkId ||
            plan.ScopeId != relation.ScopeAssignmentId ||
            plan.ApiAuthorizationSnapshotSha256 !=
                relation.ApiAuthorizationSnapshotSha256 ||
            plan.ExportAuthorizationSnapshotSha256 !=
                relation.ExportAuthorizationSnapshotSha256 ||
            plan.AuthorizationRelationSha256 != relation.SemanticSha256 ||
            actual.ExportManifest.AuthorizationSnapshotSha256 !=
                plan.ExportAuthorizationSnapshotSha256 ||
            actual.ExportManifest.WorkId != relation.WorkId ||
            actual.ExportManifest.ScopeId != relation.ScopeAssignmentId ||
            actual.ExportManifest.OwnerSemanticSha256 !=
                relation.ExportOwnerSemanticSha256)
            throw Invalid("CROSS_VIEW_V3_AUTH_RELATION_MISMATCH");
        if (apiRequired)
        {
            var api = actual.Api ?? throw Invalid(
                "CROSS_VIEW_V3_API_REQUIRED");
            if (api.Authorization.AuthorizationSnapshotSha256 !=
                    plan.ApiAuthorizationSnapshotSha256 ||
                api.Authorization.ActorUserId != relation.ApiActorUserId ||
                !api.Authorization.PermissionCodes.SequenceEqual(
                    relation.ApiPermissionCodes,
                    StringComparer.Ordinal) ||
                api.Authorization.RowCountBeforeRedaction !=
                    relation.ApiRowCountBeforeRedaction ||
                api.Authorization.RowCountAfterRedaction !=
                    relation.ApiRowCountAfterRedaction)
                throw Invalid("CROSS_VIEW_V3_API_AUTH_RELATION_MISMATCH");
        }
        else if (actual.Api is not null)
            throw Invalid("CROSS_VIEW_V3_API_NOT_APPLICABLE");
    }

    private static void RequireOriginalExportEnvelope(
        StatisticReconciliationActualCrossViewParityV2Actual actual)
    {
        var parser = new StatisticReconciliationActualExportParser();
        var manifestSha = parser.ComputeManifestSha256(actual.ExportManifest);
        if (actual.ExportCapture.ManifestSha256 != manifestSha ||
            actual.ExportCapture.ExportId != actual.ExportManifest.ExportId ||
            actual.ExportCapture.ResultKind !=
                actual.ExportManifest.ResultKind ||
            actual.ExportCapture.ResultId != actual.ExportManifest.ResultId ||
            actual.ExportCapture.ResultSha256 !=
                actual.ExportManifest.ResultSha256 ||
            actual.ExportCapture.ContentSha256 !=
                actual.ExportManifest.ContentSha256 ||
            actual.ExportCapture.OwnerSemanticSha256 !=
                actual.ExportManifest.OwnerSemanticSha256)
            throw Invalid("CROSS_VIEW_V3_EXPORT_ENVELOPE_INVALID");
        var captureSha = ExportCaptureSha(
            actual.ExportManifest,
            actual.ExportCapture,
            manifestSha);
        if (actual.ExportCapture.CaptureSemanticSha256 != captureSha)
            throw Invalid("CROSS_VIEW_V3_EXPORT_CAPTURE_DIGEST_INVALID");
    }

    private static CompatibilityBundle Compatibility(
        StatisticReconciliationActualCrossViewParityV3Plan plan,
        StatisticReconciliationActualCrossViewParityV2Base baseline,
        StatisticReconciliationActualCrossViewParityV2Actual actual,
        string authorizationSha)
    {
        var v2Plan = new StatisticReconciliationActualCrossViewParityV2Plan(
            StatisticReconciliationActualCrossViewParityV2Schemas.Plan,
            plan.Family,
            plan.ApiApplicability,
            plan.ApiSurface,
            plan.ExportResultKind,
            plan.ViewsShareOrderedRows,
            plan.WorkId,
            plan.ScopeType,
            plan.ScopeId,
            plan.DynamicFormTemplateId,
            plan.PeriodInstanceKey,
            authorizationSha,
            plan.ApiOwnerResultId,
            plan.ApiGenerationId,
            plan.ApiGenerationSha256,
            plan.DirectPublicationGenerationSha256,
            plan.DirectSourceRevision,
            plan.DirectPublicationRevision,
            plan.BasicGeneration,
            plan.CanonicalApiFilterJson,
            plan.ApiFilterSha256,
            plan.ApiExpectedTotalRows,
            plan.ApiPageSize,
            plan.ApiPageCount,
            plan.ExportId,
            plan.ExportResultId,
            plan.ExportResultSha256,
            plan.ExportConfigSha256,
            plan.ExportSourceSha256,
            plan.ExportLifecycleRevision,
            plan.ExportRequestSha256,
            plan.ExportContentSha256,
            plan.ExportColumnManifestSha256,
            plan.ExportOwnerSemanticSha256,
            plan.CanonicalExportFilterJson,
            plan.ExportFilterSha256);
        var compatManifest = actual.ExportManifest with
        {
            AuthorizationSnapshotSha256 = authorizationSha
        };
        var parser = new StatisticReconciliationActualExportParser();
        var compatManifestSha = parser.ComputeManifestSha256(compatManifest);
        var compatCapture = actual.ExportCapture with
        {
            ManifestSha256 = compatManifestSha
        };
        compatCapture = compatCapture with
        {
            CaptureSemanticSha256 = ExportCaptureSha(
                compatManifest,
                compatCapture,
                compatManifestSha)
        };
        var compatBase = baseline with
        {
            Api = baseline.Api is null
                ? null
                : baseline.Api with
                {
                    AuthorizationSnapshotSha256 = authorizationSha
                },
            Export = baseline.Export with
            {
                AuthorizationSnapshotSha256 = authorizationSha
            }
        };
        var compatActual = actual with
        {
            ExportManifest = compatManifest,
            ExportCapture = compatCapture
        };
        return new(v2Plan, compatBase, compatActual);
    }

    private static string ExportCaptureSha(
        StatisticReconciliationActualExportManifest manifest,
        StatisticReconciliationActualExportCapture capture,
        string manifestSha)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXPORT_CAPTURE_V2",
            manifest.ExportId,
            manifest.Format,
            manifest.ResultKind,
            manifest.ResultId,
            manifest.ResultSha256,
            manifest.ConfigSha256,
            manifest.SourceSha256,
            manifest.FilterSha256,
            manifest.PeriodInstanceKey ?? "~",
            manifest.CanonicalFilterJson ?? "~",
            StatisticReconciliationActualCanonical.Integer(
                manifest.LifecycleRevision),
            manifest.ContentSha256,
            manifestSha,
            manifest.OwnerSemanticSha256,
            capture.RowsSemanticSha256,
            capture.TotalsSemanticSha256);

    private static StatisticReconciliationActualCrossViewParityV3Proof
        Incomplete(string failure)
    {
        var proofSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_PARITY_PROOF_V3_INCOMPLETE",
            StatisticReconciliationActualCrossViewParityV3Schemas.Proof,
            failure);
        return new(
            StatisticReconciliationActualCrossViewParityV3Schemas.Proof,
            false,
            failure,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            proofSha);
    }

    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);

    private sealed record CompatibilityBundle(
        StatisticReconciliationActualCrossViewParityV2Plan Plan,
        StatisticReconciliationActualCrossViewParityV2Base Base,
        StatisticReconciliationActualCrossViewParityV2Actual Actual);
}
