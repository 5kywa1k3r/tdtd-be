using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowRuntimeReadService
{
    Task<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeInstanceListRow>> GetInstancesAsync(
        string workId,
        DynamicFlowRuntimeInstanceListQuery query,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowRuntimeInstanceOverview> GetInstanceAsync(
        string workId,
        string flowInstanceId,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>> GetStepsAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowRuntimePageQuery query,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>> GetInboxAsync(
        DynamicFlowRuntimeInboxQuery query,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeTimelineRow>> GetTimelineAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowRuntimePageQuery query,
        string actorUserId,
        CancellationToken ct = default);

    Task<DynamicFlowRuntimeRecoveryStatusDto> GetRecoveryStatusAsync(
        string workId,
        string flowInstanceId,
        string actorUserId,
        CancellationToken ct = default);
}

public sealed class DynamicFlowRuntimeReadService : IDynamicFlowRuntimeReadService
{
    private readonly MongoDbContext _ctx;
    private readonly IDynamicFlowRuntimeActivationPolicy _runtimeActivation;
    private readonly byte[] _cursorSigningKey;

    public DynamicFlowRuntimeReadService(
        MongoDbContext ctx,
        IConfiguration configuration,
        IDynamicFlowRuntimeActivationPolicy runtimeActivation)
    {
        _ctx = ctx;
        _runtimeActivation = runtimeActivation;
        var cursorSigningKey =
            configuration["DynamicFlowRuntime:CursorSigningKey"] ??
            configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(cursorSigningKey))
        {
            throw new InvalidOperationException(
                "Dynamic Flow runtime cursor signing key is missing.");
        }

        _cursorSigningKey = SHA256.HashData(
            Encoding.UTF8.GetBytes(cursorSigningKey));
    }

    public async Task<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeInstanceListRow>>
        GetInstancesAsync(
            string workId,
            DynamicFlowRuntimeInstanceListQuery query,
            string actorUserId,
            CancellationToken ct = default)
    {
        workId = NormalizeHiddenObjectId(workId);
        query ??= new DynamicFlowRuntimeInstanceListQuery();
        var limit = DynamicFlowRuntimeReadContract.RequireLimit(query.Limit);
        var actor = await RequireActorAsync(actorUserId, ct);
        if (string.IsNullOrWhiteSpace(actor.UnitId))
            return EmptyPage<DynamicFlowRuntimeInstanceListRow>(limit);

        var fb = Builders<DynamicFlowInstance>.Filter;
        var controlsIssuerUnit = ControlsIssuerUnit(actor, actor.UnitId);
        var filter =
            fb.Eq(x => x.WorkId, workId) &
            fb.Eq(x => x.IssuerUnitId, actor.UnitId) &
            fb.Eq(x => x.IsDeleted, false) &
            (controlsIssuerUnit
                ? fb.Empty
                : fb.Eq(x => x.IssuerUserId, actor.Id));
        if (!string.IsNullOrWhiteSpace(query.State))
        {
            filter &= fb.Eq(
                x => x.State,
                DynamicFlowRuntimeReadContract.RequireInstanceState(query.State));
        }

        var cursorScope = BuildCursorScope(
            "instances",
            actor.Id,
            actor.UnitId,
            controlsIssuerUnit ? "UNIT_CONTROLLER" : "EXACT_ISSUER",
            workId,
            query.State?.Trim().ToUpperInvariant());
        var cursor = DecodeProtectedUpdatedCursor(
            query.Cursor,
            DynamicFlowRuntimeReadContract.InstanceCursorKind,
            cursorScope);
        var anchor = cursor is null
            ? await _ctx.DynamicFlowInstances
                .Find(filter)
                .Sort(
                    Builders<DynamicFlowInstance>.Sort
                        .Descending(x => x.UpdatedAtUtc)
                        .Descending(x => x.Id))
                .Project(x => new { x.UpdatedAtUtc, x.Id })
                .FirstOrDefaultAsync(ct)
            : null;
        if (cursor is null && anchor is null)
        {
            return EmptyPage<DynamicFlowRuntimeInstanceListRow>(limit);
        }

        var anchorUpdatedAtUtc =
            cursor?.AnchorUpdatedAtUtc ?? anchor!.UpdatedAtUtc;
        var anchorId = cursor?.AnchorId ?? anchor!.Id;
        var anchoredFilter = filter & AtOrBeforeInstance(
            fb,
            anchorUpdatedAtUtc,
            anchorId!);
        var total = cursor?.Total ??
                    await _ctx.DynamicFlowInstances.CountDocumentsAsync(
                        anchoredFilter,
                        cancellationToken: ct);
        var pageFilter = cursor is null
            ? anchoredFilter
            : anchoredFilter & EarlierThanInstance(
                fb,
                cursor.UpdatedAtUtc!.Value,
                cursor.Id!);
        var rows = await _ctx.DynamicFlowInstances
            .Find(pageFilter)
            .Sort(
                Builders<DynamicFlowInstance>.Sort
                    .Descending(x => x.UpdatedAtUtc)
                    .Descending(x => x.Id))
            .Limit(limit + 1)
            .ToListAsync(ct);
        var hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);
        var attempts = await LoadInstanceRecoveryAttemptsAsync(
            rows.Select(x => x.Id),
            ct);

        return new DynamicFlowRuntimePageResponse<DynamicFlowRuntimeInstanceListRow>
        {
            Items = rows
                .Select(instance => MapIssuerInstanceRow(
                    instance,
                    attempts.GetValueOrDefault(
                        instance.Id,
                        RecoveryAttemptSnapshot.Empty)))
                .ToList(),
            Total = total,
            Limit = limit,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0
                ? EncodeProtectedUpdatedCursor(
                    DynamicFlowRuntimeReadContract.InstanceCursorKind,
                    rows[^1].UpdatedAtUtc,
                    rows[^1].Id,
                    anchorUpdatedAtUtc,
                    anchorId!,
                    total,
                    cursorScope)
                : null
        };
    }

    public async Task<DynamicFlowRuntimeInstanceOverview> GetInstanceAsync(
        string workId,
        string flowInstanceId,
        string actorUserId,
        CancellationToken ct = default)
    {
        workId = NormalizeHiddenObjectId(workId);
        flowInstanceId = NormalizeHiddenObjectId(flowInstanceId);
        var actor = await RequireActorAsync(actorUserId, ct);
        var scope = await ResolveScopeAsync(workId, flowInstanceId, actor, ct);
        var instance = await LoadVisibleInstanceAsync(workId, flowInstanceId, ct);
        var visibleStepCount = await _ctx.DynamicFlowStepInstances.CountDocumentsAsync(
            BuildVisibleStepFilter(flowInstanceId, scope),
            cancellationToken: ct);
        var gateways = await _ctx.DynamicFlowGatewayInstances
            .Find(gateway =>
                gateway.FlowInstanceId == flowInstanceId &&
                gateway.ExecutionEpoch == instance.ExecutionEpoch)
            .SortBy(gateway => gateway.GatewayInstanceId)
            .ThenBy(gateway => gateway.GatewayVersion)
            .ToListAsync(ct);
        var epochs = instance.ArchetypeId ==
                DynamicFlowFinalizeTopologyContract.ArchetypeId
            ? await _ctx.DynamicFlowExecutionEpochs
                .Find(epoch =>
                    epoch.FlowInstanceId == instance.Id &&
                    !epoch.IsDeleted)
                .SortByDescending(epoch => epoch.ExecutionEpoch)
                .ToListAsync(ct)
            : new List<DynamicFlowExecutionEpoch>();
        var attempts = await LoadRecoveryAttemptsAsync(
            flowInstanceId,
            scope,
            ct);
        var recovery = DynamicFlowRuntimeReadContract.BuildRecovery(
            instance.State,
            attempts.AttemptCount,
            attempts.LastAttemptAtUtc,
            attempts.NextAttemptAtUtc,
            HasActiveReconcileLease(instance),
            attempts.LastOutcome);
        AttachInstanceRevision(recovery, instance);
        var p6CandidatePinExecutionEnabled =
            _runtimeActivation.CanExecuteP6CandidatePin(
                instance.CatalogVersion,
                instance.CatalogSemanticHash,
                instance.ArchetypeId);
        return new DynamicFlowRuntimeInstanceOverview
        {
            WorkId = instance.WorkId,
            FlowInstanceId = instance.Id,
            FlowTemplateId = instance.FlowTemplateId,
            FlowTemplateVersionId = instance.FlowTemplateVersionId,
            FlowTemplateVersionNo = instance.FlowTemplateVersionNo,
            ArchetypeId = instance.ArchetypeId,
            DefinitionRevision = instance.DefinitionRevision,
            TopologySnapshotHash = instance.TopologySnapshotHash,
            ExecutionEpoch = instance.ExecutionEpoch,
            FinalizedExecutionEpoch = instance.FinalizedExecutionEpoch,
            FinalizedAtUtc = instance.FinalizedAtUtc,
            FinalizedByUserId = instance.FinalizedByUserId,
            FinalizedByEventId = instance.FinalizedByEventId,
            EntryFlowStepId = instance.EntryFlowStepId,
            ParentInstanceId = instance.ParentInstanceId,
            ParentStepInstanceId = instance.ParentStepInstanceId,
            RootInstanceId = instance.RootInstanceId,
            AncestryPath = instance.AncestryPath.ToList(),
            PeriodKey = instance.PeriodKey,
            ScheduleIdentityHash = instance.ScheduleIdentityHash,
            PeriodicScheduleId = instance.PeriodicScheduleId,
            PeriodicOccurrenceId = instance.PeriodicOccurrenceId,
            TimeZoneId = instance.TimeZoneId,
            SchedulePolicyVersion = instance.SchedulePolicyVersion,
            ParticipantSnapshotId = instance.ParticipantSnapshotId,
            State = instance.State,
            Revision = instance.Revision,
            RuntimeRecoveryEpoch = instance.RuntimeRecoveryEpoch,
            RevisionToken = DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(
                instance.Id,
                instance.Revision,
                instance.RuntimeRecoveryEpoch),
            VisibleStepCount = visibleStepCount,
            Gateways = gateways.Select(gateway =>
            {
                var arrived = gateway.ArrivedContributionIds
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                var arrivedSet = arrived.ToHashSet(StringComparer.Ordinal);
                var expected = gateway.ExpectedContributionIds
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                var cancelled = gateway.CancelledContributionIds
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                var cancelledSet = cancelled.ToHashSet(StringComparer.Ordinal);
                return new DynamicFlowRuntimeGatewayRow
                {
                    GatewayNodeId = gateway.GatewayNodeId,
                    GatewayInstanceId = gateway.GatewayInstanceId,
                    GatewayVersion = gateway.GatewayVersion,
                    GatewayKind = gateway.GatewayKind,
                    State = gateway.State,
                    IsCanonicalEpoch =
                        gateway.IsCanonicalEpoch != false,
                    InvalidatedByFlowEventId =
                        gateway.InvalidatedByFlowEventId,
                    InvalidatedAtUtc = gateway.InvalidatedAtUtc,
                    Revision = gateway.Revision,
                    DownstreamNodeId = gateway.DownstreamNodeId,
                    ExpectedContributionIds = expected,
                    ArrivedContributionIds = arrived,
                    MissingContributionIds = expected
                        .Where(id =>
                            !arrivedSet.Contains(id) &&
                            !cancelledSet.Contains(id))
                        .ToList(),
                    RequiredContributionCount =
                        gateway.RequiredContributionCount > 0
                            ? gateway.RequiredContributionCount
                            : expected.Count,
                    CancelledContributionIds = cancelled,
                    LateContributionIds = gateway.LateContributionIds
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToList(),
                    WinnerContributionId = gateway.WinnerContributionId,
                    InputSnapshotHash = gateway.InputSnapshotHash,
                    EvaluatorVersion = gateway.EvaluatorVersion,
                    SelectedEdgeId = gateway.SelectedEdgeId,
                    DecisionReasonCode = gateway.DecisionReasonCode,
                    ReleasedAtUtc = gateway.ReleasedAtUtc,
                    UpdatedAtUtc = gateway.UpdatedAtUtc
                };
            }).ToList(),
            Epochs = epochs.Select(epoch =>
                new DynamicFlowExecutionEpochDto
                {
                    EpochId = epoch.Id,
                    ExecutionEpoch = epoch.ExecutionEpoch,
                    State = epoch.State,
                    CheckpointNodeId = epoch.CheckpointNodeId,
                    IsCanonical = epoch.IsCanonical,
                    OpenedByCommandId = epoch.OpenedByCommandId,
                    ClosedByCommandId = epoch.ClosedByCommandId,
                    TerminalEventId = epoch.TerminalEventId,
                    ReplacedByExecutionEpoch =
                        epoch.ReplacedByExecutionEpoch,
                    OpenedAtUtc = epoch.OpenedAtUtc,
                    ClosedAtUtc = epoch.ClosedAtUtc
                }).ToList(),
            UpdatedAtUtc = instance.UpdatedAtUtc,
            VisibilityScopes = scope.VisibilityScopes,
            Capabilities = DynamicFlowRuntimeReadContract.BuildInstanceCapabilities(
                instance.Revision,
                scope.IsIssuer,
                canManageSupplemental:
                    scope.IsIssuer &&
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowSupplementalTopologyContract.ArchetypeId &&
                    instance.State == DynamicFlowInstanceStates.Active,
                canFinalize:
                    scope.IsIssuer &&
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                    instance.State == DynamicFlowInstanceStates.Completed,
                canRollback:
                    scope.IsIssuer &&
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                    instance.State is DynamicFlowInstanceStates.Active or
                        DynamicFlowInstanceStates.Completed,
                canTerminate:
                    scope.IsIssuer &&
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                    instance.State is DynamicFlowInstanceStates.Active or
                        DynamicFlowInstanceStates.Completed,
                canRestart:
                    scope.IsIssuer &&
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                    instance.State is DynamicFlowInstanceStates.Active or
                        DynamicFlowInstanceStates.Completed),
            Recovery = recovery
        };
    }

    public async Task<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>> GetStepsAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowRuntimePageQuery query,
        string actorUserId,
        CancellationToken ct = default)
    {
        workId = NormalizeHiddenObjectId(workId);
        flowInstanceId = NormalizeHiddenObjectId(flowInstanceId);
        query ??= new DynamicFlowRuntimePageQuery();
        var limit = DynamicFlowRuntimeReadContract.RequireLimit(query.Limit);
        var actor = await RequireActorAsync(actorUserId, ct);
        var scope = await ResolveScopeAsync(workId, flowInstanceId, actor, ct);
        var instance = await LoadVisibleInstanceAsync(workId, flowInstanceId, ct);
        var fb = Builders<DynamicFlowStepInstance>.Filter;
        var filter = BuildVisibleStepFilter(flowInstanceId, scope);
        var cursorScope = BuildCursorScope(
            "steps",
            actor.Id,
            workId,
            flowInstanceId);
        var cursor = DecodeProtectedStepCursor(query.Cursor, cursorScope);
        var total = cursor?.Total ??
                    await _ctx.DynamicFlowStepInstances.CountDocumentsAsync(
                        filter,
                        cancellationToken: ct);
        var pageFilter = cursor is null
            ? filter
            : filter & AfterStep(fb, cursor);
        var steps = await _ctx.DynamicFlowStepInstances
            .Find(pageFilter)
            .Sort(
                Builders<DynamicFlowStepInstance>.Sort
                    .Ascending(x => x.FlowStepId)
                    .Ascending(x => x.TargetUnitId)
                    .Ascending(x => x.AttemptNo))
            .Limit(limit + 1)
            .ToListAsync(ct);
        var hasMore = steps.Count > limit;
        if (hasMore)
            steps.RemoveAt(steps.Count - 1);
        var attempts = await LoadStepRecoveryAttemptsAsync(
            flowInstanceId,
            steps.Select(x => x.Id),
            ct);
        var downstreamAccess = await LoadDownstreamAccessAsync(
            steps,
            actor,
            new Dictionary<string, DynamicFlowInstance>(
                StringComparer.Ordinal)
            {
                [instance.Id] = instance
            },
            new Dictionary<string, DynamicFlowRuntimeReadScope>(
                StringComparer.Ordinal)
            {
                [instance.Id] = scope
            },
            ct);
        var forwardState = await LoadSequentialForwardReadStateAsync(
            steps,
            new Dictionary<string, DynamicFlowInstance>(
                StringComparer.Ordinal)
            {
                [instance.Id] = instance
            },
            actor.Id,
            ct);

        return new DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>
        {
            Items = steps
                .Select(step => MapStep(
                    instance,
                    step,
                    scope,
                    actor.Id,
                    attempts,
                    downstreamAccess,
                    forwardState,
                    _runtimeActivation.CanExecuteP6CandidatePin(
                        instance.CatalogVersion,
                        instance.CatalogSemanticHash,
                        instance.ArchetypeId)))
                .ToList(),
            Total = total,
            Limit = limit,
            HasMore = hasMore,
            NextCursor = hasMore && steps.Count > 0
                ? EncodeProtectedStepCursor(
                    steps[^1].FlowStepId,
                    steps[^1].TargetUnitId,
                    steps[^1].AttemptNo,
                    total,
                    cursorScope)
                : null
        };
    }

    public async Task<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>> GetInboxAsync(
        DynamicFlowRuntimeInboxQuery query,
        string actorUserId,
        CancellationToken ct = default)
    {
        query ??= new DynamicFlowRuntimeInboxQuery();
        var limit = DynamicFlowRuntimeReadContract.RequireLimit(query.Limit);
        var state = DynamicFlowRuntimeReadContract.RequireStepState(query.State);
        var actor = await RequireActorAsync(actorUserId, ct);
        var fb = Builders<DynamicFlowStepInstance>.Filter;
        var filter =
            fb.AnyEq(x => x.ParticipantUserIds, actor.Id) &
            fb.Eq(x => x.State, state) &
            fb.Eq(x => x.IsDeleted, false);
        var cursorScope = BuildCursorScope(
            "inbox",
            actor.Id,
            state);
        var cursor = DecodeProtectedUpdatedCursor(
            query.Cursor,
            DynamicFlowRuntimeReadContract.InboxCursorKind,
            cursorScope);
        var anchor = cursor is null
            ? await _ctx.DynamicFlowStepInstances
                .Find(filter)
                .Sort(
                    Builders<DynamicFlowStepInstance>.Sort
                        .Descending(x => x.UpdatedAtUtc)
                        .Descending(x => x.Id))
                .Project(x => new { x.UpdatedAtUtc, x.Id })
                .FirstOrDefaultAsync(ct)
            : null;
        if (cursor is null && anchor is null)
            return EmptyPage<DynamicFlowRuntimeStepRow>(limit);

        var anchorUpdatedAtUtc =
            cursor?.AnchorUpdatedAtUtc ?? anchor!.UpdatedAtUtc;
        var anchorId = cursor?.AnchorId ?? anchor!.Id;
        var anchoredFilter = filter & AtOrBeforeStep(
            fb,
            anchorUpdatedAtUtc,
            anchorId!);
        var parentLookup = BuildInboxParentLookupStage();
        var parentExists = BuildInboxParentExistsStage();
        long total;
        if (cursor?.Total is not null)
        {
            total = cursor.Total.Value;
        }
        else
        {
            var countDocument = await _ctx.DynamicFlowStepInstances
                .Aggregate()
                .Match(anchoredFilter)
                .AppendStage<BsonDocument>(parentLookup)
                .AppendStage<BsonDocument>(parentExists)
                .AppendStage<BsonDocument>(
                    new BsonDocument("$count", "value"))
                .FirstOrDefaultAsync(ct);
            total = countDocument?.GetValue("value", 0).ToInt64() ?? 0;
        }
        var pageFilter = cursor is null
            ? anchoredFilter
            : anchoredFilter & EarlierThanStep(
                fb,
                cursor.UpdatedAtUtc!.Value,
                cursor.Id!);
        var stepDocuments = await _ctx.DynamicFlowStepInstances
            .Aggregate()
            .Match(pageFilter)
            .Sort(
                Builders<DynamicFlowStepInstance>.Sort
                    .Descending(x => x.UpdatedAtUtc)
                    .Descending(x => x.Id))
            .AppendStage<BsonDocument>(parentLookup)
            .AppendStage<BsonDocument>(parentExists)
            .Limit(limit + 1)
            .ToListAsync(ct);
        var steps = stepDocuments
            .Select(document =>
                BsonSerializer.Deserialize<DynamicFlowStepInstance>(document))
            .ToList();
        var hasMore = steps.Count > limit;
        if (hasMore)
            steps.RemoveAt(steps.Count - 1);

        var instanceIds = steps
            .Select(x => x.FlowInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var instances = instanceIds.Length == 0
            ? new Dictionary<string, DynamicFlowInstance>(StringComparer.Ordinal)
            : (await _ctx.DynamicFlowInstances
                    .Find(x => instanceIds.Contains(x.Id) && !x.IsDeleted)
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, StringComparer.Ordinal);

        var attempts = await LoadStepRecoveryAttemptsAsync(
            null,
            steps.Select(x => x.Id),
            ct);
        var scopes = new Dictionary<
            string,
            DynamicFlowRuntimeReadScope>(StringComparer.Ordinal);
        foreach (var instance in instances.Values)
        {
            scopes[instance.Id] = await ResolveScopeAsync(
                instance.WorkId,
                instance.Id,
                actor,
                ct);
        }
        var downstreamAccess = await LoadDownstreamAccessAsync(
            steps,
            actor,
            instances,
            scopes,
            ct);
        var forwardState = await LoadSequentialForwardReadStateAsync(
            steps,
            instances,
            actor.Id,
            ct);
        var items = new List<DynamicFlowRuntimeStepRow>(steps.Count);
        foreach (var step in steps)
        {
            if (!instances.TryGetValue(step.FlowInstanceId, out var instance))
                continue;
            var scope = scopes[step.FlowInstanceId];
            items.Add(MapStep(
                instance,
                step,
                scope,
                actor.Id,
                attempts,
                downstreamAccess,
                forwardState,
                _runtimeActivation.CanExecuteP6CandidatePin(
                    instance.CatalogVersion,
                    instance.CatalogSemanticHash,
                    instance.ArchetypeId)));
        }

        return new DynamicFlowRuntimePageResponse<DynamicFlowRuntimeStepRow>
        {
            Items = items,
            Total = total,
            Limit = limit,
            HasMore = hasMore,
            NextCursor = hasMore && steps.Count > 0
                ? EncodeProtectedUpdatedCursor(
                    DynamicFlowRuntimeReadContract.InboxCursorKind,
                    steps[^1].UpdatedAtUtc,
                    steps[^1].Id,
                    anchorUpdatedAtUtc,
                    anchorId!,
                    total,
                    cursorScope)
                : null
        };
    }

    public async Task<DynamicFlowRuntimePageResponse<DynamicFlowRuntimeTimelineRow>>
        GetTimelineAsync(
            string workId,
            string flowInstanceId,
            DynamicFlowRuntimePageQuery query,
            string actorUserId,
            CancellationToken ct = default)
    {
        workId = NormalizeHiddenObjectId(workId);
        flowInstanceId = NormalizeHiddenObjectId(flowInstanceId);
        query ??= new DynamicFlowRuntimePageQuery();
        var limit = DynamicFlowRuntimeReadContract.RequireLimit(query.Limit);
        var actor = await RequireActorAsync(actorUserId, ct);
        var scope = await ResolveScopeAsync(workId, flowInstanceId, actor, ct);
        await LoadVisibleInstanceAsync(workId, flowInstanceId, ct);

        var fb = Builders<DynamicFlowRuntimeEvent>.Filter;
        var filter =
            fb.Eq(x => x.FlowInstanceId, flowInstanceId);
        if (!scope.IsIssuer)
        {
            var visibleStepIds = scope.VisibleStepIds
                .Select(value => (string?)value)
                .ToArray();
            var branchEvents = visibleStepIds.Length == 0
                ? fb.Empty
                : fb.In(x => x.StepInstanceId, visibleStepIds);
            // Instance-level events carry no branch payload on this API. Every
            // already-authorized branch viewer may see the aggregate envelope,
            // while affected refs are still reduced to that viewer's branches.
            var audienceFilters =
                new List<FilterDefinition<DynamicFlowRuntimeEvent>>();
            if (scope.VisibleTargetUnitIds.Count > 0)
            {
                audienceFilters.Add(
                    fb.AnyIn(
                        x => x.VisibleUnitIds,
                        scope.VisibleTargetUnitIds));
            }
            if (scope.AllowedRefs.Count > 0)
            {
                audienceFilters.Add(
                    fb.AnyIn(x => x.AffectedRefs, scope.AllowedRefs));
            }
            var visibleInstanceEvents = audienceFilters.Count == 0
                ? fb.Empty
                : fb.Eq(x => x.StepInstanceId, null) &
                  fb.Or(audienceFilters);
            filter &= fb.Or(branchEvents, visibleInstanceEvents);
        }

        var cursorScope = BuildCursorScope(
            "timeline",
            actor.Id,
            workId,
            flowInstanceId);
        var cursor = DecodeProtectedTimelineCursor(
            query.Cursor,
            cursorScope);
        var highWaterSequence = cursor?.HighWaterSequence ??
                                await _ctx.DynamicFlowRuntimeEvents
                                    .Find(filter)
                                    .SortByDescending(x => x.Sequence)
                                    .Project(x => x.Sequence)
                                    .FirstOrDefaultAsync(ct);
        if (highWaterSequence <= 0)
            return EmptyPage<DynamicFlowRuntimeTimelineRow>(limit);
        var anchoredFilter =
            filter &
            fb.Lte(x => x.Sequence, highWaterSequence);
        var total = cursor?.Total ??
                    await _ctx.DynamicFlowRuntimeEvents.CountDocumentsAsync(
                        anchoredFilter,
                        cancellationToken: ct);
        var pageFilter = cursor is null
            ? anchoredFilter
            : anchoredFilter &
              fb.Gt(x => x.Sequence, cursor.Sequence!.Value);
        var events = await _ctx.DynamicFlowRuntimeEvents
            .Find(pageFilter)
            .Sort(Builders<DynamicFlowRuntimeEvent>.Sort.Ascending(x => x.Sequence))
            .Limit(limit + 1)
            .ToListAsync(ct);
        var hasMore = events.Count > limit;
        if (hasMore)
            events.RemoveAt(events.Count - 1);
        var eventStepIds = events
            .Where(item => !string.IsNullOrWhiteSpace(item.StepInstanceId))
            .Select(item => item.StepInstanceId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var eventSteps = eventStepIds.Length == 0
            ? new Dictionary<string, DynamicFlowStepInstance>(StringComparer.Ordinal)
            : (await _ctx.DynamicFlowStepInstances
                    .Find(x =>
                        eventStepIds.Contains(x.Id) &&
                        x.FlowInstanceId == flowInstanceId &&
                        !x.IsDeleted)
                    .ToListAsync(ct))
                .ToDictionary(x => x.Id, StringComparer.Ordinal);
        var affectedReportIds = events
            .SelectMany(item => DynamicFlowRuntimeReadContract.ExtractAffectedRefIds(
                item.AffectedRefs,
                "report:"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlySet<string> allowedReportIds;
        if (scope.IsIssuer)
        {
            allowedReportIds = affectedReportIds.ToHashSet(
                StringComparer.Ordinal);
        }
        else if (affectedReportIds.Length == 0 ||
                 scope.VisibleAssignmentIds.Count == 0)
        {
            allowedReportIds = new HashSet<string>(StringComparer.Ordinal);
        }
        else
        {
            var visibleAssignmentIds = scope.VisibleAssignmentIds.ToArray();
            var reviewerAssignmentIds =
                scope.ReviewerAssignmentIds.ToArray();
            allowedReportIds = (await _ctx.WorkAssignmentReports
                    .Find(x =>
                        affectedReportIds.Contains(x.Id) &&
                        visibleAssignmentIds.Contains(x.WorkAssignmentId) &&
                        (reviewerAssignmentIds.Contains(x.WorkAssignmentId) ||
                         x.AssigneeUserId == scope.ActorUserId))
                    .Project(x => x.Id)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
        }

        return new DynamicFlowRuntimePageResponse<DynamicFlowRuntimeTimelineRow>
        {
            Items = events
                .Select(item => MapTimeline(
                    workId,
                    item,
                    scope,
                    item.StepInstanceId is not null &&
                    eventSteps.TryGetValue(item.StepInstanceId, out var step)
                        ? step
                        : null,
                    allowedReportIds))
                .ToList(),
            Total = total,
            Limit = limit,
            HasMore = hasMore,
            NextCursor = hasMore && events.Count > 0
                ? EncodeProtectedTimelineCursor(
                    events[^1].Sequence,
                    highWaterSequence,
                    total,
                    cursorScope)
                : null
        };
    }

    public async Task<DynamicFlowRuntimeRecoveryStatusDto> GetRecoveryStatusAsync(
        string workId,
        string flowInstanceId,
        string actorUserId,
        CancellationToken ct = default)
    {
        workId = NormalizeHiddenObjectId(workId);
        flowInstanceId = NormalizeHiddenObjectId(flowInstanceId);
        var actor = await RequireActorAsync(actorUserId, ct);
        var scope = await ResolveScopeAsync(workId, flowInstanceId, actor, ct);
        var instance = await LoadVisibleInstanceAsync(workId, flowInstanceId, ct);
        var attempts = await LoadRecoveryAttemptsAsync(
            flowInstanceId,
            scope,
            ct);
        var recovery = DynamicFlowRuntimeReadContract.BuildRecovery(
            instance.State,
            attempts.AttemptCount,
            attempts.LastAttemptAtUtc,
            attempts.NextAttemptAtUtc,
            HasActiveReconcileLease(instance),
            attempts.LastOutcome);
        AttachInstanceRevision(recovery, instance);
        return recovery;
    }

    private async Task<DynamicFlowRuntimeReadScope> ResolveScopeAsync(
        string workId,
        string flowInstanceId,
        AppUser actor,
        CancellationToken ct)
    {
        var instanceFilter = Builders<DynamicFlowInstance>.Filter;
        var instance = await _ctx.DynamicFlowInstances
            .Find(
                instanceFilter.Eq(x => x.Id, flowInstanceId) &
                instanceFilter.Eq(x => x.WorkId, workId) &
                instanceFilter.Eq(x => x.IsDeleted, false))
            .Project(x => new DynamicFlowInstance
            {
                Id = x.Id,
                IssuerUserId = x.IssuerUserId,
                IssuerUnitId = x.IssuerUnitId
            })
            .FirstOrDefaultAsync(ct);
        var exactIssuer =
            instance is not null &&
            string.Equals(
                instance.IssuerUserId,
                actor.Id,
                StringComparison.Ordinal);
        var issuerScope =
            instance is not null &&
            exactIssuer &&
            string.Equals(
                actor.UnitId,
                instance.IssuerUnitId,
                StringComparison.Ordinal) ||
            instance is not null &&
            ControlsIssuerUnit(actor, instance.IssuerUnitId);

        var stepFilter = Builders<DynamicFlowStepInstance>.Filter;
        var activeStepFilter =
            stepFilter.Eq(x => x.FlowInstanceId, flowInstanceId) &
            stepFilter.Eq(x => x.IsDeleted, false);
        var reporterSteps = await _ctx.DynamicFlowStepInstances
            .Find(
                activeStepFilter &
                stepFilter.AnyEq(x => x.ParticipantUserIds, actor.Id))
            .Project(x => new DynamicFlowStepInstance
            {
                Id = x.Id,
                TargetUnitId = x.TargetUnitId,
                AssignmentId = x.AssignmentId,
                ReportId = x.ReportId,
                ParticipantUserIds = x.ParticipantUserIds
            })
            .ToListAsync(ct);

        // Start reviewer resolution from actor-indexed DocRole projections.
        // The later step query binds those candidates to this exact instance,
        // so an outsider is denied before any branch-tree expansion.
        var reviewerCandidates = await _ctx.ReviewAssignmentSummaryDocRoles
            .Find(x =>
                x.ReviewerUserId == actor.Id &&
                x.WorkId == workId &&
                !x.IsDeleted)
            .Project(x => x.AssignmentId)
            .ToListAsync(ct);
        var projectedAssignerIds = reviewerCandidates.Count == 0
            ? []
            : await _ctx.DocRoles
                .Find(x =>
                    x.UserId == actor.Id &&
                    x.DocType == DocType.WORK_ASSIGNMENT &&
                    reviewerCandidates.Contains(x.DocId) &&
                    x.Role == DocRoleType.ASSIGNER &&
                    !x.IsDeleted)
                .Project(x => x.DocId)
                .ToListAsync(ct);
        // Review mutations use WorkAssignment.CreatedByUserId as their final
        // canonical guard. Include that source so a freshly materialized
        // assignment is reviewable before its list projections catch up.
        var creatorAssignmentIds = await _ctx.WorkAssignments
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.WorkId == workId &&
                x.CreatedByUserId == actor.Id &&
                !x.IsDeleted)
            .Project(x => x.Id)
            .ToListAsync(ct);
        var creatorSet = creatorAssignmentIds.ToHashSet(
            StringComparer.Ordinal);
        var reviewerAssignmentIds = creatorAssignmentIds
            .Concat(projectedAssignerIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var reviewerSteps = reviewerAssignmentIds.Count == 0
            ? new List<DynamicFlowStepInstance>()
            : await _ctx.DynamicFlowStepInstances
                .Find(
                    activeStepFilter &
                    stepFilter.In(
                        x => x.AssignmentId,
                        reviewerAssignmentIds.Select(value => (string?)value)))
                .Project(x => new DynamicFlowStepInstance
                {
                    Id = x.Id,
                    TargetUnitId = x.TargetUnitId,
                    AssignmentId = x.AssignmentId,
                    ReportId = x.ReportId,
                    ParticipantUserIds = x.ParticipantUserIds
                })
                .ToListAsync(ct);
        if (instance is null ||
            !issuerScope &&
            reporterSteps.Count == 0 &&
            reviewerSteps.Count == 0)
        {
            throw RuntimeNotFound();
        }

        return DynamicFlowRuntimeReadScope.Create(
            actor.Id,
            actor.UnitId,
            issuerScope,
            reporterSteps,
            reviewerSteps,
            reviewerAssignmentIds,
            creatorSet);
    }

    private FilterDefinition<DynamicFlowStepInstance> BuildVisibleStepFilter(
        string flowInstanceId,
        DynamicFlowRuntimeReadScope scope)
    {
        var fb = Builders<DynamicFlowStepInstance>.Filter;
        var filter =
            fb.Eq(x => x.FlowInstanceId, flowInstanceId) &
            fb.Eq(x => x.IsDeleted, false);
        if (scope.IsIssuer)
            return filter;

        var visibilityFilters = new List<FilterDefinition<DynamicFlowStepInstance>>
        {
            fb.AnyEq(x => x.ParticipantUserIds, scope.ActorUserId)
        };
        if (scope.ReviewerAssignmentIds.Count > 0)
        {
            visibilityFilters.Add(
                fb.In(
                    x => x.AssignmentId,
                    scope.ReviewerAssignmentIds.Select(value => (string?)value)));
        }

        return filter & fb.Or(visibilityFilters);
    }

    private async Task<DynamicFlowInstance> LoadVisibleInstanceAsync(
        string workId,
        string flowInstanceId,
        CancellationToken ct)
        => await _ctx.DynamicFlowInstances
               .Find(x =>
                   x.Id == flowInstanceId &&
                   x.WorkId == workId &&
                   !x.IsDeleted)
               .FirstOrDefaultAsync(ct)
           ?? throw RuntimeNotFound();

    private async Task<AppUser> RequireActorAsync(
        string actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorUserId) ||
            !ObjectId.TryParse(actorUserId, out _))
        {
            throw AppExceptionFactory.Unauthorized();
        }

        return await _ctx.Users
                   .Find(x => x.Id == actorUserId && !x.IsDeleted)
                   .FirstOrDefaultAsync(ct)
               ?? throw AppExceptionFactory.Unauthorized();
    }

    private static bool ControlsIssuerUnit(AppUser actor, string issuerUnitId)
    {
        if (string.IsNullOrWhiteSpace(issuerUnitId) ||
            !string.Equals(actor.UnitId, issuerUnitId, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(
                actor.AccountKind,
                ManagementAccountKind.UnitManager,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return actor.Roles.Any(role =>
            Roles.IsManagerUnit(role, out var managedUnitId) &&
            string.Equals(managedUnitId, issuerUnitId, StringComparison.Ordinal));
    }

    private async Task<DynamicFlowRuntimeDownstreamAccess>
        LoadDownstreamAccessAsync(
            IEnumerable<DynamicFlowStepInstance> steps,
            AppUser actor,
            IReadOnlyDictionary<string, DynamicFlowInstance> instances,
            IReadOnlyDictionary<string, DynamicFlowRuntimeReadScope> scopes,
            CancellationToken ct)
    {
        var values = steps.ToArray();
        var assignmentIds = values
            .Select(step => step.AssignmentId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (assignmentIds.Length == 0)
            return DynamicFlowRuntimeDownstreamAccess.Empty;

        var fb = Builders<AssignmentListDocRole>.Filter;
        var readableAssignmentIds = (await _ctx.AssignmentListDocRoles
                .Find(
                    fb.In(x => x.AssignmentId, assignmentIds) &
                    fb.Eq(x => x.UserId, actor.Id) &
                    fb.Eq(x => x.IsDeleted, false) &
                    fb.Where(x => x.Roles.Any()) &
                    DynamicFlowBranchVisibility.BuildAssignmentListFilter(
                        fb,
                        actor.UnitId))
                .Project(x => x.AssignmentId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var periodKeys = instances.Values
            .Select(instance => instance.PeriodKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var reportRows = periodKeys.Length == 0
            ? []
            : await _ctx.WorkAssignmentReports
                .Find(x =>
                    assignmentIds.Contains(x.WorkAssignmentId) &&
                    periodKeys.Contains(x.PeriodKey) &&
                    x.IsCurrent &&
                    x.IsActive &&
                    !x.IsDeleted)
                .Project(x => new DynamicFlowRuntimeReportAccessRef
                {
                    ReportId = x.Id,
                    AssignmentId = x.WorkAssignmentId,
                    PeriodKey = x.PeriodKey,
                    AssigneeUserId = x.AssigneeUserId,
                    Status = x.Status,
                    UpdatedAtUtc = x.UpdatedAtUtc
                })
                .ToListAsync(ct);
        var primaryReportIds =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var reportIdsByStep =
            new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.Ordinal);
        var submittableReportIds =
            new Dictionary<string, string>(StringComparer.Ordinal);
        var reviewableReportIds =
            new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var step in values)
        {
            if (string.IsNullOrWhiteSpace(step.AssignmentId) ||
                !instances.TryGetValue(
                    step.FlowInstanceId,
                    out var instance) ||
                !scopes.TryGetValue(
                    step.FlowInstanceId,
                    out var scope))
            {
                continue;
            }

            var stepScopes = scope.ScopesFor(step, actor.Id);
            var isIssuer = stepScopes.Contains(
                DynamicFlowRuntimeVisibilityScopes.Issuer,
                StringComparer.Ordinal);
            var isReporter = stepScopes.Contains(
                DynamicFlowRuntimeVisibilityScopes.Reporter,
                StringComparer.Ordinal);
            var isReviewer = stepScopes.Contains(
                DynamicFlowRuntimeVisibilityScopes.Reviewer,
                StringComparer.Ordinal);
            var authorizedReports = reportRows
                .Where(report =>
                    string.Equals(
                        report.AssignmentId,
                        step.AssignmentId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        report.PeriodKey,
                        instance.PeriodKey,
                        StringComparison.Ordinal) &&
                    (isIssuer ||
                     isReviewer ||
                     isReporter &&
                     string.Equals(
                         report.AssigneeUserId,
                         actor.Id,
                         StringComparison.Ordinal)))
                .OrderByDescending(report => report.UpdatedAtUtc)
                .ThenByDescending(
                    report => report.ReportId,
                    StringComparer.Ordinal)
                .ToArray();
            reportIdsByStep[step.Id] = authorizedReports
                .Select(report => report.ReportId)
                .ToList();

            if (!readableAssignmentIds.Contains(step.AssignmentId))
                continue;
            var openableReports = authorizedReports
                .Where(report =>
                    isReviewer ||
                    string.Equals(
                        report.AssigneeUserId,
                        actor.Id,
                        StringComparison.Ordinal))
                .ToArray();
            var primary = isReporter
                ? openableReports.FirstOrDefault(report =>
                    string.Equals(
                        report.AssigneeUserId,
                        actor.Id,
                        StringComparison.Ordinal))
                : isReviewer
                    ? openableReports
                        .OrderByDescending(report =>
                            report.Status ==
                            WorkAssignmentReportStatus.Submitted)
                        .ThenByDescending(report => report.UpdatedAtUtc)
                        .ThenByDescending(
                            report => report.ReportId,
                            StringComparer.Ordinal)
                        .FirstOrDefault()
                    : null;
            if (primary is not null)
                primaryReportIds[step.Id] = primary.ReportId;

            var submitTarget = isReporter
                ? openableReports.FirstOrDefault(report =>
                    string.Equals(
                        report.AssigneeUserId,
                        actor.Id,
                        StringComparison.Ordinal) &&
                    report.Status == WorkAssignmentReportStatus.Draft)
                : null;
            if (submitTarget is not null)
            {
                submittableReportIds[step.Id] = submitTarget.ReportId;
            }
            var reviewTarget =
                isReviewer &&
                scope.ReviewMutationAssignmentIds.Contains(
                    step.AssignmentId)
                    ? openableReports.FirstOrDefault(report =>
                        report.Status ==
                        WorkAssignmentReportStatus.Submitted)
                    : null;
            if (reviewTarget is not null)
            {
                reviewableReportIds[step.Id] = reviewTarget.ReportId;
            }
        }

        return new DynamicFlowRuntimeDownstreamAccess(
            readableAssignmentIds,
            primaryReportIds,
            reportIdsByStep,
            submittableReportIds,
            reviewableReportIds);
    }

    private async Task<RecoveryAttemptSnapshot> LoadRecoveryAttemptsAsync(
        string flowInstanceId,
        DynamicFlowRuntimeReadScope scope,
        CancellationToken ct)
    {
        IReadOnlyCollection<string> stepInstanceIds;
        if (scope.IsIssuer)
        {
            stepInstanceIds = await _ctx.DynamicFlowStepInstances
                .Find(x =>
                    x.FlowInstanceId == flowInstanceId &&
                    !x.IsDeleted)
                .Project(x => x.Id)
                .ToListAsync(ct);
        }
        else
        {
            stepInstanceIds = scope.VisibleStepIds.ToArray();
        }

        var rows = await LoadOutboxAttemptRowsAsync(
            stepInstanceIds,
            flowInstanceId,
            ct);
        var lastOutcome = await _ctx.DynamicFlowRuntimeEvents
            .Find(x =>
                x.FlowInstanceId == flowInstanceId &&
                x.StepInstanceId == null &&
                (x.EventType == DynamicFlowRuntimeEventTypes.RecoveryStarted ||
                 x.EventType == DynamicFlowRuntimeEventTypes.RecoveryReconciled ||
                 x.EventType == DynamicFlowRuntimeEventTypes.RecoveryCompleted))
            .SortByDescending(x => x.Sequence)
            .Project(x => x.EventType)
            .FirstOrDefaultAsync(ct);
        return RecoveryAttemptSnapshot.From(rows, lastOutcome);
    }

    private async Task<IReadOnlyDictionary<string, RecoveryAttemptSnapshot>>
        LoadInstanceRecoveryAttemptsAsync(
            IEnumerable<string> flowInstanceIds,
            CancellationToken ct)
    {
        var instanceIds = flowInstanceIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (instanceIds.Length == 0)
        {
            return new Dictionary<string, RecoveryAttemptSnapshot>(
                StringComparer.Ordinal);
        }

        var stepRefs = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                instanceIds.Contains(x.FlowInstanceId) &&
                !x.IsDeleted)
            .Project(x => new DynamicFlowRuntimeStepRef
            {
                FlowInstanceId = x.FlowInstanceId,
                StepInstanceId = x.Id
            })
            .ToListAsync(ct);
        var rows = await LoadOutboxAttemptRowsAsync(
            stepRefs.Select(item => item.StepInstanceId),
            null,
            ct);
        var outcomes = await _ctx.DynamicFlowRuntimeEvents
            .Find(x =>
                instanceIds.Contains(x.FlowInstanceId) &&
                x.StepInstanceId == null &&
                (x.EventType == DynamicFlowRuntimeEventTypes.RecoveryStarted ||
                 x.EventType == DynamicFlowRuntimeEventTypes.RecoveryReconciled ||
                 x.EventType == DynamicFlowRuntimeEventTypes.RecoveryCompleted))
            .Project(x => new DynamicFlowRuntimeRecoveryOutcomeRef
            {
                FlowInstanceId = x.FlowInstanceId,
                Sequence = x.Sequence,
                EventType = x.EventType
            })
            .ToListAsync(ct);
        var latestOutcomes = outcomes
            .GroupBy(item => item.FlowInstanceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Sequence)
                    .First()
                    .EventType,
                StringComparer.Ordinal);
        return instanceIds.ToDictionary(
            instanceId => instanceId,
            instanceId => RecoveryAttemptSnapshot.From(
                rows.Where(row => string.Equals(
                    row.FlowInstanceId,
                    instanceId,
                    StringComparison.Ordinal)),
                latestOutcomes.GetValueOrDefault(instanceId)),
            StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, RecoveryAttemptSnapshot>>
        LoadStepRecoveryAttemptsAsync(
            string? flowInstanceId,
            IEnumerable<string> stepInstanceIds,
            CancellationToken ct)
    {
        var ids = stepInstanceIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<string, RecoveryAttemptSnapshot>(
                StringComparer.Ordinal);
        }

        var rows = await LoadOutboxAttemptRowsAsync(
            ids,
            flowInstanceId,
            ct);
        return ids.ToDictionary(
            id => id,
            id => RecoveryAttemptSnapshot.From(
                rows.Where(row =>
                    string.Equals(
                        row.StepInstanceId,
                        id,
                        StringComparison.Ordinal))),
            StringComparer.Ordinal);
    }

    private async Task<List<DynamicFlowRuntimeOutboxItem>>
        LoadOutboxAttemptRowsAsync(
            IEnumerable<string> stepInstanceIds,
            string? flowInstanceId,
            CancellationToken ct)
    {
        var outboxIds = stepInstanceIds
            .Distinct(StringComparer.Ordinal)
            .Select(StableMaterializationOutboxId)
            .ToArray();
        if (outboxIds.Length == 0)
            return [];

        var result = new List<DynamicFlowRuntimeOutboxItem>();
        foreach (var batch in outboxIds.Chunk(500))
        {
            var fb = Builders<DynamicFlowRuntimeOutboxItem>.Filter;
            var filter = fb.In(x => x.Id, batch);
            if (!string.IsNullOrWhiteSpace(flowInstanceId))
                filter &= fb.Eq(x => x.FlowInstanceId, flowInstanceId);
            result.AddRange(await _ctx.DynamicFlowRuntimeOutbox
                .Find(filter)
                .Project(x => new DynamicFlowRuntimeOutboxItem
                {
                    FlowInstanceId = x.FlowInstanceId,
                    StepInstanceId = x.StepInstanceId,
                    AttemptCount = x.AttemptCount,
                    Status = x.Status,
                    NextAttemptAtUtc = x.NextAttemptAtUtc,
                    UpdatedAtUtc = x.UpdatedAtUtc
                })
                .ToListAsync(ct));
        }

        return result;
    }

    private DynamicFlowRuntimeInstanceListRow MapIssuerInstanceRow(
        DynamicFlowInstance instance,
        RecoveryAttemptSnapshot attempt)
    {
        var recovery = DynamicFlowRuntimeReadContract.BuildRecovery(
            instance.State,
            attempt.AttemptCount,
            attempt.LastAttemptAtUtc,
            attempt.NextAttemptAtUtc,
            HasActiveReconcileLease(instance),
            attempt.LastOutcome);
        AttachInstanceRevision(recovery, instance);
        var p6CandidatePinExecutionEnabled =
            _runtimeActivation.CanExecuteP6CandidatePin(
                instance.CatalogVersion,
                instance.CatalogSemanticHash,
                instance.ArchetypeId);
        return new()
        {
            WorkId = instance.WorkId,
            FlowInstanceId = instance.Id,
            FlowTemplateId = instance.FlowTemplateId,
            FlowTemplateVersionId = instance.FlowTemplateVersionId,
            FlowTemplateVersionNo = instance.FlowTemplateVersionNo,
            ArchetypeId = instance.ArchetypeId,
            DefinitionRevision = instance.DefinitionRevision,
            TopologySnapshotHash = instance.TopologySnapshotHash,
            ExecutionEpoch = instance.ExecutionEpoch,
            FinalizedExecutionEpoch = instance.FinalizedExecutionEpoch,
            FinalizedAtUtc = instance.FinalizedAtUtc,
            EntryFlowStepId = instance.EntryFlowStepId,
            ParentInstanceId = instance.ParentInstanceId,
            ParentStepInstanceId = instance.ParentStepInstanceId,
            RootInstanceId = instance.RootInstanceId,
            AncestryPath = instance.AncestryPath.ToList(),
            PeriodKey = instance.PeriodKey,
            ScheduleIdentityHash = instance.ScheduleIdentityHash,
            PeriodicScheduleId = instance.PeriodicScheduleId,
            PeriodicOccurrenceId = instance.PeriodicOccurrenceId,
            TimeZoneId = instance.TimeZoneId,
            SchedulePolicyVersion = instance.SchedulePolicyVersion,
            State = instance.State,
            Revision = instance.Revision,
            RuntimeRecoveryEpoch = instance.RuntimeRecoveryEpoch,
            RevisionToken = DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(
                instance.Id,
                instance.Revision,
                instance.RuntimeRecoveryEpoch),
            UpdatedAtUtc = instance.UpdatedAtUtc,
            VisibilityScopes = [DynamicFlowRuntimeVisibilityScopes.Issuer],
            Capabilities = DynamicFlowRuntimeReadContract.BuildInstanceCapabilities(
                instance.Revision,
                isIssuer: true,
                canManageSupplemental:
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowSupplementalTopologyContract.ArchetypeId &&
                    instance.State == DynamicFlowInstanceStates.Active,
                canFinalize:
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                    instance.State == DynamicFlowInstanceStates.Completed,
                canRollback:
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                    instance.State is DynamicFlowInstanceStates.Active or
                        DynamicFlowInstanceStates.Completed,
                canTerminate:
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                    instance.State is DynamicFlowInstanceStates.Active or
                        DynamicFlowInstanceStates.Completed,
                canRestart:
                    p6CandidatePinExecutionEnabled &&
                    instance.ArchetypeId ==
                        DynamicFlowFinalizeTopologyContract.ArchetypeId &&
                    instance.State is DynamicFlowInstanceStates.Active or
                        DynamicFlowInstanceStates.Completed),
            Recovery = recovery
        };
    }

    private async Task<DynamicFlowSequentialForwardReadState>
        LoadSequentialForwardReadStateAsync(
            IReadOnlyCollection<DynamicFlowStepInstance> steps,
            IReadOnlyDictionary<string, DynamicFlowInstance> instances,
            string actorUserId,
            CancellationToken ct)
    {
        if (steps.Count == 0)
        {
            return DynamicFlowSequentialForwardReadState.Empty;
        }
        var parallelForkEligible = await LoadParallelForkForwardEligibleAsync(
            steps,
            instances,
            actorUserId,
            ct);

        var topologies =
            new Dictionary<string, DynamicFlowSequentialTopology>(
                StringComparer.Ordinal);
        foreach (var instance in instances.Values)
        {
            if (instance.State != DynamicFlowInstanceStates.Active ||
                instance.ArchetypeId !=
                DynamicFlowSequentialTopologyContract.ArchetypeId ||
                !_runtimeActivation.CanExecuteP6CandidatePin(
                    instance.CatalogVersion,
                    instance.CatalogSemanticHash,
                    instance.ArchetypeId))
            {
                continue;
            }

            try
            {
                var topology = DynamicFlowSequentialTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash);
                if (instance.TopologySnapshotHash == instance.FlowPayloadHash &&
                    instance.DefinitionRevision ==
                    DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                        instance.FlowTemplateVersionId,
                        instance.FlowTemplateVersionNo,
                        instance.FlowPayloadHash))
                {
                    topologies[instance.Id] = topology;
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                JsonException)
            {
                // Read capabilities fail closed on any immutable-pin drift.
            }
        }

        var snapshotIds = topologies.Keys
            .Select(instanceId => instances[instanceId].ParticipantSnapshotId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var participantSnapshots = snapshotIds.Length == 0
            ? new Dictionary<string, DynamicFlowParticipantSnapshot>(
                StringComparer.Ordinal)
            : (await _ctx.DynamicFlowParticipantSnapshots
                .Find(snapshot => snapshotIds.Contains(snapshot.Id))
                .ToListAsync(ct))
            .ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);

        var candidateByExpectedChildId =
            new Dictionary<string, DynamicFlowStepInstance>(
                StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!instances.TryGetValue(step.FlowInstanceId, out var instance) ||
                !topologies.TryGetValue(instance.Id, out var topology) ||
                !participantSnapshots.TryGetValue(
                    instance.ParticipantSnapshotId,
                    out var participantSnapshot) ||
                !MatchesSequentialStepPins(instance, step, topology) ||
                !MatchesSequentialParticipantPins(
                    instance,
                    step,
                    participantSnapshot,
                    actorUserId) ||
                step.NextNodeIds.Count != 1 ||
                string.IsNullOrWhiteSpace(step.AssignmentId))
            {
                continue;
            }

            var expectedChildId =
                DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
                    instance.Id,
                    instance.ExecutionEpoch,
                    step.NextNodeIds[0],
                    step.BranchId,
                    1);
            if (!candidateByExpectedChildId.TryAdd(expectedChildId, step))
            {
                // Duplicate derivation is itself identity drift.
                candidateByExpectedChildId.Remove(expectedChildId);
            }
        }
        if (candidateByExpectedChildId.Count == 0)
            return new DynamicFlowSequentialForwardReadState(parallelForkEligible);

        var assignmentIds = candidateByExpectedChildId.Values
            .Select(step => step.AssignmentId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var assignments = await _ctx.WorkAssignments
            .Find(x =>
                assignmentIds.Contains(x.Id) &&
                x.FlowEffectiveStatus ==
                DynamicFlowEffectiveStatuses.Effective &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var assignmentById = assignments.ToDictionary(
            assignment => assignment.Id,
            StringComparer.Ordinal);
        var consumedChildIds = (await _ctx.DynamicFlowStepInstances
                .Find(x =>
                    candidateByExpectedChildId.Keys.Contains(x.Id) &&
                    !x.IsDeleted)
                .Project(x => x.Id)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var eligible = candidateByExpectedChildId
            .Where(pair =>
                !consumedChildIds.Contains(pair.Key) &&
                pair.Value.AssignmentId is not null &&
                assignmentById.TryGetValue(
                    pair.Value.AssignmentId,
                    out var assignment) &&
                instances.TryGetValue(
                    pair.Value.FlowInstanceId,
                    out var instance) &&
                MatchesSequentialAssignment(
                    instance,
                    pair.Value,
                    assignment) &&
                CanActorControlSequentialAssignment(
                    assignment,
                    actorUserId))
            .Select(pair => pair.Value.Id)
            .ToHashSet(StringComparer.Ordinal);
        eligible.UnionWith(parallelForkEligible);
        return new DynamicFlowSequentialForwardReadState(eligible);
    }

    private async Task<HashSet<string>> LoadParallelForkForwardEligibleAsync(
        IReadOnlyCollection<DynamicFlowStepInstance> steps,
        IReadOnlyDictionary<string, DynamicFlowInstance> instances,
        string actorUserId,
        CancellationToken ct)
    {
        var topologies =
            new Dictionary<string, DynamicFlowParallelForkTopology>(
                StringComparer.Ordinal);
        foreach (var instance in instances.Values)
        {
            if (instance.State != DynamicFlowInstanceStates.Active ||
                (instance.ArchetypeId !=
                    DynamicFlowParallelForkTopologyContract.ArchetypeId &&
                 instance.ArchetypeId !=
                    DynamicFlowJoinAllTopologyContract.ArchetypeId &&
                 instance.ArchetypeId !=
                    DynamicFlowJoinQuorumTopologyContract.ArchetypeId &&
                 instance.ArchetypeId !=
                    DynamicFlowTypedConditionalTopologyContract.ArchetypeId) ||
                !_runtimeActivation.CanExecuteP6CandidatePin(
                    instance.CatalogVersion,
                    instance.CatalogSemanticHash,
                    instance.ArchetypeId))
            {
                continue;
            }
            try
            {
                var topology = RequireParallelFanOutTopology(instance);
                if (instance.TopologySnapshotHash == instance.FlowPayloadHash &&
                    instance.DefinitionRevision ==
                    DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                        instance.FlowTemplateVersionId,
                        instance.FlowTemplateVersionNo,
                        instance.FlowPayloadHash))
                {
                    topologies[instance.Id] = topology;
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                JsonException)
            {
                // Capabilities fail closed on immutable topology drift.
            }
        }
        if (topologies.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var snapshotIds = topologies.Keys
            .Select(id => instances[id].ParticipantSnapshotId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var snapshots = (await _ctx.DynamicFlowParticipantSnapshots
                .Find(snapshot => snapshotIds.Contains(snapshot.Id))
                .ToListAsync(ct))
            .ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        var candidates = steps
            .Where(step =>
                topologies.TryGetValue(step.FlowInstanceId, out var topology) &&
                instances.TryGetValue(step.FlowInstanceId, out var instance) &&
                snapshots.TryGetValue(instance.ParticipantSnapshotId, out var snapshot) &&
                MatchesParallelForkEntryPins(instance, step, topology!) &&
                MatchesSequentialParticipantPins(
                    instance,
                    step,
                    snapshot,
                    actorUserId) &&
                step.AssignmentId is not null)
            .ToList();
        if (candidates.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var assignments = (await _ctx.WorkAssignments
                .Find(assignment =>
                    candidates.Select(step => step.AssignmentId!).Contains(assignment.Id) &&
                    assignment.FlowEffectiveStatus ==
                    DynamicFlowEffectiveStatuses.Effective &&
                    !assignment.IsDeleted)
                .ToListAsync(ct))
            .ToDictionary(assignment => assignment.Id, StringComparer.Ordinal);
        var expectedChildIds = candidates
            .SelectMany(step =>
            {
                var instance = instances[step.FlowInstanceId];
                var topology = topologies[instance.Id];
                var gateway =
                    DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        topology.ForkNode.NodeId,
                        step.BranchId);
                return topology.Branches.Select(branch =>
                {
                    var branchId =
                        DynamicFlowParallelForkTopologyContract.BuildBranchId(
                            instance.Id,
                            instance.ExecutionEpoch,
                            gateway,
                            branch.Edge.TransitionId,
                            step.TargetUnitId);
                    return DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        branch.Node.NodeId,
                        branchId,
                        1);
                });
            })
            .ToArray();
        var consumed = (await _ctx.DynamicFlowStepInstances
                .Find(step => expectedChildIds.Contains(step.Id) && !step.IsDeleted)
                .Project(step => step.FlowInstanceId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(step =>
                !consumed.Contains(step.FlowInstanceId) &&
                assignments.TryGetValue(step.AssignmentId!, out var assignment) &&
                MatchesSequentialAssignment(
                    instances[step.FlowInstanceId],
                    step,
                    assignment) &&
                assignment.IsFlowFinalNode != true &&
                CanActorControlSequentialAssignment(
                    assignment,
                    actorUserId))
            .Select(step => step.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool MatchesParallelForkEntryPins(
        DynamicFlowInstance instance,
        DynamicFlowStepInstance step,
        DynamicFlowParallelForkTopology topology)
    {
        var branchId =
            DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        var stepId =
            DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                topology.EntryNode.NodeId,
                branchId,
                1);
        return step.Id == stepId &&
               step.FlowStepId == topology.EntryNode.NodeId &&
               step.FlowStepCode == topology.EntryNode.NodeCode &&
               step.StepOrder == 1 &&
               step.FormNodeId == topology.EntryForm.FormNodeId &&
               step.FormFamilyId == topology.EntryForm.DynamicFormFamilyId &&
               step.FormVersionId == topology.EntryForm.DynamicFormTemplateId &&
               step.FormVersionNo == topology.EntryForm.DynamicFormVersionNo &&
               step.FormSchemaHash == topology.EntryForm.DynamicFormSchemaHash &&
               step.FormSnapshotHash == topology.EntryForm.DynamicFormSnapshotHash &&
               step.ExecutionEpoch == instance.ExecutionEpoch &&
               step.AttemptNo == 1 &&
               step.BranchId == branchId &&
               step.AssignmentId ==
               DynamicFlowParallelForkTopologyContract.BuildAssignmentId(stepId) &&
               step.GatewayInstanceId is null &&
               step.GatewayVersion is null &&
               step.ContributionId is null &&
               step.NextNodeIds.SequenceEqual(
                   new[] { topology.ForkNode.NodeId },
                   StringComparer.Ordinal) &&
               !step.IsTerminalNode;
    }

    private static DynamicFlowParallelForkTopology RequireParallelFanOutTopology(
        DynamicFlowInstance instance)
        => instance.ArchetypeId switch
        {
            DynamicFlowParallelForkTopologyContract.ArchetypeId =>
                DynamicFlowParallelForkTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash),
            DynamicFlowJoinAllTopologyContract.ArchetypeId =>
                DynamicFlowJoinAllTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash),
            DynamicFlowJoinQuorumTopologyContract.ArchetypeId =>
                DynamicFlowJoinQuorumTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash),
            DynamicFlowTypedConditionalTopologyContract.ArchetypeId =>
                DynamicFlowTypedConditionalTopologyContract.Require(
                    instance.TopologySnapshotJson,
                    instance.TopologySnapshotHash),
            _ => throw new InvalidOperationException(
                "Unsupported parallel fan-out archetype.")
        };

    private static bool MatchesSequentialStepPins(
        DynamicFlowInstance instance,
        DynamicFlowStepInstance step,
        DynamicFlowSequentialTopology topology)
    {
        var ordered = topology.OrderedNodes
            .Select((node, index) => new { Node = node, Order = index + 1 })
            .SingleOrDefault(item => item.Node.NodeId == step.FlowStepId);
        if (ordered is null ||
            !topology.FormsByNodeId.TryGetValue(
                ordered.Node.NodeId,
                out var form))
        {
            return false;
        }

        var expectedNextNodeIds =
            topology.OutgoingByNodeId.TryGetValue(
                ordered.Node.NodeId,
                out var outgoing)
                ? new[] { outgoing.ToNodeId }
                : Array.Empty<string>();
        var incoming = topology.OutgoingByNodeId.Values.SingleOrDefault(edge =>
            edge.ToNodeId == ordered.Node.NodeId);
        var branchId =
            DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        var stepId =
            DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                ordered.Node.NodeId,
                branchId,
                step.AttemptNo);
        var expectedResultOwnerIdentity =
            DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                instance.Id,
                instance.ExecutionEpoch,
                topology.Payload.ResultOwnerStepId ??
                ordered.Node.NodeId,
                branchId,
                "result");
        var expectedStatisticOwnerIdentity =
            DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                instance.Id,
                instance.ExecutionEpoch,
                topology.Payload.StatisticsOwnerStepId ??
                ordered.Node.NodeId,
                branchId,
                "statistics");
        return step.ExecutionEpoch == instance.ExecutionEpoch &&
               step.FlowInstanceId == instance.Id &&
               step.DefinitionRevision == instance.DefinitionRevision &&
               step.StepOrder == ordered.Order &&
               step.FlowStepCode == ordered.Node.NodeCode &&
               step.FormNodeId == form.FormNodeId &&
               step.FormFamilyId == form.DynamicFormFamilyId &&
               step.FormVersionId == form.DynamicFormTemplateId &&
               step.FormVersionNo == form.DynamicFormVersionNo &&
               step.FormSchemaHash == form.DynamicFormSchemaHash &&
               step.FormSnapshotHash == form.DynamicFormSnapshotHash &&
               step.AttemptNo == 1 &&
               step.BranchId == branchId &&
               step.Id == stepId &&
               step.AssignmentId ==
               DynamicFlowSequentialTopologyContract.BuildAssignmentId(stepId) &&
               step.ParticipantSnapshotId == instance.ParticipantSnapshotId &&
               step.NextNodeIds.SequenceEqual(
                   expectedNextNodeIds,
                   StringComparer.Ordinal) &&
               step.IsTerminalNode == (expectedNextNodeIds.Length == 0) &&
               step.ActivatedByTransitionId == incoming?.TransitionId &&
               step.ResultOwnerIdentity == expectedResultOwnerIdentity &&
               step.StatisticOwnerIdentity ==
               expectedStatisticOwnerIdentity;
    }

    private static bool MatchesSequentialAssignment(
        DynamicFlowInstance instance,
        DynamicFlowStepInstance step,
        WorkAssignment assignment)
        => assignment.Id == step.AssignmentId &&
           assignment.WorkId == instance.WorkId &&
           assignment.FlowTemplateId == instance.FlowTemplateId &&
           assignment.FlowTemplateVersionNo ==
           instance.FlowTemplateVersionNo &&
           assignment.FlowInstanceId == step.FlowInstanceId &&
           assignment.FlowStepId == step.FlowStepId &&
           assignment.FlowStepCode == step.FlowStepCode &&
           assignment.FlowStepOrder == step.StepOrder &&
           assignment.FlowBranchId == step.BranchId &&
           assignment.FlowAttemptNo == step.AttemptNo &&
           assignment.FlowRole == DynamicFlowRuntimePlanner.AssignmentFlowRole &&
           assignment.DynamicFormTemplateId == step.FormVersionId &&
           assignment.DynamicFormFamilyId == step.FormFamilyId &&
           assignment.DynamicFormVersionNo == step.FormVersionNo &&
           assignment.DynamicFormSchemaHash == step.FormSchemaHash &&
           assignment.TargetUnitIds is { Count: 1 } &&
           assignment.TargetUnitIds[0] == step.TargetUnitId &&
           assignment.IsFlowFinalNode == step.IsTerminalNode;

    private static bool MatchesSequentialParticipantPins(
        DynamicFlowInstance instance,
        DynamicFlowStepInstance step,
        DynamicFlowParticipantSnapshot snapshot,
        string actorUserId)
    {
        var binding = snapshot.Bindings.SingleOrDefault(item =>
            string.Equals(
                item.TargetUnitId,
                step.TargetUnitId,
                StringComparison.Ordinal));
        if (binding is null)
            return false;
        var participantIds = binding.Participants
            .Select(user => user.UserId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var assigneeIds = binding.AssigneeUserIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var stepParticipantIds = step.ParticipantUserIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return snapshot.Id == instance.ParticipantSnapshotId &&
               snapshot.FlowInstanceId == instance.Id &&
               snapshot.IssuerUserId == instance.IssuerUserId &&
               snapshot.IssuerUnitId == instance.IssuerUnitId &&
               snapshot.SnapshotHash == instance.ParticipantSnapshotHash &&
               SequentialParticipantSnapshotHash(snapshot) ==
               instance.ParticipantSnapshotHash &&
               snapshot.SourceRevisionTokens.TryGetValue(
                   "catalog",
                   out var catalog) &&
               catalog == instance.CatalogSemanticHash &&
               snapshot.SourceRevisionTokens.TryGetValue(
                   "flow",
                   out var flow) &&
               flow == instance.FlowPayloadHash &&
               snapshot.SourceRevisionTokens.TryGetValue(
                   "topology",
                   out var topology) &&
               topology == instance.TopologySnapshotHash &&
               participantIds.Length > 0 &&
               participantIds.Distinct(StringComparer.Ordinal).Count() ==
               participantIds.Length &&
               participantIds.SequenceEqual(
                   assigneeIds,
                   StringComparer.Ordinal) &&
               participantIds.SequenceEqual(
                   stepParticipantIds,
                   StringComparer.Ordinal) &&
               (participantIds.Contains(
                    actorUserId,
                    StringComparer.Ordinal) ||
                instance.IssuerUserId == actorUserId);
    }

    private static bool CanActorControlSequentialAssignment(
        WorkAssignment assignment,
        string actorUserId)
        => assignment.CreatedByUserId == actorUserId ||
           (assignment.LeaderWatcherUserIds ?? [])
           .Contains(actorUserId, StringComparer.Ordinal) ||
           (assignment.Assignees ?? [])
           .Any(user =>
               user.UserId == actorUserId);

    private static string SequentialParticipantSnapshotHash(
        DynamicFlowParticipantSnapshot snapshot)
    {
        var sourceTokens = new BsonDocument(snapshot.SourceRevisionTokens
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BsonElement(pair.Key, pair.Value)));
        var bindings = new BsonArray(snapshot.Bindings
            .OrderBy(binding => binding.TargetUnitId, StringComparer.Ordinal)
            .Select(binding => new BsonDocument
            {
                { "targetUnitId", binding.TargetUnitId },
                {
                    "assigneeUserIds",
                    new BsonArray(binding.AssigneeUserIds.OrderBy(
                        value => value,
                        StringComparer.Ordinal))
                },
                {
                    "participants",
                    new BsonArray(binding.Participants
                        .OrderBy(user => user.UserId, StringComparer.Ordinal)
                        .Select(SequentialParticipantDocument))
                },
                {
                    "roleCodes",
                    new BsonArray(binding.RoleCodes.OrderBy(
                        value => value,
                        StringComparer.Ordinal))
                }
            }));
        return DynamicFlowSequentialTopologyContract.Hash(new BsonDocument
        {
            { "flowInstanceId", snapshot.FlowInstanceId },
            { "issuerUserId", snapshot.IssuerUserId },
            { "issuerUnitId", snapshot.IssuerUnitId },
            { "bindings", bindings },
            { "sourceRevisionTokens", sourceTokens }
        }.ToJson());
    }

    private static BsonDocument SequentialParticipantDocument(
        DynamicFlowParticipantUserSnapshot user)
        => new()
        {
            { "userId", user.UserId },
            { "username", user.Username },
            { "fullName", user.FullName },
            { "unitId", user.UnitId },
            { "unitSymbol", BsonValue.Create(user.UnitSymbol) },
            { "unitShortName", BsonValue.Create(user.UnitShortName) },
            { "unitName", BsonValue.Create(user.UnitName) },
            { "positionCode", BsonValue.Create(user.PositionCode) },
            { "positionName", BsonValue.Create(user.PositionName) }
        };

    private static DynamicFlowRuntimeStepRow MapStep(
        DynamicFlowInstance instance,
        DynamicFlowStepInstance step,
        DynamicFlowRuntimeReadScope scope,
        string actorUserId,
        IReadOnlyDictionary<string, RecoveryAttemptSnapshot> attempts,
        DynamicFlowRuntimeDownstreamAccess downstreamAccess,
        DynamicFlowSequentialForwardReadState forwardState,
        bool p6CandidatePinExecutionEnabled)
    {
        var stepScopes = scope.ScopesFor(step, actorUserId);
        attempts.TryGetValue(step.Id, out var attempt);
        attempt ??= RecoveryAttemptSnapshot.Empty;
        var isIssuer = stepScopes.Contains(
            DynamicFlowRuntimeVisibilityScopes.Issuer,
            StringComparer.Ordinal);
        var isReporter = stepScopes.Contains(
            DynamicFlowRuntimeVisibilityScopes.Reporter,
            StringComparer.Ordinal);
        var isReviewer = stepScopes.Contains(
            DynamicFlowRuntimeVisibilityScopes.Reviewer,
            StringComparer.Ordinal);
        var canOpenAssignment =
            !string.IsNullOrWhiteSpace(step.AssignmentId) &&
            downstreamAccess.AssignmentIds.Contains(step.AssignmentId);
        downstreamAccess.PrimaryReportIds.TryGetValue(
            step.Id,
            out var primaryReportId);
        var reportIds = downstreamAccess.ReportIdsByStep.GetValueOrDefault(
            step.Id,
            []);
        var canOpenReport =
            !string.IsNullOrWhiteSpace(primaryReportId);
        downstreamAccess.SubmittableReportIds.TryGetValue(
            step.Id,
            out var submitReportId);
        downstreamAccess.ReviewableReportIds.TryGetValue(
            step.Id,
            out var reviewReportId);
        var isCanonicalAttempt =
            step.SupersededByStepInstanceId is null &&
            step.ExecutionEpoch == instance.ExecutionEpoch &&
            step.IsCanonicalEpoch != false;
        var canLaunchSubflow =
            instance.ArchetypeId ==
                DynamicFlowSubflowTopologyContract.ArchetypeId &&
            instance.State == DynamicFlowInstanceStates.Active &&
            step.State == DynamicFlowStepStates.Approved &&
            step.ChildInstanceId is null &&
            (isIssuer || isReporter) &&
            p6CandidatePinExecutionEnabled;
        var canSubmitReport =
            isCanonicalAttempt &&
            !string.IsNullOrWhiteSpace(submitReportId);
        var canReviewReport =
            isCanonicalAttempt &&
            !string.IsNullOrWhiteSpace(reviewReportId);
        int? maxReviewCycles = null;
        if (instance.ArchetypeId ==
            DynamicFlowReviewLoopTopologyContract.ArchetypeId)
        {
            try
            {
                maxReviewCycles =
                    DynamicFlowReviewLoopTopologyContract.Require(
                            instance.TopologySnapshotJson,
                            instance.TopologySnapshotHash)
                        .MaxReviewCycles;
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                JsonException)
            {
                // Immutable topology drift fails capabilities closed while the
                // historical row remains readable.
                isCanonicalAttempt = false;
            }
        }
        if (canLaunchSubflow)
        {
            try
            {
                var topology =
                    DynamicFlowSubflowTopologyContract.Require(
                        instance.TopologySnapshotJson,
                        instance.TopologySnapshotHash);
                canLaunchSubflow =
                    topology.ParentNode.NodeId ==
                    step.FlowStepId;
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                ArgumentException or
                JsonException)
            {
                canLaunchSubflow = false;
            }
        }
        var recovery = DynamicFlowRuntimeReadContract.BuildRecovery(
            step.State,
            attempt.AttemptCount,
            attempt.LastAttemptAtUtc,
            attempt.NextAttemptAtUtc,
            HasActiveReconcileLease(instance),
            attempt.LastOutcome);
        recovery.ExpectedRevision = step.Revision;
        recovery.RevisionToken =
            DynamicFlowRuntimeReadContract.BuildStepRevisionToken(
                step.Id,
                step.Revision);
        return new DynamicFlowRuntimeStepRow
        {
            WorkId = instance.WorkId,
            FlowInstanceId = instance.Id,
            FlowStepDefinitionId = step.FlowStepId,
            FlowStepCode = step.FlowStepCode,
            StepOrder = step.StepOrder,
            ExecutionEpoch = step.ExecutionEpoch,
            DefinitionRevision = step.DefinitionRevision,
            StepInstanceId = step.Id,
            BranchId = step.BranchId,
            ParentBranchId =
                (instance.ArchetypeId ==
                    DynamicFlowParallelForkTopologyContract.ArchetypeId ||
                 instance.ArchetypeId ==
                    DynamicFlowJoinAllTopologyContract.ArchetypeId ||
                 instance.ArchetypeId ==
                    DynamicFlowJoinQuorumTopologyContract.ArchetypeId ||
                 instance.ArchetypeId ==
                    DynamicFlowTypedConditionalTopologyContract.ArchetypeId) &&
                step.GatewayInstanceId is not null
                    ? DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        step.TargetUnitId)
                    : null,
            AttemptNo = step.AttemptNo,
            ReviewCycleNo = step.ReviewCycleNo,
            MaxReviewCycles = maxReviewCycles,
            PreviousAttemptStepInstanceId =
                step.PreviousAttemptStepInstanceId,
            PreviousAttemptAssignmentId =
                step.PreviousAttemptAssignmentId,
            SupersededByStepInstanceId =
                step.SupersededByStepInstanceId,
            IsCanonicalAttempt = isCanonicalAttempt,
            ActivatedByTransitionId = step.ActivatedByTransitionId,
            GatewayInstanceId = step.GatewayInstanceId,
            GatewayVersion = step.GatewayVersion,
            ContributionId = step.ContributionId,
            NextNodeIds = step.NextNodeIds.ToList(),
            IsTerminalNode = step.IsTerminalNode,
            IsCanonicalEpoch =
                step.ExecutionEpoch == instance.ExecutionEpoch &&
                step.IsCanonicalEpoch != false,
            InvalidatedByFlowEventId =
                step.InvalidatedByFlowEventId,
            InvalidatedAtUtc = step.InvalidatedAtUtc,
            IsSupplemental = step.IsSupplemental,
            SupplementalStepId = step.SupplementalStepId,
            RequestedByUserId = step.RequestedByUserId,
            CompletionRequired = step.CompletionRequired,
            IsSupplementalCancelled =
                step.IsSupplemental &&
                step.State == DynamicFlowStepStates.CancelledByGateway,
            SupplementalCancelledAtUtc =
                step.SupplementalCancelledAtUtc,
            SupplementalCancelledByUserId =
                step.SupplementalCancelledByUserId,
            SupplementalCancelReason =
                step.SupplementalCancelReason,
            ResultOwnerIdentity = step.ResultOwnerIdentity,
            StatisticOwnerIdentity = step.StatisticOwnerIdentity,
            TargetUnitId = step.TargetUnitId,
            AssignmentId = step.AssignmentId,
            ChildInstanceId = step.ChildInstanceId,
            ChildFlowTemplateId = step.ChildFlowTemplateId,
            ChildFlowVersionId = step.ChildFlowVersionId,
            ChildState = step.ChildState,
            ChildOutcomeCode = step.ChildOutcomeCode,
            ChildLinkedAtUtc = step.ChildLinkedAtUtc,
            ReportId = primaryReportId,
            ReportIds = reportIds.ToList(),
            SubmitReportId = submitReportId,
            ReviewReportId = reviewReportId,
            FormNodeId = step.FormNodeId,
            FormFamilyId = step.FormFamilyId,
            FormVersionId = step.FormVersionId,
            FormVersionNo = step.FormVersionNo,
            FormSchemaHash = step.FormSchemaHash,
            FormSnapshotHash = step.FormSnapshotHash,
            State = step.State,
            Revision = step.Revision,
            RevisionToken = DynamicFlowRuntimeReadContract.BuildStepRevisionToken(
                step.Id,
                step.Revision),
            UpdatedAtUtc = step.UpdatedAtUtc,
            VisibilityScopes = stepScopes,
            Capabilities = DynamicFlowRuntimeReadContract.BuildStepCapabilities(
                step.Revision,
                step.State,
                isIssuer,
                isReporter && canOpenAssignment,
                isReviewer && canReviewReport,
                canOpenAssignment,
                canOpenReport,
                canSubmitReport && isCanonicalAttempt,
                canReviewReport && isCanonicalAttempt,
                canForward:
                    isCanonicalAttempt &&
                    forwardState.EligibleParentStepIds.Contains(step.Id) &&
                    (step.State is DynamicFlowStepStates.Approved or
                        DynamicFlowStepStates.Completed),
                canLaunchSubflow:
                    isCanonicalAttempt &&
                    canLaunchSubflow,
                canCancelSupplemental:
                    step.IsSupplemental &&
                    isIssuer &&
                    instance.State == DynamicFlowInstanceStates.Active &&
                    step.ExecutionEpoch == instance.ExecutionEpoch &&
                    step.State is not (
                        DynamicFlowStepStates.Completed or
                        DynamicFlowStepStates.CancelledByGateway or
                        DynamicFlowStepStates.Failed or
                        DynamicFlowStepStates.Terminated)),
            Recovery = recovery
        };
    }

    private static DynamicFlowRuntimeTimelineRow MapTimeline(
        string workId,
        DynamicFlowRuntimeEvent item,
        DynamicFlowRuntimeReadScope scope,
        DynamicFlowStepInstance? step,
        IReadOnlySet<string> allowedReportIds)
    {
        var affectedRefs = scope.FilterAffectedRefs(
            item.AffectedRefs,
            allowedReportIds.Select(value => $"report:{value}"));
        var eventAssignmentId =
            DynamicFlowRuntimeReadContract.ExtractFirstAffectedRefId(
                affectedRefs,
                "assignment:");
        var eventReportId =
            DynamicFlowRuntimeReadContract.ExtractFirstAffectedRefId(
                affectedRefs,
                "report:");
        return new()
        {
            WorkId = workId,
            FlowInstanceId = item.FlowInstanceId,
            EventId = item.Id,
            StepInstanceId = item.StepInstanceId,
            FlowStepDefinitionId = step?.FlowStepId,
            ExecutionEpoch = item.ExecutionEpoch,
            BranchId = item.BranchId ?? step?.BranchId,
            GatewayInstanceId = item.GatewayInstanceId ?? step?.GatewayInstanceId,
            GatewayVersion = item.GatewayVersion ?? step?.GatewayVersion,
            ContributionId = item.ContributionId ?? step?.ContributionId,
            AttemptNo = step?.AttemptNo,
            ReviewCycleNo = step?.ReviewCycleNo,
            TargetUnitId = step?.TargetUnitId,
            AssignmentId = eventAssignmentId ?? step?.AssignmentId,
            ReportId = eventReportId,
            FormVersionId = step?.FormVersionId,
            FormVersionNo = step?.FormVersionNo,
            Sequence = item.Sequence,
            EventType = DynamicFlowRuntimeReadContract.SanitizeEventType(
                item.EventType),
            CommandId = item.CommandId,
            FromState = item.FromState,
            ToState = item.ToState,
            FromRevision = item.FromRevision,
            ToRevision = item.ToRevision,
            ReasonCode = DynamicFlowRuntimeReadContract.SanitizeReasonCode(
                item.ReasonCode),
            ActorUserId = item.ActorUserId,
            AffectedRefs = affectedRefs,
            OccurredAtUtc = item.OccurredAtUtc
        };
    }

    private static void AttachInstanceRevision(
        DynamicFlowRuntimeRecoveryStatusDto recovery,
        DynamicFlowInstance instance)
    {
        recovery.ExpectedRevision = instance.Revision;
        recovery.RevisionToken =
            DynamicFlowRuntimeReadContract.BuildInstanceRevisionToken(
                instance.Id,
                instance.Revision,
                instance.RuntimeRecoveryEpoch);
        // P5-D1 does not freeze a public retry/reconcile command envelope.
        recovery.CanRetry = false;
        recovery.CanReconcile = false;
    }

    private static FilterDefinition<DynamicFlowInstance> EarlierThanInstance(
        FilterDefinitionBuilder<DynamicFlowInstance> fb,
        DateTime updatedAtUtc,
        string id)
        => fb.Lt(x => x.UpdatedAtUtc, updatedAtUtc) |
           (fb.Eq(x => x.UpdatedAtUtc, updatedAtUtc) &
            fb.Lt(x => x.Id, id));

    private static FilterDefinition<DynamicFlowInstance> AtOrBeforeInstance(
        FilterDefinitionBuilder<DynamicFlowInstance> fb,
        DateTime updatedAtUtc,
        string id)
        => fb.Lt(x => x.UpdatedAtUtc, updatedAtUtc) |
           (fb.Eq(x => x.UpdatedAtUtc, updatedAtUtc) &
            fb.Lte(x => x.Id, id));

    private static FilterDefinition<DynamicFlowStepInstance> EarlierThanStep(
        FilterDefinitionBuilder<DynamicFlowStepInstance> fb,
        DateTime updatedAtUtc,
        string id)
        => fb.Lt(x => x.UpdatedAtUtc, updatedAtUtc) |
           (fb.Eq(x => x.UpdatedAtUtc, updatedAtUtc) &
            fb.Lt(x => x.Id, id));

    private static FilterDefinition<DynamicFlowStepInstance> AtOrBeforeStep(
        FilterDefinitionBuilder<DynamicFlowStepInstance> fb,
        DateTime updatedAtUtc,
        string id)
        => fb.Lt(x => x.UpdatedAtUtc, updatedAtUtc) |
           (fb.Eq(x => x.UpdatedAtUtc, updatedAtUtc) &
            fb.Lte(x => x.Id, id));

    private static FilterDefinition<DynamicFlowStepInstance> AfterStep(
        FilterDefinitionBuilder<DynamicFlowStepInstance> fb,
        DynamicFlowRuntimeCursor cursor)
    {
        var stepId = cursor.StepId!;
        var targetUnitId = cursor.TargetUnitId!;
        var attemptNo = cursor.AttemptNo!.Value;
        return fb.Gt(x => x.FlowStepId, stepId) |
               (fb.Eq(x => x.FlowStepId, stepId) &
                fb.Gt(x => x.TargetUnitId, targetUnitId)) |
               (fb.Eq(x => x.FlowStepId, stepId) &
                fb.Eq(x => x.TargetUnitId, targetUnitId) &
                fb.Gt(x => x.AttemptNo, attemptNo));
    }

    private static bool HasActiveReconcileLease(DynamicFlowInstance instance)
        => !string.IsNullOrWhiteSpace(instance.RuntimeReconcileLeaseId) &&
           instance.RuntimeReconcileLeaseExpiresAtUtc > DateTime.UtcNow;

    private BsonDocument BuildInboxParentLookupStage()
        => new(
            "$lookup",
            new BsonDocument
            {
                {
                    "from",
                    _ctx.DynamicFlowInstances.CollectionNamespace.CollectionName
                },
                { "localField", "flowInstanceId" },
                { "foreignField", "_id" },
                { "as", "__runtimeParent" }
            });

    private static BsonDocument BuildInboxParentExistsStage()
        => new(
            "$match",
            new BsonDocument(
                "__runtimeParent",
                new BsonDocument(
                    "$elemMatch",
                    new BsonDocument("isDeleted", false))));

    private string EncodeProtectedUpdatedCursor(
        string kind,
        DateTime updatedAtUtc,
        string id,
        DateTime anchorUpdatedAtUtc,
        string anchorId,
        long total,
        string scope)
        => ProtectCursor(
            DynamicFlowRuntimeReadContract.EncodeUpdatedCursor(
                kind,
                updatedAtUtc,
                id,
                anchorUpdatedAtUtc,
                anchorId,
                total,
                scope));

    private DynamicFlowRuntimeCursor? DecodeProtectedUpdatedCursor(
        string? value,
        string kind,
        string scope)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DynamicFlowRuntimeReadContract.DecodeUpdatedCursor(
                UnprotectCursor(value),
                kind,
                scope);

    private string EncodeProtectedStepCursor(
        string stepId,
        string targetUnitId,
        int attemptNo,
        long total,
        string scope)
        => ProtectCursor(
            DynamicFlowRuntimeReadContract.EncodeStepCursor(
                stepId,
                targetUnitId,
                attemptNo,
                total,
                scope));

    private DynamicFlowRuntimeCursor? DecodeProtectedStepCursor(
        string? value,
        string scope)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DynamicFlowRuntimeReadContract.DecodeStepCursor(
                UnprotectCursor(value),
                scope);

    private string EncodeProtectedTimelineCursor(
        long sequence,
        long highWaterSequence,
        long total,
        string scope)
        => ProtectCursor(
            DynamicFlowRuntimeReadContract.EncodeTimelineCursor(
                sequence,
                highWaterSequence,
                total,
                scope));

    private DynamicFlowRuntimeCursor? DecodeProtectedTimelineCursor(
        string? value,
        string scope)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DynamicFlowRuntimeReadContract.DecodeTimelineCursor(
                UnprotectCursor(value),
                scope);

    private string UnprotectCursor(string value)
    {
        var separator = value.LastIndexOf('.');
        if (separator <= 0 || separator == value.Length - 1)
        {
            DynamicFlowRuntimeReadContract.RequireScopedCursor(false);
        }

        var payload = value[..separator];
        byte[] suppliedSignature;
        try
        {
            suppliedSignature = DecodeBase64Url(value[(separator + 1)..]);
        }
        catch (FormatException)
        {
            DynamicFlowRuntimeReadContract.RequireScopedCursor(false);
            throw;
        }
        var expectedSignature = HMACSHA256.HashData(
            _cursorSigningKey,
            Encoding.UTF8.GetBytes(payload));
        DynamicFlowRuntimeReadContract.RequireScopedCursor(
            suppliedSignature.Length == expectedSignature.Length &&
            CryptographicOperations.FixedTimeEquals(
                suppliedSignature,
                expectedSignature));
        return payload;
    }

    private string ProtectCursor(string payload)
    {
        var signature = HMACSHA256.HashData(
            _cursorSigningKey,
            Encoding.UTF8.GetBytes(payload));
        return $"{payload}.{EncodeBase64Url(signature)}";
    }

    private static string EncodeBase64Url(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        value = value
            .Replace('-', '+')
            .Replace('_', '/');
        value = (value.Length % 4) switch
        {
            2 => $"{value}==",
            3 => $"{value}=",
            0 => value,
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Convert.FromBase64String(value);
    }

    private static string BuildCursorScope(params string?[] parts)
    {
        var canonical = string.Join(
            "\n",
            parts.Select(value =>
            {
                value ??= string.Empty;
                return $"{value.Length}:{value}";
            }));
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string StableMaterializationOutboxId(string stepInstanceId)
        => Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"{stepInstanceId}\noutbox\nmaterialize")))
            .ToLowerInvariant()[..24];

    private static DynamicFlowRuntimePageResponse<T> EmptyPage<T>(int limit)
        => new()
        {
            Limit = limit,
            Total = 0,
            HasMore = false
        };

    private sealed record DynamicFlowSequentialForwardReadState(
        IReadOnlySet<string> EligibleParentStepIds)
    {
        public static readonly DynamicFlowSequentialForwardReadState Empty =
            new(new HashSet<string>(StringComparer.Ordinal));
    }

    private static string NormalizeHiddenObjectId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            !ObjectId.TryParse(value, out _))
        {
            throw RuntimeNotFound();
        }

        return value;
    }

    private static AppException RuntimeNotFound()
        => AppExceptionFactory.NotFound(
            AppErrorCode.COMMON_NOT_FOUND,
            new { reason = "DYNAMIC_FLOW_RUNTIME_NOT_FOUND" });
}

