using tdtd_be.Data;
using tdtd_be.Data.Indexes;
using tdtd_be.Data.Infrastructure;

internal static class FixtureProvenanceChecks
{
    internal static async Task Migration(MongoDbContext db, string stage, CancellationToken ct)
    {
        await DynamicFormRuntimeProvenanceBackfill.RunAsync(db.Db, new MongoOptions(), ct);
        Console.WriteLine("PASS fixture provenance migration " + stage);
    }
}
