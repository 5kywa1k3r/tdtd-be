using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception("FAILED: " + label); checks++; }
void Error(Action action, string code)
{
    try { action(); throw new Exception("Expected " + code); }
    catch (AggregatePreviewException ex) { Check(ex.Code == code, code + " actual " + ex.Code); }
}
async Task PreviewError(Fixture fixture, AggregatePreviewRequestDto request, string code)
{
    try { await new AggregatePreviewService(fixture).PreviewAsync(request, "B", default); throw new Exception("Expected " + code); }
    catch (AggregatePreviewException ex) { Check(ex.Code == code, code + " actual " + ex.Code); }
}
AggregateExpressionDto Input(string id = "in") => new() { Kind = "INPUT", Ref = id };
AggregateExpressionDto Call(string name, AggregateFunctionOptionsDto? options = null) => new() { Kind = "CALL", Name = name, Arguments = [Input()], Options = options };
AggregateExpressionDto Literal(string value) => new() { Kind = "NUMBER", Value = value };
AggregateExpressionDto Binary(string name, AggregateExpressionDto left, AggregateExpressionDto right) => new() { Kind = "BINARY", Name = name, Arguments = [left, right] };
foreach (var (input, expected) in new[] { ("1.2345665", "1.234566"), ("1.2345675", "1.234568"), ("-1.2345665", "-1.234566"), ("-0.0000005", "0"), ("999.9999999", "1000"), ("9007199254740993", "9007199254740993") })
    Check(AggregateNumber.Parse(input).ToWire() == expected, "rounding " + input);
Check(AggregateNumber.From(10).Divide(AggregateNumber.From(3)).ToWire() == "3.333333", "six-digit division");
Check(AggregateNumber.From(1).Divide(AggregateNumber.From(3)).Multiply(AggregateNumber.From(3)).ToWire() == "1", "no intermediate rounding");
Check(AggregateNumber.Parse("1.000000000000000000000000000001").Subtract(AggregateNumber.From(1)).Numerator == 1, "exact tiny difference");
Check(AggregateNumber.ParseJson("1.25e-7").ExactKey == "1/8000000", "JSON exponent expanded exactly");
Error(() => AggregateNumber.From(1).Divide(AggregateNumber.From(0)), "AGG_DIVIDE_BY_ZERO");
Check(AggregateTimeResolver.VietnamDay("2026-09-30T17:30:00Z") == new DateOnly(2026, 10, 1), "Vietnam day boundary");
var window = new AggregateResolvedWindowDto("w", "2026-09-01", "2026-09-30", "DECLARED_DATA_WINDOW", "CONTAINED", "Asia/Ho_Chi_Minh", "v1");
Check(!AggregateTimeResolver.Matches(window, new(2026, 9, 28), new(2026, 10, 4)), "weekly cross-month contained");
Check(AggregateTimeResolver.Matches(window with { Match = "OVERLAPS_WHOLE_REPORT" }, new(2026, 9, 28), new(2026, 10, 4)), "weekly whole overlap");
Check(AggregateTimeResolver.Matches(window, new(2026, 9, 30), new(2026, 9, 30)), "inclusive end date");
Error(() => AggregateTimeResolver.Date("2026-02-29"), "AGG_DATE_INVALID");

var fixture = new Fixture();
var request = fixture.Request(Call("SUM"));
var preview = await new AggregatePreviewService(fixture).PreviewAsync(request, "B", default);
Check(preview.Preview.Results.Single().Value!.Value.GetString() == "30", "source SUM 10+20");
Check(preview.Preview.PreviewToken == null && !preview.Preview.Capabilities.ApplyDraft.Allowed, "P02 cannot mint apply capability");
Check(fixture.PayloadReads == 2 && fixture.Rechecks == 1, "source read and final recheck");
Check(preview.Preview.LockImpact.LinkedReportIds.Count == 2, "distinct lock impacts");
fixture = new Fixture(); fixture.Values["r2"] = AggregateValue.Blank("NUMBER");
preview = await new AggregatePreviewService(fixture).PreviewAsync(fixture.Request(Call("AVG")), "B", default);
Check(preview.Preview.Results.Single().Value!.Value.GetString() == "5", "AVG actual blank denominator");
Check(preview.Preview.Results.Single().Denominator == "1", "exact quotient simplified independently of sample trace");
Check(preview.Lineage.Values.Single().Count == 2, "AVG traces blank report");
Check(preview.Functions.Single(f => f.Method == "AVG").InputCount == 2 && preview.Functions.Single(f => f.Method == "AVG").ExactTotal == "10/1", "AVG explanation retains original sample denominator");
fixture = new Fixture(); fixture.AddMissing();
preview = await new AggregatePreviewService(fixture).PreviewAsync(fixture.Request(Call("AVG")), "B", default);
Check(preview.Preview.Results.Single().Value!.Value.GetString() == "15", "missing slot is not AVG blank");
Check(preview.Preview.Completeness == "MISSING_SOURCES" && preview.Preview.LockImpact.MissingSlotKeys.Count == 1, "expected missing slot separate");
Check(preview.Preview.Capabilities.Submit.Allowed, "missing does not remove submit permission");
fixture = new Fixture(); fixture.Headers[1] = fixture.Headers[1] with { Pin = fixture.Headers[1].Pin with { Status = "Submitted" } };
fixture.Headers.Add(fixture.Headers[1] with { Pin = fixture.Headers[1].Pin with { ReportId = "old", Status = "Approved", IsCurrent = false } });
preview = await new AggregatePreviewService(fixture).PreviewAsync(fixture.Request(Call("SUM")), "B", default);
Check(preview.Preview.Results.Single().Value!.Value.GetString() == "10", "no old approved fallback");
Check(preview.Preview.LockImpact.LinkedReportIds.Count == 2 && fixture.PayloadReads == 1, "unapproved linked but not read for numeric contribution");
fixture = new Fixture(); fixture.Headers[0] = fixture.Headers[0] with { WholeReportReadable = false };
await PreviewError(fixture, fixture.Request(Call("SUM")), "AGG_SOURCE_UNAVAILABLE");
Check(fixture.PayloadReads == 0, "ACL precedes payload");
fixture = new Fixture(); fixture.Headers[0] = fixture.Headers[0] with { ParentAssignmentId = "other" };
await PreviewError(fixture, fixture.Request(Call("SUM")), "AGG_SOURCE_UNAVAILABLE");
fixture = new Fixture(); fixture.Headers[0] = fixture.Headers[0] with { Pin = fixture.Headers[0].Pin with { WorkId = "foreign" } };
await PreviewError(fixture, fixture.Request(Call("SUM")), "AGG_SOURCE_UNAVAILABLE");
fixture = new Fixture { Complete = false };
await PreviewError(fixture, fixture.Request(Call("SUM")), "AGG_SOURCE_ENUMERATION_INCOMPLETE");
fixture = new Fixture { Fresh = false };
await PreviewError(fixture, fixture.Request(Call("SUM")), "AGG_INPUT_STALE");
fixture = new Fixture(); fixture.Slots[0] = fixture.Slots[0] with { DataWindow = null };
preview = await new AggregatePreviewService(fixture).PreviewAsync(fixture.Request(Call("SUM")), "B", default);
Check(preview.Preview.State == "UNAVAILABLE" && preview.Preview.Results.All(r => r.Value == null), "unknown coverage is not a complete total");
fixture = new Fixture(); request = fixture.Request(Binary("+", Call("SUM"), Call("SUM")));
preview = await new AggregatePreviewService(fixture).PreviewAsync(request, "B", default);
Check(preview.Preview.Results.Single().Value!.Value.GetString() == "60" && preview.Preview.LinkedSources.Count == 2, "multiplicity A+A with distinct locks");
fixture = new Fixture(); request = fixture.Request(Binary("/", Call("SUM"), new() { Kind = "GET", Name = "CURRENT_UNIT_COUNT" }));
fixture.Units = ["u1", "u2", "u3", "u4"];
preview = await new AggregatePreviewService(fixture).PreviewAsync(request, "B", default);
Check(preview.Preview.Results.Single().Value!.Value.GetString() == "7.5", "GET current roster independent of reports");
fixture = new Fixture(); request = fixture.Request(Call("SUM"));
await PreviewError(fixture, request with { Expected = request.Expected with { PayloadRevision = 99 } }, "AGG_REVISION_CONFLICT");
await PreviewError(fixture, request with { Context = request.Context with { BindingId = "other" } }, "AGG_CONTEXT_STALE");
fixture.Authority = fixture.Authority with { TargetStatus = "Submitted" };
await PreviewError(fixture, fixture.Request(Call("SUM")), "AGG_PREVIEW_FORBIDDEN");

