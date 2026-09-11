using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualDirectCrossViewParitySchemas
{
    internal const string V1 = "P10_ACTUAL_DIRECT_CROSS_VIEW_PARITY_V1";
}

internal static class StatisticReconciliationActualDirectCrossViewParityFailures
{
    internal const string None = "NONE";
    internal const string InputRequired = "PARITY_INPUT_REQUIRED";
    internal const string SchemaUnsupported = "PARITY_SCHEMA_UNSUPPORTED";
    internal const string SurfaceUnsupported = "PARITY_SURFACE_UNSUPPORTED";
    internal const string PlanInvalid = "PARITY_PLAN_INVALID";
    internal const string PreimageMissing = "PARITY_PREIMAGE_MISSING";
    internal const string FilterInvalid = "PARITY_FILTER_INVALID";
    internal const string FilterMismatch = "PARITY_FILTER_MISMATCH";
    internal const string ResultBindingMismatch = "PARITY_RESULT_BINDING_MISMATCH";
    internal const string BaseProjectionInvalid = "PARITY_BASE_PROJECTION_INVALID";
    internal const string ApiCaptureInvalid = "PARITY_API_CAPTURE_INVALID";
    internal const string ApiPartitionIncomplete = "PARITY_API_PARTITION_INCOMPLETE";
    internal const string ApiRowInvalid = "PARITY_API_ROW_INVALID";
    internal const string ExportCaptureInvalid = "PARITY_EXPORT_CAPTURE_INVALID";
    internal const string ExportRowInvalid = "PARITY_EXPORT_ROW_INVALID";
    internal const string RowCountMismatch = "PARITY_ROW_COUNT_MISMATCH";
    internal const string RowOrderMismatch = "PARITY_ROW_ORDER_MISMATCH";
    internal const string RowCellMismatch = "PARITY_ROW_CELL_MISMATCH";
    internal const string TotalsMismatch = "PARITY_TOTALS_MISMATCH";
    internal const string NumericOverflow = "PARITY_NUMERIC_OVERFLOW";
}

internal sealed record StatisticReconciliationActualDirectCrossViewParityPlan(
    string SchemaVersion,
    string Surface,
    string ResultKind,
    string WorkId,
    string ScopeAssignmentId,
    string DynamicFormTemplateId,
    string PeriodInstanceKey,
    string P9RunId,
    string P9GenerationId,
    string P9GenerationSha256,
    long P9DirectSourceRevision,
    long P9DirectPublicationRevision,
    string AuthorizationSnapshotSha256,
    string CanonicalApiFilterJson,
    string ApiFilterSha256,
    long ExpectedTotalRows,
    int PageSize,
    int PageCount,
    string ExportId,
    string ExportResultId);

internal sealed record StatisticReconciliationActualDirectCrossViewBaseRow(
    int Ordinal,
    string Identity,
    string CanonicalRowJson);

internal sealed record
    StatisticReconciliationActualDirectCrossViewBaseProjection(
        string SchemaVersion,
        string Surface,
        string WorkId,
        string ScopeAssignmentId,
        string DynamicFormTemplateId,
        string PeriodInstanceKey,
        string P9RunId,
        string P9GenerationId,
        string P9GenerationSha256,
        long P9DirectSourceRevision,
        long P9DirectPublicationRevision,
        string CanonicalFilterJson,
        string FilterSha256,
        ImmutableArray<StatisticReconciliationActualDirectCrossViewBaseRow> Rows,
        ImmutableArray<StatisticReconciliationActualApiTotalValue>
            FullFilterTotals);

internal sealed record StatisticReconciliationActualDirectCrossViewParityActual(
    string SchemaVersion,
    StatisticReconciliationActualDirectCrossViewBaseProjection Base,
    StatisticReconciliationActualApiCapture Api,
    StatisticReconciliationActualExportManifest ExportManifest,
    StatisticReconciliationActualExportCapture Export,
    string? ExportPeriodInstanceKey,
    string? CanonicalExportFilterJson);

internal sealed record StatisticReconciliationActualDirectCrossViewParityProof(
    string SchemaVersion,
    bool Complete,
    string FailureCode,
    string PlanSemanticSha256,
    string ActualSemanticSha256,
    string BaseRowsSha256,
    long BaseRowCount,
    string ApiPartitionSha256,
    long ApiRowCount,
    string ExportRowsSha256,
    long ExportRowCount,
    string RowRelationSha256,
    long RowRelationCount,
    string TotalsRelationSha256,
    int TotalsRelationCount,
    string FilterRelationSha256,
    string ResultRelationSha256,
    string ProofSha256);

internal sealed partial class StatisticReconciliationActualDirectCrossViewParity
{
    private const int MaximumRows = 50_000;
    private const int RequiredPageSize = 200;
    private const int MaximumPages = 32;
    private const string AssignmentScope = "ASSIGNMENT";
    private const string Schema =
        StatisticReconciliationActualDirectCrossViewParitySchemas.V1;

