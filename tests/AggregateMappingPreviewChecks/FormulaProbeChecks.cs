using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Services.AggregateMapping;

internal static class FormulaProbeChecks
{
    internal static async Task Run(Action<bool,string> check)
    {
        var f=new Fixture();var h=f.Headers[1];f.Headers.Add(h with{Pin=h.Pin with{ReportId="r3",BindingId="b3"},UnitId="u3"});
        f.Values["r3"]=AggregateValue.Numeric(AggregateNumber.From(30));
        AggregateExpressionDto input=new(){Kind="INPUT",Ref="in"};
        AggregateExpressionDto Call(string name)=>new(){Kind="CALL",Name=name,Arguments=[input]};
        var r=f.Request(new(){Kind="CALL",Name="IF",Arguments=[new(){Kind="BINARY",Name=">",Arguments=[Call("SUM"),new(){Kind="NUMBER",Value="0"}]},Call("SUM"),new(){Kind="NUMBER",Value="0"}]});
        r.Recipe.Nodes[2]=r.Recipe.Nodes[2] with{Inputs=[]};r.Recipe.Edges.RemoveAt(1);
        r=r with{Selection=new([new("s","EXPLICIT_REPORTS",["r1","r2","r3"],[])])};
        var result=await new AggregatePreviewService(f).PreviewAsync(r,"B",default,formulaProbeNode:"c");
        check(result.FormulaProbe!.Outputs.Single().Items.Single().Value!.Value.GetString()=="60","formula probe IF/SUM uses three real resolved inputs");
        check(result.FormulaProbe.Inputs.Single().Count==3&&f.PayloadReads==3&&f.Rechecks==1,"formula probe reads selected payloads and checks freshness");
        check(result.Preview.Results.Count==0&&result.Diff.Count==0&&result.Preview.PreviewToken==null,"formula probe has no target result, diff or Apply token");
        check(!AggregateMappingValidator.Parse(JsonSerializer.Serialize(r.Recipe,new JsonSerializerOptions(JsonSerializerDefaults.Web))).StructurallyValid,"probe graph cannot pass normal save validator");
        var wire=JsonSerializer.Serialize(result);var reopened=JsonSerializer.Deserialize<AggregatePreviewEnvelope>(wire)!;
        check(reopened.FormulaProbe?.Inputs.Single().Count==3,"formula probe roundtrips job envelope");
        r.Recipe.Nodes[1]=r.Recipe.Nodes[1] with{Outputs=[new("out","NUMBER","SET")],Expressions=[new("out",input)]};
        result=await new AggregatePreviewService(f).PreviewAsync(r,"B",default,formulaProbeNode:"c");
        check(result.FormulaProbe!.Outputs.Single().Count==3,"GET collection trial does not force ONLY or implicit SUM");
        try{AggregatePreviewService.ValidateProbeSelection(r with{Selection=new([new("s","FORM_SELECTOR",[],[])])});throw new Exception("expected bounded selection");}
        catch(AggregatePreviewException ex){check(ex.Code=="AGG_FORMULA_PROBE_SOURCE_LIMIT","formula trial cannot scan all source payloads");}
        f.Fresh=false;
        try{await new AggregatePreviewService(f).PreviewAsync(r,"B",default,formulaProbeNode:"c");throw new Exception("expected stale");}
        catch(AggregatePreviewException ex){check(ex.Code=="AGG_INPUT_STALE","formula probe never returns stale source inputs");}
    }
}