var budget = new AggregateBudget(default);
var evaluator = new AggregateEvaluator(budget, []);
var set = new AggregateChannel("NUMBER", "SET", [new(AggregateValue.Numeric(AggregateNumber.From(10)), [new("r1", "u1", "p", "n")]), new(AggregateValue.Blank("NUMBER"), [new("r2", "u2", "p", "n")]), new(AggregateValue.Blank("NUMBER"), [new("r3", "u3", "p", "n")])]);
var inputs = new Dictionary<string, AggregateChannel> { ["in"] = set };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(Call("COUNT_DISTINCT", new() { Trim = false, CaseSensitive = true }), inputs)).Value.Number!.ToWire() == "2", "distinct blank one bucket");
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(Call("COUNT", new() { Basis = "VALUES" }), inputs)).Value.Number!.ToWire() == "1", "COUNT values not reports");
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(Call("COUNT", new() { Basis = "REPORTS" }), inputs)).Value.Number!.ToWire() == "3", "COUNT report includes actual blank");
inputs["in"] = set with { Items = [] };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(Call("SUM"), inputs)).Value.State == "NO_RESULT", "empty sum not fake zero");
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(Call("COUNT", new() { Basis = "REPORTS" }), inputs)).Value.Number!.ToWire() == "0", "authorized empty count actual zero");
var nestedDistinct = Call("COUNT_DISTINCT", new() { Trim = false, CaseSensitive = true }) with { Arguments = [Call("SUM")] };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(nestedDistinct, inputs)).Value.Number!.ToWire() == "0", "NO_RESULT is not a DISTINCT blank");
var instantCompare = Binary("<", new() { Kind = "INSTANT", Value = "2026-01-01T00:00:00Z" }, new() { Kind = "INSTANT", Value = "2026-01-01T00:00:00.1Z" });
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(instantCompare, inputs)).Value.Boolean == true, "instant comparison uses time not lexical Z order");
inputs["in"] = new("TEXT", "SET", [new(new("TEXT", "VALUE", Text: "Nội dung\nchi tiết"), [new("r1", "u1", "p", "txt")]), new(new("TEXT", "VALUE", Text: "Nội dung\nchi tiết"), [new("r2", "u2", "p", "txt")])]);
var concat = evaluator.Evaluate(Call("CONCAT", new() { Trim = false, Separator = " | ", Order = "UNIT_THEN_PERIOD" }), inputs);
Check(AggregateEvaluator.Scalar(concat).Value.Text == "Nội dung\nchi tiết | Nội dung\nchi tiết", "CONCAT no dedup and preserves paragraph");
var longText = new string('x', 100_000) + "\nend";
inputs["in"] = inputs["in"] with { Items = [new(new("TEXT", "VALUE", Text: longText), [])] };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(Call("CONCAT", new() { Trim = false, Separator = "", Order = "UNIT_THEN_PERIOD" }), inputs)).Value.Text == longText, "long free text untruncated");
Error(() => evaluator.Infer(Call("SUM"), new Dictionary<string, AggregateExpressionType> { ["in"] = new("TEXT", "SET") }), "AGG_EXPRESSION_TYPE");
var lazy = new AggregateExpressionDto { Kind = "CALL", Name = "IF", Arguments = [new() { Kind = "BOOLEAN", Value = "false" }, Binary("/", Literal("1"), Literal("0")), Literal("7")] };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(lazy, inputs)).Value.Number!.ToWire() == "7", "IF only evaluates chosen branch after type check");
Error(() => new AggregateBudget(default, 1).Spend(2), "AGG_BUDGET_EXCEEDED");
void BudgetPath(Action action, string path)
{
    try { action(); throw new Exception("Expected budget limit"); }
    catch (AggregatePreviewException ex) { Check(ex.Code == "AGG_BUDGET_EXCEEDED" && ex.Path == path, "budget diagnostic " + path); }
}
BudgetPath(() => { var b = new AggregateBudget(default, maxOperations: int.MaxValue); b.Spend(int.MaxValue); b.Spend(int.MaxValue); }, "$.budget.operations");
BudgetPath(() => { var b = new AggregateBudget(default, maxBytes: long.MaxValue); b.Spend(bytes: long.MaxValue); b.Spend(bytes: long.MaxValue); }, "$.budget.bytes");
BudgetPath(() => new AggregateBudget(default, duration: TimeSpan.FromTicks(-1)).Spend(), "$.budget.duration");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { new AggregateBudget(cancelled.Token).Spend(); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { checks++; }
}

