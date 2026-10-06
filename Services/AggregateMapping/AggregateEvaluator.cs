using System.Globalization;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal sealed record AggregateExpressionType(string Type, string Shape, bool TableOrigin = false, AggregateListSchema? ListSchema = null);
internal sealed class AggregateEvaluator(AggregateBudget budget, IReadOnlyList<string> unitIds)
{
    internal List<AggregateFunctionTrace> FunctionTrace { get; } = [];
    internal List<AggregateListPipelineTrace> ListTrace { get; } = [];
    internal List<AggregateListValue> ListSelections { get; } = [];
    internal async Task<AggregateChannel> EvaluateWithListsAsync(AggregateExpressionDto expression,
        IReadOnlyDictionary<string, AggregateChannel> inputs, IAggregateListSink store, AggregateReadContext context, CancellationToken ct)
    {
        if (expression.Kind == "LIST_MERGE") return await AggregateListMerge.EvaluateAsync(expression.ListMerge!, inputs, budget, ListTrace, ListSelections, store, context, ct);
        if (expression.Kind == "CALL" && expression.Name == "REPORT_COUNT")
        {
            var source = await EvaluateWithListsAsync(expression.Arguments![0], inputs, store, context, ct);
            if (source.Type != "LIST") return AggregateExtendedFunctions.Evaluate("REPORT_COUNT", source, expression.Options, budget);
            var reports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in source.Items)
            {
                if (observation.Value.State != "VALUE") throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
                if (expression.Options!.Basis == "ELIGIBLE_SOURCES") continue;
                if (observation.Value.List is { } list) foreach (var row in list.Records) Add(row);
                else if (observation.Value.ListReference is { } reference) await foreach (var row in store.ReadRowsAsync(context, reference, ct)) Add(row);
                else throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
            }
            var eligible = source.EligibleSources;
            var ids = expression.Options!.Basis == "ELIGIBLE_SOURCES" ? eligible.Select(t => t.ReportId).Distinct(StringComparer.Ordinal).LongCount() : reports.Count;
            return Single(AggregateValue.Numeric(AggregateNumber.From(ids)), eligible.Where(t => expression.Options.Basis == "ELIGIBLE_SOURCES" || reports.Contains(t.ReportId)).ToArray()) with { EligibleSources = eligible };
            void Add(AggregateListRow row) { budget.Spend(); foreach (var trace in row.Cells.Values.SelectMany(c => c.Trace)) reports.Add(trace.ReportId); }
        }
        if (expression.Kind == "CALL" && expression.Name == "IF")
        {
            var condition = Scalar(await EvaluateWithListsAsync(expression.Arguments![0], inputs, store, context, ct));
            var type = Infer(expression, inputs.ToDictionary(p => p.Key, p => new AggregateExpressionType(p.Value.Type, p.Value.Shape, p.Value.TableOrigin, p.Value.ListSchema)));
            if (condition.Value.State != "VALUE") return Single(AggregateValue.NoResult(type.Type), condition.Trace, type.TableOrigin);
            var chosen = await EvaluateWithListsAsync(expression.Arguments[condition.Value.Boolean == true ? 1 : 2], inputs, store, context, ct);
            return chosen with { Items = chosen.Items.Select(v => v with { Trace = condition.Trace.Concat(v.Trace).ToArray() }).ToArray() };
        }
        var prepared = inputs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        async Task<AggregateExpressionDto> Prepare(AggregateExpressionDto e)
        {
            if (e.Kind is "LIST_PIPELINE" or "LIST_MERGE" || e.Kind == "CALL" && e.Name is "IF" or "REPORT_COUNT")
            {
                var key = "__list_" + Guid.NewGuid().ToString("N");
                prepared[key] = e.Kind == "LIST_PIPELINE"
                    ? await AggregateListPipeline.EvaluateAsync(e.ListPipeline!, prepared[e.Ref!], budget, ListTrace, ListSelections, store, context, ct)
                    : await EvaluateWithListsAsync(e, prepared, store, context, ct);
                if (e.Kind == "LIST_PIPELINE") prepared[key] = prepared[key] with { EligibleSources = prepared[e.Ref!].EligibleSources };
                return new() { Kind = "INPUT", Ref = key };
            }
            if (e.Arguments == null) return e;
            var arguments = new List<AggregateExpressionDto>();
            foreach (var child in e.Arguments) arguments.Add(await Prepare(child));
            return e with { Arguments = arguments };
        }
        return Evaluate(await Prepare(expression), prepared);
    }
    internal AggregateExpressionType Infer(AggregateExpressionDto e, IReadOnlyDictionary<string, AggregateExpressionType> inputs)
    {
        budget.Spend();
        if (e.Kind == "LIST_MERGE") return AggregateListMerge.Infer(e.ListMerge!, inputs);
        if (e.Kind == "LIST_PIPELINE")
        {
            if (!inputs.TryGetValue(e.Ref!, out var listType)) throw new AggregatePreviewException("AGG_EXPRESSION_REF");
            return AggregateListPipeline.Infer(e.ListPipeline!, listType);
        }
        if (e.Kind is "NUMBER" or "TEXT" or "BOOLEAN" or "DATE_ONLY" or "DATE_PARTIAL" or "INSTANT") return new(e.Kind, "SINGLE");
        if (e.Kind == "GET") return new("NUMBER", "SINGLE");
        if (e.Kind is "INPUT" or "TABLE_RANGE" or "TABLE_FILTER")
        {
            if (!inputs.TryGetValue(e.Ref!, out var type)) throw new AggregatePreviewException("AGG_EXPRESSION_REF");
            if (e.Kind != "INPUT" && type.Type != "TABLE") throw new AggregatePreviewException("AGG_TABLE_TYPE");
            return e.Kind != "INPUT" ? new("TABLE", type.Shape, true) : type;
        }
        var args = e.Arguments!.Select(a => Infer(a, inputs)).ToArray();
        if (e.Kind == "CALL" && AggregateExtendedFunctions.Names.Contains(e.Name ?? ""))
            return AggregateExtendedFunctions.Infer(e.Name!, args[0]);
        if (args.Any(a => a.Type == "LIST")) throw new AggregatePreviewException("AGG_LIST_OPERATOR_REQUIRED");
        var tableOrigin = args.Any(a => a.TableOrigin);
        if (e.Kind == "BINARY")
        {
            if (args.Any(a => a.Shape != "SINGLE") || args[0].Type != args[1].Type || args[0].Type is "TABLE" or "CHOICE_MANY")
                throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
            var numeric = e.Name is "+" or "-" or "*" or "/";
            if ((numeric && args[0].Type != "NUMBER") || (e.Name is "AND" or "OR" && args[0].Type != "BOOLEAN"))
                throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
            return new(numeric ? "NUMBER" : "BOOLEAN", "SINGLE", tableOrigin);
        }
        if (e.Name == "IF")
        {
            if (args[0].Type != "BOOLEAN" || args[0].Shape != "SINGLE" || args[1].Type != args[2].Type || args[1].Shape != args[2].Shape)
                throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
            return args[1] with { TableOrigin = tableOrigin };
        }
        var first = args[0];
        if (e.Name == "REPORT_TEXT_TABLE")
        {
            if (first.Type != "TEXT" || first.Shape != "SET" || e.Arguments![0].Kind != "INPUT")
                throw new AggregatePreviewException("AGG_CONTENT_TABLE_SOURCE");
            return new("TABLE", "SINGLE", true);
        }
        if (e.Name == "ONLY") return first with { Shape = "SINGLE" };
        if (e.Name is "IS_BLANK" or "IS_PRESENT")
        {
            if (first.Shape != "SINGLE") throw new AggregatePreviewException("AGG_EXPRESSION_SHAPE");
            return new("BOOLEAN", "SINGLE", tableOrigin);
        }
        if (e.Name is "HAS_CHOICE" or "TEXT_CONTAINS" or "TEXT_STARTS" or "TEXT_ENDS" or "TEXT_EQUALS")
        {
            if (args.Any(a => a.Shape != "SINGLE") || args[1].Type != "TEXT"
                || (e.Name == "HAS_CHOICE" ? first.Type is not ("CHOICE_ONE" or "CHOICE_MANY") : first.Type != "TEXT"))
                throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
            return new("BOOLEAN", "SINGLE", tableOrigin);
        }
        if (e.Name is "SUM" or "AVG" or "MIN" or "MAX" && first.Type is not ("NUMBER" or "TABLE")) throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
        if (e.Name == "CONCAT" && first.Type != "TEXT") throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
        if (e.Name == "APPEND_TABLE" && first.Type != "TABLE") throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
        if (e.Name == "COUNT_DISTINCT" && first.Type == "TABLE") throw new AggregatePreviewException("AGG_TABLE_JOIN_DEFERRED");
        if (e.Name == "COUNT_DISTINCT" && first.Type == "CHOICE_MANY") throw new AggregatePreviewException("AGG_EXPRESSION_TYPE");
        if (e.Name == "COUNT" && ((e.Options!.Basis is "ROWS" or "PRESENT_ROWS") != (first.Type == "TABLE")))
            throw new AggregatePreviewException("AGG_COUNT_BASIS_TYPE");
        return new(e.Name == "CONCAT" ? "TEXT" : e.Name == "APPEND_TABLE" ? "TABLE" : "NUMBER", "SINGLE", tableOrigin);
    }

    internal AggregateChannel Evaluate(AggregateExpressionDto e, IReadOnlyDictionary<string, AggregateChannel> inputs)
    {
        var answer = EvaluateCore(e, inputs);
        IEnumerable<string> Refs(AggregateExpressionDto x) => x.Kind == "LIST_MERGE" ? x.ListMerge!.Inputs.Select(i => i.Ref) : x.Kind is "INPUT" or "LIST_PIPELINE" ? [x.Ref!]
            : (x.Arguments ?? []).SelectMany(Refs);
        return answer with { EligibleSources = Refs(e).Distinct(StringComparer.Ordinal)
            .Where(inputs.ContainsKey).SelectMany(r => inputs[r].EligibleSources).Distinct().ToArray() };
    }
    private AggregateChannel EvaluateCore(AggregateExpressionDto e, IReadOnlyDictionary<string, AggregateChannel> inputs)
    {
        budget.Spend();
        if (e.Kind == "LIST_MERGE") return AggregateListMerge.EvaluateAsync(e.ListMerge!, inputs, budget, ListTrace, ListSelections).GetAwaiter().GetResult();
        if (e.Kind == "LIST_PIPELINE") return AggregateListPipeline.Evaluate(e.ListPipeline!, inputs[e.Ref!], budget, ListTrace, ListSelections);
        if (e.Kind == "INPUT") return inputs[e.Ref!];
        if (e.Kind == "TABLE_RANGE") return AggregateTableAdapter.Select(inputs[e.Ref!], e.Area!, budget);
        if (e.Kind == "TABLE_FILTER") return AggregateTableAdapter.FilterRegion(inputs[e.Ref!], e.Area!, e.ColumnIndex!.Value, e.Predicate!, this, budget);
        if (e.Kind == "GET") return Single(AggregateValue.Numeric(AggregateNumber.From(unitIds.Distinct(StringComparer.Ordinal).Count())), []);
        if (e.Kind is "NUMBER" or "TEXT" or "BOOLEAN" or "DATE_ONLY" or "DATE_PARTIAL" or "INSTANT")
            return Single(e.Kind switch
            {
                "NUMBER" => AggregateValue.Numeric(AggregateNumber.Parse(e.Value!)),
                "BOOLEAN" => new(e.Kind, "VALUE", Boolean: e.Value == "true"),
                _ => new(e.Kind, "VALUE", Text: e.Value)
            }, []);
        var a = Evaluate(e.Arguments![0], inputs);
        if (e.Name is "TEXT_CONTAINS" or "TEXT_STARTS" or "TEXT_ENDS" or "TEXT_EQUALS" or "COUNT_DISTINCT"
            || e.Name == "COUNT" && e.Options?.Basis == "VALUES")
            AggregateTextPolicy.CheckValueOperation(e.Name!, a);
        if (e.Kind == "CALL" && AggregateExtendedFunctions.Names.Contains(e.Name ?? ""))
            return AggregateExtendedFunctions.Evaluate(e.Name!, a, e.Options, budget);
        if (e.Name is "IS_BLANK" or "IS_PRESENT")
        {
            var item = Scalar(a);
            return Single(new("BOOLEAN", "VALUE", Boolean: e.Name == "IS_BLANK" ? item.Value.State == "BLANK" : item.Value.State == "VALUE"), item.Trace, a.TableOrigin);
        }
        if (e.Name is "HAS_CHOICE" or "TEXT_CONTAINS" or "TEXT_STARTS" or "TEXT_ENDS" or "TEXT_EQUALS")
        {
            var left = Scalar(a); var right = Scalar(Evaluate(e.Arguments[1], inputs));
            var traces = left.Trace.Concat(right.Trace).ToArray();
            if (left.Value.State != "VALUE" || right.Value.State != "VALUE") return Single(AggregateValue.NoResult("BOOLEAN"), traces, a.TableOrigin);
            var needle = e.Name == "HAS_CHOICE" ? right.Value.Text! : AggregateTextProjection.VisibleText(right.Value);
            var text = e.Name == "HAS_CHOICE" ? "" : AggregateTextProjection.VisibleText(left.Value);
            var matches = e.Name switch {
                "HAS_CHOICE" => left.Value.Type == "CHOICE_ONE" ? left.Value.Text == needle : left.Value.Choices!.Contains(needle, StringComparer.Ordinal),
                "TEXT_CONTAINS" => text.Contains(needle, StringComparison.OrdinalIgnoreCase),
                "TEXT_STARTS" => text.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
                "TEXT_EQUALS" => string.Equals(text, needle, StringComparison.OrdinalIgnoreCase),
                _ => text.EndsWith(needle, StringComparison.OrdinalIgnoreCase)
            };
            return Single(new("BOOLEAN", "VALUE", Boolean: matches), traces, a.TableOrigin);
        }
        if (e.Name == "IF")
        {
            var condition = Scalar(a);
            var type = Infer(e, inputs.ToDictionary(p => p.Key, p => new AggregateExpressionType(p.Value.Type, p.Value.Shape, p.Value.TableOrigin)));
            if (condition.Value.State != "VALUE") return Single(AggregateValue.NoResult(type.Type), condition.Trace, type.TableOrigin);
            var chosen = Evaluate(e.Arguments[condition.Value.Boolean == true ? 1 : 2], inputs);
            return chosen with { Items = chosen.Items.Select(item => item with { Trace = condition.Trace.Concat(item.Trace).ToArray() }).ToArray(), TableOrigin = type.TableOrigin };
        }
        if (e.Kind == "CALL" && e.Name == "ONLY")
        {
            if (a.Items.Count != 1) throw new AggregatePreviewException("AGG_SINGLE_REPORT_REQUIRED");
            return a with { Shape = "SINGLE" };
        }
        if (e.Kind == "BINARY")
        {
            var b = Evaluate(e.Arguments[1], inputs); var left = Scalar(a); var right = Scalar(b);
            AggregateTextPolicy.CheckValueOperation(e.Name!, a);
            AggregateTextPolicy.CheckValueOperation(e.Name!, b);
            var trace = left.Trace.Concat(right.Trace).ToArray();
            var numeric = e.Name is "+" or "-" or "*" or "/";
            if (left.Value.State != "VALUE" || right.Value.State != "VALUE") return Single(AggregateValue.NoResult(numeric ? "NUMBER" : "BOOLEAN"), trace, a.TableOrigin || b.TableOrigin);
            if (numeric)
            {
                var x = left.Value.Number!; var y = right.Value.Number!;
                var value = e.Name switch { "+" => x.Add(y), "-" => x.Subtract(y), "*" => x.Multiply(y), _ => x.Divide(y) };
                return Single(AggregateValue.Numeric(value), trace, a.TableOrigin || b.TableOrigin);
            }
            var comparison = left.Value.Type switch
            {
                "NUMBER" => left.Value.Number!.Compare(right.Value.Number!),
                "BOOLEAN" => left.Value.Boolean!.Value.CompareTo(right.Value.Boolean!.Value),
                "DATE_PARTIAL" => AggregatePartialDate.Compare(left.Value.Text!, right.Value.Text!),
                "INSTANT" => DateTimeOffset.Parse(left.Value.Text!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                    .CompareTo(DateTimeOffset.Parse(right.Value.Text!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)),
                "TEXT" => string.CompareOrdinal(AggregateTextProjection.VisibleText(left.Value), AggregateTextProjection.VisibleText(right.Value)),
                _ => string.CompareOrdinal(left.Value.Text, right.Value.Text)
            };
            var result = e.Name switch
            {
                "=" => comparison == 0, "<>" => comparison != 0, "<" => comparison < 0, "<=" => comparison <= 0,
                ">" => comparison > 0, ">=" => comparison >= 0,
                "AND" => left.Value.Boolean == true && right.Value.Boolean == true,
                "OR" => left.Value.Boolean == true || right.Value.Boolean == true,
                _ => throw new AggregatePreviewException("AGG_EXPRESSION_TYPE")
            };
            return Single(new("BOOLEAN", "VALUE", Boolean: result), trace, a.TableOrigin || b.TableOrigin);
        }
        if (e.Name == "REPORT_TEXT_TABLE") return AggregateContentTable.Build(a, e.Options!, budget);
        if (e.Name is "SUM" or "AVG" or "MIN" or "MAX" && a.Type == "TABLE")
        {
            var cells = new List<AggregateObservation>();
            foreach (var item in a.Items.Where(i => i.Value.State == "VALUE"))
                foreach (var row in item.Value.Table!.Rows) foreach (var cell in row)
                {
                    budget.Spend();
                    if (cell.Value.Type != "NUMBER") throw new AggregatePreviewException("AGG_TABLE_CELL_TYPE");
                    cells.Add(new(cell.Value, cell.Trace));
                }
            a = new("NUMBER", "SET", cells, true);
        }
        a = a with { Items = a.Items.Where(i => i.Value.State != "NO_RESULT").ToArray() };
        var allTrace = a.Items.SelectMany(i => i.Trace).ToArray();
        var present = a.Items.Where(i => i.Value.State == "VALUE").ToArray();
        budget.Spend(a.Items.Count, allTrace.Length * 64L);
        FunctionTrace.Add(new(e.Name!, e.Options?.Basis, a.Items.Count, present.Length, null,
            allTrace.Select(t => t.ReportId).Distinct(StringComparer.Ordinal).ToArray()));
        if (e.Name == "APPEND_TABLE") return AggregateTableAdapter.Append(a, budget);
        if (e.Name == "COUNT")
        {
            long count = e.Options!.Basis switch
            {
                "VALUES" => present.LongLength,
                "REPORTS" => allTrace.Select(t => t.ReportId).Distinct(StringComparer.Ordinal).LongCount(),
                "UNITS" => allTrace.Select(t => t.UnitId).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).LongCount(),
                "ROWS" => present.Sum(i => (long)i.Value.Table!.Rows.Count),
                "PRESENT_ROWS" => AggregateTableAdapter.CountPresentRows(a, e.Options.ColumnIndex!.Value, budget),
                _ => throw new AggregatePreviewException("AGG_COUNT_BASIS")
            };
            return Single(AggregateValue.Numeric(AggregateNumber.From(count)), allTrace, a.TableOrigin);
        }
        if (e.Name == "COUNT_DISTINCT")
        {
            var keys = a.Items.Select(i => i.Value.State == "BLANK" ? "BLANK" : i.Value.Type + ":" + (i.Value.Type switch
            {
                "NUMBER" => i.Value.Number!.ExactKey,
                "BOOLEAN" => i.Value.Boolean!.Value.ToString(),
                "TEXT" => Normalize(i.Value.Text!, e.Options!),
                _ => i.Value.Text!
            })).Distinct(StringComparer.Ordinal).LongCount();
            return Single(AggregateValue.Numeric(AggregateNumber.From(keys)), allTrace, a.TableOrigin);
        }
        if (e.Name == "CONCAT")
        {
            var options = e.Options!;
            var ordered = present.OrderBy(i => options.Order == "UNIT_THEN_PERIOD" ? First(i).UnitId : First(i).OccurrenceKey, StringComparer.Ordinal)
                .ThenBy(i => options.Order == "UNIT_THEN_PERIOD" ? First(i).OccurrenceKey : First(i).UnitId, StringComparer.Ordinal)
                .ThenBy(i => First(i).ReportId, StringComparer.Ordinal);
            var projected = ordered.Select(i => AggregateTextProjection.VisibleText(i.Value));
            // A string-list report contributes one complete block. Its internal lines
            // and report boundaries belong to the system, never to string dedup.
            var blocks = present.Any(i => i.Value.TextFormat == AggregateTextPolicy.StringListBlock);
            var separator = blocks ? AggregateTextPolicy.BlockSeparator : options.Separator!;
            var parts = projected.Select(text => !blocks && options.Trim == true ? text.Trim() : text).Where(s => s.Length > 0).ToArray();
            var length = parts.Sum(p => (long)p.Length) + Math.Max(0, parts.Length - 1) * (long)separator.Length;
            budget.Spend(bytes: length * 4);
            var result = parts.Length == 0 ? AggregateValue.NoResult("TEXT") : new("TEXT", "VALUE", Text: string.Join(separator, parts)) {
                TextFormat = blocks ? AggregateTextPolicy.StringListBlock : null };
            if (parts.Length > 0 && present.Any(i => i.Value.TextFormat == "RICH_HTML" || i.Value.LengthText != null))
            {
                var plain = ordered.Select(i => options.Trim == true ? AggregateExtendedFunctions.PlainText(i.Value).Trim() : AggregateExtendedFunctions.PlainText(i.Value)).Where(s => s.Length > 0);
                result = result with { LengthText = string.Join(separator, plain) };
                budget.Spend(bytes: result.LengthText.Length * 2L);
            }
            return Single(result, allTrace, a.TableOrigin);
        }
        if (present.Length == 0 && (e.Name != "AVG" || a.Items.Count == 0)) return Single(AggregateValue.NoResult("NUMBER"), allTrace, a.TableOrigin);
        var sum = AggregateNumber.From(0);
        foreach (var item in present) { budget.Spend(); sum = sum.Add(item.Value.Number!); }
        FunctionTrace[^1] = FunctionTrace[^1] with { ExactTotal = sum.ExactKey };
        var answer = e.Name switch
        {
            "SUM" => sum,
            "AVG" => sum.Divide(AggregateNumber.From(a.Items.Count)),
            "MIN" => present.Select(i => i.Value.Number!).Aggregate((x, y) => x.Compare(y) <= 0 ? x : y),
            "MAX" => present.Select(i => i.Value.Number!).Aggregate((x, y) => x.Compare(y) >= 0 ? x : y),
            _ => throw new AggregatePreviewException("AGG_EXPRESSION_TYPE")
        };
        return Single(AggregateValue.Numeric(answer), allTrace, a.TableOrigin);
    }

    internal IReadOnlyDictionary<string, AggregateChannel> Filter(AggregateNodeDto node, IReadOnlyDictionary<string, AggregateChannel> inputs)
    {
        if (node.Predicate?.Kind == "LIST_PIPELINE") return AggregateListFilter.EvaluateAsync(node, inputs, budget).GetAwaiter().GetResult();
        var declared = inputs.ToDictionary(p => p.Key, p => new AggregateExpressionType(p.Value.Type, "SINGLE", p.Value.TableOrigin, p.Value.ListSchema));
        if (Infer(node.Predicate!, declared).Type != "BOOLEAN") throw new AggregatePreviewException("AGG_FILTER_TYPE");
        var allSet = inputs.Values.All(c => c.Shape == "SET");
        if (!allSet && inputs.Values.Any(c => c.Shape == "SET")) throw new AggregatePreviewException("AGG_FILTER_ALIGNMENT");
        var output = node.Outputs.ToDictionary(p => p.Id, _ => new List<AggregateObservation>());
        if (!allSet)
        {
            if (Scalar(Evaluate(node.Predicate!, inputs)).Value.Boolean == true)
                for (var i = 0; i < node.Inputs.Count; i++) output[node.Outputs[i].Id].AddRange(inputs[node.Inputs[i].Id].Items);
        }
        else
        {
            var indexed = inputs.ToDictionary(p => p.Key, p => p.Value.Items.ToDictionary(i => SingleReport(i), StringComparer.Ordinal));
            var keys = indexed.Values.SelectMany(i => i.Keys).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var key in keys)
            {
                budget.Spend();
                if (indexed.Values.Any(i => !i.ContainsKey(key))) throw new AggregatePreviewException("AGG_FILTER_ALIGNMENT");
                var row = inputs.ToDictionary(p => p.Key, p => new AggregateChannel(p.Value.Type, "SINGLE", [indexed[p.Key][key]], p.Value.TableOrigin));
                if (Scalar(Evaluate(node.Predicate!, row)).Value.Boolean != true) continue;
                for (var i = 0; i < node.Inputs.Count; i++) output[node.Outputs[i].Id].Add(indexed[node.Inputs[i].Id][key]);
            }
        }
        return node.Outputs.ToDictionary(p => p.Id, p => new AggregateChannel(p.ValueType, p.Shape, output[p.Id],
            inputs[node.Inputs[node.Outputs.IndexOf(p)].Id].TableOrigin, inputs[node.Inputs[node.Outputs.IndexOf(p)].Id].ListSchema)
            { EligibleSources = inputs[node.Inputs[node.Outputs.IndexOf(p)].Id].EligibleSources });
    }
    internal Task<IReadOnlyDictionary<string, AggregateChannel>> FilterAsync(AggregateNodeDto node, IReadOnlyDictionary<string, AggregateChannel> inputs,
        IAggregateListSink? store, AggregateReadContext context, CancellationToken ct)
        => node.Predicate?.Kind == "LIST_PIPELINE" ? AggregateListFilter.EvaluateAsync(node, inputs, budget, store, context, ct) : Task.FromResult(Filter(node, inputs));
    internal static AggregateChannel Single(AggregateValue value, IReadOnlyList<AggregateTrace> trace, bool tableOrigin = false)
        => new(value.Type, "SINGLE", [new(value, trace)], tableOrigin);
    internal static AggregateObservation Scalar(AggregateChannel channel)
        => channel.Shape != "SINGLE" || channel.Items.Count > 1 ? throw new AggregatePreviewException("AGG_EXPRESSION_SHAPE")
            : channel.Items.Count == 0 ? new(AggregateValue.NoResult(channel.Type), []) : channel.Items[0];
    private static AggregateTrace First(AggregateObservation observation) => observation.Trace.FirstOrDefault() ?? new("", "", "", "");
    private static string SingleReport(AggregateObservation observation)
    {
        var ids = observation.Trace.Select(t => t.ReportId).Distinct(StringComparer.Ordinal).ToArray();
        return ids.Length == 1 ? ids[0] : throw new AggregatePreviewException("AGG_FILTER_ALIGNMENT");
    }
    private static string Normalize(string text, AggregateFunctionOptionsDto options)
    {
        var result = options.Trim == true ? text.Trim() : text;
        return options.CaseSensitive == false ? result.ToUpperInvariant() : result;
    }
}
