using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// Explicit TEXT-set -> two-column native list table. Legacy CONCAT remains TEXT.
internal static class AggregateContentTable
{
    internal static AggregateChannel Build(AggregateChannel source, AggregateFunctionOptionsDto options, AggregateBudget budget)
    {
        if (source.Type != "TEXT" || source.Shape != "SET") throw new AggregatePreviewException("AGG_CONTENT_TABLE_SOURCE");
        if (options.Order is not ("UNIT_THEN_PERIOD" or "PERIOD_THEN_UNIT")) throw new AggregatePreviewException("AGG_CONTENT_TABLE_ORDER");
        var notes = new List<AggregateContentRowNote>(); var rows = new List<IReadOnlyList<AggregateCell>>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in source.Items.OrderBy(i => options.Order == "UNIT_THEN_PERIOD" ? Note(i).UnitId : Note(i).OccurrenceKey, StringComparer.Ordinal)
            .ThenBy(i => options.Order == "UNIT_THEN_PERIOD" ? Note(i).OccurrenceKey : Note(i).UnitId, StringComparer.Ordinal)
            .ThenBy(i => Note(i).SourceKey, StringComparer.Ordinal))
        {
            budget.Spend(); var note = Note(item);
            if (item.Value.Type != "TEXT" || item.Value.State is not ("VALUE" or "BLANK")) throw new AggregatePreviewException("AGG_SOURCE_VALUE_INVALID");
            var occurrence = occurrences.GetValueOrDefault(note.SourceKey); occurrences[note.SourceKey] = occurrence + 1;
            // Stable source identity excludes payload revision/text/display order. Repeated
            // contributions retain their multiplicity, even if values and sources match.
            note = note with { RowKey = AggregateDigest.Of(new { note.SourceKey, occurrence }) };
            notes.Add(note);
            rows.Add([new(new("TEXT", "VALUE", Text: note.UnitName), item.Trace), new(item.Value, item.Trace)]);
        }
        var schema = new AggregateTableSchema("REPORT_TEXT_TABLE_V1", "vertical", ["unit", "content"],
            notes.Select(n => n.RowKey).ToArray(), [["TEXT", "TEXT"]]);
        var table = new AggregateTable(schema, rows, notes.Select(n => n.UnitId).ToArray()) { ContentRows = notes };
        return AggregateEvaluator.Single(new("TABLE", "VALUE", Table: table), source.Items.SelectMany(i => i.Trace).ToArray(), true);
    }
    private static AggregateContentRowNote Note(AggregateObservation item)
        => item.SourceNote ?? throw new AggregatePreviewException("AGG_CONTENT_TABLE_SOURCE_NOTE_REQUIRED");
}