AggregateObservation Table(string report, string unit, params long?[] numbers)
{
    var schema = new AggregateTableSchema("same-form:table", "list", ["c1"], [], [["NUMBER"]]);
    var rows = numbers.Select((number, r) => (IReadOnlyList<AggregateCell>)new AggregateCell[] {
        new(number.HasValue ? AggregateValue.Numeric(AggregateNumber.From(number.Value)) : AggregateValue.Blank("NUMBER"), [new(report, unit, "p", "table", r + 1, 1)]) }).ToArray();
    return new(new("TABLE", "VALUE", Table: new(schema, rows, Enumerable.Repeat(unit, rows.Length).ToArray())), [new(report, unit, "p", "table")]);
}
var tableSet = new AggregateChannel("TABLE", "SET", [Table("r1", "A", 1, 2, 3), Table("r2", "B", 4, 5)], true);
var append = AggregateEvaluator.Scalar(AggregateTableAdapter.Append(tableSet, budget)).Value.Table!;
Check(append.Rows.Count == 5 && string.Join(",", append.Units) == "A,A,A,B,B", "append A3+B2 with unit column");
Check(append.Rows[4][0].Trace.Single().ReportId == "r2" && append.Rows[4][0].Trace.Single().Row == 2, "append row lineage");
Check(AggregateEvaluator.Scalar(AggregateTableAdapter.Append(tableSet, budget)).Value.Table!.Rows.Count == 5, "repeat append rebuilds instead of appending old result");
var mismatched = tableSet with { Items = [tableSet.Items[0], tableSet.Items[1] with { Value = tableSet.Items[1].Value with { Table = tableSet.Items[1].Value.Table! with { Schema = tableSet.Items[1].Value.Table!.Schema with { Identity = "other-form" } } } }] };
Error(() => AggregateTableAdapter.Append(mismatched, budget), "AGG_TABLE_SCHEMA_INCOMPATIBLE");
var matrixSource = new AggregateChannel("TABLE", "SINGLE", [Table("r1", "A", 10, null, 0)], true);
var tableInputs = new Dictionary<string, AggregateChannel> { ["in"] = matrixSource };
var range = new AggregateExpressionDto { Kind = "TABLE_RANGE", Ref = "in", Area = new("RANGE", 1, 3, 1, 1) };
var sumCells = new AggregateExpressionDto { Kind = "CALL", Name = "SUM", Arguments = [range] };
Check(evaluator.Infer(sumCells, new Dictionary<string, AggregateExpressionType> { ["in"] = new("TABLE", "SINGLE", true) }).TableOrigin, "numeric reducer preserves Table provenance");
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(sumCells, tableInputs)).Value.Number!.ToWire() == "10", "SUM table cell range");
var rowsCount = new AggregateExpressionDto { Kind = "CALL", Name = "COUNT", Arguments = [range], Options = new() { Basis = "ROWS" } };
var presentCount = rowsCount with { Options = new() { Basis = "PRESENT_ROWS", ColumnIndex = 1 } };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(rowsCount, tableInputs)).Value.Number!.ToWire() == "3", "COUNT all table row slots");
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(presentCount, tableInputs)).Value.Number!.ToWire() == "2", "COUNT present rows includes actual zero");
var emptyColumn = new AggregateExpressionDto { Kind = "TABLE_RANGE", Ref = "in", Area = new("COLUMN", null, null, 1, 1) };
var emptyTableInputs = new Dictionary<string, AggregateChannel> { ["in"] = new("TABLE", "SINGLE", [Table("r0", "A")], true) };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(rowsCount with { Arguments = [emptyColumn] }, emptyTableInputs)).Value.Number!.ToWire() == "0", "existing empty table column has zero rows without fabricated cells");
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(sumCells with { Arguments = [emptyColumn] }, emptyTableInputs)).Value.State == "NO_RESULT", "SUM empty table column is no result");
var targetSchema = new AggregateTableSchema("target-table", "matrix", ["x"], ["row1", "row2", "row3"], [["NUMBER"], ["NUMBER"], ["NUMBER"]]);
var assignments = new AggregateTableAssignmentDto[] {
    new("copy", "in", "out", new("RANGE", 1, 3, 1, 1), new("RANGE", 1, 3, 1, 1), null),
    new("count", null, "out", null, new("CELL", 2, 2, 1, 1), rowsCount) };
