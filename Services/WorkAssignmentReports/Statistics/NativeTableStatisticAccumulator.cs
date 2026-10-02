using System.Globalization;
using System.Text.Json;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

internal sealed class NativeStatisticAccumulator
{
    private sealed record Order(WorkReportNativeSourcePin Pin, long Cell);
    private sealed record TextEntry(Order Order, string Text, NativeStatisticCellReference Source);
    private sealed record StackEntry(Order Order, int Row, int Column,
        NativeStatisticCellReference Source, NativeStatisticValue Value);
    private sealed record Block(WorkReportNativeSourcePin Pin, int Rows, int Columns);
    private readonly DynamicFormNativeStatisticPlanTargetDto _target;
    private readonly DynamicFormNativeStatisticOperationDto _operation;
    private readonly NativeStatisticCalculationBudget _budget;
    private readonly NativeStatisticExactTotal _total = new();
    private readonly HashSet<string> _distinct = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeStatisticBucket> _buckets = new(StringComparer.Ordinal);
    private readonly List<TextEntry> _text = new();
    private readonly List<StackEntry> _stack = new();
    private readonly List<Block> _blocks = new();
    private NativeStatisticValue? _selected;
    private NativeStatisticCellReference? _selectedSource;
    private Order? _selectedOrder;
    private long _selectedBytes;
    private int? _datePrecision;
    private long _matched;

    internal NativeStatisticAccumulator(DynamicFormNativeStatisticPlanTargetDto target,
        DynamicFormNativeStatisticOperationDto operation, NativeStatisticCalculationBudget budget)
    {
        (_target, _operation, _budget) = (target, operation, budget);
        budget.Retain(512);
    }

    internal void BeginSource(WorkReportNativeSourcePin pin, int rows, int columns)
    {
        if (_operation.Method is not ("STACK_ROWS" or "STACK_COLUMNS")) return;
        _budget.Retain(96); _blocks.Add(new(pin, rows, columns));
    }

    internal void Add(NativeStatisticValue value, DynamicFormNativeCellSpecDto spec,
        NativeStatisticCellReference source, WorkReportNativeSourcePin pin, long localOrder, int row, int column)
    {
        var order = new Order(pin, localOrder);
        if (_operation.Method is "STACK_ROWS" or "STACK_COLUMNS")
        {
            _budget.Retain(160 + NativeStatisticText.RetainedValueBytes(value));
            _stack.Add(new(order, row, column, source, value));
            return;
        }
        if (value.State != "value") return;
        switch (_operation.Method)
        {
            case "COUNT": break; // Group counters already count cells, not list items.
            case "SUM": case "AVG": _total.Add(value.Text!); break;
            case "TRUE_COUNT": if (value.Boolean == true) _matched++; break;
            case "FALSE_COUNT": if (value.Boolean == false) _matched++; break;
            case "DISTINCT_COUNT":
                var key = NativeStatisticText.TypedKey(value);
                if (_distinct.Add(key)) _budget.Retain(96L + 2L * key.Length + NativeStatisticText.Utf8Bytes(key));
                break;
            case "MIN": case "MAX": case "LATEST": Select(value, source, order); break;
            case "BUCKET_COUNT":
                var date = _operation.Options.GetProperty("mode").GetString() == "date";
                var codes = value.Type == "multiSelect" ? value.Items! : new[] { value.Text! };
                foreach (var code in codes.Distinct(StringComparer.Ordinal))
                {
                    // A target/group has a single type/options domain checked by the compiler.
                    if (_buckets.TryGetValue(code, out var bucket)) _buckets[code] = bucket with { Count = bucket.Count + 1 };
                    else
                    {
                        var label = date ? code : spec.Options!.Single(option => option.Code == code).Label!;
                        _budget.Retain(128L + 2L * (code.Length + label.Length)
                            + NativeStatisticText.Utf8Bytes(code) + NativeStatisticText.Utf8Bytes(label));
                        _buckets.Add(code, new(value.Type, code, label, 1));
                    }
                }
                break;
            case "CONCAT":
                var text = NativeStatisticText.Render(value, spec, _operation.Options, _budget);
                if (_operation.Options.GetProperty("blankPolicy").GetString() == "skip" && string.IsNullOrWhiteSpace(text)) break;
                _budget.Retain(128L + 2L * text.Length + NativeStatisticText.Utf8Bytes(text));
                _text.Add(new(order, text, source));
                break;
            default: throw NativeStatisticCalculationError.Invalid("CALCULATION_METHOD_UNSUPPORTED");
        }
    }

