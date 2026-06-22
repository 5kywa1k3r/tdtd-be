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
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments;

namespace tdtd_be.Services.DynamicFlows;

public sealed class DynamicFlowRuntimeService : IDynamicFlowRuntimeService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentService _assignments;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkAssignmentAdvancedSummaryDirtyService _advancedSummaryDirty;

    public DynamicFlowRuntimeService(
        MongoDbContext ctx,
        IWorkAssignmentService assignments,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkAssignmentAdvancedSummaryDirtyService advancedSummaryDirty)
    {
        _ctx = ctx;
        _assignments = assignments;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _advancedSummaryDirty = advancedSummaryDirty;
    }

    public async Task<DynamicFlowInstanceLaunchResponse> CreateInstanceAsync(
        string workId,
        CreateDynamicFlowInstanceRequest req,
        string actorUserId,
        CancellationToken ct = default)
    {
        EnsureActor(actorUserId);
        req ??= new CreateDynamicFlowInstanceRequest();

        var versionId = NormalizeRequiredObjectId(req.FlowTemplateVersionId, "flowTemplateVersionId");
        var version = await _ctx.DynamicFlowTemplateVersions
            .Find(x => x.Id == versionId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.COMMON_NOT_FOUND,
                new { flowTemplateVersionId = versionId, reason = "DYNAMIC_FLOW_TEMPLATE_VERSION_NOT_FOUND" });

        var template = await _ctx.DynamicFlowTemplates
            .Find(x => x.Id == version.TemplateId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(
                AppErrorCode.COMMON_NOT_FOUND,
                new { dynamicFlowTemplateId = version.TemplateId, reason = "DYNAMIC_FLOW_TEMPLATE_NOT_FOUND" });

        WorkAssignment? parent = null;
        if (!string.IsNullOrWhiteSpace(req.ParentAssignmentId))
        {
            var parentAssignmentId = NormalizeRequiredObjectId(req.ParentAssignmentId, "parentAssignmentId");
            parent = await _ctx.WorkAssignments
                .Find(x => x.Id == parentAssignmentId && x.WorkId == workId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw AppExceptionFactory.NotFound(
                    AppErrorCode.WORK_ASSIGNMENT_PARENT_NOT_FOUND,
                    new { workId, parentAssignmentId });
        }

        var actor = await _ctx.Users
            .Find(x => x.Id == actorUserId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var plan = DynamicFlowRuntimePlanner.CreateLaunchPlan(
            template,
            version,
            req,
            parent,
            actor?.UnitId);

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
                    actor?.UnitId,
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
                actor?.UnitId,
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

    private async Task<DynamicFlowBranchActionResponse> MutateBranchAsync(
        string workId,
        string assignmentId,
        DynamicFlowBranchActionRequest? req,
        string actorUserId,
        string action,
        CancellationToken ct)
    {
        EnsureActor(actorUserId);
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

        var normalizedPayload = DynamicFlowTemplateService.NormalizePayloadJson(
            version.PayloadJson,
            requireLockable: true);
        var root = JsonNode.Parse(normalizedPayload) as JsonObject
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "payloadJson", reason = "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_OBJECT_REQUIRED" });

        var dynamicFormTemplateId = FirstNonBlank(version.DynamicFormTemplateId, template.DynamicFormTemplateId)
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "dynamicFormTemplateId", reason = "DYNAMIC_FLOW_TEMPLATE_DYNAMIC_FORM_REQUIRED" });

        var selectedStep = SelectStep(root, request.StepId, request.StepCode);
        var targetUnitIds = NormalizeTargetUnitIds(request.TargetUnitIds);
        var allowSubFlow = ResolveAllowSubFlow(root, selectedStep);
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
                    FlowRole = AssignmentFlowRole,
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
                StepOrder = ReadInt(step, "stepOrder", "order") ?? index + 1
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

    private static bool ResolveAllowSubFlow(JsonObject root, DynamicFlowRuntimeStepSelection step)
    {
        if (root["actorPolicies"] is not JsonArray policies)
            return false;

        foreach (var item in policies)
        {
            if (item is not JsonObject policy || !PolicyMatches(policy, step, AssignmentFlowRole))
                continue;

            var allowSubFlow = ReadBool(policy, "allowSubFlow");
            if (allowSubFlow.HasValue)
                return allowSubFlow.Value;
        }

        return false;
    }

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
        => string.IsNullOrWhiteSpace(policyValue) ||
           string.Equals(policyValue, "*", StringComparison.Ordinal) ||
           string.Equals(policyValue, contextValue, comparison);

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