var assigned = AggregateEvaluator.Scalar(AggregateTableAdapter.Assign(targetSchema, "out", assignments, tableInputs, evaluator, budget)).Value.Table!;
Check(assigned.Rows[0][0].Value.Number!.ToWire() == "10" && assigned.Rows[1][0].Value.Number!.ToWire() == "3" && assigned.Rows[2][0].Value.Number!.ToWire() == "0", "last assignment wins only overlapping cell");
Check(matrixSource.Items[0].Value.Table!.Rows[1][0].Value.State == "BLANK", "table input not mutated");
Error(() => AggregateTableAdapter.Select(matrixSource, new("CELL", 4, 4, 1, 1), budget), "AGG_TABLE_RANGE_BOUNDS");
var scalarInputs = new Dictionary<string, AggregateChannel>(tableInputs) { ["field"] = AggregateEvaluator.Single(AggregateValue.Numeric(AggregateNumber.From(3)), []) };
var bridge = new AggregateTableAssignmentDto("bridge", null, "out", null, new("CELL", 1, 1, 1, 1), Binary("+", sumCells, Input("field")));
Error(() => AggregateTableAdapter.Assign(targetSchema, "out", [bridge], scalarInputs, evaluator, budget), "AGG_FIELD_TABLE_BRIDGE_DEFERRED");
var filterNode = new AggregateNodeDto { Id = "filter", Kind = "FILTER", Inputs = [new("in", "NUMBER", "SET")], Outputs = [new("out", "NUMBER", "SET")], Predicate = Binary(">", Input(), Literal("10")) };
inputs["in"] = new("NUMBER", "SET", [new(AggregateValue.Numeric(AggregateNumber.From(10)), [new("r1", "u1", "p", "n")]), new(AggregateValue.Numeric(AggregateNumber.From(20)), [new("r2", "u2", "p", "n")])]);
var filtered = evaluator.Filter(filterNode, inputs);
Check(filtered["out"].Items.Count == 1 && filtered["out"].Items[0].Trace[0].ReportId == "r2", "Filter before calculation keyed by report");
fixture = new Fixture(); request = fixture.Request(Call("SUM"));
var graphFilter = filterNode with { Id = "f" };
request.Recipe.Nodes.Insert(1, graphFilter);
request.Recipe.Edges[0] = new("e1", new("s", "out"), new("f", "in"));
request.Recipe.Edges.Add(new("ef", new("f", "out"), new("c", "in")));
preview = await new AggregatePreviewService(fixture).PreviewAsync(request, "B", default);
Check(preview.Preview.Results.Single().Value!.Value.GetString() == "20" && preview.Preview.LinkedSources.Count == 2 && preview.Preview.ContributingSources.Count == 1, "filter-excluded stays linked but does not contribute");
fixture = new Fixture(); fixture.Headers.Add(fixture.Headers[0]);
await PreviewError(fixture, fixture.Request(Call("SUM")), "AGG_CURRENT_REPORT_AMBIGUOUS");
fixture = new Fixture(); fixture.Slots[0] = fixture.Slots[0] with { EffectiveStart = new(2026, 10, 1) };
preview = await new AggregatePreviewService(fixture).PreviewAsync(fixture.Request(Call("SUM")), "B", default);
Check(preview.Preview.Coverage.Count == 1 && preview.Preview.Results.Single().Value!.Value.GetString() == "30", "expected slots honor effective dates; approved history stays usable");
fixture = new Fixture(); request = fixture.Request(Call("SUM"));
request = request with { Selection = new([new("s", "EXPLICIT_REPORTS", ["r1"], [])]) };
preview = await new AggregatePreviewService(fixture).PreviewAsync(request, "B", default);
Check(preview.Preview.LinkedSources.Count == 1 && fixture.PayloadReads == 1, "picker candidates not linked or read");
await PreviewError(new Fixture(), request with { Selection = new([new("s", "EXPLICIT_REPORTS", ["secret-id"], [])]) }, "AGG_SOURCE_UNAVAILABLE");
fixture = new Fixture(); request = fixture.Request(Call("SUM"));
request.Recipe.TimeRules[0] = request.Recipe.TimeRules[0] with { Mode = "EXPLICIT_RANGE", StartDate = "2026-10-01", EndDate = "2026-10-31" };
preview = await new AggregatePreviewService(fixture).PreviewAsync(request with { InstanceId = "october" }, "B", default);
Check(preview.Preview.Results.Single().State == "NO_RESULT" && preview.Preview.LinkedSources.Count == 0, "new window does not carry September source IDs/results");
var editorEvaluator = new AggregateEvaluator(new(default), []);
var editorInput = new Dictionary<string, AggregateChannel> { ["in"] = new("TEXT", "SET", [new(new("TEXT", "VALUE", Text: "nguyên giá trị"), [])]) };
Check(editorEvaluator.Infer(Call("ONLY"), new Dictionary<string, AggregateExpressionType> { ["in"] = new("TEXT", "SET") }).Shape == "SINGLE", "ONLY preserves type and changes shape explicitly");
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(Call("ONLY"), editorInput)).Value.Text == "nguyên giá trị", "ONLY does not sum or concatenate");
editorInput["in"] = new("TEXT", "SET", []);
Error(() => editorEvaluator.Evaluate(Call("ONLY"), editorInput), "AGG_SINGLE_REPORT_REQUIRED");
editorInput["in"] = new("TEXT", "SET", [new(new("TEXT", "VALUE", Text: "a"), []), new(new("TEXT", "VALUE", Text: "b"), [])]);
Error(() => editorEvaluator.Evaluate(Call("ONLY"), editorInput), "AGG_SINGLE_REPORT_REQUIRED");
editorInput["in"] = new("TEXT", "SINGLE", [new(new("TEXT", "VALUE", Text: "Nội dung TẬP HUẤN\nđoạn cuối"), [])]);
AggregateExpressionDto TextPredicate(string method, string value) => new() { Kind = "CALL", Name = method, Arguments = [Input(), new() { Kind = "TEXT", Value = value }] };
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(TextPredicate("TEXT_CONTAINS", "tập huấn"), editorInput)).Value.Boolean == true, "text contains ignores case and keeps full text");
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(TextPredicate("TEXT_ENDS", "đoạn cuối"), editorInput)).Value.Boolean == true, "text ends includes last line");
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(TextPredicate("TEXT_EQUALS", "nội dung tập huấn\nđoạn cuối"), editorInput)).Value.Boolean == true, "filter text equality follows case-insensitive approved UX");
editorInput["in"] = new("CHOICE_ONE", "SINGLE", [new(new("CHOICE_ONE", "VALUE", Text: "A"), [])]);
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(TextPredicate("HAS_CHOICE", "A"), editorInput)).Value.Boolean == true, "single choice compares code");
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(TextPredicate("HAS_CHOICE", "a"), editorInput)).Value.Boolean == false, "choice code remains case sensitive");
editorInput["in"] = new("CHOICE_MANY", "SINGLE", [new(new("CHOICE_MANY", "VALUE", Choices: ["A", "B"]), [])]);
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(TextPredicate("HAS_CHOICE", "B"), editorInput)).Value.Boolean == true, "multiple choice membership");
Check(AggregatePreviewService.ToWire(AggregateEvaluator.Scalar(editorInput["in"]).Value)!.Value.GetArrayLength() == 2, "multiple choice wire remains array");
editorInput["in"] = new("TEXT", "SINGLE", [new(AggregateValue.Blank("TEXT"), [])]);
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(Call("IS_BLANK"), editorInput)).Value.Boolean == true, "actual blank recognized");
editorInput["in"] = new("TEXT", "SINGLE", [new(AggregateValue.NoResult("TEXT"), [])]);
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(Call("IS_BLANK"), editorInput)).Value.Boolean == false, "missing result not blank");
Check(AggregateEvaluator.Scalar(editorEvaluator.Evaluate(Call("IS_PRESENT"), editorInput)).Value.Boolean == false, "missing result not present");
Check(AggregatePartialDate.Parse("09/2026").Precision == "MONTH", "month precision retained");
Check(AggregatePartialDate.Parse("2026").Precision == "YEAR", "year precision retained");
Check(AggregatePartialDate.Compare("09/2026", "10/2026") < 0, "compare same month precision");
Check(AggregatePartialDate.Compare("15/09/2026", "09/2026") == 0, "full day compares by filter month and year");
Check(AggregatePartialDate.Compare("15/09/2026", "2026") == 0 && AggregatePartialDate.Compare("09/2026", "2026") == 0, "day and month compare by filter year");
Check(AggregatePartialDate.Compare("01/10/2026", "09/2026") > 0 && AggregatePartialDate.Compare("12/2025", "01/2026") < 0, "month comparison preserves year ordering");
Error(() => AggregatePartialDate.Compare("09/2026", "15/09/2026"), "AGG_DATE_PRECISION_UNRESOLVED");
Error(() => AggregatePartialDate.Compare("2026", "09/2026"), "AGG_DATE_PRECISION_UNRESOLVED");
Error(() => AggregatePartialDate.Parse("31/02/2026"), "AGG_DATE_INVALID");
Check(AggregatePartialDate.Parse("29/02/2024").Precision == "DAY", "leap date valid");
var choiceFixture = new Fixture { MemberType = "CHOICE_ONE", TargetChoiceCodes = ["A"] };
choiceFixture.Headers.RemoveAt(1); choiceFixture.Slots.RemoveAt(1);
choiceFixture.Values["r1"] = new("CHOICE_ONE", "VALUE", Text: "A");
var choicePreview = await new AggregatePreviewService(choiceFixture).PreviewAsync(choiceFixture.Request(Call("ONLY")), "B", default);
Check(choicePreview.Preview.Results.Single().Value!.Value.GetString() == "A", "single choice keeps stable code through whole preview");
choiceFixture.TargetChoiceCodes = ["B"];
await PreviewError(choiceFixture, choiceFixture.Request(Call("ONLY")), "AGG_TARGET_CHOICE_UNAVAILABLE");
choiceFixture.MemberType = "CHOICE_MANY"; choiceFixture.TargetChoiceCodes = ["A", "B"];
choiceFixture.Values["r1"] = new("CHOICE_MANY", "VALUE", Choices: ["A", "B"]);
choicePreview = await new AggregatePreviewService(choiceFixture).PreviewAsync(choiceFixture.Request(Call("ONLY")), "B", default);
Check(choicePreview.Preview.Results.Single().Value!.Value.GetArrayLength() == 2, "multi choice retains array without stringification");
choiceFixture.TargetChoiceCodes = ["A"];
await PreviewError(choiceFixture, choiceFixture.Request(Call("ONLY")), "AGG_TARGET_CHOICE_UNAVAILABLE");
var tableFilter = new AggregateExpressionDto { Kind = "TABLE_FILTER", Ref = "in", Area = new("ALL", null, null, null, null),
    ColumnIndex = 1, Predicate = Binary(">", Input("cell"), Literal("2")) };
