using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowDefinitionTransactionRunner
{
    Task ExecuteAsync(
        Func<IClientSessionHandle, CancellationToken, Task> operation,
        CancellationToken ct = default);

    Task<TResult> ExecuteAsync<TResult>(
        Func<IClientSessionHandle, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default);
}

/// <summary>
/// Executes one Dynamic Flow definition aggregate mutation in one MongoDB
/// transaction. There is intentionally no non-transactional fallback: a
/// standalone or otherwise unsupported MongoDB topology is a configuration
/// failure for P4 mutations.
/// </summary>
public sealed class DynamicFlowDefinitionTransactionRunner : IDynamicFlowDefinitionTransactionRunner
{
    public const string UnsupportedReason = "DYNAMIC_FLOW_TRANSACTION_UNSUPPORTED";
    private const int MaxTransientAttempts = 5;
    private const int TransientRetryBaseDelayMilliseconds = 100;

    private static readonly TransactionOptions DefinitionTransactionOptions = new(
        readConcern: ReadConcern.Snapshot,
        readPreference: ReadPreference.Primary,
        writeConcern: WriteConcern.WMajority);

    private static readonly string[] UnsupportedMessageMarkers =
    {
        "transaction numbers are only allowed on a replica set member or mongos",
        "transactions are not supported",
        "do not support transactions",
        "transaction support is not available",
        "sessions are not supported",
        "does not support sessions"
    };

    private readonly MongoDbContext _ctx;
    private readonly ILogger<DynamicFlowDefinitionTransactionRunner> _logger;

    public DynamicFlowDefinitionTransactionRunner(
        MongoDbContext ctx,
        ILogger<DynamicFlowDefinitionTransactionRunner> logger)
    {
        _ctx = ctx;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        Func<IClientSessionHandle, CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ExecuteAsync(
            async (session, transactionCt) =>
            {
                await operation(session, transactionCt);
                return true;
            },
            ct);
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<IClientSessionHandle, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            IClientSessionHandle? session = null;
            try
            {
                session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: ct);
                session.StartTransaction(DefinitionTransactionOptions);

                var result = await operation(session, ct);
                await CommitWithRetryAsync(session, ct);
                return result;
            }
            catch (Exception ex)
            {
                var commitOutcomeUnknown =
                    IsUnknownTransactionCommitResult(ex);
                if (!commitOutcomeUnknown && session?.IsInTransaction == true)
                    await AbortQuietlyAsync(session);

                if (IsUnsupportedTransactionFailure(ex))
                {
                    _logger.LogError(
                        ex,
                        "MongoDB topology does not support the transaction required by Dynamic Flow definition mutations.");

                    throw new AppException(
                        AppErrorCode.DYNAMIC_FLOW_TRANSACTION_UNSUPPORTED,
                        new { reason = UnsupportedReason },
                        innerException: ex);
                }

                if (!commitOutcomeUnknown &&
                    attempt < MaxTransientAttempts &&
                    IsTransientTransactionFailure(ex) &&
                    !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        ex,
                        "Retrying transient Dynamic Flow transaction. attempt={Attempt} maxAttempts={MaxAttempts}",
                        attempt,
                        MaxTransientAttempts);
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(
                            TransientRetryBaseDelayMilliseconds *
                            (1 << (attempt - 1))),
                        ct);
                    continue;
                }

                throw;
            }
            finally
            {
                session?.Dispose();
            }
        }

        throw new InvalidOperationException(
            "DYNAMIC_FLOW_TRANSACTION_RETRY_EXHAUSTED");
    }

    internal static bool IsUnsupportedTransactionFailure(Exception exception)
    {
        if (exception is NotSupportedException &&
            ContainsUnsupportedMarker(exception.Message))
        {
            return true;
        }

        if (exception is MongoCommandException commandException &&
            commandException.Code == 20 &&
            ContainsUnsupportedMarker(commandException.ErrorMessage ?? commandException.Message))
        {
            return true;
        }

        if (exception is MongoClientException clientException &&
            ContainsUnsupportedMarker(clientException.Message))
        {
            return true;
        }

        if (exception is MongoException mongoException &&
            ContainsUnsupportedMarker(mongoException.Message))
        {
            return true;
        }

        return exception.InnerException is not null &&
               IsUnsupportedTransactionFailure(exception.InnerException);
    }

    internal static bool IsTransientTransactionFailure(Exception exception)
    {
        if (exception is MongoException mongoException &&
            mongoException.HasErrorLabel("TransientTransactionError"))
        {
            return true;
        }
        if (exception is MongoCommandException commandException &&
            commandException.Code is 112 or 244 or 251)
        {
            return true;
        }
        if (exception is MongoWriteException writeException &&
            writeException.WriteError?.Code == 112)
        {
            return true;
        }
        if (exception.Message.Contains(
                "Please retry your operation or multi-document transaction",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return exception.InnerException is not null &&
               IsTransientTransactionFailure(exception.InnerException);
    }

    internal static bool IsUnknownTransactionCommitResult(Exception exception)
    {
        if (exception is MongoException mongoException &&
            mongoException.HasErrorLabel("UnknownTransactionCommitResult"))
        {
            return true;
        }
        return exception.InnerException is not null &&
               IsUnknownTransactionCommitResult(exception.InnerException);
    }

    private async Task CommitWithRetryAsync(
        IClientSessionHandle session,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxTransientAttempts; attempt++)
        {
            try
            {
                await session.CommitTransactionAsync(ct);
                return;
            }
            catch (Exception ex) when (
                attempt < MaxTransientAttempts &&
                IsUnknownTransactionCommitResult(ex) &&
                !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Retrying Dynamic Flow transaction commit on the same session. attempt={Attempt} maxAttempts={MaxAttempts}",
                    attempt,
                    MaxTransientAttempts);
            }
        }
    }

    private async Task AbortQuietlyAsync(IClientSessionHandle session)
    {
        try
        {
            await session.AbortTransactionAsync(CancellationToken.None);
        }
        catch (Exception abortException)
        {
            _logger.LogWarning(
                abortException,
                "MongoDB transaction abort failed after a Dynamic Flow definition mutation error.");
        }
    }

    private static bool ContainsUnsupportedMarker(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           UnsupportedMessageMarkers.Any(marker =>
               message.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
