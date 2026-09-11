using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task<P808StartupConflictEvidence>
        RunP808ConflictingIndexStartupProbeAsync(
            string caseId,
            string collectionName,
            string desiredIndexName,
            BsonDocument desiredKey,
            bool desiredUnique,
            CancellationToken ct)
    {
        var alternateDatabaseName =
            $"{_mongo.DatabaseName}_p808_index_conflict";
        HarnessAssert.True(
            alternateDatabaseName.StartsWith("tdtd_p1_", StringComparison.Ordinal) &&
            alternateDatabaseName.All(character =>
                char.IsLetterOrDigit(character) || character == '_'),
            "P8-08 index-conflict database name is not run-owned.");

        var alternateDatabase = _mongo.Client.GetDatabase(alternateDatabaseName);
        var collection = alternateDatabase
            .GetCollection<BsonDocument>(collectionName);
        var conflictingIndexName =
            $"p808_conflict_{desiredIndexName}";
        var conflictingUnique = !desiredUnique;
        var conflictModel = new CreateIndexModel<BsonDocument>(
            new BsonDocumentIndexKeysDefinition<BsonDocument>(desiredKey),
            new CreateIndexOptions
            {
                Name = conflictingIndexName,
                Unique = conflictingUnique
            });
        await collection.Indexes.CreateOneAsync(
            conflictModel,
            cancellationToken: ct);
        var before = await ListIndexesAsync(collection, ct);
        var beforeHash = HashIndexes(before);

        var childRoot = Path.Combine(
            _iterationRoot,
            "p808-index-conflict-startup");
        Directory.CreateDirectory(childRoot);
        BackendServerLease? unexpectedServer = null;
        string? startupFailure = null;
        try
        {
            unexpectedServer = await BackendServerLease.StartAsync(
                _paths,
                childRoot,
                $"{Path.GetFileName(_paths.RunRoot)}_index_conflict",
                _mongo,
                ct,
                new BackendServerOptions
                {
                    MongoConnectionStringOverride = _mongo.ConnectionString,
                    MongoDatabaseNameOverride = alternateDatabaseName
                });
        }
        catch (Exception error)
        {
            startupFailure = $"{error.GetType().Name}: {error.Message}";
        }
        finally
        {
            if (unexpectedServer is not null)
            {
                await unexpectedServer.StopAsync();
                await unexpectedServer.DisposeAsync();
            }
        }

        var after = await ListIndexesAsync(collection, ct);
        var afterHash = HashIndexes(after);
        var conflictStillPresent = after.Any(index =>
            string.Equals(
                BsonString(index, "name"),
                conflictingIndexName,
                StringComparison.Ordinal) &&
            index.GetValue("unique", false).ToBoolean() ==
            conflictingUnique &&
            index.GetValue("key", new BsonDocument())
                .AsBsonDocument.Equals(desiredKey));
        var desiredCreated = after.Any(index =>
            string.Equals(
                BsonString(index, "name"),
                desiredIndexName,
                StringComparison.Ordinal));

        HarnessAssert.True(
            startupFailure is not null,
            "P8-08 conflicting same-key index unexpectedly allowed startup.");
        HarnessAssert.True(
            conflictStillPresent,
            "P8-08 startup dropped or rewrote the conflicting index.");
        HarnessAssert.True(
            !desiredCreated,
            "P8-08 startup created the desired index beside a same-key conflict.");
        HarnessAssert.Equal(
            beforeHash,
            afterHash,
            "P8-08 startup mutated the conflicting collection index set");

        await _mongo.Client.DropDatabaseAsync(alternateDatabaseName, ct);
        var remaining = await _mongo.Client.ListDatabaseNames()
            .ToListAsync(ct);
        var cleanupVerified = !remaining.Contains(
            alternateDatabaseName,
            StringComparer.Ordinal);
        HarnessAssert.True(
            cleanupVerified,
            "P8-08 run-owned index-conflict database cleanup failed.");

        return new P808StartupConflictEvidence(
            ChainId,
            _promptId,
            Path.GetFileName(_paths.RunRoot),
            caseId,
            alternateDatabaseName,
            collectionName,
            desiredIndexName,
            desiredKey.ToJson(),
            desiredUnique,
            conflictingIndexName,
            conflictingUnique,
            startupFailure,
            beforeHash,
            afterHash,
            conflictStillPresent,
            desiredCreated,
            cleanupVerified);
    }

    private static async Task<IReadOnlyList<BsonDocument>> ListIndexesAsync(
        IMongoCollection<BsonDocument> collection,
        CancellationToken ct)
    {
        using var cursor = await collection.Indexes.ListAsync(ct);
        return await cursor.ToListAsync(ct);
    }

    private static string HashIndexes(
        IEnumerable<BsonDocument> indexes)
    {
        var bytes = indexes
            .OrderBy(index => BsonString(index, "name"), StringComparer.Ordinal)
            .SelectMany(index =>
            {
                var bson = index.ToBson();
                return BitConverter.GetBytes(bson.Length).Concat(bson);
            })
            .ToArray();
        return Sha256(bytes);
    }
}

internal sealed record P808StartupConflictEvidence(
    string ChainId,
    string PromptId,
    string RunKey,
    string CaseId,
    string OwnedDatabase,
    string Collection,
    string DesiredIndexName,
    string DesiredKey,
    bool DesiredUnique,
    string ConflictingIndexName,
    bool ConflictingUnique,
    string StartupFailure,
    string BeforeIndexSetSha256,
    string AfterIndexSetSha256,
    bool ConflictStillPresent,
    bool DesiredIndexCreated,
    bool CleanupVerified);

