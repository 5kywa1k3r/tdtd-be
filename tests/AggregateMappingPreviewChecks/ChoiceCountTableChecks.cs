using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class ChoiceCountTableChecks
{
    internal static async Task Run(Action<bool, string> check,
        Func<Fixture, AggregatePreviewRequestDto, string, Task> error)
    {
        var fixture = FixtureWithChoices();
        var result = await new AggregatePreviewService(fixture).PreviewAsync(Request(fixture), "B", default);
        var wire = result.Preview.Results.Single().Value!.Value;
        check(wire.GetProperty("kind").GetString() == AggregateChoiceCountTable.Kind, "choice count table declares native output kind");
        var rows = wire.GetProperty("rows").EnumerateArray().ToArray();
        check(rows.Length == 3 && rows.Select(r => r.GetProperty("cells")[0].GetProperty("value").GetString())
            .SequenceEqual(new[] { "x", "y", "z" }), "all codes, including inactive and zero, are retained in stable order");
        check(rows.Select(r => r.GetProperty("cells")[1].GetProperty("value").GetString())
            .SequenceEqual(new[] { "Chọn X", "Chọn Y", "Ngừng dùng Z" }), "labels captured as literal cells alongside codes");
        check(rows.Select(r => r.GetProperty("cells")[2].GetProperty("value").GetString())
            .SequenceEqual(new[] { "1", "2", "0" }), "multi-choice report/code counts and zero row");
        check(rows[0].GetProperty("cells")[2].GetProperty("lineage").GetArrayLength() == 1
            && rows[2].GetProperty("cells")[2].GetProperty("lineage").GetArrayLength() == 0,
            "count-cell lineage contains matching reports only");
        check(result.Preview.LockImpact.LinkedReportIds.Count == 2, "zero rows do not discard linked source reports");
        var before = result.Preview.Results.Single().Value!.Value.GetRawText();
        fixture = FixtureWithChoices();
        fixture.ChoiceOptions = [new("x", "Tên mới X"), new("y", "Chọn Y"), new("z", "Ngừng dùng Z")];
        result = await new AggregatePreviewService(fixture).PreviewAsync(Request(fixture), "B", default);
        check(result.Preview.Results.Single().Value!.Value.GetProperty("rows")[0].GetProperty("cells")[1].GetProperty("value").GetString() == "Tên mới X"
            && result.Preview.Results.Single().Value!.Value.GetRawText() != before,
            "changed catalog label is recaptured while stable code and counts remain; preview evidence changes");

        fixture = FixtureWithChoices(); fixture.MemberType = "CHOICE_ONE";
        fixture.Values["r1"] = new("CHOICE_ONE", "VALUE", Text: "x");
        fixture.Values["r2"] = new("CHOICE_ONE", "VALUE", Text: "y");
        result = await new AggregatePreviewService(fixture).PreviewAsync(Request(fixture), "B", default);
        check(result.Preview.Results.Single().Value!.Value.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("cells")[2].GetProperty("value").GetString()).SequenceEqual(new[] { "1", "1", "0" }),
            "single-choice fields use the same full option table and zero row");

        fixture = FixtureWithChoices();
        fixture.TargetTable = new("wrong", "vertical", ["code", "label", "count"], [], [["TEXT", "NUMBER", "TEXT"]]);
        await error(fixture, Request(fixture), "AGG_CHOICE_TABLE_TARGET");
        fixture = FixtureWithChoices();
        fixture.ChoiceOptions = Enumerable.Range(0, 201).Select(i => new AggregateChoiceOption("code" + i, "Nhãn " + i)).ToArray();
        await error(fixture, Request(fixture), "AGG_CHOICE_TABLE_LIMIT_200");
        fixture = FixtureWithChoices();
        fixture.ChoiceOptions = Enumerable.Range(0, 200).Select(i => new AggregateChoiceOption("code" + i, "Nhãn " + i)).ToArray();
        fixture.Values["r1"] = new("CHOICE_MANY", "VALUE", Choices: ["code0"]);
        fixture.Values["r2"] = new("CHOICE_MANY", "VALUE", Choices: ["code199"]);
        result = await new AggregatePreviewService(fixture).PreviewAsync(Request(fixture), "B", default);
        check(result.Preview.Results.Single().Value!.Value.GetProperty("rows").GetArrayLength() == 200,
            "exact 200-row boundary succeeds without truncation");
        fixture = FixtureWithChoices(); fixture.Values["r1"] = new("CHOICE_MANY", "VALUE", Choices: ["deleted-code"]);
        await error(fixture, Request(fixture), "AGG_CHOICE_TABLE_CODE_UNAVAILABLE");
        fixture = FixtureWithChoices(); fixture.ChoiceOptions = null;
        await error(fixture, Request(fixture), "AGG_CHOICE_TABLE_OPTIONS_UNAVAILABLE");
    }

    private static Fixture FixtureWithChoices()
    {
        var fixture = new Fixture { MemberType = "CHOICE_MANY",
            ChoiceOptions = [new("x", "Chọn X"), new("y", "Chọn Y"), new("z", "Ngừng dùng Z")],
            TargetTable = new("target", "vertical", ["code", "label", "count"], [], [["TEXT", "TEXT", "NUMBER"]]) };
        fixture.Values["r1"] = new("CHOICE_MANY", "VALUE", Choices: ["x", "y", "x"]);
        fixture.Values["r2"] = new("CHOICE_MANY", "VALUE", Choices: ["y"]);
        return fixture;
    }

    private static AggregatePreviewRequestDto Request(Fixture fixture)
    {
        var baseRequest = fixture.Request(new() { Kind = "CALL", Name = "CHOICE_COUNT_TABLE",
            Arguments = [new() { Kind = "INPUT", Ref = "in" }] });
        return baseRequest with { Recipe = baseRequest.Recipe with {
            SchemaVersion = 3, SemanticProfile = "REPORT_MAPPING_EXTENDED_V1",
            Nodes = baseRequest.Recipe.Nodes.Select(n => n.Kind switch {
                "CALCULATION" => n with { Outputs = [new("out", "TABLE", "SINGLE")] },
                "TARGET" => n with { Inputs = [new("in", "TABLE", "SINGLE", "total")] },
                _ => n }).ToList() } };
    }
}
