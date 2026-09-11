using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.Services.DynamicFlows;

public sealed partial class DynamicFlowRuntimeService
{
    public Task<DynamicFlowEpochCommandResponse> FinalizeEpochAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowEpochCommandRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => ExecuteEpochCommandAsync(
            workId,
            flowInstanceId,
            req,
            actorUserId,
            DynamicFlowFinalizeTopologyContract.FinalizeCommand,
            ct);

    public Task<DynamicFlowEpochCommandResponse> RollbackEpochAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowEpochCommandRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => ExecuteEpochCommandAsync(
            workId,
            flowInstanceId,
            req,
            actorUserId,
            DynamicFlowFinalizeTopologyContract.RollbackCommand,
            ct);

    public Task<DynamicFlowEpochCommandResponse> TerminateEpochAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowEpochCommandRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => ExecuteEpochCommandAsync(
            workId,
            flowInstanceId,
            req,
            actorUserId,
            DynamicFlowFinalizeTopologyContract.TerminateCommand,
            ct);

    public Task<DynamicFlowEpochCommandResponse> RestartEpochAsync(
        string workId,
        string flowInstanceId,
        DynamicFlowEpochCommandRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => ExecuteEpochCommandAsync(
            workId,
            flowInstanceId,
            req,
            actorUserId,
            DynamicFlowFinalizeTopologyContract.RestartCommand,
            ct);

    private async Task<DynamicFlowEpochCommandResponse>
        ExecuteEpochCommandAsync(
            string workId,
            string flowInstanceId,
            DynamicFlowEpochCommandRequest? req,
            string actorUserId,
            string commandType,
            CancellationToken ct)
    {
        EnsureActor(actorUserId);
        req ??= new DynamicFlowEpochCommandRequest();
        workId = NormalizeRequiredObjectId(workId, "workId");
        flowInstanceId =
            NormalizeRequiredObjectId(flowInstanceId, "flowInstanceId");
        var commandId = NormalizeRequiredCommandId(req.CommandId);
        var reason = NormalizeOptionalText(req.Reason, 1000);
        if (req.ExpectedExecutionEpoch < 1 ||
            req.ExpectedInstanceRevision < 0)
        {
            throw EpochValidation(
                "DYNAMIC_FLOW_EPOCH_EXPECTED_REVISION_REQUIRED",
                commandType);
        }

        var instance = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == flowInstanceId &&
                candidate.WorkId == workId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        if (instance.IssuerUserId != actorUserId)
            throw RuntimeAccessForbidden();
        if (!_runtimeActivation.CanExecuteP6CandidatePin(
                instance.CatalogVersion,
                instance.CatalogSemanticHash,
                instance.ArchetypeId) ||
            instance.ArchetypeId !=
                DynamicFlowFinalizeTopologyContract.ArchetypeId)
        {
            throw ForwardBlocked(instance.ArchetypeId);
        }

        var topology = DynamicFlowFinalizeTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var checkpointNodeId =
            NormalizeEpochCheckpoint(req.CheckpointNodeId);
        var requestJson = JsonSerializer.Serialize(new
        {
            commandId,
            expectedExecutionEpoch = req.ExpectedExecutionEpoch,
            expectedInstanceRevision = req.ExpectedInstanceRevision,
            checkpointNodeId,
            reason
        });
        var requestHash =
            DynamicFlowSequentialTopologyContract.Hash(requestJson);
        var existing = await FindEpochReceiptAsync(
            instance.Id,
            commandType,
            commandId,
            ct);
        if (existing is not null)
        {
            RequireEpochReplay(existing, requestHash, actorUserId);
            return EpochResponse(existing.ResultSnapshot!, replayed: true);
        }
        RequireEpochCommandPreconditions(
            instance,
            topology,
            commandType,
            checkpointNodeId,
            req.ExpectedExecutionEpoch,
            req.ExpectedInstanceRevision);

        var response = await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var current = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        candidate =>
                            candidate.Id == instance.Id &&
                            candidate.WorkId == workId &&
                            !candidate.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw RuntimeAccessForbidden();
                RequireEpochCommandPreconditions(
                    current,
                    topology,
                    commandType,
                    checkpointNodeId,
                    req.ExpectedExecutionEpoch,
                    req.ExpectedInstanceRevision);

                var currentSteps = await _ctx.DynamicFlowStepInstances
                    .Find(
                        session,
                        step =>
                            step.FlowInstanceId == current.Id &&
                            step.ExecutionEpoch == current.ExecutionEpoch &&
                            step.IsCanonicalEpoch != false &&
                            !step.IsDeleted)
                    .SortBy(step => step.TargetUnitId)
                    .ToListAsync(transactionCt);
                if (currentSteps.Count == 0 ||
                    currentSteps.Any(step =>
                        step.FlowStepId != topology.EntryNode.NodeId))
                {
                    throw EpochValidation(
                        "DYNAMIC_FLOW_EPOCH_CHECKPOINT_CLOSURE_INVALID",
                        commandType);
                }
                if (commandType ==
                        DynamicFlowFinalizeTopologyContract.FinalizeCommand &&
                    currentSteps.Any(step =>
                        step.State != DynamicFlowStepStates.Completed))
                {
                    throw EpochValidation(
                        "DYNAMIC_FLOW_FINALIZE_PRECONDITION_NOT_MET",
                        commandType);
                }

                var now = DateTime.UtcNow;
                var previousEpoch = current.ExecutionEpoch;
                var createsNewEpoch =
                    commandType is
                        DynamicFlowFinalizeTopologyContract.RollbackCommand or
                        DynamicFlowFinalizeTopologyContract.RestartCommand;
                var nextEpoch = createsNewEpoch
                    ? checked(previousEpoch + 1)
                    : previousEpoch;
                var eventType = EpochEventType(commandType);
                var eventId =
                    DynamicFlowFinalizeTopologyContract.BuildEventId(
                        current.Id,
                        previousEpoch,
                        commandType,
                        commandId);
                var assignmentIds = currentSteps
                    .Where(step =>
                        !string.IsNullOrWhiteSpace(step.AssignmentId))
                    .Select(step => step.AssignmentId!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var gatewayCount =
                    await _ctx.DynamicFlowGatewayInstances.CountDocumentsAsync(
                        session,
                        gateway =>
                            gateway.FlowInstanceId == current.Id &&
                            gateway.ExecutionEpoch == previousEpoch &&
                            gateway.IsCanonicalEpoch != false,
                        cancellationToken: transactionCt);
                var reportCount = assignmentIds.Count == 0
                    ? 0
                    : await _ctx.WorkAssignmentReports.CountDocumentsAsync(
                        session,
                        report =>
                            assignmentIds.Contains(report.WorkAssignmentId) &&
                            report.IsCurrent &&
                            !report.IsDeleted,
                        cancellationToken: transactionCt);

                string? rebuildIntentId = null;
                IReadOnlyList<string>
                    invalidatedMappingProvenanceIds =
                        Array.Empty<string>();
                if (commandType !=
                    DynamicFlowFinalizeTopologyContract.FinalizeCommand)
                {
                    invalidatedMappingProvenanceIds =
                        await InvalidateDynamicFlowMappingProvenanceAsync(
                            session,
                            current,
                            eventId,
                            commandType,
                            now,
                            _runtimeActivation
                                .P7MappingLifecycleExecutionEnabled,
                            transactionCt);
                    await InvalidateEpochClosureAsync(
                        session,
                        current,
                        currentSteps,
                        assignmentIds,
                        eventId,
                        commandType,
                        now,
                        actorUserId,
                        transactionCt);
                    rebuildIntentId =
                        DynamicFlowFinalizeTopologyContract
                            .BuildRebuildIntentId(
                                current.Id,
                                previousEpoch,
                                createsNewEpoch ? nextEpoch : null,
                                eventId);
                    var rebuildPayload = new BsonDocument
                    {
                        { "policyVersion",
                            DynamicFlowFinalizeTopologyContract
                                .RebuildIntentPolicyVersion },
                        { "flowInstanceId", current.Id },
                        { "invalidatedExecutionEpoch", previousEpoch },
                        {
                            "replacementExecutionEpoch",
                            createsNewEpoch
                                ? new BsonInt32(nextEpoch)
                                : BsonNull.Value
                        },
                        { "eventId", eventId },
                        { "commandType", commandType },
                        {
                            "stepInstanceIds",
                            new BsonArray(currentSteps.Select(step => step.Id))
                        },
                        {
                            "assignmentIds",
                            new BsonArray(assignmentIds)
                        },
                        {
                            "mappingProvenanceIds",
                            new BsonArray(
                                invalidatedMappingProvenanceIds)
                        },
                        { "mappingRebuildOnly", true },
                        { "p8ExecutionEnabled", false },
                        { "p9ExecutionEnabled", false }
                    };
                    await _ctx.DynamicFlowRuntimeOutbox.InsertOneAsync(
                        session,
                        new DynamicFlowRuntimeOutboxItem
                        {
                            Id = rebuildIntentId,
                            FlowInstanceId = current.Id,
                            StepInstanceId = currentSteps[0].Id,
                            Operation =
                                DynamicFlowFinalizeTopologyContract
                                    .RebuildIntentOperation,
                            DedupeKey =
                                $"{current.Id}:{previousEpoch}:{eventId}:rebuild-intent",
                            Payload = rebuildPayload,
                            PayloadHash =
                                DynamicFlowSequentialTopologyContract.Hash(
                                    rebuildPayload.ToJson()),
                            Status =
                                DynamicFlowRuntimeOutboxStatuses.Completed,
                            AttemptCount = 0,
                            RepairEpoch = 0,
                            NextAttemptAtUtc = now,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            UpdatedByUserId = actorUserId,
                            CompletedAtUtc = now
                        },
                        cancellationToken: transactionCt);
                }

                if (createsNewEpoch)
                {
                    await CreateReplacementEpochAsync(
                        session,
                        current,
                        topology,
                        currentSteps,
                        nextEpoch,
                        commandId,
                        actorUserId,
                        now,
                        transactionCt);
                }

                var nextState = commandType switch
                {
                    DynamicFlowFinalizeTopologyContract.FinalizeCommand =>
                        DynamicFlowInstanceStates.Finalized,
                    DynamicFlowFinalizeTopologyContract.TerminateCommand =>
                        DynamicFlowInstanceStates.Terminated,
                    // Replacement epochs remain ACTIVE while their deterministic
                    // assignments are materialized. This keeps T12 on its epoch
                    // authority path instead of re-running the launch oracle for
                    // an earlier, now non-canonical epoch.
                    _ => DynamicFlowInstanceStates.Active
                };
                var instanceUpdate =
                    Builders<DynamicFlowInstance>.Update
                        .Set(candidate => candidate.State, nextState)
                        .Set(candidate => candidate.ExecutionEpoch, nextEpoch)
                        .Set(candidate => candidate.CompletedAtUtc,
                            commandType ==
                                DynamicFlowFinalizeTopologyContract
                                    .FinalizeCommand
                                ? now
                                : null)
                        .Set(candidate => candidate.UpdatedAtUtc, now)
                        .Set(candidate => candidate.UpdatedByUserId, actorUserId)
                        .Inc(candidate => candidate.Revision, 1)
                        .Inc(candidate => candidate.NextEventSequence, 1);
                if (commandType ==
                    DynamicFlowFinalizeTopologyContract.FinalizeCommand)
                {
                    instanceUpdate = instanceUpdate
                        .Set(
                            candidate => candidate.FinalizedExecutionEpoch,
                            previousEpoch)
                        .Set(candidate => candidate.FinalizedAtUtc, now)
                        .Set(
                            candidate => candidate.FinalizedByUserId,
                            actorUserId)
                        .Set(
                            candidate => candidate.FinalizedByEventId,
                            eventId);
                }

                var updated = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    candidate =>
                        candidate.Id == current.Id &&
                        candidate.ExecutionEpoch == previousEpoch &&
                        candidate.Revision == req.ExpectedInstanceRevision &&
                        candidate.State == current.State &&
                        !candidate.IsDeleted,
                    instanceUpdate,
                    cancellationToken: transactionCt);
                if (updated.ModifiedCount != 1)
                    throw EpochRevisionConflict(current);

                await CloseExecutionEpochAsync(
                    session,
                    current,
                    commandType,
                    commandId,
                    eventId,
                    createsNewEpoch ? nextEpoch : null,
                    now,
                    actorUserId,
                    transactionCt);

                var affectedRefs = currentSteps
                    .Select(step => $"step:{step.Id}")
                    .Concat(assignmentIds.Select(id => $"assignment:{id}"))
                    .ToList();
                if (rebuildIntentId is not null)
                    affectedRefs.Add($"rebuildIntent:{rebuildIntentId}");
                affectedRefs.AddRange(
                    invalidatedMappingProvenanceIds.Select(
                        id => $"mappingProvenance:{id}"));
                var eventPayload = new BsonDocument
                {
                    { "commandType", commandType },
                    { "previousExecutionEpoch", previousEpoch },
                    { "executionEpoch", nextEpoch },
                    {
                        "checkpointNodeId",
                        BsonValue.Create(checkpointNodeId)
                    },
                    { "reason", BsonValue.Create(reason) },
                    {
                        "rebuildIntentId",
                        BsonValue.Create(rebuildIntentId)
                    },
                    {
                        "mappingProvenanceIds",
                        new BsonArray(
                            invalidatedMappingProvenanceIds)
                    },
                    { "p8ExecutionEnabled", false },
                    { "p9ExecutionEnabled", false }
                };
                await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                    session,
                    new DynamicFlowRuntimeEvent
                    {
                        Id = eventId,
                        FlowInstanceId = current.Id,
                        ExecutionEpoch = previousEpoch,
                        Sequence = current.NextEventSequence,
                        EventType = eventType,
                        CommandId = commandId,
                        FromState = current.State,
                        ToState = nextState,
                        FromRevision = current.Revision,
                        ToRevision = current.Revision + 1,
                        ReasonCode = reason,
                        AffectedRefs = affectedRefs,
                        ActorUserId = actorUserId,
                        VisibleUnitIds = new List<string>
                        {
                            current.IssuerUnitId
                        },
                        Payload = eventPayload,
                        PayloadHash =
                            DynamicFlowSequentialTopologyContract.Hash(
                                eventPayload.ToJson()),
                        OccurredAtUtc = now
                    },
                    cancellationToken: transactionCt);

                var commandResponse = new DynamicFlowEpochCommandResponse
                {
                    CommandId = commandId,
                    Action = commandType,
                    FlowInstanceId = current.Id,
                    PreviousExecutionEpoch = previousEpoch,
                    ExecutionEpoch = nextEpoch,
                    InstanceRevision = current.Revision + 1,
                    InstanceState = nextState,
                    EventId = eventId,
                    RebuildIntentId = rebuildIntentId,
                    InvalidatedStepCount =
                        commandType ==
                            DynamicFlowFinalizeTopologyContract.FinalizeCommand
                            ? 0
                            : currentSteps.Count,
                    InvalidatedGatewayCount =
                        commandType ==
                            DynamicFlowFinalizeTopologyContract.FinalizeCommand
                            ? 0
                            : checked((int)gatewayCount),
                    InvalidatedAssignmentCount =
                        commandType ==
                            DynamicFlowFinalizeTopologyContract.FinalizeCommand
                            ? 0
                            : assignmentIds.Count,
                    InvalidatedReportCount =
                        commandType ==
                            DynamicFlowFinalizeTopologyContract.FinalizeCommand
                            ? 0
                            : checked((int)reportCount),
                    BusinessWritePerformed = true,
                    Replayed = false
                };
                var resultSnapshot = EpochResultDocument(commandResponse);
                await _ctx.DynamicFlowRuntimeCommandReceipts.InsertOneAsync(
                    session,
                    new DynamicFlowRuntimeCommandReceipt
                    {
                        Id = StableTopologyObjectId(
                            $"{DynamicFlowCommandScopeKinds.Instance}\n" +
                            $"{current.Id}\n{commandType}\n{commandId}"),
                        ScopeKind = DynamicFlowCommandScopeKinds.Instance,
                        ScopeId = current.Id,
                        WorkId = current.WorkId,
                        FlowTemplateVersionId =
                            current.FlowTemplateVersionId,
                        FlowInstanceId = current.Id,
                        CommandType = commandType,
                        CommandId = commandId,
                        RequestHash = requestHash,
                        CommandIdentityHash =
                            DynamicFlowSequentialTopologyContract.Hash(
                                $"{actorUserId}\n{current.Id}\n" +
                                $"{commandType}\n{commandId}"),
                        SnapshotToken =
                            DynamicFlowSequentialTopologyContract.Hash(
                                $"{requestHash}\n" +
                                $"{current.TopologySnapshotHash}\n" +
                                $"{previousEpoch}"),
                        ExpectedRevision = req.ExpectedInstanceRevision,
                        Status = DynamicFlowRuntimeCommandStatuses.Succeeded,
                        ResultSnapshot = resultSnapshot,
                        ResultSnapshotHash =
                            DynamicFlowSequentialTopologyContract.Hash(
                                resultSnapshot.ToJson()),
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        UpdatedByUserId = actorUserId,
                        CompletedAtUtc = now
                    },
                    cancellationToken: transactionCt);
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    current.WorkId,
                    transactionCt);
                return commandResponse;
            },
            ct);

        if (commandType is
            DynamicFlowFinalizeTopologyContract.RollbackCommand or
            DynamicFlowFinalizeTopologyContract.RestartCommand)
        {
            await _runtimeMaterializer.ProcessPendingAsync(50, ct);
        }
        return response;
    }

    private async Task InvalidateEpochClosureAsync(
        IClientSessionHandle session,
        DynamicFlowInstance instance,
        IReadOnlyCollection<DynamicFlowStepInstance> steps,
        IReadOnlyCollection<string> assignmentIds,
        string eventId,
        string commandType,
        DateTime now,
        string actorUserId,
        CancellationToken ct)
    {
        var stepIds = steps.Select(step => step.Id).ToList();
        var stepUpdate =
            Builders<DynamicFlowStepInstance>.Update
                .Set(step => step.IsCanonicalEpoch, false)
                .Set(step => step.InvalidatedByFlowEventId, eventId)
                .Set(step => step.InvalidatedAtUtc, now)
                .Set(step => step.UpdatedAtUtc, now)
                .Set(step => step.UpdatedByUserId, actorUserId);
        if (commandType ==
            DynamicFlowFinalizeTopologyContract.TerminateCommand)
        {
            stepUpdate =
                stepUpdate.Set(
                    step => step.State,
                    DynamicFlowStepStates.Terminated);
        }
        await _ctx.DynamicFlowStepInstances.UpdateManyAsync(
            session,
            step =>
                stepIds.Contains(step.Id) &&
                step.IsCanonicalEpoch != false &&
                !step.IsDeleted,
            stepUpdate,
            cancellationToken: ct);
        await _ctx.DynamicFlowGatewayInstances.UpdateManyAsync(
            session,
            gateway =>
                gateway.FlowInstanceId == instance.Id &&
                gateway.ExecutionEpoch == instance.ExecutionEpoch &&
                gateway.IsCanonicalEpoch != false,
            Builders<DynamicFlowGatewayInstance>.Update
                .Set(gateway => gateway.IsCanonicalEpoch, false)
                .Set(gateway => gateway.InvalidatedByFlowEventId, eventId)
                .Set(gateway => gateway.InvalidatedAtUtc, now)
                .Set(gateway => gateway.UpdatedAtUtc, now)
                .Set(gateway => gateway.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
        if (assignmentIds.Count > 0)
        {
            await _ctx.WorkAssignments.UpdateManyAsync(
                session,
                assignment =>
                    assignmentIds.Contains(assignment.Id) &&
                    !assignment.IsDeleted,
                Builders<WorkAssignment>.Update
                    .Set(
                        assignment => assignment.FlowEffectiveStatus,
                        commandType ==
                            DynamicFlowFinalizeTopologyContract
                                .TerminateCommand
                            ? DynamicFlowEffectiveStatuses.Terminated
                            : DynamicFlowEffectiveStatuses.Invalidated)
                    .Set(
                        assignment =>
                            assignment.InvalidatedByFlowEventId,
                        eventId)
                    .Set(assignment => assignment.IsActive, false)
                    .Set(assignment => assignment.UpdatedAtUtc, now)
                    .Set(
                        assignment => assignment.UpdatedByUserId,
                        actorUserId),
                cancellationToken: ct);
            await _ctx.WorkTemplateAssignees.UpdateManyAsync(
                session,
                binding =>
                    assignmentIds.Contains(binding.WorkAssignmentId) &&
                    binding.IsActive &&
                    !binding.IsDeleted,
                Builders<WorkTemplateAssignee>.Update
                    .Set(binding => binding.IsActive, false)
                    .Set(binding => binding.UpdatedAtUtc, now)
                    .Set(binding => binding.UpdatedByUserId, actorUserId),
                cancellationToken: ct);
            await _ctx.WorkAssignmentReports.UpdateManyAsync(
                session,
                report =>
                    assignmentIds.Contains(report.WorkAssignmentId) &&
                    report.IsCurrent &&
                    !report.IsDeleted,
                Builders<WorkAssignmentReport>.Update
                    .Set(report => report.IsCurrent, false)
                    .Set(report => report.IsActive, false)
                    .Set(report => report.InvalidatedByFlowEventId, eventId)
                    .Set(report => report.DeactivatedAtUtc, now)
                    .Set(
                        report => report.DeactivatedByUserId,
                        actorUserId)
                    .Set(
                        report => report.DeactivationReason,
                        "DYNAMIC_FLOW_EXECUTION_EPOCH_INVALIDATED")
                    .Set(report => report.UpdatedAtUtc, now)
                    .Set(report => report.UpdatedByUserId, actorUserId),
                cancellationToken: ct);
            await InvalidateStatisticRowsAsync(
                session,
                assignmentIds,
                eventId,
                now,
                actorUserId,
                ct);
        }
        await _ctx.DynamicFlowRuntimeOutbox.UpdateManyAsync(
            session,
            item =>
                item.FlowInstanceId == instance.Id &&
                stepIds.Contains(item.StepInstanceId) &&
                item.Status != DynamicFlowRuntimeOutboxStatuses.Completed,
            Builders<DynamicFlowRuntimeOutboxItem>.Update
                .Set(
                    item => item.Status,
                    DynamicFlowRuntimeOutboxStatuses.DeadLetter)
                .Set(
                    item => item.LastErrorCode,
                    "DYNAMIC_FLOW_EXECUTION_EPOCH_INVALIDATED")
                .Set(item => item.LeaseId, null)
                .Set(item => item.LeaseUntilUtc, null)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private async Task InvalidateStatisticRowsAsync(
        IClientSessionHandle session,
        IReadOnlyCollection<string> assignmentIds,
        string eventId,
        DateTime now,
        string actorUserId,
        CancellationToken ct)
    {
        await _ctx.WorkReportFieldStatValues.UpdateManyAsync(
            session,
            row =>
                assignmentIds.Contains(row.WorkAssignmentId) &&
                !row.IsDeleted &&
                row.DirectProjection == null,
            Builders<WorkReportFieldStatValue>.Update
                .Set(
                    row => row.FlowEffectiveStatus,
                    DynamicFlowEffectiveStatuses.Invalidated)
                .Set(row => row.InvalidatedByFlowEventId, eventId)
                .Set(row => row.UpdatedAtUtc, now)
                .Set(row => row.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
        await _ctx.WorkReportTableStatValues.UpdateManyAsync(
            session,
            row =>
                assignmentIds.Contains(row.WorkAssignmentId) &&
                !row.IsDeleted &&
                row.DirectProjection == null,
            Builders<WorkReportTableStatValue>.Update
                .Set(
                    row => row.FlowEffectiveStatus,
                    DynamicFlowEffectiveStatuses.Invalidated)
                .Set(row => row.InvalidatedByFlowEventId, eventId)
                .Set(row => row.UpdatedAtUtc, now)
                .Set(row => row.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
        await _ctx.WorkReportLabelStatValues.UpdateManyAsync(
            session,
            row =>
                assignmentIds.Contains(row.WorkAssignmentId) &&
                !row.IsDeleted &&
                row.DirectProjection == null,
            Builders<WorkReportLabelStatValue>.Update
                .Set(
                    row => row.FlowEffectiveStatus,
                    DynamicFlowEffectiveStatuses.Invalidated)
                .Set(row => row.InvalidatedByFlowEventId, eventId)
                .Set(row => row.UpdatedAtUtc, now)
                .Set(row => row.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private async Task CreateReplacementEpochAsync(
        IClientSessionHandle session,
        DynamicFlowInstance instance,
        DynamicFlowFinalizeTopology topology,
        IReadOnlyCollection<DynamicFlowStepInstance> previousSteps,
        int executionEpoch,
        string commandId,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var snapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(
                session,
                candidate =>
                    candidate.Id == instance.ParticipantSnapshotId &&
                    candidate.FlowInstanceId == instance.Id)
            .SingleAsync(ct);
        var previousByTarget = previousSteps.ToDictionary(
            step => step.TargetUnitId,
            StringComparer.Ordinal);
        var orderedBindings = snapshot.Bindings
            .OrderBy(binding => binding.TargetUnitId, StringComparer.Ordinal)
            .ToArray();
        if (orderedBindings.Length == 0 ||
            orderedBindings.Any(binding =>
                !previousByTarget.ContainsKey(binding.TargetUnitId)))
        {
            throw EpochValidation(
                "DYNAMIC_FLOW_EPOCH_PARTICIPANT_SNAPSHOT_DRIFT",
                DynamicFlowFinalizeTopologyContract.RestartCommand);
        }

        for (var index = 0; index < orderedBindings.Length; index++)
        {
            var binding = orderedBindings[index];
            var previous = previousByTarget[binding.TargetUnitId];
            var branchId =
                DynamicFlowFinalizeTopologyContract.BuildBranchId(
                    instance.Id,
                    executionEpoch,
                    binding.TargetUnitId);
            var stepId =
                DynamicFlowFinalizeTopologyContract.BuildStepInstanceId(
                    instance.Id,
                    executionEpoch,
                    topology.EntryNode.NodeId,
                    branchId);
            var assignmentId =
                DynamicFlowFinalizeTopologyContract.BuildAssignmentId(stepId);
            var step = new DynamicFlowStepInstance
            {
                Id = stepId,
                FlowInstanceId = instance.Id,
                FlowStepId = topology.EntryNode.NodeId,
                FlowStepCode = topology.EntryNode.NodeCode,
                ExecutionEpoch = executionEpoch,
                DefinitionRevision = instance.DefinitionRevision,
                StepOrder = previous.StepOrder,
                FormNodeId = topology.EntryForm.FormNodeId,
                FormFamilyId = topology.EntryForm.DynamicFormFamilyId!,
                FormVersionId = topology.EntryForm.DynamicFormTemplateId,
                FormVersionNo = topology.EntryForm.DynamicFormVersionNo!.Value,
                FormSchemaHash =
                    topology.EntryForm.DynamicFormSchemaHash!,
                FormSnapshotHash =
                    topology.EntryForm.DynamicFormSnapshotHash!,
                TargetUnitId = binding.TargetUnitId,
                ParticipantUserIds = binding.AssigneeUserIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
                ParticipantSnapshotId = snapshot.Id,
                AttemptNo = 1,
                ReviewCycleNo = 1,
                BranchId = branchId,
                NextNodeIds = previous.NextNodeIds.ToList(),
                IsTerminalNode = previous.IsTerminalNode,
                IsCanonicalEpoch = true,
                ResultOwnerIdentity =
                    DynamicFlowFinalizeTopologyContract.BuildOwnerIdentity(
                        instance.Id,
                        executionEpoch,
                        topology.EntryNode.NodeId,
                        branchId,
                        "result"),
                StatisticOwnerIdentity =
                    DynamicFlowFinalizeTopologyContract.BuildOwnerIdentity(
                        instance.Id,
                        executionEpoch,
                        topology.EntryNode.NodeId,
                        branchId,
                        "statistics"),
                AssignmentId = assignmentId,
                State = DynamicFlowStepStates.Materializing,
                Revision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId,
                IsDeleted = false
            };
            await _ctx.DynamicFlowStepInstances.InsertOneAsync(
                session,
                step,
                cancellationToken: ct);
            foreach (var participant in binding.Participants
                         .OrderBy(
                             participant => participant.UserId,
                             StringComparer.Ordinal))
            {
                await _ctx.WorkTemplateAssignees.InsertOneAsync(
                    session,
                    new WorkTemplateAssignee
                    {
                        Id = StableTopologyObjectId(
                            $"{assignmentId}\nbinding\n{participant.UserId}"),
                        WorkId = instance.WorkId,
                        WorkAssignmentId = assignmentId,
                        DynamicFormTemplateId =
                            topology.EntryForm.DynamicFormTemplateId,
                        DynamicFormFamilyId =
                            topology.EntryForm.DynamicFormFamilyId,
                        DynamicFormVersionNo =
                            topology.EntryForm.DynamicFormVersionNo,
                        DynamicFormSchemaHash =
                            topology.EntryForm.DynamicFormSchemaHash,
                        AssigneeUserId = participant.UserId,
                        AssigneeUsername = participant.Username,
                        AssigneeFullName = participant.FullName,
                        AssigneeUnitId = participant.UnitId,
                        AssigneeUnitSymbol = participant.UnitSymbol,
                        AssigneeUnitShortName =
                            participant.UnitShortName,
                        AssigneeUnitName = participant.UnitName,
                        AssignmentType = WorkAssignmentTypes.Once,
                        AggregationType = WorkAggregationTypes.Matrix,
                        IsActive = true,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        CreatedByUserId = actorUserId,
                        UpdatedByUserId = actorUserId,
                        IsDeleted = false
                    },
                    cancellationToken: ct);
            }

            var payload = new BsonDocument
            {
                { "commandId", commandId },
                { "actorUserId", actorUserId },
                { "issuerUnitId", instance.IssuerUnitId },
                { "archetypeId", instance.ArchetypeId },
                { "branchOrdinal", index + 1 },
                { "targetUnitId", binding.TargetUnitId },
                { "assignmentId", assignmentId },
                { "periodKey", instance.PeriodKey },
                {
                    "scheduleIdentityJson",
                    instance.ScheduleIdentityJson
                },
                {
                    "scheduleIdentityHash",
                    instance.ScheduleIdentityHash
                },
                { "executionEpoch", executionEpoch },
                { "branchId", branchId },
                { "attemptNo", 1 },
                { "reviewCycleNo", 1 },
                {
                    "participants",
                    new BsonArray(binding.Participants
                        .OrderBy(
                            participant => participant.UserId,
                            StringComparer.Ordinal)
                        .Select(TopologyParticipantDocument))
                }
            };
            await _ctx.DynamicFlowRuntimeOutbox.InsertOneAsync(
                session,
                new DynamicFlowRuntimeOutboxItem
                {
                    Id = StableTopologyObjectId(
                        $"{stepId}\noutbox\nmaterialize"),
                    FlowInstanceId = instance.Id,
                    StepInstanceId = stepId,
                    Operation =
                        DynamicFlowRuntimeMaterializationOperations
                            .MaterializeEntryAssignment,
                    DedupeKey =
                        $"{instance.Id}:{executionEpoch}:" +
                        $"{topology.EntryNode.NodeId}:" +
                        $"{binding.TargetUnitId}:materialize",
                    Payload = payload,
                    PayloadHash =
                        DynamicFlowSequentialTopologyContract.Hash(
                            payload.ToJson()),
                    Status = DynamicFlowRuntimeOutboxStatuses.Pending,
                    AttemptCount = 0,
                    RepairEpoch = 0,
                    NextAttemptAtUtc = now,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    UpdatedByUserId = actorUserId
                },
                cancellationToken: ct);
        }

        await _ctx.DynamicFlowExecutionEpochs.InsertOneAsync(
            session,
            new DynamicFlowExecutionEpoch
            {
                Id =
                    DynamicFlowFinalizeTopologyContract.BuildEpochId(
                        instance.Id,
                        executionEpoch),
                FlowInstanceId = instance.Id,
                ExecutionEpoch = executionEpoch,
                State = DynamicFlowExecutionEpochStates.Active,
                CheckpointNodeId = topology.EntryNode.NodeId,
                IsCanonical = true,
                OpenedByCommandId = commandId,
                OpenedAtUtc = now,
                Revision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId,
                IsDeleted = false
            },
            cancellationToken: ct);
    }

    private async Task CloseExecutionEpochAsync(
        IClientSessionHandle session,
        DynamicFlowInstance instance,
        string commandType,
        string commandId,
        string eventId,
        int? replacementEpoch,
        DateTime now,
        string actorUserId,
        CancellationToken ct)
    {
        var state = commandType switch
        {
            DynamicFlowFinalizeTopologyContract.FinalizeCommand =>
                DynamicFlowExecutionEpochStates.Finalized,
            DynamicFlowFinalizeTopologyContract.RollbackCommand =>
                DynamicFlowExecutionEpochStates.RolledBack,
            DynamicFlowFinalizeTopologyContract.TerminateCommand =>
                DynamicFlowExecutionEpochStates.Terminated,
            DynamicFlowFinalizeTopologyContract.RestartCommand =>
                DynamicFlowExecutionEpochStates.Restarted,
            _ => throw new InvalidOperationException(
                "DYNAMIC_FLOW_EPOCH_COMMAND_UNSUPPORTED")
        };
        var epochId = DynamicFlowFinalizeTopologyContract.BuildEpochId(
            instance.Id,
            instance.ExecutionEpoch);
        var closed = await _ctx.DynamicFlowExecutionEpochs.UpdateOneAsync(
            session,
            epoch =>
                epoch.Id == epochId &&
                epoch.FlowInstanceId == instance.Id &&
                epoch.ExecutionEpoch == instance.ExecutionEpoch &&
                epoch.State == DynamicFlowExecutionEpochStates.Active &&
                !epoch.IsDeleted,
            Builders<DynamicFlowExecutionEpoch>.Update
                .Set(epoch => epoch.State, state)
                .Set(
                    epoch => epoch.IsCanonical,
                    commandType ==
                        DynamicFlowFinalizeTopologyContract.FinalizeCommand ||
                    commandType ==
                        DynamicFlowFinalizeTopologyContract.TerminateCommand)
                .Set(epoch => epoch.ClosedByCommandId, commandId)
                .Set(epoch => epoch.TerminalEventId, eventId)
                .Set(
                    epoch => epoch.ReplacedByExecutionEpoch,
                    replacementEpoch)
                .Set(epoch => epoch.ClosedAtUtc, now)
                .Set(epoch => epoch.UpdatedAtUtc, now)
                .Set(epoch => epoch.UpdatedByUserId, actorUserId)
                .Inc(epoch => epoch.Revision, 1),
            cancellationToken: ct);
        if (closed.ModifiedCount != 1)
            throw EpochRevisionConflict(instance);
    }

    private Task<DynamicFlowRuntimeCommandReceipt?> FindEpochReceiptAsync(
        string instanceId,
        string commandType,
        string commandId,
        CancellationToken ct)
        => _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(receipt =>
                receipt.ScopeKind == DynamicFlowCommandScopeKinds.Instance &&
                receipt.ScopeId == instanceId &&
                receipt.CommandType == commandType &&
                receipt.CommandId == commandId)
            .FirstOrDefaultAsync(ct);

    private static void RequireEpochReplay(
        DynamicFlowRuntimeCommandReceipt receipt,
        string requestHash,
        string actorUserId)
    {
        var expectedIdentity =
            DynamicFlowSequentialTopologyContract.Hash(
                $"{actorUserId}\n{receipt.ScopeId}\n" +
                $"{receipt.CommandType}\n{receipt.CommandId}");
        if (receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
            receipt.ResultSnapshot is null ||
            receipt.RequestHash != requestHash ||
            receipt.CommandIdentityHash != expectedIdentity ||
            receipt.ResultSnapshotHash !=
                DynamicFlowSequentialTopologyContract.Hash(
                    receipt.ResultSnapshot.ToJson()))
        {
            throw EpochValidation(
                "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
                receipt.CommandType);
        }
    }

    private static void RequireEpochCommandPreconditions(
        DynamicFlowInstance instance,
        DynamicFlowFinalizeTopology topology,
        string commandType,
        string? checkpointNodeId,
        int expectedEpoch,
        long expectedRevision)
    {
        if (instance.ExecutionEpoch != expectedEpoch ||
            instance.Revision != expectedRevision)
        {
            throw EpochRevisionConflict(instance);
        }
        if (instance.State is DynamicFlowInstanceStates.Finalized or
            DynamicFlowInstanceStates.Terminated or
            DynamicFlowInstanceStates.Failed)
        {
            throw EpochValidation(
                "DYNAMIC_FLOW_EPOCH_IRREVERSIBLE",
                commandType);
        }
        if (instance.State is not (
                DynamicFlowInstanceStates.Active or
                DynamicFlowInstanceStates.Completed))
        {
            throw EpochValidation(
                "DYNAMIC_FLOW_EPOCH_STATE_INVALID",
                commandType);
        }
        if (commandType ==
            DynamicFlowFinalizeTopologyContract.FinalizeCommand)
        {
            if (checkpointNodeId is not null)
                throw EpochValidation(
                    "DYNAMIC_FLOW_FINALIZE_CHECKPOINT_FORBIDDEN",
                    commandType);
            if (instance.State != DynamicFlowInstanceStates.Completed)
                throw EpochValidation(
                    "DYNAMIC_FLOW_FINALIZE_PRECONDITION_NOT_MET",
                    commandType);
            return;
        }
        if (commandType ==
            DynamicFlowFinalizeTopologyContract.TerminateCommand)
        {
            if (checkpointNodeId is not null)
                throw EpochValidation(
                    "DYNAMIC_FLOW_TERMINATE_CHECKPOINT_FORBIDDEN",
                    commandType);
            return;
        }
        if (checkpointNodeId != topology.RollbackTargetNodeId)
        {
            throw EpochValidation(
                "DYNAMIC_FLOW_EPOCH_CHECKPOINT_NOT_ALLOWED",
                commandType);
        }
    }

    private static string? NormalizeEpochCheckpoint(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > 128)
            throw EpochValidation(
                "DYNAMIC_FLOW_EPOCH_CHECKPOINT_INVALID",
                "EPOCH");
        return value;
    }

    private static string EpochEventType(string commandType)
        => commandType switch
        {
            DynamicFlowFinalizeTopologyContract.FinalizeCommand =>
                DynamicFlowFinalizeTopologyContract.FinalizedEvent,
            DynamicFlowFinalizeTopologyContract.RollbackCommand =>
                DynamicFlowFinalizeTopologyContract.RolledBackEvent,
            DynamicFlowFinalizeTopologyContract.TerminateCommand =>
                DynamicFlowFinalizeTopologyContract.TerminatedEvent,
            DynamicFlowFinalizeTopologyContract.RestartCommand =>
                DynamicFlowFinalizeTopologyContract.RestartedEvent,
            _ => throw new InvalidOperationException(
                "DYNAMIC_FLOW_EPOCH_COMMAND_UNSUPPORTED")
        };

    private static BsonDocument EpochResultDocument(
        DynamicFlowEpochCommandResponse result)
        => new()
        {
            { "commandId", result.CommandId },
            { "action", result.Action },
            { "flowInstanceId", result.FlowInstanceId },
            {
                "previousExecutionEpoch",
                result.PreviousExecutionEpoch
            },
            { "executionEpoch", result.ExecutionEpoch },
            { "instanceRevision", result.InstanceRevision },
            { "instanceState", result.InstanceState },
            { "eventId", result.EventId },
            {
                "rebuildIntentId",
                BsonValue.Create(result.RebuildIntentId)
            },
            { "invalidatedStepCount", result.InvalidatedStepCount },
            {
                "invalidatedGatewayCount",
                result.InvalidatedGatewayCount
            },
            {
                "invalidatedAssignmentCount",
                result.InvalidatedAssignmentCount
            },
            {
                "invalidatedReportCount",
                result.InvalidatedReportCount
            },
            {
                "businessWritePerformed",
                result.BusinessWritePerformed
            }
        };

    private static DynamicFlowEpochCommandResponse EpochResponse(
        BsonDocument snapshot,
        bool replayed)
        => new()
        {
            CommandId = snapshot["commandId"].AsString,
            Action = snapshot["action"].AsString,
            FlowInstanceId = snapshot["flowInstanceId"].AsString,
            PreviousExecutionEpoch =
                snapshot["previousExecutionEpoch"].AsInt32,
            ExecutionEpoch = snapshot["executionEpoch"].AsInt32,
            InstanceRevision = snapshot["instanceRevision"].AsInt64,
            InstanceState = snapshot["instanceState"].AsString,
            EventId = snapshot["eventId"].AsString,
            RebuildIntentId =
                snapshot["rebuildIntentId"].IsBsonNull
                    ? null
                    : snapshot["rebuildIntentId"].AsString,
            InvalidatedStepCount =
                snapshot["invalidatedStepCount"].AsInt32,
            InvalidatedGatewayCount =
                snapshot["invalidatedGatewayCount"].AsInt32,
            InvalidatedAssignmentCount =
                snapshot["invalidatedAssignmentCount"].AsInt32,
            InvalidatedReportCount =
                snapshot["invalidatedReportCount"].AsInt32,
            BusinessWritePerformed =
                snapshot["businessWritePerformed"].AsBoolean,
            Replayed = replayed
        };

    private static AppException EpochRevisionConflict(
        DynamicFlowInstance instance)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new
            {
                reason = "DYNAMIC_FLOW_REVISION_CONFLICT",
                action = "EXECUTION_EPOCH",
                currentExecutionEpoch = instance.ExecutionEpoch,
                currentInstanceRevision = instance.Revision
            });

    private static AppException EpochValidation(
        string reason,
        string action)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reason, action });
}
