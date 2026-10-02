using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class ContentTableChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> error)
    {
        AggregateObservation Item(string report, string unit, string period, string? text) =>
            new(text == null ? AggregateValue.Blank("TEXT") : new("TEXT", "VALUE", Text: text), [new(report, unit, period, "body")]) {
                SourceNote = new("", unit + ":" + period + ":body", report, unit, "Đơn vị " + unit, "Báo cáo " + report, period, "body", null, "2026-09-28T00:00:00Z") };
        var first = Item("r2", "B", "02", "  Nội dung 📝\n\nGiữ nguyên  ");
        var blank = Item("r1", "A", "01", null);
        var items = new[] { first, blank, Item("r3", "B", "01", "") };
        var channel = new AggregateChannel("TEXT", "SET", items);
        AggregateTable Build(AggregateChannel c, string order = "UNIT_THEN_PERIOD") =>
            AggregateEvaluator.Scalar(AggregateContentTable.Build(c, new() { Order = order }, new(default))).Value.Table!;
        var table = Build(channel);
        check(table.Rows.Count == 3 && table.Schema.Layout == "vertical" && table.Schema.Columns.Count == 2, "content table is a real two-column list");
        check(table.Rows[0][1].Value.State == "BLANK" && table.Rows[1][1].Value.Text == "", "approved blank and empty text retain their rows without fabricated text");
        check(table.Rows[2][1].Value.Text == first.Value.Text, "content table preserves Unicode paragraphs and spaces");
        check(table.Rows[2][0].Value.Text == "Đơn vị B" && table.ContentRows![2].ReportId == "r2", "unit display and report note retain provenance");
        var changed = first with { Value = new("TEXT", "VALUE", Text: "Nội dung đã sửa"), SourceNote = first.SourceNote! with { ReportId = "r2-new", ReportTitle = "Tên mới" } };
        check(Build(channel with { Items = [changed, blank, items[2]] }).ContentRows![2].RowKey == table.ContentRows![2].RowKey, "row identity survives value and report revision changes");
        check(Build(channel with { Items = items.Reverse().ToArray() }).ContentRows!.Select(n => n.RowKey).SequenceEqual(table.ContentRows!.Select(n => n.RowKey)), "enumeration order cannot alter stable row identity");
        var twice = Build(channel with { Items = [first, first] });
        check(twice.Rows.Count == 2 && twice.ContentRows!.Select(n => n.RowKey).Distinct().Count() == 2, "repeated contributions are not deduplicated");
        check(Build(channel with { Items = [] }).Rows.Count == 0, "no eligible source gives an empty table not zero");
        var expr = new AggregateExpressionDto { Kind = "CALL", Name = "REPORT_TEXT_TABLE", Arguments = [new() { Kind = "INPUT", Ref = "in" }], Options = new() { Order = "PERIOD_THEN_UNIT" } };
        var engine = new AggregateEvaluator(new(default), []);
        check(engine.Infer(expr, new Dictionary<string, AggregateExpressionType> { ["in"] = new("TEXT", "SET") }) == new AggregateExpressionType("TABLE", "SINGLE", true), "content function infers TABLE");
        var evaluated = AggregateEvaluator.Scalar(engine.Evaluate(expr, new Dictionary<string, AggregateChannel> { ["in"] = channel }));
        check(evaluated.Value.Table!.Rows.Count == 3 && evaluated.Trace.Count == 3, "same evaluator produces rows and complete trace");
        error(() => Build(channel, ""), "AGG_CONTENT_TABLE_ORDER");
        error(() => Build(channel with { Items = [first with { SourceNote = null }] }), "AGG_CONTENT_TABLE_SOURCE_NOTE_REQUIRED");
        error(() => engine.Infer(expr, new Dictionary<string, AggregateExpressionType> { ["in"] = new("NUMBER", "SET") }), "AGG_CONTENT_TABLE_SOURCE");
        var longText = string.Concat(Enumerable.Repeat("Tiếng Việt 📝\n", 5_000));
        var large = Build(channel with { Items = Enumerable.Range(0, 166).Select(i => Item("r" + i, i.ToString("D3"), "01", longText + i)).ToArray() });
        check(large.Rows.Count == 166 && large.Rows.Select((r, i) => r[1].Value.Text == longText + i).All(x => x), "166 long texts stay intact as separate rows");
    }
}
