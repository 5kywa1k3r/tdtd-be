using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.Operations;
using tdtd_be.Models;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.WorkAssignmentReports.Runtime;

public interface IWorkReportLifecycleBusinessLogProjector
{
    Task ProjectAndVerifyAsync(
        WorkAssignmentReport report,
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pendingEntries,
        WorkReportPeriod? period,
        string actorUserId,
        CancellationToken ct = default);
}

/// <summary>
/// Materializes immutable lifecycle business events into all three audit stores. Every insert
/// uses the event key captured by the report CAS, so a foreground attempt, exact replay, expired
/// claim, or recurring retry all converge to one record per event and store.
/// </summary>
public sealed class WorkReportLifecycleBusinessLogProjector : IWorkReportLifecycleBusinessLogProjector
{
    private readonly MongoDbContext _ctx;
    private readonly IUserActionLogService _userActionLog;
    private readonly IWorkStatusOperationLogService _statusOperationLog;

    public WorkReportLifecycleBusinessLogProjector(
        MongoDbContext ctx,
        IUserActionLogService userActionLog,
        IWorkStatusOperationLogService statusOperationLog)
    {
        _ctx = ctx;
        _userActionLog = userActionLog;
        _statusOperationLog = statusOperationLog;
    }

    public async Task ProjectAndVerifyAsync(
        WorkAssignmentReport report,
        IReadOnlyCollection<WorkReportLifecycleProjectionOutboxEntry> pendingEntries,
        WorkReportPeriod? period,
        string actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(pendingEntries);

        var events = pendingEntries
            .OrderBy(x => x.LifecycleRevision)
            .ThenBy(x => x.EntryKey, StringComparer.Ordinal)
            .SelectMany(entry => WorkReportLifecycleOutboxContract
                .ResolveBusinessEvents(entry)
                .Select(item => new PendingBusinessEvent(entry, item)))
            .ToList();

        foreach (var item in events)
        {
            ct.ThrowIfCancellationRequested();
            var eventActor = ResolveActorUserId(item.Entry.ActorUserId, actorUserId);
            var businessEvent = item.Event;

            if (!string.IsNullOrWhiteSpace(businessEvent.ReportLogAction))
                await InsertReportLogAsync(report, businessEvent, eventActor, item.Entry.CreatedAtUtc, ct);

            if (!string.IsNullOrWhiteSpace(businessEvent.UserAction))
            {
                var data = businessEvent.Data is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(businessEvent.Data, StringComparer.Ordinal);
                data["lifecycleEntryKey"] = item.Entry.EntryKey;
                data["lifecycleOperation"] = item.Entry.Operation;
                data["commandId"] = item.Entry.CommandId;

                await _userActionLog.RecordIdempotentAsync(
                    businessEvent.EventKey,
                    new UserActionLogSeed
                    {
                        IdempotencyKey = businessEvent.EventKey,
                        Action = businessEvent.UserAction!,
                        Scope = "report",
                        ActorUserId = eventActor,
                        WorkId = report.WorkId,
                        WorkAssignmentId = report.WorkAssignmentId,
                        WorkReportPeriodId = report.WorkReportPeriodId,
                        WorkAssignmentReportId = report.Id,
                        TargetUserId = report.AssigneeUserId,
                        Summary = string.IsNullOrWhiteSpace(businessEvent.SummaryVerb)
                            ? null
                            : $"{businessEvent.SummaryVerb} {report.PeriodInstanceKey}",
                        Data = data,
                        OccurredAtUtc = EnsureUtc(item.Entry.CreatedAtUtc)
                    },
                    ct);
            }

            if (!string.IsNullOrWhiteSpace(businessEvent.StatusOperation))
            {
                var occurredAt = EnsureUtc(item.Entry.CreatedAtUtc);
                await _statusOperationLog.WriteIdempotentAsync(
                    businessEvent.EventKey,
                    new WorkStatusOperationLog
                    {
                        LifecycleEventKey = businessEvent.EventKey,
                        Operation = businessEvent.StatusOperation!,
                        Scope = string.IsNullOrWhiteSpace(businessEvent.StatusScope)
                            ? "report"
                            : businessEvent.StatusScope!,
                        Result = "SUCCESS",
                        WorkId = report.WorkId,
                        WorkAssignmentId = report.WorkAssignmentId,
                        WorkReportPeriodId = report.WorkReportPeriodId,
                        WorkAssignmentReportId = report.Id,
                        ActorUserId = eventActor,
                        FromStatus = businessEvent.FromStatus,
                        ToStatus = businessEvent.ToStatus,
                        PeriodToStatus = period?.Status.ToString(),
                        Summary = $"lifecycleEntryKey={item.Entry.EntryKey};durableReconcile=true",
                        StartedAtUtc = occurredAt,
                        CompletedAtUtc = occurredAt,
                        DurationMs = 0,
                        CreatedAtUtc = occurredAt,
                        UpdatedAtUtc = occurredAt,
                        CreatedByUserId = eventActor,
                        UpdatedByUserId = eventActor,
                        IsDeleted = false
                    },
                    ct);
            }
        }

        await VerifyAsync(events.Select(x => x.Event).ToList(), ct);
    }

