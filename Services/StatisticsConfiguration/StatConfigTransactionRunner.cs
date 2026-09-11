using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;

namespace tdtd_be.Services.StatisticsConfiguration;

public interface IStatConfigTransactionRunner
{
    Task<TResult> ExecuteAsync<TResult>(
        Func<IClientSessionHandle, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default);
}

/// <summary>
/// P8 owner, receipt and later audit/outbox writes are one majority transaction.
/// There is intentionally no standalone Mongo fallback.
/// </summary>
public sealed class StatConfigTransactionRunner : IStatConfigTransactionRunner
{
    private const int MaxAttempts = 5;
    private static readonly TransactionOptions Options = new(
        readConcern: ReadConcern.Snapshot,
        readPreference: ReadPreference.Primary,
        writeConcern: WriteConcern.WMajority);

    private readonly MongoDbContext _ctx;
    private readonly ILogger<StatConfigTransactionRunner> _logger;

    public StatConfigTransactionRunner(
        MongoDbContext ctx,
        ILogger<StatConfigTransactionRunner> logger)
    {
        _ctx = ctx;
        _logger = logger;
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<IClientSessionHandle, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        StatConfigCatalogActivation.EnsureMutationEnabled();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var session = await StartSessionAsync(ct);
            try
            {
                session.StartTransaction(Options);
                var result = await operation(session, ct);
                await CommitWithRetryAsync(session, ct);
                return result;
            }
            catch (Exception ex)
            {
                var commitUnknown = IsUnknownCommit(ex);
                if (!commitUnknown && session.IsInTransaction)
                {
                    try
                    {
                        await session.AbortTransactionAsync(
                            CancellationToken.None);
                    }
                    catch (Exception abortError)
                    {
                        _logger.LogWarning(
                            abortError,
                            "P8 stat-config transaction abort failed.");
                    }
                }

                if (IsUnsupported(ex))
                {
                    throw new AppException(
                        AppErrorCode.STAT_CONFIG_TRANSACTION_UNSUPPORTED,
                        new { reason = "STAT_CONFIG_TRANSACTION_UNSUPPORTED" },
                        innerException: ex);
                }

                if (!commitUnknown &&
                    attempt < MaxAttempts &&
                    IsTransient(ex) &&
                    !ct.IsCancellationRequested)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(
                            100 * (1 << (attempt - 1))),
                        ct);
                    continue;
                }

                throw;
            }
        }

        throw new InvalidOperationException(
            "STAT_CONFIG_TRANSACTION_RETRY_EXHAUSTED");
    }

    private async Task<IClientSessionHandle> StartSessionAsync(
        CancellationToken ct)
    {
        try
        {
            return await _ctx.Db.Client.StartSessionAsync(
                cancellationToken: ct);
        }
        catch (Exception ex) when (IsUnsupported(ex))
        {
            throw new AppException(
                AppErrorCode.STAT_CONFIG_TRANSACTION_UNSUPPORTED,
                new { reason = "STAT_CONFIG_TRANSACTION_UNSUPPORTED" },
                innerException: ex);
        }
    }

    private static async Task CommitWithRetryAsync(
        IClientSessionHandle session,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await session.CommitTransactionAsync(ct);
                return;
            }
            catch (Exception ex) when (
                attempt < MaxAttempts &&
                IsUnknownCommit(ex) &&
                !ct.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(50 * attempt),
                    ct);
            }
        }
    }

    internal static bool IsTransient(Exception exception)
        => exception is MongoException mongo &&
               mongo.HasErrorLabel("TransientTransactionError") ||
           exception is MongoCommandException command &&
               command.Code is 112 or 244 or 251 ||
           exception.InnerException is not null &&
               IsTransient(exception.InnerException);

    internal static bool IsUnknownCommit(Exception exception)
        => exception is MongoException mongo &&
               mongo.HasErrorLabel("UnknownTransactionCommitResult") ||
           exception.InnerException is not null &&
               IsUnknownCommit(exception.InnerException);

    internal static bool IsUnsupported(Exception exception)
    {
        var message = exception.Message;
        var matched =
            message.Contains(
                "transaction numbers are only allowed",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "transactions are not supported",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "does not support sessions",
                StringComparison.OrdinalIgnoreCase);
        return matched ||
               exception.InnerException is not null &&
               IsUnsupported(exception.InnerException);
    }
}
