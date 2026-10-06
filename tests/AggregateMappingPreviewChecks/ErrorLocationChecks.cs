using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class ErrorLocationChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        AggregateExpressionDto Input() => new() { Kind = "INPUT", Ref = "in" };
        async Task Failure(Fixture fixture, AggregatePreviewRequestDto request, string code, string path, string? node, string? port)
        {
            try { await new AggregatePreviewService(new ListSchemaReader(fixture)).PreviewAsync(request, "B", default); throw new Exception("Expected " + code); }
            catch (AggregatePreviewException ex)
            {
                check(ex.Code == code && ex.Path == path && ex.Location?.NodeId == node && ex.Location?.PortId == port,
                    "runtime failure has exact stable location: " + code + " at " + path);
            }
        }
        var fixture = new Fixture();
        var divide = fixture.Request(new() { Kind = "BINARY", Name = "/", Arguments = [new() { Kind = "CALL", Name = "SUM", Arguments = [Input()] }, new() { Kind = "NUMBER", Value = "0" }] });
        await Failure(fixture, divide, "AGG_DIVIDE_BY_ZERO", "$.nodes[c].expressions[out].expression", "c", "out");
        fixture = new Fixture { MemberType = "DATE_PARTIAL" };
        foreach (var id in fixture.Values.Keys) fixture.Values[id] = new("DATE_PARTIAL", "VALUE", Text: "2026");
        var date = fixture.Request(new() { Kind = "CALL", Name = "ONLY", Arguments = [Input()] });
        var filter = new AggregateNodeDto { Id = "filter", Kind = "FILTER", Inputs = [new("in", "DATE_PARTIAL", "SET")], Outputs = [new("out", "DATE_PARTIAL", "SET")],
            Predicate = new() { Kind = "BINARY", Name = "<", Arguments = [Input(), new() { Kind = "DATE_PARTIAL", Value = "15/09/2026" }] } };
        date = date with { Recipe = date.Recipe with { Nodes = [.. date.Recipe.Nodes, filter], Edges = [new("a", new("s", "out"), new("filter", "in")), new("b", new("filter", "out"), new("c", "in")), date.Recipe.Edges[1]] } };
        await Failure(fixture, date, "AGG_DATE_PRECISION_UNRESOLVED", "$.nodes[filter].predicate", "filter", null);
        fixture = new Fixture { MemberType = "CHOICE_ONE", TargetChoiceCodes = ["A"] };
        fixture.Headers.RemoveAt(1); fixture.Slots.RemoveAt(1); fixture.Values["r1"] = new("CHOICE_ONE", "VALUE", Text: "B");
        await Failure(fixture, fixture.Request(new() { Kind = "CALL", Name = "ONLY", Arguments = [Input()] }),
            "AGG_TARGET_CHOICE_UNAVAILABLE", "$.nodes[t].inputs[in]", "t", "in");
        fixture = new Fixture(); fixture.Values["r1"] = new("TEXT", "VALUE", Text: "wrong type");
        await Failure(fixture, fixture.Request(new() { Kind = "CALL", Name = "SUM", Arguments = [Input()] }),
            "AGG_SOURCE_VALUE_INVALID", "$.nodes[s]", "s", null);

        fixture = new Fixture();
        var schema = new AggregateListSchema([new("score", "NUMBER")]);
        foreach (var header in fixture.Headers)
        {
            var row = new AggregateListRow(header.Pin.ReportId, new(header.Pin, header.UnitId, header.OccurrenceKey, "n", header.Pin.ReportId, "s:out"),
                new Dictionary<string, AggregateCell> { ["score"] = new(AggregateValue.Numeric(AggregateNumber.From(1)), [new(header.Pin.ReportId, header.UnitId, header.OccurrenceKey, "n")]) });
            fixture.Values[header.Pin.ReportId] = new("LIST", "VALUE") { List = new(schema, [row]) };
        }
        var list = fixture.Request(new() { Kind = "LIST_PIPELINE", Ref = "in", ListPipeline = new() { Version = 1, Scope = "ALL_SOURCES", Take = "ALL", Sort = [],
            Project = [new("score", "score")], Operation = "ONLY_ITEM", ValueFieldId = "score" } });
        list = list with { Recipe = list.Recipe with { SchemaVersion = 2, SemanticProfile = "REPORT_MAPPING_LIST_V1", Nodes = list.Recipe.Nodes.Select(n => n.Kind == "SOURCE"
            ? n with { Outputs = [new("out", "LIST", "SET", "n", "w")] } : n.Kind == "CALCULATION" ? n with { Inputs = [new("in", "LIST", "SET")] } : n).ToList() } };
        await Failure(fixture, list, "LIST_SINGLE_ITEM_REQUIRED", "$.nodes[c].expressions[out].expression", "c", "out");
        var bracketId = "calculation]with[brackets";
        list = list with { Recipe = list.Recipe with { Nodes = list.Recipe.Nodes.Select(n => n.Id == "c" ? n with { Id = bracketId } : n).ToList(), Edges = list.Recipe.Edges.Select(e =>
            e with { From = e.From.NodeId == "c" ? e.From with { NodeId = bracketId } : e.From, To = e.To.NodeId == "c" ? e.To with { NodeId = bracketId } : e.To }).ToList() } };
        await Failure(fixture, list, "LIST_SINGLE_ITEM_REQUIRED", $"$.nodes[{bracketId}].expressions[out].expression", bracketId, "out");
        var contextFailure = new Fixture { Authority = new(true, false, true, true, true, false, true, true, false, true, true, true, "Draft", false, true, true, true, true) };
        await Failure(contextFailure, contextFailure.Request(new() { Kind = "CALL", Name = "SUM", Arguments = [Input()] }), "AGG_PREVIEW_FORBIDDEN", "$", null, null);
    }
    private sealed class ListSchemaReader(Fixture fixture) : IAggregatePreviewReader
    {
        public Task<AggregateReadContext> ReadContextAsync(AggregatePeriodContextDto context, string actor, CancellationToken ct) => fixture.ReadContextAsync(context, actor, ct);
        public Task<AggregateSchema> ReadSchemaAsync(AggregateFormPinDto pin, AggregateReadContext context, string actor, CancellationToken ct)
            => fixture.Values.Values.FirstOrDefault()?.List is { } list
                ? Task.FromResult(new AggregateSchema(Fixture.SourcePin, new Dictionary<string, AggregateMember> { ["n"] = new("n", "LIST", List: list.Schema) }))
                : fixture.ReadSchemaAsync(pin, context, actor, ct);
        public Task<AggregateSourceListing> ListSourcesAsync(AggregateReadContext context, AggregateFormPinDto form, string actor, CancellationToken ct) => fixture.ListSourcesAsync(context, form, actor, ct);
        public Task<AggregatePayload> ReadPayloadAsync(AggregateReadContext context, AggregateSourceHeader header, AggregateSchema schema, string actor, CancellationToken ct) => fixture.ReadPayloadAsync(context, header, schema, actor, ct);
        public Task<bool> IsCurrentAsync(AggregateReadContext context, IReadOnlyList<AggregateSourcePinDto> pins, string membership, string actor, CancellationToken ct) => fixture.IsCurrentAsync(context, pins, membership, actor, ct);
    }
}
