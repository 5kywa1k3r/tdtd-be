global using static CrossViewTestSupport;

using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class CrossViewTestSupport
{
    internal static string Canonical<T>(T value)
        => StatisticReconciliationActualJson.Canonicalize(
            JsonSerializer.SerializeToElement(value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    internal static string RawSha(string value)
        => StatisticReconciliationActualJson.RawSha256(value);

    internal static string H(string seed)
        => HValues("P10_XVIEW_TEST_V1", seed);

    internal static string HValues(string domain, params string?[] values)
        => StatisticReconciliationActualCanonical.Hash(domain, values);

    internal static string HS(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);

    internal static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);

    internal static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    internal static void Equal<T>(T expected, T actual, string reason)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{reason}:expected={expected}:actual={actual}");
    }

    internal static StatisticReconciliationActualExportCellObservation Cell(
        StatisticReconciliationActualExportColumnContract column,
        string state,
        string canonical,
        int scale,
        bool neutralized)
        => new(
            column.Ordinal,
            column.Name,
            column.ValueType,
            state,
            canonical,
            scale,
            neutralized,
            HValues("P10_ACTUAL_EXPORT_CELL_V1",
                I(column.Ordinal), column.Name, column.ValueType, state,
                canonical, I(scale), neutralized ? "true" : "false"));
}
