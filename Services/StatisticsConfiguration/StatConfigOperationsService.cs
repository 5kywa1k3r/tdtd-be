using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Data.Indexes;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models.StatisticsConfiguration;

namespace tdtd_be.Services.StatisticsConfiguration;

/// <summary>
/// Durable, no-content readiness queue for P8 configuration bundles.  The
/// service stores only configuration identities, canonical dependency pins and
/// operational state.  It deliberately has no dependency on result-producing
/// jobs or projection services.
/// </summary>
public sealed partial class StatConfigOperationsService
    : IStatConfigOperationsService
{
    internal const string EnqueueCommandKind =
        "ENQUEUE_STAT_CONFIG_VALIDATION";
    internal const string ResetCommandKind =
        "RESET_STAT_CONFIG_VALIDATION";
    internal const string CancelCommandKind =
        "CANCEL_STAT_CONFIG_VALIDATION";
    internal const string CleanupCommandKind =
        "CLEANUP_STAT_CONFIG_VALIDATION";
    private const string JobOwnerKind = "STAT_CONFIG_VALIDATION_JOB";
    private const string QueueOwnerKind = "STAT_CONFIG_VALIDATION_QUEUE";
    private const string JobTargetKind = "STAT_CONFIG_VALIDATION_JOB";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        ActorEnqueueGates = new(StringComparer.Ordinal);

    private static readonly Regex ObjectIdRegex = new(
        "^[a-f0-9]{24}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MongoDbContext _ctx;
    private readonly IStatConfigTransactionRunner _transactions;
    private readonly IStatConfigOperationsFaultInjector _faults;
    private readonly ILogger<StatConfigOperationsService> _logger;
    private readonly int _maxActiveJobsPerActor;
    private readonly int _maxRetryCount;
    private readonly int _leaseSeconds;
    private readonly int _retryBaseSeconds;
    private readonly int _retryMaxSeconds;
    private readonly TimeSpan _terminalTtl;
    private readonly string _workerId;

    public StatConfigOperationsService(
        MongoDbContext ctx,
        IStatConfigTransactionRunner transactions,
        IStatConfigOperationsFaultInjector faults,
        IConfiguration configuration,
        ILogger<StatConfigOperationsService> logger)
    {
        _ctx = ctx;
        _transactions = transactions;
        _faults = faults;
        _logger = logger;
        _maxActiveJobsPerActor = ReadBounded(
            configuration,
            "StatConfigOperations:MaxActiveJobsPerActor",
            32,
            1,
            10_000);
        _maxRetryCount = ReadBounded(
            configuration,
            "StatConfigOperations:MaxRetryCount",
            3,
            1,
            20);
        _leaseSeconds = ReadBounded(
            configuration,
            "StatConfigOperations:LeaseSeconds",
            30,
            2,
            3_600);
        _retryBaseSeconds = ReadBounded(
            configuration,
            "StatConfigOperations:RetryBaseSeconds",
            1,
            1,
            300);
        _retryMaxSeconds = ReadBounded(
            configuration,
            "StatConfigOperations:RetryMaxSeconds",
            60,
            _retryBaseSeconds,
            3_600);
        _terminalTtl = TimeSpan.FromDays(ReadBounded(
            configuration,
            "StatConfigOperations:TerminalTtlDays",
            7,
            1,
            365));
        _workerId = StatConfigCanonicalJson.HashObject(new
        {
            host = Environment.MachineName,
            process = Environment.ProcessId,
            queue = StatConfigValidationQueue.Name
        });
    }

    public async Task<StatConfigValidationJobStatusResponse> EnqueueAsync(
        string ownerKind,
        string ownerId,
        StatConfigMutationEnvelope<StatConfigValidationEnqueuePayload> request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireSystemAdmin(actor);
        ownerKind = RequireToken(ownerKind, "$.ownerKind", uppercase: true);
        ownerId = RequireToken(ownerId, "$.ownerId");
        var command = StatConfigCanonicalJson.NormalizeCommand(
            request,
            EnqueueCommandKind);
        var payload = NormalizeEnqueuePayload(command.Payload);
        command = command with
        {
            Payload = payload,
            RequestHash = StatConfigCanonicalJson.HashObject(new
            {
                commandKind = EnqueueCommandKind,
                ownerKind,
                ownerId,
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash = command.ExpectedConfigHash,
                payload
            })
        };

        var receiptId = ReceiptId(ownerKind, ownerId, command.CommandId);
        var outsideReplay = await FindReceiptAsync(receiptId, ct);
        if (outsideReplay is not null)
        {
            return RestoreReceipt<StatConfigValidationJobStatusResponse>(
                outsideReplay,
                EnqueueCommandKind,
                command.RequestHash,
                actor.Id);
        }

        using var actorGate = await AcquireActorEnqueueGateAsync(
            actor.Id, ct);
        try
        {
            return await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var replay = await FindReceiptAsync(
                        session,
                        receiptId,
                        transactionCt);
                    if (replay is not null)
                    {
                        return RestoreReceipt<
                            StatConfigValidationJobStatusResponse>(
                                replay,
                                EnqueueCommandKind,
                                command.RequestHash,
                                actor.Id);
                    }

                    var source = await ResolveSourceAsync(
                        session,
                        ownerKind,
                        ownerId,
                        payload,
                        transactionCt);
                    EnsureSourceCas(source, command);

                    var dependencyPins = ExtractDependencyPins(
                        source.ResponseJson);
                    var dependencyPinsHash =
                        StatConfigCanonicalJson.HashObject(dependencyPins);
                    var bundleHash = ComputeBundleHash(
                        ownerKind,
                        ownerId,
                        source,
                        dependencyPinsHash);
                    var dedupeKey = StatConfigCanonicalJson.HashObject(new
                    {
                        queue = StatConfigValidationQueue.Name,
                        ownerKind,
                        ownerId,
                        commandId = command.CommandId,
                        bundleHash
                    });

                    var existingJob = await _ctx.StatConfigValidationJobs
                        .Find(
                            session,
                            item => !item.IsDeleted &&
                                    item.DedupeKey == dedupeKey)
                        .FirstOrDefaultAsync(transactionCt);
                    if (existingJob is not null)
                    {
                        if (string.Equals(
                                existingJob.EnqueueCommandId,
                                command.CommandId,
                                StringComparison.Ordinal))
                        {
                            var winnerReceipt = await FindReceiptAsync(
                                session,
                                existingJob.CommandReceiptId,
                                transactionCt);
                            if (winnerReceipt is not null)
                            {
                                return RestoreReceipt<
                                    StatConfigValidationJobStatusResponse>(
                                        winnerReceipt,
                                        EnqueueCommandKind,
                                        command.RequestHash,
                                        actor.Id);
                            }
                        }

                        var dedupedResponse = ToSafeStatus(existingJob);
                        var dedupedJson = StatConfigCanonicalJson.Canonicalize(
                            dedupedResponse);
                        var dedupedReceipt = NewReceipt(
                            receiptId,
                            ownerKind,
                            ownerId,
                            EnqueueCommandKind,
                            command.CommandId,
                            command.RequestHash,
                            dedupedJson,
                            existingJob,
                            actor.Id,
                            DateTime.UtcNow);
                        var dedupedOutboxId =
                            StatConfigCanonicalJson.HashUtf8(
                                $"{JobTargetKind}\0{existingJob.Id}\0{command.CommandId}");
                        var dedupedOutbox = NewOutbox(
                            dedupedOutboxId,
                            dedupeKey,
                            ownerKind,
                            ownerId,
                            EnqueueCommandKind,
                            command.CommandId,
                            receiptId,
                            existingJob,
                            "STAT_CONFIG_VALIDATION_DEDUPED",
                            command.RequestHash,
                            "Configuration readiness validation deduplicated.",
                            actor.Id,
                            DateTime.UtcNow);
                        await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                            session,
                            dedupedReceipt,
                            cancellationToken: transactionCt);
                        await _ctx.StatConfigAuditOutbox.InsertOneAsync(
                            session,
                            dedupedOutbox,
                            cancellationToken: transactionCt);
                        return dedupedResponse;
                    }

                    var activeCount = await _ctx.StatConfigValidationJobs
                        .CountDocumentsAsync(
                            session,
                            item => !item.IsDeleted &&
                                    item.IsActive &&
                                    item.RequestedByUserId == actor.Id &&
                                    (item.OwnerKind != ownerKind ||
                                     item.OwnerId != ownerId),
                            cancellationToken: transactionCt);
                    if (activeCount >= _maxActiveJobsPerActor)
                    {
                        throw AppExceptionFactory.Create(
                            AppErrorCode.STAT_CONFIG_READINESS_QUOTA_EXCEEDED,
                            new
                            {
                                queue = StatConfigValidationQueue.Name,
                                limit = _maxActiveJobsPerActor
                            });
                    }

                    var now = DateTime.UtcNow;
                    var jobId = ObjectId.GenerateNewId(now).ToString();
                    var correlationId = StatConfigCanonicalJson.HashObject(new
                    {
                        queue = StatConfigValidationQueue.Name,
                        jobId,
                        commandId = command.CommandId
                    });
                    var outboxId = StatConfigCanonicalJson.HashUtf8(
                        $"{JobTargetKind}\0{jobId}\0{command.CommandId}");
                    var job = new StatConfigValidationJob
                    {
                        Id = jobId,
                        QueueName = StatConfigValidationQueue.Name,
                        DedupeKey = dedupeKey,
                        OwnerKind = ownerKind,
                        OwnerId = ownerId,
                        ConfigId = source.ResultConfigId,
                        VersionId = source.ResultVersionId,
                        VersionNo = source.ResultVersionNo,
                        ConfigRevision = source.ResultRevision,
                        ConfigHash = source.ResultConfigHash,
                        DependencyPins = dependencyPins,
                        DependencyPinsHash = dependencyPinsHash,
                        BundleHash = bundleHash,
                        EnqueueCommandId = command.CommandId,
                        CommandReceiptId = receiptId,
                        AuditOutboxId = outboxId,
                        Status = StatConfigValidationJobStatuses.Pending,
                        IsActive = true,
                        StateRevision = 1,
                        RequestedByUserId = actor.Id,
                        CorrelationId = correlationId,
                        RetryCount = 0,
                        MaxRetryCount = _maxRetryCount,
                        CreatedByUserId = actor.Id,
                        UpdatedByUserId = actor.Id,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        IsDeleted = false
                    };
                    job.StateHash = ComputeStateHash(job);
                    var response = ToSafeStatus(job);
                    var responseJson =
                        StatConfigCanonicalJson.Canonicalize(response);
                    var receipt = NewReceipt(
                        receiptId,
                        ownerKind,
                        ownerId,
                        EnqueueCommandKind,
                        command.CommandId,
                        command.RequestHash,
                        responseJson,
                        job,
                        actor.Id,
                        now);
                    var outbox = NewOutbox(
                        outboxId,
                        dedupeKey,
                        ownerKind,
                        ownerId,
                        EnqueueCommandKind,
                        command.CommandId,
                        receiptId,
                        job,
                        "STAT_CONFIG_VALIDATION_ENQUEUED",
                        command.RequestHash,
                        "Configuration readiness validation queued.",
                        actor.Id,
                        now);

                    _faults.ThrowIfConfigured(
                        command.CommandId,
                        StatConfigOperationsFaultPoints.BeforeJobWrite);
                    await _ctx.StatConfigValidationJobs.InsertOneAsync(
                        session,
                        job,
                        cancellationToken: transactionCt);
                    _faults.ThrowIfConfigured(
                        command.CommandId,
                        StatConfigOperationsFaultPoints.AfterJobWrite);
                    _faults.ThrowIfConfigured(
                        command.CommandId,
                        StatConfigOperationsFaultPoints.BeforeReceiptWrite);
                    await _ctx.StatConfigCommandReceipts.InsertOneAsync(
                        session,
                        receipt,
                        cancellationToken: transactionCt);
                    _faults.ThrowIfConfigured(
                        command.CommandId,
                        StatConfigOperationsFaultPoints.AfterReceiptWrite);
                    _faults.ThrowIfConfigured(
                        command.CommandId,
                        StatConfigOperationsFaultPoints.BeforeOutboxWrite);
                    await _ctx.StatConfigAuditOutbox.InsertOneAsync(
                        session,
                        outbox,
                        cancellationToken: transactionCt);
                    _faults.ThrowIfConfigured(
                        command.CommandId,
                        StatConfigOperationsFaultPoints.AfterOutboxWrite);
                    return response;
                },
                ct);
        }
        catch (Exception error) when (IsDuplicateKey(error))
        {
            var replay = await WaitForReceiptAsync(receiptId, ct);
            if (replay is null)
                throw;
            return RestoreReceipt<StatConfigValidationJobStatusResponse>(
                replay,
                EnqueueCommandKind,
                command.RequestHash,
                actor.Id);
        }
    }

    public async Task<StatConfigValidationJobStatusResponse> GetSafeStatusAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        jobId = RequireObjectId(jobId, "$.jobId");
        var job = await _ctx.StatConfigValidationJobs
            .Find(item => item.Id == jobId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        return ToSafeStatus(job ?? throw JobNotFound(jobId));
    }

    public async Task<PagedResult<StatConfigValidationJobDiagnosticsResponse>>
        SearchDiagnosticsAsync(
            StatConfigValidationJobSearchRequest request,
            MeResponse actor,
            CancellationToken ct = default)
    {
        RequireSystemAdmin(actor);
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(0, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var filters = new List<FilterDefinition<StatConfigValidationJob>>
        {
            Builders<StatConfigValidationJob>.Filter.Eq(
                item => item.QueueName,
                StatConfigValidationQueue.Name),
            Builders<StatConfigValidationJob>.Filter.Eq(
                item => item.IsDeleted,
                false)
        };
        if (!request.IncludeInactive)
        {
            filters.Add(Builders<StatConfigValidationJob>.Filter.Eq(
                item => item.IsActive,
                true));
        }
        AddExactFilter(filters, item => item.Status,
            NormalizeInternalStatus(request.Status));
        AddExactFilter(filters, item => item.OwnerKind,
            NormalizeOptional(request.OwnerKind, uppercase: true));
        AddExactFilter(filters, item => item.OwnerId,
            NormalizeOptional(request.OwnerId));
        AddExactFilter(filters, item => item.ConfigId,
            NormalizeOptional(request.ConfigId));
        AddExactFilter(filters, item => item.CorrelationId,
            NormalizeOptional(request.CorrelationId));
        var query = NormalizeOptional(request.Query);
        if (query is not null)
        {
            query = query[..Math.Min(query.Length, 128)];
            var regex = new BsonRegularExpression(
                Regex.Escape(query),
                "i");
            filters.Add(Builders<StatConfigValidationJob>.Filter.Or(
                Builders<StatConfigValidationJob>.Filter.Regex(
                    item => item.OwnerId,
                    regex),
                Builders<StatConfigValidationJob>.Filter.Regex(
                    item => item.ConfigId,
                    regex),
                Builders<StatConfigValidationJob>.Filter.Regex(
                    item => item.CorrelationId,
                    regex)));
        }

        var filter = Builders<StatConfigValidationJob>.Filter.And(filters);
        var total = await _ctx.StatConfigValidationJobs
            .CountDocumentsAsync(filter, cancellationToken: ct);
        var jobs = await _ctx.StatConfigValidationJobs
            .Find(filter)
            .SortByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
        return new PagedResult<StatConfigValidationJobDiagnosticsResponse>(
            jobs.Select(ToDiagnostics).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<StatConfigValidationJobDiagnosticsResponse>
        GetDiagnosticsAsync(
            string jobId,
            MeResponse actor,
            CancellationToken ct = default)
    {
        RequireSystemAdmin(actor);
        jobId = RequireObjectId(jobId, "$.jobId");
        var job = await _ctx.StatConfigValidationJobs
            .Find(item => item.Id == jobId && !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        return ToDiagnostics(job ?? throw JobNotFound(jobId));
    }

    public async Task<StatConfigIndexReadinessResponse> ValidateIndexesAsync(
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireSystemAdmin(actor);
        var statuses = await MongoIndexInitializer
            .ValidateStatConfigIndexesAsync(
                _ctx.Db,
                _ctx.Options,
                ct);
        var rows = statuses
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new StatConfigIndexDefinitionResponse(
                item.Collection,
                item.Name,
                item.Key.ToJson(),
                item.Unique,
                item.PartialFilter?.ToJson(),
                item.ExpireAfterSeconds,
                item.IsExact))
            .ToArray();
        return new StatConfigIndexReadinessResponse(
            rows.All(item => item.IsExact),
            StatConfigCanonicalJson.HashObject(rows.Select(item => new
            {
                item.Collection,
                item.Name,
                item.KeyJson,
                item.Unique,
                item.PartialFilterJson,
                item.ExpireAfterSeconds
            }).ToArray()),
            DateTime.UtcNow,
            rows);
    }

    private async Task<StatConfigCommandReceipt> ResolveSourceAsync(
        IClientSessionHandle session,
        string ownerKind,
        string ownerId,
        StatConfigValidationEnqueuePayload payload,
        CancellationToken ct)
    {
        var filter = Builders<StatConfigCommandReceipt>.Filter.And(
            Builders<StatConfigCommandReceipt>.Filter.Eq(
                item => item.OwnerKind,
                ownerKind),
            Builders<StatConfigCommandReceipt>.Filter.Eq(
                item => item.OwnerId,
                ownerId),
            Builders<StatConfigCommandReceipt>.Filter.Eq(
                item => item.ResultConfigId,
                payload.ConfigId),
            Builders<StatConfigCommandReceipt>.Filter.Eq(
                item => item.ResultVersionId,
                payload.VersionId),
            Builders<StatConfigCommandReceipt>.Filter.Eq(
                item => item.ResultVersionNo,
                payload.VersionNo),
            Builders<StatConfigCommandReceipt>.Filter.Ne(
                item => item.CommandKind,
                EnqueueCommandKind),
            Builders<StatConfigCommandReceipt>.Filter.Ne(
                item => item.CommandKind,
                ResetCommandKind),
            Builders<StatConfigCommandReceipt>.Filter.Ne(
                item => item.CommandKind,
                CancelCommandKind),
            Builders<StatConfigCommandReceipt>.Filter.Ne(
                item => item.CommandKind,
                CleanupCommandKind));
        var source = await _ctx.StatConfigCommandReceipts
            .Find(session, filter)
            .SortByDescending(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (source is null)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    ownerKind,
                    ownerId,
                    reason = "PINNED_CONFIG_SOURCE_NOT_FOUND"
                });
        }
        if (source.ResultStatus.Contains(
                "TOMBSTONE",
                StringComparison.OrdinalIgnoreCase) ||
            source.ResultStatus.Contains(
                "DELETED",
                StringComparison.OrdinalIgnoreCase))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new { ownerKind, ownerId, reason = "PINNED_CONFIG_INACTIVE" });
        }
        if (!string.Equals(
                source.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(source.ResponseJson),
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new
                {
                    ownerKind,
                    ownerId,
                    reason = "PINNED_CONFIG_SOURCE_INTEGRITY"
                });
        }
        return source;
    }

    private static void EnsureSourceCas(
        StatConfigCommandReceipt source,
        NormalizedStatConfigCommand<StatConfigValidationEnqueuePayload>
            command)
    {
        if (source.ResultRevision == command.ExpectedRevision &&
            string.Equals(
                source.ResultConfigHash,
                command.ExpectedConfigHash,
                StringComparison.Ordinal))
        {
            return;
        }
        throw AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new
            {
                expectedRevision = command.ExpectedRevision,
                expectedConfigHash = command.ExpectedConfigHash,
                actualRevision = source.ResultRevision,
                actualConfigHash = source.ResultConfigHash
            });
    }

    private static StatConfigValidationEnqueuePayload NormalizeEnqueuePayload(
        StatConfigValidationEnqueuePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var configId = RequireObjectId(payload.ConfigId, "$.payload.configId");
        var versionId = RequireObjectId(
            payload.VersionId,
            "$.payload.versionId");
        if (payload.VersionNo is null or <= 0)
            throw SchemaError("$.payload.versionNo", "POSITIVE_INTEGER_REQUIRED");
        return new StatConfigValidationEnqueuePayload(
            configId,
            versionId,
            payload.VersionNo.Value);
    }

    internal static List<string> ExtractDependencyPins(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var pins = FindStringArray(
                document.RootElement,
                "dependencyPins");
            return pins
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new { reason = "PINNED_CONFIG_SOURCE_SCHEMA" });
        }
    }

    private static IReadOnlyList<string> FindStringArray(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        propertyName,
                        StringComparison.Ordinal) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    return property.Value.EnumerateArray()
                        .Where(item =>
                            item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? string.Empty)
                        .ToArray();
                }
                var nested = FindStringArray(
                    property.Value,
                    propertyName);
                if (nested.Count > 0)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringArray(item, propertyName);
                if (nested.Count > 0)
                    return nested;
            }
        }
        return Array.Empty<string>();
    }

    internal static string ComputeBundleHash(
        string ownerKind,
        string ownerId,
        StatConfigCommandReceipt source,
        string dependencyPinsHash)
        => StatConfigCanonicalJson.HashObject(new
        {
            ownerKind,
            ownerId,
            configId = source.ResultConfigId,
            versionId = source.ResultVersionId,
            versionNo = source.ResultVersionNo,
            configRevision = source.ResultRevision,
            configHash = source.ResultConfigHash,
            dependencyPinsHash
        });

    internal static string ComputeStateHash(StatConfigValidationJob job)
        => StatConfigCanonicalJson.HashObject(new
        {
            job.Id,
            job.QueueName,
            job.Status,
            job.IsActive,
            job.StateRevision,
            job.RetryCount,
            job.MaxRetryCount,
            NextRetryAtUtc = BsonMillisecond(job.NextRetryAtUtc),
            LastRunAtUtc = BsonMillisecond(job.LastRunAtUtc),
            CompletedAtUtc = BsonMillisecond(job.CompletedAtUtc),
            FailedAtUtc = BsonMillisecond(job.FailedAtUtc),
            DeadLetterAtUtc = BsonMillisecond(job.DeadLetterAtUtc),
            CancelledAtUtc = BsonMillisecond(job.CancelledAtUtc),
            ResetAtUtc = BsonMillisecond(job.ResetAtUtc),
            job.ResetCount,
            job.SafeCode,
            job.FailureFingerprint
        });

    private static DateTime? BsonMillisecond(DateTime? value)
        => value.HasValue
            ? new BsonDateTime(value.Value).ToUniversalTime()
            : null;

    private static StatConfigValidationJobStatusResponse ToSafeStatus(
        StatConfigValidationJob job)
        => new(
            job.Id,
            job.CorrelationId,
            job.OwnerKind,
            job.OwnerId,
            job.ConfigId,
            job.VersionId,
            job.VersionNo,
            job.ConfigRevision,
            job.ConfigHash,
            job.BundleHash,
            MapExternalStatus(job.Status),
            job.RetryCount,
            job.MaxRetryCount,
            job.SafeCode,
            Truncate(job.SafeMessage, 240),
            job.NextRetryAtUtc,
            job.LastRunAtUtc,
            job.CompletedAtUtc,
            job.CreatedAtUtc,
            job.UpdatedAtUtc);

    private static StatConfigValidationJobDiagnosticsResponse ToDiagnostics(
        StatConfigValidationJob job)
        => new(
            ToSafeStatus(job),
            job.StateRevision,
            job.StateHash,
            job.DependencyPinsHash,
            job.RequestedByUserId,
            job.LeaseUntilUtc > DateTime.UtcNow,
            job.LeaseUntilUtc,
            job.LastHeartbeatAtUtc,
            job.FailedAtUtc,
            job.ResetAtUtc,
            job.ResetByUserId,
            job.ResetCount,
            job.DiagnosticCode,
            Truncate(job.DiagnosticMessage, 500),
            job.FailureFingerprint,
            job.ExpiresAtUtc);

    private static string MapExternalStatus(string status)
        => status switch
        {
            StatConfigValidationJobStatuses.Pending =>
                StatConfigValidationExternalStatuses.Queued,
            StatConfigValidationJobStatuses.Running =>
                StatConfigValidationExternalStatuses.Running,
            StatConfigValidationJobStatuses.RetryWaiting =>
                StatConfigValidationExternalStatuses.Retrying,
            StatConfigValidationJobStatuses.Completed =>
                StatConfigValidationExternalStatuses.Done,
            StatConfigValidationJobStatuses.Failed =>
                StatConfigValidationExternalStatuses.Failed,
            StatConfigValidationJobStatuses.Cancelled =>
                StatConfigValidationExternalStatuses.Cancelled,
            StatConfigValidationJobStatuses.Reset =>
                StatConfigValidationExternalStatuses.Reset,
            _ => throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new { reason = "READINESS_STATE_INVALID" })
        };

    private static string? NormalizeInternalStatus(string? status)
    {
        status = NormalizeOptional(status, uppercase: true);
        return status switch
        {
            null => null,
            StatConfigValidationExternalStatuses.Queued =>
                StatConfigValidationJobStatuses.Pending,
            StatConfigValidationExternalStatuses.Retrying =>
                StatConfigValidationJobStatuses.RetryWaiting,
            StatConfigValidationExternalStatuses.Done =>
                StatConfigValidationJobStatuses.Completed,
            _ => status
        };
    }

    private static StatConfigCommandReceipt NewReceipt(
        string id,
        string ownerKind,
        string ownerId,
        string commandKind,
        string commandId,
        string requestHash,
        string responseJson,
        StatConfigValidationJob job,
        string actorId,
        DateTime now)
        => new()
        {
            Id = id,
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            CommandKind = commandKind,
            CommandId = commandId,
            RequestHash = requestHash,
            ResponseJson = responseJson,
            ResponseHash = StatConfigCanonicalJson.HashUtf8(responseJson),
            ResultConfigId = job.ConfigId,
            ResultVersionId = job.VersionId,
            ResultVersionNo = job.VersionNo,
            ResultRevision = job.ConfigRevision,
            ResultStatus = job.Status,
            ResultConfigHash = job.ConfigHash,
            ActorUserId = actorId,
            CreatedAtUtc = now
        };

    private static StatConfigAuditOutboxItem NewOutbox(
        string id,
        string dedupeKey,
        string ownerKind,
        string ownerId,
        string commandKind,
        string commandId,
        string receiptId,
        StatConfigValidationJob job,
        string eventKind,
        string payloadHash,
        string safeSummary,
        string actorId,
        DateTime now)
        => new()
        {
            Id = id,
            DedupeKey = StatConfigCanonicalJson.HashObject(new
            {
                dedupeKey,
                eventKind,
                commandId
            }),
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            CommandKind = commandKind,
            CommandId = commandId,
            ReceiptId = receiptId,
            TargetKind = JobTargetKind,
            TargetId = job.Id,
            ConfigId = job.ConfigId,
            VersionId = job.VersionId,
            VersionNo = job.VersionNo,
            ConfigRevision = job.ConfigRevision,
            ConfigHash = job.ConfigHash,
            StateRevision = job.StateRevision,
            StateHash = job.StateHash,
            EventKind = eventKind,
            EventVersion = 1,
            CorrelationId = job.CorrelationId,
            PayloadHash = payloadHash,
            SafeSummary = safeSummary,
            Status = StatConfigAuditOutboxStatuses.Pending,
            AttemptCount = 0,
            CreatedByUserId = actorId,
            UpdatedByUserId = actorId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDeleted = false
        };

    private async Task<StatConfigCommandReceipt?> FindReceiptAsync(
        string receiptId,
        CancellationToken ct)
        => await _ctx.StatConfigCommandReceipts
            .Find(item => item.Id == receiptId)
            .FirstOrDefaultAsync(ct);

    private async Task<StatConfigCommandReceipt?> FindReceiptAsync(
        IClientSessionHandle session,
        string receiptId,
        CancellationToken ct)
        => await _ctx.StatConfigCommandReceipts
            .Find(session, item => item.Id == receiptId)
            .FirstOrDefaultAsync(ct);

    private async Task<StatConfigCommandReceipt?> WaitForReceiptAsync(
        string receiptId,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var receipt = await FindReceiptAsync(receiptId, ct);
            if (receipt is not null)
                return receipt;
            await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), ct);
        }
        return null;
    }

    private static T RestoreReceipt<T>(
        StatConfigCommandReceipt receipt,
        string commandKind,
        string requestHash,
        string actorId)
    {
        if (!string.Equals(receipt.CommandKind, commandKind,
                StringComparison.Ordinal) ||
            !string.Equals(receipt.RequestHash, requestHash,
                StringComparison.Ordinal) ||
            !string.Equals(receipt.ActorUserId, actorId,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_COMMAND_REPLAY_CONFLICT,
                new
                {
                    receipt.OwnerKind,
                    receipt.OwnerId,
                    receipt.CommandId
                });
        }
        if (!string.Equals(
                receipt.ResponseHash,
                StatConfigCanonicalJson.HashUtf8(receipt.ResponseJson),
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new { reason = "READINESS_RECEIPT_INTEGRITY" });
        }
        try
        {
            return JsonSerializer.Deserialize<T>(
                       receipt.ResponseJson,
                       StatConfigCanonicalJson.StrictJsonOptions)
                   ?? throw new JsonException();
        }
        catch (JsonException error)
        {
            throw new AppException(
                AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
                new { reason = "READINESS_RECEIPT_SCHEMA" },
                innerException: error);
        }
    }

    private static bool IsDuplicateKey(Exception error)
        => error is MongoWriteException write &&
               write.WriteError?.Category == ServerErrorCategory.DuplicateKey ||
           error is MongoBulkWriteException bulk &&
               bulk.WriteErrors.Any(item =>
                   item.Category == ServerErrorCategory.DuplicateKey) ||
           error is MongoCommandException command && command.Code == 11000 ||
           error.InnerException is not null &&
               IsDuplicateKey(error.InnerException);

    private static void RequireSystemAdmin(MeResponse actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RoleGuard.RequireSystemAdmin(actor);
    }

    private static string RequireObjectId(string? value, string path)
    {
        value = NormalizeOptional(value);
        if (value is null ||
            !ObjectIdRegex.IsMatch(value) ||
            !ObjectId.TryParse(value, out var parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw SchemaError(path, "CANONICAL_OBJECT_ID_REQUIRED");
        }
        return value;
    }

    private static string RequireToken(
        string? value,
        string path,
        bool uppercase = false)
        => NormalizeOptional(value, uppercase)
           ?? throw SchemaError(path, "NON_EMPTY_STRING_REQUIRED");

    private static string? NormalizeOptional(
        string? value,
        bool uppercase = false)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return uppercase ? value.ToUpperInvariant() : value;
    }

    private static AppException SchemaError(string path, string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            new { path, reason });

    private static AppException JobNotFound(string jobId)
        => AppExceptionFactory.NotFound(
            AppErrorCode.COMMON_NOT_FOUND,
            new { resource = "STAT_CONFIG_VALIDATION_JOB", jobId });

    private static string ReceiptId(
        string ownerKind,
        string ownerId,
        string commandId)
        => StatConfigCanonicalJson.HashUtf8(
            $"{ownerKind}\0{ownerId}\0{commandId}");

    private static string? Truncate(string? value, int maxLength)
    {
        value = NormalizeOptional(value);
        return value is null
            ? null
            : value[..Math.Min(value.Length, maxLength)];
    }

    private static int ReadBounded(
        IConfiguration configuration,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        var value = configuration.GetValue<int?>(key) ?? fallback;
        return Math.Clamp(value, minimum, maximum);
    }

    private static async Task<IDisposable> AcquireActorEnqueueGateAsync(
        string actorId,
        CancellationToken ct)
    {
        var gate = ActorEnqueueGates.GetOrAdd(
            actorId,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new ActorEnqueueGateLease(actorId, gate);
    }

    private sealed class ActorEnqueueGateLease : IDisposable
    {
        private readonly string _actorId;
        private SemaphoreSlim? _gate;

        public ActorEnqueueGateLease(string actorId, SemaphoreSlim gate)
        {
            _actorId = actorId;
            _gate = gate;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is null)
                return;
            gate.Release();
        }
    }

    private static void AddExactFilter(
        ICollection<FilterDefinition<StatConfigValidationJob>> filters,
        System.Linq.Expressions.Expression<
            Func<StatConfigValidationJob, string>> field,
        string? value)
    {
        if (value is not null)
        {
            filters.Add(Builders<StatConfigValidationJob>.Filter.Eq(
                field,
                value));
        }
    }
}
