using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class ReportSetChecks
{
    internal static async Task<int> Run()
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("REPORT_SET: " + label); count++; }
        AggregatePreviewRequestDto Request(Fixture f, params AggregateReportConditionDto[] conditions)
        {
            var q = f.Request(new() { Kind = "CALL", Name = "SUM", Arguments = [new() { Kind = "INPUT", Ref = "in" }] });
            q.Recipe.TimeRules[0] = new() { Id = "w", Mode = "REPORT_SET", SourceDateBasis = "REPORT_METADATA", Match = "REPORTS", ReportSet = new(1, "AND", [..conditions]) };
            return q;
        }
        async Task<AggregatePreviewEnvelope> Preview(Fixture f, AggregatePreviewRequestDto q) => await new AggregatePreviewService(f).PreviewAsync(q, "B", default);
        var fixture = new Fixture();
        fixture.AddMissing();
        for (var i = 0; i < fixture.Slots.Count; i++) fixture.Slots[i] = fixture.Slots[i] with { DataWindow = null };
        for (var i = 0; i < fixture.Headers.Count; i++) fixture.Headers[i] = fixture.Headers[i] with { DataWindow = null };
        var request = Request(fixture);
        Check(AggregateMappingValidator.Parse(JsonSerializer.Serialize(request.Recipe, new JsonSerializerOptions(JsonSerializerDefaults.Web))).StructurallyValid, "typed contract accepts explicit unbounded report set");
        var mixed = Request(fixture).Recipe;
        mixed.TimeRules.Add(mixed.TimeRules[0] with { Id = "other" });
        mixed.Nodes.Single(n=>n.Kind=="SOURCE").Outputs.Add(new("another","NUMBER","SET","another","other"));
        Check(AggregateMappingValidator.Parse(JsonSerializer.Serialize(mixed,new JsonSerializerOptions(JsonSerializerDefaults.Web))).Issues.Any(i=>i.Code=="AGG_REPORT_SET_INVALID"),"source-wide filter cannot secretly diverge per field");
        var result = await Preview(fixture, request);
        Check(result.Preview.Results.Single().Value!.Value.GetString() == "30", "missing future windows never block current reports");
        Check(result.Preview.Windows.Count == 0 && result.Preview.Coverage.Count == 0, "no invented dates or expected schedule denominator");
        Check(fixture.PayloadReads == 2, "reads eligible report payloads only");
        fixture = new Fixture();
        fixture.Headers[0] = fixture.Headers[0] with { Pin = fixture.Headers[0].Pin with { Status = "Submitted" } };
        fixture.Headers[1] = fixture.Headers[1] with { SubmittedAtUtc = new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc) };
        result = await Preview(fixture, Request(fixture, new AggregateReportConditionDto("SUBMITTED", "EQ", ["2026-10-01"])));
        Check(result.Preview.Results.Single().Value!.Value.GetString() == "20" && fixture.PayloadReads == 1, "unapproved missing metadata excluded before date evaluation; Vietnam day");
        var explicitRequest=Request(fixture) with {Selection=new([new("s","EXPLICIT_REPORTS",["r1","r2"],[])])};
        result=await Preview(fixture,explicitRequest);
        Check(result.Preview.LinkedSources.Count==2&&result.Preview.ContributingSources.Count==1,"explicit lock set differs from Approved contributors");
        fixture = new Fixture();
        fixture.Headers[0] = fixture.Headers[0] with { Active = false };
        result = await Preview(fixture, Request(fixture));
        Check(result.Preview.Results.Single().Value!.Value.GetString() == "20" && fixture.PayloadReads == 1, "hidden report does not contribute");
        fixture = new Fixture();
        request = Request(fixture, new AggregateReportConditionDto("SUBMITTED", "GE", ["2026-01-01"]));
        try { await Preview(fixture, request); throw new Exception("Expected missing metadata"); }
        catch (AggregatePreviewException e) { Check(e.Code == "AGG_METADATA_DATE_UNAVAILABLE", "unknown approved date is explicit, not zero/false"); }
        result = await Preview(fixture, Request(fixture, new AggregateReportConditionDto("SUBMITTED", "ABSENT", [])));
        Check(result.Preview.Results.Single().Value!.Value.GetString() == "30", "explicit missing-value selection");
        result = await Preview(fixture, Request(fixture, new AggregateReportConditionDto("SUBMITTED", "PRESENT", []), new("SUBMITTED", "GE", ["2026-01-01"])));
        Check(result.Preview.Results.Single().State == "NO_RESULT", "present guard excludes missing dates without manufacturing zero");
        var context = await fixture.ReadContextAsync(request.Context, "B", default);
        var h = fixture.Headers[0] with { IsHistoricalData = true, CompletedDate = new(2026, 1, 15), SubmittedAtUtc = new(2026, 10, 6),
            AssignedAtUtc = new(2026, 1, 4, 18, 0, 0), AssignmentStartDate = new(2026, 1, 1), PeriodEnd = new(2026, 1, 31), DueAtUtc = new(2026, 2, 5) };
        bool? Match(string field, string op, params string[] values) => AggregateReportSetFilter.Matches(new(1,"AND",[new(field,op,[..values])]),h,context);
        Check(Match("COMPLETION_OR_SUBMISSION","EQ","2026-01-15") == true, "historical completion stays distinct from submission");
        Check(Match("ASSIGNED","EQ","2026-01-05") == true && Match("ASSIGNMENT_START","EQ","2026-01-01") == true, "assignment event versus declared start");
        Check(Match("PERIOD_END","EQ","2026-01-31") == true && Match("DUE","EQ","2026-02-05") == true, "period end versus deadline");
        foreach (var (op,day,expected) in new[]{("LT","2026-01-16",true),("LE","2026-01-15",true),("GT","2026-01-15",false),("GE","2026-01-15",true)})
            Check(Match("COMPLETED",op,day)==expected,"typed date "+op);
        Check(AggregateReportSetFilter.Matches(new(1,"OR",[new("WORK_END","EQ",["2026-01-01"]),new("UNIT","IN",["u1"])]),h,context)==true,"OR true resolves missing other operand");
        Check(AggregateReportSetFilter.Matches(new(1,"AND",[new("WORK_END","EQ",["2026-01-01"]),new("UNIT","IN",["other"])]),h,context)==false,"AND false resolves missing other operand");
        Check(AggregateReportSetFilter.Matches(new(1,"AND",[new("COMPLETED","RANGE",[],"TARGET_DATA_WINDOW")]),h,context)==false,"relative range uses declared target bounds");
        try { AggregateReportSetFilter.Matches(new(1,"AND",[new("COMPLETED","RANGE",["2099-01-01"],"CUMULATIVE_FROM")]),h,context); throw new Exception("Expected invalid cumulative window"); }
        catch(AggregatePreviewException e) { Check(e.Code=="AGG_DATA_WINDOW_UNRESOLVED","cumulative start after target end cannot masquerade as no result"); }
        foreach(var bad in new[]{new AggregateReportConditionDto("PROGRESS","IN",["Completed"]),new("EVALUATION","ABSENT",[]),new("STATUS","IN",["Draft"]),new("SUBMITTED","EQ",["2026-02-30"]),new("UNIT","IN",[])})
        {
            var issues=new List<string>();AggregateReportSetFilter.Validate(new(1,"AND",[bad]),"$",(code,_,_)=>issues.Add(code));
            Check(issues.Contains("AGG_REPORT_SET_INVALID"),"reject unsupported or malformed "+bad.Field);
        }
        fixture=new Fixture();
        var returnedPin=fixture.Headers[1].Pin;
        fixture.Headers[1]=fixture.Headers[1] with {Pin=returnedPin with {Status="Draft"}};
        result=await Preview(fixture,Request(fixture));
        Check(result.Preview.Results.Single().Value!.Value.GetString()=="10"&&result.Preview.ContributingSources.Count==1,"returned former Approved report no longer contributes to SUM");
        fixture.Headers[0]=fixture.Headers[0] with {Pin=fixture.Headers[0].Pin with {Status="Draft"}};
        result=await Preview(fixture,Request(fixture));
        Check(result.Preview.Results.Single().State=="NO_RESULT"&&result.Preview.ContributingSources.Count==0,"returning last Approved report yields no result, not fabricated zero");
        fixture.Headers[1]=fixture.Headers[1] with {Pin=returnedPin};
        result=await Preview(fixture,Request(fixture));
        Check(result.Preview.Results.Single().Value!.Value.GetString()=="20"&&result.Preview.ContributingSources.Count==1,"approved again contributes once, not duplicate historical value");
        fixture=new Fixture {Fresh=false};
        try {await Preview(fixture,Request(fixture));throw new Exception("Expected stale");}
        catch(AggregatePreviewException e){Check(e.Code=="AGG_INPUT_STALE","retains final source fence");}
        Console.WriteLine($"PASS: {count} report-set core checks; no API/DB/browser acceptance.");
        return count;
    }
}
