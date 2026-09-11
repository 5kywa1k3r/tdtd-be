using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.DTOs.WorkAssignments;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments;

namespace tdtd_be.Services.DynamicFlows;

public sealed partial class DynamicFlowRuntimeService : IDynamicFlowRuntimeService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentService _assignments;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkAssignmentAdvancedSummaryDirtyService _advancedSummaryDirty;
    private readonly IDynamicFlowRuntimeActivationPolicy _runtimeActivation;
    private readonly IDynamicFlowRuntimeMaterializer _runtimeMaterializer;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;

    public DynamicFlowRuntimeService(
        MongoDbContext ctx,
        IWorkAssignmentService assignments,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkAssignmentAdvancedSummaryDirtyService advancedSummaryDirty,
        IDynamicFlowRuntimeActivationPolicy runtimeActivation,
        IDynamicFlowRuntimeMaterializer runtimeMaterializer,
        IDynamicFlowDefinitionTransactionRunner transactions)
    {
        _ctx = ctx;
        _assignments = assignments;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _advancedSummaryDirty = advancedSummaryDirty;
        _runtimeActivation = runtimeActivation;
        _runtimeMaterializer = runtimeMaterializer;
        _transactions = transactions;
    }

    public async Task<DynamicFlowPreflightResponse> PreflightAsync(
        string workId,
        DynamicFlowPreflightRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new DynamicFlowPreflightRequest();
        workId = NormalizeRequiredObjectId(workId, "workId");
        var versionId = NormalizeRequiredObjectId(req.FlowTemplateVersionId, "flowTemplateVersionId");
        var actor = await _ctx.Users
            .Find(x => x.Id == actorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized();

        // Only identity pins and work ownership are loaded before authorization.
        // Family lifecycle, payload and integrity details remain hidden.
        var versionPin = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.Id == versionId && !x.IsDeleted)
            .Project(x => new { x.TemplateId, x.VersionNo })
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        var hasParticipantGrant = await _ctx.WorkAssignments
            .Find(DynamicFlowTemplateReadAccess.BuildAssignmentParticipantFilter(
                actorUserId,
                versionPin.TemplateId,
                versionPin.VersionNo))
            .Project(x => x.Id)
            .AnyAsync(ct);
        var hasAssignmentTopology = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .Project(x => x.Id)
            .AnyAsync(ct);
        var hasRuntimeOwner = await _ctx.DynamicFlowInstances
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .Project(x => x.Id)
            .AnyAsync(ct);
        var ownsWork = string.Equals(work.CreatedByUserId, actorUserId, StringComparison.Ordinal) ||
                       string.Equals(work.Owner?.UserId, actorUserId, StringComparison.Ordinal) ||
                       string.Equals(work.LeaderDirectiveUserId, actorUserId, StringComparison.Ordinal);
        var hasRootIssuerGrant = ownsWork && DynamicFlowTemplateReadAccess.CanCreateDefinition(actor);
        if (!hasParticipantGrant && !hasRootIssuerGrant)
            throw RuntimeAccessForbidden();

        var family = await _ctx.DynamicFlowTemplates
            .Find(x => x.Id == versionPin.TemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(x =>
                x.Id == versionId &&
                x.TemplateId == family.Id &&
                x.VersionNo == versionPin.VersionNo &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        if (!string.Equals(family.Status, DynamicFlowTemplateStatuses.Active, StringComparison.Ordinal) ||
            !string.Equals(version.Status, DynamicFlowTemplateVersionStatuses.Locked, StringComparison.Ordinal))
            throw RuntimeAccessForbidden();

        await ValidateLockedSnapshotIntegrityAsync(family, version, ct);
        var targetIds = DynamicFlowRuntimePreflightContract.NormalizeTargets(req.TargetUnitIds);
        var units = await _ctx.Units
            .Find(unit => targetIds.Contains(unit.Id) && !unit.IsDeleted)
            .ToListAsync(ct);
        if (units.Count != targetIds.Count)
            throw RuntimeAccessForbidden();
        var users = await _ctx.Users
            .Find(user => user.UnitId != null && targetIds.Contains(user.UnitId) && !user.IsDeleted)
            .ToListAsync(ct);
        var unitMap = units.ToDictionary(unit => unit.Id, StringComparer.Ordinal);
        var participants = targetIds.ToDictionary(
            unitId => unitId,
            unitId => (IReadOnlyList<DynamicFlowParticipantUserSnapshotDto>)users
                .Where(user => string.Equals(user.UnitId, unitId, StringComparison.Ordinal))
                .OrderBy(user => user.Id, StringComparer.Ordinal)
                .Select(user =>
                {
                    unitMap.TryGetValue(unitId, out var unit);
                    return new DynamicFlowParticipantUserSnapshotDto
                    {
                        UserId = user.Id,
                        Username = user.Username ?? string.Empty,
                        FullName = user.FullName ?? string.Empty,
                        UnitId = unitId,
                        UnitSymbol = unit?.Symbol,
                        UnitShortName = unit?.ShortName,
                        UnitName = unit?.FullName,
                        PositionCode = user.PositionCode,
                        PositionName = Positions.GetName(user.PositionCode)
                    };
                })
                .ToList(),
            StringComparer.Ordinal);

        var preflight = DynamicFlowRuntimePreflightContract.Build(
            workId,
            work.Type.ToString(),
            family,
            version,
            req,
            actorUserId,
            actor.UnitId ?? string.Empty,
            participants,
            _runtimeActivation.IsP6CandidateArchetypeEnabled);
        var isPeriodic =
            preflight.FlowPin.ArchetypeId ==
            DynamicFlowPeriodicTopologyContract.ArchetypeId;
        if (work.CompletedAtUtc.HasValue ||
            work.Status == WorkStatus.S3 ||
            string.Equals(
                work.AssignmentTopologyOwner,
                WorkAssignmentTopologyOwners.Legacy,
                StringComparison.Ordinal) ||
            (!isPeriodic &&
             (hasAssignmentTopology ||
              hasRuntimeOwner ||
              !string.IsNullOrWhiteSpace(work.DynamicFlowRuntimeInstanceId) ||
              string.Equals(
                  work.AssignmentTopologyOwner,
                  WorkAssignmentTopologyOwners.P5FlowRuntime,
                  StringComparison.Ordinal))))
        {
            preflight.Eligibility = DynamicFlowRuntimeEligibilityPolicy.BlockedPhase;
            preflight.BlockedUntilPhase = "P6";
        }
        else if (string.Equals(
                     preflight.Eligibility,
                     DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
                     StringComparison.Ordinal) &&
                 !CanExecuteRuntimePin(preflight.FlowPin))
        {
            // P6-01..11 may exercise the non-current v1.3 candidate only in
            // the explicit Testing fixture. Production remains on v1.2 until
            // the P6-12 promotion gate.
            preflight.Eligibility = DynamicFlowRuntimeEligibilityPolicy.BlockedPhase;
            preflight.BlockedUntilPhase = "P6";
        }

        return preflight;
    }

    public async Task<DynamicFlowConfirmResponse> ConfirmAsync(
        string workId,
        DynamicFlowConfirmRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        try
        {
            return await ConfirmCoreAsync(workId, req, actorUserId, ct);
        }
        catch (InvalidOperationException error) when (IsCanonicalRuntimeConfirmConflict(error))
        {
            throw MapCanonicalRuntimeConfirmConflict(error);
        }
    }

    private async Task<DynamicFlowConfirmResponse> ConfirmCoreAsync(
        string workId,
        DynamicFlowConfirmRequest req,
        string actorUserId,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        req ??= new DynamicFlowConfirmRequest();
        workId = NormalizeRequiredObjectId(workId, "workId");
        var versionId = NormalizeRequiredObjectId(
            req.FlowTemplateVersionId,
            "flowTemplateVersionId");
        var actorExists = await _ctx.Users
            .Find(x => x.Id == actorUserId && !x.IsDeleted)
            .Project(x => x.Id)
            .AnyAsync(ct);
        if (!actorExists)
            throw AppExceptionFactory.Unauthorized();

        var commandIdentityHash = DynamicFlowRuntimePreflightContract.BuildCommandIdentityHash(
            workId,
            versionId,
            req,
            actorUserId);
        var existingReceipt = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(receipt =>
                receipt.ScopeKind == DynamicFlowCommandScopeKinds.Launch &&
                receipt.WorkId == workId &&
                receipt.FlowTemplateVersionId == versionId &&
                receipt.CommandType == "LAUNCH" &&
                receipt.CommandId == req.CommandId.Trim())
            .FirstOrDefaultAsync(ct);
        if (existingReceipt is not null)
        {
            var issuerMatches = existingReceipt.FlowInstanceId is not null &&
                                await _ctx.DynamicFlowInstances
                                    .Find(instance =>
                                        instance.Id == existingReceipt.FlowInstanceId &&
                                        instance.WorkId == workId &&
                                        instance.FlowTemplateVersionId == versionId &&
                                        instance.IssuerUserId == actorUserId &&
                                        !instance.IsDeleted)
                                    .Project(instance => instance.Id)
                                    .AnyAsync(ct);
            if (!issuerMatches)
                throw RuntimeAccessForbidden();
            return await _runtimeMaterializer.ResumeExistingAsync(
                existingReceipt.Id,
                commandIdentityHash,
                req.SnapshotToken,
                actorUserId,
                ct);
        }

        var preflight = await PreflightAsync(workId, req, actorUserId, ct);
        if (CanExecuteRuntimePin(preflight.FlowPin) &&
            preflight.FlowPin.ArchetypeId !=
                DynamicFlowPeriodicTopologyContract.ArchetypeId &&
            string.Equals(
                preflight.Eligibility,
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
                StringComparison.Ordinal))
        {
            return await _runtimeMaterializer.ConfirmAndMaterializeAsync(
                preflight,
                req,
                actorUserId,
                ct);
        }

        // Candidate execution remains release-disabled until P5-08. Blocked
        // catalogs/phases and pre-release candidates stay zero-write.
        return DynamicFlowRuntimePreflightContract.Confirm(preflight, req);
    }

    private bool CanExecuteRuntimePin(DynamicFlowExactPinDto pin)
    {
        if (DynamicFlowRuntimeEligibilityPolicy.IsCurrentCatalog(
                pin.CatalogVersion,
                pin.CatalogSemanticHash))
        {
            return pin.ArchetypeId is "FLOW-T01" or "FLOW-T02";
        }

        return _runtimeActivation.CanExecuteP6CandidatePin(
            pin.CatalogVersion,
            pin.CatalogSemanticHash,
            pin.ArchetypeId);
    }

    public async Task<DynamicFlowSupplementalCommandResponse>
        AddSupplementalStepAsync(
            string workId,
            string flowInstanceId,
            DynamicFlowSupplementalAddRequest req,
            string actorUserId,
            CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new DynamicFlowSupplementalAddRequest();
        workId = NormalizeRequiredObjectId(workId, "workId");
        flowInstanceId = NormalizeRequiredObjectId(
            flowInstanceId,
            "flowInstanceId");
        var commandId = NormalizeRequiredCommandId(req.CommandId);
        if (req.ExpectedInstanceRevision is null or < 1)
            throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_EXPECTED_REVISION_REQUIRED");

        var formNodeId = req.FormNodeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(formNodeId) || formNodeId.Length > 160)
            throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_FORM_NODE_REQUIRED");
        var targetUnitId = NormalizeRequiredObjectId(
            req.TargetUnitId,
            "targetUnitId");
        var requestedParticipantIds = (req.ParticipantUserIds ?? new())
            .Select(value => NormalizeRequiredObjectId(value, "participantUserId"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var completionRequired = req.CompletionRequired ?? true;

        var instance = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == flowInstanceId &&
                candidate.WorkId == workId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        RequireSupplementalCoordinator(instance, actorUserId);
        var topology = DynamicFlowSupplementalTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        if (!topology.NodesByFormNodeId.TryGetValue(
                formNodeId,
                out var node) ||
            !topology.FormsByFormNodeId.TryGetValue(formNodeId, out var form))
        {
            throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_NODE_FORM_NOT_ALLOWED");
        }

        var snapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(candidate =>
                candidate.Id == instance.ParticipantSnapshotId &&
                candidate.FlowInstanceId == instance.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_PARTICIPANT_SNAPSHOT_MISSING");
        var binding = snapshot.Bindings.SingleOrDefault(candidate =>
            candidate.TargetUnitId == targetUnitId)
            ?? throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_TARGET_NOT_ALLOWED");
        var participantIds = requestedParticipantIds.Length == 0
            ? binding.AssigneeUserIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
            : requestedParticipantIds;
        if (participantIds.Length == 0 ||
            participantIds.Any(id =>
                !binding.AssigneeUserIds.Contains(id, StringComparer.Ordinal)) ||
            !node.DeclaredRoles.Contains(
                DynamicFlowRuntimePlanner.AssignmentFlowRole,
                StringComparer.OrdinalIgnoreCase))
        {
            throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_ACTOR_NOT_ALLOWED");
        }
        var participants = binding.Participants
            .Where(user =>
                participantIds.Contains(user.UserId, StringComparer.Ordinal))
            .OrderBy(user => user.UserId, StringComparer.Ordinal)
            .ToArray();
        if (participants.Length != participantIds.Length)
            throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_ACTOR_NOT_ALLOWED");

        var supplementalStepId =
            DynamicFlowSupplementalTopologyContract.BuildSupplementalStepId(
                instance.Id,
                instance.ExecutionEpoch,
                commandId);
        var requestHash = DynamicFlowSequentialTopologyContract.Hash(
            string.Join(
                "\n",
                workId,
                instance.Id,
                instance.ExecutionEpoch,
                commandId,
                formNodeId,
                targetUnitId,
                string.Join(",", participantIds),
                completionRequired ? "required" : "optional"));
        var existing = await FindSupplementalReceiptAsync(
            instance.Id,
            DynamicFlowSupplementalTopologyContract.AddCommand,
            commandId,
            ct);
        if (existing is not null)
            return ParseSupplementalReplay(existing, requestHash);

        var now = DateTime.UtcNow;
        var response = await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var replay = await _ctx.DynamicFlowRuntimeCommandReceipts
                    .Find(
                        session,
                        receipt =>
                            receipt.ScopeKind ==
                                DynamicFlowCommandScopeKinds.Instance &&
                            receipt.ScopeId == instance.Id &&
                            receipt.CommandType ==
                                DynamicFlowSupplementalTopologyContract
                                    .AddCommand &&
                            receipt.CommandId == commandId)
                    .FirstOrDefaultAsync(transactionCt);
                if (replay is not null)
                    return ParseSupplementalReplay(replay, requestHash);

                var current = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        candidate =>
                            candidate.Id == instance.Id &&
                            candidate.WorkId == workId &&
                            !candidate.IsDeleted)
                    .SingleAsync(transactionCt);
                RequireSupplementalCoordinator(current, actorUserId);
                if (current.Revision != req.ExpectedInstanceRevision)
                    throw SupplementalRevisionConflict(current.Revision, null);
                if (current.ExecutionEpoch != instance.ExecutionEpoch ||
                    current.TopologySnapshotHash != topology.TopologyHash)
                {
                    throw SupplementalValidation(
                        "DYNAMIC_FLOW_SUPPLEMENTAL_EPOCH_OR_TOPOLOGY_DRIFT");
                }
                var count = await _ctx.DynamicFlowStepInstances
                    .CountDocumentsAsync(
                        session,
                        step =>
                            step.FlowInstanceId == current.Id &&
                            step.ExecutionEpoch == current.ExecutionEpoch &&
                            step.IsSupplemental &&
                            !step.IsDeleted,
                        cancellationToken: transactionCt);
                if (count >=
                    DynamicFlowSupplementalTopologyContract.MaxStepsPerEpoch)
                {
                    throw SupplementalValidation(
                        "DYNAMIC_FLOW_SUPPLEMENTAL_EPOCH_CAP_REACHED");
                }

                var branchId =
                    DynamicFlowSupplementalTopologyContract.BuildBranchId(
                        supplementalStepId);
                var assignmentId =
                    DynamicFlowSupplementalTopologyContract.BuildAssignmentId(
                        supplementalStepId);
                var eventId = StableTopologyObjectId(
                    $"{current.Id}\n{current.ExecutionEpoch}\n{commandId}\n" +
                    DynamicFlowSupplementalTopologyContract.AddedEvent);
                var step = new DynamicFlowStepInstance
                {
                    Id = supplementalStepId,
                    FlowInstanceId = current.Id,
                    FlowStepId = node.NodeId,
                    FlowStepCode = node.NodeCode,
                    ExecutionEpoch = current.ExecutionEpoch,
                    DefinitionRevision = current.DefinitionRevision,
                    StepOrder = count >= int.MaxValue ? int.MaxValue : (int)count + 1,
                    FormNodeId = form.FormNodeId,
                    FormFamilyId = form.DynamicFormFamilyId!,
                    FormVersionId = form.DynamicFormTemplateId,
                    FormVersionNo = form.DynamicFormVersionNo!.Value,
                    FormSchemaHash = form.DynamicFormSchemaHash!,
                    FormSnapshotHash = form.DynamicFormSnapshotHash!,
                    TargetUnitId = targetUnitId,
                    ParticipantUserIds = participantIds.ToList(),
                    ParticipantSnapshotId = current.ParticipantSnapshotId,
                    AttemptNo = 1,
                    ReviewCycleNo = 1,
                    BranchId = branchId,
                    ActivatedByTransitionId = $"supplemental:{supplementalStepId}",
                    NextNodeIds = new(),
                    IsTerminalNode = true,
                    IsSupplemental = true,
                    SupplementalStepId = supplementalStepId,
                    RequestedByUserId = actorUserId,
                    CompletionRequired = completionRequired,
                    ResultOwnerIdentity =
                        $"supplemental-result:{current.Id}:{current.ExecutionEpoch}:{supplementalStepId}",
                    StatisticOwnerIdentity =
                        $"supplemental-statistics:{current.Id}:{current.ExecutionEpoch}:{supplementalStepId}",
                    AssignmentId = assignmentId,
                    State = DynamicFlowStepStates.Materializing,
                    Revision = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    CreatedByUserId = actorUserId,
                    UpdatedByUserId = actorUserId,
                    IsDeleted = false
                };
                var outboxPayload = new BsonDocument
                {
                    { "commandId", commandId },
                    { "actorUserId", actorUserId },
                    { "issuerUnitId", current.IssuerUnitId },
                    { "archetypeId", current.ArchetypeId },
                    { "branchOrdinal", (int)count + 1 },
                    { "targetUnitId", targetUnitId },
                    { "assignmentId", assignmentId },
                    { "periodKey", current.PeriodKey },
                    { "scheduleIdentityJson", current.ScheduleIdentityJson },
                    { "scheduleIdentityHash", current.ScheduleIdentityHash },
                    { "executionEpoch", current.ExecutionEpoch },
                    { "branchId", branchId },
                    { "attemptNo", 1 },
                    { "reviewCycleNo", 1 },
                    { "supplementalStepId", supplementalStepId },
                    { "completionRequired", completionRequired },
                    {
                        "materializationEventSequence",
                        current.NextEventSequence + 1
                    },
                    {
                        "participants",
                        new BsonArray(participants.Select(
                            TopologyParticipantDocument))
                    }
                };
                var outbox = new DynamicFlowRuntimeOutboxItem
                {
                    Id = StableTopologyObjectId(
                        $"{supplementalStepId}\noutbox\nmaterialize"),
                    FlowInstanceId = current.Id,
                    StepInstanceId = step.Id,
                    Operation =
                        DynamicFlowRuntimeMaterializationOperations
                            .MaterializeSupplementalAssignment,
                    DedupeKey =
                        $"{current.Id}:{current.ExecutionEpoch}:{supplementalStepId}:supplemental:materialize",
                    Payload = outboxPayload,
                    PayloadHash =
                        DynamicFlowSequentialTopologyContract.Hash(
                            outboxPayload.ToJson()),
                    Status = DynamicFlowRuntimeOutboxStatuses.Pending,
                    AttemptCount = 0,
                    RepairEpoch = 0,
                    NextAttemptAtUtc = now,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                var result = BuildSupplementalResult(
                    commandId,
                    current,
                    current.Revision + 1,
                    step,
                    eventId,
                    DynamicFlowSupplementalTopologyContract.AddedEvent,
                    businessWritePerformed: true,
                    replayed: false);
                var resultSnapshot = SupplementalResultDocument(result);
                var receipt = new DynamicFlowRuntimeCommandReceipt
                {
                    Id = StableTopologyObjectId(
                        $"{DynamicFlowCommandScopeKinds.Instance}\n{current.Id}\n" +
                        $"{DynamicFlowSupplementalTopologyContract.AddCommand}\n{commandId}"),
                    ScopeKind = DynamicFlowCommandScopeKinds.Instance,
                    ScopeId = current.Id,
                    WorkId = current.WorkId,
                    FlowTemplateVersionId = current.FlowTemplateVersionId,
                    FlowInstanceId = current.Id,
                    CommandType =
                        DynamicFlowSupplementalTopologyContract.AddCommand,
                    CommandId = commandId,
                    RequestHash = requestHash,
                    CommandIdentityHash =
                        DynamicFlowSequentialTopologyContract.Hash(
                            $"{actorUserId}\n{current.Id}\n{commandId}\nadd"),
                    SnapshotToken =
                        DynamicFlowSequentialTopologyContract.Hash(
                            $"{requestHash}\n{current.TopologySnapshotHash}"),
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
                };
                var advanced = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                    session,
                    candidate =>
                        candidate.Id == current.Id &&
                        candidate.Revision == req.ExpectedInstanceRevision &&
                        candidate.ExecutionEpoch == current.ExecutionEpoch &&
                        candidate.State == DynamicFlowInstanceStates.Active &&
                        !candidate.IsDeleted,
                    Builders<DynamicFlowInstance>.Update
                        .Inc(candidate => candidate.Revision, 1)
                        .Inc(candidate => candidate.NextEventSequence, 2)
                        .Set(candidate => candidate.UpdatedAtUtc, now)
                        .Set(candidate => candidate.UpdatedByUserId, actorUserId),
                    cancellationToken: transactionCt);
                if (advanced.ModifiedCount != 1)
                    throw SupplementalRevisionConflict(current.Revision, null);

                await _ctx.DynamicFlowStepInstances.InsertOneAsync(
                    session,
                    step,
                    cancellationToken: transactionCt);
                foreach (var participant in participants)
                {
                    await _ctx.WorkTemplateAssignees.InsertOneAsync(
                        session,
                        new WorkTemplateAssignee
                        {
                            Id = StableTopologyObjectId(
                                $"{assignmentId}\nbinding\n{participant.UserId}"),
                            WorkId = current.WorkId,
                            WorkAssignmentId = assignmentId,
                            DynamicFormTemplateId = form.DynamicFormTemplateId,
                            DynamicFormFamilyId = form.DynamicFormFamilyId,
                            DynamicFormVersionNo = form.DynamicFormVersionNo,
                            DynamicFormSchemaHash = form.DynamicFormSchemaHash,
                            DynamicFlowSupplementalStepId = step.Id,
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
                        cancellationToken: transactionCt);
                }
                await _ctx.DynamicFlowRuntimeOutbox.InsertOneAsync(
                    session,
                    outbox,
                    cancellationToken: transactionCt);
                await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                    session,
                    new DynamicFlowRuntimeEvent
                    {
                        Id = eventId,
                        FlowInstanceId = current.Id,
                        StepInstanceId = step.Id,
                        ExecutionEpoch = current.ExecutionEpoch,
                        BranchId = branchId,
                        AttemptNo = 1,
                        Sequence = current.NextEventSequence,
                        EventType =
                            DynamicFlowSupplementalTopologyContract.AddedEvent,
                        CommandId = commandId,
                        ActorUserId = actorUserId,
                        VisibleUnitIds = new[]
                            {
                                targetUnitId,
                                current.IssuerUnitId
                            }
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToList(),
                        Payload = new BsonDocument
                        {
                            { "supplementalStepId", supplementalStepId },
                            { "formNodeId", formNodeId },
                            { "targetUnitId", targetUnitId },
                            { "participantUserIds", new BsonArray(participantIds) },
                            { "completionRequired", completionRequired },
                            { "requestedBy", actorUserId }
                        },
                        PayloadHash = requestHash,
                        OccurredAtUtc = now
                    },
                    cancellationToken: transactionCt);
                await _ctx.DynamicFlowRuntimeCommandReceipts.InsertOneAsync(
                    session,
                    receipt,
                    cancellationToken: transactionCt);
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    current.WorkId,
                    transactionCt);
                return result;
            },
            ct);

        await _runtimeMaterializer.ProcessPendingAsync(50, ct);
        var materialized = await _ctx.DynamicFlowStepInstances
            .Find(step => step.Id == response.StepInstanceId && !step.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (materialized is not null)
        {
            response.State = materialized.State;
            response.AssignmentId = materialized.AssignmentId;
        }
        return response;
    }

    public async Task<DynamicFlowSupplementalCommandResponse>
        CancelSupplementalStepAsync(
            string workId,
            string flowInstanceId,
            string supplementalStepId,
            DynamicFlowSupplementalCancelRequest req,
            string actorUserId,
            CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new DynamicFlowSupplementalCancelRequest();
        workId = NormalizeRequiredObjectId(workId, "workId");
        flowInstanceId = NormalizeRequiredObjectId(
            flowInstanceId,
            "flowInstanceId");
        supplementalStepId = NormalizeRequiredObjectId(
            supplementalStepId,
            "supplementalStepId");
        var commandId = NormalizeRequiredCommandId(req.CommandId);
        if (req.ExpectedInstanceRevision is null or < 1 ||
            req.ExpectedStepRevision is null or < 1)
        {
            throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_EXPECTED_REVISION_REQUIRED");
        }
        var reason = NormalizeOptionalText(req.Reason, 500);
        var instance = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == flowInstanceId &&
                candidate.WorkId == workId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        RequireSupplementalCoordinator(instance, actorUserId);
        _ = DynamicFlowSupplementalTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var requestHash = DynamicFlowSequentialTopologyContract.Hash(
            string.Join(
                "\n",
                workId,
                instance.Id,
                instance.ExecutionEpoch,
                commandId,
                supplementalStepId,
                req.ExpectedStepRevision,
                reason ?? string.Empty));
        var existing = await FindSupplementalReceiptAsync(
            instance.Id,
            DynamicFlowSupplementalTopologyContract.CancelCommand,
            commandId,
            ct);
        if (existing is not null)
            return ParseSupplementalReplay(existing, requestHash);

        var now = DateTime.UtcNow;
        return await _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                var replay = await _ctx.DynamicFlowRuntimeCommandReceipts
                    .Find(
                        session,
                        receipt =>
                            receipt.ScopeKind ==
                                DynamicFlowCommandScopeKinds.Instance &&
                            receipt.ScopeId == instance.Id &&
                            receipt.CommandType ==
                                DynamicFlowSupplementalTopologyContract
                                    .CancelCommand &&
                            receipt.CommandId == commandId)
                    .FirstOrDefaultAsync(transactionCt);
                if (replay is not null)
                    return ParseSupplementalReplay(replay, requestHash);
                var current = await _ctx.DynamicFlowInstances
                    .Find(
                        session,
                        candidate =>
                            candidate.Id == instance.Id &&
                            candidate.WorkId == workId &&
                            !candidate.IsDeleted)
                    .SingleAsync(transactionCt);
                RequireSupplementalCoordinator(current, actorUserId);
                var step = await _ctx.DynamicFlowStepInstances
                    .Find(
                        session,
                        candidate =>
                            candidate.Id == supplementalStepId &&
                            candidate.FlowInstanceId == current.Id &&
                            candidate.IsSupplemental &&
                            !candidate.IsDeleted)
                    .FirstOrDefaultAsync(transactionCt)
                    ?? throw SupplementalValidation(
                        "DYNAMIC_FLOW_SUPPLEMENTAL_STEP_NOT_FOUND");
                if (current.Revision != req.ExpectedInstanceRevision ||
                    step.Revision != req.ExpectedStepRevision)
                {
                    throw SupplementalRevisionConflict(
                        current.Revision,
                        step.Revision);
                }
                if (step.ExecutionEpoch != current.ExecutionEpoch ||
                    step.SupplementalStepId != step.Id)
                {
                    throw SupplementalValidation(
                        "DYNAMIC_FLOW_SUPPLEMENTAL_EPOCH_OR_IDENTITY_DRIFT");
                }
                if (step.State is DynamicFlowStepStates.Completed or
                    DynamicFlowStepStates.CancelledByGateway or
                    DynamicFlowStepStates.Failed or
                    DynamicFlowStepStates.Terminated)
                {
                    throw SupplementalValidation(
                        "DYNAMIC_FLOW_SUPPLEMENTAL_STEP_NOT_CANCELLABLE");
                }

                var eventId = StableTopologyObjectId(
                    $"{current.Id}\n{current.ExecutionEpoch}\n{commandId}\n" +
                    DynamicFlowSupplementalTopologyContract.CancelledEvent);
                var stepUpdate =
                    await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                        session,
                        candidate =>
                            candidate.Id == step.Id &&
                            candidate.Revision == req.ExpectedStepRevision &&
                            candidate.ExecutionEpoch == current.ExecutionEpoch &&
                            candidate.IsSupplemental &&
                            !candidate.IsDeleted,
                        Builders<DynamicFlowStepInstance>.Update
                            .Set(
                                candidate => candidate.State,
                                DynamicFlowStepStates.CancelledByGateway)
                            .Set(
                                candidate =>
                                    candidate.SupplementalCancelledAtUtc,
                                now)
                            .Set(
                                candidate =>
                                    candidate.SupplementalCancelledByUserId,
                                actorUserId)
                            .Set(
                                candidate =>
                                    candidate.SupplementalCancelReason,
                                reason)
                            .Set(candidate => candidate.UpdatedAtUtc, now)
                            .Set(
                                candidate => candidate.UpdatedByUserId,
                                actorUserId)
                            .Inc(candidate => candidate.Revision, 1),
                        cancellationToken: transactionCt);
                var instanceUpdate =
                    await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        candidate =>
                            candidate.Id == current.Id &&
                            candidate.Revision == req.ExpectedInstanceRevision &&
                            candidate.ExecutionEpoch == current.ExecutionEpoch &&
                            candidate.State == DynamicFlowInstanceStates.Active &&
                            !candidate.IsDeleted,
                        Builders<DynamicFlowInstance>.Update
                            .Inc(candidate => candidate.Revision, 1)
                            .Inc(candidate => candidate.NextEventSequence, 1)
                            .Set(candidate => candidate.UpdatedAtUtc, now)
                            .Set(
                                candidate => candidate.UpdatedByUserId,
                                actorUserId),
                        cancellationToken: transactionCt);
                if (stepUpdate.ModifiedCount != 1 ||
                    instanceUpdate.ModifiedCount != 1)
                {
                    throw SupplementalRevisionConflict(
                        current.Revision,
                        step.Revision);
                }
                if (step.AssignmentId is not null)
                {
                    await _ctx.WorkAssignments.UpdateOneAsync(
                        session,
                        assignment =>
                            assignment.Id == step.AssignmentId &&
                            !assignment.IsDeleted,
                        Builders<WorkAssignment>.Update
                            .Set(assignment => assignment.IsActive, false)
                            .Set(
                                assignment =>
                                    assignment.FlowEffectiveStatus,
                                DynamicFlowEffectiveStatuses.Invalidated)
                            .Set(
                                assignment =>
                                    assignment.InvalidatedByFlowEventId,
                                eventId)
                            .Set(assignment => assignment.UpdatedAtUtc, now)
                            .Set(
                                assignment => assignment.UpdatedByUserId,
                                actorUserId)
                            .Inc(
                                assignment =>
                                    assignment
                                        .DynamicFlowMaterializationRevision,
                                1),
                        cancellationToken: transactionCt);
                    await _ctx.WorkTemplateAssignees.UpdateManyAsync(
                        session,
                        binding =>
                            binding.WorkAssignmentId == step.AssignmentId &&
                            !binding.IsDeleted,
                        Builders<WorkTemplateAssignee>.Update
                            .Set(binding => binding.IsActive, false)
                            .Set(binding => binding.UpdatedAtUtc, now)
                            .Set(
                                binding => binding.UpdatedByUserId,
                                actorUserId),
                        cancellationToken: transactionCt);
                    await _ctx.WorkAssignmentQueueItems.UpdateManyAsync(
                        session,
                        item =>
                            item.WorkAssignmentId == step.AssignmentId &&
                            !item.IsDeleted,
                        Builders<WorkAssignmentQueueItem>.Update
                            .Set(item => item.IsActive, false)
                            .Set(item => item.UpdatedAtUtc, now)
                            .Set(item => item.UpdatedByUserId, actorUserId),
                        cancellationToken: transactionCt);
                }
                await _ctx.DynamicFlowRuntimeOutbox.UpdateManyAsync(
                    session,
                    item =>
                        item.StepInstanceId == step.Id &&
                        item.Status == DynamicFlowRuntimeOutboxStatuses.Pending,
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Set(
                            item => item.Status,
                            DynamicFlowRuntimeOutboxStatuses.DeadLetter)
                        .Set(
                            item => item.LastErrorCode,
                            "SUPPLEMENTAL_CANCELLED")
                        .Set(item => item.UpdatedAtUtc, now),
                    cancellationToken: transactionCt);

                var response = BuildSupplementalResult(
                    commandId,
                    current,
                    current.Revision + 1,
                    step,
                    eventId,
                    DynamicFlowSupplementalTopologyContract.CancelledEvent,
                    businessWritePerformed: true,
                    replayed: false);
                response.State = DynamicFlowStepStates.CancelledByGateway;
                var resultSnapshot = SupplementalResultDocument(response);
                await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                    session,
                    new DynamicFlowRuntimeEvent
                    {
                        Id = eventId,
                        FlowInstanceId = current.Id,
                        StepInstanceId = step.Id,
                        ExecutionEpoch = current.ExecutionEpoch,
                        BranchId = step.BranchId,
                        AttemptNo = step.AttemptNo,
                        Sequence = current.NextEventSequence,
                        EventType =
                            DynamicFlowSupplementalTopologyContract
                                .CancelledEvent,
                        CommandId = commandId,
                        ActorUserId = actorUserId,
                        VisibleUnitIds = new[]
                            {
                                step.TargetUnitId,
                                current.IssuerUnitId
                            }
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToList(),
                        Payload = new BsonDocument
                        {
                            { "supplementalStepId", step.Id },
                            { "completionRequired", step.CompletionRequired },
                            { "cancelledBy", actorUserId },
                            { "reason", BsonValue.Create(reason) }
                        },
                        PayloadHash = requestHash,
                        OccurredAtUtc = now
                    },
                    cancellationToken: transactionCt);
                await _ctx.DynamicFlowRuntimeCommandReceipts.InsertOneAsync(
                    session,
                    new DynamicFlowRuntimeCommandReceipt
                    {
                        Id = StableTopologyObjectId(
                            $"{DynamicFlowCommandScopeKinds.Instance}\n{current.Id}\n" +
                            $"{DynamicFlowSupplementalTopologyContract.CancelCommand}\n{commandId}"),
                        ScopeKind = DynamicFlowCommandScopeKinds.Instance,
                        ScopeId = current.Id,
                        WorkId = current.WorkId,
                        FlowTemplateVersionId =
                            current.FlowTemplateVersionId,
                        FlowInstanceId = current.Id,
                        CommandType =
                            DynamicFlowSupplementalTopologyContract
                                .CancelCommand,
                        CommandId = commandId,
                        RequestHash = requestHash,
                        CommandIdentityHash =
                            DynamicFlowSequentialTopologyContract.Hash(
                                $"{actorUserId}\n{current.Id}\n{commandId}\ncancel"),
                        SnapshotToken =
                            DynamicFlowSequentialTopologyContract.Hash(
                                $"{requestHash}\n{current.TopologySnapshotHash}"),
                        ExpectedRevision = req.ExpectedInstanceRevision,
                        Status =
                            DynamicFlowRuntimeCommandStatuses.Succeeded,
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
                return response;
            },
            ct);
    }

    public async Task<DynamicFlowSubflowLaunchResponse> LaunchSubflowAsync(
        string workId,
        string parentInstanceId,
        string parentStepInstanceId,
        DynamicFlowSubflowLaunchRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new DynamicFlowSubflowLaunchRequest();
        workId = NormalizeRequiredObjectId(workId, "workId");
        parentInstanceId = NormalizeRequiredObjectId(
            parentInstanceId,
            "parentInstanceId");
        parentStepInstanceId = NormalizeRequiredObjectId(
            parentStepInstanceId,
            "parentStepInstanceId");
        var commandId = NormalizeRequiredCommandId(req.CommandId);
        if (req.ExpectedParentInstanceRevision is null or < 1 ||
            req.ExpectedParentStepRevision is null or < 1)
        {
            throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_EXPECTED_REVISION_REQUIRED");
        }

        var actor = await _ctx.Users
            .Find(candidate =>
                candidate.Id == actorUserId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized();
        var parent = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == parentInstanceId &&
                candidate.WorkId == workId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        var parentStep = await _ctx.DynamicFlowStepInstances
            .Find(candidate =>
                candidate.Id == parentStepInstanceId &&
                candidate.FlowInstanceId == parent.Id &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        if (string.IsNullOrWhiteSpace(parentStep.AssignmentId))
        {
            throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_PARENT_ASSIGNMENT_MISSING");
        }
        var parentAssignment = await _ctx.WorkAssignments
            .Find(candidate =>
                candidate.Id == parentStep.AssignmentId &&
                candidate.WorkId == workId &&
                candidate.FlowInstanceId == parent.Id &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        await EnsureCanMutateFlowBranchAsync(
            parentAssignment,
            actorUserId,
            ct);

        if (parent.ArchetypeId !=
            DynamicFlowSubflowTopologyContract.ArchetypeId)
        {
            throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_PARENT_ARCHETYPE_REQUIRED");
        }
        if (!_runtimeActivation.CanExecuteP6CandidatePin(
                parent.CatalogVersion,
                parent.CatalogSemanticHash,
                parent.ArchetypeId))
        {
            throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_EXECUTION_DISABLED");
        }

        var topology = DynamicFlowSubflowTopologyContract.Require(
            parent.TopologySnapshotJson,
            parent.TopologySnapshotHash);
        if (parentStep.FlowStepId != topology.ParentNode.NodeId ||
            parentStep.FormNodeId != topology.ParentForm.FormNodeId ||
            parentAssignment.AllowSubFlow != true ||
            parentAssignment.FlowEffectiveStatus !=
                DynamicFlowEffectiveStatuses.Effective)
        {
            throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_PARENT_PIN_DRIFT");
        }

        var childVersion = await _ctx.DynamicFlowTemplateVersions
            .Find(candidate =>
                candidate.Id == topology.ChildFlowVersionId &&
                candidate.TemplateId == topology.ChildFlowFamilyId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        var childFamily = await _ctx.DynamicFlowTemplates
            .Find(candidate =>
                candidate.Id == topology.ChildFlowFamilyId &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        var hasChildExecuteGrant =
            string.Equals(
                parent.IssuerUserId,
                actorUserId,
                StringComparison.Ordinal) ||
            DynamicFlowTemplateReadAccess.IsOwner(
                childFamily,
                actorUserId) ||
            DynamicFlowTemplateReadAccess.CanCreateDefinition(actor) ||
            parentStep.ParticipantUserIds.Contains(
                actorUserId,
                StringComparer.Ordinal);
        if (!hasChildExecuteGrant ||
            childFamily.Status != DynamicFlowTemplateStatuses.Active ||
            childVersion.Status !=
                DynamicFlowTemplateVersionStatuses.Locked)
        {
            throw RuntimeAccessForbidden();
        }
        await ValidateLockedSnapshotIntegrityAsync(
            childFamily,
            childVersion,
            ct);

        var participantSnapshot =
            await _ctx.DynamicFlowParticipantSnapshots
                .Find(candidate =>
                    candidate.Id == parent.ParticipantSnapshotId &&
                    candidate.FlowInstanceId == parent.Id)
                .FirstOrDefaultAsync(ct)
            ?? throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_PARTICIPANT_SNAPSHOT_MISSING");
        var frozenBinding = participantSnapshot.Bindings
            .SingleOrDefault(binding =>
                binding.TargetUnitId == parentStep.TargetUnitId)
            ?? throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_PARTICIPANT_BINDING_MISSING");
        var frozenIds = frozenBinding.AssigneeUserIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (frozenIds.Length == 0 ||
            !frozenIds.SequenceEqual(
                parentStep.ParticipantUserIds
                    .OrderBy(value => value, StringComparer.Ordinal)))
        {
            throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_PARTICIPANT_PIN_DRIFT");
        }

        var preflightRequest = new DynamicFlowPreflightRequest
        {
            FlowTemplateVersionId = childVersion.Id,
            CommandId = commandId,
            TargetUnitIds = [parentStep.TargetUnitId],
            PeriodKey = parent.PeriodKey,
            ScheduleIdentityJson = parent.ScheduleIdentityJson
        };
        var participantsByUnit = new Dictionary<
            string,
            IReadOnlyList<DynamicFlowParticipantUserSnapshotDto>>(
            StringComparer.Ordinal)
        {
            [parentStep.TargetUnitId] = frozenBinding.Participants
                .OrderBy(participant => participant.UserId, StringComparer.Ordinal)
                .Select(participant =>
                    new DynamicFlowParticipantUserSnapshotDto
                    {
                        UserId = participant.UserId,
                        Username = participant.Username,
                        FullName = participant.FullName,
                        UnitId = participant.UnitId,
                        UnitSymbol = participant.UnitSymbol,
                        UnitShortName = participant.UnitShortName,
                        UnitName = participant.UnitName,
                        PositionCode = participant.PositionCode,
                        PositionName = participant.PositionName
                    })
                .ToList()
        };
        var preflight = DynamicFlowRuntimePreflightContract.Build(
            workId,
            parent.WorkType,
            childFamily,
            childVersion,
            preflightRequest,
            actorUserId,
            parent.IssuerUnitId,
            participantsByUnit,
            _runtimeActivation.IsP6CandidateArchetypeEnabled);
        if (!CanExecuteRuntimePin(preflight.FlowPin) ||
            preflight.Eligibility !=
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate)
        {
            throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_CHILD_NOT_EXECUTABLE");
        }

        var replay = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(receipt =>
                receipt.ScopeKind ==
                    DynamicFlowCommandScopeKinds.Launch &&
                receipt.ScopeId ==
                    $"{workId}:{childVersion.Id}" &&
                receipt.CommandType == "LAUNCH" &&
                receipt.CommandId == commandId)
            .FirstOrDefaultAsync(ct);
        var context =
            DynamicFlowSubflowTopologyContract.BuildLaunchContext(
                parent,
                parentStep,
                topology,
                childFamily.Id,
                childVersion.Id) with
            {
                ExpectedParentInstanceRevision =
                    req.ExpectedParentInstanceRevision.Value,
                ExpectedParentStepRevision =
                    req.ExpectedParentStepRevision.Value
            };
        preflight.RequestHash =
            DynamicFlowSequentialTopologyContract.Hash(string.Join(
                "\n",
                preflight.RequestHash,
                parent.Id,
                parentStep.Id,
                req.ExpectedParentInstanceRevision.Value,
                req.ExpectedParentStepRevision.Value,
                context.RootInstanceId,
                string.Join(",", context.AncestryPath),
                string.Join(",", context.AncestryFlowFamilyIds)));
        preflight.SnapshotToken =
            DynamicFlowSequentialTopologyContract.Hash(
                $"{preflight.RequestHash}\n{childVersion.PayloadHash}\n{preflight.ScheduleIdentityHash}");
        var confirmRequest = new DynamicFlowConfirmRequest
        {
            FlowTemplateVersionId =
                preflightRequest.FlowTemplateVersionId,
            CommandId = preflightRequest.CommandId,
            TargetUnitIds = preflightRequest.TargetUnitIds,
            PeriodKey = preflightRequest.PeriodKey,
            ScheduleIdentityJson =
                preflightRequest.ScheduleIdentityJson,
            SnapshotToken = preflight.SnapshotToken
        };

        if (replay is null)
        {
            if (parent.State != DynamicFlowInstanceStates.Active ||
                parentStep.State != DynamicFlowStepStates.Approved ||
                parentStep.ChildInstanceId is not null)
            {
                throw SubflowValidation(
                    "DYNAMIC_FLOW_SUBFLOW_PARENT_NOT_READY");
            }
            if (parent.Revision !=
                    req.ExpectedParentInstanceRevision.Value ||
                parentStep.Revision !=
                    req.ExpectedParentStepRevision.Value)
            {
                throw SubflowRevisionConflict(
                    parent.Revision,
                    parentStep.Revision);
            }
        }

        DynamicFlowConfirmResponse materialized;
        try
        {
            materialized =
                await _runtimeMaterializer
                    .ConfirmAndMaterializeSubflowAsync(
                        preflight,
                        confirmRequest,
                        actorUserId,
                        context,
                        ct);
        }
        catch (InvalidOperationException error) when (
            error.Message ==
            "DYNAMIC_FLOW_SUBFLOW_PARENT_REVISION_CONFLICT")
        {
            var currentParentRevision =
                await _ctx.DynamicFlowInstances
                    .Find(candidate =>
                        candidate.Id == parent.Id &&
                        !candidate.IsDeleted)
                    .Project(candidate => (long?)candidate.Revision)
                    .FirstOrDefaultAsync(ct) ?? parent.Revision;
            var currentStepRevision =
                await _ctx.DynamicFlowStepInstances
                    .Find(candidate =>
                        candidate.Id == parentStep.Id &&
                        !candidate.IsDeleted)
                    .Project(candidate => (long?)candidate.Revision)
                    .FirstOrDefaultAsync(ct) ?? parentStep.Revision;
            throw SubflowRevisionConflict(
                currentParentRevision,
                currentStepRevision);
        }

        var currentParent = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == parent.Id &&
                !candidate.IsDeleted)
            .SingleAsync(ct);
        var currentParentStep =
            await _ctx.DynamicFlowStepInstances
                .Find(candidate =>
                    candidate.Id == parentStep.Id &&
                    !candidate.IsDeleted)
                .SingleAsync(ct);
        var childInstanceId =
            materialized.FlowInstanceId ??
            currentParentStep.ChildInstanceId ??
            throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_CHILD_INSTANCE_MISSING");
        var currentChild = await _ctx.DynamicFlowInstances
            .Find(candidate =>
                candidate.Id == childInstanceId &&
                candidate.ParentInstanceId == parent.Id &&
                candidate.ParentStepInstanceId == parentStep.Id &&
                !candidate.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw SubflowValidation(
                "DYNAMIC_FLOW_SUBFLOW_CHILD_LINEAGE_DRIFT");
        return new DynamicFlowSubflowLaunchResponse
        {
            CommandId = commandId,
            Status = materialized.Status,
            BusinessWritePerformed = true,
            Replayed = replay is not null,
            ParentInstanceId = parent.Id,
            ParentStepInstanceId = parentStep.Id,
            ChildInstanceId = currentChild.Id,
            ChildFlowTemplateId = currentChild.FlowTemplateId,
            ChildFlowVersionId =
                currentChild.FlowTemplateVersionId,
            AncestryDepth = currentChild.AncestryPath.Count,
            ParentState = currentParentStep.State,
            ChildState = currentChild.State,
            EventId =
                DynamicFlowSubflowTopologyContract.BuildParentEventId(
                    parent.Id,
                    commandId,
                    DynamicFlowSubflowTopologyContract
                        .ChildLaunchedEvent)
        };
    }

    private static bool IsCanonicalRuntimeConfirmConflict(InvalidOperationException error)
        => error.Message is
            "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT" or
            "DYNAMIC_FLOW_PREFLIGHT_STALE";

    internal static AppException MapCanonicalRuntimeConfirmConflict(
        InvalidOperationException error)
        => error.Message switch
        {
            "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT" =>
                AppExceptionFactory.Create(
                    AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
                    new { reason = "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT" }),
            "DYNAMIC_FLOW_PREFLIGHT_STALE" =>
                AppExceptionFactory.Create(
                    AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
                    new { reason = "DYNAMIC_FLOW_PREFLIGHT_STALE" }),
            _ => throw new ArgumentOutOfRangeException(
                nameof(error),
                error.Message,
                "Unsupported Dynamic Flow runtime confirm conflict.")
        };

    public async Task<DynamicFlowInstanceLaunchResponse> CreateInstanceAsync(
        string workId,
        CreateDynamicFlowInstanceRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new CreateDynamicFlowInstanceRequest();

        var actor = await _ctx.Users
            .Find(x => x.Id == actorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.Unauthorized();
        var versionId = NormalizeRequiredObjectId(req.FlowTemplateVersionId, "flowTemplateVersionId");
        var versionPin = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.Id == versionId && !x.IsDeleted)
            .Project(x => new { x.TemplateId, x.VersionNo })
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        // P4 owns definition snapshots only.  Catalog v1.1 deliberately has
        // no executable Flow archetype, so authorize the server-derived
        // business grant and stop before loading a parent/work or invoking any
        // assignment/event/projection writer.  The legacy planner below stays
        // intact for P5; it is not a P4 execution claim.
        var hasExecuteGrant = await _ctx.WorkAssignments
            .Find(DynamicFlowTemplateReadAccess.BuildAssignmentParticipantFilter(
                actorUserId,
                versionPin.TemplateId,
                versionPin.VersionNo))
            .Project(x => x.Id)
            .AnyAsync(ct);
        if (!hasExecuteGrant)
            throw RuntimeAccessForbidden();

        // Only an active exact-version participant may cause family or payload
        // details to be materialized.
        var template = await _ctx.DynamicFlowTemplates
            .Find(x => x.Id == versionPin.TemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(x =>
                x.Id == versionId &&
                x.TemplateId == template.Id &&
                x.VersionNo == versionPin.VersionNo &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();

        // Existence- and lifecycle-sensitive validation comes only after the
        // exact active/effective participant pin has authorized this version.
        // This keeps an outsider from probing whether a family is active,
        // archived, or otherwise present.
        if (!string.Equals(template.Status, DynamicFlowTemplateStatuses.Active, StringComparison.Ordinal) ||
            !string.Equals(version.Status, DynamicFlowTemplateVersionStatuses.Locked, StringComparison.Ordinal))
        {
            throw RuntimeAccessForbidden();
        }

        await ValidateLockedSnapshotIntegrityAsync(template, version, ct);
        throw ExecutionBlocked(version);

#pragma warning disable CS0162 // P5 implementation is intentionally unreachable while catalog v1.1 is blocked.
        WorkAssignment? parent = null;
        if (!string.IsNullOrWhiteSpace(req.ParentAssignmentId))
        {
            var parentAssignmentId = NormalizeRequiredObjectId(req.ParentAssignmentId, "parentAssignmentId");
            parent = await _ctx.WorkAssignments
                .Find(x => x.Id == parentAssignmentId && x.WorkId == workId && x.IsActive && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw AppExceptionFactory.NotFound(
                    AppErrorCode.WORK_ASSIGNMENT_PARENT_NOT_FOUND,
                    new { workId, parentAssignmentId });
        }

        if (!CanLaunchTemplate(template, version, parent, actor))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.AUTH_FORBIDDEN,
                new
                {
                    flowTemplateId = template.Id,
                    flowTemplateVersionId = version.Id,
                    parentAssignmentId = parent?.Id,
                    actorUserId,
                    reason = "DYNAMIC_FLOW_LAUNCH_FORBIDDEN"
                });
        }

        var plan = DynamicFlowRuntimePlanner.CreateLaunchPlan(
            template,
            version,
            req,
            parent,
            actor.UnitId);

        var response = new DynamicFlowInstanceLaunchResponse
        {
            WorkId = workId,
            FlowInstanceId = plan.FlowInstanceId,
            FlowTemplateId = plan.FlowTemplateId,
            FlowTemplateVersionNo = plan.FlowTemplateVersionNo,
            DynamicFormTemplateId = plan.DynamicFormTemplateId,
            Step = new DynamicFlowRuntimeStepDto
            {
                StepId = plan.Step.StepId,
                StepCode = plan.Step.StepCode,
                StepOrder = plan.Step.StepOrder
            }
        };

        foreach (var branch in plan.Branches)
        {
            var createReq = ToAssignmentRequest(req, plan.DynamicFormTemplateId, branch.TargetUnitId);
            var created = await _assignments.CreateAsync(workId, createReq, actorUserId, ct);
            var assignment = await ApplyFlowMetadataAsync(created.Id, plan, branch, actorUserId, ct);
            if (parent is null && response.Branches.Count == 0)
            {
                await WriteFlowEventAsync(
                    ObjectId.GenerateNewId().ToString(),
                    assignment,
                    DynamicFlowEventActions.FlowCreated,
                    fromStatus: null,
                    toStatus: assignment.FlowEffectiveStatus,
                    actorUserId,
                    actor.UnitId,
                    reason: "FLOW_INSTANCE_CREATED",
                    snapshotJson: null,
                    affectedAssignmentIds: new List<string> { assignment.Id! },
                    actionAtUtc: DateTime.UtcNow,
                    ct);
            }

            await WriteFlowEventAsync(
                ObjectId.GenerateNewId().ToString(),
                assignment,
                DynamicFlowEventActions.StepAssigned,
                fromStatus: null,
                toStatus: assignment.FlowEffectiveStatus,
                actorUserId,
                actor.UnitId,
                reason: null,
                snapshotJson: null,
                affectedAssignmentIds: new List<string> { assignment.Id! },
                actionAtUtc: DateTime.UtcNow,
                ct);

            await _docRoleReadModelProjection.RebuildAssignmentAsync(assignment.Id!, actorUserId, ct);

            var detail = await _assignments.GetByIdAsync(assignment.Id!, actorUserId, ct)
                ?? throw AppExceptionFactory.NotFound(
                    AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                    new { assignmentId = assignment.Id });

            response.Branches.Add(new DynamicFlowAssignmentBranchResponse
            {
                AssignmentId = assignment.Id!,
                TargetUnitId = branch.TargetUnitId,
                FlowBranchId = branch.FlowBranchId,
                ParentFlowBranchId = branch.ParentFlowBranchId,
                AllowSubFlow = branch.AllowSubFlow,
                IsFlowFinalNode = branch.IsFlowFinalNode,
                Assignment = detail
            });
        }

        return response;
#pragma warning restore CS0162
    }

    private async Task ValidateLockedSnapshotIntegrityAsync(
        DynamicFlowTemplate family,
        DynamicFlowTemplateVersion version,
        CancellationToken ct)
    {
        var formIds = DynamicFlowLockedSnapshotIntegrity
            .CollectReferencedFormIds(new[] { version })
            .ToList();
        var forms = formIds.Count == 0
            ? new List<DynamicFormTemplate>()
            : await _ctx.DynamicFormTemplates
                .Find(form =>
                    formIds.Contains(form.Id) &&
                    !form.IsDeleted &&
                    form.IsActive &&
                    form.IsPublished)
                .ToListAsync(ct);
        DynamicFlowLockedSnapshotIntegrity.Validate(
            family,
            new[] { version },
            forms.ToDictionary(form => form.Id, StringComparer.Ordinal));
    }

    public Task<DynamicFlowBranchActionResponse> RollbackBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => MutateBranchAsync(
            workId,
            assignmentId,
            req,
            actorUserId,
            DynamicFlowEventActions.Rollback,
            ct);

    public Task<DynamicFlowBranchActionResponse> TerminateBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => MutateBranchAsync(
            workId,
            assignmentId,
            req,
            actorUserId,
            DynamicFlowEventActions.Terminated,
            ct);

    public Task<DynamicFlowBranchActionResponse> RestartBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest req,
        string actorUserId,
        CancellationToken ct = default)
        => MutateBranchAsync(
            workId,
            assignmentId,
            req,
            actorUserId,
            DynamicFlowEventActions.Restarted,
            ct);

    public async Task<DynamicFlowBranchActionResponse> ForwardBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new DynamicFlowBranchActionRequest();
        workId = NormalizeRequiredObjectId(workId, "workId");
        assignmentId = NormalizeRequiredObjectId(assignmentId, "assignmentId");

        var assignment = await _ctx.WorkAssignments
            .Find(x =>
                x.Id == assignmentId &&
                x.WorkId == workId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw RuntimeAccessForbidden();
        await EnsureCanMutateFlowBranchAsync(assignment, actorUserId, ct);
        if (string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
            throw ForwardValidation("DYNAMIC_FLOW_ASSIGNMENT_REQUIRED");

        var parentSteps = await _ctx.DynamicFlowStepInstances
            .Find(x =>
                x.FlowInstanceId == assignment.FlowInstanceId &&
                x.AssignmentId == assignment.Id &&
                !x.IsDeleted)
            .ToListAsync(ct);
        var instance = await _ctx.DynamicFlowInstances
            .Find(x =>
                x.Id == assignment.FlowInstanceId &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (parentSteps.Count != 1 || instance is null)
            throw ForwardValidation("DYNAMIC_FLOW_FORWARD_PRECONDITION_MISSING");
        var parentStep = parentSteps[0];
        if (DynamicFlowRuntimeEligibilityPolicy.IsCurrentCatalog(
                instance.CatalogVersion,
                instance.CatalogSemanticHash) &&
            instance.ArchetypeId is "FLOW-T01" or "FLOW-T02")
        {
            if (instance.State != DynamicFlowInstanceStates.Active ||
                assignment.FlowEffectiveStatus !=
                    DynamicFlowEffectiveStatuses.Effective ||
                parentStep.State is not (
                    DynamicFlowStepStates.Approved or
                    DynamicFlowStepStates.Completed))
            {
                throw P5ForwardGuard(
                    "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED");
            }

            throw P6CommandBlocked("FORWARD");
        }

        var commandId = NormalizeRequiredCommandId(req.CommandId);
        if (req.ExpectedInstanceRevision is null or < 0 ||
            req.ExpectedStepRevision is null or < 0)
        {
            throw ForwardValidation(
                "DYNAMIC_FLOW_FORWARD_EXPECTED_REVISION_REQUIRED");
        }
        if (string.Equals(
                instance.ArchetypeId,
                DynamicFlowParallelForkTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            return await ForwardParallelForkAsync(
                workId,
                assignment,
                parentStep,
                instance,
                req,
                commandId,
                actorUserId,
                ct);
        }
        if (string.Equals(
                instance.ArchetypeId,
                DynamicFlowJoinAllTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            return await ForwardParallelForkAsync(
                workId,
                assignment,
                parentStep,
                instance,
                req,
                commandId,
                actorUserId,
                ct);
        }
        if (string.Equals(
                instance.ArchetypeId,
                DynamicFlowJoinQuorumTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            return await ForwardParallelForkAsync(
                workId,
                assignment,
                parentStep,
                instance,
                req,
                commandId,
                actorUserId,
                ct);
        }
        if (string.Equals(
                instance.ArchetypeId,
                DynamicFlowTypedConditionalTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            return await ForwardParallelForkAsync(
                workId,
                assignment,
                parentStep,
                instance,
                req,
                commandId,
                actorUserId,
                ct);
        }
        if (!string.Equals(
                instance.ArchetypeId,
                DynamicFlowSequentialTopologyContract.ArchetypeId,
                StringComparison.Ordinal))
        {
            throw ForwardBlocked(instance.ArchetypeId);
        }
        if (!_runtimeActivation.CanExecuteP6CandidatePin(
                instance.CatalogVersion,
                instance.CatalogSemanticHash,
                instance.ArchetypeId))
        {
            throw ForwardBlocked(instance.ArchetypeId);
        }

        var topology = DynamicFlowSequentialTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var expectedDefinitionRevision =
            DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                instance.FlowTemplateVersionId,
                instance.FlowTemplateVersionNo,
                instance.FlowPayloadHash);
        if (!string.Equals(
                instance.DefinitionRevision,
                expectedDefinitionRevision,
                StringComparison.Ordinal) ||
            !string.Equals(
                instance.TopologySnapshotHash,
                instance.FlowPayloadHash,
                StringComparison.Ordinal))
        {
            throw ForwardValidation("DYNAMIC_FLOW_TOPOLOGY_PIN_STALE");
        }
        RequireSequentialRuntimePins(
            instance,
            parentStep,
            assignment,
            topology);
        if (!topology.OutgoingByNodeId.TryGetValue(parentStep.FlowStepId, out var edge))
            throw ForwardValidation("DYNAMIC_FLOW_FORWARD_TERMINAL_STEP");
        if (parentStep.NextNodeIds.Count != 1 ||
            !string.Equals(parentStep.NextNodeIds[0], edge.ToNodeId, StringComparison.Ordinal))
        {
            throw ForwardValidation("DYNAMIC_FLOW_FORWARD_EDGE_PIN_INVALID");
        }

        var nextNode = topology.OrderedNodes.Single(node =>
            string.Equals(node.NodeId, edge.ToNodeId, StringComparison.Ordinal));
        var nextForm = topology.FormsByNodeId[nextNode.NodeId];
        var nextOrder = topology.OrderedNodes
                            .Select((node, index) => new { node.NodeId, Order = index + 1 })
                            .Single(item => item.NodeId == nextNode.NodeId)
                            .Order;
        var nextStepId = DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
            instance.Id,
            instance.ExecutionEpoch,
            nextNode.NodeId,
            parentStep.BranchId,
            1);
        var nextAssignmentId =
            DynamicFlowSequentialTopologyContract.BuildAssignmentId(nextStepId);
        var nextNodeIds = topology.OutgoingByNodeId.TryGetValue(nextNode.NodeId, out var nextEdge)
            ? new List<string> { nextEdge.ToNodeId }
            : new List<string>();
        var isTerminalNode = nextNodeIds.Count == 0;
        var requestHash = DynamicFlowSequentialTopologyContract.Hash(
            JsonSerializer.Serialize(new
            {
                workId,
                assignmentId,
                commandId,
                actorUserId,
                instanceId = instance.Id,
                executionEpoch = instance.ExecutionEpoch,
                expectedInstanceRevision = req.ExpectedInstanceRevision.Value,
                expectedStepRevision = req.ExpectedStepRevision.Value,
                fromStepInstanceId = parentStep.Id,
                fromNodeId = parentStep.FlowStepId,
                transitionId = edge.TransitionId,
                toNodeId = nextNode.NodeId,
                topologyHash = instance.TopologySnapshotHash,
                definitionRevision = instance.DefinitionRevision
            }));
        var receiptId = DynamicFlowSequentialTopologyContract.BuildForwardReceiptId(
            instance.Id,
            commandId);
        var existing = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x => x.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return ParseForwardReplay(existing, requestHash);
        if (instance.State != DynamicFlowInstanceStates.Active ||
            assignment.FlowEffectiveStatus != DynamicFlowEffectiveStatuses.Effective ||
            parentStep.State is not (DynamicFlowStepStates.Approved or DynamicFlowStepStates.Completed))
        {
            throw ForwardValidation("DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED");
        }
        if (instance.Revision != req.ExpectedInstanceRevision ||
            parentStep.Revision != req.ExpectedStepRevision)
        {
            throw ForwardRevisionConflict(
                instance.Revision,
                parentStep.Revision);
        }
        var nextStepAlreadyExists = await _ctx.DynamicFlowStepInstances
            .Find(x => x.Id == nextStepId && !x.IsDeleted)
            .Project(x => x.Id)
            .AnyAsync(ct);
        if (nextStepAlreadyExists)
            throw ForwardValidation("DYNAMIC_FLOW_FORWARD_EDGE_ALREADY_ACTIVATED");

        var participantSnapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(x => x.Id == instance.ParticipantSnapshotId)
            .FirstOrDefaultAsync(ct)
            ?? throw ForwardValidation("DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_MISSING");
        var participantBinding = participantSnapshot.Bindings.SingleOrDefault(binding =>
                                     string.Equals(
                                         binding.TargetUnitId,
                                         parentStep.TargetUnitId,
                                         StringComparison.Ordinal))
                                 ?? throw ForwardValidation(
                                     "DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_BINDING_MISSING");
        var participantIds = participantBinding.AssigneeUserIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (participantIds.Count == 0 ||
            !participantIds.SequenceEqual(
                parentStep.ParticipantUserIds.OrderBy(value => value, StringComparer.Ordinal)))
        {
            throw ForwardValidation("DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_DRIFT");
        }
        if (!string.Equals(
                participantSnapshot.SnapshotHash,
                instance.ParticipantSnapshotHash,
                StringComparison.Ordinal) ||
            !participantIds.Contains(actorUserId, StringComparer.Ordinal) &&
            !string.Equals(instance.IssuerUserId, actorUserId, StringComparison.Ordinal))
        {
            throw RuntimeAccessForbidden();
        }
        RequireSequentialParticipantPins(
            instance,
            parentStep,
            participantSnapshot,
            actorUserId);

        var now = DateTime.UtcNow;
        var acceptedEventSequence = instance.NextEventSequence;
        var materializedEventSequence = acceptedEventSequence + 1;
        var acceptedEventId = StableTopologyObjectId(
            $"{instance.Id}\n{instance.ExecutionEpoch}\nforward\n{commandId}\naccepted");
        var resultSnapshot = new BsonDocument
        {
            { "commandId", commandId },
            { "status", "ACCEPTED_PENDING_MATERIALIZATION" },
            { "businessWritePerformed", true },
            { "flowInstanceId", instance.Id },
            { "executionEpoch", instance.ExecutionEpoch },
            { "instanceRevision", instance.Revision + 1 },
            { "stepInstanceId", parentStep.Id },
            { "nextStepInstanceId", nextStepId },
            { "nextAssignmentId", nextAssignmentId },
            { "activatedTransitionId", edge.TransitionId },
            { "eventId", acceptedEventId }
        };
        var commandIdentityHash = DynamicFlowSequentialTopologyContract.Hash(
            $"{actorUserId}\n{instance.Id}\n{commandId}\nFORWARD");
        DynamicFlowBranchActionResponse? transactionReplay = null;

        try
        {
            await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var replay = await _ctx.DynamicFlowRuntimeCommandReceipts
                        .Find(session, x => x.Id == receiptId)
                        .FirstOrDefaultAsync(transactionCt);
                    if (replay is not null)
                    {
                        transactionReplay = ParseForwardReplay(
                            replay,
                            requestHash);
                        return;
                    }

                    var currentInstance = await _ctx.DynamicFlowInstances
                        .Find(
                            session,
                            x =>
                                x.Id == instance.Id &&
                                x.State == DynamicFlowInstanceStates.Active &&
                                x.Revision == req.ExpectedInstanceRevision.Value &&
                                x.ExecutionEpoch == instance.ExecutionEpoch &&
                                x.TopologySnapshotHash == instance.TopologySnapshotHash &&
                                !x.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    var currentStep = await _ctx.DynamicFlowStepInstances
                        .Find(
                            session,
                            x =>
                                x.Id == parentStep.Id &&
                                x.FlowInstanceId == instance.Id &&
                                x.Revision == req.ExpectedStepRevision.Value &&
                                x.ExecutionEpoch == instance.ExecutionEpoch &&
                                !x.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    var currentAssignment = await _ctx.WorkAssignments
                        .Find(
                            session,
                            x =>
                                x.Id == assignment.Id &&
                                x.WorkId == workId &&
                                x.FlowInstanceId == instance.Id &&
                                x.FlowEffectiveStatus ==
                                DynamicFlowEffectiveStatuses.Effective &&
                                !x.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    var currentSnapshot = await _ctx.DynamicFlowParticipantSnapshots
                        .Find(
                            session,
                            x =>
                                x.Id == instance.ParticipantSnapshotId &&
                                x.FlowInstanceId == instance.Id)
                        .FirstOrDefaultAsync(transactionCt);
                    if (currentInstance is null ||
                        currentStep is null ||
                        currentAssignment is null ||
                        currentSnapshot is null)
                    {
                        throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    }
                    if (currentStep.State is not (
                            DynamicFlowStepStates.Approved or
                            DynamicFlowStepStates.Completed))
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED");
                    }
                    RequireSequentialRuntimePins(
                        currentInstance,
                        currentStep,
                        currentAssignment,
                        topology);
                    RequireSequentialParticipantPins(
                        currentInstance,
                        currentStep,
                        currentSnapshot,
                        actorUserId);
                    var currentNextStepExists = await _ctx.DynamicFlowStepInstances
                        .Find(
                            session,
                            x => x.Id == nextStepId && !x.IsDeleted)
                        .Project(x => x.Id)
                        .AnyAsync(transactionCt);
                    if (currentNextStepExists)
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_REVISION_CONFLICT");
                    }

                    var nextStep = new DynamicFlowStepInstance
                    {
                        Id = nextStepId,
                        FlowInstanceId = instance.Id,
                        FlowStepId = nextNode.NodeId,
                        FlowStepCode = nextNode.NodeCode,
                        ExecutionEpoch = instance.ExecutionEpoch,
                        DefinitionRevision = instance.DefinitionRevision,
                        StepOrder = nextOrder,
                        FormNodeId = nextForm.FormNodeId,
                        FormFamilyId = nextForm.DynamicFormFamilyId!,
                        FormVersionId = nextForm.DynamicFormTemplateId,
                        FormVersionNo = nextForm.DynamicFormVersionNo!.Value,
                        FormSchemaHash = nextForm.DynamicFormSchemaHash!,
                        FormSnapshotHash = nextForm.DynamicFormSnapshotHash!,
                        TargetUnitId = parentStep.TargetUnitId,
                        ParticipantUserIds = participantIds,
                        ParticipantSnapshotId = participantSnapshot.Id,
                        AttemptNo = 1,
                        BranchId = parentStep.BranchId,
                        ActivatedByTransitionId = edge.TransitionId,
                        NextNodeIds = nextNodeIds,
                        IsTerminalNode = isTerminalNode,
                        ResultOwnerIdentity =
                            DynamicFlowSequentialTopologyContract
                                .BuildStepOwnerIdentity(
                            instance.Id,
                            instance.ExecutionEpoch,
                            topology.Payload.ResultOwnerStepId ?? nextNode.NodeId,
                            parentStep.BranchId,
                            "result"),
                        StatisticOwnerIdentity =
                            DynamicFlowSequentialTopologyContract
                                .BuildStepOwnerIdentity(
                            instance.Id,
                            instance.ExecutionEpoch,
                            topology.Payload.StatisticsOwnerStepId ?? nextNode.NodeId,
                            parentStep.BranchId,
                            "statistics"),
                        AssignmentId = nextAssignmentId,
                        State = DynamicFlowStepStates.Materializing,
                        Revision = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        CreatedByUserId = actorUserId,
                        UpdatedByUserId = actorUserId,
                        IsDeleted = false
                    };
                    var participants = new BsonArray(participantBinding.Participants
                        .OrderBy(user => user.UserId, StringComparer.Ordinal)
                        .Select(TopologyParticipantDocument));
                    var outboxPayload = new BsonDocument
                    {
                        { "commandId", commandId },
                        { "actorUserId", actorUserId },
                        { "issuerUnitId", instance.IssuerUnitId },
                        { "archetypeId", instance.ArchetypeId },
                        { "branchOrdinal", nextOrder },
                        { "targetUnitId", parentStep.TargetUnitId },
                        { "assignmentId", nextAssignmentId },
                        { "periodKey", instance.PeriodKey },
                        { "scheduleIdentityJson", instance.ScheduleIdentityJson },
                        { "scheduleIdentityHash", instance.ScheduleIdentityHash },
                        { "executionEpoch", instance.ExecutionEpoch },
                        { "branchId", parentStep.BranchId },
                        { "attemptNo", 1 },
                        { "transitionId", edge.TransitionId },
                        { "materializationEventSequence", materializedEventSequence },
                        { "participants", participants }
                    };
                    var outbox = new DynamicFlowRuntimeOutboxItem
                    {
                        Id = StableTopologyObjectId($"{nextStepId}\noutbox\nmaterialize"),
                        FlowInstanceId = instance.Id,
                        StepInstanceId = nextStepId,
                        Operation =
                            DynamicFlowRuntimeMaterializationOperations
                                .MaterializeSequentialAssignment,
                        DedupeKey =
                            $"{instance.Id}:{instance.ExecutionEpoch}:{nextNode.NodeId}:{parentStep.BranchId}:1:materialize",
                        Payload = outboxPayload,
                        PayloadHash = DynamicFlowSequentialTopologyContract.Hash(
                            outboxPayload.ToJson()),
                        Status = DynamicFlowRuntimeOutboxStatuses.Pending,
                        NextAttemptAtUtc = now,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        UpdatedByUserId = actorUserId
                    };
                    var eventPayload = new BsonDocument
                    {
                        { "fromStepInstanceId", parentStep.Id },
                        { "fromNodeId", parentStep.FlowStepId },
                        { "toStepInstanceId", nextStepId },
                        { "toNodeId", nextNode.NodeId },
                        { "transitionId", edge.TransitionId },
                        { "branchId", parentStep.BranchId },
                        { "executionEpoch", instance.ExecutionEpoch },
                        { "definitionRevision", instance.DefinitionRevision },
                        { "topologyHash", instance.TopologySnapshotHash }
                    };
                    var acceptedEvent = new DynamicFlowRuntimeEvent
                    {
                        Id = acceptedEventId,
                        FlowInstanceId = instance.Id,
                        StepInstanceId = parentStep.Id,
                        ExecutionEpoch = instance.ExecutionEpoch,
                        BranchId = parentStep.BranchId,
                        AttemptNo = parentStep.AttemptNo,
                        Sequence = acceptedEventSequence,
                        EventType = DynamicFlowRuntimeEventTypes.SequentialForwardAccepted,
                        CommandId = commandId,
                        FromState = parentStep.State,
                        ToState = DynamicFlowStepStates.Completed,
                        FromRevision = parentStep.Revision,
                        ToRevision = parentStep.State == DynamicFlowStepStates.Approved
                            ? parentStep.Revision + 1
                            : parentStep.Revision,
                        ReasonCode = "SEQUENTIAL_EDGE_ACTIVATED",
                        AffectedRefs =
                        [
                            $"assignment:{assignment.Id}",
                            $"step:{parentStep.Id}",
                            $"step:{nextStepId}",
                            $"assignment:{nextAssignmentId}"
                        ],
                        ActorUserId = actorUserId,
                        VisibleUnitIds = new[] { parentStep.TargetUnitId, instance.IssuerUnitId }
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToList(),
                        Payload = eventPayload,
                        PayloadHash = DynamicFlowSequentialTopologyContract.Hash(
                            eventPayload.ToJson()),
                        OccurredAtUtc = now
                    };
                    var receipt = new DynamicFlowRuntimeCommandReceipt
                    {
                        Id = receiptId,
                        ScopeKind = DynamicFlowCommandScopeKinds.Instance,
                        ScopeId = instance.Id,
                        WorkId = workId,
                        FlowTemplateVersionId = instance.FlowTemplateVersionId,
                        FlowInstanceId = instance.Id,
                        CommandType = "FORWARD",
                        CommandId = commandId,
                        RequestHash = requestHash,
                        CommandIdentityHash = commandIdentityHash,
                        SnapshotToken = DynamicFlowSequentialTopologyContract.Hash(
                            $"{requestHash}\n{instance.TopologySnapshotHash}"),
                        ExpectedRevision = req.ExpectedInstanceRevision,
                        Status = DynamicFlowRuntimeCommandStatuses.Succeeded,
                        ResultSnapshot = resultSnapshot,
                        ResultSnapshotHash = DynamicFlowSequentialTopologyContract.Hash(
                            resultSnapshot.ToJson()),
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        UpdatedByUserId = actorUserId,
                        CompletedAtUtc = now
                    };

                    var assignmentFence = await _ctx.WorkAssignments.UpdateOneAsync(
                        session,
                        x =>
                            x.Id == currentAssignment.Id &&
                            x.DynamicFlowMaterializationRevision ==
                            currentAssignment.DynamicFlowMaterializationRevision &&
                            x.FlowEffectiveStatus ==
                            DynamicFlowEffectiveStatuses.Effective &&
                            !x.IsDeleted,
                        Builders<WorkAssignment>.Update
                            .Inc(x => x.DynamicFlowMaterializationRevision, 1)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId),
                        cancellationToken: transactionCt);
                    if (assignmentFence.ModifiedCount != 1)
                        throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    await _ctx.DynamicFlowRuntimeCommandReceipts.InsertOneAsync(
                        session,
                        receipt,
                        cancellationToken: transactionCt);
                    if (currentStep.State == DynamicFlowStepStates.Approved)
                    {
                        var completed = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                            session,
                            DynamicFlowRuntimeRevisionContract.StepCas(
                                currentStep.Id,
                                currentStep.Revision,
                                DynamicFlowStepStates.Approved),
                            Builders<DynamicFlowStepInstance>.Update
                                .Set(x => x.State, DynamicFlowStepStates.Completed)
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, actorUserId)
                                .Inc(x => x.Revision, 1),
                            cancellationToken: transactionCt);
                        if (completed.ModifiedCount != 1)
                            throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    }
                    await _ctx.DynamicFlowStepInstances.InsertOneAsync(
                        session,
                        nextStep,
                        cancellationToken: transactionCt);
                    await _ctx.DynamicFlowRuntimeOutbox.InsertOneAsync(
                        session,
                        outbox,
                        cancellationToken: transactionCt);
                    await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                        session,
                        acceptedEvent,
                        cancellationToken: transactionCt);
                    var advanced = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        DynamicFlowRuntimeRevisionContract.InstanceCas(
                            currentInstance.Id,
                            currentInstance.Revision,
                            DynamicFlowInstanceStates.Active),
                        Builders<DynamicFlowInstance>.Update
                            .Inc(x => x.Revision, 1)
                            .Inc(x => x.NextEventSequence, 2)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId),
                        cancellationToken: transactionCt);
                    if (advanced.ModifiedCount != 1)
                        throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        currentInstance.WorkId,
                        transactionCt);
                },
                ct);
            if (transactionReplay is not null)
                return transactionReplay;
        }
        catch (MongoException error) when (IsDuplicateKey(error))
        {
            existing = await _ctx.DynamicFlowRuntimeCommandReceipts
                .Find(x => x.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (existing is null)
                throw await LoadForwardRevisionConflictAsync(
                    instance.Id,
                    parentStep.Id,
                    instance.Revision,
                    parentStep.Revision,
                    ct);
            return ParseForwardReplay(existing, requestHash);
        }
        catch (InvalidOperationException error) when (
            error.Message == "DYNAMIC_FLOW_REVISION_CONFLICT")
        {
            throw await LoadForwardRevisionConflictAsync(
                instance.Id,
                parentStep.Id,
                instance.Revision,
                parentStep.Revision,
                ct);
        }

        try
        {
            await _runtimeMaterializer.ProcessPendingAsync(10, ct);
        }
        catch (Exception error) when (
            error is not OperationCanceledException &&
            error is not TaskCanceledException)
        {
            // The accepted aggregate transition remains recoverable from its
            // durable outbox. The response intentionally reports the committed
            // semantic result rather than converting an after-commit worker
            // failure into a changed command result.
        }

        return ParseForwardReplay(
            new DynamicFlowRuntimeCommandReceipt
            {
                CommandType = "FORWARD",
                CommandId = commandId,
                RequestHash = requestHash,
                Status = DynamicFlowRuntimeCommandStatuses.Succeeded,
                ResultSnapshot = resultSnapshot,
                ResultSnapshotHash = DynamicFlowSequentialTopologyContract.Hash(
                    resultSnapshot.ToJson())
            },
            requestHash,
            replayed: false);
    }

    private async Task<DynamicFlowBranchActionResponse> ForwardParallelForkAsync(
        string workId,
        WorkAssignment assignment,
        DynamicFlowStepInstance parentStep,
        DynamicFlowInstance instance,
        DynamicFlowBranchActionRequest req,
        string commandId,
        string actorUserId,
        CancellationToken ct)
    {
        if (!_runtimeActivation.CanExecuteP6CandidatePin(
                instance.CatalogVersion,
                instance.CatalogSemanticHash,
                instance.ArchetypeId))
        {
            throw ForwardBlocked(instance.ArchetypeId);
        }

        var topology = RequireParallelFanOutTopology(instance);
        var expectedDefinitionRevision =
            DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                instance.FlowTemplateVersionId,
                instance.FlowTemplateVersionNo,
                instance.FlowPayloadHash);
        if (!string.Equals(
                instance.DefinitionRevision,
                expectedDefinitionRevision,
                StringComparison.Ordinal) ||
            !string.Equals(
                instance.TopologySnapshotHash,
                instance.FlowPayloadHash,
                StringComparison.Ordinal))
        {
            throw ForwardValidation("DYNAMIC_FLOW_TOPOLOGY_PIN_STALE");
        }
        RequireParallelForkEntryPins(instance, parentStep, assignment, topology);

        var rootBranchId = parentStep.BranchId;
        var gatewayInstanceId =
            DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                topology.ForkNode.NodeId,
                rootBranchId);
        var branchPlans = topology.Branches.Select(branch =>
        {
            var branchId = DynamicFlowParallelForkTopologyContract.BuildBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                gatewayInstanceId,
                branch.Edge.TransitionId,
                parentStep.TargetUnitId);
            var contributionId =
                DynamicFlowParallelForkTopologyContract.BuildContributionId(
                    gatewayInstanceId,
                    branchId,
                    branch.Edge.TransitionId);
            var stepId = DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                branch.Node.NodeId,
                branchId,
                1);
            return new
            {
                Branch = branch,
                BranchId = branchId,
                ContributionId = contributionId,
                StepId = stepId,
                AssignmentId =
                    DynamicFlowParallelForkTopologyContract.BuildAssignmentId(stepId)
            };
        }).ToList();

        var requestHash = DynamicFlowParallelForkTopologyContract.Hash(
            JsonSerializer.Serialize(new
            {
                workId,
                assignmentId = assignment.Id,
                commandId,
                actorUserId,
                instanceId = instance.Id,
                executionEpoch = instance.ExecutionEpoch,
                expectedInstanceRevision = req.ExpectedInstanceRevision!.Value,
                expectedStepRevision = req.ExpectedStepRevision!.Value,
                fromStepInstanceId = parentStep.Id,
                fromNodeId = parentStep.FlowStepId,
                gatewayNodeId = topology.ForkNode.NodeId,
                gatewayInstanceId,
                branches = branchPlans.Select(plan => new
                {
                    transitionId = plan.Branch.Edge.TransitionId,
                    toNodeId = plan.Branch.Node.NodeId,
                    plan.BranchId,
                    plan.ContributionId,
                    plan.StepId,
                    plan.AssignmentId
                }),
                topologyHash = instance.TopologySnapshotHash,
                definitionRevision = instance.DefinitionRevision
            }));
        var receiptId =
            DynamicFlowParallelForkTopologyContract.BuildForwardReceiptId(
                instance.Id,
                commandId);
        var existing = await _ctx.DynamicFlowRuntimeCommandReceipts
            .Find(x => x.Id == receiptId)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return ParseForwardReplay(existing, requestHash);
        if (instance.State != DynamicFlowInstanceStates.Active ||
            assignment.FlowEffectiveStatus != DynamicFlowEffectiveStatuses.Effective ||
            parentStep.State is not (DynamicFlowStepStates.Approved or DynamicFlowStepStates.Completed))
        {
            throw ForwardValidation("DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED");
        }
        if (instance.Revision != req.ExpectedInstanceRevision ||
            parentStep.Revision != req.ExpectedStepRevision)
        {
            throw ForwardRevisionConflict(instance.Revision, parentStep.Revision);
        }
        DynamicFlowTypedFactSnapshot? conditionalFactSnapshot = null;
        DynamicFlowConditionalDecision? conditionalDecision = null;
        if (instance.ArchetypeId ==
            DynamicFlowTypedConditionalTopologyContract.ArchetypeId)
        {
            var authoritativeReport = await _ctx.WorkAssignmentReports
                .Find(report =>
                    report.WorkAssignmentId == assignment.Id &&
                    report.IsActive &&
                    !report.IsDeleted)
                .SortByDescending(report => report.PeriodKey)
                .ThenByDescending(report => report.Id)
                .FirstOrDefaultAsync(ct);
            conditionalFactSnapshot =
                DynamicFlowTypedConditionalTopologyContract.BuildFactSnapshot(
                    assignment,
                    parentStep,
                    instance,
                    authoritativeReport);
            try
            {
                conditionalDecision =
                    DynamicFlowTypedConditionalTopologyContract.SelectBranch(
                        topology,
                        conditionalFactSnapshot);
            }
            catch (InvalidOperationException error)
            {
                throw ForwardValidation(error.Message);
            }
            branchPlans = branchPlans
                .Where(plan =>
                    plan.Branch.Edge.TransitionId ==
                    conditionalDecision.SelectedEdgeId)
                .ToList();
            if (branchPlans.Count != 1)
            {
                throw ForwardValidation(
                    "DYNAMIC_FLOW_CONDITION_SELECTED_EDGE_INVALID");
            }
        }
        var plannedStepIds = branchPlans.Select(plan => plan.StepId).ToList();
        if (await _ctx.DynamicFlowStepInstances
                .Find(x => plannedStepIds.Contains(x.Id) && !x.IsDeleted)
                .AnyAsync(ct))
        {
            throw ForwardValidation("DYNAMIC_FLOW_FORWARD_EDGE_ALREADY_ACTIVATED");
        }

        var participantSnapshot = await _ctx.DynamicFlowParticipantSnapshots
            .Find(x => x.Id == instance.ParticipantSnapshotId)
            .FirstOrDefaultAsync(ct)
            ?? throw ForwardValidation("DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_MISSING");
        var participantBinding = participantSnapshot.Bindings.SingleOrDefault(binding =>
                                     string.Equals(
                                         binding.TargetUnitId,
                                         parentStep.TargetUnitId,
                                         StringComparison.Ordinal))
                                 ?? throw ForwardValidation(
                                     "DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_BINDING_MISSING");
        var participantIds = participantBinding.AssigneeUserIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        RequireSequentialParticipantPins(
            instance,
            parentStep,
            participantSnapshot,
            actorUserId);

        var now = DateTime.UtcNow;
        var acceptedEventSequence = instance.NextEventSequence;
        var acceptedEventId = StableTopologyObjectId(
            $"{instance.Id}\n{instance.ExecutionEpoch}\nfork\n{commandId}\naccepted");
        var activatedBranches = new BsonArray(branchPlans.Select(plan =>
            new BsonDocument
            {
                { "stepInstanceId", plan.StepId },
                { "assignmentId", plan.AssignmentId },
                { "branchId", plan.BranchId },
                { "parentBranchId", rootBranchId },
                { "gatewayInstanceId", gatewayInstanceId },
                { "gatewayVersion", 1 },
                { "contributionId", plan.ContributionId },
                { "transitionId", plan.Branch.Edge.TransitionId },
                { "nodeId", plan.Branch.Node.NodeId },
                { "nodeCode", plan.Branch.Node.NodeCode }
            }));
        var first = branchPlans[0];
        var resultSnapshot = new BsonDocument
        {
            { "commandId", commandId },
            { "status", "ACCEPTED_PENDING_MATERIALIZATION" },
            { "businessWritePerformed", true },
            { "flowInstanceId", instance.Id },
            { "executionEpoch", instance.ExecutionEpoch },
            { "instanceRevision", instance.Revision + 1 },
            { "stepInstanceId", parentStep.Id },
            { "nextStepInstanceId", first.StepId },
            { "nextAssignmentId", first.AssignmentId },
            { "activatedTransitionId", topology.EntryToForkEdge.TransitionId },
            { "gatewayInstanceId", gatewayInstanceId },
            { "activatedBranches", activatedBranches },
            { "eventId", acceptedEventId }
        };
        if (conditionalDecision is not null)
        {
            resultSnapshot.Add(
                "inputSnapshotHash",
                conditionalDecision.InputSnapshotHash);
            resultSnapshot.Add(
                "evaluatorVersion",
                conditionalDecision.EvaluatorVersion);
            resultSnapshot.Add(
                "selectedEdgeId",
                conditionalDecision.SelectedEdgeId);
            resultSnapshot.Add(
                "decisionReasonCode",
                conditionalDecision.ReasonCode);
        }
        var commandIdentityHash = DynamicFlowParallelForkTopologyContract.Hash(
            $"{actorUserId}\n{instance.Id}\n{commandId}\nFORWARD");
        DynamicFlowBranchActionResponse? transactionReplay = null;

        try
        {
            await _transactions.ExecuteAsync(
                async (session, transactionCt) =>
                {
                    var replay = await _ctx.DynamicFlowRuntimeCommandReceipts
                        .Find(session, x => x.Id == receiptId)
                        .FirstOrDefaultAsync(transactionCt);
                    if (replay is not null)
                    {
                        transactionReplay = ParseForwardReplay(replay, requestHash);
                        return;
                    }

                    var currentInstance = await _ctx.DynamicFlowInstances
                        .Find(
                            session,
                            x =>
                                x.Id == instance.Id &&
                                x.State == DynamicFlowInstanceStates.Active &&
                                x.Revision == req.ExpectedInstanceRevision.Value &&
                                x.ExecutionEpoch == instance.ExecutionEpoch &&
                                x.TopologySnapshotHash == instance.TopologySnapshotHash &&
                                !x.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    var currentStep = await _ctx.DynamicFlowStepInstances
                        .Find(
                            session,
                            x =>
                                x.Id == parentStep.Id &&
                                x.FlowInstanceId == instance.Id &&
                                x.Revision == req.ExpectedStepRevision.Value &&
                                x.ExecutionEpoch == instance.ExecutionEpoch &&
                                !x.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    var currentAssignment = await _ctx.WorkAssignments
                        .Find(
                            session,
                            x =>
                                x.Id == assignment.Id &&
                                x.WorkId == workId &&
                                x.FlowInstanceId == instance.Id &&
                                x.FlowEffectiveStatus ==
                                DynamicFlowEffectiveStatuses.Effective &&
                                !x.IsDeleted)
                        .FirstOrDefaultAsync(transactionCt);
                    if (currentInstance is null ||
                        currentStep is null ||
                        currentAssignment is null)
                    {
                        throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    }
                    if (currentStep.State is not (
                            DynamicFlowStepStates.Approved or
                            DynamicFlowStepStates.Completed))
                    {
                        throw new InvalidOperationException(
                            "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED");
                    }
                    if (conditionalFactSnapshot is not null)
                    {
                        var currentReport = await _ctx.WorkAssignmentReports
                            .Find(
                                session,
                                report =>
                                    report.WorkAssignmentId ==
                                        currentAssignment.Id &&
                                    report.IsActive &&
                                    !report.IsDeleted)
                            .SortByDescending(report => report.PeriodKey)
                            .ThenByDescending(report => report.Id)
                            .FirstOrDefaultAsync(transactionCt);
                        var currentFacts =
                            DynamicFlowTypedConditionalTopologyContract
                                .BuildFactSnapshot(
                                    currentAssignment,
                                    currentStep,
                                    currentInstance,
                                    currentReport);
                        if (currentFacts.SnapshotHash !=
                            conditionalFactSnapshot.SnapshotHash)
                        {
                            throw new InvalidOperationException(
                                "DYNAMIC_FLOW_REVISION_CONFLICT");
                        }
                    }
                    RequireParallelForkEntryPins(
                        currentInstance,
                        currentStep,
                        currentAssignment,
                        topology);
                    if (await _ctx.DynamicFlowStepInstances
                            .Find(
                                session,
                                x => plannedStepIds.Contains(x.Id) && !x.IsDeleted)
                            .AnyAsync(transactionCt))
                    {
                        throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    }

                    var steps = new List<DynamicFlowStepInstance>();
                    var outboxItems = new List<DynamicFlowRuntimeOutboxItem>();
                    for (var index = 0; index < branchPlans.Count; index++)
                    {
                        var plan = branchPlans[index];
                        var step = new DynamicFlowStepInstance
                        {
                            Id = plan.StepId,
                            FlowInstanceId = instance.Id,
                            FlowStepId = plan.Branch.Node.NodeId,
                            FlowStepCode = plan.Branch.Node.NodeCode,
                            ExecutionEpoch = instance.ExecutionEpoch,
                            DefinitionRevision = instance.DefinitionRevision,
                            StepOrder = plan.Branch.StepOrder,
                            FormNodeId = plan.Branch.Form.FormNodeId,
                            FormFamilyId = plan.Branch.Form.DynamicFormFamilyId!,
                            FormVersionId = plan.Branch.Form.DynamicFormTemplateId,
                            FormVersionNo = plan.Branch.Form.DynamicFormVersionNo!.Value,
                            FormSchemaHash = plan.Branch.Form.DynamicFormSchemaHash!,
                            FormSnapshotHash = plan.Branch.Form.DynamicFormSnapshotHash!,
                            TargetUnitId = parentStep.TargetUnitId,
                            ParticipantUserIds = participantIds,
                            ParticipantSnapshotId = participantSnapshot.Id,
                            AttemptNo = 1,
                            BranchId = plan.BranchId,
                            ActivatedByTransitionId = plan.Branch.Edge.TransitionId,
                            NextNodeIds = new List<string>(),
                            IsTerminalNode = true,
                            ResultOwnerIdentity =
                                DynamicFlowParallelForkTopologyContract
                                    .BuildStepOwnerIdentity(
                                        instance.Id,
                                        instance.ExecutionEpoch,
                                        topology.Payload.ResultOwnerStepId ??
                                        plan.Branch.Node.NodeId,
                                        plan.BranchId,
                                        "result"),
                            StatisticOwnerIdentity =
                                DynamicFlowParallelForkTopologyContract
                                    .BuildStepOwnerIdentity(
                                        instance.Id,
                                        instance.ExecutionEpoch,
                                        topology.Payload.StatisticsOwnerStepId ??
                                        plan.Branch.Node.NodeId,
                                        plan.BranchId,
                                        "statistics"),
                            GatewayInstanceId = gatewayInstanceId,
                            GatewayVersion = 1,
                            ContributionId = plan.ContributionId,
                            AssignmentId = plan.AssignmentId,
                            State = DynamicFlowStepStates.Materializing,
                            Revision = 1,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            CreatedByUserId = actorUserId,
                            UpdatedByUserId = actorUserId,
                            IsDeleted = false
                        };
                        var payload = new BsonDocument
                        {
                            { "commandId", commandId },
                            { "actorUserId", actorUserId },
                            { "issuerUnitId", instance.IssuerUnitId },
                            { "archetypeId", instance.ArchetypeId },
                            { "branchOrdinal", plan.Branch.StepOrder },
                            { "targetUnitId", parentStep.TargetUnitId },
                            { "assignmentId", plan.AssignmentId },
                            { "periodKey", instance.PeriodKey },
                            { "scheduleIdentityJson", instance.ScheduleIdentityJson },
                            { "scheduleIdentityHash", instance.ScheduleIdentityHash },
                            { "executionEpoch", instance.ExecutionEpoch },
                            { "branchId", plan.BranchId },
                            { "parentBranchId", rootBranchId },
                            { "attemptNo", 1 },
                            { "transitionId", plan.Branch.Edge.TransitionId },
                            { "gatewayInstanceId", gatewayInstanceId },
                            { "gatewayVersion", 1 },
                            { "contributionId", plan.ContributionId },
                            {
                                "materializationEventSequence",
                                acceptedEventSequence + index + 1
                            },
                            {
                                "participants",
                                new BsonArray(participantBinding.Participants
                                    .OrderBy(user => user.UserId, StringComparer.Ordinal)
                                    .Select(TopologyParticipantDocument))
                            }
                        };
                        steps.Add(step);
                        outboxItems.Add(new DynamicFlowRuntimeOutboxItem
                        {
                            Id = StableTopologyObjectId(
                                $"{plan.StepId}\noutbox\nmaterialize"),
                            FlowInstanceId = instance.Id,
                            StepInstanceId = plan.StepId,
                            Operation =
                                DynamicFlowRuntimeMaterializationOperations
                                    .MaterializeForkBranchAssignment,
                            DedupeKey =
                                $"{instance.Id}:{instance.ExecutionEpoch}:{plan.Branch.Node.NodeId}:{plan.BranchId}:1:materialize",
                            Payload = payload,
                            PayloadHash =
                                DynamicFlowParallelForkTopologyContract.Hash(
                                    payload.ToJson()),
                            Status = DynamicFlowRuntimeOutboxStatuses.Pending,
                            NextAttemptAtUtc = now,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            UpdatedByUserId = actorUserId
                        });
                    }

                    var eventPayload = new BsonDocument
                    {
                        { "fromStepInstanceId", parentStep.Id },
                        { "fromNodeId", parentStep.FlowStepId },
                        { "gatewayNodeId", topology.ForkNode.NodeId },
                        { "gatewayInstanceId", gatewayInstanceId },
                        { "gatewayVersion", 1 },
                        { "parentBranchId", rootBranchId },
                        { "branches", activatedBranches },
                        { "executionEpoch", instance.ExecutionEpoch },
                        { "definitionRevision", instance.DefinitionRevision },
                        { "topologyHash", instance.TopologySnapshotHash }
                    };
                    if (conditionalDecision is not null)
                    {
                        eventPayload.Add(
                            "inputSnapshotHash",
                            conditionalDecision.InputSnapshotHash);
                        eventPayload.Add(
                            "evaluatorVersion",
                            conditionalDecision.EvaluatorVersion);
                        eventPayload.Add(
                            "selectedEdgeId",
                            conditionalDecision.SelectedEdgeId);
                        eventPayload.Add(
                            "selectedNodeId",
                            conditionalDecision.SelectedNodeId);
                        eventPayload.Add(
                            "decisionReasonCode",
                            conditionalDecision.ReasonCode);
                    }
                    var acceptedEvent = new DynamicFlowRuntimeEvent
                    {
                        Id = acceptedEventId,
                        FlowInstanceId = instance.Id,
                        StepInstanceId = parentStep.Id,
                        ExecutionEpoch = instance.ExecutionEpoch,
                        BranchId = rootBranchId,
                        AttemptNo = parentStep.AttemptNo,
                        GatewayInstanceId = gatewayInstanceId,
                        GatewayVersion = 1,
                        Sequence = acceptedEventSequence,
                        EventType = DynamicFlowRuntimeEventTypes.ParallelForkAccepted,
                        CommandId = commandId,
                        FromState = parentStep.State,
                        ToState = DynamicFlowStepStates.Completed,
                        FromRevision = parentStep.Revision,
                        ToRevision = parentStep.State == DynamicFlowStepStates.Approved
                            ? parentStep.Revision + 1
                            : parentStep.Revision,
                        ReasonCode = conditionalDecision is null
                            ? "PARALLEL_FORK_ACTIVATED"
                            : conditionalDecision.ReasonCode,
                        AffectedRefs = branchPlans
                            .SelectMany(plan => new[]
                            {
                                $"step:{plan.StepId}",
                                $"assignment:{plan.AssignmentId}",
                                $"branch:{plan.BranchId}",
                                $"contribution:{plan.ContributionId}"
                            })
                            .Prepend($"assignment:{assignment.Id}")
                            .Prepend($"gateway:{gatewayInstanceId}")
                            .ToList(),
                        ActorUserId = actorUserId,
                        VisibleUnitIds =
                            new[] { parentStep.TargetUnitId, instance.IssuerUnitId }
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(value => value, StringComparer.Ordinal)
                                .ToList(),
                        Payload = eventPayload,
                        PayloadHash =
                            DynamicFlowParallelForkTopologyContract.Hash(
                                eventPayload.ToJson()),
                        OccurredAtUtc = now
                    };
                    var receipt = new DynamicFlowRuntimeCommandReceipt
                    {
                        Id = receiptId,
                        ScopeKind = DynamicFlowCommandScopeKinds.Instance,
                        ScopeId = instance.Id,
                        WorkId = workId,
                        FlowTemplateVersionId = instance.FlowTemplateVersionId,
                        FlowInstanceId = instance.Id,
                        CommandType = "FORWARD",
                        CommandId = commandId,
                        RequestHash = requestHash,
                        CommandIdentityHash = commandIdentityHash,
                        SnapshotToken =
                            DynamicFlowParallelForkTopologyContract.Hash(
                                $"{requestHash}\n{instance.TopologySnapshotHash}"),
                        ExpectedRevision = req.ExpectedInstanceRevision,
                        Status = DynamicFlowRuntimeCommandStatuses.Succeeded,
                        ResultSnapshot = resultSnapshot,
                        ResultSnapshotHash =
                            DynamicFlowParallelForkTopologyContract.Hash(
                                resultSnapshot.ToJson()),
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        UpdatedByUserId = actorUserId,
                        CompletedAtUtc = now
                    };

                    var assignmentFence = await _ctx.WorkAssignments.UpdateOneAsync(
                        session,
                        x =>
                            x.Id == currentAssignment.Id &&
                            x.DynamicFlowMaterializationRevision ==
                            currentAssignment.DynamicFlowMaterializationRevision &&
                            x.FlowEffectiveStatus ==
                            DynamicFlowEffectiveStatuses.Effective &&
                            !x.IsDeleted,
                        Builders<WorkAssignment>.Update
                            .Inc(x => x.DynamicFlowMaterializationRevision, 1)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId),
                        cancellationToken: transactionCt);
                    if (assignmentFence.ModifiedCount != 1)
                        throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    await _ctx.DynamicFlowRuntimeCommandReceipts.InsertOneAsync(
                        session,
                        receipt,
                        cancellationToken: transactionCt);
                    if (currentStep.State == DynamicFlowStepStates.Approved)
                    {
                        var completed = await _ctx.DynamicFlowStepInstances.UpdateOneAsync(
                            session,
                            DynamicFlowRuntimeRevisionContract.StepCas(
                                currentStep.Id,
                                currentStep.Revision,
                                DynamicFlowStepStates.Approved),
                            Builders<DynamicFlowStepInstance>.Update
                                .Set(x => x.State, DynamicFlowStepStates.Completed)
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, actorUserId)
                                .Inc(x => x.Revision, 1),
                            cancellationToken: transactionCt);
                        if (completed.ModifiedCount != 1)
                            throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    }
                    await _ctx.DynamicFlowStepInstances.InsertManyAsync(
                        session,
                        steps,
                        cancellationToken: transactionCt);
                    if (currentInstance.ArchetypeId is
                        DynamicFlowJoinAllTopologyContract.ArchetypeId or
                        DynamicFlowJoinQuorumTopologyContract.ArchetypeId)
                    {
                        var downstreamNodeId = topology.Payload.Edges
                            .Single(edge =>
                                string.Equals(
                                    edge.FromNodeId,
                                    topology.ForkNode.NodeId,
                                    StringComparison.Ordinal))
                            .ToNodeId;
                        await _ctx.DynamicFlowGatewayInstances.InsertOneAsync(
                            session,
                            new DynamicFlowGatewayInstance
                            {
                                Id = DynamicFlowJoinAllTopologyContract
                                    .BuildGatewayLedgerId(
                                        gatewayInstanceId,
                                        DynamicFlowJoinAllTopologyContract
                                            .GatewayVersion),
                                FlowInstanceId = currentInstance.Id,
                                ExecutionEpoch = currentInstance.ExecutionEpoch,
                                GatewayNodeId = topology.ForkNode.NodeId,
                                GatewayInstanceId = gatewayInstanceId,
                                GatewayVersion =
                                    DynamicFlowJoinAllTopologyContract
                                        .GatewayVersion,
                                GatewayKind =
                                    topology.ForkNode.Gateway!.Kind,
                                ExpectedContributionIds = branchPlans
                                    .Select(plan => plan.ContributionId)
                                    .OrderBy(value => value, StringComparer.Ordinal)
                                    .ToList(),
                                ArrivedContributionIds = new List<string>(),
                                RequiredContributionCount =
                                    currentInstance.ArchetypeId ==
                                    DynamicFlowJoinAllTopologyContract.ArchetypeId
                                        ? branchPlans.Count
                                        : DynamicFlowJoinQuorumTopologyContract
                                            .ResolveRequiredContributionCount(
                                                topology.ForkNode.Gateway),
                                CancelledContributionIds = new List<string>(),
                                LateContributionIds = new List<string>(),
                                DownstreamNodeId = downstreamNodeId,
                                State = DynamicFlowGatewayStates.Collecting,
                                Revision = 1,
                                CreatedAtUtc = now,
                                UpdatedAtUtc = now,
                                UpdatedByUserId = actorUserId
                            },
                            cancellationToken: transactionCt);
                    }
                    else if (currentInstance.ArchetypeId ==
                             DynamicFlowTypedConditionalTopologyContract
                                 .ArchetypeId &&
                             conditionalDecision is not null)
                    {
                        await _ctx.DynamicFlowGatewayInstances.InsertOneAsync(
                            session,
                            new DynamicFlowGatewayInstance
                            {
                                Id = DynamicFlowTypedConditionalTopologyContract
                                    .BuildDecisionLedgerId(
                                        gatewayInstanceId,
                                        DynamicFlowTypedConditionalTopologyContract
                                            .GatewayVersion),
                                FlowInstanceId = currentInstance.Id,
                                ExecutionEpoch = currentInstance.ExecutionEpoch,
                                GatewayNodeId = topology.ForkNode.NodeId,
                                GatewayInstanceId = gatewayInstanceId,
                                GatewayVersion =
                                    DynamicFlowTypedConditionalTopologyContract
                                        .GatewayVersion,
                                GatewayKind =
                                    DynamicFlowGatewayKinds.Condition,
                                ExpectedContributionIds = new List<string>(),
                                ArrivedContributionIds = new List<string>(),
                                RequiredContributionCount = 1,
                                CancelledContributionIds = new List<string>(),
                                LateContributionIds = new List<string>(),
                                InputSnapshotHash =
                                    conditionalDecision.InputSnapshotHash,
                                EvaluatorVersion =
                                    conditionalDecision.EvaluatorVersion,
                                SelectedEdgeId =
                                    conditionalDecision.SelectedEdgeId,
                                DecisionReasonCode =
                                    conditionalDecision.ReasonCode,
                                DownstreamNodeId =
                                    conditionalDecision.SelectedNodeId,
                                State = DynamicFlowGatewayStates.Satisfied,
                                Revision = 1,
                                ReleasedAtUtc = now,
                                CreatedAtUtc = now,
                                UpdatedAtUtc = now,
                                UpdatedByUserId = actorUserId
                            },
                            cancellationToken: transactionCt);
                    }
                    await _ctx.DynamicFlowRuntimeOutbox.InsertManyAsync(
                        session,
                        outboxItems,
                        cancellationToken: transactionCt);
                    await _ctx.DynamicFlowRuntimeEvents.InsertOneAsync(
                        session,
                        acceptedEvent,
                        cancellationToken: transactionCt);
                    var advanced = await _ctx.DynamicFlowInstances.UpdateOneAsync(
                        session,
                        DynamicFlowRuntimeRevisionContract.InstanceCas(
                            currentInstance.Id,
                            currentInstance.Revision,
                            DynamicFlowInstanceStates.Active),
                        Builders<DynamicFlowInstance>.Update
                            .Inc(x => x.Revision, 1)
                            .Inc(x => x.NextEventSequence, branchPlans.Count + 1)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId),
                        cancellationToken: transactionCt);
                    if (advanced.ModifiedCount != 1)
                        throw new InvalidOperationException("DYNAMIC_FLOW_REVISION_CONFLICT");
                    await WorkDirectSourceRevisionFence.IncrementAsync(
                        _ctx,
                        session,
                        currentInstance.WorkId,
                        transactionCt);
                },
                ct);
            if (transactionReplay is not null)
                return transactionReplay;
        }
        catch (MongoException error) when (IsDuplicateKey(error))
        {
            existing = await _ctx.DynamicFlowRuntimeCommandReceipts
                .Find(x => x.Id == receiptId)
                .FirstOrDefaultAsync(ct);
            if (existing is null)
                throw await LoadForwardRevisionConflictAsync(
                    instance.Id,
                    parentStep.Id,
                    instance.Revision,
                    parentStep.Revision,
                    ct);
            return ParseForwardReplay(existing, requestHash);
        }
        catch (InvalidOperationException error) when (
            error.Message == "DYNAMIC_FLOW_REVISION_CONFLICT")
        {
            throw await LoadForwardRevisionConflictAsync(
                instance.Id,
                parentStep.Id,
                instance.Revision,
                parentStep.Revision,
                ct);
        }

        try
        {
            await _runtimeMaterializer.ProcessPendingAsync(10, ct);
        }
        catch (Exception error) when (
            error is not OperationCanceledException &&
            error is not TaskCanceledException)
        {
            // Both branch intents remain durable and recoverable after commit.
        }

        return ParseForwardReplay(
            new DynamicFlowRuntimeCommandReceipt
            {
                CommandType = "FORWARD",
                CommandId = commandId,
                RequestHash = requestHash,
                Status = DynamicFlowRuntimeCommandStatuses.Succeeded,
                ResultSnapshot = resultSnapshot,
                ResultSnapshotHash =
                    DynamicFlowParallelForkTopologyContract.Hash(
                        resultSnapshot.ToJson())
            },
            requestHash,
            replayed: false);
    }

    private static DynamicFlowBranchActionResponse ParseForwardReplay(
        DynamicFlowRuntimeCommandReceipt receipt,
        string requestHash,
        bool replayed = true)
    {
        if (receipt.CommandType != "FORWARD" ||
            !string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal) ||
            receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
            receipt.ResultSnapshot is null ||
            !string.Equals(
                receipt.ResultSnapshotHash,
                DynamicFlowSequentialTopologyContract.Hash(
                    receipt.ResultSnapshot.ToJson()),
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.Create(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT,
                new { reason = "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT" });
        }

        var result = receipt.ResultSnapshot;
        var activatedBranches = result.TryGetValue(
                "activatedBranches",
                out var activated) &&
            activated.IsBsonArray
            ? activated.AsBsonArray.Select(item =>
            {
                var branch = item.AsBsonDocument;
                return new DynamicFlowActivatedBranchDto
                {
                    StepInstanceId = branch["stepInstanceId"].AsString,
                    AssignmentId = branch["assignmentId"].AsString,
                    BranchId = branch["branchId"].AsString,
                    ParentBranchId = branch["parentBranchId"].AsString,
                    GatewayInstanceId = branch["gatewayInstanceId"].AsString,
                    GatewayVersion = branch["gatewayVersion"].AsInt32,
                    ContributionId = branch["contributionId"].AsString,
                    TransitionId = branch["transitionId"].AsString,
                    NodeId = branch["nodeId"].AsString,
                    NodeCode = branch["nodeCode"].AsString
                };
            }).ToList()
            : new List<DynamicFlowActivatedBranchDto>();
        return new DynamicFlowBranchActionResponse
        {
            CommandId = result["commandId"].AsString,
            Status = result["status"].AsString,
            BusinessWritePerformed = result["businessWritePerformed"].AsBoolean,
            Replayed = replayed,
            FlowInstanceId = result["flowInstanceId"].AsString,
            ExecutionEpoch = result["executionEpoch"].AsInt32,
            InstanceRevision = result["instanceRevision"].ToInt64(),
            StepInstanceId = result["stepInstanceId"].AsString,
            NextStepInstanceId = result["nextStepInstanceId"].AsString,
            NextAssignmentId = result["nextAssignmentId"].AsString,
            ActivatedTransitionId = result["activatedTransitionId"].AsString,
            GatewayInstanceId = result.TryGetValue(
                "gatewayInstanceId",
                out var gatewayInstanceId)
                ? gatewayInstanceId.AsString
                : null,
            InputSnapshotHash = result.TryGetValue(
                "inputSnapshotHash",
                out var inputSnapshotHash)
                ? inputSnapshotHash.AsString
                : null,
            EvaluatorVersion = result.TryGetValue(
                "evaluatorVersion",
                out var evaluatorVersion)
                ? evaluatorVersion.AsString
                : null,
            SelectedEdgeId = result.TryGetValue(
                "selectedEdgeId",
                out var selectedEdgeId)
                ? selectedEdgeId.AsString
                : null,
            DecisionReasonCode = result.TryGetValue(
                "decisionReasonCode",
                out var decisionReasonCode)
                ? decisionReasonCode.AsString
                : null,
            ActivatedBranches = activatedBranches,
            EventId = result["eventId"].AsString,
            Action = "FORWARD",
            AssignmentId = result["nextAssignmentId"].AsString,
            FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective,
            FlowAttemptNo = 1,
            AffectedAssignmentCount = Math.Max(1, activatedBranches.Count)
        };
    }

    private static void RequireParallelForkEntryPins(
        DynamicFlowInstance instance,
        DynamicFlowStepInstance step,
        WorkAssignment assignment,
        DynamicFlowParallelForkTopology topology)
    {
        var expectedBranchId =
            DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        var expectedStepId =
            DynamicFlowParallelForkTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                topology.EntryNode.NodeId,
                expectedBranchId,
                1);
        var expectedAssignmentId =
            DynamicFlowParallelForkTopologyContract.BuildAssignmentId(
                expectedStepId);
        if (instance.ExecutionEpoch < 1 ||
            instance.TopologySnapshotJson != topology.CanonicalJson ||
            instance.TopologySnapshotHash != topology.TopologyHash ||
            instance.TopologySnapshotHash != instance.FlowPayloadHash ||
            step.Id != expectedStepId ||
            step.FlowStepId != topology.EntryNode.NodeId ||
            step.FlowStepCode != topology.EntryNode.NodeCode ||
            step.StepOrder != 1 ||
            step.FormNodeId != topology.EntryForm.FormNodeId ||
            step.FormFamilyId != topology.EntryForm.DynamicFormFamilyId ||
            step.FormVersionId != topology.EntryForm.DynamicFormTemplateId ||
            step.FormVersionNo != topology.EntryForm.DynamicFormVersionNo ||
            step.FormSchemaHash != topology.EntryForm.DynamicFormSchemaHash ||
            step.FormSnapshotHash != topology.EntryForm.DynamicFormSnapshotHash ||
            step.ExecutionEpoch != instance.ExecutionEpoch ||
            step.AttemptNo != 1 ||
            step.BranchId != expectedBranchId ||
            step.AssignmentId != expectedAssignmentId ||
            step.GatewayInstanceId is not null ||
            step.GatewayVersion is not null ||
            step.ContributionId is not null ||
            !step.NextNodeIds.SequenceEqual(
                new[] { topology.ForkNode.NodeId },
                StringComparer.Ordinal) ||
            step.IsTerminalNode ||
            assignment.Id != expectedAssignmentId ||
            assignment.WorkId != instance.WorkId ||
            assignment.FlowInstanceId != instance.Id ||
            assignment.FlowStepId != step.FlowStepId ||
            assignment.FlowStepCode != step.FlowStepCode ||
            assignment.FlowStepOrder != step.StepOrder ||
            assignment.FlowBranchId != expectedBranchId ||
            assignment.FlowAttemptNo != 1 ||
            assignment.DynamicFormTemplateId != step.FormVersionId ||
            assignment.DynamicFormFamilyId != step.FormFamilyId ||
            assignment.DynamicFormVersionNo != step.FormVersionNo ||
            assignment.DynamicFormSchemaHash != step.FormSchemaHash ||
            assignment.TargetUnitIds is null ||
            assignment.TargetUnitIds.Count != 1 ||
            assignment.TargetUnitIds[0] != step.TargetUnitId ||
            assignment.IsFlowFinalNode == true)
        {
            throw ForwardValidation("DYNAMIC_FLOW_TOPOLOGY_IDENTITY_DRIFT");
        }
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
                "DYNAMIC_FLOW_ARCHETYPE_BLOCKED_UNTIL_TARGET_PROMPT")
        };

    private static void RequireSequentialRuntimePins(
        DynamicFlowInstance instance,
        DynamicFlowStepInstance step,
        WorkAssignment assignment,
        DynamicFlowSequentialTopology topology)
    {
        var orderedNode = topology.OrderedNodes
            .Select((node, index) => new { Node = node, Order = index + 1 })
            .SingleOrDefault(item =>
                string.Equals(
                    item.Node.NodeId,
                    step.FlowStepId,
                    StringComparison.Ordinal));
        if (orderedNode is null)
            throw ForwardValidation("DYNAMIC_FLOW_TOPOLOGY_IDENTITY_DRIFT");
        var node = orderedNode.Node;
        var form = topology.FormsByNodeId[node.NodeId];
        var expectedNextNodeIds =
            topology.OutgoingByNodeId.TryGetValue(node.NodeId, out var outgoing)
                ? new[] { outgoing.ToNodeId }
                : Array.Empty<string>();
        var incoming = topology.OutgoingByNodeId.Values.SingleOrDefault(edge =>
            string.Equals(edge.ToNodeId, node.NodeId, StringComparison.Ordinal));
        var expectedBranchId =
            DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                instance.Id,
                instance.ExecutionEpoch,
                step.TargetUnitId);
        var expectedStepId =
            DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                node.NodeId,
                expectedBranchId,
                step.AttemptNo);
        var expectedAssignmentId =
            DynamicFlowSequentialTopologyContract.BuildAssignmentId(
                expectedStepId);
        var expectedResultOwner =
            DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
            instance.Id,
            instance.ExecutionEpoch,
            topology.Payload.ResultOwnerStepId ?? node.NodeId,
            expectedBranchId,
            "result");
        var expectedStatisticOwner =
            DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
            instance.Id,
            instance.ExecutionEpoch,
            topology.Payload.StatisticsOwnerStepId ?? node.NodeId,
            expectedBranchId,
            "statistics");

        if (instance.ExecutionEpoch < 1 ||
            !string.Equals(
                instance.TopologySnapshotJson,
                topology.CanonicalJson,
                StringComparison.Ordinal) ||
            !string.Equals(
                instance.TopologySnapshotHash,
                topology.TopologyHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                instance.TopologySnapshotHash,
                instance.FlowPayloadHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                DynamicFlowSequentialTopologyContract.Hash(
                    instance.ScheduleIdentityJson),
                instance.ScheduleIdentityHash,
                StringComparison.Ordinal) ||
            step.ExecutionEpoch != instance.ExecutionEpoch ||
            !string.Equals(
                step.DefinitionRevision,
                instance.DefinitionRevision,
                StringComparison.Ordinal) ||
            step.StepOrder != orderedNode.Order ||
            !string.Equals(step.FlowStepCode, node.NodeCode, StringComparison.Ordinal) ||
            !string.Equals(step.FormNodeId, form.FormNodeId, StringComparison.Ordinal) ||
            !string.Equals(
                step.FormFamilyId,
                form.DynamicFormFamilyId,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.FormVersionId,
                form.DynamicFormTemplateId,
                StringComparison.Ordinal) ||
            step.FormVersionNo != form.DynamicFormVersionNo ||
            !string.Equals(
                step.FormSchemaHash,
                form.DynamicFormSchemaHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.FormSnapshotHash,
                form.DynamicFormSnapshotHash,
                StringComparison.Ordinal) ||
            step.AttemptNo != 1 ||
            !string.Equals(step.BranchId, expectedBranchId, StringComparison.Ordinal) ||
            !string.Equals(step.Id, expectedStepId, StringComparison.Ordinal) ||
            !string.Equals(
                step.AssignmentId,
                expectedAssignmentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.ParticipantSnapshotId,
                instance.ParticipantSnapshotId,
                StringComparison.Ordinal) ||
            !step.NextNodeIds.SequenceEqual(expectedNextNodeIds, StringComparer.Ordinal) ||
            step.IsTerminalNode != (expectedNextNodeIds.Length == 0) ||
            !string.Equals(
                step.ActivatedByTransitionId,
                incoming?.TransitionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.ResultOwnerIdentity,
                expectedResultOwner,
                StringComparison.Ordinal) ||
            !string.Equals(
                step.StatisticOwnerIdentity,
                expectedStatisticOwner,
                StringComparison.Ordinal) ||
            !string.Equals(assignment.Id, expectedAssignmentId, StringComparison.Ordinal) ||
            !string.Equals(assignment.WorkId, instance.WorkId, StringComparison.Ordinal) ||
            !string.Equals(
                assignment.FlowTemplateId,
                instance.FlowTemplateId,
                StringComparison.Ordinal) ||
            assignment.FlowTemplateVersionNo != instance.FlowTemplateVersionNo ||
            !string.Equals(
                assignment.FlowInstanceId,
                instance.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.FlowStepId,
                step.FlowStepId,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.FlowStepCode,
                step.FlowStepCode,
                StringComparison.Ordinal) ||
            assignment.FlowStepOrder != step.StepOrder ||
            !string.Equals(
                assignment.FlowBranchId,
                step.BranchId,
                StringComparison.Ordinal) ||
            assignment.FlowAttemptNo != step.AttemptNo ||
            !string.Equals(
                assignment.FlowRole,
                DynamicFlowRuntimePlanner.AssignmentFlowRole,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.DynamicFormTemplateId,
                step.FormVersionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                assignment.DynamicFormFamilyId,
                step.FormFamilyId,
                StringComparison.Ordinal) ||
            assignment.DynamicFormVersionNo != step.FormVersionNo ||
            !string.Equals(
                assignment.DynamicFormSchemaHash,
                step.FormSchemaHash,
                StringComparison.Ordinal) ||
            assignment.TargetUnitIds is null ||
            assignment.TargetUnitIds.Count != 1 ||
            !string.Equals(
                assignment.TargetUnitIds[0],
                step.TargetUnitId,
                StringComparison.Ordinal) ||
            assignment.IsFlowFinalNode != step.IsTerminalNode)
        {
            throw ForwardValidation("DYNAMIC_FLOW_TOPOLOGY_IDENTITY_DRIFT");
        }
    }

    private static void RequireSequentialParticipantPins(
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
        var participantIds = binding?.Participants
            .Select(user => user.UserId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var assigneeIds = binding?.AssigneeUserIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var stepParticipantIds = step.ParticipantUserIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (binding is null ||
            snapshot.Id != instance.ParticipantSnapshotId ||
            snapshot.FlowInstanceId != instance.Id ||
            snapshot.IssuerUserId != instance.IssuerUserId ||
            snapshot.IssuerUnitId != instance.IssuerUnitId ||
            !string.Equals(
                snapshot.SnapshotHash,
                instance.ParticipantSnapshotHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                SequentialParticipantSnapshotHash(snapshot),
                instance.ParticipantSnapshotHash,
                StringComparison.Ordinal) ||
            !snapshot.SourceRevisionTokens.TryGetValue("catalog", out var catalog) ||
            !string.Equals(catalog, instance.CatalogSemanticHash, StringComparison.Ordinal) ||
            !snapshot.SourceRevisionTokens.TryGetValue("flow", out var flow) ||
            !string.Equals(flow, instance.FlowPayloadHash, StringComparison.Ordinal) ||
            !snapshot.SourceRevisionTokens.TryGetValue("topology", out var topology) ||
            !string.Equals(topology, instance.TopologySnapshotHash, StringComparison.Ordinal) ||
            participantIds is null ||
            assigneeIds is null ||
            participantIds.Length == 0 ||
            !participantIds.SequenceEqual(assigneeIds, StringComparer.Ordinal) ||
            !participantIds.SequenceEqual(stepParticipantIds, StringComparer.Ordinal) ||
            !participantIds.Contains(actorUserId, StringComparer.Ordinal) &&
            !string.Equals(instance.IssuerUserId, actorUserId, StringComparison.Ordinal))
        {
            throw ForwardValidation("DYNAMIC_FLOW_PARTICIPANT_SNAPSHOT_DRIFT");
        }
    }

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
                        .Select(TopologyParticipantDocument))
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

    private async Task<AppException> LoadForwardRevisionConflictAsync(
        string instanceId,
        string stepInstanceId,
        long fallbackInstanceRevision,
        long fallbackStepRevision,
        CancellationToken ct)
    {
        var currentInstanceRevision = await _ctx.DynamicFlowInstances
            .Find(x => x.Id == instanceId && !x.IsDeleted)
            .Project(x => (long?)x.Revision)
            .FirstOrDefaultAsync(ct);
        var currentStepRevision = await _ctx.DynamicFlowStepInstances
            .Find(x => x.Id == stepInstanceId && !x.IsDeleted)
            .Project(x => (long?)x.Revision)
            .FirstOrDefaultAsync(ct);
        return ForwardRevisionConflict(
            currentInstanceRevision ?? fallbackInstanceRevision,
            currentStepRevision ?? fallbackStepRevision);
    }

    private static string NormalizeRequiredCommandId(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.DYNAMIC_FLOW_COMMAND_ID_REQUIRED,
                new { reason = "DYNAMIC_FLOW_COMMAND_ID_REQUIRED" });
        }
        return value;
    }

    private Task<DynamicFlowRuntimeCommandReceipt?>
        FindSupplementalReceiptAsync(
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

    private void RequireSupplementalCoordinator(
        DynamicFlowInstance instance,
        string actorUserId)
    {
        if (!_runtimeActivation.CanExecuteP6CandidatePin(
                instance.CatalogVersion,
                instance.CatalogSemanticHash,
                instance.ArchetypeId) ||
            instance.ArchetypeId !=
                DynamicFlowSupplementalTopologyContract.ArchetypeId)
        {
            throw ForwardBlocked(instance.ArchetypeId);
        }
        if (instance.State != DynamicFlowInstanceStates.Active)
            throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_INSTANCE_NOT_ACTIVE");
        if (!string.Equals(
                instance.IssuerUserId,
                actorUserId,
                StringComparison.Ordinal))
        {
            throw RuntimeAccessForbidden();
        }
    }

    private static DynamicFlowSupplementalCommandResponse
        BuildSupplementalResult(
            string commandId,
            DynamicFlowInstance instance,
            long instanceRevision,
            DynamicFlowStepInstance step,
            string eventId,
            string status,
            bool businessWritePerformed,
            bool replayed)
        => new()
        {
            CommandId = commandId,
            Status = status,
            BusinessWritePerformed = businessWritePerformed,
            Replayed = replayed,
            FlowInstanceId = instance.Id,
            ExecutionEpoch = instance.ExecutionEpoch,
            InstanceRevision = instanceRevision,
            SupplementalStepId = step.SupplementalStepId ?? step.Id,
            StepInstanceId = step.Id,
            AssignmentId = step.AssignmentId,
            CompletionRequired = step.CompletionRequired,
            State = step.State,
            EventId = eventId
        };

    private static BsonDocument SupplementalResultDocument(
        DynamicFlowSupplementalCommandResponse response)
        => new()
        {
            { "commandId", response.CommandId },
            { "status", response.Status },
            { "flowInstanceId", response.FlowInstanceId },
            { "executionEpoch", response.ExecutionEpoch },
            { "instanceRevision", response.InstanceRevision },
            { "supplementalStepId", response.SupplementalStepId },
            { "stepInstanceId", response.StepInstanceId },
            { "assignmentId", BsonValue.Create(response.AssignmentId) },
            { "completionRequired", response.CompletionRequired },
            { "state", response.State },
            { "eventId", response.EventId }
        };

    private static DynamicFlowSupplementalCommandResponse
        ParseSupplementalReplay(
            DynamicFlowRuntimeCommandReceipt receipt,
            string requestHash)
    {
        if (receipt.Status != DynamicFlowRuntimeCommandStatuses.Succeeded ||
            receipt.ResultSnapshot is null ||
            !string.Equals(
                receipt.RequestHash,
                requestHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                receipt.ResultSnapshotHash,
                DynamicFlowSequentialTopologyContract.Hash(
                    receipt.ResultSnapshot.ToJson()),
                StringComparison.Ordinal))
        {
            throw SupplementalValidation(
                "DYNAMIC_FLOW_SUPPLEMENTAL_COMMAND_REPLAY_CONFLICT");
        }
        var result = receipt.ResultSnapshot;
        return new DynamicFlowSupplementalCommandResponse
        {
            CommandId = result["commandId"].AsString,
            Status = result["status"].AsString,
            BusinessWritePerformed = false,
            Replayed = true,
            FlowInstanceId = result["flowInstanceId"].AsString,
            ExecutionEpoch = result["executionEpoch"].AsInt32,
            InstanceRevision = result["instanceRevision"].ToInt64(),
            SupplementalStepId = result["supplementalStepId"].AsString,
            StepInstanceId = result["stepInstanceId"].AsString,
            AssignmentId =
                result.TryGetValue("assignmentId", out var assignmentId) &&
                assignmentId.IsString
                    ? assignmentId.AsString
                    : null,
            CompletionRequired = result["completionRequired"].AsBoolean,
            State = result["state"].AsString,
            EventId = result["eventId"].AsString
        };
    }

    private static AppException SupplementalValidation(string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                reason,
                action = "SUPPLEMENTAL",
                canExecute = false
            });

    private static AppException SupplementalRevisionConflict(
        long currentInstanceRevision,
        long? currentStepRevision)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new
            {
                reason = "DYNAMIC_FLOW_REVISION_CONFLICT",
                action = "SUPPLEMENTAL",
                currentInstanceRevision,
                currentStepRevision
            });

    private static AppException ForwardValidation(string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { reason, action = "FORWARD", canExecute = false });

    private static AppException P5ForwardGuard(string reason)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            new
            {
                reason,
                action = "FORWARD",
                blockedUntilPhase = "P6",
                canExecute = false
            });

    private static AppException SubflowValidation(string reason)
        => AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                reason,
                action = DynamicFlowSubflowTopologyContract.LaunchCommand,
                canExecute = false
            });

    private static AppException SubflowRevisionConflict(
        long currentInstanceRevision,
        long currentStepRevision)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new
            {
                reason = "DYNAMIC_FLOW_REVISION_CONFLICT",
                action =
                    DynamicFlowSubflowTopologyContract.LaunchCommand,
                currentInstanceRevision,
                currentStepRevision
            });

    private static AppException ForwardRevisionConflict(
        long currentInstanceRevision,
        long currentStepRevision)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new
            {
                reason = "DYNAMIC_FLOW_REVISION_CONFLICT",
                action = "FORWARD",
                currentInstanceRevision,
                currentStepRevision
            });

    private static AppException ForwardBlocked(string? archetypeId)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            new
            {
                reason = "DYNAMIC_FLOW_ARCHETYPE_BLOCKED_UNTIL_TARGET_PROMPT",
                action = "FORWARD",
                archetypeId,
                blockedUntilPrompt = ResolveBlockedPrompt(archetypeId),
                canExecute = false
            });

    private static string ResolveBlockedPrompt(string? archetypeId)
        => archetypeId switch
        {
            "FLOW-T04" => "P6-02",
            "FLOW-T05" => "P6-03",
            "FLOW-T06" => "P6-04",
            "FLOW-T07" => "P6-05",
            "FLOW-T08" => "P6-06",
            "FLOW-T09" => "P6-07",
            "FLOW-T10" => "P6-08",
            "FLOW-T11" => "P6-09",
            "FLOW-T12" => "P6-10",
            _ => "P6"
        };

    private static string StableTopologyObjectId(string seed)
        => DynamicFlowSequentialTopologyContract.Hash(seed)[..24];

    private static bool IsDuplicateKey(Exception error)
    {
        if (error is MongoWriteException write &&
            write.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            return true;
        if (error is MongoCommandException command && command.Code == 11000)
            return true;
        return error.InnerException is not null && IsDuplicateKey(error.InnerException);
    }

    private static BsonDocument TopologyParticipantDocument(
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

    private static string BuildTopologyOwnerIdentity(
        string instanceId,
        int executionEpoch,
        string nodeId,
        string ownerKind)
        => $"{ownerKind}:{instanceId}:{executionEpoch}:{nodeId}";

    private async Task<DynamicFlowBranchActionResponse> MutateBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest? req,
        string actorUserId,
        string action,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
        throw P6CommandBlocked(action);
#pragma warning disable CS0162 // Positive branch topology/invalidation belongs to P6.
        req ??= new DynamicFlowBranchActionRequest();

        workId = NormalizeRequiredObjectId(workId, "workId");
        assignmentId = NormalizeRequiredObjectId(assignmentId, "assignmentId");
        var requestedTargetAssignmentId = string.IsNullOrWhiteSpace(req.TargetAssignmentId)
            ? assignmentId
            : NormalizeRequiredObjectId(req.TargetAssignmentId, "targetAssignmentId");
        var reason = NormalizeOptionalText(req.Reason, 1000);
        var snapshotJson = NormalizeOptionalJson(req.SnapshotJson, "snapshotJson");
        var actor = await LoadActorAsync(actorUserId, ct);

        var anchor = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && x.WorkId == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                new { workId, assignmentId });

        if (!DynamicFlowBranchVisibility.IsFlowAssignment(anchor))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { assignmentId, reason = "DYNAMIC_FLOW_ASSIGNMENT_REQUIRED" });
        }

        await EnsureCanMutateFlowBranchAsync(anchor, actorUserId, ct);

        var assignments = await _ctx.WorkAssignments
            .Find(x =>
                x.WorkId == workId &&
                x.FlowInstanceId == anchor.FlowInstanceId &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var target = assignments.FirstOrDefault(x => x.Id == requestedTargetAssignmentId)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                new { workId, targetAssignmentId = requestedTargetAssignmentId, reason = "DYNAMIC_FLOW_TARGET_NOT_FOUND" });

        if (!DynamicFlowBranchMutationPlanner.IsAncestorOrSelf(assignments, target, anchor))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    assignmentId,
                    targetAssignmentId = target.Id,
                    reason = "DYNAMIC_FLOW_TARGET_MUST_BE_CURRENT_OR_ANCESTOR"
                });
        }

        await EnsureCanMutateFlowBranchAsync(target, actorUserId, ct);

        var eventId = ObjectId.GenerateNewId().ToString();
        var plan = DynamicFlowBranchMutationPlanner.Create(
            assignments,
            target,
            action,
            eventId,
            req.IncludeDescendants ?? true);
        var now = DateTime.UtcNow;

        await WriteFlowEventAsync(
            eventId,
            target,
            action,
            target.FlowEffectiveStatus,
            plan.TargetUpdate.NextStatus,
            actorUserId,
            actor?.UnitId,
            reason,
            snapshotJson,
            plan.AffectedAssignmentIds,
            now,
            ct);

        await ApplyBranchUpdateAsync(plan.TargetUpdate, actorUserId, now, ct);
        if (plan.DownstreamUpdates.Count > 0)
        {
            await ApplyBranchUpdatesAsync(plan.DownstreamUpdates, actorUserId, now, ct);
        }

        var dirtyReportCount = await MarkFlowImpactDirtyAsync(
            workId,
            plan.AffectedAssignmentIds,
            action,
            actorUserId,
            now,
            ct);

        foreach (var updatedAssignmentId in plan.AffectedAssignmentIds)
        {
            await _docRoleReadModelProjection.RebuildAssignmentAsync(updatedAssignmentId, actorUserId, ct);
        }

        return new DynamicFlowBranchActionResponse
        {
            EventId = eventId,
            Action = action,
            AssignmentId = target.Id,
            FlowEffectiveStatus = plan.TargetUpdate.NextStatus,
            FlowAttemptNo = plan.TargetUpdate.NextAttemptNo,
            AffectedAssignmentCount = plan.AffectedAssignmentIds.Count,
            DirtyReportCount = dirtyReportCount
        };
