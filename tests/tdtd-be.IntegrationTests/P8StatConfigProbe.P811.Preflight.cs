using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task DrainP811PreexistingActiveJobsAsync(
        CancellationToken ct)
    {
        const int maxPasses = 8;
        const int maxJobsPerPass = 100;
        var actor = Actor("system_admin");
        var exchangeStart = _api.Exchanges.Count;
        var before = await ReadP811PreflightActiveJobsAsync(ct);
        var attempts = new List<P811PreflightWorkerAttempt>();
        var current = before;
        var stalled = false;

        // Always cross the real Kestrel worker endpoint, including when a
        // predecessor no longer leaves an active job. This keeps the
        // preflight API-bound and makes the zero-work state explicit.
        for (var pass = 1; pass <= maxPasses; pass++)
        {
            var countBefore = current.Count;
            var response = await ProcessP808JobsAsync(
                actor,
                maxJobsPerPass,
                ct);
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.OK,
                $"P8-11 inherited readiness drain pass {pass}");

            var claimed = ApiHarnessClient.FindIntRecursive(
                response.Json,
                "claimed") ?? 0;
            var completed = ApiHarnessClient.FindIntRecursive(
                response.Json,
                "completed") ?? 0;
            var failed = ApiHarnessClient.FindIntRecursive(
                response.Json,
                "failed") ?? 0;
            current = await ReadP811PreflightActiveJobsAsync(ct);
            attempts.Add(new P811PreflightWorkerAttempt(
                pass,
                countBefore,
                current.Count,
                claimed,
                completed,
                failed,
                (int)response.StatusCode));

            if (current.Count == 0)
                break;
            if (claimed == 0)
            {
                stalled = true;
                break;
            }
        }

        var apiExchangeSequences = _api.Exchanges
            .Skip(exchangeStart)
            .Select(exchange => exchange.Sequence)
            .ToArray();
        var inheritedP809PendingBefore = before.Count(job =>
            string.Equals(
                job.EnqueueCommandId,
                "p809-readiness-queued",
                StringComparison.Ordinal) &&
            string.Equals(job.Status, "PENDING",
                StringComparison.Ordinal));
        var passed = !stalled && current.Count == 0;

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot,
                "p8-race-preflight-active-job-drain.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = _promptId,
                contract = "P8-11-PREFLIGHT-ACTIVE-JOB-DRAIN-1",
                outsideOwnedCaseDeltas = true,
                realKestrelWorkerApi = true,
                directMongoBeforeAndAfter = true,
                queue = P808QueueName,
                activeStatuses = new[] { "PENDING", "RUNNING" },
                inheritedP809PendingBefore,
                before,
                attempts,
                after = current,
                apiExchangeSequences,
                stalled,
                passed
            },
            ct);

        HarnessAssert.True(
            !stalled,
            "P8-11 preflight worker made no progress while inherited " +
            "PENDING/RUNNING readiness jobs remained active.");
        HarnessAssert.Equal(
            0,
            current.Count,
            "P8-11 preflight left active PENDING/RUNNING readiness jobs " +
            "before the owned race cases.");
    }

    private async Task<IReadOnlyList<P811PreflightActiveJob>>
        ReadP811PreflightActiveJobsAsync(CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq(
                         "queueName", P808QueueName) &
                     Builders<BsonDocument>.Filter.Eq(
                         "isDeleted", false) &
                     Builders<BsonDocument>.Filter.Eq(
                         "isActive", true) &
                     Builders<BsonDocument>.Filter.In(
                         "status", new[] { "PENDING", "RUNNING" });
        var documents = await _database
            .GetCollection<BsonDocument>(P808JobsCollection)
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        return documents.Select(document => new P811PreflightActiveJob(
                P811PreflightValue(document, "_id"),
                P811PreflightValue(document, "status"),
                P811PreflightValue(document, "enqueueCommandId"),
                P811PreflightValue(document, "ownerKind"),
                P811PreflightValue(document, "ownerId")))
            .ToArray();
    }

    private static string? P811PreflightValue(
        BsonDocument document,
        string name)
        => document.TryGetValue(name, out var value) &&
           !value.IsBsonNull
            ? value.ToString()
            : null;
}

internal sealed record P811PreflightActiveJob(
    string? JobId,
    string? Status,
    string? EnqueueCommandId,
    string? OwnerKind,
    string? OwnerId);

internal sealed record P811PreflightWorkerAttempt(
    int Pass,
    int ActiveBefore,
    int ActiveAfter,
    int Claimed,
    int Completed,
    int Failed,
    int HttpStatus);