var filteredTables = evaluator.Evaluate(tableFilter, new Dictionary<string, AggregateChannel> { ["in"] = tableSet });
var filteredAppend = AggregateEvaluator.Scalar(AggregateTableAdapter.Append(filteredTables, budget)).Value.Table!;
Check(filteredAppend.Rows.Count == 3 && string.Join(",", filteredAppend.Units) == "A,B,B", "table region filtering preserves units");
Check(filteredAppend.Rows[0][0].Trace.Single().Row == 3 && filteredAppend.Rows[2][0].Trace.Single().Row == 2, "filtered cells retain original source row coordinates");
var none = evaluator.Evaluate(tableFilter with { Predicate = Binary(">", Input("cell"), Literal("100")) }, new Dictionary<string, AggregateChannel> { ["in"] = tableSet });
Check(AggregateEvaluator.Scalar(AggregateTableAdapter.Append(none, budget)).Value.State == "NO_RESULT", "zero matching records never become a zero result");
Check(none.Items.Count==2,"row filter retains report cardinality even when no records match");
var copyEmpty = new AggregateExpressionDto { Kind="CALL", Name="ONLY", Arguments=[tableFilter with { Predicate=Binary(">",Input("cell"),Literal("100")) }] };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(copyEmpty,new Dictionary<string,AggregateChannel>{["in"]=tableSet with {Items=[tableSet.Items[0]]}})).Value.State=="NO_RESULT","one report with an empty filtered region copies no result rather than failing report cardinality");
var fixedItem = matrixSource.Items[0];
var fixedInput = matrixSource with { Items = [fixedItem with { Value = fixedItem.Value with { Table = fixedItem.Value.Table! with { Schema = targetSchema } } }] };
var masked = evaluator.Evaluate(tableFilter, new Dictionary<string, AggregateChannel> { ["in"] = fixedInput });
Check(masked.Items.Single().Value.Table!.Rows.Count == 3 && masked.Items.Single().Value.Table!.Rows[1][0].Value.State == "NO_RESULT", "matrix masks values without deleting fixed rows");
Check(fixedInput.Items[0].Value.Table!.Rows[1][0].Value.State == "BLANK", "mask does not mutate source table");
var allMasked = tableFilter with { Predicate = Binary(">", Input("cell"), Literal("100")) };
var maskedSum = new AggregateExpressionDto { Kind = "CALL", Name = "SUM", Arguments = [allMasked] };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(maskedSum, new Dictionary<string, AggregateChannel> { ["in"] = fixedInput })).Value.State == "NO_RESULT", "all excluded fixed cells give no result");
var twoColumnSchema = new AggregateTableSchema("two", "vertical", ["status", "amount"], [], [["TEXT", "NUMBER"]]);
var twoColumnTable = new AggregateTable(twoColumnSchema, [
    [new(new("TEXT","VALUE",Text:"yes"), []),new(AggregateValue.Numeric(AggregateNumber.From(7)), [new("r", "u", "p", "table", 1, 2)])],
    [new(new("TEXT","VALUE",Text:"no"), []),new(AggregateValue.Numeric(AggregateNumber.From(9)), [])]], ["u","u"]);
var byStatus = tableFilter with { Area = new("COLUMN", null, null, 2, 2), Predicate = Binary("=", Input("cell"), new() { Kind="TEXT", Value="yes" }) };
var filteredValues = evaluator.Evaluate(byStatus, new Dictionary<string, AggregateChannel> { ["in"] = AggregateEvaluator.Single(new("TABLE", "VALUE", Table:twoColumnTable), [], true) });
Check(filteredValues.Items.Single().Value.Table!.Rows.Single().Single().Value.Number!.ToWire()=="7", "vertical table filters a field outside the numeric calculation region; headers are not values");
Check(filteredValues.Items.Single().Value.Table!.Schema.ColumnCoordinates!.Single()==2, "logical column coordinate survives filtering");
var choiceTable = new AggregateTable(new("choices", "horizontal", ["choices"], [], [["CHOICE_MANY"]]),
    [[new(new("CHOICE_MANY", "VALUE", Choices:["A","B"]), [])], [new(new("CHOICE_MANY", "VALUE", Choices:["B"]), [])]], ["u1","u2"]);
var choiceFilter = tableFilter with { Predicate = new() { Kind="CALL", Name="HAS_CHOICE", Arguments=[Input("cell"),new() {Kind="TEXT",Value="A"}] } };
var choiceFiltered = evaluator.Evaluate(choiceFilter,new Dictionary<string,AggregateChannel> { ["in"]=AggregateEvaluator.Single(new("TABLE","VALUE",Table:choiceTable),[],true) });
Check(choiceFiltered.Items.Single().Value.Table!.Rows.Count==1&&choiceFiltered.Items.Single().Value.Table!.Rows[0][0].Value.Choices!.SequenceEqual(new[]{"A","B"}),"table choice filter uses source codes and preserves the full multi-choice value");
var avgMasked = maskedSum with { Name="AVG", Arguments=[tableFilter] };
Check(AggregateEvaluator.Scalar(evaluator.Evaluate(avgMasked,new Dictionary<string,AggregateChannel> { ["in"]=fixedInput })).Value.Number!.ToWire()=="10","excluded matrix cells do not become AVG blank denominator");
Error(()=>evaluator.Evaluate(tableFilter with {ColumnIndex=0}, new Dictionary<string, AggregateChannel> { ["in"] = tableSet }),"AGG_TABLE_RANGE_BOUNDS");
Error(()=>evaluator.Evaluate(tableFilter with {Predicate=Literal("1")}, new Dictionary<string, AggregateChannel> { ["in"] = tableSet }),"AGG_FILTER_TYPE");
var dateFixture = new Fixture();
var historical = dateFixture.Headers[0] with { IsHistoricalData=true, CompletedDate=new(2026,1,15), SubmittedAtUtc=new(2026,9,28,0,0,0,DateTimeKind.Utc), DueAtUtc=new(2026,2,1), PeriodKey="202601" };
var january = new AggregateReportFilterDto("2026-01-01","2026-01-31");
Check(AggregateReportMetadataFilter.Matches(january,historical),"historical completion date wins over actual later submission");
Check(AggregateReportMetadataFilter.Matches(new(FromDate:"2026-01-15"),historical)
    && AggregateReportMetadataFilter.Matches(new(ToDate:"2026-01-15"),historical),"one-sided date bounds include the selected day");
Check(!AggregateReportMetadataFilter.Matches(new(FromDate:"2026-01-16"),historical)
    && !AggregateReportMetadataFilter.Matches(new(ToDate:"2026-01-14"),historical),"one-sided date bounds exclude outside dates");
