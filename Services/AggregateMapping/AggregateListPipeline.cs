using System.Globalization;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateListPipeline
{
    internal static void Validate(AggregateListPipelineDto p)
    {
        if (p.Version is not (1 or 2) || p.Sort == null || p.Project == null || p.Sort.Count > 8
            || p.Project.Count is < 1 or > 200 || p.Project.Any(x => x == null || !Id(x.FieldId) || !Id(x.OutputFieldId))
            || p.Project.Select(x => x.OutputFieldId).Distinct(StringComparer.Ordinal).Count() != p.Project.Count
            || p.Scope is not ("PER_REPORT" or "ALL_SOURCES") || p.Take is not ("ALL" or "TOP_N")
            || (p.Take == "ALL" ? p.TopN != null : p.TopN is not (> 0 and <= 200) || p.Sort.Count == 0)
            || p.Sort.Any(x => x == null || !Id(x.FieldId) || x.Direction is not ("ASC" or "DESC") || x.Nulls != "LAST")
            || p.Sort.Select(x => x.FieldId).Distinct(StringComparer.Ordinal).Count() != p.Sort.Count
            || !(p.Operation is "COLLECT" or "COUNT_RECORDS" or "COUNT_VALUES" or "SUM" or "AVG" or "MIN" or "MAX" or "ONLY_ITEM"
                || p.Version == 2 && p.Operation is "AVG_PRESENT" or "WEIGHTED_AVG" or "COUNT_DISTINCT_FIELD")
            || (p.Operation == "WEIGHTED_AVG" ? !Id(p.WeightFieldId) : p.WeightFieldId != null)
            || (p.Operation != "COUNT_DISTINCT_FIELD" && (p.Trim != null || p.CaseSensitive != null))
            || (p.Operation is "COLLECT" or "COUNT_RECORDS" ? p.ValueFieldId != null : !Id(p.ValueFieldId)))
            throw new AggregatePreviewException("AGG_LIST_PIPELINE_SCHEMA");
        var count = 0;
        if (p.Where != null) Check(p.Where, 0);
        void Check(AggregateListPredicateDto predicate, int depth)
        {
            if (++count > 30 || depth > 4) throw new AggregatePreviewException("AGG_LIST_PREDICATE_LIMIT");
            if (predicate.Operator is "AND" or "OR")
            {
                if (predicate.Children is not { Count: > 0 } || predicate.Children.Any(c => c == null)
                    || predicate.FieldId != null || predicate.Values != null || predicate.Trim != null || predicate.CaseSensitive != null)
                    throw new AggregatePreviewException("AGG_LIST_PREDICATE_SCHEMA");
                foreach (var child in predicate.Children) Check(child, depth + 1);
                return;
            }
            if (!Id(predicate.FieldId) || predicate.Children != null) throw new AggregatePreviewException("AGG_LIST_PREDICATE_SCHEMA");
            var size = predicate.Operator switch {
                "IS_BLANK" or "IS_PRESENT" or "IS_TRUE" or "IS_FALSE" => 0,
                "BETWEEN" => 2,
                "IN" or "NOT_IN" or "HAS_ANY" or "HAS_ALL" or "HAS_NONE" or "EQUALS_SET" => -1,
                "EQ" or "NE" or "LT" or "LTE" or "GT" or "GTE" or "CONTAINS" or "NOT_CONTAINS" or "STARTS" or "ENDS" => 1,
                _ => throw new AggregatePreviewException("AGG_LIST_PREDICATE_OPERATOR") };
            var values = predicate.Values;
            if (size == 0 ? values != null : values == null || values.Any(v => v == null || v.Length > 4096)
                || (size > 0 ? values.Count != size : values.Count is < 1 or > 100 || values.Distinct(StringComparer.Ordinal).Count() != values.Count))
                throw new AggregatePreviewException("AGG_LIST_PREDICATE_OPERAND");
        }
    }
    internal static AggregateExpressionType Infer(AggregateListPipelineDto p, AggregateExpressionType input)
    {
        Validate(p);
        if (input.Type != "LIST" || input.ListSchema == null) throw new AggregatePreviewException("AGG_LIST_SOURCE_TYPE");
        var schema = input.ListSchema;
        string Type(string id) => schema.Fields.SingleOrDefault(f => f.Id == id)?.Type
            ?? throw new AggregatePreviewException("AGG_LIST_FIELD_REF");
        foreach (var field in p.Project) _ = Type(field.FieldId);
        foreach (var sort in p.Sort)
        {
            var type = Type(sort.FieldId);
            if (type is "CHOICE_MANY" or "DATE_PARTIAL" or "UNSUPPORTED") throw new AggregatePreviewException("AGG_LIST_SORT_TYPE");
            if (type == "CHOICE_ONE" ? sort.ChoiceOrder != "CODE" : sort.ChoiceOrder != null) throw new AggregatePreviewException("AGG_LIST_SORT_TYPE");
            CheckTextOptions(type, sort.Trim, sort.CaseSensitive);
        }
        if (p.Where != null) CheckPredicate(p.Where);
        var projected = new AggregateListSchema(p.Project.Select(f => schema.Fields.Single(s => s.Id == f.FieldId) with { Id = f.OutputFieldId }).ToArray());
        if (p.Operation == "COLLECT") return new("LIST", "SINGLE", ListSchema: projected);
        var scalarType = p.ValueFieldId == null ? null : projected.Fields.SingleOrDefault(f => f.Id == p.ValueFieldId)?.Type
            ?? (p.ValueFieldId == null ? null : throw new AggregatePreviewException("AGG_LIST_FIELD_REF"));
        if (p.Operation is "SUM" or "AVG" or "MIN" or "MAX" or "AVG_PRESENT" or "WEIGHTED_AVG" && scalarType != "NUMBER") throw new AggregatePreviewException("AGG_LIST_NUMERIC_FIELD_REQUIRED");
        if (p.Operation == "WEIGHTED_AVG" && projected.Fields.SingleOrDefault(f => f.Id == p.WeightFieldId)?.Type != "NUMBER")
            throw new AggregatePreviewException("AGG_LIST_NUMERIC_FIELD_REQUIRED");
        if (p.Operation == "COUNT_DISTINCT_FIELD")
        {
            if (scalarType is "LIST" or "TABLE" or "CHOICE_MANY" or "UNSUPPORTED") throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
            CheckTextOptions(scalarType!, p.Trim, p.CaseSensitive);
        }
        return new(p.Operation == "ONLY_ITEM" ? scalarType! : "NUMBER", "SINGLE");

        void CheckPredicate(AggregateListPredicateDto predicate)
        {
            if (predicate.Children != null) { foreach (var c in predicate.Children) CheckPredicate(c); return; }
            var type = Type(predicate.FieldId!);
            var op = predicate.Operator;
            if (op is "IS_BLANK" or "IS_PRESENT")
            {
                if (predicate.Trim != null || predicate.CaseSensitive != null) throw new AggregatePreviewException("AGG_LIST_TEXT_OPTIONS");
                return;
            }
            var allowed = type switch {
                "NUMBER" or "DATE_ONLY" or "DATE_PARTIAL" => new[] { "EQ", "NE", "LT", "LTE", "GT", "GTE", "BETWEEN" },
                "TEXT" => ["EQ", "NE", "CONTAINS", "NOT_CONTAINS", "STARTS", "ENDS"],
                "BOOLEAN" => ["EQ", "NE", "IS_TRUE", "IS_FALSE"],
                "CHOICE_ONE" => ["IN", "NOT_IN"],
                "CHOICE_MANY" => ["HAS_ANY", "HAS_ALL", "HAS_NONE", "EQUALS_SET"],
                _ => [] };
            if (!allowed.Contains(op, StringComparer.Ordinal)) throw new AggregatePreviewException("AGG_LIST_PREDICATE_TYPE");
            CheckTextOptions(type, predicate.Trim, predicate.CaseSensitive);
            if (type is "CHOICE_ONE" or "CHOICE_MANY" && schema.Fields.Single(f => f.Id == predicate.FieldId).AllowedChoiceCodes is { } allowedCodes
                && predicate.Values!.Any(v => !allowedCodes.Contains(v, StringComparer.Ordinal))) throw new AggregatePreviewException("AGG_LIST_CHOICE_UNAVAILABLE");
            var openDateRange = op == "BETWEEN" && type is "DATE_ONLY" or "DATE_PARTIAL";
            foreach (var literal in predicate.Values ?? [])
                if (!(openDateRange && literal.Length == 0)) _ = Literal(type, literal);
            if (op == "BETWEEN")
            {
                if (type == "DATE_PARTIAL") AggregatePartialDate.CheckBounds(predicate.Values![0], predicate.Values[1]);
                else if ((!openDateRange || predicate.Values!.All(v => v.Length > 0))
                    && Compare(Literal(type, predicate.Values![0]), Literal(type, predicate.Values[1]), null, null) > 0)
                    throw new AggregatePreviewException("AGG_LIST_PREDICATE_OPERAND");
            }
        }
    }
    internal static AggregateChannel Evaluate(AggregateListPipelineDto p, AggregateChannel input,
        AggregateBudget budget, List<AggregateListPipelineTrace> traces, List<AggregateListValue>? selections = null)
    {
        var selection = new Selection(p, input, budget);
        foreach (var observation in input.Items)
        {
            selection.Observe(observation);
            if (observation.Value.State != "VALUE" || observation.Value.List == null)
                throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
            foreach (var row in observation.Value.List.Records) selection.Add(row);
        }
        return selection.Finish(traces, selections);
    }
    internal static async Task<AggregateChannel> EvaluateAsync(AggregateListPipelineDto p, AggregateChannel input,
        AggregateBudget budget, List<AggregateListPipelineTrace> traces, List<AggregateListValue> selections,
        IAggregateListSink store, AggregateReadContext context, CancellationToken ct)
    {
        var selection = new Selection(p, input, budget);
        foreach (var observation in input.Items)
        {
            selection.Observe(observation);
            if (observation.Value.State != "VALUE") throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
            if (observation.Value.List is { } list) foreach (var row in list.Records) selection.Add(row);
            else if (observation.Value.ListReference is { } reference)
            {
                await foreach (var row in store.ReadRowsAsync(context, reference, ct)) selection.Add(row);
            }
            else throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
        }
        return selection.Finish(traces, selections);
    }
    // Top-N retains at most N rows per requested scope. Source snapshots are scanned in bounded pages;
    // excluded long text is released before reading the next page. ALL remains explicitly budget bounded.
    private sealed class Selection
    {
        private readonly AggregateListPipelineDto p;
        private readonly AggregateChannel input;
        private readonly AggregateBudget budget;
        private readonly AggregateExpressionType inferred;
        private readonly IComparer<AggregateListRow> comparer;
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);
        private readonly Dictionary<(string?, string), PriorityQueue<AggregateListRow, AggregateListRow>> heaps = [];
        private readonly Dictionary<(string?, string), (int Read, int Matched)> counts = [];
        private readonly List<AggregateListRow> all = [];
        private readonly HashSet<string> retainedFields;
        private long bytes;
        private int matched;
        internal Selection(AggregateListPipelineDto plan, AggregateChannel channel, AggregateBudget limit)
        {
            p = plan; input = channel; budget = limit;
            inferred = Infer(p, new(input.Type, input.Shape, ListSchema: input.ListSchema));
            retainedFields = p.Project.Select(f => f.FieldId).Concat(p.Sort.Select(f => f.FieldId)).ToHashSet(StringComparer.Ordinal);
            comparer = Comparer<AggregateListRow>.Create((a, b) => {
                budget.Spend();
                foreach (var key in p.Sort)
                {
                    var left = a.Cells[key.FieldId].Value; var right = b.Cells[key.FieldId].Value;
                    var leftBlank = left.State != "VALUE"; var rightBlank = right.State != "VALUE";
                    if (leftBlank != rightBlank) return leftBlank ? 1 : -1;
                    if (leftBlank) continue;
                    var comparison = Compare(left, right, key.Trim, key.CaseSensitive);
                    if (comparison != 0) return key.Direction == "ASC" ? comparison : -comparison;
                }
                return Tie(a.Origin, b.Origin);
            });
        }
        internal void Add(AggregateListRow row)
        {
            budget.Spend();
            if (!seen.Add(row.Key)) throw new AggregatePreviewException("AGG_LIST_ID_COLLISION");
            if (seen.Count > 40_000) throw new AggregatePreviewException("AGG_LIST_RECORD_LIMIT");
            // Normalize derived values, including snapshots from before the blank rule was
            // approved. Keep source payload, row identity and cell lineage untouched.
            if (row.Cells.Any(c => c.Value.Value is { Type: "CHOICE_MANY", State: "VALUE", Choices.Count: 0 }))
                row = row with { Cells = row.Cells.ToDictionary(c => c.Key,
                    c => c.Value with { Value = AggregateListChoicePolicy.Normalize(c.Value.Value) }, StringComparer.Ordinal) };
            var origin = (row.Origin.Pin?.ReportId, row.Origin.SourceSlot);
            counts.TryGetValue(origin, out var count); counts[origin] = (count.Read + 1, count.Matched);
            if (p.Where != null) CheckTextPredicate(p.Where, row);
            foreach (var sort in p.Sort) AggregateTextPolicy.CheckValueOperation("SORT", row.Cells[sort.FieldId].Value);
            if (p.Where != null && !Match(p.Where, row, budget)) return;
            matched++; counts[origin] = (count.Read + 1, count.Matched + 1);
            row = row with { Cells = row.Cells.Where(c => retainedFields.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal) };
            if (p.Take == "ALL") { all.Add(row); bytes += Size(row); }
            else
            {
                var key = p.Scope == "PER_REPORT" ? origin : (null, "");
                if (!heaps.TryGetValue(key, out var heap)) heaps[key] = heap = new(Comparer<AggregateListRow>.Create((a, b) => comparer.Compare(b, a)));
                if (heap.Count < p.TopN!.Value) { heap.Enqueue(row, row); bytes += Size(row); }
                else if (comparer.Compare(row, heap.Peek()) < 0)
                { bytes -= Size(heap.Dequeue()); heap.Enqueue(row, row); bytes += Size(row); }
            }
            if (bytes > 16_777_216) throw new AggregatePreviewException("AGG_LIST_SELECTION_BUDGET");
        }
        internal void Observe(AggregateObservation observation)
        {
            if (observation.Trace.FirstOrDefault() is { SourceSlot: not null } trace)
                counts.TryAdd((trace.ReportId, trace.SourceSlot), (0, 0));
        }
        private static long Size(AggregateListRow row) => 256L + row.Cells.Sum(c => 512L + (c.Value.Value.Text?.Length ?? 0) * 2L
            + (c.Value.Value.Choices?.Sum(v => v.Length * 2L + 32) ?? 0));
        internal AggregateChannel Finish(List<AggregateListPipelineTrace> traces, List<AggregateListValue>? selections)
        {
            var groups = p.Take == "ALL" ? p.Scope == "PER_REPORT"
                    ? all.GroupBy(r => (r.Origin.Pin?.ReportId, r.Origin.SourceSlot)).Select(g => g.ToList()).ToList() : [all]
                : heaps.Values.Select(h => h.UnorderedItems.Select(v => v.Element).ToList()).ToList();
            if (p.Sort.Count > 0) foreach (var group in groups) group.Sort(comparer);
            var selected = groups.SelectMany(g => g).ToList();
            budget.Spend(bytes: bytes + seen.Count * 160L);
        var projected = selected.Select(row => row with { Cells = p.Project.ToDictionary(
            f => f.OutputFieldId, f => row.Cells[f.FieldId], StringComparer.Ordinal) }).ToArray();
        selections?.Add(new(new(p.Project.Select(f => input.ListSchema!.Fields.Single(s => s.Id == f.FieldId) with { Id = f.OutputFieldId }).ToArray()), projected));
        var allTrace = projected.SelectMany(r => r.Cells.Values.SelectMany(c => c.Trace)).ToArray();
        var selectedCounts = selected.GroupBy(r => (r.Origin.Pin?.ReportId, r.Origin.SourceSlot)).ToDictionary(g => g.Key, g => g.Count());
        traces.Add(new(p.Scope, seen.Count, matched, projected.Length, matched - projected.Length, p.Operation, null, 0) {
            Reports = counts.Select(c => new AggregateListReportCount(c.Key.Item1, c.Key.Item2, c.Value.Read, c.Value.Matched,
                selectedCounts.GetValueOrDefault(c.Key))).ToArray()
        });
        if (p.Operation == "COLLECT")
        {
            var value = new AggregateValue("LIST", "VALUE") { List = new(inferred.ListSchema!, projected) };
            return AggregateEvaluator.Single(value, allTrace) with { ListSchema = inferred.ListSchema, EligibleSources = input.EligibleSources };
        }
        if (p.Operation == "COUNT_RECORDS") return AggregateEvaluator.Single(AggregateValue.Numeric(AggregateNumber.From(projected.Length)), allTrace);
        if (p.Operation == "ONLY_ITEM")
        {
            if (projected.Length > 1) throw new AggregatePreviewException("LIST_SINGLE_ITEM_REQUIRED");
            if (projected.Length == 0) return AggregateEvaluator.Single(AggregateValue.NoResult(inferred.Type), []);
            var cell = projected[0].Cells[p.ValueFieldId!];
            return AggregateEvaluator.Single(cell.Value, cell.Trace);
        }
        var values = projected.Select(r => r.Cells[p.ValueFieldId!]).ToArray();
        var present = values.Where(v => v.Value.State == "VALUE").ToArray();
        var trace = values.SelectMany(v => v.Trace).ToArray();
        traces[^1] = traces[^1] with { BlankCount = values.Length - present.Length };
        if (p.Operation == "COUNT_DISTINCT_FIELD")
        {
            foreach (var cell in values) AggregateTextPolicy.CheckValueOperation("COUNT_DISTINCT", cell.Value);
            var keys = values.Where(c => c.Value.State != "NO_RESULT").Select(c => c.Value.State == "BLANK" ? "BLANK" : c.Value.Type + ":" + (c.Value.Type switch {
                "NUMBER" => c.Value.Number!.ExactKey, "BOOLEAN" => c.Value.Boolean!.Value.ToString(),
                "TEXT" => Normalize(c.Value.Text!, p.Trim, p.CaseSensitive), _ => c.Value.Text! })).Distinct(StringComparer.Ordinal).LongCount();
            return AggregateEvaluator.Single(AggregateValue.Numeric(AggregateNumber.From(keys)), trace);
        }
        if (p.Operation == "WEIGHTED_AVG")
        {
            var numerator = AggregateNumber.From(0); var denominator = AggregateNumber.From(0);
            var weightedTrace = new List<AggregateTrace>();
            foreach (var row in projected)
            {
                budget.Spend();
                var weight = row.Cells[p.WeightFieldId!]; var score = row.Cells[p.ValueFieldId!];
                weightedTrace.AddRange(weight.Trace); weightedTrace.AddRange(score.Trace);
                if (weight.Value.State != "VALUE" || weight.Value.Number!.Compare(AggregateNumber.From(0)) < 0)
                    throw new AggregatePreviewException("AGG_LIST_WEIGHT_INVALID");
                if (weight.Value.Number.Compare(AggregateNumber.From(0)) == 0) continue;
                if (score.Value.State != "VALUE") throw new AggregatePreviewException("AGG_LIST_SCORE_MISSING");
                numerator = numerator.Add(score.Value.Number!.Multiply(weight.Value.Number));
                denominator = denominator.Add(weight.Value.Number);
            }
            traces[^1] = traces[^1] with { ExactTotal = numerator.ExactKey };
            return AggregateEvaluator.Single(denominator.Compare(AggregateNumber.From(0)) == 0 ? AggregateValue.NoResult("NUMBER")
                : AggregateValue.Numeric(numerator.Divide(denominator)), weightedTrace);
        }
        if (p.Operation == "COUNT_VALUES") return AggregateEvaluator.Single(AggregateValue.Numeric(AggregateNumber.From(present.Length)), trace);
        if (values.Length == 0 || (present.Length == 0 && p.Operation != "AVG"))
            return AggregateEvaluator.Single(AggregateValue.NoResult("NUMBER"), trace);
        var sum = present.Aggregate(AggregateNumber.From(0), (n, c) => n.Add(c.Value.Number!));
        traces[^1] = traces[^1] with { ExactTotal = sum.ExactKey };
        var number = p.Operation switch {
            "SUM" => sum, "AVG" => sum.Divide(AggregateNumber.From(values.Length)), "AVG_PRESENT" => sum.Divide(AggregateNumber.From(present.Length)),
            "MIN" => present.Select(v => v.Value.Number!).Aggregate((a, b) => a.Compare(b) <= 0 ? a : b),
            "MAX" => present.Select(v => v.Value.Number!).Aggregate((a, b) => a.Compare(b) >= 0 ? a : b),
            _ => throw new AggregatePreviewException("AGG_LIST_PIPELINE_SCHEMA") };
        return AggregateEvaluator.Single(AggregateValue.Numeric(number), trace);
        }
    }
    private static void CheckTextPredicate(AggregateListPredicateDto p, AggregateListRow row)
    {
        if (p.Children != null) { foreach (var child in p.Children) CheckTextPredicate(child, row); return; }
        if (p.Operator is not ("IS_BLANK" or "IS_PRESENT")) AggregateTextPolicy.CheckValueOperation(p.Operator, row.Cells[p.FieldId!].Value);
    }
    internal static bool Matches(AggregateListPredicateDto p, AggregateListRow row, AggregateBudget budget)
    { CheckTextPredicate(p, row); return Match(p, row, budget); }
    private static bool Match(AggregateListPredicateDto p, AggregateListRow row, AggregateBudget budget)
    {
        budget.Spend();
        if (p.Operator == "AND") return p.Children!.All(c => Match(c, row, budget));
        if (p.Operator == "OR") return p.Children!.Any(c => Match(c, row, budget));
        var value = row.Cells[p.FieldId!].Value;
        if (p.Operator == "IS_BLANK") return value.State == "BLANK";
        if (p.Operator == "IS_PRESENT") return value.State == "VALUE";
        var operands = p.Values ?? [];
        if (p.Operator == "BETWEEN" && value.Type is "DATE_ONLY" or "DATE_PARTIAL")
        {
            if (operands.All(v => v.Length == 0)) return true;
            if (value.State != "VALUE") return false;
            return (operands[0].Length == 0 || Compare(value, Literal(value.Type, operands[0]), null, null) >= 0)
                && (operands[1].Length == 0 || Compare(value, Literal(value.Type, operands[1]), null, null) <= 0);
        }
        if (value.State != "VALUE") return false;
        if (value.Type == "CHOICE_ONE") return p.Operator == "IN" ? operands.Contains(value.Text!, StringComparer.Ordinal) : !operands.Contains(value.Text!, StringComparer.Ordinal);
        if (value.Type == "CHOICE_MANY")
        {
            var codes = value.Choices!.ToHashSet(StringComparer.Ordinal);
            return p.Operator switch { "HAS_ANY" => operands.Any(codes.Contains), "HAS_ALL" => operands.All(codes.Contains),
                "HAS_NONE" => !operands.Any(codes.Contains), "EQUALS_SET" => codes.SetEquals(operands), _ => false };
        }
        if (p.Operator == "IS_TRUE") return value.Boolean == true;
        if (p.Operator == "IS_FALSE") return value.Boolean == false;
        if (value.Type == "TEXT")
        {
            var text = Normalize(AggregateTextProjection.VisibleText(value), p.Trim, p.CaseSensitive);
            var needle = Normalize(operands[0], p.Trim, p.CaseSensitive);
            return p.Operator switch { "EQ" => text == needle, "NE" => text != needle,
                "CONTAINS" => text.Contains(needle, StringComparison.Ordinal), "NOT_CONTAINS" => !text.Contains(needle, StringComparison.Ordinal),
                "STARTS" => text.StartsWith(needle, StringComparison.Ordinal), "ENDS" => text.EndsWith(needle, StringComparison.Ordinal), _ => false };
        }
        var cmp = Compare(value, Literal(value.Type, operands[0]), null, null);
        return p.Operator switch { "EQ" => cmp == 0, "NE" => cmp != 0, "LT" => cmp < 0, "LTE" => cmp <= 0,
            "GT" => cmp > 0, "GTE" => cmp >= 0, "BETWEEN" => cmp >= 0 && Compare(value, Literal(value.Type, operands[1]), null, null) <= 0, _ => false };
    }
    private static AggregateValue Literal(string type, string value)
    {
        if (type == "NUMBER") return AggregateValue.Numeric(AggregateNumber.Parse(value));
        if (type == "BOOLEAN") return value is "true" or "false" ? new(type, "VALUE", Boolean: value == "true") : throw new AggregatePreviewException("AGG_LIST_PREDICATE_OPERAND");
        if (type == "DATE_ONLY" && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new AggregatePreviewException("AGG_LIST_PREDICATE_OPERAND");
        if (type == "DATE_PARTIAL") value = AggregatePartialDate.Parse(value).Text;
        return new(type, "VALUE", Text: value);
    }
    private static int Compare(AggregateValue a, AggregateValue b, bool? trim, bool? caseSensitive)
        => a.Type == "NUMBER" ? a.Number!.Compare(b.Number!) : a.Type == "BOOLEAN" ? a.Boolean!.Value.CompareTo(b.Boolean!.Value)
            : a.Type == "DATE_PARTIAL" ? AggregatePartialDate.Compare(a.Text!, b.Text!)
            : string.CompareOrdinal(Normalize(a.Text!, trim, caseSensitive), Normalize(b.Text!, trim, caseSensitive));
    private static int Tie(AggregateListOrigin a, AggregateListOrigin b)
    {
        foreach (var (left, right) in new[] { (a.Pin?.ReportId, b.Pin?.ReportId), (a.ListId, b.ListId), (a.RecordId, b.RecordId), (a.SourceSlot, b.SourceSlot) })
        { var result = string.CompareOrdinal(left, right); if (result != 0) return result; }
        return 0;
    }
    private static void CheckTextOptions(string type, bool? trim, bool? caseSensitive)
    {
        if (type == "TEXT" ? trim == null || caseSensitive == null : trim != null || caseSensitive != null)
            throw new AggregatePreviewException("AGG_LIST_TEXT_OPTIONS");
    }
    private static string Normalize(string text, bool? trim, bool? caseSensitive)
        => caseSensitive == false ? (trim == true ? text.Trim() : text).ToUpperInvariant() : trim == true ? text.Trim() : text;
    private static bool Id(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256;
}
