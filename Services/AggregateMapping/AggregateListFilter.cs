using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// A standalone List filter uses the existing typed pipeline predicate wire, but cannot reduce,
// project, reorder or merge reports. Empty valid Lists remain valid source observations.
internal static class AggregateListFilter
{
    internal static void Validate(AggregateNodeDto node, AggregateChannel input)
    {
        var plan = node.Predicate?.ListPipeline;
        if (node.Inputs.Count != 1 || node.Outputs.Count != 1 || node.Predicate?.Kind != "LIST_PIPELINE"
            || node.Predicate.Ref != node.Inputs[0].Id || plan == null || plan.Version != 2 || plan.Operation != "COLLECT"
            || plan.Sort.Count != 0 || plan.Scope != "PER_REPORT" || plan.Take != "ALL" || plan.TopN != null
            || plan.Project.Any(f => f.FieldId != f.OutputFieldId) || input.ListSchema == null
            || plan.Project.Count != input.ListSchema.Fields.Count || plan.Project.Any(f => !input.ListSchema.Fields.Any(s => s.Id == f.FieldId)))
            throw new AggregatePreviewException("AGG_LIST_FILTER_SCHEMA");
        _ = AggregateListPipeline.Infer(plan, new(input.Type, input.Shape, ListSchema: input.ListSchema));
    }
    internal static async Task<IReadOnlyDictionary<string, AggregateChannel>> EvaluateAsync(AggregateNodeDto node,
        IReadOnlyDictionary<string, AggregateChannel> inputs, AggregateBudget budget, IAggregateListSink? store = null,
        AggregateReadContext? context = null, CancellationToken ct = default)
    {
        var input = inputs[node.Inputs[0].Id]; Validate(node, input);
        var observations = new List<AggregateObservation>();var scanned=0;
        foreach (var observation in input.Items)
        {
            var selected = new List<AggregateListRow>(); long bytes = 0;
            if (observation.Value.State != "VALUE") throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
            void Add(AggregateListRow row)
            {
                budget.Spend();
                if(++scanned>40_000)throw new AggregatePreviewException("AGG_LIST_RECORD_LIMIT");
                if (node.Predicate!.ListPipeline!.Where is {} where && !AggregateListPipeline.Matches(where, row, budget)) return;
                selected.Add(row);
                bytes += row.Cells.Values.Sum(c => 512L + (c.Value.Text?.Length ?? 0) * 2L);
                if (selected.Count > 40_000 || bytes > 16_777_216) throw new AggregatePreviewException("AGG_LIST_SELECTION_BUDGET");
            }
            if (observation.Value.List is {} list) foreach(var row in list.Records) Add(row);
            else if (observation.Value.ListReference is {} reference && store != null && context != null)
                await foreach(var row in store.ReadRowsAsync(context, reference, ct)) Add(row);
            else throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
            var value = new AggregateListValue(input.ListSchema!, selected);
            var filtered = observation.Value with { List = value, ListReference = null };
            if (store != null && context != null)
            {
                filtered = filtered with { List = null, ListReference = await store.CaptureAsync(context, value, ct) };
                budget.Spend(bytes: 1024);
            }
            else budget.Spend(bytes: bytes);
            observations.Add(observation with { Value = filtered });
        }
        return new Dictionary<string, AggregateChannel> { [node.Outputs[0].Id] = input with { Items = observations } };
    }
}