Check(AggregateReportMetadataFilter.Matches(new(),historical with {CompletedDate=null}),"no date bounds does not demand a completion date");
Error(()=>AggregateReportMetadataFilter.Matches(new(ToDate:"2026-01-31"),historical with {CompletedDate=null}),"AGG_METADATA_DATE_UNAVAILABLE");
Check(AggregateReportMetadataFilter.Matches(new(DueFromDate:"2026-02-01"),historical)
    && AggregateReportMetadataFilter.Matches(new(DueToDate:"2026-02-01"),historical),"deadline also supports inclusive one-sided bounds");
Check(AggregateReportMetadataFilter.EffectiveDate(historical with {IsHistoricalData=false})==new DateOnly(2026,9,28),"current uses submission date, not historical completion");
Check(AggregateReportMetadataFilter.EffectiveDate(historical with {IsHistoricalData=false,SubmittedAtUtc=new(2026,9,30,18,0,0,DateTimeKind.Utc)})==new DateOnly(2026,10,1),"current submission uses Vietnam date across midnight");
Error(()=>AggregateReportMetadataFilter.Matches(january,historical with {CompletedDate=null}),"AGG_METADATA_DATE_UNAVAILABLE");
Error(()=>AggregateReportMetadataFilter.Matches(january,historical with {WholeReportReadable=false}),"AGG_SOURCE_UNAVAILABLE");
Check(!AggregateReportMetadataFilter.Matches(january with {Kinds=["CURRENT"]},historical),"metadata groups combine with AND");
Check(AggregateReportMetadataFilter.Matches(january with {Kinds=["HISTORICAL"],PeriodKeys=["202601"],UnitIds=["u1"],DueFromDate="2026-02-01",DueToDate="2026-02-01"},historical),"deadline is an independent date filter");
Check(AggregateDigest.Of(historical)!=AggregateDigest.Of(historical with {CompletedDate=new(2026,1,16)}),"date metadata participates in freshness fingerprint");
dateFixture.Headers[0]=historical;dateFixture.Headers[1]=historical with {Pin=dateFixture.Headers[1].Pin,UnitId="u2",CompletedDate=new(2026,2,15)};
var dateRequest=dateFixture.Request(Call("SUM"));dateRequest.Recipe.TimeRules[0]=dateRequest.Recipe.TimeRules[0] with {ReportFilter=january};
var datePreview=await new AggregatePreviewService(dateFixture).PreviewAsync(dateRequest,"B",default);
Check(datePreview.Preview.Results.Single().Value!.Value.GetString()=="10","source metadata filters apply before contribution using existing preview engine");
var fingerprintContext=await dateFixture.ReadContextAsync(dateRequest.Context,"B",default);
var fingerprintSource=await new AggregateSourceResolver(dateFixture).ResolveAsync(dateRequest.Recipe.Nodes[0],dateRequest.Recipe,
    dateRequest.Selection.Sources[0],fingerprintContext,"B",new AggregateBudget(default),default);
var fingerprintSources=new[] {fingerprintSource};
var fingerprintMembership=AggregateDigest.Of(fingerprintSources.Select(r=>new {r.Node.Id,r.MembershipRevision}).OrderBy(r=>r.Id));
var fingerprintLinked=fingerprintSource.Linked.OrderBy(p=>p.ReportId,StringComparer.Ordinal).ToArray();
var legacyDateFingerprint=AggregateDigest.Of(new {actor="B",fingerprintContext.AuthorizationFingerprint,Context=fingerprintContext.Context,
    fingerprintContext.Revisions,recipe=dateRequest.Recipe,Selection=dateRequest.Selection,linked=fingerprintLinked,membership=fingerprintMembership,
    windows=fingerprintSources.SelectMany(r=>r.Windows).ToArray(),numeric="EXACT_RATIONAL_6_TO_EVEN"});
var currentDateFingerprint=AggregateDigest.Of(new {actor="B",fingerprintContext.AuthorizationFingerprint,Context=fingerprintContext.Context,
    fingerprintContext.Revisions,recipe=dateRequest.Recipe,Selection=dateRequest.Selection,linked=fingerprintLinked,membership=fingerprintMembership,
    windows=fingerprintSources.SelectMany(r=>r.Windows).ToArray(),numeric="EXACT_RATIONAL_6_TO_EVEN",dateFilters=AggregatePartialDate.FilterSemantics,textPolicy=AggregateTextPolicy.Semantics});
Check(datePreview.Preview.Revisions.InputDigest==currentDateFingerprint&&currentDateFingerprint!=legacyDateFingerprint,
    "date rule revision changes preview input fingerprint before confirmation can be reused");
dateRequest.Recipe.TimeRules.Add(dateRequest.Recipe.TimeRules[0] with { Id="w2",ReportFilter=new("2026-02-01","2026-02-28") });
dateRequest.Recipe.Nodes[0].Outputs.Add(new("other","NUMBER","SET","n","w2"));
var dateContext=await dateFixture.ReadContextAsync(dateRequest.Context,"B",default);
var separateFields=await new AggregateSourceResolver(dateFixture).ResolveAsync(dateRequest.Recipe.Nodes[0],dateRequest.Recipe,dateRequest.Selection.Sources[0],dateContext,"B",new AggregateBudget(default),default);
Check(separateFields.Outputs["out"].Items.Single().Value.Number!.ToWire()=="10"&&separateFields.Outputs["other"].Items.Single().Value.Number!.ToWire()=="20","fields sharing a source Form keep independent report date filters");
var regionCopy = new AggregateTableAssignmentDto("region-copy", null, "out", null, new("RANGE",1,3,1,1),
    new() { Kind="CALL", Name="ONLY", Arguments=[tableFilter] });
