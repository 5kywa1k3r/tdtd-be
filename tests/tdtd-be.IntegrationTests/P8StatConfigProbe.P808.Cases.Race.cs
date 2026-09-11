using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private string? _p808RaceJobId;

    private async Task RunP808ConcurrentDuplicateEnqueueCaseAsync(
        CancellationToken ct)
    {
        const string commandId = "p808-concurrent-enqueue-019";
        await RunEvidenceCaseAsync(
            "P8-OPS-019",
            "system_admin",
            [commandId],
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
                var owner = RequireP808Owner("race");
                var release = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var contenders = Enumerable.Range(0, 2)
                    .Select(async _ =>
                    {
                        await release.Task.WaitAsync(ct);
                        return await EnqueueP808JobAsync(
                            Actor("system_admin"),
                            owner.OwnerKind,
                            owner.OwnerId,
                            P808EnqueueEnvelope(commandId, owner),
                            ct);
                    })
                    .ToArray();
                release.SetResult(true);
                var responses = await Task.WhenAll(contenders);
                foreach (var response in responses)
                {
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.OK,
                        "P8 concurrent duplicate readiness enqueue");
                }

                var identities = responses
                    .Select(response => ParseP808JobIdentity(
                        response.Json,
                        "P8 concurrent readiness response"))
                    .ToArray();
                HarnessAssert.Equal(
                    identities[0].JobId,
                    identities[1].JobId,
                    "Concurrent P8 enqueue returned different jobs");
                HarnessAssert.Equal(
                    CanonicalResponse(responses[0]),
                    CanonicalResponse(responses[1]),
                    "Concurrent P8 enqueue replay response drifted");
                HarnessAssert.Equal(
                    1L,
                    await CountP808JobsAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "enqueueCommandId",
                            commandId),
                        ct),
                    "Concurrent P8 enqueue persisted multiple jobs");
                HarnessAssert.Equal(
                    1L,
                    await _database
                        .GetCollection<BsonDocument>(ReceiptsCollection)
                        .CountDocumentsAsync(
                            Builders<BsonDocument>.Filter.Eq(
                                "commandId",
                                commandId),
                            cancellationToken: ct),
                    "Concurrent P8 enqueue persisted multiple receipts");
                HarnessAssert.Equal(
                    1L,
                    await CountP808OutboxAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "commandId",
                            commandId),
                        ct),
                    "Concurrent P8 enqueue persisted multiple outbox intents");
                var links = await RequireP808AtomicLinksAsync(
                    identities[0].JobId,
                    owner,
                    commandId,
                    ct);
                HarnessAssert.Equal(
                    "PENDING",
                    links.InternalStatus,
                    "Concurrent enqueue winner was not pending");
                _p808RaceJobId = identities[0].JobId;
                var direct = await RequireP808JobAsync(
                    identities[0].JobId,
                    ct);
                direct["externalStatus"] = "QUEUED";
                RecordP808QueueTrace(
                    "P8-OPS-019",
                    identities[0].JobId,
                    direct);

                return new CaseObservation(
                    "Two barrier-released real Kestrel enqueue contenders converged on one canonical response and one atomically linked pending job/receipt/outbox winner.",
                    "contenders=2;winnerJob=1;responsesEqual=1;job=1;receipt=1;outbox=1;status=QUEUED;resultDelta=0");
            },
            ct);
    }
}
