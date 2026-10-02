using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

// This policy belongs to the List profile; legacy scalar/Table semantics stay unchanged.
internal static class AggregateListChoicePolicy
{
    internal static AggregateValue Normalize(AggregateValue value)
        => value.State == "VALUE" && value.Type == "CHOICE_MANY" && value.Choices is { Count: 0 }
            ? AggregateValue.Blank(value.Type) : value;

    internal static IReadOnlyList<AggregateEditorListSpecialOptionDto> SpecialOptions(string fieldId, string type, bool required)
        => !required && type is "CHOICE_ONE" or "CHOICE_MANY"
            ? [new("BLANK", "Để trống", new() { Operator = "IS_BLANK", FieldId = fieldId })] : [];
}
