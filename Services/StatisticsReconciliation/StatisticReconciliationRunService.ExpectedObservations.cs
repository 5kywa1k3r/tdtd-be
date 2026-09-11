using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    internal const int ExpectedObservationDefaultPageSize = 50;
    internal const int ExpectedObservationMaxPageSize = 200;

    private const int ExpectedObservationIntegrityBatchSize = 512;
    private static readonly Collation ExpectedObservationOrdinalCollation = new("simple");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        ExpectedObservationIntegrityGates = new(StringComparer.Ordinal);

    public async Task<StatisticReconciliationExpectedBusinessSummaryResponse>
        GetExpectedBusinessSummaryAsync(
            string workId,
            string scopeAssignmentId,
            string reconciliationId,
            string? generationId,
            MeResponse actor,
            CancellationToken ct = default)
        => await StatisticReconciliationExpectedObservationReadBoundary.ExecuteAsync(
            requireDetailedPermission: false,
            requireDetail: () => RequireDetailRole(actor),
            authorizeScope: () =>
                AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct),
            loadAuthorizedRun: async scope =>
            {
                var binding = _activation.RequireFoundation(
                    StatisticReconciliationCapabilities.SourceToResultReconciliation,
                    StatisticReconciliationRouteRegistry.Read);
                return await LoadAuthorizedRunAsync(
                    scope.WorkId,
                    scope.Id,
                    reconciliationId,
                    binding.ChainId,
                    actor,
                    ct);
            },
            requireRunIntegrity: RequireReadIntegrity,
            readAuthorizedTarget: async (_, run) =>
            {
                var normalizedGenerationId =
                    NormalizeExpectedGenerationId(generationId);
                var validated = await RequireValidatedExpectedGenerationAsync(
                    run,
                    normalizedGenerationId,
                    actor,
                    ct);
                return StatisticReconciliationExpectedObservationPresentation.BusinessSummary(
                    run.Id,
                    normalizedGenerationId,
                    actor.Id,
                    RoleGuard.IsSystemAdmin(actor) || RoleGuard.IsAdmin(actor),
                    SummaryPermission,
                    validated.BusinessBuckets);
            });

    public async Task<StatisticReconciliationExpectedProvenancePageResponse>
        GetExpectedProvenanceAsync(
            string workId,
            string scopeAssignmentId,
            string reconciliationId,
            string? generationId,
            string? cursor,
            string? pageSize,
            MeResponse actor,
            CancellationToken ct = default)
        => await StatisticReconciliationExpectedObservationReadBoundary.ExecuteAsync(
            requireDetailedPermission: true,
            requireDetail: () => RequireDetailRole(actor),
            authorizeScope: () =>
                AuthorizeScopeAsync(workId, scopeAssignmentId, actor, ct),
            loadAuthorizedRun: async scope =>
            {
                var binding = _activation.RequireFoundation(
                    StatisticReconciliationCapabilities.SourceToResultReconciliation,
                    StatisticReconciliationRouteRegistry.Read);
                return await LoadAuthorizedRunAsync(
                    scope.WorkId,
                    scope.Id,
                    reconciliationId,
                    binding.ChainId,
                    actor,
                    ct);
            },
            requireRunIntegrity: RequireReadIntegrity,
            readAuthorizedTarget: async (_, run) =>
            {
                var normalizedGenerationId =
                    NormalizeExpectedGenerationId(generationId);
                var normalizedPageSize = NormalizeExpectedPageSize(pageSize);
                var validated = await RequireValidatedExpectedGenerationAsync(
                    run,
                    normalizedGenerationId,
                    actor,
                    ct);
                var manifestSha256 = validated.Commit.Commit!.ManifestSha256;
                var cursorScope = new StatisticReconciliationExpectedCursorScope(
                    actor.Id,
                    run.WorkId,
                    run.ScopeAssignmentId,
                    run.Id,
                    normalizedGenerationId,
                    manifestSha256,
                    StatisticReconciliationExpectedObservationPresentation.ProvenanceView,
                    DetailPermission);
                var codec = new StatisticReconciliationExpectedObservationCursorCodec(
                    _expectedObservationCursorSigningKey);
                StatisticReconciliationExpectedCursorPosition? after = null;
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    try
                    {
                        after = codec.Decode(cursor, cursorScope);
                    }
                    catch (StatisticReconciliationExpectedCursorException)
                    {
                        throw RequestInvalid("cursor", "CURSOR_INVALID");
                    }
                }
                return await ReadExpectedTargetSafelyAsync(
                    actor,
                    () => ReadExpectedProvenancePageAsync(
                        run.Id,
                        normalizedGenerationId,
                        actor.Id,
                        validated,
                        normalizedPageSize,
                        after,
                        position => codec.Encode(cursorScope, position),
                        ct));
            });

    private async Task<ExpectedGenerationRead> RequireValidatedExpectedGenerationAsync(
        StatisticReconciliationRun run,
        string generationId,
        MeResponse actor,
        CancellationToken ct)
    {
        var generationFilter = ExpectedGenerationFilter(run.Id, generationId);
        var commitFilter = generationFilter &
                           Builders<StatisticReconciliationObservation>.Filter.Eq(
                               item => item.RecordKind,
                               StatisticReconciliationObservationRecordKinds.GenerationCommit);
        try
        {
            var commits = await _ctx.StatisticReconciliationObservations
                .Find(
                    commitFilter,
                    new FindOptions
                    {
                        Collation = ExpectedObservationOrdinalCollation
                    })
                .Limit(2)
                .ToListAsync(ct);
            if (commits.Count != 1)
                throw new StatisticReconciliationExpectedObservationIntegrityException(
                    "EXACT_COMMIT_REQUIRED");
            var commit = commits[0];
            StatisticReconciliationExpectedObservationIntegrity.ValidateDocument(commit);
            RequireExpectedGenerationRunBinding(run, commit);
            var commitPayload = commit.Commit!;
            var expectedDocumentCount =
                StatisticReconciliationExpectedObservationIntegrity
                    .RequireGenerationDocumentCount(
                        commitPayload.SourceDecisionCount,
                        commitPayload.ConfigurationPinCount,
                        commitPayload.LineagePinCount,
                        commitPayload.AtomCount);
            if (commitPayload.DocumentCount != expectedDocumentCount)
                throw new StatisticReconciliationExpectedObservationIntegrityException(
                    "COMMIT_COUNT_MISMATCH");

            var actualDocumentCount = await _ctx.StatisticReconciliationObservations
                .CountDocumentsAsync(
                    generationFilter,
                    new CountOptions
                    {
                        Limit = StatisticReconciliationExpectedObservationIntegrity
                            .MaxGenerationDocuments + 1L
                    },
                    ct);
            if (actualDocumentCount != expectedDocumentCount)
                throw new StatisticReconciliationExpectedObservationIntegrityException(
                    "COMMIT_COUNT_MISMATCH");

            var cacheKey = string.Join(
                ":",
                "P10_EXPECTED_INTEGRITY_V1",
                run.Id,
                generationId,
                commit.DocumentSemanticSha256,
                commitPayload.ManifestSha256,
                commit.CreatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                actualDocumentCount.ToString(CultureInfo.InvariantCulture));
            if (_expectedObservationIntegrityCache.TryGetValue<
                    ExpectedGenerationIntegrityCacheEntry>(cacheKey, out var cached) &&
                cached is not null && cached.Total == actualDocumentCount)
                return ExpectedGenerationFromCache(
                    commit,
                    actualDocumentCount,
                    cached);

            var gate = ExpectedObservationIntegrityGates.GetOrAdd(
                cacheKey,
                static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (_expectedObservationIntegrityCache.TryGetValue<
                        ExpectedGenerationIntegrityCacheEntry>(cacheKey, out cached) &&
                    cached is not null && cached.Total == actualDocumentCount)
                    return ExpectedGenerationFromCache(
                        commit,
                        actualDocumentCount,
                        cached);

                ExpectedGenerationValidation streamed;
                try
                {
                    streamed = await StreamValidateExpectedGenerationAsync(
                        generationFilter,
                        ct);
                    if (!string.Equals(
                            streamed.Commit.Id,
                            commit.Id,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            streamed.Commit.DocumentSemanticSha256,
                            commit.DocumentSemanticSha256,
                            StringComparison.Ordinal) ||
                        streamed.Commit.CreatedAtUtc != commit.CreatedAtUtc)
                        throw new
                            StatisticReconciliationExpectedObservationIntegrityException(
                                "COMMIT_CHANGED_DURING_READ");
                    RequireExpectedGenerationRunBinding(run, streamed.Commit);
                }
                catch (StatisticReconciliationExpectedObservationIntegrityException error)
                {
                    _expectedObservationIntegrityCache.Set(
                        cacheKey,
                        new ExpectedGenerationIntegrityCacheEntry(
                            actualDocumentCount,
                            [],
                            error.Reason),
                        TimeSpan.FromSeconds(5));
                    throw;
                }
                catch (StatisticReconciliationExpectedLedgerInputException)
                {
                    const string reason = "CATALOG_PIN_INVALID";
                    _expectedObservationIntegrityCache.Set(
                        cacheKey,
                        new ExpectedGenerationIntegrityCacheEntry(
                            actualDocumentCount,
                            [],
                            reason),
                        TimeSpan.FromSeconds(5));
                    throw new
                        StatisticReconciliationExpectedObservationIntegrityException(reason);
                }
                catch (OverflowException)
                {
                    const string reason = "GENERATION_COUNT_OVERFLOW";
                    _expectedObservationIntegrityCache.Set(
                        cacheKey,
                        new ExpectedGenerationIntegrityCacheEntry(
                            actualDocumentCount,
                            [],
                            reason),
                        TimeSpan.FromSeconds(5));
                    throw new
                        StatisticReconciliationExpectedObservationIntegrityException(reason);
                }

                var cacheEntry = new ExpectedGenerationIntegrityCacheEntry(
                    actualDocumentCount,
                    streamed.BusinessBuckets,
                    null);
                _expectedObservationIntegrityCache.Set(
                    cacheKey,
                    cacheEntry,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
                    });
                return ExpectedGenerationFromCache(
                    streamed.Commit,
                    actualDocumentCount,
                    cacheEntry);
            }
            finally
            {
                gate.Release();
                ExpectedObservationIntegrityGates.TryRemove(cacheKey, out _);
            }
        }
        catch (StatisticReconciliationExpectedObservationIntegrityException error)
        {
            throw HiddenExpectedObservationFailure(actor, error.Reason);
        }
        catch (StatisticReconciliationExpectedLedgerInputException)
        {
            throw HiddenExpectedObservationFailure(actor, "CATALOG_PIN_INVALID");
        }
        catch (OverflowException)
        {
            throw HiddenExpectedObservationFailure(actor, "GENERATION_COUNT_OVERFLOW");
        }
    }

    private async Task<ExpectedGenerationValidation>
        StreamValidateExpectedGenerationAsync(
            FilterDefinition<StatisticReconciliationObservation> generationFilter,
            CancellationToken ct)
    {
        var options = new FindOptions
        {
            Collation = ExpectedObservationOrdinalCollation,
            BatchSize = ExpectedObservationIntegrityBatchSize
        };
        using var cursor = await _ctx.StatisticReconciliationObservations
            .Find(generationFilter, options)
            .SortBy(item => item.Id)
            .ToCursorAsync(ct);
        var accumulator =
            new StatisticReconciliationExpectedObservationIntegrityAccumulator();
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var document in cursor.Current)
                accumulator.Add(document);
        }
        var commit = accumulator.Complete();
        return new ExpectedGenerationValidation(
            commit,
            accumulator.BusinessBuckets);
    }

    private async Task<StatisticReconciliationExpectedProvenancePageResponse>
        ReadExpectedProvenancePageAsync(
            string reconciliationId,
            string generationId,
            string actorUserId,
            ExpectedGenerationRead generation,
            int pageSize,
            StatisticReconciliationExpectedCursorPosition? after,
            Func<StatisticReconciliationExpectedCursorPosition, string> encodeCursor,
            CancellationToken ct)
    {
        var filter = ExpectedGenerationFilter(reconciliationId, generationId);
        if (after is not null)
        {
            var builder = Builders<StatisticReconciliationObservation>.Filter;
            filter &= builder.Or(
                builder.Gt(item => item.RecordKind, after.RecordKind),
                builder.And(
                    builder.Eq(item => item.RecordKind, after.RecordKind),
                    builder.Gt(item => item.Id, after.RecordId)));
        }
        var options = new FindOptions
        {
            Collation = ExpectedObservationOrdinalCollation
        };
        var rows = await _ctx.StatisticReconciliationObservations
            .Find(filter, options)
            .SortBy(item => item.RecordKind)
            .ThenBy(item => item.Id)
            .Limit(pageSize + 1)
            .ToListAsync(ct);
        foreach (var document in rows)
            StatisticReconciliationExpectedObservationIntegrity.RequireCommonHeader(
                document,
                generation.Commit);
        return StatisticReconciliationExpectedObservationPresentation
            .ProvenancePageFromOrderedRows(
                reconciliationId,
                generationId,
                generation.Commit.Commit!.ManifestSha256,
                actorUserId,
                DetailPermission,
                rows,
                generation.Total,
                pageSize,
                encodeCursor);
    }

    private static FilterDefinition<StatisticReconciliationObservation>
        ExpectedGenerationFilter(string reconciliationId, string generationId)
    {
        var builder = Builders<StatisticReconciliationObservation>.Filter;
        return builder.Eq(item => item.ReconciliationId, reconciliationId) &
               builder.Eq(item => item.GenerationId, generationId) &
               builder.Eq(
                   item => item.SchemaVersion,
                   StatisticReconciliationExpectedObservationIntegrity
                       .ObservationSchemaVersion) &
               builder.In(
                   item => item.RecordKind,
                   StatisticReconciliationExpectedObservationIntegrity
                       .RecordKindOrder);
    }

    private static void RequireExpectedGenerationRunBinding(
        StatisticReconciliationRun run,
        StatisticReconciliationObservation commit)
    {
        var pins = commit.CatalogPins;
        var coherent =
            commit.ReconciliationId == run.Id &&
            commit.ImmutableIdentitySha256 == run.ImmutableIdentityHash &&
            commit.ImmutableHeaderSha256 == run.ImmutableHeaderHash &&
            commit.TenantUnitId == run.TenantUnitId &&
            commit.WorkId == run.WorkId &&
            commit.ScopeAssignmentId == run.ScopeAssignmentId &&
            commit.PeriodKey == run.PeriodKey &&
            commit.PeriodInstanceKey == run.PeriodInstanceKey &&
            commit.ConceptKey == run.ConceptKey &&
            commit.Grain == run.Grain &&
            commit.TimeAxis == run.TimeAxis &&
            commit.FilterSha256 == run.FilterHash &&
            commit.DynamicFormVersionId == run.DynamicFormVersionId &&
            commit.DynamicFormSchemaSha256 == run.DynamicFormSchemaHash &&
            commit.FlowTemplateVersionId == run.FlowTemplateVersionId &&
            commit.FlowPayloadSha256 == run.FlowPayloadHash &&
            commit.FlowInstanceId == run.FlowInstanceId &&
            commit.ExecutionEpochId == run.FlowExecutionEpochId &&
            commit.ExecutionEpoch == run.FlowExecutionEpoch &&
            commit.ExecutionEpochRevision == run.FlowExecutionEpochRevision &&
            commit.P8ConfigurationOwnerId == run.P8ConfigOwnerId &&
            commit.P8ConfigurationBundleSha256 == run.P8ConfigBundleHash &&
            pins.P9CatalogVersion == run.P9CatalogVersion &&
            pins.P9CatalogRawSha256 == run.P9CatalogRawSha256 &&
            pins.P9CatalogSemanticSha256 == run.P9CatalogSemanticSha256 &&
            pins.P9SchemaRawSha256 == run.P9SchemaRawSha256 &&
            pins.P9SchemaSemanticSha256 == run.P9SchemaSemanticSha256 &&
            pins.P9StageLockSha256 == run.P9StageLockSha256 &&
            pins.CandidateChainId == run.CandidateChainId &&
            pins.CandidatePromptId == run.CandidatePromptId &&
            pins.CandidateCatalogVersion == run.CandidateCatalogVersion &&
            pins.CandidateCatalogRawSha256 == run.CandidateCatalogRawSha256 &&
            pins.CandidateCatalogSemanticSha256 == run.CandidateCatalogSemanticSha256 &&
            pins.CandidateSchemaRawSha256 == run.CandidateSchemaRawSha256 &&
            pins.CandidateSchemaSemanticSha256 == run.CandidateSchemaSemanticSha256 &&
            pins.CandidateStageLockSha256 == run.CandidateStageLockSha256;
        if (!coherent)
            throw new StatisticReconciliationExpectedObservationIntegrityException(
                "RUN_BINDING_MISMATCH");
    }

    private static string NormalizeExpectedGenerationId(string? value)
    {
        value = value?.Trim();
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(value))
            throw RequestInvalid("generationId", "SHA256_CANONICAL_INVALID");
        return value!;
    }

    private static int NormalizeExpectedPageSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ExpectedObservationDefaultPageSize;
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) || parsed is < 1 or > ExpectedObservationMaxPageSize)
            throw RequestInvalid("pageSize", "PAGE_SIZE_OUT_OF_RANGE");
        return parsed;
    }

    private static async Task<T> ReadExpectedTargetSafelyAsync<T>(
        MeResponse actor,
        Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (StatisticReconciliationExpectedObservationIntegrityException error)
        {
            throw HiddenExpectedObservationFailure(actor, error.Reason);
        }
        catch (StatisticReconciliationExpectedLedgerInputException)
        {
            throw HiddenExpectedObservationFailure(actor, "CATALOG_PIN_INVALID");
        }
        catch (OverflowException)
        {
            throw HiddenExpectedObservationFailure(actor, "GENERATION_COUNT_OVERFLOW");
        }
    }
    private static Exception HiddenExpectedObservationFailure(
        MeResponse actor,
        string reason)
        => RoleGuard.IsSystemAdmin(actor)
            ? RequestInvalid("generationId", reason)
            : Forbidden();

    private static ExpectedGenerationRead ExpectedGenerationFromCache(
        StatisticReconciliationObservation commit,
        long total,
        ExpectedGenerationIntegrityCacheEntry cached)
    {
        if (cached.FailureReason is not null)
            throw new StatisticReconciliationExpectedObservationIntegrityException(
                cached.FailureReason);
        return new ExpectedGenerationRead(
            commit,
            total,
            cached.BusinessBuckets);
    }
    private sealed record ExpectedGenerationRead(
        StatisticReconciliationObservation Commit,
        long Total,
        IReadOnlyList<StatisticReconciliationExpectedBusinessBucket> BusinessBuckets);

    private sealed record ExpectedGenerationValidation(
        StatisticReconciliationObservation Commit,
        IReadOnlyList<StatisticReconciliationExpectedBusinessBucket> BusinessBuckets);

    private sealed record ExpectedGenerationIntegrityCacheEntry(
        long Total,
        IReadOnlyList<StatisticReconciliationExpectedBusinessBucket> BusinessBuckets,
        string? FailureReason);
}
