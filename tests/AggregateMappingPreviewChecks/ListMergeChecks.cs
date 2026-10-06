using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class ListMergeChecks
{
    internal static async Task Run(Action<bool, string> check, Action<Action, string> error)
    {
        var schema = new AggregateListSchema([new("n", "NUMBER"), new("label", "TEXT")]);
        AggregateListRow Row(string report, string record, int number)
        {
            var pin = new AggregateSourcePinDto("w", "a", "b", null, null, report, 1, 1, 1, "hash", "schema", "Approved", true, "rel", "auth");
            return new(report + record, new(pin, "unit" + report, "p", "list", record, "slot" + report), new Dictionary<string, AggregateCell> {
                ["n"] = new(AggregateValue.Numeric(AggregateNumber.From(number)), [new(report, "unit" + report, "p", "list", RecordId: record, FieldId: "n", SourceSlot: "slot" + report)]),
                ["label"] = new(new("TEXT", "VALUE", Text: report + record), [new(report, "unit" + report, "p", "list", RecordId: record, FieldId: "label", SourceSlot: "slot" + report)]) });
        }
        AggregateChannel Channel(string report, params AggregateListRow[] rows) => new("LIST", "SET",
            [new(new("LIST", "VALUE") { List = new(schema, rows) }, [new(report, "unit" + report, "p", "list")])], ListSchema: schema)
            { EligibleSources = [new(report, "unit" + report, "p", "list")] };
        var inputs = new Dictionary<string, AggregateChannel> { ["A"] = Channel("r1", Row("r1", "1", 5), Row("r1", "2", 6)),
            ["B"] = Channel("r2", Row("r2", "1", 7), Row("r2", "2", 8), Row("r2", "3", 9)) };
        var vertical = new AggregateListMergeDto(1, "VERTICAL", [new("A", [new("n", "n"), new("label", "label")]), new("B", [new("n", "n"), new("label", "label")])]);
        var traces = new List<AggregateListPipelineTrace>();
        async Task<AggregateChannel> Merge(AggregateListMergeDto plan, Dictionary<string, AggregateChannel>? data = null)
        { traces.Clear(); return await AggregateListMerge.EvaluateAsync(plan, data ?? inputs, new(default), traces, []); }
        var joined = await Merge(vertical); var rows = AggregateEvaluator.Scalar(joined).Value.List!.Records;
        check(rows.Count == 5 && rows.All(r => r.Contributors!.Count == 1) && rows.SelectMany(r => r.Cells["n"].Trace).Select(t => t.ReportId).Distinct().Count() == 2, "merge vertical five rows retain origins without dedup");
        var horizontal = vertical with { Mode = "HORIZONTAL", Inputs = [new("A", [new("n", "left"), new("label", "leftName")]), new("B", [new("n", "right"), new("label", "rightName")])] };
        joined = await Merge(horizontal); rows = AggregateEvaluator.Scalar(joined).Value.List!.Records;
        check(rows.Count == 3 && rows[0].Contributors!.Count == 2 && rows[2].Contributors!.Count == 1, "horizontal max row count and contributors");
        check(rows[2].Cells["left"].Value.State == "BLANK" && rows[2].Cells["left"].Trace.Count == 0 && rows[2].Cells["right"].Value.Number!.ToWire() == "9", "missing side is blank, no invented source or zero");
        check(traces.Single().MergeInputs!.Single(i => i.InputId == "A").MissingRows == 1, "warning identifies input and missing row count");
        var id = AggregateListWire.OutputId("instance", "member", rows[0]);
        var changed = rows[0] with { Cells = rows[0].Cells.ToDictionary(c => c.Key, c => c.Value with { Value = c.Value.Value.Type == "TEXT" ? c.Value.Value with { Text = "renamed" } : c.Value.Value }) };
        check(id == AggregateListWire.OutputId("instance", "member", changed) && Guid.TryParse(id, out _), "UUID stable after content change");
        check(id != AggregateListWire.OutputId("instance", "member", rows[0] with { Contributors = [rows[0].Contributors![0]] }), "UUID changes when source pair changes");
        var wire = JsonSerializer.SerializeToElement(AggregateListWire.Encode(AggregateEvaluator.Scalar(joined).Value.List!), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundtrip = AggregateListWire.Decode(wire);
        check(wire.GetProperty("kind").GetString() == AggregateListWire.CompositeKind && roundtrip.Records[0].Contributors!.Count == 2 && roundtrip.Records[0].Cells["right"].Trace.Single().ReportId == "r2", "v2 snapshot roundtrip composite per-cell provenance");
        var old = inputs["A"].Items[0].Value.List!;
        var oldWire = JsonSerializer.SerializeToElement(AggregateListWire.Encode(old), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(oldWire.GetProperty("kind").GetString() == AggregateListWire.Kind && !oldWire.GetProperty("records")[0].TryGetProperty("contributors", out _) && AggregateListWire.Decode(oldWire).Records.Count == 2, "old snapshot remains v1, no null property/hash drift");
        error(() => AggregateListMerge.Infer(horizontal with { Inputs = vertical.Inputs }, inputs.ToDictionary(p => p.Key, p => new AggregateExpressionType("LIST", "SET", ListSchema: schema))), "AGG_LIST_ID_COLLISION");
        var many = new Dictionary<string, AggregateChannel>(inputs) { ["A"] = Channel("r1", Enumerable.Range(0, 201).Select(i => Row("r1", i.ToString(), i)).ToArray()) };
        try { await Merge(vertical, many); throw new Exception("merge accepted >200"); } catch (AggregatePreviewException ex) { check(ex.Code == "AGG_LIST_TARGET_LIMIT", "merge target limit enforced before capture"); }
        var count = new AggregateExpressionDto { Kind = "CALL", Name = "REPORT_COUNT", Options = new() { Basis = "ELIGIBLE_SOURCES" }, Arguments = [new() { Kind = "INPUT", Ref = "A" }] };
        var empty = Channel("r1");
        check(AggregateEvaluator.Scalar(new AggregateEvaluator(new(default), []).Evaluate(count, new Dictionary<string, AggregateChannel> { ["A"] = empty })).Value.Number!.ToWire() == "1", "eligible empty List report counts once");
        check(AggregateEvaluator.Scalar(new AggregateEvaluator(new(default), []).Evaluate(count with { Options = new() { Basis = "SELECTED_ELEMENTS" } }, new Dictionary<string, AggregateChannel> { ["A"] = empty })).Value.Number!.ToWire() == "0", "selected-element basis excludes empty List");
        var where = new AggregateListPredicateDto { Operator = "GT", FieldId = "n", Values = ["6"] };
        var filter = new AggregateNodeDto { Id = "f", Kind = "FILTER", Inputs = [new("A", "LIST", "SET")], Outputs = [new("out", "LIST", "SET")],
            Predicate = new() { Kind = "LIST_PIPELINE", Ref = "A", ListPipeline = new() { Version = 2, Where = where, Sort = [], Scope = "PER_REPORT", Take = "ALL", Project = [new("n", "n"), new("label", "label")], Operation = "COLLECT" } } };
        var filtered = (await AggregateListFilter.EvaluateAsync(filter, inputs, new(default)))["out"];
        check(filtered.Shape == "SET" && filtered.Items.Count == 1 && filtered.Items[0].Value.List!.Records.Count == 0 && filtered.EligibleSources.Count == 1, "standalone filter preserves shape and empty-report envelope");
        var sameRow = new AggregateListPredicateDto { Operator = "AND", Children = [
            new() { Operator = "GT", FieldId = "n", Values = ["5"] },
            new() { Operator = "EQ", FieldId = "label", Values = ["r11"], Trim=false, CaseSensitive=true }] };
        var sameRowFilter=filter with {Predicate=filter.Predicate! with {ListPipeline=filter.Predicate!.ListPipeline! with {Where=sameRow}}};
        var noCrossRow=(await AggregateListFilter.EvaluateAsync(sameRowFilter,inputs,new(default)))["out"];
        check(noCrossRow.Items[0].Value.List!.Records.Count==0,"AND cannot combine matches from different elements");
        var postWhere=new AggregateListPredicateDto {Operator="GT",FieldId="right",Values=["8"]};
        var post=filter with {Inputs=[new("A","LIST","SINGLE")],Outputs=[new("out","LIST","SINGLE")],Predicate=filter.Predicate! with {
            ListPipeline=filter.Predicate!.ListPipeline! with {Where=postWhere,Project=roundtrip.Schema.Fields.Select(f=>new AggregateListProjectionDto(f.Id,f.Id)).ToList()}}};
        var postResult=(await AggregateListFilter.EvaluateAsync(post,new Dictionary<string,AggregateChannel>{["A"]=joined},new(default)))["out"];
        var postRows=postResult.Items.Single().Value.List!.Records;
        check(postResult.Shape=="SINGLE"&&postRows.Count==1&&postRows[0].Cells["left"].Value.State=="BLANK"&&postRows[0].Cells["right"].Trace.Single().ReportId=="r2","filter after horizontal merge retains projected fields, blanks and cell lineage");
        var contributor=rows[0].Contributors![0];
        var newPin=contributor with {Origin=contributor.Origin with {Pin=contributor.Origin.Pin! with {PayloadRevision=2,PayloadHash="updated",SchemaHash="new-schema"}}};
        check(id==AggregateListWire.OutputId("instance","member",rows[0] with {Contributors=[newPin,rows[0].Contributors![1]]}),"UUID ignores payload/schema revisions of the same source identity");
        var oversized=new Dictionary<string,AggregateChannel>{["A"]=Channel("r1",Enumerable.Range(0,40_001).Select(i=>Row("r1",i.ToString(),i)).ToArray())};
        var excluding=filter with {Predicate=filter.Predicate! with {ListPipeline=filter.Predicate!.ListPipeline! with {Where=where with {Values=["100000"]}}}};
        try {await AggregateListFilter.EvaluateAsync(excluding,oversized,new(default));throw new Exception("filter scan exceeded limit");}
        catch(AggregatePreviewException ex){check(ex.Code=="AGG_LIST_RECORD_LIMIT","filter scan counts excluded elements toward 40000 limit");}
        var wideSchema=new AggregateListSchema(Enumerable.Range(0,200).Select(i=>new AggregateListField("f"+i,i==0?"TEXT":"NUMBER")).ToArray());
        AggregateChannel Wide(string report){
            var channel=Channel(report,Enumerable.Range(0,50).Select(i=>{
                var row=Row(report,i.ToString(),i);
                return row with {Cells=wideSchema.Fields.ToDictionary(f=>f.Id,f=>new AggregateCell(f.Type=="TEXT"?new("TEXT","VALUE",Text:i==0?new string('x',100_000):"Nội dung"):AggregateValue.Numeric(AggregateNumber.From(i)),row.Cells["n"].Trace.Select(t=>t with {FieldId=f.Id}).ToArray()))};
            }).ToArray());
            return channel with {ListSchema=wideSchema,Items=channel.Items.Select(o=>o with {Value=o.Value with {List=o.Value.List! with {Schema=wideSchema}}}).ToArray()};
        }
        var wideInputs=new Dictionary<string,AggregateChannel>{["A"]=Wide("r1"),["B"]=Wide("r2")};
        var wideProject=wideSchema.Fields.Select(f=>new AggregateListProjectionDto(f.Id,f.Id)).ToList();
        var wideMerge=await Merge(vertical with {Inputs=[new("A",wideProject),new("B",wideProject)]},wideInputs);
        check(wideMerge.ListSchema!.Fields.Count==200&&AggregateEvaluator.Scalar(wideMerge).Value.List!.Records.Count==100,"merge 200 typed fields and 100 rows including 100000 character cells");
        var tooMuch=Row("r1","huge",1);tooMuch=tooMuch with {Cells=tooMuch.Cells.ToDictionary(c=>c.Key,c=>c.Key=="label"?c.Value with {Value=new("TEXT","VALUE",Text:new string('x',8_500_000))}:c.Value)};
        try {await Merge(vertical,new Dictionary<string,AggregateChannel>{["A"]=Channel("r1",tooMuch),["B"]=Channel("r2")});throw new Exception("unbounded merge");}
        catch(AggregatePreviewException ex){check(ex.Code=="AGG_LIST_SELECTION_BUDGET","large text rejects retained selection over 16MiB");}
        var fixture = new Fixture(); fixture.Values["r2"] = AggregateValue.Blank("NUMBER");
        var request = fixture.Request(new() { Kind = "CALL", Name = "AVG_PRESENT", Arguments = [new() { Kind = "INPUT", Ref = "in" }] });
        request = request with { Recipe = request.Recipe with { SchemaVersion = 3, SemanticProfile = "REPORT_MAPPING_EXTENDED_V1" } };
        var preview = await new AggregatePreviewService(fixture).PreviewAsync(request, "B", default);
        check(preview.Preview.Results.Single().Value!.Value.GetString() == "10" && preview.Preview.LinkedSources.Count == 2, "v3 real preview service retains locks and approved source pins");
        fixture.Fresh = false;
        try { await new AggregatePreviewService(fixture).PreviewAsync(request, "B", default); throw new Exception("stale accepted"); }
        catch (AggregatePreviewException ex) { check(ex.Code == "AGG_INPUT_STALE", "v3 source freshness fence unchanged"); }
    }
}
