using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public sealed partial class WorkReportStatisticDiffService
{
    private const string P9DiffCollection = "work_report_statistic_diff_results";

    public async Task<P9StatisticDiffResultResponse> RunP9Async(
        string assignmentId,
        string dynamicFormTemplateId,
        P9StatisticDiffRunRequest request,
        CancellationToken ct = default)
    {
        var candidate = _candidateActivation.RequireCapability(
            StatRunCapabilities.Diff,
            StatRunRouteRegistry.DiffExecute);
        var prepared = await P806PrepareAsync(
            assignmentId,
            dynamicFormTemplateId,
            P806Access.Read,
            ct);
        var owner = await P806LoadOwnerAsync(null, prepared, P806Access.Read, ct);
        var state = await P806LoadStateAsync(null, owner, ct);
        var versionId = NormalizeP9DiffObjectId(
            request?.ConfigVersionId,
            "DIFF_CONFIG_VERSION_INVALID");
        var config = state.Rows.SingleOrDefault(row =>
                         string.Equals(row.Id, versionId, StringComparison.Ordinal))
                     ?? throw P9DiffConflict("DIFF_CONFIG_VERSION_NOT_LOCKED");
        if (!string.Equals(config.Status, StatConfigStatuses.Locked, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(config.ConfigHash))
        {
            throw P9DiffConflict("DIFF_CONFIG_VERSION_NOT_LOCKED");
        }
        if (request?.ExpectedConfigRevision is long expectedRevision &&
            expectedRevision != config.Revision)
            throw P9DiffConflict("DIFF_CONFIG_REVISION_STALE");
        if (request?.ExpectedConfigHash is string expectedHash &&
            !string.IsNullOrWhiteSpace(expectedHash) &&
            !string.Equals(expectedHash.Trim(), config.ConfigHash, StringComparison.Ordinal))
            throw P9DiffConflict("DIFF_CONFIG_HASH_STALE");

        var payload = P806DeserializeStoredPayload(config.ConfigJson);
        IReadOnlyList<string> resolvedDependencyPins;
        using (var dependencySession = await _ctx.Db.Client.StartSessionAsync(
                   cancellationToken: ct))
        {
            try
            {
                resolvedDependencyPins = await P806ResolveDependencyPinsAsync(
                    dependencySession,
                    owner,
                    payload,
                    prepared.Me,
                    ct);
            }
            catch (AppException ex) when (
                ex.Code is AppErrorCode.STAT_CONFIG_SCHEMA_INVALID or
                    AppErrorCode.STAT_CONFIG_CAS_CONFLICT)
            {
                throw P9DiffConflict("DIFF_CONFIG_DEPENDENCY_STALE");
            }
        }
        var resolvedConfigHash = P806ComputeConfigHash(
            P806OwnerId(owner),
            payload,
            resolvedDependencyPins);
        if (!resolvedDependencyPins.SequenceEqual(
                config.DependencyPins,
                StringComparer.Ordinal) ||
            !string.Equals(
                resolvedConfigHash,
                config.ConfigHash,
                StringComparison.Ordinal))
        {
            throw P9DiffConflict("DIFF_CONFIG_DEPENDENCY_STALE");
        }
        var commandId = NormalizeP9DiffCommandId(request?.CommandId);
        var limit = Math.Clamp(request?.Limit ?? DefaultLimit, 1, MaxLimit);
        var requestHash = StatConfigCanonicalJson.HashObject(new
        {
            ownerId = P806OwnerId(owner),
            configVersionId = config.Id,
            configRevision = config.Revision,
            configHash = config.ConfigHash,
            commandId,
            limit,
            timeAxis = "UTC_GREGORIAN"
        });
        var receiptId = P9DiffSha256($"{commandId}\n{requestHash}");
        var results = P9DiffResults();
        var existing = await results.Find(item =>
                item.AssignmentId == prepared.AssignmentId &&
                item.DynamicFormTemplateId == prepared.TemplateId &&
                item.CommandId == commandId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                throw P9DiffConflict("DIFF_COMMAND_REPLAY_MISMATCH");
            return MapP9DiffResult(existing, 0, limit);
        }

        var now = DateTime.UtcNow;
        var resultId = ObjectId.GenerateNewId().ToString();
        var leaseOwner = $"p9-diff-sync:{ObjectId.GenerateNewId()}";
        var result = new WorkReportStatisticDiffResult
        {
            Id = resultId,
            RunId = resultId,
            JobId = resultId,
            WorkId = owner.Assignment.WorkId,
            AssignmentId = owner.Assignment.Id,
            DynamicFormTemplateId = owner.Template.Id,
            ConfigId = config.ConfigId!,
            ConfigVersionId = config.Id,
            ConfigVersionNo = config.VersionNo,
            ConfigRevision = config.Revision,
            ConfigHash = config.ConfigHash!,
            DependencyPins = config.DependencyPins.ToList(),
            CandidateChainId = candidate.ChainId,
            CandidatePromptId = candidate.PromptId,
            CandidateStage = candidate.Stage,
            CandidateCatalogRawSha256 = candidate.CatalogRawSha256,
            CandidateCatalogSemanticSha256 = candidate.CatalogSemanticSha256,
            CandidateStageLockSha256 = candidate.StageLockSha256,
            LeftConceptKind = payload.Left!.Selector!.ConceptKind!,
            LeftConceptKey = payload.Left.Selector.ConceptKey!,
            LeftConceptCode = payload.Left.Selector.ConceptCode!,
            LeftDataType = payload.Left.Selector.DataType!,
            LeftPeriodJson = StatConfigCanonicalJson.Canonicalize(payload.Left.Period!),
            RightConceptKind = payload.Right!.Selector!.ConceptKind!,
            RightConceptKey = payload.Right.Selector.ConceptKey!,
            RightConceptCode = payload.Right.Selector.ConceptCode!,
            RightDataType = payload.Right.Selector.DataType!,
            RightPeriodJson = StatConfigCanonicalJson.Canonicalize(payload.Right.Period!),
            Direction = payload.Direction!,
            MissingPolicy = payload.MissingPolicy!,
            EmptyPolicy = payload.EmptyPolicy!,
            CommandId = commandId,
            RequestHash = requestHash,
            ReceiptId = receiptId,
            RequestedByUserId = prepared.Me.Id,
            Status = P9StatisticDiffResultStatuses.Running,
            AttemptNo = 1,
            LeaseOwner = leaseOwner,
            LeaseExpiresAtUtc = now.AddMinutes(10),
            FenceToken = 1,
            IsCurrent = false,
            IsFresh = false,
            IsDirty = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = prepared.Me.Id,
            UpdatedByUserId = prepared.Me.Id,
            IsDeleted = false
        };
        try
        {
            await results.InsertOneAsync(result, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (
            ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            existing = await results.Find(item =>
                    item.AssignmentId == prepared.AssignmentId &&
                    item.DynamicFormTemplateId == prepared.TemplateId &&
                    item.CommandId == commandId &&
                    !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (existing is null ||
                !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                throw P9DiffConflict("DIFF_COMMAND_CONCURRENT_CONFLICT");
            return MapP9DiffResult(existing, 0, limit);
        }

        try
        {
            var computed = await BuildP9DiffResultAsync(owner.Assignment, payload, limit, ct);
            var resultHash = StatConfigCanonicalJson.HashObject(new
            {
                result.ConfigVersionId,
                result.ConfigHash,
                result.Direction,
                result.MissingPolicy,
                result.EmptyPolicy,
                rows = computed.Rows
            });
            using var session = await _ctx.Db.Client.StartSessionAsync(cancellationToken: ct);
            session.StartTransaction();
            await results.UpdateManyAsync(
                session,
                item => item.AssignmentId == result.AssignmentId &&
                        item.DynamicFormTemplateId == result.DynamicFormTemplateId &&
                        item.ConfigId == result.ConfigId &&
                        item.IsCurrent && !item.IsDeleted,
                Builders<WorkReportStatisticDiffResult>.Update
                    .Set(item => item.IsCurrent, false)
                    .Set(item => item.ExpiresAtUtc, now.AddDays(30))
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
            var completedAt = DateTime.UtcNow;
            var complete = await results.UpdateOneAsync(
                session,
                item => item.Id == result.Id &&
                        item.Status == P9StatisticDiffResultStatuses.Running &&
                        item.LeaseOwner == leaseOwner &&
                        item.FenceToken == 1 && !item.IsDeleted,
                Builders<WorkReportStatisticDiffResult>.Update
                    .Set(item => item.Rows, computed.Rows.ToList())
                    .Set(item => item.SourcePins, computed.SourcePins.ToList())
                    .Set(item => item.TotalRowCount, computed.Rows.Count)
                    .Set(item => item.EqualRowCount, computed.Rows.Count(row => row.Equal))
                    .Set(item => item.ChangedRowCount, computed.Rows.Count(row => !row.Equal))
                    .Set(item => item.ResultHash, resultHash)
                    .Set(item => item.Status, P9StatisticDiffResultStatuses.Completed)
                    .Set(item => item.IsCurrent, true)
                    .Set(item => item.IsFresh, true)
                    .Set(item => item.IsDirty, false)
                    .Set(item => item.LeaseOwner, null)
                    .Set(item => item.LeaseExpiresAtUtc, null)
                    .Set(item => item.CompletedAtUtc, completedAt)
                    .Set(item => item.ExpiresAtUtc, null)
                    .Set(item => item.UpdatedAtUtc, completedAt),
                cancellationToken: ct);
            if (complete.ModifiedCount != 1)
                throw P9DiffConflict("DIFF_RESULT_FENCE_LOST");
            await session.CommitTransactionAsync(ct);
        }
        catch (Exception ex)
        {
            await results.UpdateOneAsync(
                item => item.Id == result.Id &&
                        item.Status == P9StatisticDiffResultStatuses.Running &&
                        item.LeaseOwner == leaseOwner && !item.IsDeleted,
                Builders<WorkReportStatisticDiffResult>.Update
                    .Set(item => item.Status, P9StatisticDiffResultStatuses.Failed)
                    .Set(item => item.IsCurrent, false)
                    .Set(item => item.IsFresh, false)
                    .Set(item => item.IsDirty, true)
                    .Set(item => item.LeaseOwner, null)
                    .Set(item => item.LeaseExpiresAtUtc, null)
                    .Set(item => item.FailureCode,
                        ex is AppException app ? app.Code.ToString() : ex.GetType().Name)
                    .Set(item => item.FailureMessage, P9DiffSafeFailure(ex))
                    .Set(item => item.CompletedAtUtc, DateTime.UtcNow)
                    .Set(item => item.ExpiresAtUtc, DateTime.UtcNow.AddDays(7))
                    .Set(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken: ct);
            throw;
        }

        var completed = await results.Find(item => item.Id == result.Id && !item.IsDeleted)
            .SingleAsync(ct);
        return MapP9DiffResult(completed, 0, limit);
    }

    public async Task<P9StatisticDiffResultResponse> GetP9ResultAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string resultId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        _ = _candidateActivation.RequireCapability(
            StatRunCapabilities.Diff,
            StatRunRouteRegistry.DiffResult);
        var prepared = await P806PrepareAsync(
            assignmentId,
            dynamicFormTemplateId,
            P806Access.Read,
            ct);
        resultId = NormalizeP9DiffObjectId(resultId, "DIFF_RESULT_NOT_FOUND");
        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, MaxLimit);
        var result = await P9DiffResults().Find(item =>
                item.Id == resultId &&
                item.AssignmentId == prepared.AssignmentId &&
                item.DynamicFormTemplateId == prepared.TemplateId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.COMMON_NOT_FOUND,
                new { reason = "DIFF_RESULT_NOT_FOUND" });
        return MapP9DiffResult(result, page, pageSize);
    }

    private IMongoCollection<WorkReportStatisticDiffResult> P9DiffResults()
        => _ctx.Db.GetCollection<WorkReportStatisticDiffResult>(P9DiffCollection);

    private static P9StatisticDiffResultResponse MapP9DiffResult(
        WorkReportStatisticDiffResult result,
        int page,
        int pageSize)
    {
        var rows = result.Rows
            .OrderBy(row => row.Ordinal)
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(row => new P9StatisticDiffRowResponse(
                row.RowId,
                row.Ordinal,
                row.Key,
                row.ConceptKind,
                row.ConceptKey,
                MapP9DiffValue(row.Left),
                MapP9DiffValue(row.Right),
                row.Equal,
                row.DifferenceKind,
                row.NumericDelta))
            .ToArray();
        var totalPages = result.TotalRowCount == 0
            ? 0
            : (int)Math.Ceiling(result.TotalRowCount / (double)pageSize);
        return new P9StatisticDiffResultResponse(
            result.Id,
            result.RunId,
            result.Status,
            result.AssignmentId,
            result.DynamicFormTemplateId,
            result.ConfigId,
            result.ConfigVersionId,
            result.ConfigVersionNo,
            result.ConfigRevision,
            result.ConfigHash,
            result.CommandId,
            result.RequestHash,
            result.ReceiptId,
            result.TimeAxis,
            result.TotalRowCount,
            result.EqualRowCount,
            result.ChangedRowCount,
            page,
            pageSize,
            totalPages,
            result.ResultHash,
            result.IsCurrent,
            result.IsFresh,
            result.IsDirty,
            result.CompletedAtUtc,
            result.FailureCode,
            rows);
    }

    private static P9StatisticDiffTypedValueResponse MapP9DiffValue(
        P9StatisticDiffTypedValue value)
        => new(
            value.State,
            value.DataType,
            value.State == P9StatisticDiffValueStates.Redacted
                ? null
                : value.CanonicalValue,
            value.State == P9StatisticDiffValueStates.Redacted
                ? null
                : value.NumericValue,
            value.State == P9StatisticDiffValueStates.Redacted
                ? null
                : value.BooleanValue,
            value.State == P9StatisticDiffValueStates.Redacted
                ? null
                : value.DateValueUtc,
            value.State == P9StatisticDiffValueStates.Redacted
                ? Array.Empty<string>()
                : value.ChoiceIds);

    private static string NormalizeP9DiffCommandId(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            Encoding.UTF8.GetByteCount(normalized) > 128)
            throw P9DiffValidation("DIFF_COMMAND_ID_INVALID");
        return normalized;
    }

    private static string NormalizeP9DiffObjectId(string? value, string reason)
    {
        var normalized = value?.Trim();
        if (!ObjectId.TryParse(normalized, out var parsed) ||
            !string.Equals(normalized, parsed.ToString(), StringComparison.Ordinal))
            throw P9DiffValidation(reason);
        return normalized;
    }

    private static AppException P9DiffValidation(string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reason });

    private static AppException P9DiffConflict(string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.STAT_CONFIG_CAS_CONFLICT,
            new { reason });

    private static string P9DiffSafeFailure(Exception ex)
    {
        var message = ex.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
            message = ex.GetType().Name;
        return message.Length <= 500 ? message : message[..500];
    }

    private static string P9DiffSha256(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed record P9DiffComputation(
    IReadOnlyList<P9StatisticDiffResultRow> Rows,
    IReadOnlyList<P9StatisticDiffSourcePin> SourcePins);
