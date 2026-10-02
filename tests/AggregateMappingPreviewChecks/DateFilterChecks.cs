using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class DateFilterChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> error)
    {
        var schema = new AggregateListSchema([new("date", "DATE_PARTIAL")]);
        AggregateListRow Row(string id, string? date)
        {
            var origin = new AggregateListOrigin(new("work", "assignment", "binding", null, null, "report", 1, 1, 1,
                "payload", "schema", "Approved", true, "rel", "auth"), "unit", "period", "list", id, "source:out");
            var trace = new AggregateTrace("report", "unit", "period", "list", RecordId: id, FieldId: "date");
            return new(id, origin, new Dictionary<string, AggregateCell> {
                ["date"] = new(date == null ? AggregateValue.Blank("DATE_PARTIAL") : new("DATE_PARTIAL", "VALUE", Text: date), [trace]) });
        }
        AggregateChannel Channel(params AggregateListRow[] rows) => new("LIST", "SET",
            [new(new("LIST", "VALUE") { List = new(schema, rows) }, [])], ListSchema: schema);
        var plan = new AggregateListPipelineDto { Version = 1, Sort = [], Scope = "ALL_SOURCES", Take = "ALL",
            Project = [new("date", "date")], Operation = "COUNT_RECORDS" };
        AggregateValue Run(AggregateListPredicateDto predicate, AggregateChannel channel) => AggregateEvaluator.Scalar(
            AggregateListPipeline.Evaluate(plan with { Where = predicate }, channel, new(default), [])).Value;
        AggregateListPredicateDto Predicate(string op, params string[] values) => new() { Operator = op, FieldId = "date", Values = values.ToList() };
        var days = Channel(Row("a", "15/09/2026"), Row("b", "01/10/2026"), Row("blank", null));
        foreach (var (op, expected) in new[] { ("EQ", "1"), ("NE", "1"), ("LT", "0"), ("LTE", "1"), ("GT", "1"), ("GTE", "2") })
            check(Run(Predicate(op, "09/2026"), days).Number!.ToWire() == expected, "List date " + op + " uses filter month precision");
        check(Run(Predicate("EQ", "2026"), days).Number!.ToWire() == "2", "List year filter preserves separate record identities");
        foreach (var bounds in new[] { new[] { "09/2026", "09/2026" }, new[] { "", "09/2026" } })
            check(Run(Predicate("BETWEEN", bounds), days).Number!.ToWire() == "1", "List inclusive month range " + string.Join(" / ", bounds));
        check(Run(Predicate("BETWEEN", "10/2026", ""), days).Number!.ToWire() == "1", "List missing upper bound is unbounded");
        check(Run(Predicate("BETWEEN", "", ""), days).Number!.ToWire() == "3", "List absent bounds impose no condition, including a blank field");
        error(() => Run(Predicate("EQ", "09/2026"), Channel(Row("year", "2026"))), "AGG_DATE_PRECISION_UNRESOLVED");
        error(() => Run(Predicate("EQ", "15/09/2026"), Channel(Row("month", "09/2026"))), "AGG_DATE_PRECISION_UNRESOLVED");
        error(() => Run(Predicate("BETWEEN", "10/2026", "09/2026"), days), "AGG_LIST_PREDICATE_OPERAND");
        error(() => Run(Predicate("BETWEEN", "2026", "09/2026"), days), "AGG_DATE_PRECISION_UNRESOLVED");
        error(() => Run(Predicate("BETWEEN", "", "31/02/2026"), days), "AGG_DATE_INVALID");

        var evaluator = new AggregateEvaluator(new(default), []);
        AggregateExpressionDto Date(string value) => new() { Kind = "DATE_PARTIAL", Value = value };
        AggregateExpressionDto Compare(string op, string value) => new() { Kind = "BINARY", Name = op,
            Arguments = [new() { Kind = "INPUT", Ref = "in" }, Date(value)] };
        var node = new AggregateNodeDto { Id = "filter", Kind = "FILTER", Inputs = [new("in", "DATE_PARTIAL", "SET")],
            Outputs = [new("out", "DATE_PARTIAL", "SET")], Predicate = Compare("<=", "09/2026") };
        AggregateObservation Observation(string report, string value) => new(new("DATE_PARTIAL", "VALUE", Text: value),
            [new(report, "unit", "period", "field")]);
        var channel = new AggregateChannel("DATE_PARTIAL", "SET", [Observation("r1", "15/09/2026"), Observation("r2", "01/10/2026")]);
        var result = evaluator.Filter(node, new Dictionary<string, AggregateChannel> { ["in"] = channel });
        check(result["out"].Items.Count == 1 && result["out"].Items[0].Trace.Single().ReportId == "r1",
            "scalar Filter compares by threshold precision without changing the original value or lineage");
        var yes = new AggregateExpressionDto { Kind = "BOOLEAN", Value = "true" };
        var open = node with { Predicate = new() { Kind = "BINARY", Name = "AND", Arguments = [Compare(">=", "10/2026"), yes] } };
        check(evaluator.Filter(open, new Dictionary<string, AggregateChannel> { ["in"] = channel })["out"].Items.Single().Trace.Single().ReportId == "r2",
            "scalar open range uses the same engine Filter as closed ranges");
    }
}
