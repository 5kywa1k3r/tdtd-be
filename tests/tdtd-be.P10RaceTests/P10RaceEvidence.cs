using System.Text.Json.Serialization;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

internal sealed record P10RaceEvidence(
    string Identity,
    string Config,
    string Expected,
    string Actual,
    string Delta,
    string Freshness,
    string Permission,
    string Verdict)
{
    [JsonIgnore]
    internal IReadOnlyList<KeyValuePair<string, string>> Columns =>
    [
        new("Identity", Identity),
        new("Config", Config),
        new("Expected", Expected),
        new("Actual", Actual),
        new("Delta", Delta),
        new("Freshness", Freshness),
        new("Permission", Permission),
        new("Verdict", Verdict)
    ];

    internal static P10RaceEvidence From(
        StatisticReconciliationEightColumnRecord value)
        => new(value.Identity, value.Config, value.Expected, value.Actual,
            value.Delta, value.Freshness, value.Permission, value.Verdict);
}
