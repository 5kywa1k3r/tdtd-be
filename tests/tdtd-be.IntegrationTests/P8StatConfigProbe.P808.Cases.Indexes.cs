using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P808ReceiptOwnerCommandIndex =
        "ux_statConfigCommandReceipts_owner_command";
    private const string P808ReceiptOwnerCreatedIndex =
        "ix_statConfigCommandReceipts_owner_created";
    private const string P808JobDedupeIndex =
        "ux_statConfigValidationJobs_dedupe";
    private const string P808JobCorrelationIndex =
        "ux_statConfigValidationJobs_correlation";
    private const string P808JobClaimIndex =
        "ix_statConfigValidationJobs_claim";
    private const string P808JobOwnerHistoryIndex =
        "ix_statConfigValidationJobs_owner_history";
    private const string P808JobTtlIndex =
        "ix_statConfigValidationJobs_ttl";
    private const string P808OutboxDedupeIndex =
        "ux_statConfigAuditOutbox_dedupe";
    private const string P808OutboxDueIndex =
        "ix_statConfigAuditOutbox_due";
    private const string P808OutboxTargetIndex =
        "ix_statConfigAuditOutbox_target";
    private const string P808OutboxTtlIndex =
        "ix_statConfigAuditOutbox_ttl";

    private static readonly string[] P808RequiredOperationsIndexes =
    [
        P808ReceiptOwnerCommandIndex,
        P808ReceiptOwnerCreatedIndex,
        P808JobDedupeIndex,
        P808JobCorrelationIndex,
        P808JobClaimIndex,
        P808JobOwnerHistoryIndex,
        P808JobTtlIndex,
        P808OutboxDedupeIndex,
        P808OutboxDueIndex,
        P808OutboxTargetIndex,
        P808OutboxTtlIndex
    ];

    private async Task RunP808IndexCasesAsync(CancellationToken ct)
    {
        await RunP808ExactIndexInventoryCaseAsync(ct);
        await RunP808UniquePartialIndexCaseAsync(ct);
        await RunP808ClaimQueryIndexCaseAsync(ct);
        await RunP808FailClosedIndexStartupCaseAsync(ct);
    }

    private async Task RunP808ExactIndexInventoryCaseAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-011",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var response = await ReadP808IndexReadinessAsync(
                    Actor("system_admin"),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P8 operations index readiness");
                HarnessAssert.True(
                    response.Json?["ready"]?.GetValue<bool>() == true,
                    "P8 operations index readiness is not ready.");
                RequireP808LowerSha(
                    OptionalP808String(response.Json, "contractHash"),
                    "operations index contractHash");
                var apiIndexes = response.Json?["indexes"] as JsonArray;
                HarnessAssert.Equal(
                    P808RequiredOperationsIndexes.Length,
                    apiIndexes?.Count,
                    "P8 operations readiness API index count mismatch");

                var stringType = new BsonDocument("$type", "string");
                var receiptTyped = new BsonDocument
                {
                    { "ownerKind", stringType },
                    { "ownerId", stringType },
                    { "commandId", stringType }
                };
                var activeDedupe = new BsonDocument
                {
                    { "isDeleted", false },
                    { "dedupeKey", new BsonDocument("$type", "string") }
                };
                var activeCorrelation = new BsonDocument
                {
                    { "isDeleted", false },
                    { "correlationId", new BsonDocument("$type", "string") }
                };
                var activeOnly = new BsonDocument
                {
                    { "isDeleted", false },
                    { "isActive", true }
                };
                var notDeleted = new BsonDocument("isDeleted", false);

                var receipts = await ListP808IndexesAsync(
                    ReceiptsCollection,
                    ct);
                RequireP808Index(
                    receipts,
                    P808ReceiptOwnerCommandIndex,
                    new BsonDocument
                    {
                        { "ownerKind", 1 },
                        { "ownerId", 1 },
                        { "commandId", 1 }
                    },
                    true,
                    receiptTyped);
                RequireP808Index(
                    receipts,
                    P808ReceiptOwnerCreatedIndex,
                    new BsonDocument
                    {
                        { "ownerKind", 1 },
                        { "ownerId", 1 },
                        { "createdAtUtc", -1 }
                    },
                    false);

                var jobs = await ListP808IndexesAsync(
                    P808JobsCollection,
                    ct);
                RequireP808Index(
                    jobs,
                    P808JobDedupeIndex,
                    new BsonDocument("dedupeKey", 1),
                    true,
                    activeDedupe);
                RequireP808Index(
                    jobs,
                    P808JobCorrelationIndex,
                    new BsonDocument("correlationId", 1),
                    true,
                    activeCorrelation);
                RequireP808Index(
                    jobs,
                    P808JobClaimIndex,
                    new BsonDocument
                    {
                        { "queueName", 1 },
                        { "isActive", 1 },
                        { "status", 1 },
                        { "nextRetryAtUtc", 1 },
                        { "leaseUntilUtc", 1 },
                        { "createdAtUtc", 1 }
                    },
                    false,
                    activeOnly);
                RequireP808Index(
                    jobs,
                    P808JobOwnerHistoryIndex,
                    new BsonDocument
                    {
                        { "ownerKind", 1 },
                        { "ownerId", 1 },
                        { "configId", 1 },
                        { "versionNo", -1 },
                        { "createdAtUtc", -1 }
                    },
                    false,
                    notDeleted);
                RequireP808Index(
                    jobs,
                    P808JobTtlIndex,
                    new BsonDocument("expiresAtUtc", 1),
                    false,
                    expireAfterSeconds: 0);

                var outbox = await ListP808IndexesAsync(
                    P808AuditOutboxCollection,
                    ct);
                RequireP808Index(
                    outbox,
                    P808OutboxDedupeIndex,
                    new BsonDocument("dedupeKey", 1),
                    true,
                    activeDedupe);
                RequireP808Index(
                    outbox,
                    P808OutboxDueIndex,
                    new BsonDocument
                    {
                        { "status", 1 },
                        { "nextAttemptAtUtc", 1 },
                        { "leaseUntilUtc", 1 },
                        { "createdAtUtc", 1 }
                    },
                    false,
                    notDeleted);
                RequireP808Index(
                    outbox,
                    P808OutboxTargetIndex,
                    new BsonDocument
                    {
                        { "targetKind", 1 },
                        { "targetId", 1 },
                        { "createdAtUtc", -1 }
                    },
                    false,
                    notDeleted);
                RequireP808Index(
                    outbox,
                    P808OutboxTtlIndex,
                    new BsonDocument("expiresAtUtc", 1),
                    false,
                    expireAfterSeconds: 0);

                var relevantInventory = _p808StatisticsIndexInventory
                    .Where(row => row.Collection is
                        ReceiptsCollection or
                        P808JobsCollection or
                        P808AuditOutboxCollection)
                    .ToArray();
                HarnessAssert.True(
                    relevantInventory.Length == 3 &&
                    relevantInventory.All(row =>
                        row.DuplicateNames.Count == 0 &&
                        row.DuplicateKeys.Count == 0),
                    "P8 operations index inventory contains duplicate names or keys.");
                _p808IndexQueries.Add(new P808IndexQueryEvidence(
                    "P8-OPS-011",
                    "stat_config_command_receipts + stat_config_validation_jobs + stat_config_audit_outbox",
                    "startup exact-contract validation",
                    "READY",
                    P808RequiredOperationsIndexes,
                    Sha256(System.Text.Encoding.UTF8.GetBytes(
                        CanonicalResponse(response)))));

                return new CaseObservation(
                    "MongoIndexInitializer and the admin readiness API agreed on all eleven exact receipt/job/outbox index contracts with no duplicate owner.",
                    "startupOwner=MongoIndexInitializer;ready=1;exactIndexes=11;duplicateNames=0;duplicateKeys=0;writes=0");
            },
            ct);
    }

    private async Task RunP808UniquePartialIndexCaseAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-012",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                const string marker = "p808-index-unique-012";
                var jobs = _database.GetCollection<BsonDocument>(
                    P808JobsCollection);
                var receipts = _database.GetCollection<BsonDocument>(
                    ReceiptsCollection);
                var outbox = _database.GetCollection<BsonDocument>(
                    P808AuditOutboxCollection);
                var markerFilter = Builders<BsonDocument>.Filter.Eq(
                    "p808OwnedMarker",
                    marker);
                try
                {
                    var firstJob = P808UniqueIndexFixture(
                        $"{marker}-job-a",
                        marker,
                        $"{marker}-dedupe",
                        $"{marker}-correlation",
                        isDeleted: false);
                    await jobs.InsertOneAsync(
                        firstJob,
                        cancellationToken: ct);
                    await RequireP808DuplicateKeyAsync(
                        () => jobs.InsertOneAsync(
                            P808UniqueIndexFixture(
                                $"{marker}-job-b",
                                marker,
                                $"{marker}-dedupe",
                                $"{marker}-correlation-b",
                                isDeleted: false),
                            cancellationToken: ct),
                        "job dedupe unique index");
                    await RequireP808DuplicateKeyAsync(
                        () => jobs.InsertOneAsync(
                            P808UniqueIndexFixture(
                                $"{marker}-job-c",
                                marker,
                                $"{marker}-dedupe-c",
                                $"{marker}-correlation",
                                isDeleted: false),
                            cancellationToken: ct),
                        "job correlation unique index");
                    await jobs.InsertOneAsync(
                        P808UniqueIndexFixture(
                            $"{marker}-job-deleted",
                            marker,
                            $"{marker}-dedupe",
                            $"{marker}-correlation",
                            isDeleted: true),
                        cancellationToken: ct);

                    var receipt = new BsonDocument
                    {
                        { "_id", $"{marker}-receipt-a" },
                        { "p808OwnedMarker", marker },
                        { "ownerKind", "LABEL" },
                        { "ownerId", marker },
                        { "commandId", marker },
                        { "createdAtUtc", DateTime.UtcNow }
                    };
                    await receipts.InsertOneAsync(
                        receipt,
                        cancellationToken: ct);
                    var duplicateReceipt = new BsonDocument(receipt)
                    {
                        ["_id"] = $"{marker}-receipt-b"
                    };
                    await RequireP808DuplicateKeyAsync(
                        () => receipts.InsertOneAsync(
                            duplicateReceipt,
                            cancellationToken: ct),
                        "receipt owner/command unique index");

                    var outboxItem = new BsonDocument
                    {
                        { "_id", $"{marker}-outbox-a" },
                        { "p808OwnedMarker", marker },
                        { "dedupeKey", $"{marker}-outbox-dedupe" },
                        { "isDeleted", false },
                        { "createdAtUtc", DateTime.UtcNow }
                    };
                    await outbox.InsertOneAsync(
                        outboxItem,
                        cancellationToken: ct);
                    var duplicateOutbox = new BsonDocument(outboxItem)
                    {
                        ["_id"] = $"{marker}-outbox-b"
                    };
                    await RequireP808DuplicateKeyAsync(
                        () => outbox.InsertOneAsync(
                            duplicateOutbox,
                            cancellationToken: ct),
                        "outbox dedupe unique index");
                }
                finally
                {
                    await jobs.DeleteManyAsync(markerFilter, ct);
                    await receipts.DeleteManyAsync(markerFilter, ct);
                    await outbox.DeleteManyAsync(markerFilter, ct);
                }

                HarnessAssert.Equal(
                    0L,
                    await jobs.CountDocumentsAsync(
                        markerFilter,
                        cancellationToken: ct),
                    "P8 index fixture job cleanup failed");
                HarnessAssert.Equal(
                    0L,
                    await receipts.CountDocumentsAsync(
                        markerFilter,
                        cancellationToken: ct),
                    "P8 index fixture receipt cleanup failed");
                HarnessAssert.Equal(
                    0L,
                    await outbox.CountDocumentsAsync(
                        markerFilter,
                        cancellationToken: ct),
                    "P8 index fixture outbox cleanup failed");
                _p808IndexQueries.Add(new P808IndexQueryEvidence(
                    "P8-OPS-012",
                    "stat_config_command_receipts + stat_config_validation_jobs + stat_config_audit_outbox",
                    "duplicate insert probes under typed/not-deleted partial filters",
                    "DUPLICATE_KEY",
                    [
                        P808ReceiptOwnerCommandIndex,
                        P808JobDedupeIndex,
                        P808JobCorrelationIndex,
                        P808OutboxDedupeIndex
                    ],
                    Sha256(System.Text.Encoding.UTF8.GetBytes(marker))));

                return new CaseObservation(
                    "Direct Mongo duplicate probes proved receipt owner-command, active job dedupe/correlation and active outbox dedupe uniqueness while soft-deleted job rows stayed outside the partial constraint.",
                    "duplicateRejects=4;softDeletedOutsidePartial=1;ownedFixtureCleanup=1;finalDelta=0");
            },
            ct);
    }

    private async Task RunP808ClaimQueryIndexCaseAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-013",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var now = DateTime.UtcNow;
                var filter = new BsonDocument
                {
                    { "queueName", P808QueueName },
                    { "isDeleted", false },
                    { "isActive", true },
                    {
                        "status",
                        new BsonDocument(
                            "$in",
                            new BsonArray
                            {
                                "PENDING",
                                "RESET",
                                "RETRY_WAITING"
                            })
                    },
                    {
                        "$and",
                        new BsonArray
                        {
                            new BsonDocument(
                                "$or",
                                new BsonArray
                                {
                                    new BsonDocument(
                                        "nextRetryAtUtc",
                                        BsonNull.Value),
                                    new BsonDocument(
                                        "nextRetryAtUtc",
                                        new BsonDocument("$lte", now))
                                }),
                            new BsonDocument(
                                "$or",
                                new BsonArray
                                {
                                    new BsonDocument(
                                        "leaseUntilUtc",
                                        BsonNull.Value),
                                    new BsonDocument(
                                        "leaseUntilUtc",
                                        new BsonDocument("$lte", now))
                                })
                        }
                    }
                };
                var sort = new BsonDocument("createdAtUtc", 1);
                var explain = await ExplainP808FindAsync(
                    P808JobsCollection,
                    filter,
                    sort,
                    ct);
                var winningPlan = explain
                    .GetValue("queryPlanner", new BsonDocument())
                    .AsBsonDocument
                    .GetValue("winningPlan", new BsonDocument())
                    .ToJson();
                HarnessAssert.True(
                    winningPlan.Contains(
                        P808JobClaimIndex,
                        StringComparison.Ordinal),
                    $"P8 readiness atomic-claim query did not use {P808JobClaimIndex}. Plan={winningPlan}");
                _p808IndexQueries.Add(new P808IndexQueryEvidence(
                    "P8-OPS-013",
                    P808JobsCollection,
                    $"filter={filter.ToJson()};sort={sort.ToJson()}",
                    winningPlan,
                    [P808JobClaimIndex],
                    Sha256(explain.ToBson())));

                return new CaseObservation(
                    "Mongo queryPlanner selected the dedicated partial compound claim index for the queue/status/retry/expired-lease/created ordering shape.",
                    $"winningIndex={P808JobClaimIndex};collection={P808JobsCollection};writes=0");
            },
            ct);
    }

    private async Task RunP808FailClosedIndexStartupCaseAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-014",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var evidence =
                    await RunP808ConflictingIndexStartupProbeAsync(
                        "P8-OPS-014",
                        ReceiptsCollection,
                        P808ReceiptOwnerCommandIndex,
                        new BsonDocument
                        {
                            { "ownerKind", 1 },
                            { "ownerId", 1 },
                            { "commandId", 1 }
                        },
                        desiredUnique: true,
                        ct);
                await EvidenceJson.WriteAsync(
                    Path.Combine(
                        _paths.RunRoot,
                        "p8-ops-014-index-conflict.json"),
                    evidence,
                    ct);
                _p808IndexQueries.Add(new P808IndexQueryEvidence(
                    "P8-OPS-014",
                    ReceiptsCollection,
                    "fresh owned database with a same-key wrong-unique index",
                    "STARTUP_FAILED_CLOSED_NO_MUTATION",
                    [P808ReceiptOwnerCommandIndex],
                    evidence.AfterIndexSetSha256));

                return new CaseObservation(
                    "A run-owned same-key/wrong-option receipt index made the second real Kestrel startup fail closed; startup neither dropped, rebuilt nor renamed the conflict, and exact owned-database cleanup was verified.",
                    "sameKeyConflict=1;startupFailed=1;indexSetMutation=0;unsafeDropOrRebuild=0;ownedDatabaseCleanup=1");
            },
            ct);
    }

    private static BsonDocument P808UniqueIndexFixture(
        string id,
        string marker,
        string dedupeKey,
        string correlationId,
        bool isDeleted)
        => new()
        {
            { "_id", id },
            { "p808OwnedMarker", marker },
            { "queueName", P808QueueName },
            { "dedupeKey", dedupeKey },
            { "correlationId", correlationId },
            { "isDeleted", isDeleted },
            { "isActive", false },
            { "status", "COMPLETED" },
            { "createdAtUtc", DateTime.UtcNow }
        };

    private static async Task RequireP808DuplicateKeyAsync(
        Func<Task> mutation,
        string context)
    {
        try
        {
            await mutation();
        }
        catch (MongoWriteException error)
            when (error.WriteError?.Category ==
                  ServerErrorCategory.DuplicateKey)
        {
            return;
        }

        throw new InvalidOperationException(
            $"P8-08 {context} accepted a forbidden duplicate.");
    }
}
