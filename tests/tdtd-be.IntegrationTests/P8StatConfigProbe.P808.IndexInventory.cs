using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] P808StatisticsIndexCollections =
    [
        "labels",
        "dynamic_form_templates",
        "stat_config_command_receipts",
        "work_assignment_basic_summary_configs",
        "work_assignment_advanced_summary_configs",
        "work_summary_token_ledgers",
        "dynamic_flow_templates",
        "dynamic_flow_template_versions",
        "dynamic_flow_definition_command_receipts",
        "work_report_statistic_diff_configs",
        P808JobsCollection,
        P808AuditOutboxCollection,
        "work_report_statistic_rebuild_jobs",
        "work_report_field_stat_values",
        "work_report_field_stat_aggregates",
        "work_report_table_stat_values",
        "work_report_table_stat_aggregates",
        "work_report_label_stat_values",
        "work_report_label_stat_aggregates"
    ];

    private IReadOnlyList<P808CollectionIndexInventory>
        _p808StatisticsIndexInventory =
            Array.Empty<P808CollectionIndexInventory>();

    private async Task CaptureP808StatisticsIndexInventoryAsync(
        CancellationToken ct)
    {
        var rows = new List<P808CollectionIndexInventory>();
        foreach (var collectionName in P808StatisticsIndexCollections)
        {
            var indexes = await ListP808IndexesAsync(collectionName, ct);
            var normalized = indexes
                .OrderBy(index => BsonString(index, "name"), StringComparer.Ordinal)
                .Select(index => new P808IndexInventoryRow(
                    BsonString(index, "name") ?? "<missing>",
                    index.GetValue("key", new BsonDocument()).ToJson(),
                    index.GetValue("unique", false).ToBoolean(),
                    index.TryGetValue("partialFilterExpression", out var partial)
                        ? partial.ToJson()
                        : null,
                    index.TryGetValue("expireAfterSeconds", out var ttl)
                        ? ttl.ToInt64()
                        : null,
                    Sha256(index.ToBson())))
                .ToArray();
            var duplicateNames = normalized
                .GroupBy(index => index.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            var duplicateKeys = normalized
                .Where(index => index.Name != "_id_")
                .GroupBy(index => index.Key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => new P808DuplicateIndexKey(
                    group.Key,
                    group.Select(index => index.Name)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray()))
                .ToArray();
            rows.Add(new P808CollectionIndexInventory(
                collectionName,
                normalized,
                duplicateNames,
                duplicateKeys));
        }

        _p808StatisticsIndexInventory = rows;
        await EvidenceJson.WriteAsync(
            Path.Combine(
                _paths.RunRoot,
                "p8-ops-statistics-index-inventory.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                startupOwner = "MongoIndexInitializer",
                collections = rows
            },
            ct);
    }
}

internal sealed record P808CollectionIndexInventory(
    string Collection,
    IReadOnlyList<P808IndexInventoryRow> Indexes,
    IReadOnlyList<string> DuplicateNames,
    IReadOnlyList<P808DuplicateIndexKey> DuplicateKeys);

internal sealed record P808IndexInventoryRow(
    string Name,
    string Key,
    bool Unique,
    string? PartialFilter,
    long? ExpireAfterSeconds,
    string DocumentSha256);

internal sealed record P808DuplicateIndexKey(
    string Key,
    IReadOnlyList<string> Names);

