using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class ExtendedOperatorChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> error)
    {
        AggregateExpressionDto Call(string name, string? basis = null) => new() { Kind = "CALL", Name = name,
            Arguments = [new() { Kind = "INPUT", Ref = "A" }], Options = basis == null ? null : new() { Basis = basis } };
        AggregateChannel Channel(string type, params AggregateValue[] values) => new(type, "SET",
            values.Select((v, i) => new AggregateObservation(v, [new("r" + i, "u", "p", "f")])).ToArray());
        AggregateValue Run(string name, AggregateChannel input, string? basis = null) => AggregateEvaluator.Scalar(
            new AggregateEvaluator(new(default), []).Evaluate(Call(name, basis), new Dictionary<string, AggregateChannel> { ["A"] = input })).Value;
        var numbers = Channel("NUMBER", AggregateValue.Numeric(AggregateNumber.From(10)), AggregateValue.Blank("NUMBER"));
        check(Run("AVG", numbers).Number!.ToWire() == "5" && Run("AVG_PRESENT", numbers).Number!.ToWire() == "10", "v3 AVG_PRESENT keeps old AVG denominator");
        check(Run("AVG_PRESENT", Channel("NUMBER", AggregateValue.Blank("NUMBER"))).State == "NO_RESULT", "present average all blank is not zero");
        check(Run("DATE_MIN", Channel("DATE_ONLY", new("DATE_ONLY", "VALUE", Text: "2026-10-03"), AggregateValue.Blank("DATE_ONLY"), new("DATE_ONLY", "VALUE", Text: "2026-09-29"))).Text == "2026-09-29", "minimum date ignores blank");
        check(Run("DATE_MAX", Channel("DATE_ONLY", new("DATE_ONLY", "VALUE", Text: "2026-10-03"), new("DATE_ONLY", "VALUE", Text: "2026-09-29"))).Text == "2026-10-03", "maximum date");
        error(() => Run("DATE_MIN", Channel("DATE_PARTIAL", new AggregateValue("DATE_PARTIAL", "VALUE", Text: "2026"))), "AGG_EXPRESSION_TYPE");
        var choices = Channel("CHOICE_MANY", new("CHOICE_MANY", "VALUE", Choices: ["B", "A"]), new("CHOICE_MANY", "VALUE", Choices: ["C", "B"]));
        check(Run("CHOICE_UNION", choices).Choices!.SequenceEqual(new[] { "A", "B", "C" }), "choice union canonical order");
        check(Run("CHOICE_INTERSECTION", choices).Choices!.SequenceEqual(new[] { "B" }), "choice intersection");
        choices = choices with { Items = choices.Items.Append(new(AggregateValue.Blank("CHOICE_MANY"), [])).ToArray() };
        check(Run("CHOICE_UNION", choices).Choices!.Count == 3 && Run("CHOICE_INTERSECTION", choices).Choices!.Count == 0, "valid blank choice is empty set, not a missing source");
        check(Run("CHOICE_UNION", Channel("CHOICE_MANY")).State == "NO_RESULT", "no choice sources is no result");
        AggregateExpressionDto CountChoice(string code) => new() { Kind = "CALL", Name = "COUNT_CHOICE",
            Arguments = [new() { Kind = "INPUT", Ref = "A" }, new() { Kind = "TEXT", Value = code }] };
        AggregateChannel Counted(AggregateChannel input, string code) => new AggregateEvaluator(new(default), [])
            .Evaluate(CountChoice(code), new Dictionary<string, AggregateChannel> { ["A"] = input });
        var one = new AggregateChannel("CHOICE_ONE", "SET", [
            new(new("CHOICE_ONE", "VALUE", Text: "x"), [new("r1", "u1", "p", "f")]),
            new(new("CHOICE_ONE", "VALUE", Text: "y"), [new("r2", "u2", "p", "f")]),
            new(AggregateValue.Blank("CHOICE_ONE"), [new("r3", "u3", "p", "f")])]);
        check(AggregateEvaluator.Scalar(Counted(one, "x")).Value.Number!.ToWire() == "1", "single-choice code count ignores other code and blank");
        var many = new AggregateChannel("CHOICE_MANY", "SET", [
            new(new("CHOICE_MANY", "VALUE", Choices: ["x", "y", "x"]), [new("r1", "u1", "p", "f")]),
            new(new("CHOICE_MANY", "VALUE", Choices: ["x"]), [new("r1", "u1", "p", "f")]),
            new(new("CHOICE_MANY", "VALUE", Choices: ["y"]), [new("r2", "u2", "p", "f")])]);
        var counted = AggregateEvaluator.Scalar(Counted(many, "x"));
        check(counted.Value.Number!.ToWire() == "1" && counted.Trace.Single().ReportId == "r1", "multi-choice code counts each report once and traces matching report");
        check(AggregateEvaluator.Scalar(Counted(many, "y")).Value.Number!.ToWire() == "2", "second target code counts independently on the same source set");
        check(AggregateEvaluator.Scalar(Counted(many, "z")).Value.Number!.ToWire() == "0", "unselected code is zero, not report count");
        error(() => Counted(Channel("TEXT", new AggregateValue("TEXT", "VALUE", Text: "x")), "x"), "AGG_EXPRESSION_TYPE");
        error(() => Counted(one, ""), "AGG_CHOICE_COUNT_CODE");
        var opinions = new AggregateChannel("TEXT", "SET", [
            new(new("TEXT", "VALUE", Text: "Ý kiến A"), [new("r1", "u2", "p1", "f")]) { SourceNote = new("", "s1", "r1", "u2", "Đơn vị B", null, "p1", "f", null, null) },
            new(new("TEXT", "VALUE", Text: "Ý kiến B"), [new("r2", "u1", "p1", "f")]) { SourceNote = new("", "s2", "r2", "u1", "Đơn vị A", null, "p1", "f", null, null) }]);
        var joined = new AggregateEvaluator(new(default), []).Evaluate(Call("CONCAT_UNIT"), new Dictionary<string, AggregateChannel> { ["A"] = opinions });
        check(AggregateEvaluator.Scalar(joined).Value.Text == "Đơn vị A - Ý kiến B\n\nĐơn vị B - Ý kiến A"
            && AggregateEvaluator.Scalar(joined).Trace.Count == 2, "unit-opinion text is ordered and retains both source traces");
        error(() => Run("CONCAT_UNIT", Channel("TEXT", new AggregateValue("TEXT", "VALUE", Text: "ý kiến"))), "AGG_UNIT_NAME_UNAVAILABLE");
        AggregateChannel Text(AggregateValue value) => AggregateEvaluator.Single(value, []);
        check(Run("LEN", Text(new("TEXT", "VALUE", Text: "a\u0301👨‍👩‍👧‍👦🇻🇳"))).Number!.ToWire() == "3", "LEN counts Vietnamese combining and emoji graphemes");
        check(Run("LEN", Text(new("TEXT", "VALUE", Text: "<b>x</b>"))).Number!.ToWire() == "8", "LEN never treats plain text as markup");
        check(Run("LEN", Text(new("TEXT", "VALUE", Text: "<p><b>a&#769;</b> &amp; x</p>") { TextFormat = "RICH_HTML" })).Number!.ToWire() == "5", "LEN rich text uses decoded visible text");
        check(Run("TRIM", Text(new("TEXT", "VALUE", Text: "  a  b  "))).Text == "a  b", "TRIM preserves interior whitespace");
        check(Run("LEN", Text(AggregateValue.Blank("TEXT"))).State == "BLANK" && Run("TRIM", Text(AggregateValue.NoResult("TEXT"))).State == "NO_RESULT", "text blank/no result states preserved");
        error(() => Run("LEN", Channel("TEXT", new AggregateValue("TEXT", "VALUE", Text: "x"))), "AGG_EXPRESSION_TYPE");
        var reports = numbers with { Items = [], EligibleSources = [new("r0", "u", "p", "f"), new("r1", "u", "p", "f")] };
        check(Run("REPORT_COUNT", reports, "ELIGIBLE_SOURCES").Number!.ToWire() == "2" && Run("REPORT_COUNT", reports, "SELECTED_ELEMENTS").Number!.ToWire() == "0", "report envelope survives no selected values");
        var listSchema = new AggregateListSchema([new("score", "NUMBER"), new("weight", "NUMBER"), new("name", "TEXT")]);
        AggregateListRow Row(string id, int? score, int? weight, string name) => new(id, new(null, "u", "p", "l", id, "s"), new Dictionary<string, AggregateCell> {
            ["score"] = new(score == null ? AggregateValue.Blank("NUMBER") : AggregateValue.Numeric(AggregateNumber.From(score.Value)), []),
            ["weight"] = new(weight == null ? AggregateValue.Blank("NUMBER") : AggregateValue.Numeric(AggregateNumber.From(weight.Value)), []),
            ["name"] = new(new("TEXT", "VALUE", Text: name), []) });
        AggregateChannel Lists(params AggregateListRow[] rows) => new("LIST", "SET", [new(new("LIST", "VALUE") { List = new(listSchema, rows) }, [])], ListSchema: listSchema);
        var plan = new AggregateListPipelineDto { Version = 2, Sort = [], Scope = "ALL_SOURCES", Take = "ALL",
            Project = [new("score", "score"), new("weight", "weight"), new("name", "name")], Operation = "WEIGHTED_AVG", ValueFieldId = "score", WeightFieldId = "weight" };
        AggregateValue Pipeline(AggregateListPipelineDto p, AggregateChannel c) => AggregateEvaluator.Scalar(AggregateListPipeline.Evaluate(p, c, new(default), [])).Value;
        check(Pipeline(plan, Lists(Row("1", 10, 1, "a"), Row("2", 20, 2, "b"))).Number!.ToWire() == "16.666667", "weighted average same-item pairing and six decimals");
        check(Pipeline(plan, Lists(Row("1", null, 0, "a"))).State == "NO_RESULT", "zero weights ignore blank scores and no zero answer");
        error(() => Pipeline(plan, Lists(Row("1", 10, null, "a"))), "AGG_LIST_WEIGHT_INVALID");
        error(() => Pipeline(plan, Lists(Row("1", 10, -1, "a"))), "AGG_LIST_WEIGHT_INVALID");
        error(() => Pipeline(plan, Lists(Row("1", null, 1, "a"))), "AGG_LIST_SCORE_MISSING");
        var distinct = plan with { Operation = "COUNT_DISTINCT_FIELD", ValueFieldId = "name", WeightFieldId = null, Trim = true, CaseSensitive = false };
        check(Pipeline(distinct, Lists(Row("1", 10, 1, " A "), Row("2", 20, 2, "a"))).Number!.ToWire() == "1", "distinct list field explicit text normalization");
        error(() => Pipeline(distinct with { Version = 1 }, Lists()), "AGG_LIST_PIPELINE_SCHEMA");
        var avg = plan with { Operation = "AVG_PRESENT", WeightFieldId = null };
        check(Pipeline(avg, Lists(Row("1", 10, 1, "a"), Row("2", null, 2, "b"))).Number!.ToWire() == "10", "List AVG_PRESENT ignores blank scores");
        var recipe = new Fixture().Request(Call("AVG_PRESENT") with { Arguments = [new() { Kind = "INPUT", Ref = "in" }] }).Recipe;
        string Json(AggregateRecipeDto r) => JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(AggregateMappingValidator.Parse(Json(recipe)).Issues.Any(i => i.Code == "AGG_EXTENDED_PROFILE_REQUIRED"), "v1 rejects new call, no silent semantic upgrade");
        check(AggregateMappingValidator.Parse(Json(recipe with { SchemaVersion = 3, SemanticProfile = "REPORT_MAPPING_EXTENDED_V1" })).StructurallyValid, "v3 accepts opt-in new call");
        var choiceRecipe = new Fixture().Request(CountChoice("x") with { Arguments = [new() { Kind = "INPUT", Ref = "in" }, new() { Kind = "TEXT", Value = "x" }] }).Recipe;
        check(AggregateMappingValidator.Parse(Json(choiceRecipe)).Issues.Any(i => i.Code == "AGG_EXTENDED_PROFILE_REQUIRED"), "choice count requires v3 profile");
        check(AggregateMappingValidator.Parse(Json(choiceRecipe with { SchemaVersion = 3, SemanticProfile = "REPORT_MAPPING_EXTENDED_V1" })).StructurallyValid,
            "v3 structurally accepts exact choice code literal");
        var invalidCode = choiceRecipe with { SchemaVersion = 3, SemanticProfile = "REPORT_MAPPING_EXTENDED_V1",
            Nodes = choiceRecipe.Nodes.Select(n => n.Kind == "CALCULATION" ? n with { Expressions = [new("out", CountChoice(""))] } : n).ToList() };
        check(AggregateMappingValidator.Parse(Json(invalidCode)).Issues.Any(i => i.Code == "AGG_CHOICE_COUNT_CODE"), "blank choice code rejected by recipe validator");
    }
}
