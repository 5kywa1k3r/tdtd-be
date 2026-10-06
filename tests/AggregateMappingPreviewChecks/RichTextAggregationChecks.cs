using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class RichTextAggregationChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> error)
    {
        AggregateChannel Text(string text, bool rich = true) => AggregateEvaluator.Single(
            new AggregateValue("TEXT", "VALUE", Text: text) { TextFormat = rich ? "RICH_HTML" : null }, [new("r", "u", "ONCE", "f")]);
        AggregateExpressionDto Input() => new() { Kind = "INPUT", Ref = "in" };
        AggregateValue Eval(AggregateExpressionDto expression, AggregateChannel source) => AggregateEvaluator.Scalar(
            new AggregateEvaluator(new(default), []).Evaluate(expression, new Dictionary<string, AggregateChannel> { ["in"] = source })).Value;
        AggregateValue Call(string name, AggregateChannel source, AggregateFunctionOptionsDto? options = null) =>
            Eval(new() { Kind = "CALL", Name = name, Arguments = [Input()], Options = options }, source);
        const string html = "<p><strong>đã</strong> xử lý</p>";
        var source = Text(html);
        foreach (var name in new[] { "TEXT_CONTAINS", "TEXT_STARTS", "TEXT_ENDS", "TEXT_EQUALS" })
        {
            var predicate = new AggregateExpressionDto { Kind = "CALL", Name = name,
                Arguments = [Input(), new() { Kind = "TEXT", Value = "ĐÃ XỬ LÝ" }] };
            check(Eval(predicate, source).Boolean == true, name + " matches visible formatted Vietnamese");
            check(Eval(predicate, Text("đã xử lý", false)).Boolean == true, name + " plain text unchanged");
        }
        var containsTag = new AggregateExpressionDto { Kind = "CALL", Name = "TEXT_CONTAINS",
            Arguments = [Input(), new() { Kind = "TEXT", Value = "strong" }] };
        check(Eval(containsTag, source).Boolean == false, "rich filter ignores invisible markup");
        check(Eval(containsTag, Text("<strong>đã</strong>", false)).Boolean == true, "plain tags remain literal in filters");
        check(Eval(new() { Kind = "BINARY", Name = "=", Arguments = [Input(), new() { Kind = "TEXT", Value = "đã xử lý" }] }, source).Boolean == true, "text equality uses visible rich text");
        string Visible(string value) => AggregateTextProjection.VisibleText(Text(value).Items[0].Value);
        check(Visible("<p>Một</p><p>Hai</p>") == "Một\nHai", "paragraph boundaries preserved");
        check(Visible("<p>Một<br /><br />Hai<br /></p>") == "Một\n\nHai\n", "explicit and trailing line breaks preserved");
        check(Visible("<p>Một</p><p><br /></p><p>Hai</p>") == "Một\n\nHai", "empty paragraph preserved");
        check(Visible("<ul><li>Một</li><li><em>Hai</em></li></ul>") == "Một\nHai", "list item boundaries preserved");
        check(Visible("<table><tbody><tr><th>Họ tên</th><th>Đơn vị</th></tr><tr><td><p>A</p></td><td><p>PA02</p></td></tr></tbody></table>") == "Họ tên\tĐơn vị\nA\tPA02", "rich table cell and row boundaries preserved");
        check(Visible("<p>A &amp; B &lt; C&#160;D</p>") == "A & B < C\u00a0D", "rich entities decoded once");
        check(AggregateTextProjection.VisibleText(Text("A &amp; <b>B</b>", false).Items[0].Value) == "A &amp; <b>B</b>", "plain content is not parsed or decoded");
        check(Visible("<p>" + new string('a', 20000) + "</p>").Length == 20000, "long rich text is not truncated");
        error(() => Visible("<p>bad"), "AGG_RICH_TEXT_INVALID");
        error(() => Visible("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///secret'>]><p>&x;</p>"), "AGG_RICH_TEXT_INVALID");
        var richObservation = source.Items[0];
        var set = source with { Shape = "SET", Items = [richObservation, richObservation] };
        var joined = Call("CONCAT", set, new() { Order = "UNIT_THEN_PERIOD", Separator = "\n", Trim = false });
        check(joined.Text == "đã xử lý\nđã xử lý", "CONCAT takes visible words and retains duplicates");
        check(Call("LEN", AggregateEvaluator.Single(joined, [])).Number!.ToWire() == "17", "LEN after CONCAT keeps existing contract");
        check(Call("LEN", Text("<p>Một</p><p>Hai</p>")).Number!.ToWire() == "6", "direct rich LEN paragraph semantics unchanged");
        var mixed = set with { Items = [richObservation, Text("<b>literal</b>", false).Items[0]] };
        check(Call("CONCAT", mixed, new() { Order = "UNIT_THEN_PERIOD", Separator = " | ", Trim = false }).Text == "đã xử lý | <b>literal</b>", "mixed CONCAT preserves plain literal tags");
        var withNotes = set with { Items = [richObservation with {
            SourceNote = new("r", "s", "r", "u", "PA02", "Thử", "ONCE", "f", null, null)
        }, new(AggregateValue.Blank("TEXT"), [new("empty", "v", "ONCE", "f")]) {
            SourceNote = new("empty", "e", "empty", "v", "PV01", "Trống", "ONCE", "f", null, null)
        }] };
        var table = Call("REPORT_TEXT_TABLE", withNotes, new() { Order = "UNIT_THEN_PERIOD" }).Table!;
        check(table.Rows[0][1].Value.Text == "đã xử lý" && table.Rows[0][1].Value.TextFormat == null, "content table contains plain visible words");
        check(table.Rows.Count == 2 && table.Rows[1][1].Value.State == "BLANK", "blank report retains its table row");
        check(table.Rows[0][1].Trace.SequenceEqual(richObservation.Trace) && table.ContentRows![0].ReportId == "r", "projection preserves lineage and source note");
        check(richObservation.Value.Text == html && richObservation.Value.TextFormat == "RICH_HTML", "source remains unchanged");
        check(AggregatePreviewService.ToWire(richObservation.Value)!.Value.GetString() == "đã xử lý", "scalar preview wire is visible text");
        check(AggregatePreviewService.ToWire(new("TABLE", "VALUE", Table: table))!.Value.GetProperty("rows")[0].GetProperty("cells")[1].GetProperty("value").GetString() == "đã xử lý", "table preview wire is visible text");
        check(AggregateTextProjection.RichDestination("A < B & C\nD") == "<p>A &lt; B &amp; C<br />D</p>", "rich destination escapes literal characters and keeps line breaks");
        check(AggregateTextProjection.RichDestination("<b>literal</b>") == "<p>&lt;b&gt;literal&lt;/b&gt;</p>", "plain tags do not become rich destination markup");
    }
}
