using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class ListPipelineChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> error)
    {
        var schema = new AggregateListSchema([new("score", "NUMBER"), new("name", "TEXT"), new("unit", "CHOICE_ONE"),
            new("tags", "CHOICE_MANY"), new("day", "DATE_ONLY"), new("active", "BOOLEAN")]);
        AggregateListRow Row(string report, string id, int? score, string unit = "A", string name = "Nguyễn An")
        {
            var origin = new AggregateListOrigin(new("work", "assignment", "binding", null, null, report, 1, 1, 1, "payload", "schema", "Approved", true, "rel", "auth"), "report-unit", "month", "list", id, "source:out");
            AggregateCell Cell(string field, AggregateValue value) => new(value, [new(report, "report-unit", "month", "list", RecordId: id, FieldId: field, SourceSlot: "source:out")]);
            return new(report + ":" + id, origin, new Dictionary<string, AggregateCell> {
                ["score"] = Cell("score", score.HasValue ? AggregateValue.Numeric(AggregateNumber.From(score.Value)) : AggregateValue.Blank("NUMBER")),
                ["name"] = Cell("name", new("TEXT", "VALUE", Text: name)), ["unit"] = Cell("unit", new("CHOICE_ONE", "VALUE", Text: unit)),
                ["tags"] = Cell("tags", new("CHOICE_MANY", "VALUE", Choices: ["X", "Y"])),
                ["day"] = Cell("day", new("DATE_ONLY", "VALUE", Text: "2026-09-29")), ["active"] = Cell("active", new("BOOLEAN", "VALUE", Boolean: false)) });
        }
        AggregateChannel Channel(params AggregateListRow[] rows) => new("LIST", "SET", [new(new("LIST", "VALUE") { List = new(schema, rows) }, [])], ListSchema: schema);
        var basePlan = new AggregateListPipelineDto { Version = 1, Sort = [new("score", "DESC", "LAST")], Scope = "ALL_SOURCES",
            Take = "TOP_N", TopN = 3, Project = [new("score", "score"), new("name", "title")], Operation = "COLLECT" };
        var traces = new List<AggregateListPipelineTrace>();
        AggregateValue Run(AggregateListPipelineDto plan, AggregateChannel input)
        { traces.Clear(); return AggregateEvaluator.Scalar(AggregateListPipeline.Evaluate(plan, input, new(default), traces)).Value; }
        var data = Channel(Row("an", "1", 12), Row("an", "2", 9), Row("an", "3", 6), Row("an", "4", 3),
            Row("binh", "1", 11), Row("binh", "2", 10), Row("binh", "3", 8), Row("binh", "4", 2));
        var global = Run(basePlan, data).List!;
        check(global.Records.Count == 3 && global.Records.Select(r => r.Cells["score"].Value.Number!.ToWire()).SequenceEqual(new[] { "12", "11", "10" }), "List global top3 and projection order");
        check(traces.Single().Omitted == 5 && traces.Single().Matched == 8, "List warns whenever matching items exceed N");
        check(Run(basePlan with { Scope = "PER_REPORT" }, data).List!.Records.Count == 6 && traces.Single().Omitted == 2, "List per-report top3 keeps six");
        check(Run(basePlan with { Operation = "SUM", ValueFieldId = "score" }, data).Number!.ToWire() == "33", "List global top3 sum33");
        check(Run(basePlan with { Scope = "PER_REPORT", Operation = "SUM", ValueFieldId = "score" }, data).Number!.ToWire() == "56", "List per-report sum56");
        var all = basePlan with { Take = "ALL", TopN = null, Sort = [], Operation = "COUNT_RECORDS" };
        AggregateListPredicateDto Predicate(string op, string field, params string[] values) => new() { Operator = op, FieldId = field, Values = values.ToList() };
        var children = new List<AggregateListPredicateDto> { Predicate("IN", "unit", "A"), Predicate("GT", "score", "0") };
        var crossed = Channel(Row("r", "1", 0, "A"), Row("r", "2", 5, "B"));
        check(Run(all with { Where = new() { Operator = "AND", Children = children } }, crossed).Number!.ToWire() == "0", "List AND never combines fields from different records");
        check(Run(all with { Where = new() { Operator = "OR", Children = children } }, crossed).Number!.ToWire() == "2", "List OR same-record predicates");
        foreach (var (op, values, expected) in new[] { ("HAS_ANY", new[] { "Y", "Z" }, "1"), ("HAS_ALL", new[] { "X", "Z" }, "0"),
            ("HAS_NONE", new[] { "Z" }, "1"), ("EQUALS_SET", new[] { "Y", "X" }, "1") })
            check(Run(all with { Where = Predicate(op, "tags", values) }, Channel(Row("r", "1", 1))).Number!.ToWire() == expected, "List choice many " + op);
        check(Run(all with { Where = Predicate("BETWEEN", "day", "2026-09-01", "2026-09-30") }, Channel(Row("r", "1", 1))).Number!.ToWire() == "1", "List date inclusive range");
        check(Run(all with { Where = new() { Operator = "IS_FALSE", FieldId = "active" } }, Channel(Row("r", "1", 1))).Number!.ToWire() == "1", "List false is present value");
        check(Run(all with { Where = Predicate("CONTAINS", "name", "nguyễn") with { Trim = false, CaseSensitive = false } }, Channel(Row("r", "1", 1))).Number!.ToWire() == "1", "List explicit text normalization");
        var emptyChoice = Row("r", "empty", 1);
        var emptyCells = emptyChoice.Cells.ToDictionary(c => c.Key, c => c.Value);
        emptyCells["tags"] = emptyCells["tags"] with { Value = new("CHOICE_MANY", "VALUE", Choices: []) };
        emptyCells["unit"] = emptyCells["unit"] with { Value = AggregateValue.Blank("CHOICE_ONE") };
        var emptyInput = Channel(emptyChoice with { Cells = emptyCells });
        foreach (var (field, op, code) in new[] { ("tags", "HAS_NONE", "X"), ("unit", "NOT_IN", "A") })
        {
            var blankPredicate = new AggregateListPredicateDto { Operator = "IS_BLANK", FieldId = field };
            check(Run(all with { Where = blankPredicate }, emptyInput).Number!.ToWire() == "1", "List blank choice matches explicit special option " + field);
            check(Run(all with { Where = blankPredicate with { Operator = "IS_PRESENT" } }, emptyInput).Number!.ToWire() == "0", "List blank choice is not present " + field);
            check(Run(all with { Where = Predicate(op, field, code) }, emptyInput).Number!.ToWire() == "0", "List negative predicate does not implicitly include blank " + field);
            check(Run(all with { Where = new() { Operator = "OR", Children = [Predicate(op, field, code), blankPredicate] } }, emptyInput).Number!.ToWire() == "1", "List explicit OR includes blank " + field);
            var countChoice = all with { Operation = "COUNT_VALUES", Project = [new(field, field)], ValueFieldId = field };
            check(Run(countChoice, emptyInput).Number!.ToWire() == "0" && traces.Single().BlankCount == 1, "List COUNT_VALUES excludes blank choice " + field);
            check(Run(countChoice with { Operation = "ONLY_ITEM" }, emptyInput).State == "BLANK", "List ONLY_ITEM returns blank choice " + field);
            var special = AggregateListChoicePolicy.SpecialOptions(field, field == "tags" ? "CHOICE_MANY" : "CHOICE_ONE", false);
            check(special.Count == 1 && special[0].Label == "Để trống" && special[0].Predicate == blankPredicate
                && AggregateListChoicePolicy.SpecialOptions(field, field == "tags" ? "CHOICE_MANY" : "CHOICE_ONE", true).Count == 0,
                "List blank option is built in for optional choice only " + field);
        }
        check(Run(all, emptyInput).Number!.ToWire() == "1", "List blank choice does not discard its record");
        var blankCollected = Run(all with { Operation = "COLLECT", Project = [new("tags", "tags")] }, emptyInput).List!.Records.Single();
        check(blankCollected.Cells["tags"].Value.State == "BLANK" && blankCollected.Origin == emptyChoice.Origin
            && blankCollected.Cells["tags"].Trace == emptyCells["tags"].Trace && emptyCells["tags"].Value.State == "VALUE"
            && emptyCells["tags"].Value.Choices!.Count == 0, "List blank normalization preserves source value identity and lineage");
        var numeric = all with { Operation = "AVG", ValueFieldId = "score" };
        check(Run(numeric, Channel(Row("r", "1", 10), Row("r", "2", null), Row("r", "3", 0))).Number!.ToWire() == "3.333333" && traces.Single().BlankCount == 1, "List AVG retains true blank denominator");
        check(Run(numeric, Channel(Row("r", "1", null), Row("r", "2", null))).Number!.ToWire() == "0", "List all-blank AVG zero");
        foreach (var op in new[] { "SUM", "MIN", "MAX" }) check(Run(numeric with { Operation = op }, Channel(Row("r", "1", null))).State == "NO_RESULT", "List all-blank " + op);
        check(Run(numeric, Channel()).State == "NO_RESULT", "List empty AVG no result");
        check(Run(all, Channel()).Number!.ToWire() == "0", "List authorized empty count zero");
        check(Run(all with { Operation = "COUNT_VALUES", ValueFieldId = "score" }, Channel(Row("r", "1", null), Row("r", "2", 0))).Number!.ToWire() == "1", "List count values preserves zero");
        check(Run(numeric with { Operation = "ONLY_ITEM" }, Channel()).State == "NO_RESULT", "List only empty no result");
        check(Run(numeric with { Operation = "ONLY_ITEM" }, Channel(Row("r", "1", null))).State == "BLANK", "List only blank item");
        error(() => Run(numeric with { Operation = "ONLY_ITEM" }, crossed), "LIST_SINGLE_ITEM_REQUIRED");
        var tied = Channel(Row("r", "d", 10), Row("r", "b", 10), Row("r", "a", 12), Row("r", "c", 10), Row("r", "e", null));
        check(Run(basePlan, tied).List!.Records.Select(r => r.Origin.RecordId).SequenceEqual(new[] { "a", "b", "c" }), "List stable ID tie-break exact N");
        check(Run(basePlan with { Take = "ALL", TopN = null, Sort = [new("score", "ASC", "LAST")] }, tied).List!.Records.Last().Origin.RecordId == "e", "List null last for ascending");
        var oldKey = AggregateListWire.OutputId("i", "target", global.Records[0].Origin);
        check(Guid.TryParse(oldKey, out _) && oldKey == AggregateListWire.OutputId("i", "target", global.Records[0].Origin with { Pin = global.Records[0].Origin.Pin! with { PayloadRevision = 99 } }), "List UUID stable across payload revisions/rank");
        check(oldKey != AggregateListWire.OutputId("i", "target", global.Records[0].Origin with { SourceSlot = "another" }), "List repeated source slot preserves separate identity");
        var cloned = JsonSerializer.SerializeToElement(AggregateListWire.Encode(global), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var decoded = AggregateListWire.Decode(cloned);
        check(decoded.Records[0].Cells["score"].Value.Number!.ExactKey == "12/1" && decoded.Records[0].Cells["title"].Trace[0].FieldId == "name", "List snapshot preserves exact values and original field lineage");
        error(() => Run(basePlan, Channel(Row("r", "same", 1), Row("r", "same", 2))), "AGG_LIST_ID_COLLISION");
        error(() => Run(basePlan with { Sort = [] }, data), "AGG_LIST_PIPELINE_SCHEMA");
        error(() => Run(basePlan with { TopN = 0 }, data), "AGG_LIST_PIPELINE_SCHEMA");
        error(() => Run(all with { Where = Predicate("HAS_ALL", "tags") }, data), "AGG_LIST_PREDICATE_OPERAND");
        error(() => Run(all with { Where = Predicate("GT", "name", "10") with { Trim = false, CaseSensitive = true } }, data), "AGG_LIST_PREDICATE_TYPE");
        error(() => Run(all with { Where = Predicate("EQ", "name", "An") }, data), "AGG_LIST_TEXT_OPTIONS");
        error(() => Run(all with { ValueFieldId = "title", Operation = "SUM" }, data), "AGG_LIST_NUMERIC_FIELD_REQUIRED");
        error(() => Run(all, new("TABLE", "SET", [])), "AGG_LIST_SOURCE_TYPE");
        error(() => Run(all with { Project = [new("absent", "out")] }, data), "AGG_LIST_FIELD_REF");
        error(() => AggregateListPipeline.Evaluate(basePlan, data, new(default, maxOperations: 1), []), "AGG_BUDGET_EXCEEDED");
        error(() => new AggregateEvaluator(new(default), []).Infer(new() { Kind = "CALL", Name = "ONLY", Arguments = [new() { Kind = "INPUT", Ref = "x" }] },
            new Dictionary<string, AggregateExpressionType> { ["x"] = new("LIST", "SET", ListSchema: schema) }), "AGG_LIST_OPERATOR_REQUIRED");
        var scan = new GeneratedRows(reference => Enumerable.Range(0, 200).Select(i => Row(reference.Id,
            i.ToString("D4"), int.Parse(reference.Id) * 200 + i, name: new string('x', 4000))));
        var staged = new AggregateChannel("LIST", "SET", Enumerable.Range(0, 166).Select(i => new AggregateObservation(
            new("LIST", "VALUE") { ListReference = new(AggregateListWire.Kind, i.ToString(), "fixture", 200, schema) }, [])).ToArray(), ListSchema: schema);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var evaluator = new AggregateEvaluator(new(default), []);
        var expression = new AggregateExpressionDto { Kind = "LIST_PIPELINE", Ref = "x", ListPipeline = basePlan };
        var streamed = evaluator.EvaluateWithListsAsync(expression, new Dictionary<string, AggregateChannel> { ["x"] = staged }, scan, null!, default).GetAwaiter().GetResult();
        check(AggregateEvaluator.Scalar(streamed).Value.List!.Records.Select(r => r.Cells["score"].Value.Number!.ToWire())
            .SequenceEqual(new[] { "33199", "33198", "33197" }) && scan.Read == 33200, "List 166x200 bounded scan sees every item before global Top3");
        check(evaluator.ListTrace.Single().Omitted == 33197 && evaluator.ListSelections.Single().Records.Count == 3, "List scan retains selected rows rather than all long text");
        Console.WriteLine($"L3_PROBE sources=166 items=33200 textPerItem=4000 selected=3 elapsedMs={timer.ElapsedMilliseconds} engine-only; no DB/API SLA");
        var lazy = new AggregateExpressionDto { Kind = "CALL", Name = "IF", Arguments = [new() { Kind = "BOOLEAN", Value = "true" },
            new() { Kind = "NUMBER", Value = "7" }, new() { Kind = "LIST_PIPELINE", Ref = "x", ListPipeline = numeric with { Operation = "ONLY_ITEM" } }] };
        check(AggregateEvaluator.Scalar(new AggregateEvaluator(new(default), []).EvaluateWithListsAsync(lazy,
            new Dictionary<string, AggregateChannel> { ["x"] = crossed }, scan, null!, default).GetAwaiter().GetResult()).Value.Number!.ToWire() == "7",
            "List async preparation preserves lazy IF branch errors");
    }
    private sealed class GeneratedRows(Func<AggregateListReference, IEnumerable<AggregateListRow>> generate) : IAggregateListSink
    {
        internal int Read;
        public Task<AggregateListReference> CaptureAsync(AggregateReadContext context, AggregateListValue value, CancellationToken ct) => throw new NotSupportedException();
        public async IAsyncEnumerable<AggregateListRow> ReadRowsAsync(AggregateReadContext context, AggregateListReference reference,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var row in generate(reference)) { ct.ThrowIfCancellationRequested(); Read++; yield return row; }
        }
    }
}
