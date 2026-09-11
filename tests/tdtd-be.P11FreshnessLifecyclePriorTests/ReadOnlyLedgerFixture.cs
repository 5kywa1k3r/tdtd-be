using System.Reflection;
using System.Runtime.CompilerServices;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

// No MongoClient is constructed. The collection is deliberately unavailable:
// admission invalid-input/append checks must finish before accessing it.
internal sealed class ReadOnlyLedgerFixture
{
    public List<string> Operations { get; } = [];
    public MongoDbContext Context { get; }

    public ReadOnlyLedgerFixture()
    {
        var database = StrictMongoProxy.Create<IMongoDatabase>((method, args) =>
        {
            if (method.Name != "GetCollection" ||
                args?[0] as string != StatisticReconciliationActualLifecycleMongoLedger.CollectionName)
                throw new InvalidOperationException("UNEXPECTED_DATABASE_OPERATION:" + method.Name);
            return null;
        });
        Context = (MongoDbContext)RuntimeHelpers.GetUninitializedObject(typeof(MongoDbContext));
        typeof(MongoDbContext).GetField("<Db>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Context, database);
    }

}

internal sealed class NoReadActualBackend : tdtd_be.Services.StatisticsReconciliation.ActualObservation.IStatisticReconciliationActualObservationBackend
{
    public Task<IReadOnlyList<tdtd_be.Models.StatisticsReconciliation.StatisticReconciliationObservation>> ReadGenerationAsync(
        string reconciliationId, string generationId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("UNEXPECTED_ACTUAL_READ");
    public Task AppendContentAsync(IReadOnlyList<tdtd_be.Models.StatisticsReconciliation.StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken) => throw new InvalidOperationException("UNEXPECTED_ACTUAL_APPEND");
    public Task AppendCommitAsync(tdtd_be.Models.StatisticsReconciliation.StatisticReconciliationObservation observation,
        CancellationToken cancellationToken) => throw new InvalidOperationException("UNEXPECTED_ACTUAL_COMMIT");
}

internal class StrictMongoProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> handler = null!;
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, StrictMongoProxy>();
        ((StrictMongoProxy)(object)proxy).handler = handler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handler(targetMethod!, args);
}
