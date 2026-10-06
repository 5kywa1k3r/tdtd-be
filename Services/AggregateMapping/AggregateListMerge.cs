using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateListMerge
{
    internal static void Validate(AggregateListMergeDto p, IReadOnlySet<string> inputs)
    {
        if (p.Version != 1 || p.Mode is not ("VERTICAL" or "HORIZONTAL") || p.Inputs is not { Count: >= 2 and <= 8 }
            || p.Inputs.Any(i => i == null || !inputs.Contains(i.Ref) || i.Project is not { Count: >= 1 and <= 200 }
                || i.Project.Any(f => string.IsNullOrWhiteSpace(f.FieldId) || string.IsNullOrWhiteSpace(f.OutputFieldId) || f.FieldId.Length > 256 || f.OutputFieldId.Length > 256)
                || i.Project.Select(f => f.OutputFieldId).Distinct(StringComparer.Ordinal).Count() != i.Project.Count)
            || p.Inputs.Select(i => i.Ref).Distinct(StringComparer.Ordinal).Count() != p.Inputs.Count)
            throw new AggregatePreviewException("AGG_LIST_MERGE_SCHEMA");
    }
    internal static AggregateExpressionType Infer(AggregateListMergeDto p, IReadOnlyDictionary<string, AggregateExpressionType> inputs)
    {
        Validate(p, inputs.Keys.ToHashSet(StringComparer.Ordinal));
        var schemas = p.Inputs.Select(i => {
            var input = inputs[i.Ref];
            if (input.Type != "LIST" || input.ListSchema == null) throw new AggregatePreviewException("AGG_LIST_SOURCE_TYPE");
            return i.Project.Select(f => input.ListSchema.Fields.SingleOrDefault(s => s.Id == f.FieldId)
                is { } source ? source with { Id = f.OutputFieldId } : throw new AggregatePreviewException("AGG_LIST_FIELD_REF")).ToArray();
        }).ToArray();
        var fields = p.Mode == "VERTICAL" ? schemas[0] : schemas.SelectMany(s => s).ToArray();
        if (fields.Length > 200 || fields.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new AggregatePreviewException("AGG_LIST_ID_COLLISION");
        if (p.Mode == "VERTICAL" && schemas.Any(s => s.Length != fields.Length || s.Any(f => !fields.Any(first => first.Id == f.Id && first.Type == f.Type))))
            throw new AggregatePreviewException("AGG_LIST_MERGE_TYPE");
        if(p.Mode == "VERTICAL") fields = fields.Select(f => f with { AllowedChoiceCodes = f.Type is "CHOICE_ONE" or "CHOICE_MANY"
            ? schemas.SelectMany(s => s.Where(x => x.Id == f.Id).SelectMany(x => x.AllowedChoiceCodes ?? [])).Distinct(StringComparer.Ordinal).ToArray()
            : f.AllowedChoiceCodes }).ToArray();
        return new("LIST", "SINGLE", ListSchema: new(fields));
    }
    internal static async Task<AggregateChannel> EvaluateAsync(AggregateListMergeDto p, IReadOnlyDictionary<string, AggregateChannel> inputs,
        AggregateBudget budget, List<AggregateListPipelineTrace> traces, List<AggregateListValue> selections,
        IAggregateListSink? store = null, AggregateReadContext? context = null, CancellationToken ct = default)
    {
        var inferred = Infer(p, inputs.ToDictionary(i => i.Key, i => new AggregateExpressionType(i.Value.Type, i.Value.Shape, i.Value.TableOrigin, i.Value.ListSchema)));
        var groups = new List<AggregateListRow[]>(); long bytes = 0;
        foreach (var source in p.Inputs)
        {
            var rows = new List<AggregateListRow>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            void Add(AggregateListRow row)
            {
                budget.Spend();
                if (!ids.Add(row.Key)) throw new AggregatePreviewException("AGG_LIST_ID_COLLISION");
                if (rows.Count == 200) throw new AggregatePreviewException("AGG_LIST_TARGET_LIMIT");
                var cells = source.Project.ToDictionary(f => f.OutputFieldId, f => row.Cells.TryGetValue(f.FieldId, out var cell)
                    ? cell : throw new AggregatePreviewException("AGG_LIST_FIELD_REF"), StringComparer.Ordinal);
                bytes += cells.Values.Sum(c => 512L + (c.Value.Text?.Length ?? 0) * 2L + c.Trace.Count * 64L);
                if (bytes > 16_777_216) throw new AggregatePreviewException("AGG_LIST_SELECTION_BUDGET");
                var contributors = row.Contributors == null ? new[] { new AggregateListContributor(source.Ref, row.Origin) }
                    : row.Contributors.Select(c => c with { InputId = source.Ref + "/" + c.InputId }).ToArray();
                rows.Add(row with { Cells = cells, Contributors = contributors });
            }
            foreach (var item in inputs[source.Ref].Items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Value.State != "VALUE") throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
                if (item.Value.List is { } list) foreach (var row in list.Records) Add(row);
                else if (item.Value.ListReference is { } reference && store != null && context != null)
                    await foreach (var row in store.ReadRowsAsync(context, reference, ct)) Add(row);
                else throw new AggregatePreviewException("AGG_LIST_SOURCE_UNAVAILABLE");
            }
            groups.Add(rows.ToArray());
        }
        var count = p.Mode == "VERTICAL" ? groups.Sum(g => g.Length) : groups.Max(g => g.Length);
        if (count > 200) throw new AggregatePreviewException("AGG_LIST_TARGET_LIMIT");
        var output = new List<AggregateListRow>();
        if (p.Mode == "VERTICAL") output.AddRange(groups.SelectMany(g => g));
        else for (var index = 0; index < count; index++)
        {
            var cells = new Dictionary<string, AggregateCell>(StringComparer.Ordinal); var contributors = new List<AggregateListContributor>();
            for (var slot = 0; slot < groups.Count; slot++)
            {
                budget.Spend();
                if (index < groups[slot].Length)
                { foreach (var cell in groups[slot][index].Cells) cells.Add(cell.Key, cell.Value); contributors.AddRange(groups[slot][index].Contributors!); }
                else foreach (var f in p.Inputs[slot].Project)
                    cells.Add(f.OutputFieldId, new(AggregateValue.Blank(inferred.ListSchema!.Fields.Single(s => s.Id == f.OutputFieldId).Type), []));
            }
            output.Add(new("", new(null, "", "", "", "", ""), cells) { Contributors = contributors });
        }
        if (output.Any(r => r.Contributors!.Count > 64)) throw new AggregatePreviewException("AGG_BUDGET_EXCEEDED");
        output = output.Select(row => row with { Key = AggregateDigest.Of(row.Contributors!.Select(c => new {
            c.InputId, c.Origin.SourceSlot, ReportId = c.Origin.Pin?.ReportId, c.Origin.ListId, c.Origin.RecordId })) }).ToList();
        if (output.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != output.Count) throw new AggregatePreviewException("AGG_LIST_ID_COLLISION");
        budget.Spend(bytes: bytes);
        var value = new AggregateListValue(inferred.ListSchema!, output); selections.Add(value);
        traces.Add(new("ALL_SOURCES", groups.Sum(g => g.Length), groups.Sum(g => g.Length), count, 0, "LIST_MERGE_" + p.Mode, null, 0) {
            MergeInputs = p.Inputs.Select((s, i) => new AggregateListMergeInputCount(s.Ref, groups[i].Length, p.Mode == "HORIZONTAL" ? count - groups[i].Length : 0)).ToArray(),
            Reports = groups.SelectMany(g => g).SelectMany(r => r.Contributors!).GroupBy(c => (c.Origin.Pin?.ReportId, c.Origin.SourceSlot))
                .Select(g => new AggregateListReportCount(g.Key.Item1, g.Key.SourceSlot, g.Count(), g.Count(), g.Count())).ToArray()
        });
        return AggregateEvaluator.Single(new("LIST", "VALUE") { List = value }, output.SelectMany(r => r.Cells.Values.SelectMany(c => c.Trace)).ToArray())
            with { ListSchema = inferred.ListSchema, EligibleSources = p.Inputs.SelectMany(i => inputs[i.Ref].EligibleSources).Distinct().ToArray() };
    }
}
