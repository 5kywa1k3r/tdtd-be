namespace tdtd_be.Services.AggregateMapping;

// A choice contributes once per report and code. Display labels are never identities.
internal static class AggregateChoiceCount
{
    internal static AggregateChannel Evaluate(AggregateChannel source, string code, AggregateBudget budget)
    {
        if (source.Type is not ("CHOICE_ONE" or "CHOICE_MANY") || source.Shape != "SET" || string.IsNullOrWhiteSpace(code))
            throw new AggregatePreviewException("AGG_CHOICE_COUNT_SOURCE");
        var reports = new HashSet<string>(StringComparer.Ordinal);
        var contributors = new List<AggregateTrace>();
        foreach (var item in source.Items)
        {
            budget.Spend();
            if (item.Value.State == "NO_RESULT") continue;
            if (item.Value.State is not ("VALUE" or "BLANK") || item.Value.Type != source.Type)
                throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
            if (item.Value.State == "BLANK") continue;
            if (source.Type == "CHOICE_ONE" && item.Value.Text == null
                || source.Type == "CHOICE_MANY" && item.Value.Choices == null)
                throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
            var selected = source.Type == "CHOICE_ONE" ? item.Value.Text == code
                : item.Value.Choices!.Contains(code, StringComparer.Ordinal);
            if (!selected) continue;
            foreach (var trace in item.Trace)
            {
                if (string.IsNullOrEmpty(trace.ReportId)) throw new AggregatePreviewException("AGG_CHOICE_COUNT_SOURCE");
                if (reports.Add(trace.ReportId)) contributors.Add(trace);
            }
        }
        return AggregateEvaluator.Single(AggregateValue.Numeric(AggregateNumber.From(reports.Count)), contributors)
            with { EligibleSources = source.EligibleSources };
    }
}
