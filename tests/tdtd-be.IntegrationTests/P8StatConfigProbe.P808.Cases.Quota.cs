using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP808ActiveJobQuotaCaseAsync(
        CancellationToken ct)
    {
        const string winnerCommand = "p808-quota-active-winner-018";
        const string loserCommand = "p808-quota-rejected-loser-018";
        const string cancelCommand = "p808-quota-winner-cancel-018";
        await RunEvidenceCaseAsync(
            "P8-OPS-018",
            "system_admin",
            [winnerCommand, loserCommand, cancelCommand],
            [
                P808JobsCollection,
                ReceiptsCollection,
                P808AuditOutboxCollection
            ],
            [
                P808JobsCollection,
                ReceiptsCollection,
                P808AuditOutboxCollection
            ],
            async () =>
            {
                var actor = Actor("system_admin");
                var ownerA = RequireP808Owner("quota-a");
                var ownerB = RequireP808Owner("quota-b");
                var winnerResponse = await EnqueueP808JobAsync(
                    actor,
                    ownerA.OwnerKind,
                    ownerA.OwnerId,
                    P808EnqueueEnvelope(winnerCommand, ownerA),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    winnerResponse,
                    HttpStatusCode.OK,
                    "P8 readiness active-quota winner enqueue");
                var winner = ParseP808JobIdentity(
                    winnerResponse.Json,
                    "P8 readiness active-quota winner");
                HarnessAssert.Equal(
                    "QUEUED",
                    winner.ExternalStatus,
                    "P8 readiness quota winner is not active QUEUED");

                var loserBefore = await CaptureDatabaseSnapshotAsync(ct);
                var tokenBefore = await _database
                    .GetCollection<BsonDocument>(TokenLedgersCollection)
                    .CountDocumentsAsync(
                        FilterDefinition<BsonDocument>.Empty,
                        cancellationToken: ct);
                var loserResponse = await EnqueueP808JobAsync(
                    actor,
                    ownerB.OwnerKind,
                    ownerB.OwnerId,
                    P808EnqueueEnvelope(loserCommand, ownerB),
                    ct);
                ExpectFailure(
                    loserResponse,
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_READINESS_QUOTA_EXCEEDED");
                var loserAfter = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-OPS-018/quota-loser",
                    BuildDeltas(loserBefore, loserAfter),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                HarnessAssert.Equal(
                    0L,
                    await CountP808JobsAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "enqueueCommandId",
                            loserCommand),
                        ct),
                    "Quota loser persisted a readiness job");
                HarnessAssert.Equal(
                    0L,
                    await _database
                        .GetCollection<BsonDocument>(ReceiptsCollection)
                        .CountDocumentsAsync(
                            Builders<BsonDocument>.Filter.Eq(
                                "commandId",
                                loserCommand),
                            cancellationToken: ct),
                    "Quota loser persisted a command receipt");
                HarnessAssert.Equal(
                    0L,
                    await CountP808OutboxAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "commandId",
                            loserCommand),
                        ct),
                    "Quota loser persisted an audit-outbox item");
                HarnessAssert.Equal(
                    tokenBefore,
                    await _database
                        .GetCollection<BsonDocument>(TokenLedgersCollection)
                        .CountDocumentsAsync(
                            FilterDefinition<BsonDocument>.Empty,
                            cancellationToken: ct),
                    "Readiness quota rejection changed Advanced token ledger");

                var winnerDirect = await RequireP808JobAsync(
                    winner.JobId,
                    ct);
                var stateRevision = BsonLong(
                    winnerDirect,
                    "stateRevision") ?? throw new InvalidOperationException(
                    "P8 quota winner lacks stateRevision.");
                var stateHash = BsonString(winnerDirect, "stateHash")
                                ?? throw new InvalidOperationException(
                                    "P8 quota winner lacks stateHash.");
                var cancelResponse = await CancelP808JobAsync(
                    actor,
                    winner.JobId,
                    P808StateEnvelope(
                        cancelCommand,
                        stateRevision,
                        stateHash,
                        new JsonObject
                        {
                            ["reason"] =
                                "P8-OPS-018 release active quota fixture"
                        }),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    cancelResponse,
                    HttpStatusCode.OK,
                    "P8 readiness quota fixture cancel");
                var cancelled = ParseP808JobIdentity(
                    cancelResponse.Json,
                    "P8 readiness quota cancel response");
                HarnessAssert.Equal(
                    "CANCELLED",
                    cancelled.ExternalStatus,
                    "P8 quota fixture was not cancelled");
                var cancelledDirect = await RequireP808JobAsync(
                    winner.JobId,
                    ct);
                HarnessAssert.Equal(
                    false,
                    BsonBool(cancelledDirect, "isActive"),
                    "Cancelled quota fixture remained active");
                winnerDirect["externalStatus"] = "QUEUED";
                cancelledDirect["externalStatus"] = "CANCELLED";
                RecordP808QueueTrace(
                    "P8-OPS-018",
                    winner.JobId,
                    winnerDirect,
                    cancelledDirect);

                return new CaseObservation(
                    "With Testing active-job quota frozen at one, a second owner for the same actor was rejected inside the transaction before any loser job/receipt/outbox/token/result write; the owned winner was then cancelled through CAS.",
                    "maxActivePerActor=1;winner=QUEUED;loser=409:STAT_CONFIG_READINESS_QUOTA_EXCEEDED;loserJob=0;loserReceipt=0;loserOutbox=0;tokenDelta=0;resultDelta=0;winner=CANCELLED");
            },
            ct);
    }
}