internal static class DynamicFlowRuntimeVisibilityScopes
{
    public const string Issuer = "ISSUER";
    public const string Reporter = "REPORTER";
    public const string Reviewer = "REVIEWER";
}

internal sealed class DynamicFlowRuntimeReadScope
{
    private readonly HashSet<string> _reporterStepIds;
    private readonly HashSet<string> _reviewerStepIds;
    private readonly HashSet<string> _allowedRefs;

    private DynamicFlowRuntimeReadScope(
        string actorUserId,
        string? actorUnitId,
        bool isIssuer,
        IEnumerable<string> reporterStepIds,
        IEnumerable<string> reviewerStepIds,
        IEnumerable<string> reviewerAssignmentIds,
        IEnumerable<string> reviewMutationAssignmentIds,
        IEnumerable<string> visibleTargetUnitIds,
        IEnumerable<string> visibleAssignmentIds,
        IEnumerable<string> allowedRefs)
    {
        ActorUserId = actorUserId;
        ActorUnitId = actorUnitId;
        IsIssuer = isIssuer;
        _reporterStepIds = reporterStepIds.ToHashSet(StringComparer.Ordinal);
        _reviewerStepIds = reviewerStepIds.ToHashSet(StringComparer.Ordinal);
        ReviewerAssignmentIds = reviewerAssignmentIds
            .ToHashSet(StringComparer.Ordinal);
        ReviewMutationAssignmentIds = reviewMutationAssignmentIds
            .ToHashSet(StringComparer.Ordinal);
        VisibleStepIds = _reporterStepIds
            .Concat(_reviewerStepIds)
            .ToHashSet(StringComparer.Ordinal);
        VisibleTargetUnitIds = visibleTargetUnitIds
            .ToHashSet(StringComparer.Ordinal);
        VisibleAssignmentIds = visibleAssignmentIds
            .ToHashSet(StringComparer.Ordinal);
        _allowedRefs = allowedRefs.ToHashSet(StringComparer.Ordinal);
        VisibilityScopes = new[]
            {
                isIssuer
                    ? DynamicFlowRuntimeVisibilityScopes.Issuer
                    : null,
                _reporterStepIds.Count > 0
                    ? DynamicFlowRuntimeVisibilityScopes.Reporter
                    : null,
                _reviewerStepIds.Count > 0
                    ? DynamicFlowRuntimeVisibilityScopes.Reviewer
                    : null
            }
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
    }

