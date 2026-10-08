namespace tdtd_be.Services.AggregateMapping;

// A bounded native vertical Table. Codes identify options; labels are captured when
// the preview/Apply reads the form or catalog, then persisted as ordinary text cells.
internal static class AggregateChoiceCountTable
{
    internal const string Kind = "CHOICE_COUNT_TABLE_V1";
    internal const int MaxRows = 200;

    internal static AggregateChannel Build(AggregateChannel source, AggregateBudget budget)
    {
        if (source.Type is not ("CHOICE_ONE" or "CHOICE_MANY") || source.Shape != "SET")
            throw new AggregatePreviewException("AGG_CHOICE_TABLE_SOURCE");
        var options = source.ChoiceOptions ?? throw new AggregatePreviewException("AGG_CHOICE_TABLE_OPTIONS_UNAVAILABLE");
        if (options.Count > MaxRows) throw new AggregatePreviewException("AGG_CHOICE_TABLE_LIMIT_200");
        var choices = new Dictionary<string, Dictionary<string, AggregateTrace>>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Code) || option.Code.Length > 256 || option.Label == null
                || !choices.TryAdd(option.Code, new(StringComparer.Ordinal)))
                throw new AggregatePreviewException("AGG_CHOICE_TABLE_OPTIONS_INVALID");
        }
        foreach (var item in source.Items)
        {
            budget.Spend();
            if (item.Value.State == "NO_RESULT") continue;
            if (item.Value.State is not ("VALUE" or "BLANK") || item.Value.Type != source.Type)
                throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
            if (item.Value.State == "BLANK") continue;
            var selected = source.Type == "CHOICE_ONE" ? item.Value.Text is { } selectedCode ? new[] { selectedCode } : null
                : item.Value.Choices;
            if (selected == null) throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
            foreach (var code in selected.Distinct(StringComparer.Ordinal))
            {
                budget.Spend();
                if (!choices.TryGetValue(code, out var reports))
                    throw new AggregatePreviewException("AGG_CHOICE_TABLE_CODE_UNAVAILABLE");
                foreach (var trace in item.Trace)
                {
                    if (string.IsNullOrEmpty(trace.ReportId)) throw new AggregatePreviewException("AGG_CHOICE_TABLE_SOURCE");
                    reports.TryAdd(trace.ReportId, trace);
                }
            }
        }
        var rows = new List<IReadOnlyList<AggregateCell>>(options.Count);
        foreach (var option in options)
        {
            var reports = choices[option.Code].OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
            budget.Spend(bytes: (long)(option.Code.Length + option.Label.Length) * 4 + reports.Length * 64L);
            rows.Add([
                new(new("TEXT", "VALUE", Text: option.Code), []),
                new(new("TEXT", "VALUE", Text: option.Label), []),
                new(AggregateValue.Numeric(AggregateNumber.From(reports.Length)), reports)
            ]);
        }
        var schema = new AggregateTableSchema(Kind, "vertical", ["code", "label", "count"],
            options.Select(o => AggregateDigest.Of(new { o.Code })).ToArray(), [["TEXT", "TEXT", "NUMBER"]]);
        var allSources = source.Items.SelectMany(i => i.Trace).Distinct().ToArray();
        return AggregateEvaluator.Single(new("TABLE", "VALUE", Table: new(schema, rows, Enumerable.Repeat("", rows.Count).ToArray())),
            allSources, true) with { EligibleSources = source.EligibleSources };
    }
}