    private int CompareOrder(Order left, Order right)
    {
        var source = ComparePins(left.Pin, right.Pin);
        return source != 0 ? source : left.Cell.CompareTo(right.Cell);
    }

    private int ComparePins(WorkReportNativeSourcePin left, WorkReportNativeSourcePin right)
    {
        if (_target.Order!.Reports == "sourceUpdatedAtAsc")
        {
            if (left.PayloadUpdatedAtUtc is null || right.PayloadUpdatedAtUtc is null)
                throw NativeStatisticCalculationError.Invalid("SOURCE_TIMESTAMP_REQUIRED");
            var time = left.PayloadUpdatedAtUtc.Value.CompareTo(right.PayloadUpdatedAtUtc.Value);
            if (time != 0) return time;
        }
        return string.CompareOrdinal(left.ReportId, right.ReportId);
    }

    private void Select(NativeStatisticValue value, NativeStatisticCellReference source, Order order)
    {
        var comparison = 0;
        if (_operation.Method is "MIN" or "MAX")
        {
            if (value.Type is "date" or "fullDate")
            {
                var currentDate = NativeStatisticText.DateKey(value.Text!);
                if (_datePrecision.HasValue && _datePrecision != currentDate.Precision)
                    throw NativeStatisticCalculationError.Invalid("DATE_EXTREMA_MIXED_PRECISION");
                _datePrecision = currentDate.Precision;
                if (_selected is not null) comparison = currentDate.Key.CompareTo(NativeStatisticText.DateKey(_selected.Text!).Key);
            }
            else if (_selected is not null)
                comparison = decimal.Parse(value.Text!, NumberStyles.Float, CultureInfo.InvariantCulture)
                    .CompareTo(decimal.Parse(_selected.Text!, NumberStyles.Float, CultureInfo.InvariantCulture));
        }
        var replace = _selected is null || (_operation.Method == "LATEST"
            ? CompareOrder(order, _selectedOrder!) > 0
            : comparison == 0 ? CompareOrder(order, _selectedOrder!) < 0
            : _operation.Method == "MIN" ? comparison < 0 : comparison > 0);
        if (!replace) return;
        var bytes = 128 + NativeStatisticText.RetainedValueBytes(value);
        if (bytes > _selectedBytes) { _budget.Retain(bytes - _selectedBytes); _selectedBytes = bytes; }
        (_selected, _selectedSource, _selectedOrder) = (value, source, order);
    }

    internal NativeStatisticOperationResult Finish(NativeStatisticCounts counts)
    {
        NativeStatisticOperationResult Result(string kind, NativeStatisticValue? value = null)
            => new(_operation.OperationId!, _operation.Method!, kind, counts, value);
        NativeStatisticValue Number(long value) => new("number", "value", value.ToString(CultureInfo.InvariantCulture));
        switch (_operation.Method)
        {
            case "COUNT": return Result("scalar", Number(counts.PresentCount));
            case "TRUE_COUNT": case "FALSE_COUNT": return Result("scalar", Number(_matched));
            case "DISTINCT_COUNT": return Result("scalar", Number(_distinct.Count));
            case "SUM": case "AVG":
                var numeric = _total.Finish();
                return Result(_operation.Method == "AVG" ? "average" : "scalar",
                    _operation.Method == "SUM" && numeric.Count > 0 ? new("number", "value", numeric.Sum) : null) with { Numeric = numeric };
            case "MIN": case "MAX": case "LATEST": return Result("scalar", _selected) with { SelectedSource = _selectedSource };
            case "BUCKET_COUNT": return Result("buckets") with { Buckets = _buckets.Values.OrderBy(b => b.Code, StringComparer.Ordinal).ToArray() };
            case "CONCAT": return FinishText(counts);
            case "STACK_ROWS": case "STACK_COLUMNS": return Result("stack") with { Stack = FinishStack() };
            default: throw NativeStatisticCalculationError.Invalid("CALCULATION_METHOD_UNSUPPORTED");
        }
    }