    public string ActorUserId { get; }
    public string? ActorUnitId { get; }
    public bool IsIssuer { get; }
    public IReadOnlySet<string> VisibleStepIds { get; }
    public IReadOnlySet<string> VisibleTargetUnitIds { get; }
    public IReadOnlySet<string> VisibleAssignmentIds { get; }
    public IReadOnlySet<string> ReviewerAssignmentIds { get; }
    public IReadOnlySet<string> ReviewMutationAssignmentIds { get; }
    public IReadOnlySet<string> AllowedRefs => _allowedRefs;
    public List<string> VisibilityScopes { get; }

    public static DynamicFlowRuntimeReadScope Issuer(
        string actorUserId,
        string? actorUnitId)
        => new(
            actorUserId,
            actorUnitId,
            true,
            [],
            [],
            [],
            [],
            [],
            [],
            []);

    public static DynamicFlowRuntimeReadScope Reporter(
        string actorUserId,
        string? actorUnitId,
        DynamicFlowStepInstance step)
        => BranchScoped(
            actorUserId,
            actorUnitId,
            [step],
            [],
            []);

    public static DynamicFlowRuntimeReadScope BranchScoped(
        string actorUserId,
        string? actorUnitId,
        IEnumerable<DynamicFlowStepInstance> reporterSteps,
        IEnumerable<DynamicFlowStepInstance> reviewerSteps,
        IEnumerable<string> reviewerAssignmentIds)
        => Create(
            actorUserId,
            actorUnitId,
            false,
            reporterSteps,
            reviewerSteps,
            reviewerAssignmentIds,
            reviewerAssignmentIds);

