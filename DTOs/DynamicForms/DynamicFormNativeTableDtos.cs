using System.Text.Json;
using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.DynamicForms;

public sealed record DynamicFormNativeDefinitionCapabilities(
    string ContractId, int NativeTablesVersion, bool SupportsCreateDraft,
    bool SupportsUpdateDraft, bool SupportsPublish, bool SupportsStatistics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeStatisticPlanVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeListDefinitionVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SupportsLists = null);

// Native definition grammar. P8 owns statistic metadata; report values use a separate contract.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeTableDto
{
    public string? Id { get; init; }
    public string? SectionId { get; init; }
    public string? Name { get; init; }
    public int? Order { get; init; }
    public string? Layout { get; init; }
    public List<DynamicFormNativeColumnDto>? Fields { get; init; }
    public List<DynamicFormNativeRowDto>? Rows { get; init; }
    public DynamicFormNativeTypeConfigDto? TypeConfig { get; init; }
    public List<DynamicFormNativeStatisticTargetDto>? StatisticTargets { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DynamicFormNativeOverviewSelectionDto>? OverviewSelections { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DynamicFormNativeOverviewPlanSelectionDto>? OverviewPlanSelections { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DynamicFormNativeTableStatisticPlanDto? StatisticPlan { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DynamicFormNativeListPresentationDto? Presentation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DynamicFormNativeListItemConstraintsDto? ItemConstraints { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeListPresentationDto
{
    public string? Kind { get; init; }
    public string? ItemLabel { get; init; }
    public string? AddLabel { get; init; }
    public List<string>? SummaryFieldIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeListItemConstraintsDto
{
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeColumnDto
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public int? Order { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeRowDto
{
    public string? Id { get; init; }
    public string? Name { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeTargetDto
{
    public string? Scope { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FieldId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RowId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeTypeConfigDto
{
    public int? Version { get; init; }
    public long? Sequence { get; init; }
    public List<DynamicFormNativeTypeRuleDto>? Rules { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeTypeRuleDto
{
    public DynamicFormNativeTargetDto? Target { get; init; }
    public DynamicFormNativeCellSpecDto? Spec { get; init; }
    public long? Order { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeCellSpecDto
{
    public string? Type { get; init; }
    public bool? Required { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxLength { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MinLength { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Minimum { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Maximum { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IntegerOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinDate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaxDate { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinSelected { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxSelected { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DynamicFormNativeOptionDto>? Options { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DynamicFormNativeValueSourceDto? ValueSource { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeValueSourceDto
{
    public string? SourceType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogName { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeOptionDto
{
    public string? Code { get; init; }
    public string? Label { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeStatisticTargetDto
{
    public string? Id { get; init; }
    public DynamicFormNativeTargetDto? Target { get; init; }
    public bool? IsStatistic { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowOnOverview { get; init; }
    // Missing legacy config remains missing. Readback never supplies defaults.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Statistic { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? StatisticLabelCodes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeOverviewSelectionDto
{
    public DynamicFormNativeTargetDto? Target { get; init; }
    public bool? ShowOnOverview { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeOverviewPlanSelectionDto
{
    public string? TargetId { get; init; }
    public bool? ShowOnOverview { get; init; }
}
