using System.Text.Json;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal sealed record AggregateFormulaProbe(string NodeId, IReadOnlyList<AggregateProbeChannel> Inputs,
    IReadOnlyList<AggregateProbeChannel> Outputs, IReadOnlyList<AggregateProbeSource> Sources);
internal sealed record AggregateProbeSource(string ReportId, string UnitId, string UnitName, string OccurrenceKey);
internal sealed record AggregateProbeChannel(string PortId, string ValueType, string Shape, int Count, IReadOnlyList<AggregateProbeValue> Items);
internal sealed record AggregateProbeValue(string State, JsonElement? Value, bool Truncated, IReadOnlyList<AggregateTrace> Trace);

internal sealed partial class AggregatePreviewService
{
    internal static void ValidateProbeSelection(AggregatePreviewRequestDto request)
    {
        var selections=request.Selection?.Sources;
        if(selections==null || selections.Any(s=>s==null || s.Mode!="EXPLICIT_REPORTS" || s.ReportIds==null || s.ExcludedReportIds==null || s.ExcludedReportIds.Count!=0)
            || selections.SelectMany(s=>s.ReportIds).Distinct().Count() is <1 or >3)
            throw new AggregatePreviewException("AGG_FORMULA_PROBE_SOURCE_LIMIT");
    }
    private async Task<AggregateFormulaProbe> CaptureFormulaProbe(string nodeId, AggregateRecipeDto recipe,
        Dictionary<(string,string),AggregateChannel> channels, AggregateReadContext context,
        IReadOnlyList<AggregateResolvedSource> resolved, CancellationToken ct)
    {
        var node=recipe.Nodes.Single(n=>n.Id==nodeId);
        async Task<AggregateProbeChannel> Capture(string id,AggregateChannel channel)
        {
            var items=new List<AggregateProbeValue>();
            foreach(var item in channel.Items.Take(20))
            {
                var value=item.Value; var truncated=false;
                JsonElement? wire;
                if(value.ListReference!=null)wire=JsonSerializer.SerializeToElement(value.ListReference,new JsonSerializerOptions(JsonSerializerDefaults.Web));
                else if(value.List!=null && listSink!=null)wire=JsonSerializer.SerializeToElement(await listSink.CaptureAsync(context,value.List,ct),new JsonSerializerOptions(JsonSerializerDefaults.Web));
                else if(value.Table?.ContentRows!=null && contentSink!=null)wire=JsonSerializer.SerializeToElement(await contentSink.CaptureAsync(context,value.Table,ct),new JsonSerializerOptions(JsonSerializerDefaults.Web));
                else if(value.Type=="TEXT" && value.Text?.Length>2000){wire=JsonSerializer.SerializeToElement(value.Text[..2000]);truncated=true;}
                else wire=ToWire(value);
                items.Add(new(value.State,wire,truncated,item.Trace.Take(60).ToArray()));
            }
            return new(id,channel.Type,channel.Shape,channel.Items.Count,items);
        }
        var inputs=new List<AggregateProbeChannel>();var outputs=new List<AggregateProbeChannel>();
        foreach(var port in node.Inputs){var edge=recipe.Edges.Single(e=>e.To.NodeId==nodeId&&e.To.PortId==port.Id);inputs.Add(await Capture(port.Id,channels[(edge.From.NodeId,edge.From.PortId)]));}
        foreach(var port in node.Outputs)outputs.Add(await Capture(port.Id,channels[(nodeId,port.Id)]));
        // Only contributor metadata, already authorized by the normal source resolver.
        var observations=resolved.SelectMany(r=>r.Outputs.Values).SelectMany(c=>c.Items).ToArray();
        var sources=observations.SelectMany(i=>i.Trace).DistinctBy(t=>t.ReportId)
            .Select(t=>new AggregateProbeSource(t.ReportId,t.UnitId,observations.FirstOrDefault(i=>i.SourceNote?.ReportId==t.ReportId)?.SourceNote?.UnitName??"",t.OccurrenceKey)).ToArray();
        return new(nodeId,inputs,outputs,sources);
    }
}
