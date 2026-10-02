namespace tdtd_be.Services.AggregateMapping;

// Charge retained value objects, not a temporary JSON rendering of the same lineage.
// Shared immutable strings are charged once per member; list slots and cell objects still count.
internal static class AggregateValueBudget
{
    internal static void Charge(AggregateValue value, AggregateBudget budget)
    {
        var strings = new HashSet<string>(ReferenceEqualityComparer.Instance);
        void Text(string? text) { if (text != null && strings.Add(text)) budget.Spend(bytes: 32L + 2L * text.Length); }
        void Visit(AggregateValue value)
        {
            budget.Spend(bytes: 128);
            Text(value.Text);
            if (value.Number is { } number) budget.Spend(bytes: 96 + number.Numerator.GetByteCount() + number.Denominator.GetByteCount());
            if (value.Choices != null) foreach (var choice in value.Choices) Text(choice);
            if (value.List is { } list)
                foreach (var row in list.Records)
                {
                    budget.Spend(bytes: 256 + row.Cells.Count * 64L);
                    Text(row.Key); Text(row.Origin.RecordId); Text(row.Origin.ListId);
                    foreach (var cell in row.Cells.Values) Visit(cell.Value);
                }
            if (value.Table is not { } table) return;
            foreach (var unit in table.Units) Text(unit);
            foreach (var column in table.Schema.Columns) Text(column);
            foreach (var row in table.Rows)
            {
                budget.Spend(bytes: 32L + 8L * row.Count);
                foreach (var cell in row)
                {
                    budget.Spend(bytes: 64); Visit(cell.Value);
                    foreach (var trace in cell.Trace)
                    { budget.Spend(bytes: 64); Text(trace.ReportId); Text(trace.UnitId); Text(trace.OccurrenceKey); Text(trace.MemberId); }
                }
            }
        }
        Visit(value);
    }
}
