using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using tdtd_be.DTOs.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Production, read-only mapping from already captured P9 owner observations to
/// P10's neutral actual tuple. Explicit owner channels are normalized directly.
/// Owner payloads which do not expose a complete neutral metric identity are
/// retained under the OWNER_OPAQUE namespace, so their exact bytes remain in
/// the immutable generation without accidentally joining an expected metric.
/// </summary>
internal sealed class StatisticReconciliationActualAdapterTypedMapper
    : IStatisticReconciliationActualAdapterTypedMapper
{
    private const string OpaquePrefix = "OWNER_OPAQUE";
    private const string ExplicitPrefix = "OWNER_EXPLICIT";
    private static readonly JsonSerializerOptions DirectViewJson = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ImmutableArray<StatisticReconciliationActualTypedObservation> MapDirect(
        ActualDirectProjectionCapture capture,
        string ownerId,
        string ownerVersionSha256)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var samples = new List<DirectSample>();
        var pending = new List<PendingObservation>();

        foreach (var row in capture.FieldRows)
        {
            var labels = ExactDistinct(
                row.StatisticLabelCodes,
                "DIRECT_FIELD_METRIC_LABEL_DUPLICATE");
            if (labels.Length == 0)
            {
                pending.Add(Opaque(
                    DirectFieldShape(row, OpaqueMetric(
                        "DIRECT_FIELD", row.FieldId, row.FieldKey)),
                    JsonSerializer.Serialize(row),
                    row.SemanticSha256));
                continue;
            }

            foreach (var metricId in labels)
            {
                var shape = DirectFieldShape(row, metricId);
                if (TryDirectChannel(
                        row.TypedValue,
                        row.FieldType,
                        allowFullDate: false,
                        out var channel))
                {
                    samples.Add(new DirectSample(
                        shape,
                        row.ReportId,
                        channel!,
                        row.OccurrenceIdentitySha256));
                }
                else
                {
                    pending.Add(Opaque(
                        shape with
                        {
                            MetricId = OpaqueMetric(
                                "DIRECT_FIELD_VALUE", metricId, row.FieldId)
                        },
                        JsonSerializer.Serialize(row),
                        row.SemanticSha256));
                }
            }
        }

        foreach (var row in capture.TableMetricRows)
        {
            var metricId = row.ConceptCode;
            var shape = DirectTableShape(
                row,
                metricId ?? OpaqueMetric(
                    "DIRECT_TABLE", row.BlockId, row.MetricKey));
            if (metricId is not null && TryDirectChannel(
                    row.TypedValue,
                    row.DataType,
                    allowFullDate: row.DataType == "FULL_DATE",
                    out var channel))
            {
                samples.Add(new DirectSample(
                    shape,
                    row.ReportId,
                    channel!,
                    row.OccurrenceIdentitySha256));
            }
            else
            {
                pending.Add(Opaque(
                    shape with
                    {
                        MetricId = OpaqueMetric(
                            "DIRECT_TABLE_VALUE",
                            metricId,
                            row.BlockId,
                            row.MetricKey)
                    },
                    JsonSerializer.Serialize(row),
                    row.SemanticSha256));
            }
        }

        foreach (var row in capture.RowLabelRows)
        {
            var shape = new MetricShape(
                "DIRECT",
                "ROW_LABEL",
                row.LabelCode,
                null,
                row.BlockId,
                row.RowKey,
                row.LabelCode,
                null,
                null,
                null,
                null,
                null,
                null,
                row.PeriodKey);
            if (!TryDirectChannel(
                    row.TypedValue,
                    "ROW_LABEL",
                    allowFullDate: false,
                    out var channel))
            {
                pending.Add(Opaque(
                    shape with
                    {
                        MetricId = OpaqueMetric(
                            "DIRECT_ROW_LABEL_VALUE",
                            row.BlockId,
                            row.RowKey,
                            row.LabelCode)
                    },
                    JsonSerializer.Serialize(row),
                    row.SemanticSha256));
                continue;
            }
            samples.Add(new DirectSample(
                shape,
                row.ReportId,
                channel!,
                row.OccurrenceIdentitySha256));
        }

        AppendDirectGroups(samples, pending);
        return Materialize(
            StatisticReconciliationActualCoherentLayers.DirectProjection,
            ownerId,
            ownerVersionSha256,
            pending);
    }

    public ImmutableArray<StatisticReconciliationActualTypedObservation> MapAggregate(
        ActualAggregateCapture capture,
        string scopeAssignmentId,
        string ownerId,
        string ownerVersionSha256)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (string.IsNullOrWhiteSpace(scopeAssignmentId))
            throw Fail("AGGREGATE_SCOPE_ASSIGNMENT_ID_INVALID");
        scopeAssignmentId = scopeAssignmentId.Trim();
        var pending = new List<PendingObservation>();

        foreach (var row in capture.FieldRows.Where(row =>
                     row.ScopeType == "ASSIGNMENT" &&
                     row.ScopeId == scopeAssignmentId))
        {
            var labels = ExactDistinct(
                row.StatisticLabelCodes,
                "AGGREGATE_FIELD_METRIC_LABEL_DUPLICATE");
            if (labels.Length == 0)
            {
                pending.Add(Opaque(
                    AggregateFieldShape(row, OpaqueMetric(
                        "AGGREGATE_FIELD", row.FieldId, row.FieldKey)),
                    JsonSerializer.Serialize(row),
                    row.SemanticSha256));
                continue;
            }
            foreach (var metricId in labels)
            {
                AppendAggregateMetric(
                    pending,
                    AggregateFieldShape(row, metricId),
                    row.FieldType,
                    row.BucketKey,
                    row.Counts,
                    row.Measures,
                    row.SemanticSha256);
            }
        }

        foreach (var row in capture.TableMetricRows.Where(row =>
                     row.ScopeType == "ASSIGNMENT" &&
                     row.ScopeId == scopeAssignmentId))
        {
            if (row.MetricLabelCode is null)
            {
                pending.Add(Opaque(
                    AggregateTableShape(row, OpaqueMetric(
                        "AGGREGATE_TABLE", row.BlockId, row.MetricKey)),
                    JsonSerializer.Serialize(row),
                    row.SemanticSha256));
                continue;
            }
            AppendAggregateMetric(
                pending,
                AggregateTableShape(row, row.MetricLabelCode),
                row.DataType,
                row.BucketKey,
                row.Counts,
                row.Measures,
                row.SemanticSha256);
        }

        foreach (var row in capture.RowLabelRows.Where(row =>
                     row.ScopeType == "ASSIGNMENT" &&
                     row.ScopeId == scopeAssignmentId))
        {
            var syntheticRowId = OwnerId(
                "AGGREGATE_ROW_LABEL_ROWSET",
                row.ScopeType,
                row.ScopeId,
                row.BlockId,
                row.LabelCode,
                row.Time.PeriodKey);
            pending.Add(Opaque(
                new MetricShape(
                    "DIRECT",
                    "ROW_LABEL",
                    OpaqueMetric(
                        "AGGREGATE_ROW_LABEL",
                        row.BlockId,
                        row.LabelCode,
                        row.ScopeType,
                        row.ScopeId),
                    null,
                    row.BlockId,
                    syntheticRowId,
                    row.LabelCode,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    row.Time.PeriodKey),
                JsonSerializer.Serialize(row),
                row.SemanticSha256));
        }

        return Materialize(
            StatisticReconciliationActualCoherentLayers.Aggregate,
            ownerId,
            ownerVersionSha256,
            pending);
    }

    public ImmutableArray<StatisticReconciliationActualTypedObservation> MapBasic(
        ActualBasicResultObservation capture,
        string ownerId,
        string ownerVersionSha256)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var pending = new List<PendingObservation>(capture.ResultItems.Length);
        foreach (var item in capture.ResultItems)
        {
            var itemIdentity = OwnerId(
                "BASIC_ITEM",
                item.Section,
                item.IdentityHint,
                StatisticReconciliationActualCanonical.Integer(item.OwnerOrdinal));
            pending.Add(Opaque(
                BasicShape(capture.Boundary, itemIdentity),
                item.CanonicalJson,
                item.SemanticSha256));
        }
        return Materialize(
            StatisticReconciliationActualCoherentLayers.Basic,
            ownerId,
            ownerVersionSha256,
            pending);
    }

    public ImmutableArray<StatisticReconciliationActualTypedObservation> MapAdvanced(
        ActualAdvancedCapture capture,
        string ownerId,
        string ownerVersionSha256)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var pending = new List<PendingObservation>();
        foreach (var node in capture.Nodes)
        {
            var nodeFieldId = OwnerId(
                "ADVANCED_NODE",
                node.OwnerNodeId,
                node.Grain,
                node.GrainKey);
            pending.Add(Opaque(
                new MetricShape(
                    "ADVANCED",
                    "FIELD",
                    OpaqueMetric(
                        "ADVANCED_NODE", node.OwnerNodeId, node.Grain, node.GrainKey),
                    nodeFieldId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    node.Grain,
                    null,
                    node.GrainKey),
                JsonSerializer.Serialize(new
                {
                    node.ValueJson,
                    node.StoredValueSha256,
                    node.ObservedValueSha256,
                    node.CanonicalValueSha256,
                    node.OwnerState
                }),
                node.SemanticSha256));

            if (node.Value is null)
                continue;
            foreach (var field in node.Value.Fields)
            {
                var metricId = $"{ExplicitPrefix}:ADVANCED:" +
                               StatisticReconciliationActualCanonical.Hash(
                                   "P10_ACTUAL_MAPPER_ADVANCED_METRIC_V1",
                                   field.FieldKey,
                                   field.Method);
                var shape = new MetricShape(
                    "ADVANCED",
                    "FIELD",
                    metricId,
                    field.FieldId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    node.Grain,
                    null,
                    node.GrainKey);
                var numericCount = IsNumber(field.DataType)
                    ? field.ValueCount
                    : 0;
                AddCount(
                    pending,
                    shape,
                    "REPORT_COUNT",
                    field.SourceReportCount,
                    field.SourceReportCount,
                    0,
                    numericCount,
                    field.SemanticSha256);
                AddCount(
                    pending,
                    shape,
                    "COUNT",
                    field.ValueCount,
                    field.SourceReportCount,
                    0,
                    numericCount,
                    field.SemanticSha256);
                AddCount(
                    pending,
                    shape,
                    "NUMERIC_VALUE_COUNT",
                    numericCount,
                    field.SourceReportCount,
                    0,
                    numericCount,
                    field.SemanticSha256);
            }
        }
        return Materialize(
            StatisticReconciliationActualCoherentLayers.Advanced,
            ownerId,
            ownerVersionSha256,
            pending);
    }

    public ImmutableArray<StatisticReconciliationActualTypedObservation> MapDiff(
        ActualP9DiffCapture capture,
        string ownerId,
        string ownerVersionSha256)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var pending = new List<PendingObservation>();
        var left = capture.Direction == "RIGHT_TO_LEFT"
            ? new DiffSide(
                "LEFT",
                "RIGHT",
                capture.RightConceptKind,
                capture.RightConceptKey,
                capture.RightConceptCode,
                capture.RightPeriodCanonicalJson)
            : new DiffSide(
                "LEFT",
                "LEFT",
                capture.LeftConceptKind,
                capture.LeftConceptKey,
                capture.LeftConceptCode,
                capture.LeftPeriodCanonicalJson);
        var right = capture.Direction == "RIGHT_TO_LEFT"
            ? new DiffSide(
                "RIGHT",
                "LEFT",
                capture.LeftConceptKind,
                capture.LeftConceptKey,
                capture.LeftConceptCode,
                capture.LeftPeriodCanonicalJson)
            : new DiffSide(
                "RIGHT",
                "RIGHT",
                capture.RightConceptKind,
                capture.RightConceptKey,
                capture.RightConceptCode,
                capture.RightPeriodCanonicalJson);

        foreach (var row in capture.Rows)
        {
            AppendDiffValue(pending, capture, row, left, row.Left);
            AppendDiffValue(pending, capture, row, right, row.Right);

            var effective = DiffShape(
                row,
                left,
                OpaqueMetric(
                    "P9_DIFF_ROW_VERDICT",
                    row.OwnerRowId,
                    row.Key,
                    row.ConceptKind,
                    row.ConceptKey));
            pending.Add(Opaque(
                effective with
                {
                    PeriodKey = $"{ExplicitPrefix}:P9_DIFF_PERIOD_PAIR:" +
                                StatisticReconciliationActualCanonical.Hash(
                                    "P10_ACTUAL_MAPPER_DIFF_PERIOD_PAIR_V1",
                                    capture.LeftPeriodCanonicalJson,
                                    capture.RightPeriodCanonicalJson)
                },
                JsonSerializer.Serialize(row),
                row.SemanticSha256));

            if (row.P9NumericDelta.HasValue)
            {
                var deltaShape = effective with
                {
                    MetricId = $"{ExplicitPrefix}:P9_DIFF:DELTA:" +
                               StatisticReconciliationActualCanonical.Hash(
                                   "P10_ACTUAL_MAPPER_DIFF_DELTA_METRIC_V1",
                                   row.ConceptKind,
                                   row.ConceptKey,
                                   row.Key)
                };
                var canonical = StatisticReconciliationActualCanonical.Number(
                    row.P9NumericDelta.Value);
                pending.Add(new PendingObservation(
                    deltaShape,
                    "SUM",
                    "NUMBER",
                    canonical,
                    DecimalScale(canonical),
                    1,
                    DiffReportCount(capture, left.OwnerSide),
                    1,
                    1,
                    row.SemanticSha256));
            }
        }

        return Materialize(
            StatisticReconciliationActualCoherentLayers.Diff,
            ownerId,
            ownerVersionSha256,
            pending);
    }

    public ImmutableArray<StatisticReconciliationActualTypedObservation> MapApi(
        StatisticReconciliationActualApiCapture capture,
        string ownerId,
        string ownerVersionSha256)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var pending = new List<PendingObservation>();
        var period = $"{OpaquePrefix}:API_FILTER:{capture.FilterSha256}";

        foreach (var group in capture.Pages
                     .SelectMany(page => page.FullFilterTotals)
                     .GroupBy(total => total.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var values = group.Distinct().ToArray();
            if (values.Length != 1)
                throw Fail("API_TOTAL_NOT_STABLE");
            var total = values[0];
            var identity = OwnerId(
                "API_TOTAL", capture.Surface, total.Name);
            pending.Add(Opaque(
                new MetricShape(
                    "API",
                    "FIELD",
                    OpaqueMetric("API_TOTAL", capture.Surface, total.Name),
                    identity,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    period),
                JsonSerializer.Serialize(total),
                identity));
        }

        foreach (var group in capture.Pages
                     .SelectMany(page => page.Rows)
                     .GroupBy(
                         row => new { row.AbsoluteOrdinal, row.Identity })
                     .OrderBy(group => group.Key.AbsoluteOrdinal)
                     .ThenBy(group => group.Key.Identity, StringComparer.Ordinal))
        {
            var values = group.Distinct().ToArray();
            if (values.Length != 1)
                throw Fail("API_OVERLAP_ROW_NOT_STABLE");
            var row = values[0];
            var tableId = OwnerId(
                "API_SURFACE", capture.Surface, capture.RouteId);
            var rowId = OwnerId(
                "API_ROW", row.Identity,
                StatisticReconciliationActualCanonical.Integer(row.AbsoluteOrdinal));
            var labelId = OwnerId("API_ROW_LABEL", capture.RouteId);
            pending.Add(Opaque(
                new MetricShape(
                    "API",
                    "ROW_LABEL",
                    OpaqueMetric(
                        "API_ROW",
                        capture.Surface,
                        row.Identity,
                        StatisticReconciliationActualCanonical.Integer(
                            row.AbsoluteOrdinal)),
                    null,
                    tableId,
                    rowId,
                    labelId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    period),
                row.CanonicalRowJson,
                row.RowSemanticSha256));

            if (capture.Surface ==
                StatisticReconciliationActualApiSurfaces.DirectField)
            {
                AppendDirectFieldView(
                    pending,
                    DirectFieldApiRow(capture, row),
                    row.RowSemanticSha256);
            }
        }

        return Materialize(
            StatisticReconciliationActualCoherentLayers.Api,
            ownerId,
            ownerVersionSha256,
            pending);
    }

    public ImmutableArray<StatisticReconciliationActualTypedObservation> MapExport(
        StatisticReconciliationActualExportCapture capture,
        string ownerId,
        string ownerVersionSha256)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var pending = new List<PendingObservation>();
        var period = $"{OpaquePrefix}:EXPORT_FILTER:{capture.FilterSha256}";
        var tableId = OwnerId(
            "EXPORT_RESULT", capture.ResultKind, capture.ResultId);

        foreach (var row in capture.Rows.OrderBy(item => item.Ordinal))
        {
            foreach (var cell in row.Cells.OrderBy(item => item.ColumnOrdinal))
            {
                var rowId = OwnerId(
                    "EXPORT_ROW",
                    capture.ResultKind,
                    StatisticReconciliationActualCanonical.Integer(row.Ordinal));
                var labelId = OwnerId(
                    "EXPORT_COLUMN",
                    StatisticReconciliationActualCanonical.Integer(
                        cell.ColumnOrdinal),
                    cell.ColumnName);
                pending.Add(Opaque(
                    new MetricShape(
                        "EXPORT",
                        "ROW_LABEL",
                        OpaqueMetric(
                            "EXPORT_CELL",
                            capture.ResultKind,
                            StatisticReconciliationActualCanonical.Integer(
                                row.Ordinal),
                            StatisticReconciliationActualCanonical.Integer(
                                cell.ColumnOrdinal),
                            cell.ColumnName),
                        null,
                        tableId,
                        rowId,
                        labelId,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        period),
                    JsonSerializer.Serialize(cell),
                    cell.CellSemanticSha256));
            }

            if (capture.ResultKind ==
                tdtd_be.Models.Statistics.StatRunExportResultKinds.DirectField)
            {
                AppendDirectFieldView(
                    pending,
                    DirectFieldExportRow(capture, row),
                    row.RowSemanticSha256);
            }
        }

        foreach (var total in capture.FullFilterTotals
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var identity = OwnerId(
                "EXPORT_TOTAL", capture.ResultKind, total.Name);
            pending.Add(Opaque(
                new MetricShape(
                    "EXPORT",
                    "FIELD",
                    OpaqueMetric(
                        "EXPORT_TOTAL", capture.ResultKind, total.Name),
                    identity,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    period),
                JsonSerializer.Serialize(total),
                total.TotalSemanticSha256));
        }

        return Materialize(
            StatisticReconciliationActualCoherentLayers.Export,
            ownerId,
            ownerVersionSha256,
            pending);
    }

    private static FieldStatisticSummaryRow DirectFieldApiRow(
        StatisticReconciliationActualApiCapture capture,
        StatisticReconciliationActualApiRowObservation observed)
    {
        FieldStatisticSummaryRow row;
        try
        {
            row = JsonSerializer.Deserialize<FieldStatisticSummaryRow>(
                      observed.CanonicalRowJson,
                      DirectViewJson) ??
                  throw Fail("API_DIRECT_FIELD_ROW_REQUIRED");
        }
        catch (JsonException error)
        {
            throw Fail(
                $"API_DIRECT_FIELD_ROW_INVALID:{error.GetType().Name}");
        }
        RequireDirectFieldBoundary(
            row,
            capture.WorkId,
            capture.ScopeAssignmentId,
            capture.DynamicFormTemplateId,
            "API");
        return row;
    }

    private static FieldStatisticSummaryRow DirectFieldExportRow(
        StatisticReconciliationActualExportCapture capture,
        StatisticReconciliationActualExportRowObservation observed)
    {
        if (capture.Headers.IsDefaultOrEmpty ||
            capture.Headers.Length != observed.Cells.Length ||
            capture.Headers[0] != "ordinal" ||
            capture.Headers.Distinct(StringComparer.Ordinal).Count() !=
            capture.Headers.Length)
            throw Fail("EXPORT_DIRECT_FIELD_COLUMNS_INVALID");

        var json = new JsonObject();
        for (var index = 0; index < observed.Cells.Length; index++)
        {
            var cell = observed.Cells[index];
            if (cell.ColumnOrdinal != index ||
                cell.ColumnName != capture.Headers[index])
                throw Fail("EXPORT_DIRECT_FIELD_CELL_BINDING_INVALID");
            if (index == 0)
            {
                if (cell.ValueState !=
                        StatisticReconciliationActualExportValueStates.Value ||
                    !long.TryParse(
                        cell.CanonicalValue,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var ordinal) ||
                    ordinal != observed.Ordinal)
                    throw Fail("EXPORT_DIRECT_FIELD_ORDINAL_INVALID");
                continue;
            }
            json.Add(cell.ColumnName, ExportCellNode(cell));
        }

        FieldStatisticSummaryRow row;
        try
        {
            row = json.Deserialize<FieldStatisticSummaryRow>(DirectViewJson) ??
                  throw Fail("EXPORT_DIRECT_FIELD_ROW_REQUIRED");
        }
        catch (JsonException error)
        {
            throw Fail(
                $"EXPORT_DIRECT_FIELD_ROW_INVALID:{error.GetType().Name}");
        }
        RequireDirectFieldBoundary(
            row,
            workId: null,
            scopeAssignmentId: null,
            dynamicFormTemplateId: null,
            "EXPORT");
        return row;
    }

    private static JsonNode? ExportCellNode(
        StatisticReconciliationActualExportCellObservation cell)
    {
        if (cell.ValueState ==
            StatisticReconciliationActualExportValueStates.Null)
            return null;
        if (cell.ValueState ==
            StatisticReconciliationActualExportValueStates.Empty)
            return JsonValue.Create(string.Empty);
        if (cell.ValueState !=
            StatisticReconciliationActualExportValueStates.Value)
            throw Fail("EXPORT_DIRECT_FIELD_CELL_STATE_INVALID");

        try
        {
            return cell.ValueType switch
            {
                StatisticReconciliationActualExportValueTypes.Integer =>
                    JsonValue.Create(long.Parse(
                        cell.CanonicalValue,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture)),
                StatisticReconciliationActualExportValueTypes.Decimal =>
                    JsonValue.Create(decimal.Parse(
                        cell.CanonicalValue,
                        NumberStyles.AllowLeadingSign |
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture)),
                StatisticReconciliationActualExportValueTypes.Boolean =>
                    JsonValue.Create(bool.Parse(cell.CanonicalValue)),
                StatisticReconciliationActualExportValueTypes.UtcInstant or
                StatisticReconciliationActualExportValueTypes.Text =>
                    JsonValue.Create(cell.CanonicalValue),
                StatisticReconciliationActualExportValueTypes.Json =>
                    JsonNode.Parse(cell.CanonicalValue),
                _ => throw Fail("EXPORT_DIRECT_FIELD_CELL_TYPE_INVALID")
            };
        }
        catch (Exception error) when (error is FormatException or
                                      OverflowException or JsonException)
        {
            throw Fail(
                $"EXPORT_DIRECT_FIELD_CELL_VALUE_INVALID:{error.GetType().Name}");
        }
    }

    private static void RequireDirectFieldBoundary(
        FieldStatisticSummaryRow row,
        string? workId,
        string? scopeAssignmentId,
        string? dynamicFormTemplateId,
        string source)
    {
        if (string.IsNullOrWhiteSpace(row.WorkId) ||
            row.ScopeType != "ASSIGNMENT" ||
            string.IsNullOrWhiteSpace(row.ScopeId) ||
            string.IsNullOrWhiteSpace(row.DynamicFormTemplateId) ||
            string.IsNullOrWhiteSpace(row.FieldId) ||
            string.IsNullOrWhiteSpace(row.FieldKey) ||
            string.IsNullOrWhiteSpace(row.FieldType) ||
            string.IsNullOrWhiteSpace(row.PeriodKey) ||
            string.IsNullOrWhiteSpace(row.PeriodInstanceKey) ||
            row.StatisticLabelCodes is null ||
            row.StatisticLabelCodes.Count == 0 ||
            !row.ShowInTree && !row.ShowInDetail ||
            workId is not null && row.WorkId != workId ||
            scopeAssignmentId is not null && row.ScopeId != scopeAssignmentId ||
            dynamicFormTemplateId is not null &&
            row.DynamicFormTemplateId != dynamicFormTemplateId ||
            row.ValueCount < 0 || row.NumericValueCount < 0 ||
            row.NumericValueCount > row.ValueCount || row.ReportCount < 0)
            throw Fail($"{source}_DIRECT_FIELD_IDENTITY_INVALID");

        if (row.NumericValueCount > 0)
        {
            if (!row.Sum.HasValue || !row.Min.HasValue || !row.Max.HasValue ||
                !row.Average.HasValue ||
                row.Average.Value !=
                row.Sum.Value / row.NumericValueCount)
                throw Fail($"{source}_DIRECT_FIELD_NUMERIC_MEASURES_INVALID");
        }
        else if (row.Average.HasValue)
            throw Fail($"{source}_DIRECT_FIELD_AVERAGE_INVALID");
    }

    private static void AppendDirectFieldView(
        ICollection<PendingObservation> pending,
        FieldStatisticSummaryRow row,
        string discriminator)
    {
        var labels = ExactDistinct(
            row.StatisticLabelCodes.ToImmutableArray(),
            "DIRECT_VIEW_FIELD_METRIC_LABEL_DUPLICATE");
        var counts = new ActualAggregateCounts(
            row.ReportCount,
            row.ValueCount,
            row.NumericValueCount);
        var measures = new ActualAggregateMeasures(
            row.Sum ?? 0m,
            row.Min,
            row.Max,
            row.Average,
            row.TrueCount,
            row.FalseCount,
            row.EarliestDateUtc,
            row.LatestDateUtc);
        foreach (var metricId in labels)
            AppendAggregateMetric(
                pending,
                new MetricShape(
                    "DIRECT", "FIELD", metricId, row.FieldId,
                    null, null, null, null, null, null, null, null, null,
                    row.PeriodKey),
                row.FieldType,
                row.BucketKey,
                counts,
                measures,
                discriminator);
    }

    internal static ImmutableArray<StatisticReconciliationActualTypedObservation>
        ProjectPlanBoundDirectFields(
            StatisticReconciliationActualSummaryPlanBinding exactPlan,
            ImmutableArray<StatisticReconciliationActualTypedObservation> values)
    {
        var plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
            exactPlan ?? throw new ArgumentNullException(nameof(exactPlan)));
        if (values.IsDefault)
            throw Fail("DIRECT_PLAN_BOUND_VALUES_DEFAULT");

        var projected = new List<StatisticReconciliationActualTypedObservation>(
            values.Length);
        foreach (var value in values)
        {
            var matches = plan.IdentityDescriptors.Where(descriptor =>
                    descriptor.Family == "DIRECT" &&
                    descriptor.Kind == "FIELD" &&
                    value.Family == descriptor.Family &&
                    value.Kind == descriptor.Kind &&
                    value.MetricId == descriptor.MetricId &&
                    value.PeriodKey == descriptor.PeriodKey &&
                    value.FieldId == descriptor.FieldId &&
                    value.TableId == descriptor.TableId &&
                    value.RowId == descriptor.RowId &&
                    value.LabelId == descriptor.LabelId &&
                    value.BasicScope == descriptor.BasicScope &&
                    value.BasicScopeId == descriptor.BasicScopeId &&
                    value.AdvancedGrain == descriptor.AdvancedGrain &&
                    value.DiffKind == descriptor.DiffKind &&
                    value.TransitionLeg == descriptor.TransitionLegs.Single() &&
                    value.TransitionKind == descriptor.TransitionKind &&
                    value.CollectionSemantics ==
                        descriptor.CollectionSemantics)
                .Take(2)
                .ToArray();
            if (matches.Length > 1)
                throw Fail("DIRECT_PLAN_BOUND_DESCRIPTOR_AMBIGUOUS");
            if (matches.Length == 0)
            {
                projected.Add(value);
                continue;
            }

            var descriptor = matches[0];
            if (!descriptor.AtomKinds.Contains(
                    value.AtomKind, StringComparer.Ordinal))
                continue;
            var rowCount = descriptor.ExpandArray ? value.RowCount : 0;
            var canonicalValue = value.AtomKind == "ROW_COUNT"
                ? StatisticReconciliationActualCanonical.Integer(rowCount)
                : value.CanonicalValue;
            projected.Add(RecreatePlanBound(
                value, projected.Count, canonicalValue, rowCount));
        }

        return projected
            .Select((value, ordinal) =>
                StatisticReconciliationActualTypedObservationCanonical
                    .Reordinalize(value, ordinal))
            .ToImmutableArray();
    }

    private static StatisticReconciliationActualTypedObservation
        RecreatePlanBound(
            StatisticReconciliationActualTypedObservation value,
            int ordinal,
            string canonicalValue,
            long rowCount)
        => StatisticReconciliationActualTypedObservationCanonical
            .CreateSummaryCompatible(
                ordinal,
                value.Layer,
                value.OwnerId,
                value.OwnerVersionSha256,
                value.Family,
                value.Kind,
                value.MetricId,
                value.PeriodKey,
                value.AtomKind,
                value.ValueType,
                value.ValueState,
                canonicalValue,
                value.DecimalScale,
                value.OccurrenceCount,
                value.ReportCount,
                rowCount,
                value.NumericValueCount,
                value.FieldId,
                value.TableId,
                value.RowId,
                value.LabelId,
                value.BasicScope,
                value.BasicScopeId,
                value.FlowBranchId,
                value.FlowStepId,
                value.AdvancedGrain,
                value.DiffKind,
                value.TransitionLeg,
                value.TransitionKind,
                value.CollectionSemantics);

    private static void AppendDirectGroups(
        IEnumerable<DirectSample> samples,
        ICollection<PendingObservation> pending)
    {
        foreach (var group in samples
                     .GroupBy(sample => sample.Shape)
                     .OrderBy(group => group.Key.SortKey, StringComparer.Ordinal))
        {
            var channels = group
                .Select(sample => (sample.Channel.AtomKind,
                    sample.Channel.ValueType,
                    sample.Channel.CollectionSemantics))
                .Distinct()
                .ToArray();
            if (channels.Length != 1)
                throw Fail("DIRECT_VALUE_CHANNEL_AMBIGUOUS");
            var values = group
                .OrderBy(value => value.SortDiscriminator, StringComparer.Ordinal)
                .ToArray();
            var reportCount = values.Select(value => value.ReportId)
                .Distinct(StringComparer.Ordinal)
                .LongCount();
            var rowCount = values.LongLength;
            var numericCount = channels[0].ValueType == "NUMBER"
                ? rowCount
                : 0;
            var discriminator = StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_MAPPER_DIRECT_GROUP_V1",
                values.Select(value => value.SortDiscriminator));
            AddStandardCounts(
                pending,
                group.Key,
                reportCount,
                rowCount,
                numericCount,
                discriminator);

            if (channels[0].ValueType == "NUMBER")
            {
                var numbers = values.Select(value => decimal.Parse(
                        value.Channel.CanonicalValue,
                        NumberStyles.AllowLeadingSign |
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture))
                    .ToArray();
                decimal sum = 0;
                try
                {
                    foreach (var number in numbers)
                        sum += number;
                }
                catch (OverflowException)
                {
                    throw Fail("DIRECT_NUMBER_SUM_OVERFLOW");
                }
                AddNumeric(
                    pending,
                    group.Key,
                    "SUM",
                    sum,
                    reportCount,
                    rowCount,
                    numericCount,
                    discriminator);
                AddNumeric(
                    pending,
                    group.Key,
                    "MIN",
                    numbers.Min(),
                    reportCount,
                    rowCount,
                    numericCount,
                    discriminator);
                AddNumeric(
                    pending,
                    group.Key,
                    "MAX",
                    numbers.Max(),
                    reportCount,
                    rowCount,
                    numericCount,
                    discriminator);
                decimal mean;
                try
                {
                    mean = sum / numericCount;
                }
                catch (OverflowException)
                {
                    throw Fail("DIRECT_NUMBER_MEAN_OVERFLOW");
                }
                AddNumeric(
                    pending,
                    group.Key,
                    "MEAN",
                    mean,
                    reportCount,
                    rowCount,
                    numericCount,
                    discriminator);
                continue;
            }

            foreach (var valueGroup in values
                         .GroupBy(value => value.Channel.CanonicalValue,
                             StringComparer.Ordinal)
                         .OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var first = valueGroup.First().Channel;
                pending.Add(new PendingObservation(
                    group.Key,
                    first.AtomKind,
                    first.ValueType,
                    first.CanonicalValue,
                    first.DecimalScale,
                    valueGroup.LongCount(),
                    reportCount,
                    rowCount,
                    numericCount,
                    discriminator,
                    first.CollectionSemantics));
            }
        }
    }

    private static void AppendAggregateMetric(
        ICollection<PendingObservation> pending,
        MetricShape shape,
        string declaredType,
        string? bucketKey,
        ActualAggregateCounts counts,
        ActualAggregateMeasures measures,
        string discriminator)
    {
        AddStandardCounts(
            pending,
            shape,
            counts.ReportCount,
            counts.RowCount,
            counts.NumericValueCount,
            discriminator);
        var type = declaredType.ToUpperInvariant();
        if (IsNumber(type))
        {
            if (counts.NumericValueCount == 0)
                return;
            if (!measures.Min.HasValue || !measures.Max.HasValue ||
                !measures.Mean.HasValue)
                throw Fail("AGGREGATE_NUMBER_MEASURES_INCOMPLETE");
            AddNumeric(pending, shape, "SUM", measures.Sum,
                counts.ReportCount, counts.RowCount,
                counts.NumericValueCount, discriminator);
            AddNumeric(pending, shape, "MIN", measures.Min.Value,
                counts.ReportCount, counts.RowCount,
                counts.NumericValueCount, discriminator);
            AddNumeric(pending, shape, "MAX", measures.Max.Value,
                counts.ReportCount, counts.RowCount,
                counts.NumericValueCount, discriminator);
            AddNumeric(pending, shape, "MEAN", measures.Mean.Value,
                counts.ReportCount, counts.RowCount,
                counts.NumericValueCount, discriminator);
            return;
        }
        if (counts.NumericValueCount != 0)
            throw Fail("AGGREGATE_NON_NUMBER_NUMERIC_COUNT");

        if (type == "BOOLEAN")
        {
            AddBooleanOccurrences(
                pending,
                shape,
                true,
                measures.TrueCount,
                counts,
                discriminator);
            AddBooleanOccurrences(
                pending,
                shape,
                false,
                measures.FalseCount,
                counts,
                discriminator);
            return;
        }
        if (bucketKey is null)
            return;
        if (counts.RowCount == 0)
            return;
        pending.Add(new PendingObservation(
            shape,
            "BUCKET",
            "BUCKET",
            $"S:{bucketKey}",
            0,
            counts.RowCount,
            counts.ReportCount,
            counts.RowCount,
            counts.NumericValueCount,
            discriminator));
    }

    private static void AddBooleanOccurrences(
        ICollection<PendingObservation> pending,
        MetricShape shape,
        bool value,
        long count,
        ActualAggregateCounts counts,
        string discriminator)
    {
        if (count == 0)
            return;
        pending.Add(new PendingObservation(
            shape,
            "BOOLEAN",
            "BOOLEAN",
            StatisticReconciliationActualCanonical.Boolean(value),
            0,
            count,
            counts.ReportCount,
            counts.RowCount,
            counts.NumericValueCount,
            discriminator));
    }

    private static void AppendDiffValue(
        ICollection<PendingObservation> pending,
        ActualP9DiffCapture capture,
        ActualP9DiffRowObservation row,
        DiffSide side,
        ActualP9DiffTypedObservation value)
    {
        var shape = DiffShape(
            row,
            side,
            $"{ExplicitPrefix}:P9_DIFF:{side.RelativeSide}:" +
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_MAPPER_DIFF_SIDE_METRIC_V1",
                side.ConceptKind,
                side.ConceptKey,
                side.ConceptCode));
        if (!TryDiffChannel(value, out var channel))
        {
            pending.Add(Opaque(
                shape with
                {
                    MetricId = OpaqueMetric(
                        "P9_DIFF_VALUE",
                        side.RelativeSide,
                        side.ConceptKind,
                        side.ConceptKey,
                        row.Key)
                },
                JsonSerializer.Serialize(value),
                value.SemanticSha256));
            return;
        }
        var reportCount = DiffReportCount(capture, side.OwnerSide);
        pending.Add(new PendingObservation(
            shape,
            channel!.AtomKind,
            channel.ValueType,
            channel.CanonicalValue,
            channel.DecimalScale,
            1,
            reportCount,
            1,
            channel.ValueType == "NUMBER" ? 1 : 0,
            value.SemanticSha256,
            channel.CollectionSemantics));
    }

    private static long DiffReportCount(
        ActualP9DiffCapture capture,
        string ownerSide)
        => capture.SourcePins
            .Where(pin => pin.Side == ownerSide)
            .Select(pin => pin.SourceReportId)
            .Distinct(StringComparer.Ordinal)
            .LongCount();

    private static MetricShape DiffShape(
        ActualP9DiffRowObservation row,
        DiffSide side,
        string metricId)
    {
        var period = $"{ExplicitPrefix}:P9_DIFF_PERIOD:" +
                     StatisticReconciliationActualCanonical.Hash(
                         "P10_ACTUAL_MAPPER_DIFF_PERIOD_V1",
                         side.PeriodCanonicalJson);
        if (side.ConceptKind == "FIELD")
        {
            return new MetricShape(
                "DIFF", "FIELD", metricId, side.ConceptKey,
                null, null, null, null, null, null, null, null,
                "FIELD", period);
        }
        var separator = side.ConceptKey.IndexOf(':');
        if (separator <= 0 || separator == side.ConceptKey.Length - 1)
            throw Fail("DIFF_CONCEPT_KEY_INVALID");
        var tableId = side.ConceptKey[..separator];
        var localId = side.ConceptKey[(separator + 1)..];
        if (side.ConceptKind == "TABLE_METRIC")
        {
            return new MetricShape(
                "DIFF", "TABLE", metricId, null, tableId,
                null, null, null, null, null, null, null,
                "TABLE_METRIC", period);
        }
        if (side.ConceptKind != "ROW_LABEL" ||
            !row.Key.StartsWith("ROW:", StringComparison.Ordinal) ||
            row.Key.Length == 4)
            throw Fail("DIFF_ROW_LABEL_KEY_INVALID");
        return new MetricShape(
            "DIFF", "ROW_LABEL", metricId, null, tableId,
            row.Key[4..], localId, null, null, null, null, null,
            "ROW_LABEL", period);
    }

    private static bool TryDiffChannel(
        ActualP9DiffTypedObservation value,
        out ValueChannel? channel)
    {
        channel = null;
        if (!value.TypedShapeValid || value.State != "VALUE")
            return false;
        switch (value.DataType)
        {
            case "NUMBER" when value.NumericValue.HasValue:
            {
                var canonical = StatisticReconciliationActualCanonical.Number(
                    value.NumericValue.Value);
                channel = new ValueChannel(
                    "SUM", "NUMBER", canonical, DecimalScale(canonical));
                return true;
            }
            case "BOOLEAN" when value.BooleanValue.HasValue:
                channel = new ValueChannel(
                    "BOOLEAN",
                    "BOOLEAN",
                    StatisticReconciliationActualCanonical.Boolean(
                        value.BooleanValue.Value),
                    0);
                return true;
            case "CHOICE" when value.ChoiceIds.Length > 0:
                channel = new ValueChannel(
                    "STRING_LIST",
                    "STRING_LIST",
                    JsonSerializer.Serialize(value.ChoiceIds),
                    0,
                    "UNORDERED");
                return true;
            case "TEXT" when !string.IsNullOrEmpty(value.CanonicalValue):
                channel = new ValueChannel(
                    "TEXT", "TEXT", value.CanonicalValue, 0);
                return true;
            default:
                return false;
        }
    }

    private static bool TryDirectChannel(
        ActualDirectTypedValue value,
        string declaredType,
        bool allowFullDate,
        out ValueChannel? channel)
    {
        channel = null;
        switch (value.ValueKind)
        {
            case "NUMBER" when value.NumericValue.HasValue:
            {
                var canonical = StatisticReconciliationActualCanonical.Number(
                    value.NumericValue.Value);
                channel = new ValueChannel(
                    "SUM", "NUMBER", canonical, DecimalScale(canonical));
                return true;
            }
            case "BOOLEAN" when value.BooleanValue.HasValue:
                channel = new ValueChannel(
                    "BOOLEAN",
                    "BOOLEAN",
                    StatisticReconciliationActualCanonical.Boolean(
                        value.BooleanValue.Value),
                    0);
                return true;
            case "DATE" when allowFullDate && declaredType == "FULL_DATE" &&
                              value.DateValueUtc.HasValue:
                channel = new ValueChannel(
                    "FULL_DATE",
                    "FULL_DATE",
                    "DAY:" + value.DateValueUtc.Value.ToString(
                        "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    0);
                return true;
            case "OPTION" or "TEXT_BUCKET" when value.BucketKey is not null:
                channel = new ValueChannel(
                    "BUCKET", "BUCKET", $"S:{value.BucketKey}", 0);
                return true;
            case "TEXT" when !string.IsNullOrEmpty(value.CanonicalValue):
                channel = new ValueChannel(
                    "TEXT", "TEXT", value.CanonicalValue, 0);
                return true;
            case "ROW_LABEL" when !string.IsNullOrEmpty(value.CanonicalValue):
                channel = new ValueChannel(
                    "TEXT", "TEXT", value.CanonicalValue, 0);
                return true;
            default:
                return false;
        }
    }

    private static void AddStandardCounts(
        ICollection<PendingObservation> pending,
        MetricShape shape,
        long reportCount,
        long rowCount,
        long numericCount,
        string discriminator)
    {
        AddCount(pending, shape, "REPORT_COUNT", reportCount,
            reportCount, rowCount, numericCount, discriminator);
        AddCount(pending, shape, "ROW_COUNT", rowCount,
            reportCount, rowCount, numericCount, discriminator);
        AddCount(pending, shape, "COUNT", rowCount,
            reportCount, rowCount, numericCount, discriminator);
        AddCount(pending, shape, "NUMERIC_VALUE_COUNT", numericCount,
            reportCount, rowCount, numericCount, discriminator);
    }

    private static void AddCount(
        ICollection<PendingObservation> pending,
        MetricShape shape,
        string atomKind,
        long value,
        long reportCount,
        long rowCount,
        long numericCount,
        string discriminator)
        => pending.Add(new PendingObservation(
            shape,
            atomKind,
            "NUMBER",
            StatisticReconciliationActualCanonical.Integer(value),
            0,
            1,
            reportCount,
            rowCount,
            numericCount,
            discriminator));

    private static void AddNumeric(
        ICollection<PendingObservation> pending,
        MetricShape shape,
        string atomKind,
        decimal value,
        long reportCount,
        long rowCount,
        long numericCount,
        string discriminator)
    {
        var canonical = StatisticReconciliationActualCanonical.Number(value);
        pending.Add(new PendingObservation(
            shape,
            atomKind,
            "NUMBER",
            canonical,
            DecimalScale(canonical),
            1,
            reportCount,
            rowCount,
            numericCount,
            discriminator));
    }

    private static PendingObservation Opaque(
        MetricShape shape,
        string canonicalValue,
        string discriminator)
        => new(
            shape,
            "TEXT",
            "TEXT",
            canonicalValue,
            0,
            1,
            0,
            0,
            0,
            discriminator);

    private static ImmutableArray<StatisticReconciliationActualTypedObservation>
        Materialize(
            string layer,
            string ownerId,
            string ownerVersionSha256,
            IEnumerable<PendingObservation> values)
    {
        var ordered = values
            .OrderBy(value => value.Shape.SortKey, StringComparer.Ordinal)
            .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
            .ThenBy(value => value.ValueType, StringComparer.Ordinal)
            .ThenBy(value => value.CollectionSemantics, StringComparer.Ordinal)
            .ThenBy(value => value.CanonicalValue, StringComparer.Ordinal)
            .ThenBy(value => value.SortDiscriminator, StringComparer.Ordinal)
            .ToArray();
        var result = ordered.Select((value, ordinal) =>
                StatisticReconciliationActualTypedObservationCanonical
                    .CreateSummaryCompatible(
                    ordinal,
                    layer,
                    ownerId,
                    ownerVersionSha256,
                    value.Shape.Family,
                    value.Shape.Kind,
                    value.Shape.MetricId,
                    value.Shape.PeriodKey,
                    value.AtomKind,
                    value.ValueType,
                    "VALUE",
                    value.CanonicalValue,
                    value.DecimalScale,
                    value.OccurrenceCount,
                    value.ReportCount,
                    value.RowCount,
                    value.NumericValueCount,
                    fieldId: value.Shape.FieldId,
                    tableId: value.Shape.TableId,
                    rowId: value.Shape.RowId,
                    labelId: value.Shape.LabelId,
                    basicScope: value.Shape.BasicScope,
                    basicScopeId: value.Shape.BasicScopeId,
                    flowBranchId: value.Shape.FlowBranchId,
                    flowStepId: value.Shape.FlowStepId,
                    advancedGrain: value.Shape.AdvancedGrain,
                    diffKind: value.Shape.DiffKind,
                    collectionSemantics: value.CollectionSemantics))
            .ToImmutableArray();
        return StatisticReconciliationActualTypedObservationCanonical.NormalizeSet(
            result);
    }

    private static MetricShape DirectFieldShape(
        ActualFieldProjectionObservation row,
        string metricId)
        => new(
            "DIRECT", "FIELD", metricId, row.FieldId,
            null, null, null, null, null, null, null, null, null,
            row.PeriodKey);

    private static MetricShape DirectTableShape(
        ActualTableMetricProjectionObservation row,
        string metricId)
        => new(
            "DIRECT", "TABLE", metricId, null, row.BlockId,
            null, null, null, null, null, null, null, null,
            row.PeriodKey);

    private static MetricShape AggregateFieldShape(
        ActualFieldAggregateObservation row,
        string metricId)
        => new(
            "DIRECT", "FIELD", metricId, row.FieldId,
            null, null, null, null, null, null, null, null, null,
            row.Time.PeriodKey);

    private static MetricShape AggregateTableShape(
        ActualTableMetricAggregateObservation row,
        string metricId)
        => new(
            "DIRECT", "TABLE", metricId, null, row.BlockId,
            null, null, null, null, null, null, null, null,
            row.Time.PeriodKey);

    private static MetricShape BasicShape(
        ActualBasicOwnerBoundary boundary,
        string itemIdentity)
    {
        string scopeId;
        string? branchId = null;
        string? stepId = null;
        switch (boundary.SourceScopeMode)
        {
            case "DIRECT_CHILDREN_OR_SELF":
            case "DIRECT_CHILDREN":
                scopeId = boundary.ScopeAssignmentId;
                break;
            case "FLOW_BRANCH":
                scopeId = boundary.SourceFlowBranchId
                    ?? throw Fail("BASIC_FLOW_BRANCH_ID_REQUIRED");
                branchId = scopeId;
                break;
            case "FLOW_STEP":
                scopeId = boundary.SourceFlowStepId
                    ?? throw Fail("BASIC_FLOW_STEP_ID_REQUIRED");
                stepId = scopeId;
                break;
            case "FLOW_EFFECTIVE_PATH":
            case "FLOW_FINAL":
                scopeId = boundary.SourceFlowInstanceId
                    ?? throw Fail("BASIC_FLOW_INSTANCE_ID_REQUIRED");
                break;
            default:
                throw Fail("BASIC_SCOPE_INVALID");
        }
        return new MetricShape(
            "BASIC",
            "FIELD",
            OpaqueMetric(
                "BASIC_ITEM",
                boundary.SourceScopeMode,
                scopeId,
                itemIdentity),
            itemIdentity,
            null,
            null,
            null,
            boundary.SourceScopeMode,
            scopeId,
            branchId,
            stepId,
            null,
            null,
            $"{OpaquePrefix}:BASIC_REQUEST:{boundary.RequestHash}");
    }

    private static ImmutableArray<string> ExactDistinct(
        ImmutableArray<string> values,
        string reason)
    {
        var normalized = values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalized.Length != values.Length)
            throw Fail(reason);
        return normalized;
    }

    private static string OpaqueMetric(
        string layer,
        params string?[] identity)
        => $"{OpaquePrefix}:{layer}:" +
           StatisticReconciliationActualCanonical.Hash(
               "P10_ACTUAL_MAPPER_OPAQUE_METRIC_V1",
               identity);

    private static string OwnerId(
        string domain,
        params string?[] identity)
        => StatisticReconciliationActualCanonical.Hash(
            $"P10_ACTUAL_MAPPER_{domain}_V1",
            identity);

    private static bool IsNumber(string value)
        => value.Equals("NUMBER", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("DECIMAL", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("NUMERIC", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("CURRENCY", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("PERCENT", StringComparison.OrdinalIgnoreCase);

    private static int DecimalScale(string canonical)
    {
        var point = canonical.IndexOf('.');
        return point < 0 ? 0 : canonical.Length - point - 1;
    }

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_TYPED_MAPPER_{reason}");

    private sealed record MetricShape(
        string Family,
        string Kind,
        string MetricId,
        string? FieldId,
        string? TableId,
        string? RowId,
        string? LabelId,
        string? BasicScope,
        string? BasicScopeId,
        string? FlowBranchId,
        string? FlowStepId,
        string? AdvancedGrain,
        string? DiffKind,
        string PeriodKey)
    {
        internal string SortKey =>
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_MAPPER_SHAPE_ORDER_V1",
                Family,
                Kind,
                MetricId,
                FieldId,
                TableId,
                RowId,
                LabelId,
                BasicScope,
                BasicScopeId,
                FlowBranchId,
                FlowStepId,
                AdvancedGrain,
                DiffKind,
                PeriodKey);
    }

    private sealed record PendingObservation(
        MetricShape Shape,
        string AtomKind,
        string ValueType,
        string CanonicalValue,
        int DecimalScale,
        long OccurrenceCount,
        long ReportCount,
        long RowCount,
        long NumericValueCount,
        string SortDiscriminator,
        string? CollectionSemantics = null);

    private sealed record ValueChannel(
        string AtomKind,
        string ValueType,
        string CanonicalValue,
        int DecimalScale,
        string? CollectionSemantics = null);

    private sealed record DirectSample(
        MetricShape Shape,
        string ReportId,
        ValueChannel Channel,
        string SortDiscriminator);

    private sealed record DiffSide(
        string RelativeSide,
        string OwnerSide,
        string ConceptKind,
        string ConceptKey,
        string ConceptCode,
        string PeriodCanonicalJson);
}