var copyInputs = new Dictionary<string,AggregateChannel> { ["in"]=fixedInput with { Shape="SET" } };
var copiedRegion=AggregateEvaluator.Scalar(AggregateTableAdapter.Assign(targetSchema,"out",[regionCopy],copyInputs,evaluator,budget)).Value.Table!;
Check(copiedRegion.Schema==targetSchema&&copiedRegion.Rows[0][0].Value.Number!.ToWire()=="10"&&copiedRegion.Rows[1][0].Value.State=="NO_RESULT","filtered region copy preserves fixed target and masked cells");
Error(()=>AggregateTableAdapter.Assign(targetSchema,"out",[regionCopy with {Target=new("CELL",1,1,1,1)}],copyInputs,evaluator,budget),"AGG_TABLE_SHAPE");
Error(()=>AggregateTableAdapter.Assign(targetSchema,"out",[regionCopy],new Dictionary<string,AggregateChannel>{["in"]=fixedInput with {Shape="SET",Items=[fixedItem,fixedItem]}},evaluator,budget),"AGG_SINGLE_REPORT_REQUIRED");
var textTarget=targetSchema with {CellTypes=[["TEXT"],["TEXT"],["TEXT"]]};
Error(()=>AggregateTableAdapter.Assign(textTarget,"out",[regionCopy],copyInputs,evaluator,budget),"AGG_TABLE_CELL_TYPE");
var emptyCopy=regionCopy with {Expression=new(){Kind="CALL",Name="ONLY",Arguments=[allMasked]}};
var emptyRegion=AggregateEvaluator.Scalar(AggregateTableAdapter.Assign(targetSchema,"out",[emptyCopy],copyInputs,evaluator,budget)).Value.Table!;
Check(emptyRegion.Rows.All(r=>r.All(c=>c.Value.State=="NO_RESULT")),"copy of fully masked matrix preserves blank results without zero");
var emptyListRegion=AggregateEvaluator.Scalar(AggregateTableAdapter.Assign(targetSchema,"out",[emptyCopy],new Dictionary<string,AggregateChannel>{["in"]=tableSet with {Items=[tableSet.Items[0]]}},evaluator,budget)).Value.Table!;
Check(emptyListRegion.Rows.Count==3&&emptyListRegion.Rows.All(r=>r.All(c=>c.Value.State=="NO_RESULT")),"one report with no matching list rows clears selected target region without losing its structure");
// P04 schema projection must preserve original predicate coordinates after selecting a region.
var wideSchema = new AggregateTableSchema("wide", "matrix", ["a","b","c"], ["r1","r2"], [["NUMBER","NUMBER","NUMBER"],["NUMBER","NUMBER","NUMBER"]]);
IReadOnlyList<AggregateCell> WideRow(params long[] numbers) => numbers.Select(n => new AggregateCell(AggregateValue.Numeric(AggregateNumber.From(n)), [])).ToArray();
var wide = AggregateEvaluator.Single(new("TABLE","VALUE",Table:new(wideSchema,[WideRow(99,10,1),WideRow(98,20,0)],["u","u"])),[],true);
var croppedChannel = AggregateTableAdapter.Select(wide,new("RANGE",1,2,2,3),budget);
var croppedTable = AggregateEvaluator.Scalar(croppedChannel).Value.Table!;
Check(croppedTable.Schema.Columns.SequenceEqual(["b","c"])&&croppedTable.Schema.ColumnCoordinates!.SequenceEqual([2,3]),"cropped headers retain original filter column coordinates");
var filteredCrop = AggregateEvaluator.Scalar(AggregateTableAdapter.FilterRegion(croppedChannel,new("COLUMN",null,null,1,1),3,Binary(">",Input("cell"),Literal("0")),evaluator,budget)).Value.Table!;
Check(filteredCrop.Schema.Columns.SequenceEqual(["b"])&&filteredCrop.Rows[0][0].Value.Number!.ToWire()=="10"&&filteredCrop.Rows[1][0].Value.State=="NO_RESULT","second crop uses local range but original predicate column and preserves matrix rows");
Error(()=>AggregateTableAdapter.FilterRegion(croppedChannel,new("ALL",null,null,null,null),1,Binary(">",Input("cell"),Literal("0")),evaluator,budget),"AGG_TABLE_RANGE_BOUNDS");
Check(AggregateEvaluator.Scalar(wide).Value.Table!.Schema.Columns.Count==3,"schema projection does not mutate source table");
var progressFixture = new Fixture();
var progressEvents = new List<AggregatePreviewProgress>();
var progressResult = await new AggregatePreviewService(progressFixture).PreviewAsync(progressFixture.Request(Call("SUM")), "B", default,
    p => { progressEvents.Add(p); return Task.CompletedTask; });
Check(progressEvents.First().Processed == 0 && progressEvents.First().Results.Single().State == "WAITING"
    && progressEvents.First().Results.Single().Value == null, "unread inputs never appear as zero in progress");
Check(progressEvents.Last().Processed == 2 && progressEvents.Last().Total == 2
    && progressEvents.Last().Results.Single().State == "PROVISIONAL" && progressEvents.Last().Results.Single().Value?.GetString() == "30",
    "bounded progress exposes provisional sum from the authoritative evaluator");
Check(progressResult.Preview.State == "VALID" && progressResult.Preview.Results.Single().State == "RESULT"
    && progressResult.Preview.PreviewToken == null, "only final envelope is valid and progress cannot mint confirmation");
var waitingFixture = new Fixture(); waitingFixture.Headers.RemoveAt(1); waitingFixture.Slots.RemoveAt(1);
var onlyProgress = new List<AggregatePreviewProgress>();
var onlyResult = await new AggregatePreviewService(waitingFixture).PreviewAsync(waitingFixture.Request(Call("ONLY")), "B", default,
    p => { onlyProgress.Add(p); return Task.CompletedTask; });
Check(onlyProgress.All(p => p.Results.Single().State == "WAITING") && onlyResult.Preview.Results.Single().Value?.GetString() == "10",
    "ONLY waits for complete enumeration even when partial input has one report");
var denominatorFixture = new Fixture(); var templateHeader = denominatorFixture.Headers[0];
denominatorFixture.Headers.Clear(); denominatorFixture.Slots.Clear(); denominatorFixture.Values.Clear();
for (var i = 0; i < 65; i++)
{
    var sourceId = "batch-" + i.ToString("D3");
    denominatorFixture.Headers.Add(templateHeader with { Pin = templateHeader.Pin with { ReportId = sourceId, BindingId = sourceId }, UnitId = sourceId });
    denominatorFixture.Values[sourceId] = AggregateValue.Numeric(AggregateNumber.From(i == 64 ? 1 : 0));
}
var denominatorProgress = new List<AggregatePreviewProgress>();
var denominatorResult = await new AggregatePreviewService(denominatorFixture).PreviewAsync(denominatorFixture.Request(Binary("/", Literal("1"), Call("SUM"))), "B", default,
    p => { denominatorProgress.Add(p); return Task.CompletedTask; });
Check(denominatorProgress.Single(p => p.Processed == 64).Results.Single().State == "WAITING"
    && denominatorResult.Preview.Results.Single().Value?.GetString() == "1", "temporary divide by zero waits while final complete input evaluates successfully");