    private async Task InsertReportLogAsync(
        WorkAssignmentReport report,
        WorkReportLifecycleBusinessEvent businessEvent,
        string actorUserId,
        DateTime occurredAtUtc,
        CancellationToken ct)
    {
        occurredAtUtc = EnsureUtc(occurredAtUtc);
        var log = new WorkAssignmentReportLog
        {
            Id = WorkReportLifecycleOutboxContract.ComputeStableObjectId(businessEvent.EventKey),
            LifecycleEventKey = businessEvent.EventKey,
            WorkId = report.WorkId,
            WorkAssignmentId = report.WorkAssignmentId,
            WorkReportPeriodId = report.WorkReportPeriodId,
            WorkAssignmentReportId = report.Id,
            Action = businessEvent.ReportLogAction!,
            FromStatus = businessEvent.FromStatus,
            ToStatus = businessEvent.ToStatus,
            ActionByUserId = actorUserId,
            ActionAtUtc = occurredAtUtc,
            Reason = businessEvent.Reason,
            Comment = businessEvent.Comment,
            SnapshotJson = businessEvent.SnapshotJson,
            CreatedAtUtc = occurredAtUtc,
            UpdatedAtUtc = occurredAtUtc,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        try
        {
            await _ctx.WorkAssignmentReportLogs.InsertOneAsync(log, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var exists = await _ctx.WorkAssignmentReportLogs
                .Find(x => x.LifecycleEventKey == businessEvent.EventKey && !x.IsDeleted)
                .AnyAsync(ct);
            if (!exists)
                throw;
        }
    }

    private async Task VerifyAsync(
        IReadOnlyCollection<WorkReportLifecycleBusinessEvent> events,
        CancellationToken ct)
    {
        var reportKeys = events
            .Where(x => !string.IsNullOrWhiteSpace(x.ReportLogAction))
            .Select(x => x.EventKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var userKeys = events
            .Where(x => !string.IsNullOrWhiteSpace(x.UserAction))
            .Select(x => x.EventKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var statusKeys = events
            .Where(x => !string.IsNullOrWhiteSpace(x.StatusOperation))
            .Select(x => x.EventKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (reportKeys.Count > 0)
        {
            var count = await _ctx.WorkAssignmentReportLogs.CountDocumentsAsync(
                x => reportKeys.Contains(x.LifecycleEventKey!) && !x.IsDeleted,
                cancellationToken: ct);
            if (count != reportKeys.Count)
                throw new InvalidOperationException("Lifecycle report-log projection verification failed.");
        }

        if (userKeys.Count > 0)
        {
            var count = await _ctx.UserActionLogs.CountDocumentsAsync(
                x => userKeys.Contains(x.IdempotencyKey!) && !x.IsDeleted,
                cancellationToken: ct);
            if (count != userKeys.Count)
                throw new InvalidOperationException("Lifecycle user-action projection verification failed.");
        }

        if (statusKeys.Count > 0)
        {
            var count = await _ctx.WorkStatusOperationLogs.CountDocumentsAsync(
                x => statusKeys.Contains(x.LifecycleEventKey!) && !x.IsDeleted,
                cancellationToken: ct);
            if (count != statusKeys.Count)
                throw new InvalidOperationException("Lifecycle status-operation projection verification failed.");
        }
    }

    private static string ResolveActorUserId(string? capturedActorUserId, string fallbackActorUserId)
    {
        foreach (var candidate in new[] { capturedActorUserId, fallbackActorUserId })
        {
            if (ObjectId.TryParse(candidate, out _))
                return candidate!;
        }

        throw new InvalidOperationException("Lifecycle business event has no valid actor ObjectId.");
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private sealed record PendingBusinessEvent(
        WorkReportLifecycleProjectionOutboxEntry Entry,
        WorkReportLifecycleBusinessEvent Event);
}
