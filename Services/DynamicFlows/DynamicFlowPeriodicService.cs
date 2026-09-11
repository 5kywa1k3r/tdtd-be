using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowPeriodicService
{
    Task<DynamicFlowPeriodicScheduleDto> CreateScheduleAsync(
        string workId,
        DynamicFlowPeriodicScheduleCreateRequest request,
        string actorUserId,
        CancellationToken ct = default);

    Task<IReadOnlyList<DynamicFlowPeriodicScheduleDto>> GetSchedulesAsync(
        string workId,
        string actorUserId,
        CancellationToken ct = default);

    Task<IReadOnlyList<DynamicFlowPeriodicOccurrenceDto>> GetOccurrencesAsync(
        string workId,
        string scheduleId,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowPeriodicOccurrenceDto> ManualRerunAsync(
        string workId,
        string scheduleId,
        string periodKey,
        DynamicFlowPeriodicManualRerunRequest request,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowPeriodicProcessResponse> ProcessDueAsync(
        DateTime observedAtUtc,
        int maxItems,
        CancellationToken ct = default);
}

public sealed class DynamicFlowPeriodicService : IDynamicFlowPeriodicService
{
    private static readonly TimeSpan ScheduleLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OccurrenceLease = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OnTimeGrace = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    private readonly MongoDbContext _ctx;
    private readonly IDynamicFlowRuntimeService _runtime;
    private readonly IDynamicFlowRuntimeMaterializer _materializer;

    public DynamicFlowPeriodicService(
        MongoDbContext ctx,
        IDynamicFlowRuntimeService runtime,
        IDynamicFlowRuntimeMaterializer materializer)
    {
        _ctx = ctx;
        _runtime = runtime;
        _materializer = materializer;
    }

    public async Task<DynamicFlowPeriodicScheduleDto> CreateScheduleAsync(
        string workId,
        DynamicFlowPeriodicScheduleCreateRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireObjectId(workId, "workId");
        RequireObjectId(request.FlowTemplateVersionId, "flowTemplateVersionId");
        RequireCommand(request.CommandId);
        var normalizedTimeZoneId =
            DynamicFlowPeriodicTopologyContract.NormalizeTimeZoneId(
                request.TimeZoneId);
        var localTime =
            DynamicFlowPeriodicTopologyContract.ParseLocalTime(
                request.LocalTime);
        var observedAtUtc = NormalizeUtc(
            request.EffectiveFromUtc ?? DateTime.UtcNow);
        var provisional = await _runtime.PreflightAsync(
            workId,
            new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = request.FlowTemplateVersionId,
                CommandId = request.CommandId.Trim(),
                TargetUnitIds = request.TargetUnitIds,
                PeriodKey = observedAtUtc.ToString("yyyy-MM-dd"),
                ScheduleIdentityJson = "{}"
            },
            actorUserId,
            ct);
        if (provisional.FlowPin.ArchetypeId !=
                DynamicFlowPeriodicTopologyContract.ArchetypeId ||
            provisional.Eligibility !=
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_NOT_EXECUTABLE");
        }
        var topology = DynamicFlowPeriodicTopologyContract.Require(
            provisional.LockedDefinitionJson,
            provisional.FlowPin.TopologyHash);
        var scheduleId =
            DynamicFlowPeriodicTopologyContract.BuildScheduleId(
                workId,
                provisional.FlowPin.FlowTemplateVersionId,
                topology.ScheduleKey);
        var scheduleIdentityJson = CanonicalJson(new
        {
            scheduleId,
            scheduleKey = topology.ScheduleKey,
            timeZoneId = normalizedTimeZoneId,
            cadence = DynamicFlowPeriodicCadences.Daily,
            localTime = request.LocalTime.Trim(),
            policyVersion = DynamicFlowPeriodicTopologyContract.PolicyVersion,
            flowTemplateVersionId =
                provisional.FlowPin.FlowTemplateVersionId,
            flowPayloadHash = provisional.FlowPin.PayloadHash,
            topologyHash = provisional.FlowPin.TopologyHash,
            catalogVersion = provisional.FlowPin.CatalogVersion,
            catalogSemanticHash =
                provisional.FlowPin.CatalogSemanticHash
        });
        var nextDueAtUtc =
            DynamicFlowPeriodicTopologyContract.NextDueAtUtc(
                observedAtUtc.AddTicks(-1),
                localTime,
                normalizedTimeZoneId);
        provisional = await _runtime.PreflightAsync(
            workId,
            new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId =
                    request.FlowTemplateVersionId,
                CommandId =
                    DynamicFlowPeriodicTopologyContract
                        .BuildLaunchCommandId(
                            scheduleId,
                            DynamicFlowPeriodicTopologyContract
                                .PeriodKey(
                                    nextDueAtUtc,
                                    normalizedTimeZoneId)),
                TargetUnitIds = request.TargetUnitIds,
                PeriodKey =
                    DynamicFlowPeriodicTopologyContract.PeriodKey(
                        nextDueAtUtc,
                        normalizedTimeZoneId),
                ScheduleIdentityJson = scheduleIdentityJson
            },
            actorUserId,
            ct);
        scheduleIdentityJson = provisional.ScheduleIdentityJson;
        var scheduleIdentityHash =
            provisional.ScheduleIdentityHash;
        var now = DateTime.UtcNow;
        var schedule = new DynamicFlowPeriodicSchedule
        {
            Id = scheduleId,
            WorkId = workId,
            FlowTemplateId = provisional.FlowPin.FlowTemplateId,
            FlowTemplateVersionId =
                provisional.FlowPin.FlowTemplateVersionId,
            FlowTemplateVersionNo =
                provisional.FlowPin.FlowTemplateVersionNo,
            FlowPayloadHash = provisional.FlowPin.PayloadHash,
            CatalogVersion = provisional.FlowPin.CatalogVersion,
            CatalogSemanticHash =
                provisional.FlowPin.CatalogSemanticHash,
            DefinitionRevision =
                provisional.FlowPin.DefinitionRevision,
            TopologySnapshotJson = provisional.LockedDefinitionJson,
            TopologySnapshotHash = provisional.FlowPin.TopologyHash,
            ScheduleKey = topology.ScheduleKey,
            TimeZoneId = request.TimeZoneId.Trim(),
            NormalizedTimeZoneId = normalizedTimeZoneId,
            Cadence = DynamicFlowPeriodicCadences.Daily,
            LocalTime = request.LocalTime.Trim(),
            PolicyVersion =
                DynamicFlowPeriodicTopologyContract.PolicyVersion,
            TargetUnitIds = provisional.Targets
                .Select(target => target.TargetUnitId)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList(),
            IssuerUserId = actorUserId,
            IssuerUnitId = provisional.IssuerUnitId,
            ScheduleIdentityJson = scheduleIdentityJson,
            ScheduleIdentityHash = scheduleIdentityHash,
            NextDueAtUtc = nextDueAtUtc,
            State = DynamicFlowPeriodicScheduleStates.Active,
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        var existing = await _ctx.DynamicFlowPeriodicSchedules
            .Find(candidate => candidate.Id == scheduleId)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            EnsureIssuer(existing, actorUserId);
            if (!FixedEquals(
                    existing.ScheduleIdentityHash,
                    scheduleIdentityHash) ||
                !existing.TargetUnitIds.SequenceEqual(
                    schedule.TargetUnitIds,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_SCHEDULE_CONFLICT");
            }
            return ToDto(existing);
        }

        try
        {
            await _ctx.DynamicFlowPeriodicSchedules.InsertOneAsync(
                schedule,
                cancellationToken: ct);
        }
        catch (MongoException error) when (IsDuplicateKey(error))
        {
            existing = await _ctx.DynamicFlowPeriodicSchedules
                .Find(candidate => candidate.Id == scheduleId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_SCHEDULE_REPLAY_NOT_FOUND",
                    error);
            EnsureIssuer(existing, actorUserId);
            if (!FixedEquals(
                    existing.ScheduleIdentityHash,
                    scheduleIdentityHash))
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_SCHEDULE_CONFLICT",
                    error);
            }
            return ToDto(existing);
        }
        return ToDto(schedule);
    }

    public async Task<IReadOnlyList<DynamicFlowPeriodicScheduleDto>>
        GetSchedulesAsync(
            string workId,
            string actorUserId,
            CancellationToken ct = default)
    {
        RequireObjectId(workId, "workId");
        RequireObjectId(actorUserId, "actorUserId");
        return (await _ctx.DynamicFlowPeriodicSchedules
                .Find(candidate =>
                    candidate.WorkId == workId &&
                    candidate.IssuerUserId == actorUserId &&
                    !candidate.IsDeleted)
                .SortBy(candidate => candidate.NextDueAtUtc)
                .ThenBy(candidate => candidate.Id)
                .ToListAsync(ct))
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<DynamicFlowPeriodicOccurrenceDto>>
        GetOccurrencesAsync(
            string workId,
            string scheduleId,
            string actorUserId,
            CancellationToken ct = default)
    {
        var schedule = await RequireScheduleAsync(
            workId,
            scheduleId,
            actorUserId,
            ct);
        return (await _ctx.DynamicFlowPeriodicOccurrences
                .Find(candidate =>
                    candidate.ScheduleId == schedule.Id &&
                    !candidate.IsDeleted)
                .SortByDescending(candidate => candidate.ScheduledAtUtc)
                .ThenByDescending(candidate => candidate.Id)
                .Limit(200)
                .ToListAsync(ct))
            .Select(ToDto)
            .ToList();
    }

    public async Task<DynamicFlowPeriodicOccurrenceDto> ManualRerunAsync(
        string workId,
        string scheduleId,
        string periodKey,
        DynamicFlowPeriodicManualRerunRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCommand(request.CommandId);
        var schedule = await RequireScheduleAsync(
            workId,
            scheduleId,
            actorUserId,
            ct);
        var occurrence = await _ctx.DynamicFlowPeriodicOccurrences
            .Find(candidate =>
                candidate.ScheduleId == schedule.Id &&
                candidate.PeriodKey == periodKey &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_NOT_FOUND");
        if (occurrence.ManualCommandIds.Contains(
                request.CommandId.Trim(),
                StringComparer.Ordinal))
        {
            return ToDto(occurrence);
        }

        var recorded = await _ctx.DynamicFlowPeriodicOccurrences.UpdateOneAsync(
            candidate =>
                candidate.Id == occurrence.Id &&
                !candidate.ManualCommandIds.Contains(request.CommandId.Trim()) &&
                candidate.Revision == occurrence.Revision,
            Builders<DynamicFlowPeriodicOccurrence>.Update
                .AddToSet(
                    candidate => candidate.ManualCommandIds,
                    request.CommandId.Trim())
                .Set(candidate => candidate.UpdatedAtUtc, DateTime.UtcNow)
                .Set(candidate => candidate.UpdatedByUserId, actorUserId)
                .Inc(candidate => candidate.Revision, 1),
            cancellationToken: ct);
        if (recorded.ModifiedCount != 1)
        {
            occurrence = await _ctx.DynamicFlowPeriodicOccurrences
                .Find(candidate => candidate.Id == occurrence.Id)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_NOT_FOUND");
            if (!occurrence.ManualCommandIds.Contains(
                    request.CommandId.Trim(),
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_STALE");
            }
            return ToDto(occurrence);
        }
        occurrence.Revision++;
        occurrence.ManualCommandIds.Add(request.CommandId.Trim());

        if (occurrence.FlowInstanceId is null &&
            occurrence.State is
                DynamicFlowPeriodicOccurrenceStates.Missed or
                DynamicFlowPeriodicOccurrenceStates.Failed)
        {
            var launched = await TryClaimAndLaunchAsync(
                schedule,
                occurrence,
                actorUserId,
                DateTime.UtcNow,
                ct);
            return ToDto(launched);
        }
        return ToDto(occurrence);
    }

    public async Task<DynamicFlowPeriodicProcessResponse> ProcessDueAsync(
        DateTime observedAtUtc,
        int maxItems,
        CancellationToken ct = default)
    {
        observedAtUtc = NormalizeUtc(observedAtUtc);
        maxItems = Math.Clamp(maxItems, 1, 100);
        var response = new DynamicFlowPeriodicProcessResponse
        {
            ObservedAtUtc = observedAtUtc
        };

        var stranded = await _ctx.DynamicFlowPeriodicOccurrences
            .Find(candidate =>
                candidate.State ==
                    DynamicFlowPeriodicOccurrenceStates.Launching &&
                candidate.FlowInstanceId == null &&
                (candidate.LeaseUntilUtc == null ||
                 candidate.LeaseUntilUtc <= observedAtUtc) &&
                !candidate.IsDeleted)
            .SortBy(candidate => candidate.ScheduledAtUtc)
            .Limit(maxItems)
            .ToListAsync(ct);
        foreach (var occurrence in stranded)
        {
            var schedule = await _ctx.DynamicFlowPeriodicSchedules
                .Find(candidate =>
                    candidate.Id == occurrence.ScheduleId &&
                    candidate.State ==
                        DynamicFlowPeriodicScheduleStates.Active &&
                    !candidate.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (schedule is null)
                continue;
            var launched = await TryClaimAndLaunchAsync(
                schedule,
                occurrence,
                schedule.IssuerUserId,
                observedAtUtc,
                ct);
            response.RecoveredCount++;
            if (launched.State ==
                DynamicFlowPeriodicOccurrenceStates.Launched)
            {
                response.LaunchedCount++;
            }
        }

        var remaining = maxItems - response.RecoveredCount;
        if (remaining <= 0)
            return response;
        var candidates = await _ctx.DynamicFlowPeriodicSchedules
            .Find(candidate =>
                candidate.State ==
                    DynamicFlowPeriodicScheduleStates.Active &&
                candidate.NextDueAtUtc <= observedAtUtc &&
                (candidate.LeaseUntilUtc == null ||
                 candidate.LeaseUntilUtc <= observedAtUtc) &&
                !candidate.IsDeleted)
            .SortBy(candidate => candidate.NextDueAtUtc)
            .ThenBy(candidate => candidate.Id)
            .Limit(remaining)
            .ToListAsync(ct);
        foreach (var candidate in candidates)
        {
            var leaseId = Guid.NewGuid().ToString("N");
            var claimed = await _ctx.DynamicFlowPeriodicSchedules
                .FindOneAndUpdateAsync(
                    schedule =>
                        schedule.Id == candidate.Id &&
                        schedule.Revision == candidate.Revision &&
                        schedule.State ==
                            DynamicFlowPeriodicScheduleStates.Active &&
                        schedule.NextDueAtUtc <= observedAtUtc &&
                        (schedule.LeaseUntilUtc == null ||
                         schedule.LeaseUntilUtc <= observedAtUtc) &&
                        !schedule.IsDeleted,
                    Builders<DynamicFlowPeriodicSchedule>.Update
                        .Set(schedule => schedule.LeaseId, leaseId)
                        .Set(
                            schedule => schedule.LeaseUntilUtc,
                            observedAtUtc + ScheduleLease)
                        .Set(schedule => schedule.UpdatedAtUtc, DateTime.UtcNow)
                        .Inc(schedule => schedule.Revision, 1),
                    new FindOneAndUpdateOptions<
                        DynamicFlowPeriodicSchedule>
                    {
                        ReturnDocument = ReturnDocument.After
                    },
                    ct);
            if (claimed is null)
                continue;
            response.ClaimedCount++;
            var occurrence = await ObserveOccurrenceAsync(
                claimed,
                leaseId,
                observedAtUtc,
                ct);
            if (occurrence.State ==
                DynamicFlowPeriodicOccurrenceStates.Missed)
            {
                response.MissedCount++;
                continue;
            }
            var launched = await TryClaimAndLaunchAsync(
                claimed,
                occurrence,
                claimed.IssuerUserId,
                observedAtUtc,
                ct);
            if (launched.State ==
                DynamicFlowPeriodicOccurrenceStates.Launched)
            {
                response.LaunchedCount++;
            }
        }
        return response;
    }

    private async Task<DynamicFlowPeriodicOccurrence>
        ObserveOccurrenceAsync(
            DynamicFlowPeriodicSchedule schedule,
            string scheduleLeaseId,
            DateTime observedAtUtc,
            CancellationToken ct)
    {
        var scheduledAtUtc = schedule.NextDueAtUtc;
        var periodKey = DynamicFlowPeriodicTopologyContract.PeriodKey(
            scheduledAtUtc,
            schedule.NormalizedTimeZoneId);
        var occurrenceId =
            DynamicFlowPeriodicTopologyContract.BuildOccurrenceId(
                schedule.Id,
                periodKey);
        var activeOverlap = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.PeriodicScheduleId == schedule.Id &&
                candidate.State != DynamicFlowInstanceStates.Completed &&
                candidate.State != DynamicFlowInstanceStates.Terminated &&
                candidate.State != DynamicFlowInstanceStates.Failed &&
                !candidate.IsDeleted)
            .AnyAsync(ct);
        var reason = activeOverlap
            ? DynamicFlowPeriodicTopologyContract.MissedOverlap
            : observedAtUtc > scheduledAtUtc + OnTimeGrace
                ? DynamicFlowPeriodicTopologyContract.MissedNoCatchUp
                : null;
        var state = reason is null
            ? DynamicFlowPeriodicOccurrenceStates.Pending
            : DynamicFlowPeriodicOccurrenceStates.Missed;
        var commandId =
            DynamicFlowPeriodicTopologyContract.BuildLaunchCommandId(
                schedule.Id,
                periodKey);
        var now = DateTime.UtcNow;
        var occurrence = new DynamicFlowPeriodicOccurrence
        {
            Id = occurrenceId,
            ScheduleId = schedule.Id,
            WorkId = schedule.WorkId,
            PeriodKey = periodKey,
            TimeZoneId = schedule.NormalizedTimeZoneId,
            PolicyVersion = schedule.PolicyVersion,
            ScheduleIdentityHash = schedule.ScheduleIdentityHash,
            ScheduledAtUtc = scheduledAtUtc,
            ObservedAtUtc = observedAtUtc,
            State = state,
            ReasonCode = reason,
            LaunchCommandId = commandId,
            Revision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = schedule.IssuerUserId,
            UpdatedByUserId = schedule.IssuerUserId,
            IsDeleted = false
        };
        try
        {
            await _ctx.DynamicFlowPeriodicOccurrences.InsertOneAsync(
                occurrence,
                cancellationToken: ct);
        }
        catch (MongoException error) when (IsDuplicateKey(error))
        {
            occurrence = await _ctx.DynamicFlowPeriodicOccurrences
                .Find(candidate => candidate.Id == occurrenceId)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_REPLAY_NOT_FOUND",
                    error);
        }

        var localTime =
            DynamicFlowPeriodicTopologyContract.ParseLocalTime(
                schedule.LocalTime);
        var nextDueAtUtc =
            DynamicFlowPeriodicTopologyContract.NextDueAtUtc(
                observedAtUtc,
                localTime,
                schedule.NormalizedTimeZoneId);
        var advanced = await _ctx.DynamicFlowPeriodicSchedules.UpdateOneAsync(
            candidate =>
                candidate.Id == schedule.Id &&
                candidate.LeaseId == scheduleLeaseId &&
                candidate.Revision == schedule.Revision,
            Builders<DynamicFlowPeriodicSchedule>.Update
                .Set(candidate => candidate.NextDueAtUtc, nextDueAtUtc)
                .Set(candidate => candidate.LastObservedPeriodKey, periodKey)
                .Set(candidate => candidate.LeaseId, null)
                .Set(candidate => candidate.LeaseUntilUtc, null)
                .Set(candidate => candidate.UpdatedAtUtc, now)
                .Set(candidate => candidate.UpdatedByUserId, schedule.IssuerUserId)
                .Inc(candidate => candidate.Revision, 1),
            cancellationToken: ct);
        if (advanced.ModifiedCount != 1)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_SCHEDULE_LEASE_LOST");
        }
        return occurrence;
    }

    private async Task<DynamicFlowPeriodicOccurrence>
        TryClaimAndLaunchAsync(
            DynamicFlowPeriodicSchedule schedule,
            DynamicFlowPeriodicOccurrence occurrence,
            string actorUserId,
            DateTime observedAtUtc,
            CancellationToken ct)
    {
        if (occurrence.FlowInstanceId is not null ||
            occurrence.State ==
                DynamicFlowPeriodicOccurrenceStates.Launched)
        {
            return occurrence;
        }
        var activeOverlap = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.PeriodicScheduleId == schedule.Id &&
                candidate.PeriodKey != occurrence.PeriodKey &&
                candidate.State != DynamicFlowInstanceStates.Completed &&
                candidate.State != DynamicFlowInstanceStates.Terminated &&
                candidate.State != DynamicFlowInstanceStates.Failed &&
                !candidate.IsDeleted)
            .AnyAsync(ct);
        if (activeOverlap)
        {
            await _ctx.DynamicFlowPeriodicOccurrences.UpdateOneAsync(
                candidate => candidate.Id == occurrence.Id,
                Builders<DynamicFlowPeriodicOccurrence>.Update
                    .Set(
                        candidate => candidate.State,
                        DynamicFlowPeriodicOccurrenceStates.Missed)
                    .Set(
                        candidate => candidate.ReasonCode,
                        DynamicFlowPeriodicTopologyContract.MissedOverlap)
                    .Set(candidate => candidate.LeaseId, null)
                    .Set(candidate => candidate.LeaseUntilUtc, null)
                    .Set(candidate => candidate.UpdatedAtUtc, DateTime.UtcNow)
                    .Inc(candidate => candidate.Revision, 1),
                cancellationToken: ct);
            occurrence.State =
                DynamicFlowPeriodicOccurrenceStates.Missed;
            occurrence.ReasonCode =
                DynamicFlowPeriodicTopologyContract.MissedOverlap;
            occurrence.Revision++;
            return occurrence;
        }

        var occurrenceLeaseId = Guid.NewGuid().ToString("N");
        var claimed = await _ctx.DynamicFlowPeriodicOccurrences
            .FindOneAndUpdateAsync(
                candidate =>
                    candidate.Id == occurrence.Id &&
                    candidate.FlowInstanceId == null &&
                    candidate.State !=
                        DynamicFlowPeriodicOccurrenceStates.Launched &&
                    (candidate.State !=
                         DynamicFlowPeriodicOccurrenceStates.Launching ||
                     candidate.LeaseUntilUtc == null ||
                     candidate.LeaseUntilUtc <= observedAtUtc) &&
                    !candidate.IsDeleted,
                Builders<DynamicFlowPeriodicOccurrence>.Update
                    .Set(
                        candidate => candidate.State,
                        DynamicFlowPeriodicOccurrenceStates.Launching)
                    .Set(candidate => candidate.ReasonCode, null)
                    .Set(candidate => candidate.LeaseId, occurrenceLeaseId)
                    .Set(
                        candidate => candidate.LeaseUntilUtc,
                        observedAtUtc + OccurrenceLease)
                    .Set(candidate => candidate.UpdatedAtUtc, DateTime.UtcNow)
                    .Set(candidate => candidate.UpdatedByUserId, actorUserId)
                    .Inc(candidate => candidate.Revision, 1),
                new FindOneAndUpdateOptions<DynamicFlowPeriodicOccurrence>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
        if (claimed is null)
        {
            return await _ctx.DynamicFlowPeriodicOccurrences
                .Find(candidate => candidate.Id == occurrence.Id)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_NOT_FOUND");
        }

        var preflight = await _runtime.PreflightAsync(
            schedule.WorkId,
            new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId =
                    schedule.FlowTemplateVersionId,
                CommandId = claimed.LaunchCommandId,
                TargetUnitIds = schedule.TargetUnitIds,
                PeriodKey = claimed.PeriodKey,
                ScheduleIdentityJson =
                    schedule.ScheduleIdentityJson
            },
            actorUserId,
            ct);
        EnsureSchedulePins(schedule, preflight);
        var confirm = new DynamicFlowConfirmRequest
        {
            FlowTemplateVersionId =
                schedule.FlowTemplateVersionId,
            CommandId = claimed.LaunchCommandId,
            TargetUnitIds = schedule.TargetUnitIds,
            PeriodKey = claimed.PeriodKey,
            ScheduleIdentityJson = schedule.ScheduleIdentityJson,
            SnapshotToken = preflight.SnapshotToken
        };
        try
        {
            var result =
                await _materializer.ConfirmAndMaterializePeriodicAsync(
                    preflight,
                    confirm,
                    actorUserId,
                    new DynamicFlowPeriodicLaunchContext(
                        schedule.Id,
                        claimed.Id,
                        claimed.PeriodKey,
                        schedule.NormalizedTimeZoneId,
                        schedule.PolicyVersion,
                        schedule.ScheduleIdentityJson,
                        schedule.ScheduleIdentityHash,
                        occurrenceLeaseId,
                        claimed.Revision),
                    ct);
            return await _ctx.DynamicFlowPeriodicOccurrences
                .Find(candidate => candidate.Id == claimed.Id)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(
                    "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_NOT_FOUND");
        }
        catch
        {
            await _ctx.DynamicFlowPeriodicOccurrences.UpdateOneAsync(
                candidate =>
                    candidate.Id == claimed.Id &&
                    candidate.State ==
                        DynamicFlowPeriodicOccurrenceStates.Launching &&
                    candidate.LeaseId == occurrenceLeaseId,
                Builders<DynamicFlowPeriodicOccurrence>.Update
                    .Set(
                        candidate => candidate.State,
                        DynamicFlowPeriodicOccurrenceStates.Failed)
                    .Set(
                        candidate => candidate.ReasonCode,
                        "DYNAMIC_FLOW_PERIODIC_LAUNCH_FAILED")
                    .Set(candidate => candidate.LeaseId, null)
                    .Set(candidate => candidate.LeaseUntilUtc, null)
                    .Set(candidate => candidate.UpdatedAtUtc, DateTime.UtcNow)
                    .Inc(candidate => candidate.Revision, 1),
                cancellationToken: ct);
            throw;
        }
    }

    private async Task<DynamicFlowPeriodicSchedule> RequireScheduleAsync(
        string workId,
        string scheduleId,
        string actorUserId,
        CancellationToken ct)
    {
        RequireObjectId(workId, "workId");
        RequireObjectId(scheduleId, "scheduleId");
        RequireObjectId(actorUserId, "actorUserId");
        var schedule = await _ctx.DynamicFlowPeriodicSchedules
            .Find(candidate =>
                candidate.Id == scheduleId &&
                candidate.WorkId == workId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_SCHEDULE_NOT_FOUND");
        EnsureIssuer(schedule, actorUserId);
        return schedule;
    }

    private static void EnsureIssuer(
        DynamicFlowPeriodicSchedule schedule,
        string actorUserId)
    {
        if (schedule.IssuerUserId != actorUserId)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_ACTION_FORBIDDEN");
        }
    }

    private static void EnsureSchedulePins(
        DynamicFlowPeriodicSchedule schedule,
        DynamicFlowPreflightResponse preflight)
    {
        if (preflight.FlowPin.ArchetypeId !=
                DynamicFlowPeriodicTopologyContract.ArchetypeId ||
            preflight.FlowPin.FlowTemplateVersionId !=
                schedule.FlowTemplateVersionId ||
            preflight.FlowPin.PayloadHash != schedule.FlowPayloadHash ||
            preflight.FlowPin.TopologyHash !=
                schedule.TopologySnapshotHash ||
            preflight.FlowPin.CatalogVersion !=
                schedule.CatalogVersion ||
            preflight.FlowPin.CatalogSemanticHash !=
                schedule.CatalogSemanticHash ||
            preflight.FlowPin.DefinitionRevision !=
                schedule.DefinitionRevision ||
            preflight.ScheduleIdentityHash !=
                schedule.ScheduleIdentityHash)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_SCHEDULE_PIN_DRIFT");
        }
    }

    private static DynamicFlowPeriodicScheduleDto ToDto(
        DynamicFlowPeriodicSchedule schedule)
        => new()
        {
            ScheduleId = schedule.Id,
            WorkId = schedule.WorkId,
            FlowTemplateVersionId =
                schedule.FlowTemplateVersionId,
            FlowTemplateVersionNo =
                schedule.FlowTemplateVersionNo,
            ScheduleKey = schedule.ScheduleKey,
            TimeZoneId = schedule.TimeZoneId,
            NormalizedTimeZoneId =
                schedule.NormalizedTimeZoneId,
            Cadence = schedule.Cadence,
            LocalTime = schedule.LocalTime,
            PolicyVersion = schedule.PolicyVersion,
            ScheduleIdentityHash =
                schedule.ScheduleIdentityHash,
            NextDueAtUtc = schedule.NextDueAtUtc,
            State = schedule.State,
            Revision = schedule.Revision
        };

    private static DynamicFlowPeriodicOccurrenceDto ToDto(
        DynamicFlowPeriodicOccurrence occurrence)
        => new()
        {
            OccurrenceId = occurrence.Id,
            ScheduleId = occurrence.ScheduleId,
            WorkId = occurrence.WorkId,
            PeriodKey = occurrence.PeriodKey,
            TimeZoneId = occurrence.TimeZoneId,
            PolicyVersion = occurrence.PolicyVersion,
            ScheduledAtUtc = occurrence.ScheduledAtUtc,
            ObservedAtUtc = occurrence.ObservedAtUtc,
            State = occurrence.State,
            ReasonCode = occurrence.ReasonCode,
            FlowInstanceId = occurrence.FlowInstanceId,
            ManualCommandIds = occurrence.ManualCommandIds,
            Revision = occurrence.Revision
        };

    private static string CanonicalJson<T>(T value)
        => JsonSerializer.Serialize(value, Json);

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(
                   leftBytes,
                   rightBytes);
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

    private static void RequireObjectId(string? value, string field)
    {
        if (!ObjectId.TryParse(value?.Trim(), out _))
            throw new InvalidOperationException(
                $"DYNAMIC_FLOW_PERIODIC_{field.ToUpperInvariant()}_INVALID");
    }

    private static void RequireCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Trim().Length > 160)
        {
            throw new InvalidOperationException(
                "DYNAMIC_FLOW_PERIODIC_COMMAND_ID_INVALID");
        }
    }

    private static bool IsDuplicateKey(Exception error)
    {
        if (error is MongoWriteException write &&
            write.WriteError?.Category ==
                ServerErrorCategory.DuplicateKey)
            return true;
        if (error is MongoCommandException command &&
            command.Code == 11000)
            return true;
        return error.InnerException is not null &&
               IsDuplicateKey(error.InnerException);
    }
}
