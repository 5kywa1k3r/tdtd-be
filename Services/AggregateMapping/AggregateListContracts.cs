using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal sealed record AggregateListField(string Id, string Type)
{
    // Dynamic option availability is fenced separately; it is not an immutable structural field identity.
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string>? AllowedChoiceCodes { get; init; }
}
internal sealed record AggregateListSchema(IReadOnlyList<AggregateListField> Fields);
internal sealed record AggregateListOrigin(AggregateSourcePinDto? Pin, string UnitId,
    string OccurrenceKey, string ListId, string RecordId, string SourceSlot)
{
    public AggregateFormPinDto? Form { get; init; }
    public string? SourceNodeId { get; init; }
    public string? SourcePortId { get; init; }
}
internal sealed record AggregateListRow(string Key, AggregateListOrigin Origin,
    IReadOnlyDictionary<string, AggregateCell> Cells)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AggregateListContributor>? Contributors { get; init; }
}
internal sealed record AggregateListContributor(string InputId, AggregateListOrigin Origin);
internal sealed record AggregateListCellSource(AggregateListOrigin Origin, string? FieldId);
internal sealed record AggregateListValue(AggregateListSchema Schema, IReadOnlyList<AggregateListRow> Records);
internal sealed record AggregateListReference(string Kind, string Id, string Hash, int Count, AggregateListSchema Schema);
internal sealed record AggregateListReportCount(string? ReportId, string SourceSlot, int Read, int Matched, int Selected);
internal sealed record AggregateListPipelineTrace(string Scope, int Read, int Matched, int Selected,
    int Omitted, string Operation, string? ExactTotal, int BlankCount)
{
    public AggregateListReference? Selection { get; init; }
    public IReadOnlyList<AggregateListReportCount> Reports { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AggregateListMergeInputCount>? MergeInputs { get; init; }
}
internal sealed record AggregateListMergeInputCount(string InputId, int Rows, int MissingRows);
internal interface IAggregateListSink
{
    Task<AggregateListReference> CaptureAsync(AggregateReadContext context, AggregateListValue value, CancellationToken ct);
    IAsyncEnumerable<AggregateListRow> ReadRowsAsync(AggregateReadContext context, AggregateListReference reference, CancellationToken ct);
}
internal interface IAggregateListTargetValidator
{
    Task ValidateListResultAsync(AggregateReadContext context, string memberId, AggregateListValue value, CancellationToken ct);
}
