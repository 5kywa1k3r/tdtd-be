using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Pure family-aware cross-view verifier. It consumes complete immutable
/// preimages only; it never queries an owner and never treats an opaque digest
/// as evidence that two value-bearing views are equal.
/// </summary>
internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private const string PlanSchema =
        StatisticReconciliationActualCrossViewParityV2Schemas.Plan;
    private const string BaseSchema =
        StatisticReconciliationActualCrossViewParityV2Schemas.Base;
    private const string ActualSchema =
        StatisticReconciliationActualCrossViewParityV2Schemas.Actual;
    private const string ProofSchema =
        StatisticReconciliationActualCrossViewParityV2Schemas.Proof;
    private const int RequiredPageSize = 200;
    private const int MaximumPages = 32;
    private const int MaximumRows = 50_000;

    internal StatisticReconciliationActualCrossViewParityV2Proof Prove(
        StatisticReconciliationActualCrossViewParityV2Plan? plan,
        StatisticReconciliationActualCrossViewParityV2Base? baseline,
        StatisticReconciliationActualCrossViewParityV2Actual? actual)
    {
        try
        {
            var normalized = Normalize(plan);
            RequirePlanFilterSemantics(normalized);
            RequireExactProtocolFilterSemantics(normalized);
            RequireFamilyExportFilterShape(normalized);
            var exportBase = ValidateExportBase(normalized, baseline);
            var export = ValidateExport(normalized, actual);
            var exportRelation = RelateExport(exportBase, export);

            ValidatedApiBase? apiBase = null;
            ValidatedApi? api = null;
            var apiRelationSha = NotApplicableSha("API");
            var apiRows = 0L;
            var totalsRelation = exportRelation.TotalsRelationSha256;
            var totalsCount = exportRelation.TotalsRelationCount;
            var filterRelation = exportRelation.FilterRelationSha256;
            var sharedRelation = NotApplicableSha("SHARED_ROWS");
            var sharedRelationCount = 0L;
            var apiSemantic = NotApplicableSha("API");

            if (normalized.ApiRequired)
            {
                apiBase = ValidateApiBase(normalized, baseline);
                api = ValidateApi(normalized, actual);
                apiRelationSha = RelateApi(apiBase, api);
                apiRows = api.Rows.Length;
                apiSemantic = api.CaptureSha256;
                totalsRelation = H(
                    "P10_ACTUAL_CROSS_VIEW_TOTAL_RELATIONS_V2",
                    apiBase.TotalsSha256,
                    api.TotalsSha256,
                    exportRelation.TotalsRelationSha256);
                totalsCount = checked(
                    apiBase.Totals.Length + exportRelation.TotalsRelationCount);
                filterRelation = H(
                    "P10_ACTUAL_CROSS_VIEW_FILTER_RELATIONS_V2",
                    apiBase.FilterRelationSha256,
                    api.FilterRelationSha256,
                    exportRelation.FilterRelationSha256);

                if (normalized.ViewsShareOrderedRows)
                {
                    (sharedRelation, sharedRelationCount) = RelateSharedRows(
                        normalized,
                        apiBase,
                        exportBase);
                }
            }

            var baseSha = H(
                "P10_ACTUAL_CROSS_VIEW_BASE_V2",
                BaseSchema,
                apiBase?.ProjectionSha256,
                exportBase.ProjectionSha256,
                normalized.ViewsShareOrderedRows ? "true" : "false");
            var resultRelation = H(
                "P10_ACTUAL_CROSS_VIEW_RESULT_RELATION_V2",
                normalized.Family,
                normalized.ApiSurface,
                normalized.ApiOwnerResultId,
                normalized.ApiGenerationId,
                normalized.ApiGenerationSha256,
                normalized.ExportResultKind,
                normalized.ExportId,
                normalized.ExportResultId,
                normalized.ExportResultSha256,
                actual!.ExportManifest.ExportId,
                actual.ExportManifest.ResultId,
                actual.ExportManifest.ResultSha256,
                actual.ExportCapture.CaptureSemanticSha256);
            var planSha = normalized.SemanticSha256;
            var cellRelation = H(
                "P10_ACTUAL_CROSS_VIEW_CELL_RELATIONS_V2",
                apiRelationSha,
                exportRelation.CellRelationSha256,
                sharedRelation);
            var cellRelationCount = checked(
                exportRelation.CellRelationCount + sharedRelationCount);
            var proofSha = H(
                "P10_ACTUAL_CROSS_VIEW_PROOF_V2",
                ProofSchema,
                "true",
                StatisticReconciliationActualCrossViewParityV2Failures.None,
                planSha,
                baseSha,
                apiSemantic,
                I(apiRows),
                export.CaptureSha256,
                I(export.Rows.Length),
                sharedRelation,
                I(sharedRelationCount),
                cellRelation,
                I(cellRelationCount),
                totalsRelation,
                I(totalsCount),
                filterRelation,
                resultRelation);
            return new(
                ProofSchema,
                true,
                StatisticReconciliationActualCrossViewParityV2Failures.None,
                planSha,
                baseSha,
                apiSemantic,
                apiRows,
                export.CaptureSha256,
                export.Rows.Length,
                sharedRelation,
                sharedRelationCount,
                cellRelation,
                cellRelationCount,
                totalsRelation,
                totalsCount,
                filterRelation,
                resultRelation,
                proofSha);
        }
        catch (ParityV2Failure failure)
        {
            return Incomplete(failure.Code);
        }
        catch (OverflowException)
        {
            return Incomplete(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .NumericOverflow);
        }
        catch (Exception error) when (error is
            StatisticReconciliationActualObservationException or
            System.Text.Json.JsonException or ArgumentException or
            InvalidOperationException or NullReferenceException or
            KeyNotFoundException or IndexOutOfRangeException or
            FormatException or InvalidCastException)
        {
            return Incomplete(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PlanInvalid);
        }
    }

    private static NormalizedPlan Normalize(
        StatisticReconciliationActualCrossViewParityV2Plan? value)
    {
        if (value is null)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .InputRequired);
        if (!Eq(value.SchemaVersion, PlanSchema))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .SchemaUnsupported);
        var family = Upper(value.Family);
        var applicability = Upper(value.ApiApplicability);
        var exportKind = Upper(value.ExportResultKind);
        var apiSurface = OptionalUpper(value.ApiSurface);
        var expected = family switch
        {
            StatisticReconciliationActualCrossViewFamilies.Direct =>
                (Api: true, Shared: true,
                 ApiSurfaces: new[]
                 {
                     StatisticReconciliationActualApiSurfaces.DirectField,
                     StatisticReconciliationActualApiSurfaces.DirectTable,
                     StatisticReconciliationActualApiSurfaces.DirectLabel
                 }, ExportKinds: new[]
                 {
                     StatisticReconciliationActualApiSurfaces.DirectField,
                     StatisticReconciliationActualApiSurfaces.DirectTable,
                     StatisticReconciliationActualApiSurfaces.DirectLabel
                 }),
            StatisticReconciliationActualCrossViewFamilies.Basic =>
                (Api: true, Shared: false,
                 ApiSurfaces: new[]
                 { StatisticReconciliationActualApiSurfaces.BasicSource },
                 ExportKinds: new[] { "BASIC" }),
            StatisticReconciliationActualCrossViewFamilies.Flow =>
                (Api: true, Shared: false,
                 ApiSurfaces: new[]
                 { StatisticReconciliationActualApiSurfaces.BasicSource },
                 ExportKinds: new[] { "FLOW" }),
            StatisticReconciliationActualCrossViewFamilies.Diff =>
                (Api: true, Shared: true,
                 ApiSurfaces: new[]
                 { StatisticReconciliationActualApiSurfaces.P9Diff },
                 ExportKinds: new[] { "DIFF" }),
            StatisticReconciliationActualCrossViewFamilies.Advanced =>
                (Api: false, Shared: false,
                 ApiSurfaces: Array.Empty<string>(),
                 ExportKinds: new[] { "ADVANCED" }),
            _ => throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FamilySurfaceInvalid)
        };
        var apiRequired = applicability ==
            StatisticReconciliationActualCrossViewApiApplicability.Required;
        var apiNotApplicable = applicability ==
            StatisticReconciliationActualCrossViewApiApplicability
                .NoProductionApi;
        if (apiRequired != expected.Api || apiNotApplicable == expected.Api ||
            value.ViewsShareOrderedRows != expected.Shared ||
            (expected.Api ? !expected.ApiSurfaces.Contains(apiSurface, StringComparer.Ordinal) : apiSurface is not null) ||
            !expected.ExportKinds.Contains(exportKind, StringComparer.Ordinal) ||
            (family == StatisticReconciliationActualCrossViewFamilies.Direct &&
             !Eq(apiSurface, exportKind)))
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FamilySurfaceInvalid);
        }

        var workId = Required(value.WorkId);
        var scopeType = Upper(value.ScopeType);
        var scopeId = Required(value.ScopeId);
        var templateId = Required(value.DynamicFormTemplateId);
        var period = Required(value.PeriodInstanceKey);
        var authorization = Sha(value.AuthorizationSnapshotSha256);
        var exportId = Required(value.ExportId);
        var exportResultId = Required(value.ExportResultId);
        var exportResultSha = Sha(value.ExportResultSha256);
        var exportConfigSha = Sha(value.ExportConfigSha256);
        var exportSourceSha = Sha(value.ExportSourceSha256);
        var exportRequestSha = Sha(value.ExportRequestSha256);
        var exportContentSha = Sha(value.ExportContentSha256);
        var exportColumnSha = Sha(value.ExportColumnManifestSha256);
        var exportOwnerSha = Sha(value.ExportOwnerSemanticSha256);
        var canonicalExportFilter = CanonicalObject(
            value.CanonicalExportFilterJson,
            StatisticReconciliationActualCrossViewParityV2Failures
                .PreimageMissing);
        var exportFilterSha = Sha(value.ExportFilterSha256);
        if (!Eq(StatisticReconciliationActualJson.RawSha256(
                    canonicalExportFilter), exportFilterSha) ||
            scopeType is not ("ASSIGNMENT" or "ROOT") ||
            (apiRequired && scopeType != "ASSIGNMENT") ||            value.ExportLifecycleRevision < 0)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PlanInvalid);
        }

        string? apiOwner = null;
        string? apiGenerationId = null;
        string? apiGenerationSha = null;
        string? canonicalApiFilter = null;
        string? apiFilterSha = null;
        if (apiRequired)
        {
            apiOwner = Required(value.ApiOwnerResultId);
            apiGenerationId = Required(value.ApiGenerationId);
            apiGenerationSha = Sha(value.ApiGenerationSha256);
            canonicalApiFilter = CanonicalObject(
                value.CanonicalApiFilterJson,
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PreimageMissing);
            apiFilterSha = Sha(value.ApiFilterSha256);
            var expectedPages = Math.Max(1, checked((int)(
                (value.ApiExpectedTotalRows + RequiredPageSize - 1) /
                RequiredPageSize)));
            if (!Eq(StatisticReconciliationActualJson.RawSha256(
                        canonicalApiFilter), apiFilterSha) ||
                value.ApiExpectedTotalRows is < 0 or > MaximumRows ||
                value.ApiPageSize != RequiredPageSize ||
                value.ApiPageCount is < 1 or > MaximumPages ||
                value.ApiPageCount != expectedPages)
            {
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .PlanInvalid);
            }
        }
        else if (value.ApiOwnerResultId is not null ||
                 value.ApiGenerationId is not null ||
                 value.ApiGenerationSha256 is not null ||
                 value.CanonicalApiFilterJson is not null ||
                 value.ApiFilterSha256 is not null ||
                 value.ApiExpectedTotalRows != 0 || value.ApiPageSize != 0 ||
                 value.ApiPageCount != 0)
        {
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PlanInvalid);
        }

        RequireFamilyOwnerBinding(
            value,
            family,
            apiOwner,
            apiGenerationId,
            apiGenerationSha,
            exportResultId,
            exportResultSha);
        var basicGenerationBindingSha = RequireBasicGenerationBinding(
            value,
            family,
            apiGenerationId,
            apiGenerationSha,
            exportResultSha,
            exportConfigSha,
            exportSourceSha,
            value.ExportLifecycleRevision);
        var directPublicationSha = family ==
            StatisticReconciliationActualCrossViewFamilies.Direct
                ? Sha(value.DirectPublicationGenerationSha256)
                : null;
        var directSourceRevision = value.DirectSourceRevision.HasValue
            ? I(value.DirectSourceRevision.Value)
            : null;
        var directPublicationRevision =
            value.DirectPublicationRevision.HasValue
                ? I(value.DirectPublicationRevision.Value)
                : null;

        var semantic = H(
            "P10_ACTUAL_CROSS_VIEW_PLAN_V2",
            PlanSchema,
            family,
            applicability,
            apiSurface,
            exportKind,
            value.ViewsShareOrderedRows ? "true" : "false",
            workId,
            scopeType,
            scopeId,
            templateId,
            period,
            authorization,
            apiOwner,
            apiGenerationId,
            apiGenerationSha,
            directPublicationSha,
            directSourceRevision,
            directPublicationRevision,
            basicGenerationBindingSha,
            canonicalApiFilter,
            apiFilterSha,
            I(value.ApiExpectedTotalRows),
            I(value.ApiPageSize),
            I(value.ApiPageCount),
            exportId,
            exportResultId,
            exportResultSha,
            exportConfigSha,
            exportSourceSha,
            I(value.ExportLifecycleRevision),
            exportRequestSha,
            exportContentSha,
            exportColumnSha,
            exportOwnerSha,
            canonicalExportFilter,
            exportFilterSha);
        return new(
            family, apiRequired, apiSurface, exportKind,
            value.ViewsShareOrderedRows, workId, scopeType, scopeId,
            templateId, period, authorization, apiOwner, apiGenerationId,
            apiGenerationSha, canonicalApiFilter, apiFilterSha,
            value.ApiExpectedTotalRows, value.ApiPageSize,
            value.ApiPageCount, exportId, exportResultId, exportResultSha,
            exportConfigSha, exportSourceSha, value.ExportLifecycleRevision,
            exportRequestSha, exportContentSha, exportColumnSha,
            exportOwnerSha, canonicalExportFilter, exportFilterSha, semantic);
    }

    private sealed record NormalizedPlan(
        string Family,
        bool ApiRequired,
        string? ApiSurface,
        string ExportResultKind,
        bool ViewsShareOrderedRows,
        string WorkId,
        string ScopeType,
        string ScopeId,
        string DynamicFormTemplateId,
        string PeriodInstanceKey,
        string AuthorizationSnapshotSha256,
        string? ApiOwnerResultId,
        string? ApiGenerationId,
        string? ApiGenerationSha256,
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
        string ExportFilterSha256,
        string SemanticSha256);

    private sealed class ParityV2Failure(string code) : Exception(code)
    {
        internal string Code { get; } = code;
    }

    private static ParityV2Failure Fail(string code) => new(code);
    private static bool Eq(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);
    private static string H(string domain, params string?[] values)
        => StatisticReconciliationActualCanonical.Hash(domain, values);
    private static string HS(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);
    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);
    private static string Required(string? value)
        => StatisticReconciliationActualCanonical.Required(
            value, "CROSS_VIEW_VALUE");
    private static string Sha(string? value)
        => StatisticReconciliationActualCanonical.Sha256(
            value, "CROSS_VIEW_SHA256");
    private static string Upper(string? value)
    {
        var result = Required(value);
        if (!Eq(result, result.ToUpperInvariant()))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .PlanInvalid);
        return result;
    }
    private static string? OptionalUpper(string? value)
        => value is null ? null : Upper(value);

    private static string CanonicalObject(string? value, string failure)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Fail(failure);
        using var document = StatisticReconciliationActualJson.ParseStrict(
            value, "CROSS_VIEW_JSON");
        if (document.RootElement.ValueKind !=
            System.Text.Json.JsonValueKind.Object)
            throw Fail(failure);
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        if (!Eq(canonical, value))
            throw Fail(failure);
        return canonical;
    }

    private static string NotApplicableSha(string layer)
        => H("P10_ACTUAL_CROSS_VIEW_NOT_APPLICABLE_V2", layer);
}