#pragma warning restore CS0162
    }

    private async Task WriteFlowEventAsync(
        string eventId,
        WorkAssignment assignment,
        string action,
        string? fromStatus,
        string? toStatus,
        string actorUserId,
        string? actorUnitId,
        string? reason,
        string? snapshotJson,
        IReadOnlyCollection<string>? affectedAssignmentIds,
        DateTime actionAtUtc,
        CancellationToken ct)
    {
        eventId = NormalizeRequiredObjectId(eventId, "flowEventId");
        var affectedIds = (affectedAssignmentIds ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (affectedIds.Count == 0 && !string.IsNullOrWhiteSpace(assignment.Id))
            affectedIds.Add(assignment.Id);

        await _ctx.DynamicFlowEvents.InsertOneAsync(new DynamicFlowEvent
        {
            Id = eventId,
            WorkId = assignment.WorkId,
            AssignmentId = assignment.Id,
            FlowTemplateId = assignment.FlowTemplateId,
            FlowTemplateVersionNo = assignment.FlowTemplateVersionNo,
            FlowInstanceId = assignment.FlowInstanceId!,
            FlowStepId = assignment.FlowStepId,
            FlowStepCode = assignment.FlowStepCode,
            FlowBranchId = assignment.FlowBranchId,
            ParentFlowBranchId = assignment.ParentFlowBranchId,
            Action = action,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ActorUserId = actorUserId,
            ActorUnitId = actorUnitId,
            VisibleUnitIds = DynamicFlowBranchVisibility.BuildVisibleUnitIds(assignment),
            Reason = reason,
            SnapshotJson = snapshotJson,
            SnapshotHash = ComputeHash(snapshotJson),
            AffectedAssignmentIds = affectedIds,
            ActionAtUtc = actionAtUtc,
            CreatedAtUtc = actionAtUtc,
            UpdatedAtUtc = actionAtUtc,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId
        }, cancellationToken: ct);
    }

    private async Task ApplyBranchUpdateAsync(
        DynamicFlowBranchMutationUpdate branchUpdate,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var update = Builders<WorkAssignment>.Update
            .Set(x => x.FlowEffectiveStatus, branchUpdate.NextStatus)
            .Set(x => x.FlowAttemptNo, branchUpdate.NextAttemptNo)
            .Set(x => x.InvalidatedByFlowEventId, branchUpdate.InvalidatedByFlowEventId)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        var result = await _ctx.WorkAssignments.UpdateOneAsync(
            x => x.Id == branchUpdate.AssignmentId && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (result.MatchedCount == 0)
        {
            throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                new { assignmentId = branchUpdate.AssignmentId });
        }

        await SyncStatisticProjectionFlowMetadataAsync(branchUpdate, actorUserId, now, ct);
    }

    private async Task SyncStatisticProjectionFlowMetadataAsync(
        DynamicFlowBranchMutationUpdate branchUpdate,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        await _ctx.WorkReportFieldStatValues.UpdateManyAsync(
            x => x.WorkAssignmentId == branchUpdate.AssignmentId &&
                 !x.IsDeleted &&
                 x.DirectProjection == null,
            Builders<WorkReportFieldStatValue>.Update
                .Set(x => x.FlowEffectiveStatus, branchUpdate.NextStatus)
                .Set(x => x.FlowAttemptNo, branchUpdate.NextAttemptNo)
                .Set(x => x.InvalidatedByFlowEventId, branchUpdate.InvalidatedByFlowEventId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        await _ctx.WorkReportTableStatValues.UpdateManyAsync(
            x => x.WorkAssignmentId == branchUpdate.AssignmentId &&
                 !x.IsDeleted &&
                 x.DirectProjection == null,
            Builders<WorkReportTableStatValue>.Update
                .Set(x => x.FlowEffectiveStatus, branchUpdate.NextStatus)
                .Set(x => x.FlowAttemptNo, branchUpdate.NextAttemptNo)
                .Set(x => x.InvalidatedByFlowEventId, branchUpdate.InvalidatedByFlowEventId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        await _ctx.WorkReportLabelStatValues.UpdateManyAsync(
            x => x.WorkAssignmentId == branchUpdate.AssignmentId &&
                 !x.IsDeleted &&
                 x.DirectProjection == null,
            Builders<WorkReportLabelStatValue>.Update
                .Set(x => x.FlowEffectiveStatus, branchUpdate.NextStatus)
                .Set(x => x.FlowAttemptNo, branchUpdate.NextAttemptNo)
                .Set(x => x.InvalidatedByFlowEventId, branchUpdate.InvalidatedByFlowEventId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private async Task ApplyBranchUpdatesAsync(
        IReadOnlyCollection<DynamicFlowBranchMutationUpdate> branchUpdates,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        foreach (var branchUpdate in branchUpdates)
        {
            await ApplyBranchUpdateAsync(branchUpdate, actorUserId, now, ct);
        }
    }

    private async Task<int> MarkFlowImpactDirtyAsync(
        string workId,
        IReadOnlyCollection<string> assignmentIds,
        string action,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        if (assignmentIds.Count == 0)
            return 0;

        var reportFilter = Builders<WorkAssignmentReport>.Filter.Eq(x => x.WorkId, workId)
                           & Builders<WorkAssignmentReport>.Filter.In(x => x.WorkAssignmentId, assignmentIds)
                           & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsCurrent, true)
                           & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsActive, true)
                           & Builders<WorkAssignmentReport>.Filter.Eq(x => x.IsDeleted, false);

        var reports = await _ctx.WorkAssignmentReports
            .Find(reportFilter)
            .ToListAsync(ct);
        var affectedAssignments = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && assignmentIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        var reportUpdateResult = await _ctx.WorkAssignmentReports.UpdateManyAsync(
            reportFilter,
            Builders<WorkAssignmentReport>.Update
                .Set(x => x.AggregateSnapshotDirty, true)
                .Set(x => x.AggregateSnapshotDirtyAtUtc, now)
                .Set(x => x.AggregateRefreshError, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        await MarkBasicSummarySnapshotsDirtyAsync(workId, assignmentIds, affectedAssignments, actorUserId, now, ct);

        foreach (var report in reports)
        {
            await _advancedSummaryDirty.MarkApprovedReportPayloadDirtyAsync(
                report,
                $"DYNAMIC_FLOW_{action}",
                actorUserId,
                ct);
        }

        return reportUpdateResult.ModifiedCount > int.MaxValue
            ? int.MaxValue
            : (int)reportUpdateResult.ModifiedCount;
    }

    private async Task MarkBasicSummarySnapshotsDirtyAsync(
        string workId,
        IReadOnlyCollection<string> assignmentIds,
        IReadOnlyCollection<WorkAssignment> affectedAssignments,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignmentBasicSummarySnapshot>.Filter;
        var sourceFilters = new List<FilterDefinition<WorkAssignmentBasicSummarySnapshot>>
        {
            fb.AnyIn(x => x.SourceAssignmentIds, assignmentIds)
        };
        var flowInstanceIds = affectedAssignments
            .Select(x => x.FlowInstanceId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (flowInstanceIds.Count > 0)
            sourceFilters.Add(fb.In(x => x.SourceFlowInstanceId, flowInstanceIds));

        var filter = fb.Eq(x => x.WorkId, workId)
                     & fb.Or(sourceFilters)
                     & fb.Eq(x => x.IsDeleted, false);

        await _ctx.WorkAssignmentBasicSummarySnapshots.UpdateManyAsync(
            filter,
            Builders<WorkAssignmentBasicSummarySnapshot>.Update
                .Set(x => x.SnapshotDirty, true)
                .Set(x => x.SnapshotDirtyAtUtc, now)
                .Set(x => x.RefreshError, (string?)null)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private async Task EnsureCanMutateFlowBranchAsync(
        WorkAssignment assignment,
        string actorUserId,
        CancellationToken ct)
    {
        var canRead = await WorkAssignmentReadAccessHelper.CanReadAssignmentAsync(
            _ctx,
            assignment.Id,
            actorUserId,
            ct);

        if (!canRead && !CanActorControlFlowBranch(assignment, actorUserId))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_FORBIDDEN,
                new { assignmentId = assignment.Id, actorUserId, reason = "DYNAMIC_FLOW_BRANCH_NOT_VISIBLE" });
        }

        if (!CanActorControlFlowBranch(assignment, actorUserId))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_FORBIDDEN,
                new { assignmentId = assignment.Id, actorUserId, reason = "DYNAMIC_FLOW_BRANCH_MUTATION_FORBIDDEN" });
        }
    }

    private static bool CanActorControlFlowBranch(
        WorkAssignment assignment,
        string actorUserId)
    {
        if (string.Equals(assignment.CreatedByUserId, actorUserId, StringComparison.Ordinal))
            return true;

        if ((assignment.LeaderWatcherUserIds ?? new List<string>()).Contains(actorUserId, StringComparer.Ordinal))
            return true;

        if ((assignment.Assignees ?? new List<UserRef>())
            .Any(x => string.Equals(x.UserId, actorUserId, StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private async Task<AppUser?> LoadActorAsync(string actorUserId, CancellationToken ct)
        => await _ctx.Users
            .Find(x => x.Id == actorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

    private async Task<WorkAssignment> ApplyFlowMetadataAsync(
        string assignmentId,
        DynamicFlowLaunchPlan plan,
        DynamicFlowLaunchBranch branch,
        string actorUserId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var update = Builders<WorkAssignment>.Update
            .Set(x => x.FlowTemplateId, plan.FlowTemplateId)
            .Set(x => x.FlowTemplateVersionNo, plan.FlowTemplateVersionNo)
            .Set(x => x.FlowInstanceId, plan.FlowInstanceId)
            .Set(x => x.FlowStepId, plan.Step.StepId)
            .Set(x => x.FlowStepCode, plan.Step.StepCode)
            .Set(x => x.FlowStepOrder, plan.Step.StepOrder)
            .Set(x => x.FlowBranchId, branch.FlowBranchId)
            .Set(x => x.ParentFlowBranchId, branch.ParentFlowBranchId)
            .Set(x => x.FlowAttemptNo, branch.FlowAttemptNo)
            .Set(x => x.FlowRole, branch.FlowRole)
            .Set(x => x.FlowEffectiveStatus, branch.FlowEffectiveStatus)
            .Set(x => x.IssuedByUnitId, plan.IssuedByUnitId)
            .Set(x => x.TargetUnitIds, new List<string> { branch.TargetUnitId })
            .Set(x => x.AllowSubFlow, branch.AllowSubFlow)
            .Set(x => x.IsFlowFinalNode, branch.IsFlowFinalNode)
            .Set(x => x.InvalidatedByFlowEventId, (string?)null)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, actorUserId);

        var result = await _ctx.WorkAssignments.UpdateOneAsync(
            x => x.Id == assignmentId && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (result.MatchedCount == 0)
            throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                new { assignmentId });

        return await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND,
                new { assignmentId });
    }

    private static SaveWorkAssignmentRequest ToAssignmentRequest(
        CreateDynamicFlowInstanceRequest req,
        string dynamicFormTemplateId,
        string targetUnitId)
        => new()
        {
            Name = req.Name,
            DynamicFormTemplateId = dynamicFormTemplateId,
            AssignmentType = NormalizeDefault(req.AssignmentType, WorkAssignmentTypes.Once),
            AggregationType = NormalizeDefault(req.AggregationType, WorkAggregationTypes.Matrix),
            Schedule = req.Schedule,
            StartDate = req.StartDate,
            DueDate = req.DueDate,
            DueAtUtc = req.DueAtUtc,
            AssigneeUserIds = new List<string>(),
            AssigneeUnitIds = new List<string> { targetUnitId },
            LeaderWatcherUserIds = req.LeaderWatcherUserIds ?? new List<string>(),
            DynamicFormDataSourceRulesJson = req.DynamicFormDataSourceRulesJson,
            AutoApproveConditionJson = req.AutoApproveConditionJson,
            Description = req.Description,
            ParentAssignmentId = string.IsNullOrWhiteSpace(req.ParentAssignmentId) ? null : req.ParentAssignmentId.Trim(),
            IsActive = req.IsActive
        };

    private static string NormalizeDefault(string? value, string fallback)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string NormalizeRequiredObjectId(string? value, string field)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_ARGUMENT_REQUIRED, new { field });

        if (!ObjectId.TryParse(value, out _))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field, reason = "OBJECT_ID_INVALID" });
        }

        return value;
    }

    private static string? NormalizeOptionalText(string? value, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string? NormalizeOptionalJson(string? value, string field)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            JsonNode.Parse(value);
            return value;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field, reason = "JSON_INVALID" });
        }
    }

    private static string? ComputeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static bool CanLaunchTemplate(
        DynamicFlowTemplate template,
        DynamicFlowTemplateVersion version,
        WorkAssignment? parent,
        AppUser actor)
        => false;

    private static AppException RuntimeAccessForbidden()
        => AppExceptionFactory.Forbidden(
            AppErrorCode.AUTH_FORBIDDEN,
            new { reason = "DYNAMIC_FLOW_DEFINITION_ACCESS_FORBIDDEN" });

    private static AppException P6CommandBlocked(string action)
        => AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            new
            {
                reason = "DYNAMIC_FLOW_COMMAND_BLOCKED_UNTIL_P6",
                action,
                blockedUntilPhase = "P6",
                canExecute = false
            });

    private static AppException ExecutionBlocked(DynamicFlowTemplateVersion version)
    {
        var blockedUntilPhase = ResolveBlockedUntilPhase(version);
        return AppExceptionFactory.Create(
            AppErrorCode.DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE,
            new
            {
                reason = "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                executionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
                executionBlockedReason = DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented,
                blockedUntilPhase,
                canExecute = false
            });
    }

    private static string ResolveBlockedUntilPhase(DynamicFlowTemplateVersion version)
    {
        if (string.Equals(version.BlockedUntilPhase, "P5", StringComparison.Ordinal) ||
            string.Equals(version.BlockedUntilPhase, "P6", StringComparison.Ordinal))
        {
            return version.BlockedUntilPhase;
        }

        try
        {
            var root = JsonNode.Parse(version.PayloadJson) as JsonObject;
            var archetypeId = root?["archetypeId"]?.GetValue<string>()?.Trim().ToUpperInvariant();
            return archetypeId is "FLOW-T01" or "FLOW-T02" ? "P5" : "P6";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A malformed or legacy snapshot is never made executable by a
            // failed classifier.  P6 is the conservative target boundary.
            return "P6";
        }
    }

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized();
    }
}

