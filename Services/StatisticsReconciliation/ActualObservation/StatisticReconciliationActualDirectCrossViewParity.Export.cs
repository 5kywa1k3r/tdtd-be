using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualDirectCrossViewParity
{
    private static ValidatedExport ValidateExport(
        NormalizedPlan plan,
        StatisticReconciliationActualExportManifest? manifest,
        StatisticReconciliationActualExportCapture? capture,
        string periodInstanceKey,
        ExportFilter filter)
    {
        if (manifest is null || capture is null ||
            !Eq(periodInstanceKey, plan.PeriodInstanceKey))
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportCaptureInvalid);
        }
        var canonicalFilter = CanonicalExportFilter(filter);
        var filterSha = StatisticReconciliationActualJson.RawSha256(
            canonicalFilter);
        if (!Eq(manifest.SchemaVersion,
                StatisticReconciliationActualExportParser.RequiredSchemaVersion) ||
            !Eq(manifest.ExportId, plan.ExportId) ||
            !Eq(manifest.AuthorizationSnapshotSha256,
                plan.AuthorizationSnapshotSha256) ||
            !Eq(manifest.ResultKind, plan.ResultKind) ||
            !Eq(manifest.WorkId, plan.WorkId) ||
            !Eq(manifest.ScopeType, AssignmentScope) ||
            !Eq(manifest.ScopeId, plan.ScopeAssignmentId) ||
            !Eq(manifest.ResultId, plan.ExportResultId) ||
            !Eq(manifest.ResultSha256, plan.P9GenerationSha256) ||
            !Eq(manifest.FilterSha256, filterSha) ||
            manifest.RowCount != plan.ExpectedTotalRows ||
            manifest.RowCount < 0 || manifest.RowCount > MaximumRows ||
            manifest.ColumnCount <= 1 || manifest.Columns.IsDefault ||
            manifest.Columns.Length != manifest.ColumnCount)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ResultBindingMismatch);
        }

        string manifestSha;
        try
        {
            manifestSha = new StatisticReconciliationActualExportParser()
                .ComputeManifestSha256(manifest);
        }
        catch (StatisticReconciliationActualObservationException)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportCaptureInvalid);
        }

        if (!Eq(capture.ExportId, manifest.ExportId) ||
            !Eq(capture.Format, manifest.Format) ||
            !Eq(capture.ResultKind, manifest.ResultKind) ||
            !Eq(capture.ResultId, manifest.ResultId) ||
            !Eq(capture.ResultSha256, manifest.ResultSha256) ||
            !Eq(capture.ConfigSha256, manifest.ConfigSha256) ||
            !Eq(capture.SourceSha256, manifest.SourceSha256) ||
            !Eq(capture.FilterSha256, manifest.FilterSha256) ||
            capture.LifecycleRevision != manifest.LifecycleRevision ||
            !Eq(capture.ContentSha256, manifest.ContentSha256) ||
            !Eq(capture.ManifestSha256, manifestSha) ||
            !Eq(capture.OwnerSemanticSha256, manifest.OwnerSemanticSha256) ||
            capture.Headers.IsDefault ||
            !capture.Headers.SequenceEqual(
                manifest.Columns.Select(column => column.Name),
                StringComparer.Ordinal) ||
            capture.Rows.IsDefault || capture.Rows.Length != manifest.RowCount ||
            capture.FullFilterTotals.IsDefault)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportCaptureInvalid);
        }

        var columns = manifest.Columns;
        for (var index = 0; index < columns.Length; index++)
        {
            var column = columns[index] ?? throw F(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .ExportCaptureInvalid);
            if (column.Ordinal != index || string.IsNullOrWhiteSpace(column.Name) ||
                !StatisticReconciliationActualExportValueTypes.IsSupported(
                    column.ValueType) ||
                !StatisticReconciliationActualExportBlankPolicies.IsSupported(
                    column.BlankPolicy))
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ExportCaptureInvalid);
            }
        }
        if (!Eq(columns[0].Name, "ordinal") ||
            !Eq(columns[0].ValueType,
                StatisticReconciliationActualExportValueTypes.Integer) ||
            !Eq(columns[0].BlankPolicy,
                StatisticReconciliationActualExportBlankPolicies.Forbidden) ||
            columns[0].IsFullFilterTotal ||
            columns.Select(column => column.Name)
                .Distinct(StringComparer.Ordinal).Count() != columns.Length)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportCaptureInvalid);
        }

        var rows = ImmutableArray.CreateBuilder<ExportSemanticRow>(
            capture.Rows.Length);
        for (var rowIndex = 0; rowIndex < capture.Rows.Length; rowIndex++)
        {
            var row = capture.Rows[rowIndex] ?? throw F(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .ExportRowInvalid);
            if (row.Ordinal != rowIndex + 1 || row.Cells.IsDefault ||
                row.Cells.Length != columns.Length)
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ExportRowInvalid);
            }
            var cells = ImmutableArray.CreateBuilder<ExportSemanticCell>(
                row.Cells.Length);
            for (var columnIndex = 0; columnIndex < row.Cells.Length;
                 columnIndex++)
            {
                var cell = ValidateExportCell(
                    columns[columnIndex], row.Cells[columnIndex]);
                cells.Add(cell);
            }
            var immutableCells = cells.MoveToImmutable();
            if (!Eq(immutableCells[0].ValueState,
                    StatisticReconciliationActualExportValueStates.Value) ||
                !Eq(immutableCells[0].CanonicalValue, I(rowIndex + 1)))
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ExportRowInvalid);
            }
            var rowSha = HS(
                "P10_ACTUAL_EXPORT_ROW_V1",
                immutableCells.Select(cell => cell.SemanticSha256));
            if (!Eq(row.RowSemanticSha256, rowSha))
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ExportRowInvalid);
            rows.Add(new(row.Ordinal, immutableCells, rowSha));
        }
        var immutableRows = rows.MoveToImmutable();

        var totals = ValidateExportTotals(
            manifest, immutableRows, capture.FullFilterTotals);
        var rowsSha = HS(
            "P10_ACTUAL_EXPORT_ROWS_V1",
            immutableRows.Select(row => row.SemanticSha256));
        var totalsSha = HS(
            "P10_ACTUAL_EXPORT_TOTALS_V1",
            totals.Select(total => total.SemanticSha256));
        if (!Eq(capture.RowsSemanticSha256, rowsSha) ||
            !Eq(capture.TotalsSemanticSha256, totalsSha))
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportCaptureInvalid);
        }
        var captureSha = H(
            "P10_ACTUAL_EXPORT_CAPTURE_V1",
            manifest.ExportId,
            manifest.Format,
            manifest.ResultKind,
            manifest.ResultId,
            manifest.ResultSha256,
            manifest.ConfigSha256,
            manifest.SourceSha256,
            manifest.FilterSha256,
            I(manifest.LifecycleRevision),
            manifest.ContentSha256,
            manifestSha,
            manifest.OwnerSemanticSha256,
            rowsSha,
            totalsSha);
        if (!Eq(capture.CaptureSemanticSha256, captureSha))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportCaptureInvalid);

        var partitionSha = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_EXPORT_PARTITION_V1",
            manifestSha,
            captureSha,
            rowsSha,
            I(immutableRows.Length),
            totalsSha,
            canonicalFilter,
            filterSha,
            periodInstanceKey);
        return new(
            columns,
            immutableRows,
            totals,
            rowsSha,
            totalsSha,
            partitionSha,
            manifestSha,
            captureSha,
            canonicalFilter,
            filterSha);
    }

    private static ExportSemanticCell ValidateExportCell(
        StatisticReconciliationActualExportColumnContract column,
        StatisticReconciliationActualExportCellObservation? value)
    {
        if (value is null || value.ColumnOrdinal != column.Ordinal ||
            !Eq(value.ColumnName, column.Name) ||
            !Eq(value.ValueType, column.ValueType) ||
            value.DecimalScale < 0 || value.DecimalScale > 28)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportRowInvalid);
        }
        var state = ExactUpper(value.ValueState);
        if (state is not (
                StatisticReconciliationActualExportValueStates.Value or
                StatisticReconciliationActualExportValueStates.Null or
                StatisticReconciliationActualExportValueStates.Empty))
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportRowInvalid);
        }
        if (state != StatisticReconciliationActualExportValueStates.Value)
        {
            var expectedPolicy = state ==
                StatisticReconciliationActualExportValueStates.Null
                ? StatisticReconciliationActualExportBlankPolicies.Null
                : StatisticReconciliationActualExportBlankPolicies.Empty;
            if (!Eq(column.BlankPolicy, expectedPolicy) ||
                value.CanonicalValue.Length != 0 || value.DecimalScale != 0 ||
                value.FormulaNeutralized)
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ExportRowInvalid);
            }
        }
        else
            ValidateExportCanonicalValue(value);

        var sha = H(
            "P10_ACTUAL_EXPORT_CELL_V1",
            I(column.Ordinal),
            column.Name,
            column.ValueType,
            state,
            value.CanonicalValue,
            I(value.DecimalScale),
            value.FormulaNeutralized ? "true" : "false");
        if (!Eq(value.CellSemanticSha256, sha))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportRowInvalid);
        return new(
            column.Ordinal,
            column.Name,
            column.ValueType,
            state,
            value.CanonicalValue,
            value.DecimalScale,
            value.FormulaNeutralized,
            sha);
    }

    private static void ValidateExportCanonicalValue(
        StatisticReconciliationActualExportCellObservation value)
    {
        switch (value.ValueType)
        {
            case StatisticReconciliationActualExportValueTypes.Integer:
                if (!long.TryParse(value.CanonicalValue,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var integer) ||
                    !Eq(value.CanonicalValue, I(integer)) ||
                    value.DecimalScale != 0 || value.FormulaNeutralized)
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ExportRowInvalid);
                break;
            case StatisticReconciliationActualExportValueTypes.Decimal:
                if (!CanonicalDecimal(value.CanonicalValue,
                        out var scale) || scale != value.DecimalScale ||
                    value.FormulaNeutralized)
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ExportRowInvalid);
                break;
            case StatisticReconciliationActualExportValueTypes.Boolean:
                if (value.CanonicalValue is not ("true" or "false") ||
                    value.DecimalScale != 0 || value.FormulaNeutralized)
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ExportRowInvalid);
                break;
            case StatisticReconciliationActualExportValueTypes.UtcInstant:
                if (!DateTimeOffset.TryParseExact(
                        value.CanonicalValue,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var instant) || instant.Offset != TimeSpan.Zero ||
                    !Eq(value.CanonicalValue,
                        instant.UtcDateTime.ToString(
                            "O", CultureInfo.InvariantCulture)) ||
                    value.DecimalScale != 0 || value.FormulaNeutralized)
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ExportRowInvalid);
                break;
            case StatisticReconciliationActualExportValueTypes.Json:
                using (var document = StatisticReconciliationActualJson.ParseStrict(
                           value.CanonicalValue,
                           "PARITY_EXPORT_JSON_CELL"))
                {
                    if (!Eq(value.CanonicalValue,
                            StatisticReconciliationActualJson.Canonicalize(
                                document.RootElement)) ||
                        value.DecimalScale != 0 || value.FormulaNeutralized)
                        throw F(
                            StatisticReconciliationActualDirectCrossViewParityFailures
                                .ExportRowInvalid);
                }
                break;
            case StatisticReconciliationActualExportValueTypes.Text:
                var neutralized = value.CanonicalValue.Length > 1 &&
                    value.CanonicalValue[0] == '\'' &&
                    value.CanonicalValue[1] is '=' or '+' or '-' or '@';
                var unsafeUnescaped = value.CanonicalValue.Length > 0 &&
                    value.CanonicalValue[0] is '=' or '+' or '-' or '@';
                if (value.DecimalScale != 0 || unsafeUnescaped ||
                    value.FormulaNeutralized != neutralized)
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ExportRowInvalid);
                break;
            default:
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ExportRowInvalid);
        }
    }

    private static bool CanonicalDecimal(string value, out int scale)
    {
        scale = 0;
        var index = value.Length > 0 && value[0] == '-' ? 1 : 0;
        if (index >= value.Length || !char.IsAsciiDigit(value[index]))
            return false;
        var integerStart = index;
        while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
        if (index - integerStart > 1 && value[integerStart] == '0')
            return false;
        if (index < value.Length)
        {
            if (value[index++] != '.' || index >= value.Length)
                return false;
            var start = index;
            while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
            scale = index - start;
        }
        if (index != value.Length || !decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number))
            return false;
        return Eq(
            value,
            number.ToString(scale == 0 ? "0" :
                $"0.{new string('0', scale)}", CultureInfo.InvariantCulture));
    }

    private static ImmutableArray<ExportSemanticTotal> ValidateExportTotals(
        StatisticReconciliationActualExportManifest manifest,
        ImmutableArray<ExportSemanticRow> rows,
        ImmutableArray<StatisticReconciliationActualExportTotalObservation> input)
    {
        var expected = new Dictionary<string, ExportSemanticTotal>(
            StringComparer.Ordinal)
        {
            ["columnCount"] = ExportTotal(
                "columnCount",
                StatisticReconciliationActualExportValueTypes.Integer,
                I(manifest.ColumnCount), 0),
            ["rowCount"] = ExportTotal(
                "rowCount",
                StatisticReconciliationActualExportValueTypes.Integer,
                I(manifest.RowCount), 0)
        };
        foreach (var column in manifest.Columns.Where(value =>
                     value.IsFullFilterTotal))
        {
            if (rows.Length == 0)
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .TotalsMismatch);
            var first = rows[0].Cells[column.Ordinal];
            if (first.ValueState !=
                    StatisticReconciliationActualExportValueStates.Value ||
                rows.Any(row => !SameExportValue(
                    first, row.Cells[column.Ordinal])) ||
                !expected.TryAdd(column.Name, ExportTotal(
                    column.Name,
                    first.ValueType,
                    first.CanonicalValue,
                    first.DecimalScale)))
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .TotalsMismatch);
            }
        }
        var ordered = expected.Values.OrderBy(value => value.Name,
            StringComparer.Ordinal).ToImmutableArray();
        if (input.Length != ordered.Length)
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .TotalsMismatch);
        for (var index = 0; index < ordered.Length; index++)
        {
            var actual = input[index] ?? throw F(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .TotalsMismatch);
            var item = ordered[index];
            if (!Eq(actual.Name, item.Name) ||
                !Eq(actual.ValueType, item.ValueType) ||
                !Eq(actual.ValueState, item.ValueState) ||
                !Eq(actual.CanonicalValue, item.CanonicalValue) ||
                actual.DecimalScale != item.DecimalScale ||
                !Eq(actual.TotalSemanticSha256, item.SemanticSha256))
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .TotalsMismatch);
            }
        }
        return ordered;
    }

    private static ExportSemanticTotal ExportTotal(
        string name, string valueType, string canonical, int scale)
        => new(
            name,
            valueType,
            StatisticReconciliationActualExportValueStates.Value,
            canonical,
            scale,
            H("P10_ACTUAL_EXPORT_TOTAL_V1",
                name,
                valueType,
                StatisticReconciliationActualExportValueStates.Value,
                canonical,
                I(scale)));

    private static bool SameExportValue(
        ExportSemanticCell left,
        ExportSemanticCell right)
        => Eq(left.ValueType, right.ValueType) &&
           Eq(left.ValueState, right.ValueState) &&
           Eq(left.CanonicalValue, right.CanonicalValue) &&
           left.DecimalScale == right.DecimalScale;

    private static string CanonicalExportFilter(ExportFilter value)
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            blockId = value.BlockId,
            bucketKey = value.BucketKey,
            dynamicFormTemplateId = value.DynamicFormTemplateId,
            fieldId = value.FieldId,
            fieldKey = value.FieldKey,
            labelCode = value.LabelCode,
            metricKey = value.MetricKey,
            periodKey = value.PeriodKey
        });
        return StatisticReconciliationActualJson.Canonicalize(element);
    }

    private sealed record ExportSemanticCell(
        int Ordinal,
        string Name,
        string ValueType,
        string ValueState,
        string CanonicalValue,
        int DecimalScale,
        bool FormulaNeutralized,
        string SemanticSha256);

    private sealed record ExportSemanticRow(
        int Ordinal,
        ImmutableArray<ExportSemanticCell> Cells,
        string SemanticSha256);

    private sealed record ExportSemanticTotal(
        string Name,
        string ValueType,
        string ValueState,
        string CanonicalValue,
        int DecimalScale,
        string SemanticSha256);

    private sealed record ValidatedExport(
        ImmutableArray<StatisticReconciliationActualExportColumnContract> Columns,
        ImmutableArray<ExportSemanticRow> Rows,
        ImmutableArray<ExportSemanticTotal> Totals,
        string RowsSha256,
        string TotalsSha256,
        string PartitionSha256,
        string ManifestSha256,
        string CaptureSha256,
        string CanonicalFilterJson,
        string FilterSha256);
}
