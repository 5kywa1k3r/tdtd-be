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
using tdtd_be.Services.WorkAssignments;

namespace tdtd_be.Services.DynamicFlows;

public sealed class DynamicFlowRuntimeService : IDynamicFlowRuntimeService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentService _assignments;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;

    public DynamicFlowRuntimeService(
        MongoDbContext ctx,
        IWorkAssignmentService assignments,
        IDocRoleReadModelProjectionService docRoleReadModelProjection)
    {
        _ctx = ctx;
        _assignments = assignments;
        _docRoleReadModelProjection = docRoleReadModelProjection;
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

    private static void EnsureActor(string actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            throw AppExceptionFactory.Unauthorized();
    }
}

internal static class DynamicFlowRuntimePlanner
{
    public const string AssignmentFlowRole = "ASSIGNEE";
    public const string EffectiveStatus = "EFFECTIVE";

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
