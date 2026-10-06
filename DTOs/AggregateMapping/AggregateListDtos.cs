namespace tdtd_be.DTOs.AggregateMapping;

// LIST_PIPELINE/v1 is opt-in. Existing TABLE expressions retain their semantics.
public sealed record AggregateListPipelineDto
{
    public required int Version { get; init; }
    public AggregateListPredicateDto? Where { get; init; }
    public required List<AggregateListSortDto> Sort { get; init; }
    public required string Scope { get; init; }
    public required string Take { get; init; }
    public int? TopN { get; init; }
    public required List<AggregateListProjectionDto> Project { get; init; }
    public required string Operation { get; init; }
    public string? ValueFieldId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? WeightFieldId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Trim { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? CaseSensitive { get; init; }
}
public sealed record AggregateListMergeDto(int Version, string Mode, List<AggregateListMergeInputDto> Inputs);
public sealed record AggregateListMergeInputDto(string Ref, List<AggregateListProjectionDto> Project);
public sealed record AggregateListPredicateDto
{
    public required string Operator { get; init; }
    public string? FieldId { get; init; }
    public List<string>? Values { get; init; }
    public List<AggregateListPredicateDto>? Children { get; init; }
    public bool? Trim { get; init; }
    public bool? CaseSensitive { get; init; }
}
public sealed record AggregateListSortDto(string FieldId, string Direction, string Nulls,
    bool? Trim = null, bool? CaseSensitive = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ChoiceOrder = null);
public sealed record AggregateListProjectionDto(string FieldId, string OutputFieldId);
public sealed record AggregateListOptionsQueryDto(AggregatePeriodContextDto Context, AggregateFormPinDto Form,
    string ListId, string FieldId, string? Query = null, string? Cursor = null, int PageSize = 20);
public sealed record AggregateListPageRequestDto(string ReportId, string? JobId, string SnapshotId,
    string SnapshotHash, int Offset = 0, int Limit = 20, string? RowKey = null, string? FieldId = null, int OperationIndex = 0, string? ReadId = null, bool IncludeLineage = false, string? ViewId = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string TargetId => ViewId != null && string.IsNullOrEmpty(ReportId) ? ViewId : ViewId == null ? ReportId : throw new System.InvalidOperationException("AGG_CONTEXT_STALE");
}
