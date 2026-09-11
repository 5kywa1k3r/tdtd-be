using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed record DirectMongoControlEvidence(
    int DocumentCount,
    string ManifestSha256,
    int CollectionCount,
    bool CleanupVerified);

internal static class DirectMongoControl
{
    private const string DefaultConnection =
        "mongodb://127.0.0.1:27017/?directConnection=true";

    internal static async Task<DirectMongoControlEvidence> AppendValidateAndCleanAsync(
        StatisticReconciliationExpectedCompiledGeneration generation)
    {
        var connection = Environment.GetEnvironmentVariable(
            "P10_T16_MONGO_CONNECTION") ?? DefaultConnection;
        var databaseName =
            $"tdtd_p10_t16_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        var client = new MongoClient(connection);
        var cleanupVerified = false;
        try
        {
            var options = new MongoOptions
            {
                ConnectionString = connection,
                Database = databaseName
            };
            var context = new MongoDbContext(Options.Create(options));
            var beforeCollections = await context.Db
                .ListCollectionNames()
                .ToListAsync();
            Require(beforeCollections.Count == 0, "isolated database was not empty");

            var backend = new StatisticReconciliationExpectedObservationMongoBackend(
                context);
            var store = new StatisticReconciliationExpectedObservationStore(
                backend,
                new StatisticReconciliationExpectedMetricIdentityCompiler());
            var result = await store.AppendGenerationAsync(
                generation,
                new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));
            var documents = await context.StatisticReconciliationObservations
                .Find(item =>
                    item.ReconciliationId == generation.ContextPin.ReconciliationId &&
                    item.GenerationId == generation.GenerationId)
                .ToListAsync();
            var commit = StatisticReconciliationExpectedObservationIntegrity
                .ValidateGeneration(documents);
            Require(documents.Count == result.DocumentCount,
                "direct Mongo document count mismatch");
            Require(commit.Commit is not null &&
                    StringComparer.Ordinal.Equals(
                        commit.Commit.ManifestSha256,
                        result.ManifestSha256),
                "direct Mongo manifest mismatch");
            var collections = await context.Db
                .ListCollectionNames()
                .ToListAsync();
            Require(collections.Count == 1 &&
                    StringComparer.Ordinal.Equals(
                        collections[0],
                        options.StatisticReconciliationObservationCollection),
                "T16 wrote outside the observation owner collection");

            return new DirectMongoControlEvidence(
                documents.Count,
                result.ManifestSha256,
                collections.Count,
                CleanupVerified: true);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
            var databases = await client.ListDatabaseNames().ToListAsync();
            cleanupVerified = !databases.Contains(databaseName, StringComparer.Ordinal);
            await client.DropDatabaseAsync(databaseName);
            if (!cleanupVerified)
                throw new InvalidOperationException("T16 isolated Mongo cleanup failed.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
