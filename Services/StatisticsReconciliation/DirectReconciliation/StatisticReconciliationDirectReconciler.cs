using System.Collections.Immutable;
using System.Globalization;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.DirectReconciliation;

public sealed class StatisticReconciliationDirectReconciler
{
    private const int MaximumObservations = 99_991;
    private const int MaximumPageSize = 200;
    private const int MaximumPages = 10_000;
    private readonly StatisticReconciliationStatefulComparator _stateful = new();
    private readonly StatisticReconciliationTypedComparator _typed = new();

    public StatisticReconciliationDirectIdentity CreateIdentity(
        string kind,
        string formTemplateId,
        string periodKey,
        string metricId,
        string stableSortKey,
        string? fieldId = null,
        string? tableId = null,
        string? rowId = null,
        string? labelId = null)
    {
        kind = StatisticReconciliationDirectCanonical.Upper(kind, "identity.kind");
        formTemplateId = StatisticReconciliationDirectCanonical.Required(formTemplateId, "identity.formTemplateId");
        periodKey = StatisticReconciliationDirectCanonical.Required(periodKey, "identity.periodKey");
        metricId = StatisticReconciliationDirectCanonical.Required(metricId, "identity.metricId");
        stableSortKey = StatisticReconciliationDirectCanonical.Required(stableSortKey, "identity.stableSortKey");
        fieldId = StatisticReconciliationDirectCanonical.Optional(fieldId, "identity.fieldId");
        tableId = StatisticReconciliationDirectCanonical.Optional(tableId, "identity.tableId");
        rowId = StatisticReconciliationDirectCanonical.Optional(rowId, "identity.rowId");
        labelId = StatisticReconciliationDirectCanonical.Optional(labelId, "identity.labelId");
        var valid = kind switch
        {
            StatisticReconciliationDirectKinds.Field =>
                fieldId is not null && tableId is null && rowId is null && labelId is null,
            StatisticReconciliationDirectKinds.TableMetric =>
                fieldId is null && tableId is not null && rowId is not null && labelId is null,
            StatisticReconciliationDirectKinds.RowLabel =>
                fieldId is null && tableId is not null && rowId is not null && labelId is not null,
            _ => false
        };
        if (!valid)
            throw StatisticReconciliationDirectCanonical.Invalid("identity.shape");
        var identitySha = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_STABLE_IDENTITY_V1",
            kind, formTemplateId, periodKey, metricId,
            fieldId ?? "~", tableId ?? "~", rowId ?? "~", labelId ?? "~",
            stableSortKey);
        return new StatisticReconciliationDirectIdentity(
            kind, formTemplateId, periodKey, metricId,
            fieldId, tableId, rowId, labelId, stableSortKey, identitySha);
    }

    public StatisticReconciliationDirectObservation CreateObservation(
        StatisticReconciliationDirectIdentity identity,
        string atomKind,
        string valueType,
        string valueState,
        string? canonicalValue,
        int decimalScale,
        long reportCount,
        long rowCount,
        long numericValueCount,
        string sourceLineageSha256,
        string? collectionSemantics = null)
    {
        identity = NormalizeIdentity(identity);
        atomKind = StatisticReconciliationDirectCanonical.Upper(atomKind, "observation.atomKind");
        if (atomKind is not (
                StatisticReconciliationDirectAtomKinds.ReportCount or
                StatisticReconciliationDirectAtomKinds.RowCount or
                StatisticReconciliationDirectAtomKinds.Count or
                StatisticReconciliationDirectAtomKinds.NumericValueCount or
                StatisticReconciliationDirectAtomKinds.Sum or
                StatisticReconciliationDirectAtomKinds.Min or
                StatisticReconciliationDirectAtomKinds.Max or
                StatisticReconciliationDirectAtomKinds.Mean or
                StatisticReconciliationDirectAtomKinds.Value))
            throw StatisticReconciliationDirectCanonical.Invalid("observation.atomKind");
        valueType = StatisticReconciliationDirectCanonical.Upper(valueType, "observation.valueType");
        valueState = StatisticReconciliationDirectCanonical.Upper(valueState, "observation.valueState");
        if (valueState == StatisticReconciliationObservationValueStates.Redacted)
            throw StatisticReconciliationDirectCanonical.Invalid("observation.redactedMustRemainPresentationOnly");
        if (reportCount < 0 || rowCount < 0 || numericValueCount < 0 || decimalScale is < 0 or > 28)
            throw StatisticReconciliationDirectCanonical.Invalid("observation.countsOrScale");
        sourceLineageSha256 = StatisticReconciliationDirectCanonical.Sha(sourceLineageSha256, "observation.sourceLineageSha256");

        var stable = StableIdentity(identity);
        try
        {
            _ = _stateful.Compare(
                new StatisticReconciliationStatefulObservation(
                    stable, valueType, valueState, canonicalValue, decimalScale, collectionSemantics),
                new StatisticReconciliationStatefulObservation(
                    stable, valueType, valueState, canonicalValue, decimalScale, collectionSemantics));
        }
        catch (Exception exception) when (exception is StatisticReconciliationTypedComparisonException or
                                           StatisticReconciliationDirectException)
        {
            throw StatisticReconciliationDirectCanonical.Invalid("observation.typedValue");
        }
        if (atomKind != StatisticReconciliationDirectAtomKinds.Value &&
            valueType != StatisticReconciliationStatefulValueTypes.Number)
            throw StatisticReconciliationDirectCanonical.Invalid("observation.numericMetricType");
        if (atomKind is StatisticReconciliationDirectAtomKinds.ReportCount or
            StatisticReconciliationDirectAtomKinds.RowCount or
            StatisticReconciliationDirectAtomKinds.Count or
            StatisticReconciliationDirectAtomKinds.NumericValueCount)
        {
            if (valueState != StatisticReconciliationObservationValueStates.Value || decimalScale != 0 ||
                canonicalValue is null || !long.TryParse(canonicalValue, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                throw StatisticReconciliationDirectCanonical.Invalid("observation.countValue");
        }
        var semanticSha = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_OBSERVATION_V1",
            identity.IdentitySha256, atomKind, valueType, valueState,
            canonicalValue ?? "~", StatisticReconciliationDirectCanonical.I(decimalScale),
            collectionSemantics ?? "~", StatisticReconciliationDirectCanonical.I(reportCount),
            StatisticReconciliationDirectCanonical.I(rowCount),
            StatisticReconciliationDirectCanonical.I(numericValueCount), sourceLineageSha256);
        return new StatisticReconciliationDirectObservation(
            identity, atomKind, valueType, valueState, canonicalValue, decimalScale,
            collectionSemantics, reportCount, rowCount, numericValueCount,
            sourceLineageSha256, semanticSha);
    }

    public StatisticReconciliationDirectFullFilterTotals CreateTotals(
        IEnumerable<(string Name, string ValueType, string CanonicalValue, int DecimalScale)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values.Select(value =>
        {
            var name = StatisticReconciliationDirectCanonical.Upper(value.Name, "totals.name");
            var type = StatisticReconciliationDirectCanonical.Upper(value.ValueType, "totals.valueType");
            var canonical = StatisticReconciliationDirectCanonical.Required(value.CanonicalValue, "totals.canonicalValue");
            var identity = new StatisticReconciliationStableIdentity(
                StatisticReconciliationStableIdentityKinds.Row,
                ["DIRECT_TOTAL", name]);
            try
            {
                _ = _stateful.Compare(
                    new StatisticReconciliationStatefulObservation(identity, type,
                        StatisticReconciliationObservationValueStates.Value, canonical, value.DecimalScale),
                    new StatisticReconciliationStatefulObservation(identity, type,
                        StatisticReconciliationObservationValueStates.Value, canonical, value.DecimalScale));
            }
            catch (StatisticReconciliationTypedComparisonException)
            {
                throw StatisticReconciliationDirectCanonical.Invalid("totals.value");
            }
            return new StatisticReconciliationDirectNamedTotal(
                name, type, canonical, value.DecimalScale,
                StatisticReconciliationDirectCanonical.Hash(
                    "P10_DIRECT_NAMED_TOTAL_V1", name, type, canonical,
                    StatisticReconciliationDirectCanonical.I(value.DecimalScale)));
        }).OrderBy(value => value.Name, StringComparer.Ordinal).ToImmutableArray();
        if (normalized.Length == 0 ||
            normalized.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw StatisticReconciliationDirectCanonical.Invalid("totals.unique");
        return new StatisticReconciliationDirectFullFilterTotals(
            normalized,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_FULL_FILTER_TOTALS_V1",
                normalized.Select(value => value.TotalSemanticSha256).ToArray()));
    }

    public StatisticReconciliationDirectLayerCapture CreateLayer(
        int ordinal,
        string layer,
        string comparisonBindingSha256,
        string ownerGenerationSha256,
        bool evidenceComplete,
        IEnumerable<StatisticReconciliationDirectObservation> observations,
        StatisticReconciliationDirectFullFilterTotals totals)
    {
        if (ordinal < 0 || ordinal >= StatisticReconciliationDirectLayers.Ordered.Length ||
            layer != StatisticReconciliationDirectLayers.Ordered[ordinal])
            throw StatisticReconciliationDirectCanonical.Invalid("layer.order");
        comparisonBindingSha256 = StatisticReconciliationDirectCanonical.Sha(
            comparisonBindingSha256, "layer.comparisonBindingSha256");
        ownerGenerationSha256 = StatisticReconciliationDirectCanonical.Sha(
            ownerGenerationSha256, "layer.ownerGenerationSha256");
        ArgumentNullException.ThrowIfNull(observations);
        var rows = observations.Take(MaximumObservations + 1)
            .Select(NormalizeObservation)
            .OrderBy(value => value.Identity.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
            .ToImmutableArray();
        if (rows.Length is < 1 or > MaximumObservations ||
            rows.Select(Key).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw StatisticReconciliationDirectCanonical.Invalid("layer.observations");
        totals = NormalizeTotals(totals);
        ValidateMeans(rows);
        var semantic = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_LAYER_V1",
            StatisticReconciliationDirectCanonical.I(ordinal), layer,
            comparisonBindingSha256, ownerGenerationSha256,
            evidenceComplete ? "1" : "0", totals.TotalsSemanticSha256,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_LAYER_ROWS_V1",
                rows.Select(value => value.ObservationSemanticSha256).ToArray()));
        return new StatisticReconciliationDirectLayerCapture(
            ordinal, layer, comparisonBindingSha256, ownerGenerationSha256,
            evidenceComplete, rows, totals, semantic);
    }

    public StatisticReconciliationDirectApiPage CreateApiPage(
        StatisticReconciliationDirectLayerCapture apiLayer,
        int page,
        int pageSize,
        string sortDirection,
        int totalPages,
        long totalRows,
        IEnumerable<string> rowIdentitySha256s,
        StatisticReconciliationDirectFullFilterTotals totals)
    {
        apiLayer = NormalizeStandaloneLayer(apiLayer, StatisticReconciliationDirectLayers.Api);
        if (page < 0 || pageSize is < 1 or > MaximumPageSize ||
            totalPages is < 1 or > MaximumPages || totalRows < 0)
            throw new StatisticReconciliationDirectException(
                StatisticReconciliationDirectFailureCodes.PageInvalid, "page.bounds");
        sortDirection = StatisticReconciliationDirectCanonical.Upper(sortDirection, "page.sortDirection");
        if (sortDirection is not ("ASC" or "DESC"))
            throw new StatisticReconciliationDirectException(
                StatisticReconciliationDirectFailureCodes.PageInvalid, "page.sortDirection");
        ArgumentNullException.ThrowIfNull(rowIdentitySha256s);
        var identities = apiLayer.Observations.Select(value => value.Identity)
            .DistinctBy(value => value.IdentitySha256)
            .ToDictionary(value => value.IdentitySha256, StringComparer.Ordinal);
        var normalizedRows = rowIdentitySha256s.Select(identitySha256 =>
        {
            identitySha256 = StatisticReconciliationDirectCanonical.Sha(
                identitySha256, "page.row.identitySha256");
            if (!identities.TryGetValue(identitySha256, out var identity))
                throw new StatisticReconciliationDirectException(
                    StatisticReconciliationDirectFailureCodes.PageInvalid,
                    "page.row.identityMissingFromApiLayer");
            return new StatisticReconciliationDirectPageRow(
                identitySha256,
                identity.StableSortKey,
                PageRowSemantic(apiLayer, identitySha256));
        }).ToImmutableArray();
        if (normalizedRows.Length > pageSize || normalizedRows.Select(row => row.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != normalizedRows.Length)
            throw new StatisticReconciliationDirectException(
                StatisticReconciliationDirectFailureCodes.PageInvalid, "page.rows");
        totals = NormalizeTotals(totals);
        var semantic = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_API_PAGE_V1", StatisticReconciliationDirectCanonical.I(page),
            StatisticReconciliationDirectCanonical.I(pageSize), sortDirection,
            StatisticReconciliationDirectCanonical.I(totalPages),
            StatisticReconciliationDirectCanonical.I(totalRows), totals.TotalsSemanticSha256,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_API_PAGE_ROWS_V1", normalizedRows.Select(row =>
                    StatisticReconciliationDirectCanonical.Hash(
                        "P10_DIRECT_API_PAGE_ROW_V1", row.IdentitySha256,
                        row.StableSortKey, row.RowSemanticSha256)).ToArray()));
        return new StatisticReconciliationDirectApiPage(
            page, pageSize, sortDirection, totalPages, totalRows,
            normalizedRows, totals, semantic);
    }

    public StatisticReconciliationDirectExportEvidence CreateExportEvidence(
        StatisticReconciliationDirectLayerCapture exportLayer,
        IEnumerable<(string IdentitySha256, string CanonicalRowJson)> rows,
        StatisticReconciliationDirectFullFilterTotals totals)
    {
        exportLayer = NormalizeStandaloneLayer(exportLayer, StatisticReconciliationDirectLayers.Export);
        ArgumentNullException.ThrowIfNull(rows);
        var normalized = rows.Select(value =>
        {
            var identity = StatisticReconciliationDirectCanonical.Sha(value.IdentitySha256, "export.identitySha256");
            if (!exportLayer.Observations.Any(item => item.Identity.IdentitySha256 == identity))
                throw StatisticReconciliationDirectCanonical.Invalid("export.identityMissingFromLayer");
            var json = StatisticReconciliationDirectCanonical.CanonicalJson(value.CanonicalRowJson, "export.canonicalRowJson");
            var observationManifest = ObservationManifest(exportLayer, identity);
            return new StatisticReconciliationDirectExportRow(
                identity, observationManifest, json,
                StatisticReconciliationDirectCanonical.Hash(
                    "P10_DIRECT_EXPORT_ROW_V1", identity, observationManifest, json));
        }).OrderBy(value => value.IdentitySha256, StringComparer.Ordinal).ToImmutableArray();
        if (normalized.Length == 0 || normalized.Select(value => value.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw StatisticReconciliationDirectCanonical.Invalid("export.rows");
        totals = NormalizeTotals(totals);
        var contentSha = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_EXPORT_CONTENT_V1",
            normalized.Select(value => value.RowSemanticSha256).ToArray());
        return new StatisticReconciliationDirectExportEvidence(
            normalized, totals, contentSha,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_EXPORT_EVIDENCE_V1", contentSha, totals.TotalsSemanticSha256));
    }

    public StatisticReconciliationDirectPermissionEvidence CreatePermissionEvidence(
        string state,
        string authorizationSnapshotSha256,
        long rowCountBeforeRedaction,
        long rowCountAfterRedaction)
    {
        state = StatisticReconciliationDirectCanonical.Upper(state, "permission.state");
        if (state is not (
                StatisticReconciliationDirectPermissionStates.AuthorizedDetail or
                StatisticReconciliationDirectPermissionStates.AuthorizedRedacted or
                StatisticReconciliationDirectPermissionStates.Denied) ||
            rowCountBeforeRedaction < 0 || rowCountAfterRedaction < 0 ||
            rowCountAfterRedaction > rowCountBeforeRedaction ||
            state == StatisticReconciliationDirectPermissionStates.AuthorizedDetail &&
                rowCountBeforeRedaction != rowCountAfterRedaction ||
            state == StatisticReconciliationDirectPermissionStates.Denied && rowCountAfterRedaction != 0)
            throw StatisticReconciliationDirectCanonical.Invalid("permission.shape");
        authorizationSnapshotSha256 = StatisticReconciliationDirectCanonical.Sha(
            authorizationSnapshotSha256, "permission.authorizationSnapshotSha256");
        return new StatisticReconciliationDirectPermissionEvidence(
            state, authorizationSnapshotSha256, rowCountBeforeRedaction, rowCountAfterRedaction,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_PERMISSION_V1", state, authorizationSnapshotSha256,
                StatisticReconciliationDirectCanonical.I(rowCountBeforeRedaction),
                StatisticReconciliationDirectCanonical.I(rowCountAfterRedaction)));
    }

    public StatisticReconciliationDirectResult Reconcile(StatisticReconciliationDirectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var permission = NormalizePermission(request.Permission);
        if (permission.State == StatisticReconciliationDirectPermissionStates.Denied)
            return Forbidden(permission);

        var reconciliationId = StatisticReconciliationDirectCanonical.Required(
            request.ReconciliationId, "request.reconciliationId");
        var binding = StatisticReconciliationDirectCanonical.Sha(
            request.ComparisonBindingSha256, "request.comparisonBindingSha256");
        var expectedGenerationId = StatisticReconciliationDirectCanonical.Required(
            request.ExpectedGenerationId, "request.expectedGenerationId");
        var actualGenerationId = StatisticReconciliationDirectCanonical.Required(
            request.ActualGenerationId, "request.actualGenerationId");
        if (expectedGenerationId == actualGenerationId)
            throw StatisticReconciliationDirectCanonical.Invalid("request.independentGenerations");
        var layers = NormalizeLayers(request.Layers);
        var stale = layers.Any(layer => !layer.EvidenceComplete || layer.ComparisonBindingSha256 != binding);
        if (stale)
            return Stale(
                reconciliationId, binding, expectedGenerationId, actualGenerationId,
                permission, layers);

        var expected = layers[0];
        var detailed = permission.State == StatisticReconciliationDirectPermissionStates.AuthorizedDetail;
        var normalizedPages = NormalizePages(layers[4], request.ApiPages);
        var normalizedExport = NormalizeExport(layers[5], request.ExportEvidence);
        var results = layers.Select(layer => CompareLayer(expected, layer, detailed)).ToImmutableArray();
        var paging = ValidatePaging(expected, normalizedPages);
        var export = ValidateExport(expected, normalizedExport);
        var first = results.FirstOrDefault(layer => layer.NonzeroCount > 0 || !layer.TotalsEqual);
        string? earliest = first?.Layer;
        string? root = first is null ? null : Root(first);
        if ((!paging.TotalsStable || !paging.RowsExact) &&
            (first is null || first.Ordinal >= 4))
        {
            earliest = StatisticReconciliationDirectLayers.Api;
            root = StatisticReconciliationDirectRootCauses.Api;
        }
        if (!export && first is null)
        {
            earliest = StatisticReconciliationDirectLayers.Export;
            root = StatisticReconciliationDirectRootCauses.Export;
        }
        var allZero = results.All(layer => layer.NonzeroCount == 0 && layer.TotalsEqual) &&
                      paging.TotalsStable && paging.RowsExact && export;
        var outcome = allZero
            ? StatisticReconciliationDirectOutcomes.Matched
            : StatisticReconciliationDirectOutcomes.Mismatched;
        var manifest = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_EVIDENCE_MANIFEST_V1", binding,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_LAYER_RESULTS_V1", results.Select(value => value.DeltaManifestSha256).ToArray()),
            paging.ManifestSha256, normalizedExport.EvidenceSemanticSha256,
            permission.EvidenceSemanticSha256);
        var semantic = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_RESULT_V1", outcome, reconciliationId, binding,
            expectedGenerationId, actualGenerationId, permission.State,
            detailed ? "1" : "0", allZero ? "1" : "0",
            paging.TotalsStable ? "1" : "0", paging.RowsExact ? "1" : "0",
            export ? "1" : "0", earliest ?? "~", root ?? "~", manifest);
        return new StatisticReconciliationDirectResult(
            outcome, reconciliationId, binding, expectedGenerationId, actualGenerationId,
            permission.State, detailed, true, allZero,
            paging.TotalsStable, paging.RowsExact, export,
            earliest, root, results, manifest, semantic);
    }

    private StatisticReconciliationDirectLayerResult CompareLayer(
        StatisticReconciliationDirectLayerCapture expected,
        StatisticReconciliationDirectLayerCapture actual,
        bool detailed)
    {
        var expectedByKey = expected.Observations.ToDictionary(Key, StringComparer.Ordinal);
        var actualByKey = actual.Observations.ToDictionary(Key, StringComparer.Ordinal);
        var keys = expectedByKey.Keys.Concat(actualByKey.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var deltas = new List<StatisticReconciliationDirectDelta>();
        var comparisonHashes = new List<string>();
        foreach (var key in keys)
        {
            expectedByKey.TryGetValue(key, out var expectedValue);
            actualByKey.TryGetValue(key, out var actualValue);
            var comparison = _stateful.Compare(
                expectedValue is null ? null : Stateful(expectedValue),
                actualValue is null ? null : Stateful(actualValue));
            comparisonHashes.Add(comparison.ComparisonSha256);
            if (!comparison.Equal)
            {
                var identity = expectedValue?.Identity ?? actualValue!.Identity;
                var atomKind = expectedValue?.AtomKind ?? actualValue!.AtomKind;
                deltas.Add(new StatisticReconciliationDirectDelta(
                    actual.Layer, identity.IdentitySha256, atomKind,
                    comparison.DeltaCode, comparison.ComparisonSha256));
            }
        }
        var orderedDeltas = deltas
            .OrderBy(value => DeltaPriority(value.DeltaCode))
            .ThenBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
            .ToImmutableArray();
        var totalsEqual = expected.FullFilterTotals.TotalsSemanticSha256 ==
                          actual.FullFilterTotals.TotalsSemanticSha256;
        var manifest = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_LAYER_DELTA_MANIFEST_V1", actual.Layer,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_LAYER_COMPARISONS_V1", comparisonHashes.ToArray()),
            totalsEqual ? "1" : "0");
        return new StatisticReconciliationDirectLayerResult(
            actual.Ordinal, actual.Layer, keys.Length, orderedDeltas.Length,
            orderedDeltas.Count(value => value.DeltaCode == StatisticReconciliationIdentityDeltaCodes.MissingIdentity),
            orderedDeltas.Count(value => value.DeltaCode == StatisticReconciliationIdentityDeltaCodes.ExtraIdentity),
            totalsEqual, manifest,
            detailed ? orderedDeltas : ImmutableArray<StatisticReconciliationDirectDelta>.Empty);
    }

    private (bool TotalsStable, bool RowsExact, string ManifestSha256) ValidatePaging(
        StatisticReconciliationDirectLayerCapture expected,
        ImmutableArray<StatisticReconciliationDirectApiPage> pages)
    {
        if (pages.IsDefaultOrEmpty)
            throw new StatisticReconciliationDirectException(
                StatisticReconciliationDirectFailureCodes.PageInvalid, "pages.missing");
        var expectedRows = expected.Observations.Select(value => value.Identity)
            .DistinctBy(value => value.IdentitySha256)
            .OrderBy(value => value.StableSortKey, StringComparer.Ordinal)
            .ThenBy(value => value.IdentitySha256, StringComparer.Ordinal).ToArray();
        var groups = pages.GroupBy(page => (page.PageSize, page.SortDirection)).ToArray();
        if (!groups.Any(group => group.Key.PageSize == 1) ||
            !groups.Any(group => group.Key.PageSize == 50) ||
            !groups.Any(group => group.Key.SortDirection == "ASC") ||
            !groups.Any(group => group.Key.SortDirection == "DESC"))
            throw new StatisticReconciliationDirectException(
                StatisticReconciliationDirectFailureCodes.PageInvalid, "pages.requiredVariants");
        var totalsStable = true;
        var rowsExact = true;
        var hashes = new List<string>();
        foreach (var group in groups.OrderBy(value => value.Key.PageSize)
                     .ThenBy(value => value.Key.SortDirection, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(value => value.Page).ToArray();
            var totalPages = Math.Max(1, checked((int)((expectedRows.LongLength + group.Key.PageSize - 1) /
                                                       group.Key.PageSize)));
            if (ordered.Length != totalPages || ordered.Select(value => value.Page)
                    .Where((value, index) => value != index).Any())
                rowsExact = false;
            var fullOrder = group.Key.SortDirection == "ASC"
                ? expectedRows
                : expectedRows.Reverse().ToArray();
            foreach (var page in ordered)
            {
                hashes.Add(page.PageSemanticSha256);
                totalsStable &= page.TotalRows == expectedRows.LongLength &&
                                page.TotalPages == totalPages &&
                                page.FullFilterTotals.TotalsSemanticSha256 ==
                                expected.FullFilterTotals.TotalsSemanticSha256;
                var slice = fullOrder.Skip(page.Page * page.PageSize).Take(page.PageSize).ToArray();
                rowsExact &= page.Rows.Length == slice.Length && page.Rows
                    .Select((row, index) => row.IdentitySha256 == slice[index].IdentitySha256 &&
                                            row.StableSortKey == slice[index].StableSortKey &&
                                            row.RowSemanticSha256 == PageRowSemantic(
                                                expected, slice[index].IdentitySha256))
                    .All(value => value);
            }
        }
        return (totalsStable, rowsExact,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_PAGE_MANIFEST_V1", hashes.ToArray()));
    }

    private static bool ValidateExport(
        StatisticReconciliationDirectLayerCapture expected,
        StatisticReconciliationDirectExportEvidence? export)
    {
        if (export is null)
            throw StatisticReconciliationDirectCanonical.Invalid("export.missing");
        var expectedIds = expected.Observations.Select(value => value.Identity.IdentitySha256)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return export.FullFilterTotals.TotalsSemanticSha256 == expected.FullFilterTotals.TotalsSemanticSha256 &&
               export.Rows.Select(value => value.IdentitySha256).SequenceEqual(expectedIds, StringComparer.Ordinal) &&
               export.Rows.All(value => value.ObservationManifestSha256 ==
                   ObservationManifest(expected, value.IdentitySha256));
    }

    private StatisticReconciliationDirectResult Forbidden(
        StatisticReconciliationDirectPermissionEvidence permission)
    {
        var opaque = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_FORBIDDEN_OPAQUE_V1", permission.EvidenceSemanticSha256);
        return new StatisticReconciliationDirectResult(
            StatisticReconciliationDirectOutcomes.Forbidden,
            "REDACTED", opaque, "REDACTED", "REDACTED", permission.State,
            false, false, false, false, false, false,
            null, null, ImmutableArray<StatisticReconciliationDirectLayerResult>.Empty,
            opaque, StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_FORBIDDEN_RESULT_V1", opaque));
    }

    private static StatisticReconciliationDirectResult Stale(
        string reconciliationId,
        string binding,
        string expectedGenerationId,
        string actualGenerationId,
        StatisticReconciliationDirectPermissionEvidence permission,
        ImmutableArray<StatisticReconciliationDirectLayerCapture> layers)
    {
        var manifest = StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_STALE_MANIFEST_V1", binding,
            StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_STALE_LAYERS_V1", layers.Select(value => value.LayerSemanticSha256).ToArray()));
        return new StatisticReconciliationDirectResult(
            StatisticReconciliationDirectOutcomes.Stale,
            reconciliationId, binding, expectedGenerationId, actualGenerationId,
            permission.State,
            permission.State == StatisticReconciliationDirectPermissionStates.AuthorizedDetail,
            false, false, false, false, false,
            null, null, ImmutableArray<StatisticReconciliationDirectLayerResult>.Empty,
            manifest, StatisticReconciliationDirectCanonical.Hash(
                "P10_DIRECT_STALE_RESULT_V1", reconciliationId, binding, manifest));
    }

    private ImmutableArray<StatisticReconciliationDirectLayerCapture> NormalizeLayers(
        ImmutableArray<StatisticReconciliationDirectLayerCapture> layers)
    {
        if (layers.IsDefault || layers.Length != StatisticReconciliationDirectLayers.Ordered.Length)
            throw StatisticReconciliationDirectCanonical.Invalid("layers.count");
        return layers.Select((value, index) =>
        {
            var normalized = CreateLayer(
                index, StatisticReconciliationDirectLayers.Ordered[index],
                value.ComparisonBindingSha256, value.OwnerGenerationSha256,
                value.EvidenceComplete, value.Observations, value.FullFilterTotals);
            if (normalized.LayerSemanticSha256 != value.LayerSemanticSha256 ||
                value.Ordinal != index || value.Layer != normalized.Layer)
                throw StatisticReconciliationDirectCanonical.Invalid($"layers[{index}].semantic");
            return normalized;
        }).ToImmutableArray();
    }

    private StatisticReconciliationDirectLayerCapture NormalizeStandaloneLayer(
        StatisticReconciliationDirectLayerCapture value,
        string expectedLayer)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Layer != expectedLayer ||
            value.Ordinal != StatisticReconciliationDirectLayers.Ordered.IndexOf(expectedLayer))
            throw StatisticReconciliationDirectCanonical.Invalid("standaloneLayer.identity");
        var normalized = CreateLayer(
            value.Ordinal, value.Layer, value.ComparisonBindingSha256,
            value.OwnerGenerationSha256, value.EvidenceComplete,
            value.Observations, value.FullFilterTotals);
        if (normalized.LayerSemanticSha256 != value.LayerSemanticSha256)
            throw StatisticReconciliationDirectCanonical.Invalid("standaloneLayer.semantic");
        return normalized;
    }

    private ImmutableArray<StatisticReconciliationDirectApiPage> NormalizePages(
        StatisticReconciliationDirectLayerCapture apiLayer,
        ImmutableArray<StatisticReconciliationDirectApiPage> pages)
    {
        if (pages.IsDefaultOrEmpty)
            throw new StatisticReconciliationDirectException(
                StatisticReconciliationDirectFailureCodes.PageInvalid, "pages.missing");
        return pages.Select((value, index) =>
        {
            var normalized = CreateApiPage(
                apiLayer, value.Page, value.PageSize, value.SortDirection,
                value.TotalPages, value.TotalRows,
                value.Rows.Select(row => row.IdentitySha256), value.FullFilterTotals);
            if (normalized.PageSemanticSha256 != value.PageSemanticSha256 ||
                !normalized.Rows.SequenceEqual(value.Rows))
                throw new StatisticReconciliationDirectException(
                    StatisticReconciliationDirectFailureCodes.PageInvalid,
                    $"pages[{index}].semantic");
            return normalized;
        }).ToImmutableArray();
    }

    private StatisticReconciliationDirectExportEvidence NormalizeExport(
        StatisticReconciliationDirectLayerCapture exportLayer,
        StatisticReconciliationDirectExportEvidence? value)
    {
        if (value is null)
            throw StatisticReconciliationDirectCanonical.Invalid("export.missing");
        var normalized = CreateExportEvidence(
            exportLayer,
            value.Rows.Select(row => (row.IdentitySha256, row.CanonicalRowJson)),
            value.FullFilterTotals);
        if (normalized.EvidenceSemanticSha256 != value.EvidenceSemanticSha256 ||
            normalized.ContentSha256 != value.ContentSha256 ||
            !normalized.Rows.SequenceEqual(value.Rows))
            throw StatisticReconciliationDirectCanonical.Invalid("export.semantic");
        return normalized;
    }

    private StatisticReconciliationDirectPermissionEvidence NormalizePermission(
        StatisticReconciliationDirectPermissionEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = CreatePermissionEvidence(
            value.State, value.AuthorizationSnapshotSha256,
            value.RowCountBeforeRedaction, value.RowCountAfterRedaction);
        if (normalized.EvidenceSemanticSha256 != value.EvidenceSemanticSha256)
            throw StatisticReconciliationDirectCanonical.Invalid("permission.semantic");
        return normalized;
    }

    private StatisticReconciliationDirectObservation NormalizeObservation(
        StatisticReconciliationDirectObservation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = CreateObservation(
            value.Identity, value.AtomKind, value.ValueType, value.ValueState,
            value.CanonicalValue, value.DecimalScale, value.ReportCount,
            value.RowCount, value.NumericValueCount, value.SourceLineageSha256,
            value.CollectionSemantics);
        if (normalized.ObservationSemanticSha256 != value.ObservationSemanticSha256)
            throw StatisticReconciliationDirectCanonical.Invalid("observation.semantic");
        return normalized;
    }

    private StatisticReconciliationDirectFullFilterTotals NormalizeTotals(
        StatisticReconciliationDirectFullFilterTotals value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = CreateTotals(value.Values.Select(item =>
            (item.Name, item.ValueType, item.CanonicalValue, item.DecimalScale)));
        if (normalized.TotalsSemanticSha256 != value.TotalsSemanticSha256)
            throw StatisticReconciliationDirectCanonical.Invalid("totals.semantic");
        return normalized;
    }

    private StatisticReconciliationDirectIdentity NormalizeIdentity(
        StatisticReconciliationDirectIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = CreateIdentity(
            value.Kind, value.FormTemplateId, value.PeriodKey, value.MetricId,
            value.StableSortKey, value.FieldId, value.TableId, value.RowId, value.LabelId);
        if (normalized.IdentitySha256 != value.IdentitySha256)
            throw StatisticReconciliationDirectCanonical.Invalid("identity.semantic");
        return normalized;
    }

    private void ValidateMeans(ImmutableArray<StatisticReconciliationDirectObservation> values)
    {
        foreach (var group in values.GroupBy(value => value.Identity.IdentitySha256, StringComparer.Ordinal))
        {
            var mean = group.SingleOrDefault(value => value.AtomKind == StatisticReconciliationDirectAtomKinds.Mean);
            if (mean is null) continue;
            var sum = group.SingleOrDefault(value => value.AtomKind == StatisticReconciliationDirectAtomKinds.Sum);
            var count = group.SingleOrDefault(value => value.AtomKind ==
                StatisticReconciliationDirectAtomKinds.NumericValueCount);
            if (sum?.CanonicalValue is null || count?.CanonicalValue is null || mean.CanonicalValue is null ||
                sum.ValueState != StatisticReconciliationObservationValueStates.Value ||
                count.ValueState != StatisticReconciliationObservationValueStates.Value ||
                mean.ValueState != StatisticReconciliationObservationValueStates.Value ||
                !long.TryParse(count.CanonicalValue, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var numericCount) || numericCount <= 0 ||
                mean.NumericValueCount != numericCount || sum.NumericValueCount != numericCount ||
                count.NumericValueCount != numericCount)
                throw new StatisticReconciliationDirectException(
                    StatisticReconciliationDirectFailureCodes.MeanInvalid, "mean.companions");
            StatisticReconciliationMeanComparison comparison;
            try
            {
                comparison = _typed.CompareMean(
                    new StatisticReconciliationNumericAggregate(
                        new StatisticReconciliationCanonicalNumber(sum.CanonicalValue, sum.DecimalScale),
                        numericCount),
                    new StatisticReconciliationNumericAggregate(
                        new StatisticReconciliationCanonicalNumber(sum.CanonicalValue, sum.DecimalScale),
                        numericCount));
            }
            catch (StatisticReconciliationTypedComparisonException)
            {
                throw new StatisticReconciliationDirectException(
                    StatisticReconciliationDirectFailureCodes.MeanInvalid, "mean.numeric");
            }
            if (comparison.ExpectedMean.CanonicalValue != mean.CanonicalValue ||
                comparison.ExpectedMean.DecimalScale != mean.DecimalScale)
                throw new StatisticReconciliationDirectException(
                    StatisticReconciliationDirectFailureCodes.MeanInvalid, "mean.value");
        }
    }

    private static StatisticReconciliationStatefulObservation Stateful(
        StatisticReconciliationDirectObservation value)
        => new(StableIdentity(value.Identity), value.ValueType, value.ValueState,
            value.CanonicalValue, value.DecimalScale, value.CollectionSemantics);

    private static StatisticReconciliationStableIdentity StableIdentity(
        StatisticReconciliationDirectIdentity value)
        => new(value.Kind switch
        {
            StatisticReconciliationDirectKinds.TableMetric => StatisticReconciliationStableIdentityKinds.Table,
            StatisticReconciliationDirectKinds.RowLabel => StatisticReconciliationStableIdentityKinds.Label,
            _ => StatisticReconciliationStableIdentityKinds.Row
        },
        [value.Kind, value.FormTemplateId, value.PeriodKey, value.MetricId,
            value.FieldId ?? "~", value.TableId ?? "~", value.RowId ?? "~",
            value.LabelId ?? "~", value.StableSortKey]);

    private static string Key(StatisticReconciliationDirectObservation value)
        => $"{value.Identity.IdentitySha256}:{value.AtomKind}";

    private static string ObservationManifest(
        StatisticReconciliationDirectLayerCapture layer,
        string identitySha256)
        => StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_IDENTITY_OBSERVATION_MANIFEST_V1",
            layer.Observations.Where(value => value.Identity.IdentitySha256 == identitySha256)
                .OrderBy(value => value.AtomKind, StringComparer.Ordinal)
                .Select(value => value.ObservationSemanticSha256).ToArray());

    private static string PageRowSemantic(
        StatisticReconciliationDirectLayerCapture layer,
        string identitySha256)
        => StatisticReconciliationDirectCanonical.Hash(
            "P10_DIRECT_API_ROW_V1", identitySha256,
            ObservationManifest(layer, identitySha256));

    private static int DeltaPriority(string value) => value switch
    {
        StatisticReconciliationIdentityDeltaCodes.MissingIdentity => 0,
        StatisticReconciliationIdentityDeltaCodes.ExtraIdentity => 1,
        _ => 2
    };

    private static string Root(StatisticReconciliationDirectLayerResult result)
    {
        var first = result.DetailedDeltas.FirstOrDefault();
        if (first?.DeltaCode == StatisticReconciliationIdentityDeltaCodes.MissingIdentity)
            return StatisticReconciliationDirectRootCauses.MissingIdentity;
        if (first?.DeltaCode == StatisticReconciliationIdentityDeltaCodes.ExtraIdentity)
            return StatisticReconciliationDirectRootCauses.ExtraIdentity;
        return result.Layer switch
        {
            StatisticReconciliationDirectLayers.SourceLedger => StatisticReconciliationDirectRootCauses.SourceLedger,
            StatisticReconciliationDirectLayers.Projection => StatisticReconciliationDirectRootCauses.Projection,
            StatisticReconciliationDirectLayers.Aggregate => StatisticReconciliationDirectRootCauses.Aggregate,
            StatisticReconciliationDirectLayers.Result => StatisticReconciliationDirectRootCauses.Result,
            StatisticReconciliationDirectLayers.Api => StatisticReconciliationDirectRootCauses.Api,
            _ => StatisticReconciliationDirectRootCauses.Export
        };
    }
}