    public static DynamicFlowRuntimeReadScope Create(
        string actorUserId,
        string? actorUnitId,
        bool isIssuer,
        IEnumerable<DynamicFlowStepInstance> reporterSteps,
        IEnumerable<DynamicFlowStepInstance> reviewerSteps,
        IEnumerable<string> reviewerAssignmentIds,
        IEnumerable<string> reviewMutationAssignmentIds)
    {
        var reporter = reporterSteps.ToArray();
        var reviewer = reviewerSteps.ToArray();
        var all = reporter
            .Concat(reviewer)
            .GroupBy(step => step.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var refs = all
            .SelectMany(step => new[]
            {
                $"step:{step.Id}",
                string.IsNullOrWhiteSpace(step.AssignmentId)
                    ? null
                    : $"assignment:{step.AssignmentId}"
            })
            .Where(value => value is not null)
            .Select(value => value!);
        return new DynamicFlowRuntimeReadScope(
            actorUserId,
            actorUnitId,
            isIssuer,
            reporter.Select(step => step.Id),
            reviewer.Select(step => step.Id),
            reviewerAssignmentIds,
            reviewMutationAssignmentIds,
            all.Select(step => step.TargetUnitId),
            all
                .Select(step => step.AssignmentId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!),
            refs);
    }

    public List<string> ScopesFor(
        DynamicFlowStepInstance step,
        string actorUserId)
    {
        var result = new List<string>();
        if (IsIssuer)
            result.Add(DynamicFlowRuntimeVisibilityScopes.Issuer);
        if (_reporterStepIds.Contains(step.Id) ||
            step.ParticipantUserIds.Contains(actorUserId, StringComparer.Ordinal))
        {
            result.Add(DynamicFlowRuntimeVisibilityScopes.Reporter);
        }
        if (_reviewerStepIds.Contains(step.Id) ||
            step.AssignmentId is not null &&
            ReviewerAssignmentIds.Contains(step.AssignmentId))
        {
            result.Add(DynamicFlowRuntimeVisibilityScopes.Reviewer);
        }

        return result;
    }

    public List<string> FilterAffectedRefs(
        IEnumerable<string>? refs,
        IEnumerable<string>? additionalAllowedRefs = null)
    {
        var values = (refs ?? [])
            .Where(value =>
                value.StartsWith("step:", StringComparison.Ordinal) ||
                value.StartsWith("assignment:", StringComparison.Ordinal) ||
                value.StartsWith("report:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        return IsIssuer
            ? values.OrderBy(value => value, StringComparer.Ordinal).ToList()
            : values
                .Where(_allowedRefs
                    .Concat(additionalAllowedRefs ?? [])
                    .ToHashSet(StringComparer.Ordinal)
                    .Contains)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
    }
}

internal sealed record DynamicFlowRuntimeDownstreamAccess(
    IReadOnlySet<string> AssignmentIds,
    IReadOnlyDictionary<string, string> PrimaryReportIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ReportIdsByStep,
    IReadOnlyDictionary<string, string> SubmittableReportIds,
    IReadOnlyDictionary<string, string> ReviewableReportIds)
{
    public static readonly DynamicFlowRuntimeDownstreamAccess Empty =
        new(
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
}

internal sealed class DynamicFlowRuntimeReportAccessRef
{
    public string ReportId { get; set; } = default!;
    public string AssignmentId { get; set; } = default!;
    public string PeriodKey { get; set; } = default!;
    public string AssigneeUserId { get; set; } = default!;
    public WorkAssignmentReportStatus Status { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class DynamicFlowRuntimeStepRef
{
    public string FlowInstanceId { get; set; } = default!;
    public string StepInstanceId { get; set; } = default!;
}

internal sealed class DynamicFlowRuntimeRecoveryOutcomeRef
{
    public string FlowInstanceId { get; set; } = default!;
    public long Sequence { get; set; }
    public string EventType { get; set; } = default!;
}

internal sealed record RecoveryAttemptSnapshot(
    int AttemptCount,
    DateTime? LastAttemptAtUtc,
    DateTime? NextAttemptAtUtc,
    string? LastOutcome)
{
    public static readonly RecoveryAttemptSnapshot Empty =
        new(0, null, null, null);

    public static RecoveryAttemptSnapshot From(
        IEnumerable<DynamicFlowRuntimeOutboxItem> rows,
        string? lastOutcome = null)
    {
        var values = rows.ToArray();
        if (values.Length == 0)
            return new RecoveryAttemptSnapshot(0, null, null, lastOutcome);
        var next = values
            .Where(row =>
                row.Status != DynamicFlowRuntimeOutboxStatuses.Completed &&
                row.Status != DynamicFlowRuntimeOutboxStatuses.DeadLetter)
            .Select(row => (DateTime?)row.NextAttemptAtUtc)
            .OrderBy(value => value)
            .FirstOrDefault();
        var attempted = values
            .Where(row => row.AttemptCount > 0)
            .ToArray();
        return new RecoveryAttemptSnapshot(
            values.Max(row => row.AttemptCount),
            attempted.Length == 0
                ? null
                : attempted.Max(row => (DateTime?)row.UpdatedAtUtc),
            next,
            lastOutcome);
    }
}

internal sealed record DynamicFlowRuntimeCursor(
    string Kind,
    DateTime? UpdatedAtUtc = null,
    string? Id = null,
    string? StepId = null,
    string? TargetUnitId = null,
    int? AttemptNo = null,
    long? Sequence = null,
    string? SnapshotToken = null,
    DateTime? AnchorUpdatedAtUtc = null,
    string? AnchorId = null,
    long? Total = null,
    long? HighWaterSequence = null,
    string? Scope = null);

internal static class DynamicFlowRuntimeReadContract
{
    public const string InstanceCursorKind = "INSTANCE_UPDATED_DESC";
    public const string InboxCursorKind = "INBOX_UPDATED_DESC";
    public const string StepCursorKind = "STEP_IDENTITY_ASC";
    public const string TimelineCursorKind = "TIMELINE_SEQUENCE_ASC";
    public const string PreflightTargetCursorKind = "PREFLIGHT_TARGET_ASC";
    public const int MaxPageLimit = 200;

    public static int RequireLimit(int limit)
    {
        if (limit is < 1 or > MaxPageLimit)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "limit",
                    reason = "DYNAMIC_FLOW_RUNTIME_PAGE_LIMIT_INVALID",
                    min = 1,
                    max = MaxPageLimit
                });
        }

        return limit;
    }

    public static DynamicFlowPreflightResponse PagePreflight(
        DynamicFlowPreflightResponse response,
        DynamicFlowPreflightPageQuery query)
    {
        ArgumentNullException.ThrowIfNull(response);
        query ??= new DynamicFlowPreflightPageQuery();
        var limit = RequireLimit(query.Limit);
        var cursor = DecodePreflightCursor(
            query.Cursor,
            response.SnapshotToken);
        var ordered = response.Targets
            .OrderBy(item => item.TargetUnitId, StringComparer.Ordinal)
            .ToArray();
        var page = ordered
            .Where(item =>
                cursor is null ||
                string.CompareOrdinal(
                    item.TargetUnitId,
                    cursor.TargetUnitId) > 0)
            .Take(limit + 1)
            .ToList();
        if (cursor is not null &&
            !ordered.Any(item =>
                string.Equals(
                    item.TargetUnitId,
                    cursor.TargetUnitId,
                    StringComparison.Ordinal)))
        {
            throw InvalidCursor();
        }
        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        response.Targets = page;
        response.TargetTotal = ordered.LongLength;
        response.TargetLimit = limit;
        response.TargetHasMore = hasMore;
        response.TargetNextCursor = hasMore && page.Count > 0
            ? Encode(new DynamicFlowRuntimeCursor(
                PreflightTargetCursorKind,
                TargetUnitId: page[^1].TargetUnitId,
                SnapshotToken: response.SnapshotToken))
            : null;
        return response;
    }

    public static string RequireInstanceState(string? value)
    {
        value = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value) ||
            !DynamicFlowInstanceStates.All.Contains(value))
        {
            throw InvalidState("state");
        }

        return value;
    }

    public static string RequireStepState(string? value)
    {
        value = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value) ||
            !DynamicFlowStepStates.All.Contains(value))
        {
            throw InvalidState("state");
        }

