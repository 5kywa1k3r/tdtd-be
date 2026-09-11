using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<(ApiHarnessResponse Response,
            IReadOnlyList<P808JobChangeEvent> Events)>
        ObserveP808JobChangesAsync(
            string jobId,
            Func<Task<ApiHarnessResponse>> action,
            CancellationToken ct)
    {
        var collection = _database
            .GetCollection<BsonDocument>(P808JobsCollection);
        using var cursor = await collection.WatchAsync(
            options: new ChangeStreamOptions
            {
                FullDocument = ChangeStreamFullDocumentOption.UpdateLookup,
                MaxAwaitTime = TimeSpan.FromMilliseconds(250)
            },
            cancellationToken: ct);
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var events = new List<P808JobChangeEvent>();
        var actionTask = action();

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var moved = await cursor.MoveNextAsync(deadline.Token);
                if (!moved)
                    continue;
                foreach (var change in cursor.Current)
                {
                    var changedId = change.DocumentKey is null
                        ? null
                        : BsonString(change.DocumentKey, "_id");
                    if (!string.Equals(
                            changedId,
                            jobId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var updatedStatus = change.UpdateDescription?
                        .UpdatedFields.TryGetValue(
                            "status",
                            out var statusValue) == true
                            ? statusValue.ToString()
                            : change.FullDocument is null
                                ? null
                                : BsonString(change.FullDocument, "status");
                    events.Add(new P808JobChangeEvent(
                        change.OperationType.ToString(),
                        updatedStatus,
                        change.ClusterTime?.ToString(),
                        change.UpdateDescription?.UpdatedFields.ToJson(),
                        change.FullDocument is null
                            ? null
                            : Sha256(change.FullDocument.ToBson()),
                        Sha256(change.ResumeToken.ToBson())));
                }

                if (actionTask.IsCompleted &&
                    events.Any(item =>
                        item.Status is "COMPLETED" or "RETRY_WAITING" or
                        "FAILED" or "CANCELLED"))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            // The assertions below report the missing transition precisely.
        }

        return (await actionTask, events);
    }
}

internal sealed record P808JobChangeEvent(
    string OperationType,
    string? Status,
    string? ClusterTime,
    string? UpdatedFieldsJson,
    string? FullDocumentSha256,
    string ResumeTokenSha256);