    internal StatisticReconciliationActualDirectCrossViewParityProof Prove(
        StatisticReconciliationActualDirectCrossViewParityPlan? plan,
        StatisticReconciliationActualDirectCrossViewParityActual? actual)
    {
        try
        {
            var normalizedPlan = NormalizePlan(plan);
            var filters = ValidateFilters(normalizedPlan, actual);
            var baseProjection = ValidateBase(normalizedPlan, actual!.Base);
            var api = ValidateApi(normalizedPlan, actual.Api);
            var export = ValidateExport(
                normalizedPlan,
                actual.ExportManifest,
                actual.Export,
                actual.ExportPeriodInstanceKey!,
                filters.ExportFilter);
            return Relate(
                normalizedPlan,
                actual,
                filters,
                baseProjection,
                api,
                export);
        }
        catch (ParityFailure failure)
        {
            return Incomplete(failure.Code);
        }
        catch (OverflowException)
        {
            return Incomplete(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .NumericOverflow);
        }
        catch (Exception error) when (error is
            StatisticReconciliationActualObservationException or
            JsonException or ArgumentException)
        {
            return Incomplete(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .PlanInvalid);
        }
    }

    private static NormalizedPlan NormalizePlan(
        StatisticReconciliationActualDirectCrossViewParityPlan? value)
    {
        if (value is null)
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .InputRequired);
        if (!Eq(value.SchemaVersion, Schema))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .SchemaUnsupported);
        var surface = ExactUpper(value.Surface);
        if (!DirectSurface(surface))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .SurfaceUnsupported);
        var resultKind = ExactUpper(value.ResultKind);
        if (!Eq(resultKind, surface))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ResultBindingMismatch);
        var workId = Required(value.WorkId);
        var scopeId = Required(value.ScopeAssignmentId);
        var templateId = Required(value.DynamicFormTemplateId);
        var period = Required(value.PeriodInstanceKey);
        var runId = Required(value.P9RunId);
        var generationId = Sha(value.P9GenerationId);
        var generationSha = Sha(value.P9GenerationSha256);
        var authorization = Sha(value.AuthorizationSnapshotSha256);
        var apiFilterSha = Sha(value.ApiFilterSha256);
        var exportId = Required(value.ExportId);
        var exportResultId = Required(value.ExportResultId);
        if (value.P9DirectSourceRevision <= 0 ||
            value.P9DirectPublicationRevision <= 0 ||
            value.ExpectedTotalRows is < 0 or > MaximumRows ||
            value.PageSize != RequiredPageSize ||
            value.PageCount is < 1 or > MaximumPages ||
            value.PageCount != Math.Max(
                1,
                checked((int)((value.ExpectedTotalRows +
                    RequiredPageSize - 1) / RequiredPageSize))) ||
            exportResultId != runId && exportResultId != generationId)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .PlanInvalid);
        }
        var canonicalApiFilter = CanonicalObject(
            value.CanonicalApiFilterJson,
            StatisticReconciliationActualDirectCrossViewParityFailures
                .PreimageMissing);
        if (!Eq(
                StatisticReconciliationActualJson.RawSha256(canonicalApiFilter),
                apiFilterSha))
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterInvalid);
        }
        var planSha = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_PLAN_V1",
            Schema,
            surface,
            resultKind,
            workId,
            scopeId,
            templateId,
            period,
            runId,
            generationId,
            generationSha,
            I(value.P9DirectSourceRevision),
            I(value.P9DirectPublicationRevision),
            authorization,
            canonicalApiFilter,
            apiFilterSha,
            I(value.ExpectedTotalRows),
            I(value.PageSize),
            I(value.PageCount),
            exportId,
            exportResultId);
        return new(
            surface,
            resultKind,
            workId,
            scopeId,
            templateId,
            period,
            runId,
            generationId,
            generationSha,
            value.P9DirectSourceRevision,
            value.P9DirectPublicationRevision,
            authorization,
            canonicalApiFilter,
            apiFilterSha,
            value.ExpectedTotalRows,
            value.PageSize,
            value.PageCount,
            exportId,
            exportResultId,
            planSha);
    }

    private sealed record NormalizedPlan(
        string Surface,
        string ResultKind,
        string WorkId,
        string ScopeAssignmentId,
        string DynamicFormTemplateId,
        string PeriodInstanceKey,
        string P9RunId,
        string P9GenerationId,
        string P9GenerationSha256,
        long P9DirectSourceRevision,
        long P9DirectPublicationRevision,
        string AuthorizationSnapshotSha256,
        string CanonicalApiFilterJson,
        string ApiFilterSha256,
        long ExpectedTotalRows,
        int PageSize,
        int PageCount,
        string ExportId,
        string ExportResultId,
        string SemanticSha256);

    private sealed class ParityFailure(string code) : Exception(code)
    {
        internal string Code { get; } = code;
    }

    private static ParityFailure F(string code) => new(code);
    private static bool Eq(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);
    private static string H(string domain, params string?[] values)
        => StatisticReconciliationActualCanonical.Hash(domain, values);
    private static string HS(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);
    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);
    private static string Required(string? value)
        => StatisticReconciliationActualCanonical.Required(value, "PARITY_VALUE");
    private static string Sha(string? value)
        => StatisticReconciliationActualCanonical.Sha256(value, "PARITY_SHA256");
    private static string ExactUpper(string? value)
    {
        var result = Required(value);
        if (!Eq(result, result.ToUpperInvariant()))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .PlanInvalid);
        return result;
    }
    private static bool DirectSurface(string value)
        => value is StatisticReconciliationActualApiSurfaces.DirectField or
            StatisticReconciliationActualApiSurfaces.DirectTable or
            StatisticReconciliationActualApiSurfaces.DirectLabel;

    private static string CanonicalObject(string? json, string missingCode)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw F(missingCode);
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json,
            "PARITY_JSON");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterInvalid);
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        if (!Eq(canonical, json))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterInvalid);
        return canonical;
    }
}
