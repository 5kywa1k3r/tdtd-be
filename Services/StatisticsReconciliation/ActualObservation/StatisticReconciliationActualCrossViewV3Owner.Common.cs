using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualCrossViewV3OwnerCommon
{
    internal static readonly JsonSerializerOptions WebJson = new(
        JsonSerializerDefaults.Web)
    {
        MaxDepth = 128,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string CanonicalRow<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, WebJson);
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json,
            "CROSS_VIEW_OWNER_ROW_JSON");
        return StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
    }

    internal static string SerializeRows<T>(IReadOnlyList<T> rows)
        => JsonSerializer.Serialize(rows, WebJson);

    internal static StatisticReconciliationActualCrossViewApiBaseProjection
        ApiBase(
            string workId,
            string scopeId,
            string templateId,
            string period,
            string authorizationSha,
            string surface,
            string ownerResultId,
            string generationId,
            string generationSha,
            string canonicalFilter,
            string filterSha,
            IEnumerable<(string Identity, string CanonicalJson)> rows,
            ImmutableArray<StatisticReconciliationActualApiTotalValue> totals)
    {
        var materialized = rows.Select((row, index) =>
                new StatisticReconciliationActualCrossViewApiBaseRow(
                    index,
                    row.Identity,
                    row.CanonicalJson))
            .ToImmutableArray();
        return new(
            StatisticReconciliationActualCrossViewParityV2Schemas.Base,
            workId,
            scopeId,
            templateId,
            period,
            authorizationSha,
            surface,
            ownerResultId,
            generationId,
            generationSha,
            canonicalFilter,
            filterSha,
            materialized,
            totals);
    }

    internal static ImmutableArray<StatisticReconciliationActualApiTotalValue>
        Totals(params StatisticReconciliationActualApiTotalValue[] totals)
        => totals.OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToImmutableArray();

    internal static StatisticReconciliationActualApiTotalValue IntegerTotal(
        string name,
        long value)
        => new(
            name,
            StatisticReconciliationActualExportValueTypes.Integer,
            StatisticReconciliationActualCanonical.Integer(value));

    internal static StatisticReconciliationActualApiTotalValue DecimalTotal(
        string name,
        decimal value)
        => new(
            name,
            StatisticReconciliationActualExportValueTypes.Decimal,
            StatisticReconciliationActualCanonical.Number(value));

    internal static (string GenerationId, string GenerationSha256)
        Generation(StatisticReconciliationActualApiCapture capture)
    {
        if (capture.Pages.IsDefaultOrEmpty)
            throw Invalid("CROSS_VIEW_OWNER_API_PAGES_REQUIRED");
        var ids = capture.Pages.Select(value => value.GenerationId)
            .Distinct(StringComparer.Ordinal).ToArray();
        var hashes = capture.Pages.Select(value => value.GenerationSha256)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length != 1 || hashes.Length != 1 || ids[0] is null ||
            hashes[0] is null)
            throw Invalid("CROSS_VIEW_OWNER_API_GENERATION_UNSTABLE");
        return (
            StatisticReconciliationActualCanonical.Required(
                ids[0],
                "CROSS_VIEW_OWNER_API_GENERATION_ID"),
            StatisticReconciliationActualCanonical.Sha256(
                hashes[0],
                "CROSS_VIEW_OWNER_API_GENERATION_SHA"));
    }

    internal static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}

internal sealed class StatisticReconciliationActualCrossViewV3Filter
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    internal StatisticReconciliationActualCrossViewV3Filter(string json)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json,
            "CROSS_VIEW_OWNER_FILTER_JSON");
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            StatisticReconciliationActualJson.Canonicalize(
                document.RootElement) != json)
            throw StatisticReconciliationActualCrossViewV3OwnerCommon.Invalid(
                "CROSS_VIEW_OWNER_FILTER_NOT_CANONICAL");
        _values = document.RootElement.EnumerateObject().ToDictionary(
            value => value.Name,
            value => value.Value.Clone(),
            StringComparer.Ordinal);
    }

    internal string? String(string name)
    {
        if (!_values.TryGetValue(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw StatisticReconciliationActualCrossViewV3OwnerCommon.Invalid(
                "CROSS_VIEW_OWNER_FILTER_VALUE_INVALID");
        return value.GetString();
    }

    internal void RequireExact(params string[] names)
    {
        if (!_values.Keys.OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(names.OrderBy(value => value,
                StringComparer.Ordinal), StringComparer.Ordinal))
            throw StatisticReconciliationActualCrossViewV3OwnerCommon.Invalid(
                "CROSS_VIEW_OWNER_FILTER_PROPERTIES_INVALID");
    }
}
