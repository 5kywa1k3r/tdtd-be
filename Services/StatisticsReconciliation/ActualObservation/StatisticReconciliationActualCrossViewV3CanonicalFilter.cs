using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Strict reader for server-pinned canonical filter preimages. API and export
/// filters are always instantiated separately; callers cannot reuse one view's
/// parsed values as the other view's proof.
/// </summary>
internal sealed class StatisticReconciliationActualCrossViewV3CanonicalFilter
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    internal StatisticReconciliationActualCrossViewV3CanonicalFilter(
        string json)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json,
            "CROSS_VIEW_OWNER_FILTER_JSON");
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !string.Equals(
                StatisticReconciliationActualJson.Canonicalize(
                    document.RootElement),
                json,
                StringComparison.Ordinal))
            throw Invalid("CROSS_VIEW_OWNER_FILTER_NOT_CANONICAL");
        var properties = document.RootElement.EnumerateObject().ToArray();
        if (properties.Select(item => item.Name)
                .Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw Invalid("CROSS_VIEW_OWNER_FILTER_DUPLICATE_PROPERTY");
        _values = properties.ToDictionary(
            item => item.Name,
            item => item.Value.Clone(),
            StringComparer.Ordinal);
    }

    internal string? String(string name)
    {
        if (!_values.TryGetValue(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid("CROSS_VIEW_OWNER_FILTER_STRING_INVALID");
        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > 1024 ||
            !string.Equals(result, result.Trim(), StringComparison.Ordinal) ||
            result.Any(char.IsControl))
            throw Invalid("CROSS_VIEW_OWNER_FILTER_STRING_INVALID");
        return result;
    }

    internal bool? Boolean(string name)
    {
        if (!_values.TryGetValue(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid("CROSS_VIEW_OWNER_FILTER_BOOLEAN_INVALID")
        };
    }

    internal int? Int32(string name)
    {
        if (!_values.TryGetValue(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
            throw Invalid("CROSS_VIEW_OWNER_FILTER_INTEGER_INVALID");
        return result;
    }

    internal void RequireAllowed(params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        if (_values.Keys.Any(name => !allowed.Contains(name)))
            throw Invalid("CROSS_VIEW_OWNER_FILTER_PROPERTIES_INVALID");
    }

    internal void RequireExact(params string[] names)
    {
        if (!_values.Keys.OrderBy(item => item, StringComparer.Ordinal)
            .SequenceEqual(
                names.OrderBy(item => item, StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw Invalid("CROSS_VIEW_OWNER_FILTER_PROPERTIES_INVALID");
    }

    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}
