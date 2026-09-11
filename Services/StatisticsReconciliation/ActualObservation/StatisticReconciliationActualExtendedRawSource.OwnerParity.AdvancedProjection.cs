using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class
    StatisticReconciliationActualExtendedRawSourceOwnerParity
{
    private static bool TryAdvancedProjectionValue(
        JsonElement payload,
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection
            projection,
        out JsonElement value)
    {
        value = default;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("fieldValues", out var fieldValues) ||
            fieldValues.ValueKind != JsonValueKind.Object)
            return false;
        var values = fieldValues.TryGetProperty(
                "values", out var nested) &&
            nested.ValueKind == JsonValueKind.Object
                ? nested
                : fieldValues;
        if (values.TryGetProperty(projection.FieldId, out value))
            return true;
        return projection.FieldKey != projection.FieldId &&
            values.TryGetProperty(projection.FieldKey, out value);
    }

    private static IEnumerable<string> AdvancedJoinDisplayValues(
        JsonElement value,
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection
            projection)
    {
        if (projection.RawFieldType is "stringList" or "richText")
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var text = AdvancedJoinNullableString(item);
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return AdvancedJoinOptionLabel(
                            text.Trim(), projection);
                }
                yield break;
            }

            var single = AdvancedJoinNullableString(value);
            if (!string.IsNullOrWhiteSpace(single))
                yield return AdvancedJoinOptionLabel(
                    single.Trim(), projection);
            yield break;
        }

        var scalar = AdvancedJoinNullableString(value);
        if (!string.IsNullOrWhiteSpace(scalar))
            yield return scalar;
    }

    private static string AdvancedJoinOptionLabel(
        string code,
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection
            projection)
    {
        foreach (var option in projection.Options)
        {
            if (option.Code == code)
                return option.Label;
        }
        return code;
    }

    private static string? AdvancedJoinNullableString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetDecimal(out var number)
                ? number.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
}