        return value;
    }

    public static void RequireScopedCursor(bool valid)
    {
        if (!valid)
            throw InvalidCursor();
    }

    public static string EncodeUpdatedCursor(
        string kind,
        DateTime updatedAtUtc,
        string id,
        DateTime? anchorUpdatedAtUtc = null,
        string? anchorId = null,
        long total = 0,
        string? scope = null)
        => Encode(new DynamicFlowRuntimeCursor(
            kind,
            updatedAtUtc.ToUniversalTime(),
            id,
            AnchorUpdatedAtUtc:
                (anchorUpdatedAtUtc ?? updatedAtUtc).ToUniversalTime(),
            AnchorId: anchorId ?? id,
            Total: total,
            Scope: scope));

    public static DynamicFlowRuntimeCursor? DecodeUpdatedCursor(
        string? value,
        string kind,
        string? requiredScope = null)
    {
        var cursor = Decode(value);
        if (cursor is null)
            return null;
        if (cursor.Kind != kind ||
            !cursor.UpdatedAtUtc.HasValue ||
            !ObjectId.TryParse(cursor.Id, out _) ||
            !cursor.AnchorUpdatedAtUtc.HasValue ||
            !ObjectId.TryParse(cursor.AnchorId, out _) ||
            cursor.Total is null or < 0 ||
            requiredScope is not null &&
            !FixedEquals(cursor.Scope, requiredScope))
        {
            throw InvalidCursor();
        }

        return cursor;
    }

    public static string EncodeStepCursor(
        string stepId,
        string targetUnitId,
        int attemptNo,
        long total = 0,
        string? scope = null)
        => Encode(new DynamicFlowRuntimeCursor(
            StepCursorKind,
            StepId: stepId,
            TargetUnitId: targetUnitId,
            AttemptNo: attemptNo,
            Total: total,
            Scope: scope));

    public static DynamicFlowRuntimeCursor? DecodeStepCursor(
        string? value,
        string? requiredScope = null)
    {
        var cursor = Decode(value);
        if (cursor is null)
            return null;
        if (cursor.Kind != StepCursorKind ||
            string.IsNullOrWhiteSpace(cursor.StepId) ||
            !ObjectId.TryParse(cursor.TargetUnitId, out _) ||
            cursor.AttemptNo is null or < 1 ||
            cursor.Total is null or < 0 ||
            requiredScope is not null &&
            !FixedEquals(cursor.Scope, requiredScope))
        {
            throw InvalidCursor();
        }

        return cursor;
    }

    public static string EncodeTimelineCursor(
        long sequence,
        long? highWaterSequence = null,
        long total = 0,
        string? scope = null)
        => Encode(new DynamicFlowRuntimeCursor(
            TimelineCursorKind,
            Sequence: sequence,
            Total: total,
            HighWaterSequence: highWaterSequence ?? sequence,
            Scope: scope));

    public static DynamicFlowRuntimeCursor? DecodeTimelineCursor(
        string? value,
        string? requiredScope = null)
    {
        var cursor = Decode(value);
        if (cursor is null)
            return null;
        if (cursor.Kind != TimelineCursorKind ||
            cursor.Sequence is null or < 0 ||
            cursor.HighWaterSequence is null or < 0 ||
            cursor.Sequence > cursor.HighWaterSequence ||
            cursor.Total is null or < 0 ||
            requiredScope is not null &&
            !FixedEquals(cursor.Scope, requiredScope))
        {
            throw InvalidCursor();
        }

        return cursor;
    }

    public static DynamicFlowRuntimeCapabilitiesDto BuildInstanceCapabilities(
        long revision,
        bool isIssuer,
        bool canManageSupplemental = false,
        bool canFinalize = false,
        bool canRollback = false,
        bool canTerminate = false,
        bool canRestart = false)
        => new()
        {
            CanViewOverview = true,
            CanViewTimeline = true,
            CanViewAllBranches = isIssuer,
            CanOpenAssignment = false,
            CanOpenReport = false,
            CanSubmitReport = false,
            CanReviewReport = false,
            CanRetry = false,
            CanReconcile = false,
            CanForward = false,
            CanLaunchSubflow = false,
            CanManageSupplemental = canManageSupplemental,
            CanCancelSupplemental = false,
            CanFinalize = canFinalize,
            CanRollback = canRollback,
            CanTerminate = canTerminate,
            CanRestart = canRestart,
            ExpectedRevision = revision
        };

    public static DynamicFlowRuntimeCapabilitiesDto BuildStepCapabilities(
        long revision,
        string state,
        bool isIssuer,
        bool isReporter,
        bool isReviewer,
        bool hasAssignment,
        bool hasReport,
        bool? canSubmitReport = null,
        bool? canReviewReport = null,
        bool canForward = false,
        bool canLaunchSubflow = false,
        bool canCancelSupplemental = false)
        => new()
        {
            CanViewOverview = true,
            CanViewTimeline = true,
            CanViewAllBranches = isIssuer,
            CanOpenAssignment = hasAssignment,
            CanOpenReport = hasReport,
            CanSubmitReport =
                canSubmitReport ??
                (isReporter &&
                 state is DynamicFlowStepStates.Assigned
                     or DynamicFlowStepStates.InProgress
                     or DynamicFlowStepStates.Returned),
            CanReviewReport =
                canReviewReport ??
                (isReviewer &&
                 state == DynamicFlowStepStates.Submitted),
            CanRetry = false,
            CanReconcile = false,
            CanForward = canForward,
            CanLaunchSubflow = canLaunchSubflow,
            CanManageSupplemental = false,
            CanCancelSupplemental = canCancelSupplemental,
            CanFinalize = false,
            CanRollback = false,
            CanTerminate = false,
            CanRestart = false,
            ExpectedRevision = revision
        };

    public static DynamicFlowRuntimeRecoveryStatusDto BuildRecovery(
        string state,
        int attemptCount,
        DateTime? lastAttemptAtUtc,
        DateTime? nextAttemptAtUtc,
        bool reconcileRunning,
        string? lastOutcome = null)
    {
        var reconciledReadback =
            state is not DynamicFlowInstanceStates.Partial
                and not DynamicFlowInstanceStates.Retrying
                and not DynamicFlowInstanceStates.Reconciled
                and not DynamicFlowInstanceStates.Failed &&
            lastOutcome is DynamicFlowRuntimeEventTypes.RecoveryReconciled
                or DynamicFlowRuntimeEventTypes.RecoveryCompleted;
        var (required, text, reason, action) = reconciledReadback
            ? (false, "Đã khôi phục và đối soát", "DYNAMIC_FLOW_RUNTIME_RECONCILED", "REFRESH")
            : state switch
        {
            DynamicFlowInstanceStates.Partial =>
                (true, "Đang chờ khôi phục", "DYNAMIC_FLOW_RUNTIME_PARTIAL", "WAIT_FOR_RECONCILE"),
            DynamicFlowInstanceStates.Retrying =>
                (true, "Đang thử lại", "DYNAMIC_FLOW_RUNTIME_RETRYING", "WAIT_FOR_RETRY"),
            DynamicFlowInstanceStates.Reconciled =>
                (false, "Đã đối soát", "DYNAMIC_FLOW_RUNTIME_RECONCILED", "REFRESH"),
            DynamicFlowInstanceStates.Failed =>
                (true, "Khôi phục không thành công", "DYNAMIC_FLOW_RUNTIME_FAILED", "CONTACT_SYSTEM_ADMIN"),
            _ => (false, "Bình thường", "DYNAMIC_FLOW_RUNTIME_HEALTHY", "NONE")
        };
        return new DynamicFlowRuntimeRecoveryStatusDto
        {
            State = state,
            RecoveryRequired = required,
            StatusText = text,
            ReasonCode = reason,
            NextAction = action,
            AttemptCount = Math.Max(0, attemptCount),
            LastAttemptAtUtc = lastAttemptAtUtc,
            NextAttemptAtUtc = nextAttemptAtUtc,
            ReconcileStatus = reconcileRunning
                ? "RUNNING"
                : reconciledReadback ||
                  state == DynamicFlowInstanceStates.Reconciled
                    ? "RECONCILED"
                    : "IDLE"
        };
    }

    private static DynamicFlowRuntimeCursor? DecodePreflightCursor(
        string? value,
        string snapshotToken)
    {
        var cursor = Decode(value);
        if (cursor is null)
            return null;
        if (cursor.Kind != PreflightTargetCursorKind ||
            !ObjectId.TryParse(cursor.TargetUnitId, out _) ||
            !FixedEquals(cursor.SnapshotToken, snapshotToken))
        {
            throw InvalidCursor();
        }

        return cursor;
    }

    public static string BuildInstanceRevisionToken(
        string instanceId,
        long revision,
        long recoveryEpoch)
        => Hash($"instance\n{instanceId}\n{revision}\n{recoveryEpoch}");

    public static string BuildStepRevisionToken(
        string stepInstanceId,
        long revision)
        => Hash($"step\n{stepInstanceId}\n{revision}");

    public static IEnumerable<string> ExtractAffectedRefIds(
        IEnumerable<string>? refs,
        string prefix)
        => (refs ?? [])
            .Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
            .Select(value => value[prefix.Length..])
            .Where(value => ObjectId.TryParse(value, out _));

    public static string? ExtractFirstAffectedRefId(
        IEnumerable<string>? refs,
        string prefix)
        => ExtractAffectedRefIds(refs, prefix)
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault();

    public static string? SanitizeReasonCode(string? reason)
    {
        reason = reason?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(reason))
            return null;
        if (reason.StartsWith("REPORT_LIFECYCLE_", StringComparison.Ordinal) ||
            reason is "ASSIGNMENT_COMPLETED"
                or "STATE_RECONCILE"
                or "STATE_RECONCILE_INSTANCE_COMPLETION"
                or "ALL_RUNTIME_STEPS_COMPLETED"
                or "SEQUENTIAL_EDGE_ACTIVATED")
        {
            return reason;
        }

        return "DYNAMIC_FLOW_RUNTIME_STATE_CHANGED";
    }

    public static string SanitizeEventType(string? eventType)
    {
        eventType = eventType?.Trim().ToUpperInvariant();
        if (eventType is
                DynamicFlowRuntimeEventTypes.LaunchIntentCommitted or
                DynamicFlowRuntimeEventTypes.EntryAssignmentMaterialized or
                DynamicFlowRuntimeEventTypes.SequentialForwardAccepted or
                DynamicFlowRuntimeEventTypes.SequentialAssignmentMaterialized or
                DynamicFlowRuntimeEventTypes.ReviewAttemptAssignmentMaterialized or
                DynamicFlowRuntimeEventTypes.ReviewReturnAttemptCreated or
                DynamicFlowRuntimeEventTypes.ReviewCycleExhausted or
                DynamicFlowRuntimeEventTypes.RecoveryStarted or
                DynamicFlowRuntimeEventTypes.RecoveryReconciled or
                DynamicFlowRuntimeEventTypes.RecoveryCompleted or
                DynamicFlowRuntimeEventTypes.CompensationApplied or
                DynamicFlowFinalizeTopologyContract.FinalizedEvent or
                DynamicFlowFinalizeTopologyContract.RolledBackEvent or
                DynamicFlowFinalizeTopologyContract.TerminatedEvent or
                DynamicFlowFinalizeTopologyContract.RestartedEvent ||
            eventType is not null &&
            DynamicFlowRuntimeStateProjectionEventTypes.All.Contains(eventType))
        {
            return eventType;
        }

        return "DYNAMIC_FLOW_RUNTIME_STATE_CHANGED";
    }

    private static string Encode(DynamicFlowRuntimeCursor cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static DynamicFlowRuntimeCursor? Decode(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            var normalized = value
                .Replace('-', '+')
                .Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - normalized.Length % 4) % 4),
                '=');
            var cursor = JsonSerializer.Deserialize<DynamicFlowRuntimeCursor>(
                Convert.FromBase64String(normalized));
            return cursor ?? throw InvalidCursor();
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw InvalidCursor();
        }
    }

    private static AppException InvalidState(string field)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                field,
                reason = "DYNAMIC_FLOW_RUNTIME_STATE_INVALID"
            });

    private static AppException InvalidCursor()
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                field = "cursor",
                reason = "DYNAMIC_FLOW_RUNTIME_CURSOR_INVALID"
            });

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null)
            return false;
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length &&
               CryptographicOperations.FixedTimeEquals(a, b);
    }
}