TraceDictionaryChecks.Run(preview, Check);
await FormulaProbeChecks.Run(Check);
ContentTableChecks.Run(Check, Error);
ListPipelineChecks.Run(Check, Error);
DateFilterChecks.Run(Check, Error);
ExtendedOperatorChecks.Run(Check, Error);
await ChoiceCountTableChecks.Run(Check, PreviewError);
RichTextAggregationChecks.Run(Check, Error);
await ListMergeChecks.Run(Check, Error);
await ErrorLocationChecks.Run(Check);
TextBlockPolicyChecks.Run(Check, Error);
var hiddenFixture = new Fixture();
var hiddenHeader = hiddenFixture.Headers[0] with { Active = false, Pin = hiddenFixture.Headers[0].Pin with { IsCurrent = false } };
hiddenFixture.Headers.RemoveAt(0); hiddenFixture.Hidden.Add(hiddenHeader);
hiddenFixture.Slots[0] = hiddenFixture.Slots[0] with { CurrentReportId = null };
var hiddenRequest = hiddenFixture.Request(Call("SUM")) with { Selection = new([new("s", "EXPLICIT_REPORTS", ["r1","r2"], [])]) };
var hiddenResult = await new AggregatePreviewService(hiddenFixture).PreviewAsync(hiddenRequest,"B",default);
Check(hiddenResult.Preview.Results.Single().Value!.Value.GetString()=="20" && hiddenFixture.PayloadReads==1,"explicit hidden source retains selection but never reads or contributes payload");
Check(hiddenResult.Preview.Issues.Any(i=>i.Code=="AGG_SOURCE_INACTIVE") && hiddenResult.Preview.LinkedSources.All(p=>p.ReportId!="r1"),"hidden warning and lock/contribution identities stay separate");
hiddenRequest = hiddenRequest with { Selection = new([new("s","FORM_SELECTOR",[],["r1"])]) };
Check((await new AggregatePreviewService(hiddenFixture).PreviewAsync(hiddenRequest,"B",default)).Preview.Results.Single().Value!.Value.GetString()=="20","hidden exclusion does not invalidate configuration");
hiddenFixture.Hidden[0]=hiddenHeader with {WholeReportReadable=false};
await PreviewError(hiddenFixture,hiddenRequest,"AGG_SOURCE_UNAVAILABLE");
hiddenFixture.Hidden.Clear();
await PreviewError(hiddenFixture,hiddenRequest,"AGG_SOURCE_UNAVAILABLE");
checks += await ReportSetChecks.Run();
Console.WriteLine($"PASS: {checks} P02/P04 in-memory preview checks; no API, database or jobs invoked.");

sealed class Fixture : IAggregatePreviewReader
{
    internal static readonly AggregateFormPinDto SourcePin = new("formC", "familyC", 1, "schemaC");
    internal static readonly AggregateFormPinDto TargetPin = new("formB", "familyB", 1, "schemaB");
    internal static readonly AggregateDataWindowDeclarationDto Dates = new("2026-09-01", "2026-09-30", "USER_DECLARED", "declaration", 1);
    internal AggregateAuthorityFacts Authority = new(true, true, true, true, true, false, true, true, false, true, true, true, "Draft", false, true, true, true, true);
    internal readonly List<AggregateSourceHeader> Headers = [];
    internal readonly List<AggregateSourceHeader> Hidden = [];
    internal readonly List<AggregateSlot> Slots = [];
    internal readonly Dictionary<string, AggregateValue> Values = [];
    internal bool Complete = true, Fresh = true;
    internal int PayloadReads, Rechecks;
    internal string[] Units = ["u1", "u2"];
    internal string MemberType = "NUMBER";
    internal IReadOnlyList<string>? TargetChoiceCodes;
    internal AggregateTableSchema? TargetTable;
    internal IReadOnlyList<AggregateChoiceOption>? ChoiceOptions;
    private readonly AggregatePeriodContextDto _context = new("PERIODIC", "work", "B", "bindingB", "reportB", "periodB", "piB", "20260930", "2026-09-01", "2026-09-30", null, "scheduleB");
    internal Fixture()
    {
        for (var i = 1; i <= 2; i++)
        {
            var pin = new AggregateSourcePinDto("work", "C" + i, "bindingC" + i, "periodC" + i, "piC" + i,
                "r" + i, 1, 1, 1, "hash" + i, "schemaC", "Approved", true, "rel1", "auth1");
            Headers.Add(new(pin, "B", SourcePin, "u" + i, "20260930", true, true, false, Dates));
            Slots.Add(new("slot" + i, pin.BindingId, "scheduleC", "20260930", pin.WorkReportPeriodId, pin.ReportId, SourcePin, true, new(2026, 1, 1), null, new(2026, 9, 30), Dates));
            Values[pin.ReportId] = AggregateValue.Numeric(AggregateNumber.From(10 * i));
        }
    }
    internal void AddMissing() => Slots.Add(new("slot3", "bindingC3", "scheduleC", "20260930", null, null, SourcePin, true, new(2026, 1, 1), null, new(2026, 9, 30), Dates));
    internal AggregatePreviewRequestDto Request(AggregateExpressionDto expression)
    {
        var nodes = new List<AggregateNodeDto>
        {
            new() { Id = "s", Kind = "SOURCE", Form = SourcePin, Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [], Outputs = [new("out", MemberType, "SET", "n", "w")] },
            new() { Id = "c", Kind = "CALCULATION", Inputs = [new("in", MemberType, "SET")], Outputs = [new("out", MemberType, "SINGLE")], Expressions = [new("out", expression)] },
            new() { Id = "t", Kind = "TARGET", Form = TargetPin, Inputs = [new("in", MemberType, "SINGLE", "total")], Outputs = [] }
        };
        var recipe = new AggregateRecipeDto { SchemaVersion = 1, SemanticProfile = "REPORT_MAPPING_V1", Nodes = nodes,
            Edges = [new("e1", new("s", "out"), new("c", "in")), new("e2", new("c", "out"), new("t", "in"))],
            TimeRules = [new() { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }] };
        return new(_context, "instance", "config", new(1, 1, 0, 0, "schemaB", ""), recipe, new([new("s", "FORM_SELECTOR", [], [])]));
    }
    public Task<AggregateReadContext> ReadContextAsync(AggregatePeriodContextDto selector, string actor, CancellationToken ct)
        => Task.FromResult(new AggregateReadContext(_context, new(TargetPin, new Dictionary<string, AggregateMember> { ["total"] = new("total", TargetTable == null ? MemberType : "TABLE", TargetTable, AllowedChoiceCodes: TargetChoiceCodes) }),
            Authority, new(1, 1, 0, 0, "schemaB", ""), "B:auth", Dates, new Dictionary<string, AggregateValue>()));
    public Task<AggregateSchema> ReadSchemaAsync(AggregateFormPinDto pin, AggregateReadContext context, string actor, CancellationToken ct)
        => Task.FromResult(new AggregateSchema(SourcePin, new Dictionary<string, AggregateMember> { ["n"] = new("n", MemberType) { ChoiceOptions = ChoiceOptions } }));
    public Task<AggregateSourceListing> ListSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct)
        => Task.FromResult(new AggregateSourceListing(Headers, Slots, Complete, "membership1", Units));
    public Task<IReadOnlyList<AggregateSourceHeader>> ReadInactiveSourcesAsync(AggregateReadContext context,AggregateFormPinDto form,IReadOnlyList<string> ids,string actor,CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AggregateSourceHeader>>(Hidden.Where(h=>ids.Contains(h.Pin.ReportId)).ToArray());
    public Task<AggregatePayload> ReadPayloadAsync(AggregateReadContext context, AggregateSourceHeader header, AggregateSchema schema, string actor, CancellationToken ct)
    {
        PayloadReads++; return Task.FromResult(new AggregatePayload(header.Pin, new Dictionary<string, AggregateValue> { ["n"] = Values[header.Pin.ReportId] }));
    }
    public Task<bool> IsCurrentAsync(AggregateReadContext context, IReadOnlyList<AggregateSourcePinDto> pins, string membershipRevision, string actor, CancellationToken ct)
    { Rechecks++; return Task.FromResult(Fresh); }
}
