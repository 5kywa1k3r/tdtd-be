using System.Text.Json;
using MongoDB.Bson;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

/// <summary>Pure L5b calculation over an explicitly captured source set.
/// No DB/job calls, source authorization, persistence, migration or UI totals.
/// L5c must match schema/config pins, authorize membership and publish atomically.</summary>
internal static class NativeTableStatisticCalculator
{
    internal static NativeStatisticCalculationResult Calculate(string planJson,
        IReadOnlyList<DynamicFormNativeTableDto> definitions, string sectionsJson, string fieldsJson, string blocksJson,
        string schemaHash, IReadOnlyList<WorkReportNativeSourcePin> expectedSources, string expectedSourceOrderDigest,
        IEnumerable<NativeStatisticCalculationSource> sources, NativeStatisticCalculationLimits limits,
        CancellationToken ct = default)
    {
        var budget = new NativeStatisticCalculationBudget(limits, ct);
        if (!StatRunCanonicalJson.IsCanonicalSha256(schemaHash)
            || !StatRunCanonicalJson.IsCanonicalSha256(expectedSourceOrderDigest)
            || expectedSources.Count > limits.MaxReports)
            throw NativeStatisticCalculationError.Invalid("CALCULATION_PINS_OR_SOURCE_LIMIT");
        // Detach once. Source enumerators cannot mutate definitions halfway through a run.
        var json = JsonSerializer.SerializeToElement(definitions, StatConfigCanonicalJson.StrictJsonOptions);
        var tables = StatConfigCanonicalJson.DeserializeStrict<List<DynamicFormNativeTableDto>>(json);
        var plan = DynamicFormNativeStatisticPlan.Prepare(planJson, tables, sectionsJson, fieldsJson, blocksJson);
        var needsTime = plan.Targets.Any(t => t.Target.Order!.Reports == "sourceUpdatedAtAsc");
        var expected = new Dictionary<string, WorkReportNativeSourcePin>(StringComparer.Ordinal);
        foreach (var pin in expectedSources)
        {
            if (pin is null || string.IsNullOrWhiteSpace(pin.ReportId) || pin.ReportId != pin.ReportId.Trim()
                || !ObjectId.TryParse(pin.ReportId, out var sourceId) || sourceId.ToString() != pin.ReportId
                || pin.PayloadRevision < 1 || !StatRunCanonicalJson.IsCanonicalSha256(pin.PayloadHash)
                || !expected.TryAdd(pin.ReportId, pin)) throw NativeStatisticCalculationError.Invalid("SOURCE_PIN_INVALID_OR_DUPLICATE");
            if (needsTime && pin.PayloadUpdatedAtUtc is null) throw NativeStatisticCalculationError.Invalid("SOURCE_TIMESTAMP_REQUIRED");
            if (pin.PayloadUpdatedAtUtc is DateTime time && (time.Kind != DateTimeKind.Utc
                || time == DateTime.MinValue || time.Ticks % TimeSpan.TicksPerMillisecond != 0))
                throw NativeStatisticCalculationError.Invalid("SOURCE_TIMESTAMP_INVALID");
            budget.Retain(256L + 2L * pin.ReportId.Length);
        }
        var orderedPins = expected.Values.OrderBy(p => p.ReportId, StringComparer.Ordinal).ToArray();
        var digest = WorkReportNativeSourcePin.Digest(orderedPins);
        if (digest != expectedSourceOrderDigest) throw NativeStatisticCalculationError.Invalid("SOURCE_ORDER_DIGEST_MISMATCH");
        var groups = new Dictionary<NativeStatisticGroupAddress, Group>();
        Group Resolve(int targetIndex, int groupIndex, string? reportId = null, string? recordId = null)
        {
            var target = plan.Targets[targetIndex]; var binding = target.Groups[groupIndex];
            var address = new NativeStatisticGroupAddress(target.Target.TableId!, target.Target.TargetId!,
                binding.RowId, binding.FieldId, reportId, recordId);
            if (!groups.TryGetValue(address, out var group))
            {
                budget.Group(); group = new(address, targetIndex, groupIndex, target.Target, binding, budget);
                groups.Add(address, group);
            }
            return group;
        }
        for (var ti = 0; ti < plan.Targets.Count; ti++)
            for (var gi = 0; gi < plan.Targets[ti].Groups.Count; gi++)
                if (!plan.Targets[ti].Groups[gi].RecordScoped) _ = Resolve(ti, gi);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source is null || !expected.TryGetValue(source.Pin.ReportId, out var pin)
                || pin != source.Pin || !seen.Add(pin.ReportId))
                throw NativeStatisticCalculationError.Invalid("SOURCE_MEMBERSHIP_DUPLICATE_OR_DRIFT");
            // Reject errors and invalid values even if a type filter would exclude the cell.
            DynamicFormNativeTableValues.ValidateEnvelope(tables, schemaHash, source.Values, submitting: true);
            // Membership and envelope checks also apply to a plan with no
            // operations. Such a plan does not need to index paged content.
            if (plan.Targets.Count == 0) continue;
            var index = IndexSource(source.Values, tables);
            for (var ti = 0; ti < plan.Targets.Count; ti++)
            {
                var target = plan.Targets[ti]; var table = index[target.Target.TableId!];
                var selector = target.Target.Selector!;
                var columnIndexes = selector.FieldIds!.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);
                var rowIndexes = selector.RowIds?.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);
                for (var gi = 0; gi < target.Groups.Count; gi++)
                {
                    ct.ThrowIfCancellationRequested();
                    var binding = target.Groups[gi];
                    if (binding.RecordScoped)
                    {
                        foreach (var record in table.Rows)
                        {
                            var group = Resolve(ti, gi, pin.ReportId, record.Id);
                            group.BeginSource(pin, 1, selector.FieldIds.Count, 1);
                            long order = 0;
                            foreach (var cell in binding.Cells) Apply(group, record, cell, order++, record.Index);
                        }
                    }
                    else
                    {
                        var group = Resolve(ti, gi);
                        var matrix = selector.Kind == "matrix";
                        group.BeginSource(pin, matrix ? selector.RowIds!.Count : table.Rows.Count,
                            selector.FieldIds.Count, matrix ? 1 : table.Rows.Count);
                        long order = 0;
                        if (matrix)
                            foreach (var cell in binding.Cells) Apply(group, table.ById[cell.RowId!], cell, order++, rowIndexes![cell.RowId!]);
                        else if (target.Target.Order!.Cells == "columnMajor")
                            foreach (var cell in binding.Cells) foreach (var record in table.Rows) Apply(group, record, cell, order++, record.Index);
                        else
                            foreach (var record in table.Rows) foreach (var cell in binding.Cells) Apply(group, record, cell, order++, record.Index);
                    }
                }
                void Apply(Group group, Row record, DynamicFormNativeStatisticPlan.CellBinding cell, long order, int row)
                {
                    ct.ThrowIfCancellationRequested(); budget.Visit();
                    record.Cells.TryGetValue(cell.FieldId, out var raw);
                    var value = NativeStatisticText.Read(raw, cell.Spec.Type!);
                    var reference = new NativeStatisticCellReference(pin.ReportId, target.Target.TableId!,
                        selector.Kind == "matrix" ? record.Id : null, selector.Kind == "records" ? record.Id : null, cell.FieldId);
                    group.Add(value, cell.Spec, reference, pin, order, row, columnIndexes[cell.FieldId]);
                }
            }
        }
        if (seen.Count != expected.Count) throw NativeStatisticCalculationError.Invalid("SOURCE_SET_INCOMPLETE");
        ct.ThrowIfCancellationRequested();
        var results = groups.Values.OrderBy(g => g.TargetIndex).ThenBy(g => g.GroupIndex)
            .ThenBy(g => g.Address.ReportId, StringComparer.Ordinal).ThenBy(g => g.Address.RecordId, StringComparer.Ordinal)
            .Select(group => { ct.ThrowIfCancellationRequested(); return group.Finish(); }).ToArray();
        var result = new NativeStatisticCalculationResult(2, schemaHash, plan.ContentDigest, digest, orderedPins, results);
        if (NativeStatisticText.Utf8Bytes(JsonSerializer.Serialize(result, StatConfigCanonicalJson.StrictJsonOptions)) > limits.MaxOutputBytes)
            throw NativeStatisticCalculationError.Invalid("CALCULATION_OUTPUT_BYTES_LIMIT");
        return result; // Nothing is returned/published on a partial source set, error or exceeded limit.
    }

    private sealed record Row(string Id, int Index, IReadOnlyDictionary<string, JsonElement> Cells);
    private sealed record Table(IReadOnlyList<Row> Rows, IReadOnlyDictionary<string, Row> ById);
    private static Dictionary<string, Table> IndexSource(JsonElement envelope, IReadOnlyList<DynamicFormNativeTableDto> definitions)
    {
        var matrixIds = definitions.Where(t => t.Layout == "matrix").Select(t => t.Id!).ToHashSet(StringComparer.Ordinal);
        return envelope.GetProperty("tables").EnumerateArray().ToDictionary(table => table.GetProperty("tableId").GetString()!, table =>
        {
            if(table.TryGetProperty("contentRef",out _))throw NativeStatisticCalculationError.Invalid("CONTENT_TABLE_REQUIRES_PAGED_READER");
            var matrix = matrixIds.Contains(table.GetProperty("tableId").GetString()!);
            var rows = table.GetProperty(matrix ? "rows" : "records").EnumerateArray()
                .OrderBy(row => row.GetProperty(matrix ? "rowId" : "recordId").GetString(), StringComparer.Ordinal)
                .Select((row, index) => new Row(row.GetProperty(matrix ? "rowId" : "recordId").GetString()!, index,
                    row.GetProperty("cells").EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal))).ToArray();
            return new Table(rows, rows.ToDictionary(row => row.Id, StringComparer.Ordinal));
        }, StringComparer.Ordinal);
    }

    private sealed class Group
    {
        internal NativeStatisticGroupAddress Address { get; }
        internal int TargetIndex { get; }
        internal int GroupIndex { get; }
        private readonly DynamicFormNativeStatisticPlan.GroupBinding _binding;
        private readonly NativeStatisticAccumulator[] _operations;
        private long _reports, _presentReports, _cells, _present, _missing, _null, _excluded;
        private bool _reportHasValue;
        internal Group(NativeStatisticGroupAddress address, int ti, int gi, DynamicFormNativeStatisticPlanTargetDto target,
            DynamicFormNativeStatisticPlan.GroupBinding binding, NativeStatisticCalculationBudget budget)
        {
            (Address, TargetIndex, GroupIndex, _binding) = (address, ti, gi, binding);
            _operations = target.Operations!.Select(op => new NativeStatisticAccumulator(target, op, budget)).ToArray();
        }
        internal void BeginSource(WorkReportNativeSourcePin pin, int rows, int columns, int repetitions)
        {
            _reports++; _reportHasValue = false;
            _excluded = checked(_excluded + (long)_binding.ExcludedCellCount * repetitions);
            foreach (var operation in _operations) operation.BeginSource(pin, rows, columns);
        }
        internal void Add(NativeStatisticValue value, DynamicFormNativeCellSpecDto spec,
            NativeStatisticCellReference source, WorkReportNativeSourcePin pin, long order, int row, int column)
        {
            _cells++;
            if (value.State == "missing") _missing++;
            else if (value.State == "null") _null++;
            else
            {
                _present++;
                if (!_reportHasValue) { _presentReports++; _reportHasValue = true; }
            }
            foreach (var operation in _operations) operation.Add(value, spec, source, pin, order, row, column);
        }
        internal NativeStatisticGroupResult Finish()
        {
            var counts = new NativeStatisticCounts(_reports, _presentReports, _cells, _present, _missing, _null, _excluded);
            return new(Address, _operations.Select(operation => operation.Finish(counts)).ToArray());
        }
    }
}