internal static class DynamicFlowRuntimePlanner
{
    public const string AssignmentFlowRole = "ASSIGNEE";
    public const string EffectiveStatus = DynamicFlowEffectiveStatuses.Effective;

    public static DynamicFlowLaunchPlan CreateLaunchPlan(
        DynamicFlowTemplate template,
        DynamicFlowTemplateVersion version,
        CreateDynamicFlowInstanceRequest? request,
        WorkAssignment? parent,
        string? actorUnitId)
    {
        request ??= new CreateDynamicFlowInstanceRequest();

        if (version.Status != DynamicFlowTemplateVersionStatuses.Locked)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    flowTemplateVersionId = version.Id,
                    status = version.Status,
                    reason = "DYNAMIC_FLOW_TEMPLATE_VERSION_LOCKED_REQUIRED"
                });
        }

        var rootDynamicFormTemplateId = FirstNonBlank(version.RootDynamicFormTemplateId, template.RootDynamicFormTemplateId)
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "rootDynamicFormTemplateId", reason = "DYNAMIC_FLOW_TEMPLATE_ROOT_FORM_REQUIRED" });

        var normalizedPayload = DynamicFlowTemplateService.NormalizePayloadJson(
            version.PayloadJson,
            requireLockable: true,
            rootDynamicFormTemplateId,
            dynamicFormTemplates: null);
        var root = JsonNode.Parse(normalizedPayload) as JsonObject
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "payloadJson", reason = "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_OBJECT_REQUIRED" });

        var selectedStep = SelectStep(root, request.StepId, request.StepCode);
        EnsureParentTransition(template, version, root, selectedStep, parent);
        var dynamicFormTemplateId = FirstNonBlank(selectedStep.DynamicFormTemplateId, rootDynamicFormTemplateId)
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "steps.dynamicFormTemplateId",
                    selectedStep.StepId,
                    selectedStep.StepCode,
                    reason = "DYNAMIC_FLOW_TEMPLATE_STEP_FORM_REQUIRED"
                });
        var targetUnitIds = NormalizeTargetUnitIds(request.TargetUnitIds);
        // A launched branch is assigned to its target unit. The caller cannot elevate
        // that server-owned relationship by supplying a role in the request payload.
        var flowRole = AssignmentFlowRole;
        var allowSubFlow = ResolveAllowSubFlow(root, selectedStep, flowRole);
        var isFinalNode = !HasOutgoingTransition(root, selectedStep);
        var flowInstanceId = string.IsNullOrWhiteSpace(parent?.FlowInstanceId)
            ? ObjectId.GenerateNewId().ToString()
            : parent!.FlowInstanceId!;
        var parentFlowBranchId = string.IsNullOrWhiteSpace(parent?.FlowBranchId)
            ? null
            : parent!.FlowBranchId;
        var attemptNo = parent?.FlowAttemptNo ?? 1;

        return new DynamicFlowLaunchPlan
        {
            FlowTemplateId = version.TemplateId,
            FlowTemplateVersionNo = version.VersionNo,
            DynamicFormTemplateId = dynamicFormTemplateId,
            FlowInstanceId = flowInstanceId,
            IssuedByUnitId = string.IsNullOrWhiteSpace(actorUnitId) ? null : actorUnitId.Trim(),
            Step = selectedStep,
            Branches = targetUnitIds
                .Select(targetUnitId => new DynamicFlowLaunchBranch
                {
                    TargetUnitId = targetUnitId,
                    FlowBranchId = ObjectId.GenerateNewId().ToString(),
                    ParentFlowBranchId = parentFlowBranchId,
                    FlowAttemptNo = attemptNo,
                    FlowRole = flowRole,
                    FlowEffectiveStatus = EffectiveStatus,
                    AllowSubFlow = allowSubFlow,
                    IsFlowFinalNode = isFinalNode
                })
                .ToList()
        };
    }

    private static DynamicFlowRuntimeStepSelection SelectStep(
        JsonObject root,
        string? requestedStepId,
        string? requestedStepCode)
    {
        requestedStepId = requestedStepId?.Trim();
        requestedStepCode = requestedStepCode?.Trim();

        if (root["steps"] is not JsonArray steps || steps.Count == 0)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "steps", reason = "DYNAMIC_FLOW_TEMPLATE_STEPS_REQUIRED" });

        if (string.IsNullOrWhiteSpace(requestedStepId) && string.IsNullOrWhiteSpace(requestedStepCode))
            requestedStepId = ResolveEntryStepId(root, steps);

        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index] is not JsonObject step)
                continue;

            var stepId = ReadString(step, "stepId", "id") ?? string.Empty;
            var stepCode = ReadString(step, "stepCode", "code") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(requestedStepId) &&
                !string.Equals(requestedStepId, stepId, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrWhiteSpace(requestedStepCode) &&
                !string.Equals(requestedStepCode, stepCode, StringComparison.OrdinalIgnoreCase))
                continue;

            return new DynamicFlowRuntimeStepSelection
            {
                StepId = stepId,
                StepCode = stepCode,
                StepOrder = ReadInt(step, "stepOrder", "order") ?? index + 1,
                DynamicFormTemplateId = ReadString(step, "dynamicFormTemplateId", "formTemplateId")
            };
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                stepId = requestedStepId,
                stepCode = requestedStepCode,
                reason = "DYNAMIC_FLOW_STEP_NOT_FOUND"
            });
    }

    private static string ResolveEntryStepId(JsonObject root, JsonArray steps)
    {
        var stepIds = steps
            .OfType<JsonObject>()
            .Select(step => ReadString(step, "stepId", "id"))
            .Where(stepId => !string.IsNullOrWhiteSpace(stepId))
            .Select(stepId => stepId!)
            .ToList();
        if (stepIds.Count == 1)
            return stepIds[0];

        var incoming = new HashSet<string>(StringComparer.Ordinal);
        if (root["transitions"] is JsonArray transitions)
        {
            var idByCode = steps
                .OfType<JsonObject>()
                .Select(step => new
                {
                    Id = ReadString(step, "stepId", "id"),
                    Code = ReadString(step, "stepCode", "code")
                })
                .Where(step => !string.IsNullOrWhiteSpace(step.Id) && !string.IsNullOrWhiteSpace(step.Code))
                .ToDictionary(step => step.Code!, step => step.Id!, StringComparer.OrdinalIgnoreCase);
            foreach (var transition in transitions.OfType<JsonObject>())
            {
                var toStepId = ReadString(transition, "toStepId", "targetStepId");
                if (!string.IsNullOrWhiteSpace(toStepId))
                {
                    incoming.Add(toStepId);
                    continue;
                }

                var toStepCode = ReadString(transition, "toStepCode", "targetStepCode");
                if (!string.IsNullOrWhiteSpace(toStepCode) && idByCode.TryGetValue(toStepCode, out var resolved))
                    incoming.Add(resolved);
            }
        }

        var roots = stepIds.Where(stepId => !incoming.Contains(stepId)).ToList();
        if (roots.Count == 1)
            return roots[0];

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { field = "transitions", rootCount = roots.Count, reason = "DYNAMIC_FLOW_TEMPLATE_SINGLE_ROOT_REQUIRED" });
    }

    private static void EnsureParentTransition(
        DynamicFlowTemplate template,
        DynamicFlowTemplateVersion version,
        JsonObject root,
        DynamicFlowRuntimeStepSelection selectedStep,
        WorkAssignment? parent)
    {
        if (parent is null)
        {
            var entryStepId = ResolveEntryStepId(root, (JsonArray)root["steps"]!);
            if (!string.Equals(entryStepId, selectedStep.StepId, StringComparison.Ordinal))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { selectedStep.StepId, entryStepId, reason = "DYNAMIC_FLOW_INITIAL_STEP_MUST_BE_ROOT" });
            }

            return;
        }

        if (!string.Equals(parent.FlowTemplateId, template.Id, StringComparison.Ordinal) ||
            parent.FlowTemplateVersionNo != version.VersionNo ||
            string.IsNullOrWhiteSpace(parent.FlowInstanceId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { parentAssignmentId = parent.Id, reason = "DYNAMIC_FLOW_PARENT_CONTEXT_MISMATCH" });
        }

        if (parent.AllowSubFlow != true)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { parentAssignmentId = parent.Id, reason = "DYNAMIC_FLOW_PARENT_SUBFLOW_NOT_ALLOWED" });
        }

        if (!string.Equals(
                parent.FlowEffectiveStatus,
                DynamicFlowEffectiveStatuses.Effective,
                StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    parentAssignmentId = parent.Id,
                    parent.FlowEffectiveStatus,
                    reason = "DYNAMIC_FLOW_PARENT_NOT_EFFECTIVE"
                });
        }

        if (!HasTransition(root, parent.FlowStepId, parent.FlowStepCode, selectedStep))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    parentAssignmentId = parent.Id,
                    parent.FlowStepId,
                    parent.FlowStepCode,
                    selectedStep.StepId,
                    selectedStep.StepCode,
                    reason = "DYNAMIC_FLOW_PARENT_TRANSITION_INVALID"
                });
        }
    }

    private static bool HasTransition(
        JsonObject root,
        string? fromStepId,
        string? fromStepCode,
        DynamicFlowRuntimeStepSelection targetStep)
    {
        if (root["transitions"] is not JsonArray transitions)
            return false;

        foreach (var transition in transitions.OfType<JsonObject>())
        {
            var transitionFromId = ReadString(transition, "fromStepId", "sourceStepId");
            var transitionFromCode = ReadString(transition, "fromStepCode", "sourceStepCode");
            var transitionToId = ReadString(transition, "toStepId", "targetStepId");
            var transitionToCode = ReadString(transition, "toStepCode", "targetStepCode");
            var fromMatches = (!string.IsNullOrWhiteSpace(transitionFromId) &&
                               string.Equals(transitionFromId, fromStepId, StringComparison.Ordinal)) ||
                              (!string.IsNullOrWhiteSpace(transitionFromCode) &&
                               string.Equals(transitionFromCode, fromStepCode, StringComparison.OrdinalIgnoreCase));
            var toMatches = (!string.IsNullOrWhiteSpace(transitionToId) &&
                             string.Equals(transitionToId, targetStep.StepId, StringComparison.Ordinal)) ||
                            (!string.IsNullOrWhiteSpace(transitionToCode) &&
                             string.Equals(transitionToCode, targetStep.StepCode, StringComparison.OrdinalIgnoreCase));
            if (fromMatches && toMatches)
                return true;
        }

        return false;
    }

    private static List<string> NormalizeTargetUnitIds(IEnumerable<string>? values)
    {
        var result = (values ?? Enumerable.Empty<string>())
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (result.Count == 0)
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_ARGUMENT_REQUIRED,
                new { field = "targetUnitIds" });

        foreach (var value in result)
        {
            if (!ObjectId.TryParse(value, out _))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = "targetUnitIds", value, reason = "OBJECT_ID_INVALID" });
            }
        }

        return result;
    }

    private static bool ResolveAllowSubFlow(
        JsonObject root,
        DynamicFlowRuntimeStepSelection step,
        string actorRole)
    {
        if (root["actorPolicies"] is not JsonArray policies)
            return false;

        bool? resolved = null;
        foreach (var item in policies
                     .OfType<JsonObject>()
                     .Select((policy, index) => new { policy, index })
                     .OrderBy(x => PolicySpecificity(x.policy))
                     .ThenBy(x => x.index)
                     .Select(x => x.policy))
        {
            var policy = item;
            if (!PolicyMatches(policy, step, actorRole))
                continue;

            var allowSubFlow = ReadBool(policy, "allowSubFlow");
            if (allowSubFlow.HasValue)
                resolved = allowSubFlow.Value;
        }

        return resolved ?? false;
    }

    private static int PolicySpecificity(JsonObject policy)
        => new[] { "stepId", "stepCode", "actorRole" }
            .Count(property =>
            {
                var value = ReadString(policy, property);
                return !string.IsNullOrWhiteSpace(value) &&
                       !string.Equals(value, "*", StringComparison.Ordinal);
            });

    private static bool HasOutgoingTransition(JsonObject root, DynamicFlowRuntimeStepSelection step)
    {
        if (root["transitions"] is not JsonArray transitions)
            return false;

        foreach (var item in transitions)
        {
            if (item is not JsonObject transition)
                continue;

            var fromStepId = ReadString(transition, "fromStepId", "sourceStepId");
            if (!string.IsNullOrWhiteSpace(fromStepId) &&
                string.Equals(fromStepId, step.StepId, StringComparison.Ordinal))
                return true;

            var fromStepCode = ReadString(transition, "fromStepCode", "sourceStepCode");
            if (!string.IsNullOrWhiteSpace(fromStepCode) &&
                string.Equals(fromStepCode, step.StepCode, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool PolicyMatches(JsonObject policy, DynamicFlowRuntimeStepSelection step, string actorRole)
        => ScopeMatches(ReadString(policy, "stepId"), step.StepId, StringComparison.Ordinal)
           && ScopeMatches(ReadString(policy, "stepCode"), step.StepCode, StringComparison.OrdinalIgnoreCase)
           && ScopeMatches(ReadString(policy, "actorRole"), actorRole, StringComparison.OrdinalIgnoreCase);

    private static bool ScopeMatches(string? policyValue, string contextValue, StringComparison comparison)
        => string.Equals(policyValue, "*", StringComparison.Ordinal) ||
           (!string.IsNullOrWhiteSpace(policyValue) &&
            string.Equals(policyValue, contextValue, comparison));

    private static string? ReadString(JsonObject root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetPropertyValue(propertyName, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonObject root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetPropertyValue(propertyName, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonObject root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetPropertyValue(propertyName, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }
        }

        return null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
}

internal static class DynamicFlowBranchMutationPlanner
{
    public static DynamicFlowBranchMutationPlan Create(
        IReadOnlyCollection<WorkAssignment> assignments,
        WorkAssignment target,
        string action,
        string eventId,
        bool includeDescendants)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.Id))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { reason = "DYNAMIC_FLOW_TARGET_REQUIRED" });
        }

        if (string.IsNullOrWhiteSpace(target.FlowInstanceId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { assignmentId = target.Id, reason = "DYNAMIC_FLOW_ASSIGNMENT_REQUIRED" });
        }

        eventId = NormalizeRequired(eventId, "flowEventId");
        action = NormalizeRequired(action, "action");

        var descendants = includeDescendants
            ? ResolveDescendants(assignments, target)
            : new List<WorkAssignment>();

        var targetNextStatus = action switch
        {
            DynamicFlowEventActions.Rollback => DynamicFlowEffectiveStatuses.Effective,
            DynamicFlowEventActions.Restarted => DynamicFlowEffectiveStatuses.Effective,
            DynamicFlowEventActions.Terminated => DynamicFlowEffectiveStatuses.Terminated,
            _ => throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { action, reason = "DYNAMIC_FLOW_ACTION_UNSUPPORTED" })
        };

        var downstreamNextStatus = action == DynamicFlowEventActions.Terminated
            ? DynamicFlowEffectiveStatuses.Terminated
            : DynamicFlowEffectiveStatuses.Invalidated;
        var shouldBumpAttempt = action == DynamicFlowEventActions.Rollback ||
                                action == DynamicFlowEventActions.Restarted;
        var targetAttemptNo = Math.Max(1, target.FlowAttemptNo ?? 1) + (shouldBumpAttempt ? 1 : 0);
        var targetInvalidatedByEventId = action == DynamicFlowEventActions.Terminated ? eventId : null;

        var targetUpdate = new DynamicFlowBranchMutationUpdate
        {
            AssignmentId = target.Id,
            NextStatus = targetNextStatus,
            NextAttemptNo = targetAttemptNo,
            InvalidatedByFlowEventId = targetInvalidatedByEventId
        };

        var downstreamUpdates = descendants
            .Select(x => new DynamicFlowBranchMutationUpdate
            {
                AssignmentId = x.Id,
                NextStatus = downstreamNextStatus,
                NextAttemptNo = x.FlowAttemptNo,
                InvalidatedByFlowEventId = eventId
            })
            .ToList();

        var affectedAssignmentIds = new List<string> { target.Id };
        affectedAssignmentIds.AddRange(downstreamUpdates.Select(x => x.AssignmentId));

        return new DynamicFlowBranchMutationPlan
        {
            Action = action,
            TargetUpdate = targetUpdate,
            DownstreamUpdates = downstreamUpdates,
            AffectedAssignmentIds = affectedAssignmentIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };
    }

    public static bool IsAncestorOrSelf(
        IReadOnlyCollection<WorkAssignment> assignments,
        WorkAssignment possibleAncestor,
        WorkAssignment assignment)
    {
        if (string.Equals(possibleAncestor.Id, assignment.Id, StringComparison.Ordinal))
            return true;

        return ResolveDescendants(assignments, possibleAncestor)
            .Any(x => string.Equals(x.Id, assignment.Id, StringComparison.Ordinal));
    }

    private static List<WorkAssignment> ResolveDescendants(
        IReadOnlyCollection<WorkAssignment> assignments,
        WorkAssignment target)
    {
        var sameFlowAssignments = assignments
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Id) &&
                string.Equals(x.FlowInstanceId, target.FlowInstanceId, StringComparison.Ordinal) &&
                !string.Equals(x.Id, target.Id, StringComparison.Ordinal))
            .ToList();
        var targetPath = target.Path?.Trim();
        var targetId = target.Id;
        var descendants = new List<WorkAssignment>();

        foreach (var candidate in sameFlowAssignments)
        {
            if (IsPathDescendant(candidate, targetPath) ||
                HasAncestorByParentChain(sameFlowAssignments, candidate, targetId))
            {
                descendants.Add(candidate);
            }
        }

        return descendants
            .OrderBy(x => x.Path)
            .ThenBy(x => x.Id)
            .ToList();
    }

    private static bool IsPathDescendant(WorkAssignment candidate, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(candidate.Path))
            return false;

        return candidate.Path.StartsWith($"{targetPath}/", StringComparison.Ordinal);
    }

    private static bool HasAncestorByParentChain(
        IReadOnlyCollection<WorkAssignment> assignments,
        WorkAssignment candidate,
        string targetId)
    {
        var byId = assignments
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parentId = candidate.ParentAssignmentId;

        while (!string.IsNullOrWhiteSpace(parentId) && seen.Add(parentId))
        {
            if (string.Equals(parentId, targetId, StringComparison.Ordinal))
                return true;

            parentId = byId.TryGetValue(parentId, out var parent)
                ? parent.ParentAssignmentId
                : null;
        }

        return false;
    }

    private static string NormalizeRequired(string? value, string field)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_ARGUMENT_REQUIRED,
                new { field });
        }

        return value;
    }
}

