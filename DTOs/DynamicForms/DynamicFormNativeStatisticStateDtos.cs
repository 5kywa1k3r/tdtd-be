using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.DynamicForms;

// Complete wire state, separate from the editable Canvas draft envelope.
// Disabled entries carry identity only; unfinished settings stay in the session.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeTableStatisticPlanDto(
    int? Version, IReadOnlyList<DynamicFormNativeTableStatisticStateDto>? Targets);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeTableStatisticStateDto
{
    public string? Id { get; init; }
    public bool? IsStatistic { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DynamicFormNativeStatisticSelectorDto? Selector { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Grouping { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DynamicFormNativeStatisticInputPolicyDto? Input { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DynamicFormNativeStatisticOrderDto? Order { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DynamicFormNativeStatisticOperationDto>? Operations { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? StatisticLabelCodes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowInDetail { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowInTree { get; init; }
}

// Atomic replacement of all native statistic metadata through the existing P8
// transaction/receipt. Table structure and v1 target addresses remain immutable.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeStatisticsPayload(
    int? Version, IReadOnlyList<DynamicFormNativeTableStatisticsPayload>? Tables);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeTableStatisticsPayload(
    string? TableId, IReadOnlyList<DynamicFormNativeStatisticTargetDto>? StatisticTargets,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DynamicFormNativeTableStatisticPlanDto? StatisticPlan);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativePlanConfigDto(
    int Version, DynamicFormNativeStatisticsPayload Definition,
    IReadOnlyList<DynamicFormNativePlanTargetConfigDto> Targets);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativePlanTargetConfigDto(
    DynamicFormNativeStatisticPlanTargetDto Configuration,
    IReadOnlyList<DynamicFormStatisticLabelSnapshotDto> LabelSnapshots,
    string StructureHash);
