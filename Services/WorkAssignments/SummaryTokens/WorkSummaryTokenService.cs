using System.Globalization;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Options;

namespace tdtd_be.Services.WorkAssignments.SummaryTokens;

public sealed class WorkSummaryTokenService : IWorkSummaryTokenService
{
    private readonly MongoDbContext _ctx;
    private readonly SummaryTokenOptions _options;

    public WorkSummaryTokenService(
        MongoDbContext ctx,
        IOptions<SummaryTokenOptions> options)
    {
        _ctx = ctx;
        _options = options.Value;
    }

    public async Task<WorkSummaryTokenConsumeResult> ConsumeAdvancedConfigLockAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        long existingLockedConfigCount,
        string actorUserId,
        string? requestTokenId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthKey = ToMonthKey(now);
        var monthlyQuota = Math.Max(0, _options.MonthlyQuota);
        var isFree = existingLockedConfigCount <= 0;
        var units = isFree ? 0 : 1;
        var usedBefore = isFree
            ? 0
            : await CountMonthlyConsumedAsync(actorUserId, monthKey, ct);

        if (WouldExceedQuota(usedBefore, units, monthlyQuota))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.WORK_SUMMARY_TOKEN_QUOTA_EXCEEDED,
                new
                {
                    actorUserId,
                    monthKey,
                    monthlyQuota,
                    usedBefore,
                    requestedUnits = units,
                    tokenKind = WorkSummaryTokenKinds.AdvancedSummaryConfigLock,
                    configId = config.Id,
                    config.WorkId,
                    config.AssignmentId,
                    config.DynamicFormTemplateId,
                    config.SectionId
                });
        }

        var ledger = new WorkSummaryTokenLedger
        {
            Id = ObjectId.GenerateNewId().ToString(),
            OwnerUserId = actorUserId,
            ActorUserId = actorUserId,
            TokenKind = WorkSummaryTokenKinds.AdvancedSummaryConfigLock,
            Direction = isFree ? WorkSummaryTokenDirections.Free : WorkSummaryTokenDirections.Consume,
            Units = units,
            MonthlyQuota = monthlyQuota,
            PeriodMonthKey = monthKey,
            RequestTokenId = NormalizeOptionalText(requestTokenId),
            WorkId = config.WorkId,
            WorkAssignmentId = config.AssignmentId,
            DynamicFormTemplateId = config.DynamicFormTemplateId,
            SectionId = config.SectionId,
            ConfigId = config.Id,
            ConfigVersionNo = config.VersionNo,
            ConfigHash = config.ConfigHash,
            Reason = isFree
                ? "INITIAL_ADVANCED_SUMMARY_CONFIG_LOCK"
                : "CHANGE_ADVANCED_SUMMARY_CONFIG_LOCK",
            Outcome = WorkSummaryTokenOutcomes.Success,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        await _ctx.WorkSummaryTokenLedgers.InsertOneAsync(ledger, cancellationToken: ct);

        return new WorkSummaryTokenConsumeResult(
            ledger.Id,
            units,
            monthlyQuota,
            usedBefore,
            usedBefore + units,
            isFree);
    }

    public async Task MarkFailedAsync(
        string ledgerId,
        string actorUserId,
        string error,
        CancellationToken ct)
    {
        ledgerId = ledgerId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ledgerId))
            return;

        var now = DateTime.UtcNow;
        var update = Builders<WorkSummaryTokenLedger>.Update
            .Set(x => x.Outcome, WorkSummaryTokenOutcomes.Failed)
            .Set(x => x.Error, NormalizeError(error))
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        await _ctx.WorkSummaryTokenLedgers.UpdateOneAsync(
            x => x.Id == ledgerId && !x.IsDeleted,
            update,
            cancellationToken: ct);
    }

    private async Task<int> CountMonthlyConsumedAsync(
        string ownerUserId,
        string monthKey,
        CancellationToken ct)
    {
        var fb = Builders<WorkSummaryTokenLedger>.Filter;
        var filter = fb.Eq(x => x.OwnerUserId, ownerUserId)
                     & fb.Eq(x => x.PeriodMonthKey, monthKey)
                     & fb.Eq(x => x.TokenKind, WorkSummaryTokenKinds.AdvancedSummaryConfigLock)
                     & fb.Eq(x => x.Direction, WorkSummaryTokenDirections.Consume)
                     & fb.Eq(x => x.Outcome, WorkSummaryTokenOutcomes.Success)
                     & fb.Eq(x => x.IsDeleted, false);

        var rows = await _ctx.WorkSummaryTokenLedgers
            .Find(filter)
            .Project(x => x.Units)
            .ToListAsync(ct);

        return rows.Sum();
    }

    internal static string ToMonthKey(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    internal static bool WouldExceedQuota(int usedBefore, int requestedUnits, int monthlyQuota)
        => requestedUnits > 0 && usedBefore + requestedUnits > Math.Max(0, monthlyQuota);

    private static string? NormalizeOptionalText(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string NormalizeError(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "UNKNOWN_TOKEN_CONSUME_FAILURE";

        return text.Length <= 1000 ? text : text[..1000];
    }
}
