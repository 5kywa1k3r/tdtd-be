using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunExportService : IStatRunExportService
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    private readonly MongoDbContext _ctx;
    private readonly IP9DirectResultService _directResults;
    private readonly IStatRunCandidateActivation _activation;
    private readonly string _artifactRoot;

    public StatRunExportService(
        MongoDbContext ctx,
        IP9DirectResultService directResults,
        IStatRunCandidateActivation activation,
        IHostEnvironment environment,
        IOptions<MongoOptions> mongo)
    {
        _ctx = ctx;
        _directResults = directResults;
        _activation = activation;
        var database = mongo.Value.Database;
        if (string.IsNullOrWhiteSpace(database) ||
            database.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            database = StatRunCanonicalJson.HashText(database ?? string.Empty)[..16];
        }
        _artifactRoot = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            ".build",
            "stat-run-exports",
            database));
    }

    public async Task<StatRunExportResponse> CreateAsync(
        StatRunExportCreateRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        var normalized = NormalizeRequest(request, actor);
        var capabilityId = StatRunExportContract.CapabilityFor(
            normalized.ResultKind);
        var binding = _activation.RequireCapability(
            capabilityId,
            StatRunExportContract.RouteForCapability(capabilityId));

        // Result authorization deliberately runs before command receipt lookup.
        var canonical = await ResolveCanonicalResultAsync(
            normalized,
            capabilityId,
            binding,
            actor,
            ct);
        ValidateExpectedPins(normalized, canonical);

        var requestHash = StatRunCanonicalJson.HashObject(new
        {
            normalized.CommandId,
            normalized.Format,
            normalized.ResultKind,
            normalized.WorkId,
            normalized.ScopeType,
            normalized.ScopeId,
            normalized.PeriodInstanceKey,
            normalized.ResultId,
            normalized.ExpectedResultHash,
            normalized.ExpectedConfigHash,
            normalized.ExpectedSourceHash,
            normalized.ExpectedLifecycleRevision,
            normalized.Filters
        });
        var exportId = StatRunCanonicalJson.HashText(
            $"{actor.Id}:{normalized.CommandId}")[..24];
        var collection = ExportCollection(normalized.ResultKind);
        var existing = await collection.Find(item =>
                item.Id == exportId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return ValidateReplayAndMap(
                existing,
                normalized,
                requestHash,
                actor,
                true);

        var table = BuildTable(normalized.ResultKind, canonical.Rows);
        if (table.Rows.Count > StatRunExportContract.MaxRows)
            throw ExportLimit("ROW_LIMIT", table.Rows.Count, 0);
        var canonicalFilterJson =
            StatRunCanonicalJson.CanonicalObject(normalized.Filters);
        var filterHash = StatRunCanonicalJson.HashText(canonicalFilterJson);
        var semanticHash =
            StatRunExportColumnManifestContract.ComputeOwnerSemanticSha256(
                normalized.ResultKind,
                normalized.WorkId,
                normalized.ScopeType,
                normalized.ScopeId,
                canonical.ResultId,
                canonical.ResultHash,
                canonical.ConfigHash,
                canonical.SourceHash,
                filterHash,
                canonical.LifecycleRevision,
                table.Rows.Count,
                table.Headers.Count,
                table.ColumnManifestSha256);
        var completedAtUtc = NormalizeUtcToMillisecondPrecision(DateTime.UtcNow);
        var rendering = normalized.Format == StatRunExportFormats.Csv
            ? RenderCsv(table)
            : RenderXlsx(table, canonical, semanticHash, completedAtUtc);
        if (rendering.Content.LongLength > StatRunExportContract.MaxBytes)
        {
            throw ExportLimit(
                "BYTE_LIMIT",
                table.Rows.Count,
                rendering.Content.LongLength);
        }

        var contentHash = Hash(rendering.Content);
        var safeResultId = new string(normalized.ResultId
            .Where(char.IsAsciiLetterOrDigit)
            .Take(12)
            .ToArray());
        if (safeResultId.Length == 0)
            safeResultId = "result";
        var extension = normalized.Format == StatRunExportFormats.Csv
            ? "csv"
            : "xlsx";
        var fileName =
            $"stat-{normalized.ResultKind.ToLowerInvariant().Replace('_', '-')}-{safeResultId}-{contentHash[..12]}.{extension}";
        var storageKey = $"{exportId}/{contentHash}.{extension}";
        var createdFile = await WriteArtifactAtomicallyAsync(
            storageKey,
            rendering.Content,
            contentHash,
            ct);
        var authorizationSnapshotHash = StatRunCanonicalJson.HashObject(new
        {
            actorId = actor.Id,
            actor.UnitId,
            roles = actor.Roles
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            policy = "REQUESTOR_OR_SYSTEM_ADMIN"
        });
        var artifact = new StatRunExportArtifact
        {
            Id = exportId,
            CommandId = normalized.CommandId,
            RequestHash = requestHash,
            ReceiptId = StatRunCanonicalJson.HashObject(new
            {
                actorId = actor.Id,
                normalized.CommandId,
                requestHash
            }),
            RequestedByUserId = actor.Id,
            AuthorizationSnapshotHash = authorizationSnapshotHash,
            CapabilityId = capabilityId,
            ResultKind = normalized.ResultKind,
            Format = normalized.Format,
            WorkId = normalized.WorkId,
            ScopeType = normalized.ScopeType,
            ScopeId = normalized.ScopeId,
            PeriodInstanceKey = normalized.PeriodInstanceKey,
            ResultId = canonical.ResultId,
            ResultHash = canonical.ResultHash,
            ConfigHash = canonical.ConfigHash,
            SourceHash = canonical.SourceHash,
            LifecycleRevision = canonical.LifecycleRevision,
            CatalogVersion = binding.CatalogVersion,
            CatalogRawSha256 = binding.CatalogRawSha256,
            CatalogSemanticSha256 = binding.CatalogSemanticSha256,
            StageLockSha256 = binding.StageLockSha256,
            CandidateChainId = binding.ChainId,
            CandidatePromptId = binding.PromptId,
            CandidateStage = binding.Stage,
            FilterHash = filterHash,
            CanonicalFilterJson = canonicalFilterJson,
            SemanticHash = semanticHash,
            ColumnManifestJson = table.ColumnManifestJson,
            ColumnManifestSha256 = table.ColumnManifestSha256,
            FileName = fileName,
            ContentType = rendering.ContentType,
            StorageKey = storageKey,
            ContentHash = contentHash,
            ByteCount = rendering.Content.LongLength,
            RowCount = table.Rows.Count,
            ColumnCount = table.Headers.Count,
            CompletedAtUtc = completedAtUtc,
            ExpiresAtUtc = completedAtUtc.Add(
                StatRunExportContract.MetadataTtl),
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            CreatedAtUtc = completedAtUtc,
            UpdatedAtUtc = completedAtUtc
        };

        try
        {
            await collection.InsertOneAsync(
                artifact,
                cancellationToken: ct);
            return Map(artifact, false);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category ==
                  ServerErrorCategory.DuplicateKey)
        {
            var winner = await collection.Find(item =>
                    item.Id == exportId &&
                    !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (winner is null)
                throw;
            if (createdFile &&
                !string.Equals(
                    winner.StorageKey,
                    storageKey,
                    StringComparison.Ordinal))
            {
                DeleteArtifactGuarded(storageKey);
            }
            return ValidateReplayAndMap(
                winner,
                normalized,
                requestHash,
                actor,
                true);
        }
    }

    private IMongoCollection<StatRunExportArtifact> ExportCollection(
        string resultKind)
        => string.Equals(
                resultKind,
                StatRunExportResultKinds.Diff,
                StringComparison.Ordinal)
                ? _ctx.WorkReportStatisticDiffExports
                : _ctx.WorkReportStatisticExports;

    private IReadOnlyList<IMongoCollection<StatRunExportArtifact>>
        ExportCollections()
        =>
        [
            _ctx.WorkReportStatisticExports,
            _ctx.WorkReportStatisticDiffExports
        ];

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static DateTime NormalizeUtcToMillisecondPrecision(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private static AppException ExportLimit(
        string reason,
        long rowCount,
        long byteCount)
        => new(
            AppErrorCode.STAT_RUN_EXPORT_LIMIT,
            new
            {
                reason,
                rowCount,
                byteCount,
                maxRows = StatRunExportContract.MaxRows,
                maxBytes = StatRunExportContract.MaxBytes,
                artifactPromoted = false
            });

    private static AppException Invalid(string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reason, writes = 0 });

    private static AppException NotFound(string reason)
        => AppExceptionFactory.NotFound(
            AppErrorCode.COMMON_NOT_FOUND,
            new { reason });
}
