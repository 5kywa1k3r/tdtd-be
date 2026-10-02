using System.Text.Json;
using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.DynamicForms;

// L5a preparation contract. Not mounted on the v1 definition/P8 endpoints.
// This describes enabled, complete targets only; authoring drafts stay separate.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeStatisticPlanDto(
    int? Version, IReadOnlyList<DynamicFormNativeStatisticPlanTargetDto>? Targets);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeStatisticPlanTargetDto(
    string? TableId, string? TargetId,
    DynamicFormNativeStatisticSelectorDto? Selector,
    string? Grouping, DynamicFormNativeStatisticInputPolicyDto? Input,
    DynamicFormNativeStatisticOrderDto? Order,
    IReadOnlyList<DynamicFormNativeStatisticOperationDto>? Operations,
    IReadOnlyList<string>? StatisticLabelCodes,
    bool? ShowInDetail, bool? ShowInTree);

// A matrix selection is the Cartesian product of the ordered, frozen ID lists.
// A repeating selection names fields and ALL records, never a report's record IDs.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeStatisticSelectorDto
{
    public string? Kind { get; init; }
    public IReadOnlyList<string>? FieldIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RowIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Records { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeStatisticInputPolicyDto
{
    public string? Mode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Types { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeStatisticOrderDto(
    string? Cells, string? Reports, string? Records);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicFormNativeStatisticOperationDto(
    string? OperationId, string? Method, JsonElement Options);
