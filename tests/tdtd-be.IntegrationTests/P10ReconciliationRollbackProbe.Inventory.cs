using System.Net;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRollbackProbe
{
    // Frozen P8/P9 zero-write registry (53) plus the four P10-owned stores.
    // This sequence is a versioned closeout contract shared with the verifier;
    // changes require an explicit producer/consumer review.
    internal static readonly string[] InventoryCollections =
    [
        "assignment_list_doc_roles",
        "doc_roles",
        "docrole_read_model_projection_retry_jobs",
        "dynamic_flow_execution_epochs",
        "dynamic_flow_gateway_contributions",
        "dynamic_flow_gateway_instances",
        "dynamic_flow_instances",
        "dynamic_flow_mapping_apply_receipts",
        "dynamic_flow_mapping_events",
        "dynamic_flow_mapping_outbox",
        "dynamic_flow_mapping_provenance",
        "dynamic_flow_participant_snapshots",
        "dynamic_flow_periodic_occurrences",
        "dynamic_flow_periodic_schedules",
        "dynamic_flow_runtime_command_receipts",
        "dynamic_flow_runtime_events",
        "dynamic_flow_runtime_outbox",
        "dynamic_flow_step_instances",
        "my_report_period_list_doc_roles",
        "my_report_template_list_doc_roles",
        "notifications",
        "review_assignment_summary_doc_roles",
        "review_report_list_doc_roles",
        "stat_config_audit_outbox",
        "stat_config_command_receipts",
        "stat_config_validation_jobs",
        "user_action_log_retry_jobs",
        "user_action_logs",
        "work_assignment_advanced_summary_day_nodes",
        "work_assignment_advanced_summary_month_nodes",
        "work_assignment_advanced_summary_year_nodes",
        "work_assignment_aggregate_configs",
        "work_assignment_basic_summary_snapshots",
        "work_assignment_materialize_jobs",
        "work_assignment_queue",
        "work_assignment_report",
        "work_assignment_report_logs",
        "work_assignment_report_sections",
        "work_list_doc_roles",
        "work_report_field_stat_aggregates",
        "work_report_field_stat_values",
        "work_report_label_stat_aggregates",
        "work_report_label_stat_values",
        "work_report_payloads",
        "work_report_periods",
        "work_report_statistic_diff_exports",
        "work_report_statistic_diff_results",
        "work_report_statistic_exports",
        "work_report_statistic_rebuild_jobs",
        "work_report_table_stat_aggregates",
        "work_report_table_stat_values",
        "work_report_table_values",
        "work_status_operation_logs",
        "work_report_statistic_reconciliation_exports",
        "work_report_statistic_reconciliation_observations",
        "work_report_statistic_reconciliation_reviews",
        "work_report_statistic_reconciliations"
    ];

    private async Task AwaitInventoryInfrastructureAsync(CancellationToken ct)
    {
        // List/read once and require two equal snapshots. This absorbs only
        // run-owned lazy collection/index setup before route boundaries.
        P10RollbackInventorySnapshot? previous = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var current = await CaptureInventoryAsync(ct);
            if (previous?.SemanticSha256 == current.SemanticSha256)
                return;
            previous = current;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
        throw new InvalidOperationException(
            "P10_ROLLBACK_INVENTORY_DID_NOT_STABILIZE");
    }

    private async Task<P10RollbackInventorySnapshot> CaptureInventoryAsync(
        CancellationToken ct)
    {
        HarnessAssert.Equal(57, InventoryCollections.Length,
            "P10-12 frozen zero-write collection count");
        HarnessAssert.Equal(57,
            InventoryCollections.Distinct(StringComparer.Ordinal).Count(),
            "P10-12 frozen zero-write collection uniqueness");

        var existing = (await (await Database().ListCollectionNamesAsync(
                cancellationToken: ct)).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var rows = new List<P10RollbackCollectionState>(
            InventoryCollections.Length);
        foreach (var collection in InventoryCollections)
        {
            if (!existing.Contains(collection))
            {
                rows.Add(new(
                    collection,
                    false,
                    0,
                    HashBytes(Array.Empty<byte>())));
                continue;
            }

            var documents = await Database()
                .GetCollection<BsonDocument>(collection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            foreach (var document in documents)
            {
                var bytes = document.ToBson();
                hash.AppendData(BitConverter.GetBytes(
                    IPAddress.HostToNetworkOrder(bytes.Length)));
                hash.AppendData(bytes);
            }
            rows.Add(new(
                collection,
                true,
                documents.Count,
                Convert.ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant()));
        }

        var canonical = string.Join(
            "\n",
            rows.Select(value =>
                $"{value.Collection}|{value.Exists}|{value.Count}|" +
                value.DocumentSetSha256));
        return new(
            rows,
            HashBytes(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void RequireSameInventory(
        string context,
        P10RollbackInventorySnapshot before,
        P10RollbackInventorySnapshot after)
    {
        HarnessAssert.Equal(InventoryCollections.Length,
            before.Collections.Count,
            context + " before inventory count");
        HarnessAssert.Equal(InventoryCollections.Length,
            after.Collections.Count,
            context + " after inventory count");
        var left = before.Collections.ToDictionary(
            value => value.Collection,
            StringComparer.Ordinal);
        var right = after.Collections.ToDictionary(
            value => value.Collection,
            StringComparer.Ordinal);
        foreach (var collection in InventoryCollections)
        {
            HarnessAssert.True(left.TryGetValue(collection, out var beforeRow),
                context + " before omitted " + collection);
            HarnessAssert.True(right.TryGetValue(collection, out var afterRow),
                context + " after omitted " + collection);
            HarnessAssert.Equal(beforeRow!.Exists, afterRow!.Exists,
                context + " collection existence changed: " + collection);
            HarnessAssert.Equal(beforeRow.Count, afterRow.Count,
                context + " collection count changed: " + collection);
            HarnessAssert.Equal(beforeRow.DocumentSetSha256,
                afterRow.DocumentSetSha256,
                context + " document bytes changed: " + collection);
        }
        HarnessAssert.Equal(before.SemanticSha256, after.SemanticSha256,
            context + " exact 57-store inventory hash");
    }

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed record P10RollbackCollectionState(
    string Collection,
    bool Exists,
    long Count,
    string DocumentSetSha256);

internal sealed record P10RollbackInventorySnapshot(
    IReadOnlyList<P10RollbackCollectionState> Collections,
    string SemanticSha256);