    private NativeStatisticOperationResult FinishText(NativeStatisticCounts counts)
    {
        _text.Sort((a, b) => CompareOrder(a.Order, b.Order));
        var distinct = _operation.Options.GetProperty("duplicatePolicy").GetString() == "distinct";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var separator = _operation.Options.GetProperty("separator").GetString()!;
        var writer = new NativeStatisticTextChunks(_budget);
        var provenance = new List<NativeStatisticTextSegment>();
        long segments = 0, offset = 0;
        foreach (var entry in _text)
        {
            if (distinct && !seen.Add(entry.Text)) continue;
            if (distinct) _budget.Retain(48); // The set references the retained full strings.
            if (segments > 0) { writer.Append(separator); offset += separator.Length; }
            _budget.Retain(128);
            provenance.Add(new(segments, offset, entry.Text.Length, entry.Source));
            writer.Append(entry.Text); offset += entry.Text.Length; segments++;
        }
        return new(_operation.OperationId!, _operation.Method!, "text", counts,
            TextChunks: writer.Finish(), SegmentCount: segments, TextSegments: provenance.ToArray());
    }

    private NativeStatisticStack FinishStack()
    {
        var vertical = _operation.Method == "STACK_ROWS";
        _blocks.Sort((a, b) => ComparePins(a.Pin, b.Pin));
        var blocks = new List<NativeStatisticStackBlock>();
        var offsets = new Dictionary<string, NativeStatisticStackBlock>(StringComparer.Ordinal);
        long rows = 0, columns = 0;
        foreach (var block in _blocks)
        {
            var result = new NativeStatisticStackBlock(block.Pin.ReportId, vertical ? rows : 0,
                vertical ? 0 : columns, block.Rows, block.Columns);
            blocks.Add(result); offsets.Add(block.Pin.ReportId, result); _budget.Retain(128);
            rows = vertical ? rows + block.Rows : Math.Max(rows, block.Rows);
            columns = vertical ? Math.Max(columns, block.Columns) : columns + block.Columns;
        }
        _stack.Sort((a, b) => { var source = ComparePins(a.Order.Pin, b.Order.Pin);
            return source != 0 ? source : a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column); });
        var chunks = new List<NativeStatisticStackChunk>();
        var cells = new List<NativeStatisticStackCell>();
        long chunkBytes = 2;
        void Flush()
        {
            if (cells.Count == 0) return;
            _budget.Retain(64 + chunkBytes); chunks.Add(new(chunks.Count, cells.ToArray()));
            cells.Clear(); chunkBytes = 2;
        }
        foreach (var cell in _stack)
        {
            var offset = offsets[cell.Source.ReportId];
            var result = new NativeStatisticStackCell(offset.RowOffset + cell.Row, offset.ColumnOffset + cell.Column, cell.Source, cell.Value);
            var bytes = NativeStatisticText.Utf8Bytes(JsonSerializer.Serialize(result, StatConfigCanonicalJson.StrictJsonOptions));
            if (bytes + 2L > _budget.Limits.ChunkBytes) throw NativeStatisticCalculationError.Invalid("STACK_CELL_EXCEEDS_CHUNK_BUDGET");
            if (chunkBytes + bytes + (cells.Count > 0 ? 1 : 0) > _budget.Limits.ChunkBytes) Flush();
            chunkBytes += bytes + (cells.Count > 0 ? 1 : 0); cells.Add(result);
        }
        Flush();
        return new(vertical ? "rows" : "columns", rows, columns, _target.Selector!.FieldIds!.ToArray(),
            _target.Selector.RowIds?.ToArray() ?? Array.Empty<string>(), blocks.ToArray(), chunks.ToArray());
    }
}