internal sealed class DynamicFlowBranchMutationPlan
{
    public string Action { get; set; } = string.Empty;
    public DynamicFlowBranchMutationUpdate TargetUpdate { get; set; } = new();
    public List<DynamicFlowBranchMutationUpdate> DownstreamUpdates { get; set; } = new();
    public List<string> AffectedAssignmentIds { get; set; } = new();
}

internal sealed class DynamicFlowBranchMutationUpdate
{
    public string AssignmentId { get; set; } = string.Empty;
    public string NextStatus { get; set; } = string.Empty;
    public int? NextAttemptNo { get; set; }
    public string? InvalidatedByFlowEventId { get; set; }
}

internal sealed class DynamicFlowLaunchPlan
{
    public string FlowTemplateId { get; set; } = string.Empty;
    public int FlowTemplateVersionNo { get; set; }
    public string DynamicFormTemplateId { get; set; } = string.Empty;
    public string FlowInstanceId { get; set; } = string.Empty;
    public string? IssuedByUnitId { get; set; }
    public DynamicFlowRuntimeStepSelection Step { get; set; } = new();
    public List<DynamicFlowLaunchBranch> Branches { get; set; } = new();
}

internal sealed class DynamicFlowRuntimeStepSelection
{
    public string StepId { get; set; } = string.Empty;
    public string StepCode { get; set; } = string.Empty;
    public int StepOrder { get; set; }
    public string? DynamicFormTemplateId { get; set; }
}

internal sealed class DynamicFlowLaunchBranch
{
    public string TargetUnitId { get; set; } = string.Empty;
    public string FlowBranchId { get; set; } = string.Empty;
    public string? ParentFlowBranchId { get; set; }
    public int FlowAttemptNo { get; set; }
    public string FlowRole { get; set; } = string.Empty;
    public string FlowEffectiveStatus { get; set; } = string.Empty;
    public bool AllowSubFlow { get; set; }
    public bool IsFlowFinalNode { get; set; }
}
