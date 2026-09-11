using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

/// <summary>
/// Readiness gate for the MongoDB session transaction required by every P4
/// Dynamic Flow definition mutation. It performs a transactional read, because
/// creating a client session alone does not prove replica-set transaction support.
/// </summary>
public sealed class DynamicFlowDefinitionTransactionHealthCheck : IHealthCheck
{
    private readonly MongoDbContext _ctx;

    public DynamicFlowDefinitionTransactionHealthCheck(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        IClientSessionHandle? session = null;
        try
        {
            session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: cancellationToken);
            session.StartTransaction(new TransactionOptions(
                readConcern: ReadConcern.Snapshot,
                readPreference: ReadPreference.Primary,
                writeConcern: WriteConcern.WMajority));
            _ = await _ctx.DynamicFlowDefinitionCommandReceipts
                .Find(session, Builders<DynamicFlowDefinitionCommandReceipt>.Filter.Empty)
                .Limit(1)
                .AnyAsync(cancellationToken);
            await session.AbortTransactionAsync(cancellationToken);
            return HealthCheckResult.Healthy("Dynamic Flow definition transactions are supported.");
        }
        catch (Exception error) when (error is MongoException or InvalidOperationException)
        {
            if (session?.IsInTransaction == true)
            {
                try
                {
                    await session.AbortTransactionAsync(CancellationToken.None);
                }
                catch
                {
                    // Preserve the original capability failure.
                }
            }
            return HealthCheckResult.Unhealthy(
                DynamicFlowDefinitionTransactionRunner.UnsupportedReason,
                error,
                new Dictionary<string, object>
                {
                    ["errorCode"] = DynamicFlowDefinitionTransactionRunner.UnsupportedReason,
                    ["requiredTopology"] = "REPLICA_SET_OR_MONGOS"
                });
        }
        finally
        {
            session?.Dispose();
        }
    }
}
