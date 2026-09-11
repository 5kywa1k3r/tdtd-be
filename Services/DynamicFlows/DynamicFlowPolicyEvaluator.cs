using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public interface IDynamicFlowPolicyEvaluator
{
    DynamicFlowPolicyEvaluationResult Evaluate(
        string payloadJson,
        DynamicFlowPolicyEvaluationContext context);
}

public sealed class DynamicFlowPolicyEvaluator : IDynamicFlowPolicyEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public DynamicFlowPolicyEvaluationResult Evaluate(
        string payloadJson,
        DynamicFlowPolicyEvaluationContext context)
    {
        var normalizedPayload = DynamicFlowTemplateService.NormalizePayloadJson(payloadJson, requireLockable: false);
        var root = JsonNode.Parse(normalizedPayload) as JsonObject
            ?? throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "payloadJson", reason = "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_OBJECT_REQUIRED" });

        context ??= new DynamicFlowPolicyEvaluationContext();
        var result = new DynamicFlowPolicyEvaluationResult();

        var matchingFieldPolicies = ReadPolicyArray(root, "fieldPolicies")
            .Where(policy => PolicyMatches(policy, context))
            .ToList();
        EnsureNoEqualSpecificityConflicts(matchingFieldPolicies, "fieldPolicies", FieldPolicyTargetKey);
        foreach (var policy in matchingFieldPolicies
                     .Select((item, index) => new { item, index })
                     .OrderBy(x => PolicySpecificity(x.item))
                     .ThenBy(x => x.index)
                     .Select(x => x.item))
        {
            var fieldId = ReadString(policy, "fieldId");
            var fieldKey = ReadString(policy, "fieldKey");
            var targetKey = SelectPolicyTargetKey(fieldId, fieldKey);
            if (targetKey is null)
                continue;

            if (!result.Fields.TryGetValue(targetKey, out var permission))
            {
                permission = new DynamicFlowFieldPermissionDto
                {
                    TargetKey = targetKey,
                    FieldId = fieldId,
                    FieldKey = fieldKey
                };
                result.Fields[targetKey] = permission;
            }

            ApplyPermissionPolicy(permission, policy, context.IsAfterSubmit);
        }

        var matchingTablePolicies = ReadPolicyArray(root, "tableColumnPolicies")
            .Where(policy => PolicyMatches(policy, context))
            .ToList();
        EnsureNoEqualSpecificityConflicts(matchingTablePolicies, "tableColumnPolicies", TablePolicyTargetKey);
        foreach (var policy in matchingTablePolicies
                     .Select((item, index) => new { item, index })
                     .OrderBy(x => PolicySpecificity(x.item))
                     .ThenBy(x => x.index)
                     .Select(x => x.item))
        {
            var blockId = ReadString(policy, "blockId");
            var columnKey = ReadString(policy, "columnKey");
            if (blockId is null || columnKey is null)
                continue;

            var targetKey = $"{blockId}:{columnKey}";
            if (!result.TableColumns.TryGetValue(targetKey, out var permission))
            {
                permission = new DynamicFlowTableColumnPermissionDto
                {
                    TargetKey = targetKey,
                    BlockId = blockId,
                    ColumnKey = columnKey
                };
                result.TableColumns[targetKey] = permission;
            }

            ApplyPermissionPolicy(permission, policy, context.IsAfterSubmit);
        }

        // A missing policy for the resolved step/role is not an implicit grant.
        // Coverage validation can make this more granular at lock time; runtime stays safe meanwhile.
        result.DenyAllFields = result.Fields.Count == 0;
        result.DenyAllTableColumns = result.TableColumns.Count == 0;

        return result;
    }

    private static IEnumerable<JsonObject> ReadPolicyArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonArray array)
            yield break;

        foreach (var item in array)
        {
            if (item is JsonObject policy)
                yield return policy;
        }
    }

    private static bool PolicyMatches(JsonObject policy, DynamicFlowPolicyEvaluationContext context)
        => ScopeMatches(ReadString(policy, "stepId"), context.StepId)
           && ScopeMatches(ReadString(policy, "stepCode"), context.StepCode)
           && ScopeMatches(ReadString(policy, "actorRole"), context.ActorRole);

    private static int PolicySpecificity(JsonObject policy)
        => new[] { "stepId", "stepCode", "actorRole" }
            .Count(property =>
            {
                var value = ReadString(policy, property);
                return !string.IsNullOrWhiteSpace(value) &&
                       !string.Equals(value, "*", StringComparison.Ordinal);
            });

    private static string? FieldPolicyTargetKey(JsonObject policy)
        => SelectPolicyTargetKey(
            ReadString(policy, "fieldId"),
            ReadString(policy, "fieldKey"));

    private static string? TablePolicyTargetKey(JsonObject policy)
    {
        var blockId = ReadString(policy, "blockId");
        var columnKey = ReadString(policy, "columnKey");
        return blockId is null || columnKey is null ? null : $"{blockId}:{columnKey}";
    }

    private static void EnsureNoEqualSpecificityConflicts(
        IReadOnlyList<JsonObject> policies,
        string path,
        Func<JsonObject, string?> targetKeySelector)
    {
        var indexed = policies.Select((policy, index) => new
        {
            Policy = policy,
            Index = index,
            TargetKey = targetKeySelector(policy),
            Specificity = PolicySpecificity(policy)
        });

        foreach (var group in indexed
                     .Where(x => !string.IsNullOrWhiteSpace(x.TargetKey))
                     .GroupBy(x => $"{x.TargetKey}\n{x.Specificity}", StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToList();
            for (var leftIndex = 0; leftIndex < rows.Count; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < rows.Count; rightIndex++)
                {
                    var conflictProperty = FindConflictingPermissionProperty(
                        rows[leftIndex].Policy,
                        rows[rightIndex].Policy);
                    if (conflictProperty is null)
                        continue;

                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new
                        {
                            path,
                            targetKey = rows[leftIndex].TargetKey,
                            specificity = rows[leftIndex].Specificity,
                            property = conflictProperty,
                            policyIds = new[]
                            {
                                ReadString(rows[leftIndex].Policy, "policyId", "id"),
                                ReadString(rows[rightIndex].Policy, "policyId", "id")
                            },
                            reason = "DYNAMIC_FLOW_POLICY_SPECIFICITY_CONFLICT",
                            tab = "policy"
                        });
                }
            }
        }
    }

    private static string? FindConflictingPermissionProperty(JsonObject left, JsonObject right)
    {
        var aliases = new[]
        {
            new[] { "read", "canRead" },
            new[] { "write", "canWrite" },
            new[] { "required", "isRequired" },
            new[] { "hidden", "isHidden" },
            new[] { "locked", "isLocked" },
            new[] { "lockedAfterSubmit" }
        };
        foreach (var names in aliases)
        {
            var leftValue = ReadBool(left, names);
            var rightValue = ReadBool(right, names);
            if (leftValue.HasValue && rightValue.HasValue && leftValue.Value != rightValue.Value)
                return names[0];
        }

        return null;
    }

    private static bool ScopeMatches(string? policyValue, string? contextValue)
        => string.Equals(policyValue, "*", StringComparison.Ordinal) ||
           (!string.IsNullOrWhiteSpace(contextValue) &&
            !string.IsNullOrWhiteSpace(policyValue) &&
            string.Equals(policyValue, contextValue, StringComparison.OrdinalIgnoreCase));

    private static void ApplyPermissionPolicy(DynamicFlowFieldPermissionDto target, JsonObject policy, bool isAfterSubmit)
    {
        ApplyCommonPolicy(
            policy,
            isAfterSubmit,
            read => target.Read = read,
            write => target.Write = write,
            required => target.Required = required,
            hidden => target.Hidden = hidden,
            locked => target.Locked = locked,
            lockedAfterSubmit => target.LockedAfterSubmit = lockedAfterSubmit,
            sourcePolicyId => target.SourcePolicyId = sourcePolicyId,
            () => target.Hidden,
            () => target.Locked,
            read => target.Read = read,
            write => target.Write = write,
            required => target.Required = required);
    }

    private static void ApplyPermissionPolicy(DynamicFlowTableColumnPermissionDto target, JsonObject policy, bool isAfterSubmit)
    {
        ApplyCommonPolicy(
            policy,
            isAfterSubmit,
            read => target.Read = read,
            write => target.Write = write,
            required => target.Required = required,
            hidden => target.Hidden = hidden,
            locked => target.Locked = locked,
            lockedAfterSubmit => target.LockedAfterSubmit = lockedAfterSubmit,
            sourcePolicyId => target.SourcePolicyId = sourcePolicyId,
            () => target.Hidden,
            () => target.Locked,
            read => target.Read = read,
            write => target.Write = write,
            required => target.Required = required);
    }

    private static void ApplyCommonPolicy(
        JsonObject policy,
        bool isAfterSubmit,
        Action<bool> setRead,
        Action<bool> setWrite,
        Action<bool> setRequired,
        Action<bool> setHidden,
        Action<bool> setLocked,
        Action<bool> setLockedAfterSubmit,
        Action<string?> setSourcePolicyId,
        Func<bool> getHidden,
        Func<bool> getLocked,
        Action<bool> forceRead,
        Action<bool> forceWrite,
        Action<bool> forceRequired)
    {
        var read = ReadBool(policy, "read", "canRead");
        if (read.HasValue)
            setRead(read.Value);

        var write = ReadBool(policy, "write", "canWrite");
        if (write.HasValue)
            setWrite(write.Value);

        var required = ReadBool(policy, "required", "isRequired");
        if (required.HasValue)
            setRequired(required.Value);

        var hidden = ReadBool(policy, "hidden", "isHidden");
        if (hidden.HasValue)
            setHidden(hidden.Value);

        var locked = ReadBool(policy, "locked", "isLocked");
        if (locked.HasValue)
            setLocked(locked.Value);

        var lockedAfterSubmit = ReadBool(policy, "lockedAfterSubmit");
        if (lockedAfterSubmit.HasValue)
        {
            setLockedAfterSubmit(lockedAfterSubmit.Value);
            if (lockedAfterSubmit.Value && isAfterSubmit)
                setLocked(true);
        }

        setSourcePolicyId(ReadString(policy, "policyId", "id"));

        if (getHidden())
        {
            forceRead(false);
            forceWrite(false);
            forceRequired(false);
        }
        else if (getLocked())
        {
            forceWrite(false);
        }
    }

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

    private static string? SelectPolicyTargetKey(params string?[] selectors)
        => selectors.FirstOrDefault(selector =>
               !string.IsNullOrWhiteSpace(selector) &&
               !string.Equals(selector, "*", StringComparison.Ordinal))
           ?? selectors.FirstOrDefault(selector =>
               string.Equals(selector, "*", StringComparison.Ordinal));
}

internal static class DynamicFlowPolicyValidator
{
    private static readonly string[] ActorRoles =
    {
        "ISSUER", "ASSIGNEE", "COORDINATOR", "REVIEWER", "FINALIZER"
    };

    private static readonly string[] MappingDataTypes =
    {
        "TEXT", "NUMBER", "BOOLEAN", "DATE", "FULL_DATE",
        "SINGLE_SELECT", "MULTI_SELECT", "JSON"
    };

    public static void ValidatePayload(JsonObject root, DynamicFormTemplate? dynamicFormTemplate)
        => ValidatePayload(
            root,
            dynamicFormTemplate is null
                ? null
                : new Dictionary<string, DynamicFormTemplate>(StringComparer.Ordinal)
                {
                    [dynamicFormTemplate.Id] = dynamicFormTemplate
                });

    public static void ValidatePayload(
        JsonObject root,
        IReadOnlyDictionary<string, DynamicFormTemplate>? dynamicFormTemplates)
    {
        var refs = DynamicFlowFormReferenceIndex.From(root, dynamicFormTemplates);
        var stepRefs = DynamicFlowStepReferenceIndex.From(root);

        var rootStepId = ValidateTransitions(root, stepRefs);
        ValidateFormTopology(root, rootStepId, dynamicFormTemplates);
        ValidateActorPolicies(root, stepRefs);
        ValidateFieldPolicies(root, stepRefs, refs);
        ValidateTableColumnPolicies(root, stepRefs, refs);
        ValidateMappingRules(root, stepRefs, refs);
    }

    private static string? ValidateTransitions(JsonObject root, DynamicFlowStepReferenceIndex stepRefs)
    {
        if (root["steps"] is not JsonArray steps)
            return null;

        var stepKeys = new HashSet<string>(StringComparer.Ordinal);
        var idByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index] is not JsonObject step)
                continue;

            var stepId = ReadString(step, "stepId", "id");
            var stepCode = ReadString(step, "stepCode", "code");
            if (string.IsNullOrWhiteSpace(stepId))
                continue;
            if (!stepKeys.Add(stepId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"steps[{index}].stepId", stepId, reason = "DYNAMIC_FLOW_TEMPLATE_STEP_ID_DUPLICATE" });
            }

            if (!string.IsNullOrWhiteSpace(stepCode))
            {
                if (idByCode.ContainsKey(stepCode))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { field = $"steps[{index}].stepCode", stepCode, reason = "DYNAMIC_FLOW_TEMPLATE_STEP_CODE_DUPLICATE" });
                }

                idByCode[stepCode] = stepId;
            }
        }

        var transitions = root["transitions"] as JsonArray;
        if (stepKeys.Count > 1 && (transitions is null || transitions.Count == 0))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "transitions", reason = "DYNAMIC_FLOW_TEMPLATE_TRANSITIONS_REQUIRED" });
        }

        if (transitions is null || transitions.Count == 0)
            return stepKeys.FirstOrDefault();

        var outgoing = stepKeys.ToDictionary(key => key, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var incomingCount = stepKeys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        var edges = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < transitions.Count; index++)
        {
            if (transitions[index] is not JsonObject transition)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"transitions[{index}]", reason = "DYNAMIC_FLOW_TEMPLATE_TRANSITION_OBJECT_REQUIRED" });
            }

            var from = ResolveTransitionStep(transition, "from", stepKeys, idByCode, index);
            var to = ResolveTransitionStep(transition, "to", stepKeys, idByCode, index);
            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"transitions[{index}]", fromStepId = from, reason = "DYNAMIC_FLOW_TEMPLATE_TRANSITION_SELF_REFERENCE" });
            }

            if (!edges.Add($"{from}\n{to}"))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"transitions[{index}]", fromStepId = from, toStepId = to, reason = "DYNAMIC_FLOW_TEMPLATE_TRANSITION_DUPLICATE" });
            }

            outgoing[from].Add(to);
            incomingCount[to]++;
            if (incomingCount[to] > 1)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"transitions[{index}]", toStepId = to, reason = "DYNAMIC_FLOW_TEMPLATE_STEP_MULTIPLE_PARENTS" });
            }
        }

        var roots = incomingCount.Where(item => item.Value == 0).Select(item => item.Key).ToList();
        if (roots.Count != 1)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "transitions", rootCount = roots.Count, reason = "DYNAMIC_FLOW_TEMPLATE_SINGLE_ROOT_REQUIRED" });
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        VisitTransitionNode(roots[0], outgoing, visited, active);
        if (visited.Count != stepKeys.Count)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "transitions",
                    unreachableStepIds = stepKeys.Except(visited, StringComparer.Ordinal).ToList(),
                    reason = "DYNAMIC_FLOW_TEMPLATE_STEP_UNREACHABLE"
                });
        }

        return roots[0];
    }

    private static void ValidateFormTopology(
        JsonObject root,
        string? rootStepId,
        IReadOnlyDictionary<string, DynamicFormTemplate>? dynamicFormTemplates)
    {
        var rootFormId = ReadString(root, "rootDynamicFormTemplateId");
        var formIdByNodeId = new Dictionary<string, string>(StringComparer.Ordinal);
        var formNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var formIds = new HashSet<string>(StringComparer.Ordinal);
        var rootNodes = new List<(string NodeId, string FormId)>();

        if (root["formNodes"] is JsonArray formNodes)
        {
            for (var index = 0; index < formNodes.Count; index++)
            {
                if (formNodes[index] is not JsonObject formNode)
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { field = $"formNodes[{index}]", reason = "DYNAMIC_FLOW_FORM_NODE_OBJECT_REQUIRED" });
                }

                var nodeId = ReadString(formNode, "formNodeId", "nodeId", "id");
                var formId = ReadString(formNode, "dynamicFormTemplateId", "formTemplateId");
                if (string.IsNullOrWhiteSpace(nodeId))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { field = $"formNodes[{index}].formNodeId", reason = "DYNAMIC_FLOW_FORM_NODE_ID_REQUIRED" });
                }

                if (string.IsNullOrWhiteSpace(formId))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new
                        {
                            field = $"formNodes[{index}].dynamicFormTemplateId",
                            reason = "DYNAMIC_FLOW_FORM_NODE_FORM_REQUIRED"
                        });
                }

                if (!formNodeIds.Add(nodeId))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new
                        {
                            field = $"formNodes[{index}].formNodeId",
                            formNodeId = nodeId,
                            reason = "DYNAMIC_FLOW_FORM_NODE_ID_DUPLICATE"
                        });
                }

                if (!formIds.Add(formId))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new
                        {
                            field = $"formNodes[{index}].dynamicFormTemplateId",
                            dynamicFormTemplateId = formId,
                            reason = "DYNAMIC_FLOW_FORM_NODE_FORM_DUPLICATE"
                        });
                }

                formIdByNodeId[nodeId] = formId;
                if (string.Equals(ReadString(formNode, "role"), "ROOT", StringComparison.OrdinalIgnoreCase))
                    rootNodes.Add((nodeId, formId));

                if (dynamicFormTemplates is not null && !dynamicFormTemplates.ContainsKey(formId))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { dynamicFormTemplateId = formId, reason = "DYNAMIC_FLOW_FORM_REFERENCE_UNKNOWN" });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(rootFormId) &&
            (rootNodes.Count != 1 || !string.Equals(rootNodes[0].FormId, rootFormId, StringComparison.Ordinal)))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = "formNodes",
                    rootDynamicFormTemplateId = rootFormId,
                    rootNodeCount = rootNodes.Count,
                    reason = "DYNAMIC_FLOW_ROOT_FORM_NODE_MISMATCH"
                });
        }

        JsonObject? rootStep = null;
        if (root["steps"] is JsonArray steps)
        {
            for (var index = 0; index < steps.Count; index++)
            {
                if (steps[index] is not JsonObject step)
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { field = $"steps[{index}]", reason = "DYNAMIC_FLOW_TEMPLATE_STEP_OBJECT_REQUIRED" });
                }

                var stepId = ReadString(step, "stepId", "id");
                var stepFormId = ReadString(step, "dynamicFormTemplateId", "formTemplateId");
                var formNodeId = ReadString(step, "formNodeId", "nodeId");
                if (!string.IsNullOrWhiteSpace(formNodeId))
                {
                    if (!formIdByNodeId.TryGetValue(formNodeId, out var nodeFormId))
                    {
                        throw AppExceptionFactory.BadRequest(
                            AppErrorCode.COMMON_VALIDATION_FAILED,
                            new
                            {
                                field = $"steps[{index}].formNodeId",
                                formNodeId,
                                reason = "DYNAMIC_FLOW_STEP_FORM_NODE_UNKNOWN"
                            });
                    }

                    if (!string.IsNullOrWhiteSpace(stepFormId) &&
                        !string.Equals(stepFormId, nodeFormId, StringComparison.Ordinal))
                    {
                        throw AppExceptionFactory.BadRequest(
                            AppErrorCode.COMMON_VALIDATION_FAILED,
                            new
                            {
                                field = $"steps[{index}]",
                                formNodeId,
                                dynamicFormTemplateId = stepFormId,
                                formNodeDynamicFormTemplateId = nodeFormId,
                                reason = "DYNAMIC_FLOW_STEP_FORM_NODE_MISMATCH"
                            });
                    }
                }

                var topologyDeclared = !string.IsNullOrWhiteSpace(rootFormId) || formIds.Count > 0;
                if (topologyDeclared &&
                    (string.IsNullOrWhiteSpace(stepFormId) || !formIds.Contains(stepFormId)))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new
                        {
                            field = $"steps[{index}].dynamicFormTemplateId",
                            dynamicFormTemplateId = stepFormId,
                            reason = "DYNAMIC_FLOW_STEP_FORM_NODE_REQUIRED"
                        });
                }

                if (!string.IsNullOrWhiteSpace(stepFormId) &&
                    dynamicFormTemplates is not null &&
                    !dynamicFormTemplates.ContainsKey(stepFormId))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { dynamicFormTemplateId = stepFormId, reason = "DYNAMIC_FLOW_FORM_REFERENCE_UNKNOWN" });
                }

                if (!string.IsNullOrWhiteSpace(rootStepId) && string.Equals(stepId, rootStepId, StringComparison.Ordinal))
                    rootStep = step;
            }
        }

        if (!string.IsNullOrWhiteSpace(rootFormId) && rootStep is not null)
        {
            var rootStepFormId = ReadString(rootStep, "dynamicFormTemplateId", "formTemplateId");
            if (!string.Equals(rootStepFormId, rootFormId, StringComparison.Ordinal))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = "steps",
                        rootStepId,
                        rootDynamicFormTemplateId = rootFormId,
                        rootStepDynamicFormTemplateId = rootStepFormId,
                        reason = "DYNAMIC_FLOW_ROOT_STEP_FORM_MISMATCH"
                    });
            }
        }
    }

    private static string ResolveTransitionStep(
        JsonObject transition,
        string direction,
        ISet<string> stepIds,
        IReadOnlyDictionary<string, string> idByCode,
        int transitionIndex)
    {
        var stepId = ReadString(transition, $"{direction}StepId", direction == "from" ? "sourceStepId" : "targetStepId");
        if (!string.IsNullOrWhiteSpace(stepId) && stepIds.Contains(stepId))
            return stepId;

        var stepCode = ReadString(transition, $"{direction}StepCode", direction == "from" ? "sourceStepCode" : "targetStepCode");
        if (!string.IsNullOrWhiteSpace(stepCode) && idByCode.TryGetValue(stepCode, out var resolved))
            return resolved;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                field = $"transitions[{transitionIndex}].{direction}StepId",
                stepId,
                stepCode,
                reason = "DYNAMIC_FLOW_TEMPLATE_TRANSITION_STEP_UNKNOWN"
            });
    }

    private static void VisitTransitionNode(
        string stepId,
        IReadOnlyDictionary<string, HashSet<string>> outgoing,
        ISet<string> visited,
        ISet<string> active)
    {
        if (active.Contains(stepId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "transitions", stepId, reason = "DYNAMIC_FLOW_TEMPLATE_TRANSITION_CYCLE" });
        }

        if (!visited.Add(stepId))
            return;

        active.Add(stepId);
        foreach (var next in outgoing[stepId])
            VisitTransitionNode(next, outgoing, visited, active);
        active.Remove(stepId);
    }

    private static void ValidateActorPolicies(JsonObject root, DynamicFlowStepReferenceIndex stepRefs)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var policy in ReadRequiredObjectArray(root, "actorPolicies"))
        {
            var fieldName = $"actorPolicies[{index}]";
            ValidateStepScope(policy, stepRefs, fieldName);
            ValidateActorRole(policy, fieldName);
            EnsureBooleanIfPresent(policy, fieldName, "allowSubFlow", "allowForward", "canFinalize");
            EnsureUniquePolicyScope(scopes, policy, fieldName, targetKey: "ACTOR");
            index++;
        }
    }

    private static void ValidateFieldPolicies(
        JsonObject root,
        DynamicFlowStepReferenceIndex stepRefs,
        DynamicFlowFormReferenceIndex? refs)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var policy in ReadRequiredObjectArray(root, "fieldPolicies"))
        {
            var fieldName = $"fieldPolicies[{index}]";
            ValidateStepScope(policy, stepRefs, fieldName);
            ValidateActorRole(policy, fieldName);
            EnsureBooleanIfPresent(policy, fieldName, "read", "canRead", "write", "canWrite", "required", "isRequired",
                "hidden", "isHidden", "locked", "isLocked", "lockedAfterSubmit");

            refs?.CanonicalizeFieldReference(fieldName, policy);
            var fieldId = ReadString(policy, "fieldId");
            var fieldKey = ReadString(policy, "fieldKey");
            if (string.IsNullOrWhiteSpace(fieldId) && string.IsNullOrWhiteSpace(fieldKey))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_FIELD_POLICY_TARGET_REQUIRED" });
            }

            var fieldIdentity = refs?.ResolveFieldIdentity(fieldName, policy, fieldId, fieldKey)
                                ?? FirstNonBlank(fieldId, fieldKey)!;
            EnsureUniquePolicyScope(scopes, policy, fieldName, fieldIdentity);

            index++;
        }
    }

    private static void ValidateTableColumnPolicies(
        JsonObject root,
        DynamicFlowStepReferenceIndex stepRefs,
        DynamicFlowFormReferenceIndex? refs)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var policy in ReadRequiredObjectArray(root, "tableColumnPolicies"))
        {
            var fieldName = $"tableColumnPolicies[{index}]";
            ValidateStepScope(policy, stepRefs, fieldName);
            ValidateActorRole(policy, fieldName);
            EnsureBooleanIfPresent(policy, fieldName, "read", "canRead", "write", "canWrite", "required", "isRequired",
                "hidden", "isHidden", "locked", "isLocked", "lockedAfterSubmit");

            var blockId = ReadString(policy, "blockId");
            var columnKey = ReadString(policy, "columnKey");
            if (string.IsNullOrWhiteSpace(blockId) || string.IsNullOrWhiteSpace(columnKey))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_TABLE_COLUMN_POLICY_TARGET_REQUIRED" });
            }

            refs?.EnsureTableColumnExists(fieldName, policy, blockId, columnKey);
            EnsureUniquePolicyScope(scopes, policy, fieldName, $"{blockId}:{columnKey}");
            index++;
        }
    }

    private static void ValidateMappingRules(
        JsonObject root,
        DynamicFlowStepReferenceIndex stepRefs,
        DynamicFlowFormReferenceIndex? refs)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targetWrites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var rule in ReadRequiredObjectArray(root, "mappingRules"))
        {
            var fieldName = $"mappingRules[{index}]";
            ValidateMappingStepScope(rule, stepRefs, fieldName);

            var mappingId = ReadString(rule, "mappingId", "id");
            if (string.IsNullOrWhiteSpace(mappingId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.mappingId", reason = "DYNAMIC_FLOW_MAPPING_ID_REQUIRED" });
            }

            if (!ids.Add(mappingId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.mappingId", mappingId, reason = "DYNAMIC_FLOW_MAPPING_ID_DUPLICATE" });
            }

            var mappingVersion = ReadInt(rule, "mappingVersion", "version");
            if (mappingVersion.HasValue && mappingVersion.Value <= 0)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.mappingVersion", mappingVersion, reason = "DYNAMIC_FLOW_MAPPING_VERSION_INVALID" });
            }

            if (rule["inputs"] is JsonArray || rule["target"] is JsonObject || rule["calculation"] is JsonObject)
            {
                var targetWrite = ValidateStructuredMappingRule(rule, stepRefs, refs, fieldName);
                EnsureUniqueMappingTarget(targetWrites, targetWrite, fieldName);
                index++;
                continue;
            }

            stepRefs.CanonicalizeLegacyMappingScope(rule, fieldName);

            var sourceFieldId = ReadString(rule, "sourceFieldId");
            var sourceFieldKey = ReadString(rule, "sourceFieldKey");
            var sourceBlockId = ReadString(rule, "sourceBlockId");
            var sourceColumnKey = ReadString(rule, "sourceColumnKey");
            var sourceSectionId = ReadString(rule, "sourceSectionId");
            var sourceSectionCode = ReadString(rule, "sourceSectionCode");
            var targetFieldId = ReadString(rule, "targetFieldId");
            var targetFieldKey = ReadString(rule, "targetFieldKey");
            var targetBlockId = ReadString(rule, "targetBlockId");
            var targetColumnKey = ReadString(rule, "targetColumnKey");
            var targetSectionId = ReadString(rule, "targetSectionId");
            var targetSectionCode = ReadString(rule, "targetSectionCode");
            var mappingKind = ReadString(rule, "mappingKind")?.ToUpperInvariant();

            var hasSourceField = !string.IsNullOrWhiteSpace(sourceFieldId) || !string.IsNullOrWhiteSpace(sourceFieldKey);
            var hasSourceTable = !string.IsNullOrWhiteSpace(sourceBlockId) || !string.IsNullOrWhiteSpace(sourceColumnKey);
            var hasSourceSection = !string.IsNullOrWhiteSpace(sourceSectionId) || !string.IsNullOrWhiteSpace(sourceSectionCode);
            var hasTargetField = !string.IsNullOrWhiteSpace(targetFieldId) || !string.IsNullOrWhiteSpace(targetFieldKey);
            var hasTargetTable = !string.IsNullOrWhiteSpace(targetBlockId) || !string.IsNullOrWhiteSpace(targetColumnKey);
            var hasTargetSection = !string.IsNullOrWhiteSpace(targetSectionId) || !string.IsNullOrWhiteSpace(targetSectionCode);

            if (mappingKind is "SECTION")
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_SECTION_BUNDLE_RULES_REQUIRED" });
            }
            else if (mappingKind is "FIELD")
            {
                if (!hasSourceField || !hasTargetField)
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_FIELD_REQUIRED" });
                }
            }

            if (!hasSourceField && !hasSourceTable && mappingKind is not "SECTION")
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_SOURCE_REQUIRED" });
            }

            if (hasSourceField && hasSourceTable)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_SOURCE_AMBIGUOUS" });
            }

            if (!hasTargetField && !hasTargetTable && mappingKind is not "SECTION")
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_TARGET_REQUIRED" });
            }

            if (hasTargetField && hasTargetTable)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_TARGET_AMBIGUOUS" });
            }

            if (hasSourceTable && (string.IsNullOrWhiteSpace(sourceBlockId) || string.IsNullOrWhiteSpace(sourceColumnKey)))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_SOURCE_TABLE_TARGET_REQUIRED" });
            }

            if (hasTargetTable && (string.IsNullOrWhiteSpace(targetBlockId) || string.IsNullOrWhiteSpace(targetColumnKey)))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_TARGET_REQUIRED" });
            }

            if (hasTargetTable && !hasSourceTable)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_TABLE_TARGET_ROW_CONTEXT_REQUIRED" });
            }

            if (refs is not null)
            {
                if (hasSourceSection)
                    refs.EnsureSectionExists(fieldName, rule, isSource: true, sourceSectionId, sourceSectionCode);
                if (hasTargetSection)
                    refs.EnsureSectionExists(fieldName, rule, isSource: false, targetSectionId, targetSectionCode);
                if (hasSourceField)
                    refs.EnsureFieldExists(fieldName, rule, sourceFieldId, sourceFieldKey, isSource: true);
                if (hasSourceTable)
                    refs.EnsureTableColumnExists(fieldName, rule, sourceBlockId!, sourceColumnKey!, isSource: true);
                if (hasTargetTable)
                    refs.EnsureTableColumnExists(fieldName, rule, targetBlockId!, targetColumnKey!, isSource: false);
            }

            var targetReference = hasTargetField
                ? refs?.ResolveFieldIdentity(fieldName, rule, targetFieldId, targetFieldKey, isSource: false)
                  ?? FirstNonBlank(targetFieldId, targetFieldKey)!
                : $"{targetBlockId?.Trim()}:{targetColumnKey?.Trim().ToLowerInvariant()}";
            EnsureUniqueMappingTarget(
                targetWrites,
                BuildMappingTargetKey(
                    ReadString(rule, "targetDynamicFormTemplateId", "targetFormTemplateId"),
                    ReadString(rule, "targetStepId"),
                    ReadString(rule, "targetStepCode"),
                    hasTargetField ? "FIELD" : "TABLE_COLUMN",
                    targetReference),
                fieldName);

            EnsureStringInSetIfPresent(
                rule,
                fieldName,
                "mappingKind",
                "FIELD",
                "TABLE_COLUMN",
                "TABLE");

            EnsureStringInSetIfPresent(
                rule,
                fieldName,
                "dataType",
                "TEXT",
                "NUMBER",
                "BOOLEAN",
                "DATE",
                "FULL_DATE",
                "SINGLE_SELECT",
                "MULTI_SELECT",
                "JSON");
            EnsureStringInSetIfPresent(
                rule,
                fieldName,
                "valueTransform",
                "COPY",
                "FIRST",
                "FIRST_NON_BLANK",
                "SUM",
                "COUNT",
                "TEXT_JOIN",
                "JSON");
            EnsureStringInSetIfPresent(
                rule,
                fieldName,
                "conflictPolicy",
                "OVERWRITE",
                "SOURCE_WINS",
                "TARGET_WINS",
                "KEEP_TARGET",
                "ERROR",
                "ERROR_ON_CONFLICT",
                "APPEND",
                "MERGE");
            EnsureStringInSetIfPresent(
                rule,
                fieldName,
                "contributionPolicy",
                "INCLUDE",
                "EXCLUDE",
                "SOURCE_ONLY",
                "TARGET_ONLY",
                "NON_CONTRIBUTING");

            index++;
        }
    }

    private static string ValidateStructuredMappingRule(
        JsonObject rule,
        DynamicFlowStepReferenceIndex stepRefs,
        DynamicFlowFormReferenceIndex? refs,
        string fieldName)
    {
        if (rule["inputs"] is not JsonArray inputs || inputs.Count is < 1 or > 16)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.inputs", reason = "DYNAMIC_FLOW_MAPPING_INPUT_COUNT_INVALID" });
        }

        var inputKeys = new HashSet<string>(StringComparer.Ordinal);
        var inputDataTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputCardinalities = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceStepScopes = new List<(string? StepId, string? StepCode)>();
        var hasTableInput = false;
        for (var index = 0; index < inputs.Count; index++)
        {
            if (inputs[index] is not JsonObject input)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.inputs[{index}]", reason = "DYNAMIC_FLOW_MAPPING_INPUT_OBJECT_REQUIRED" });
            }

            var inputField = $"{fieldName}.inputs[{index}]";
            var inputKey = ReadString(input, "inputKey");
            if (string.IsNullOrWhiteSpace(inputKey) || !inputKeys.Add(inputKey))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = $"{inputField}.inputKey",
                        inputKey,
                        reason = string.IsNullOrWhiteSpace(inputKey)
                            ? "DYNAMIC_FLOW_MAPPING_INPUT_KEY_REQUIRED"
                            : "DYNAMIC_FLOW_MAPPING_INPUT_KEY_DUPLICATE"
                    });
            }

            var inputDataType = ReadString(input, "dataType")?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(inputDataType))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{inputField}.dataType", reason = "DYNAMIC_FLOW_MAPPING_INPUT_DATA_TYPE_REQUIRED" });
            }

            EnsureStringInSetIfPresent(input, inputField, "dataType", MappingDataTypes);
            inputDataTypes[inputKey] = inputDataType;
            EnsureStringInSetIfPresent(input, inputField, "cardinality", "ONE", "MANY", "SCALAR", "COLLECTION");
            inputCardinalities[inputKey] = NormalizeMappingCardinality(ReadString(input, "cardinality"));
            EnsureStringInSetIfPresent(input, inputField, "nullPolicy", "KEEP_NULL", "SKIP", "ZERO", "EMPTY_TEXT", "ERROR");
            var nullPolicy = ReadString(input, "nullPolicy")?.ToUpperInvariant();
            if (nullPolicy == "ZERO" && inputDataType != "NUMBER")
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{inputField}.nullPolicy", inputDataType, reason = "DYNAMIC_FLOW_MAPPING_ZERO_POLICY_NUMBER_REQUIRED" });
            }
            if (nullPolicy == "EMPTY_TEXT" && inputDataType is not "TEXT" and not "SINGLE_SELECT")
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{inputField}.nullPolicy", inputDataType, reason = "DYNAMIC_FLOW_MAPPING_EMPTY_TEXT_POLICY_TEXT_REQUIRED" });
            }

            if (input["source"] is not JsonObject source)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{inputField}.source", reason = "DYNAMIC_FLOW_MAPPING_SOURCE_OBJECT_REQUIRED" });
            }

            var sourceKind = ValidateStructuredEndpoint(
                source,
                inputField + ".source",
                stepRefs,
                refs,
                isSource: true,
                allowConstant: true,
                declaredDataType: inputDataType);
            hasTableInput |= sourceKind == "TABLE_COLUMN";
            if (sourceKind != "CONSTANT")
                sourceStepScopes.Add((ReadString(source, "stepId"), ReadString(source, "stepCode")));
            if (sourceKind == "CONSTANT" && !input.ContainsKey("constantValue"))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{inputField}.constantValue", reason = "DYNAMIC_FLOW_MAPPING_CONSTANT_VALUE_REQUIRED" });
            }
            if (sourceKind == "CONSTANT")
                EnsureMappingLiteralType(input["constantValue"], inputDataType, $"{inputField}.constantValue");
        }

        if (rule["target"] is not JsonObject target)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.target", reason = "DYNAMIC_FLOW_MAPPING_TARGET_OBJECT_REQUIRED" });
        }

        var targetDataType = ReadString(target, "dataType")?.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(targetDataType))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.target.dataType", reason = "DYNAMIC_FLOW_MAPPING_TARGET_DATA_TYPE_REQUIRED" });
        }

        var targetKind = ValidateStructuredEndpoint(
            target,
            fieldName + ".target",
            stepRefs,
            refs,
            isSource: false,
            allowConstant: false,
            declaredDataType: targetDataType);
        foreach (var sourceStepScope in sourceStepScopes)
        {
            stepRefs.EnsureMappingStepsConnected(
                fieldName,
                sourceStepScope.StepId,
                sourceStepScope.StepCode,
                ReadString(target, "stepId"),
                ReadString(target, "stepCode"));
        }
        var evaluationGrain = ReadString(rule, "evaluationGrain")?.ToUpperInvariant() ?? "FLOW_INSTANCE";
        EnsureStringInSetIfPresent(rule, fieldName, "evaluationGrain", "FLOW_INSTANCE", "SOURCE_REPORT", "TABLE_ROW");
        EnsureStringInSetIfPresent(rule, fieldName, "errorPolicy", "BLOCK_APPLY", "SKIP_RULE", "USE_DEFAULT");
        EnsureStringInSetIfPresent(rule, fieldName, "conflictPolicy", "OVERWRITE", "TARGET_WINS", "ERROR_ON_CONFLICT");
        EnsureStringInSetIfPresent(
            rule,
            fieldName,
            "contributionPolicy",
            "INCLUDE",
            "EXCLUDE",
            "SOURCE_ONLY",
            "TARGET_ONLY",
            "NON_CONTRIBUTING");

        if (targetKind == "FIELD" && evaluationGrain != "FLOW_INSTANCE")
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.evaluationGrain", evaluationGrain, reason = "DYNAMIC_FLOW_MAPPING_FIELD_TARGET_SINGLE_RESULT_REQUIRED" });
        }

        if (targetKind == "TABLE_COLUMN" && (evaluationGrain != "TABLE_ROW" || !hasTableInput))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = $"{fieldName}.evaluationGrain",
                    evaluationGrain,
                    reason = "DYNAMIC_FLOW_MAPPING_TABLE_TARGET_ROW_CONTEXT_REQUIRED"
                });
        }

        if (evaluationGrain == "TABLE_ROW" && !hasTableInput)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.evaluationGrain", reason = "DYNAMIC_FLOW_MAPPING_TABLE_ROW_SOURCE_REQUIRED" });
        }

        if (rule["calculation"] is not JsonObject calculation)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.calculation", reason = "DYNAMIC_FLOW_MAPPING_CALCULATION_REQUIRED" });
        }

        var calculationKind = ReadString(calculation, "kind")?.ToUpperInvariant() ?? "DIRECT";
        EnsureStringInSetIfPresent(calculation, fieldName + ".calculation", "kind", "DIRECT", "EXPRESSION", "REGISTERED_FUNCTION");
        EnsureStringInSetIfPresent(calculation, fieldName + ".calculation", "resultDataType", MappingDataTypes);
        var declaredResultType = ReadString(calculation, "resultDataType")?.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(declaredResultType))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.calculation.resultDataType", reason = "DYNAMIC_FLOW_MAPPING_RESULT_DATA_TYPE_REQUIRED" });
        }

        if (string.Equals(ReadString(rule, "errorPolicy"), "USE_DEFAULT", StringComparison.OrdinalIgnoreCase) &&
            !calculation.ContainsKey("defaultValue"))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.calculation.defaultValue", reason = "DYNAMIC_FLOW_MAPPING_DEFAULT_VALUE_REQUIRED" });
        }
        if (calculation.ContainsKey("defaultValue"))
            EnsureMappingLiteralType(calculation["defaultValue"], declaredResultType, $"{fieldName}.calculation.defaultValue");

        var directOperation = ReadString(calculation, "operation")?.Trim().ToLowerInvariant() ?? "copy";
        if (calculationKind == "DIRECT" &&
            directOperation == "copy" &&
            inputCardinalities.Values.Any(cardinality => cardinality == "MANY"))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = $"{fieldName}.calculation.operation",
                    evaluationGrain,
                    reason = "DYNAMIC_FLOW_MAPPING_COPY_COLLECTION_REDUCER_REQUIRED"
                });
        }
        if (calculationKind == "DIRECT" &&
            inputCardinalities.Values.Any(cardinality => cardinality == "MANY") &&
            directOperation is "divide" or "round" or "trim" or "upper" or "lower" or
                "datediffdays" or "dateadddays" or "if" or "and" or "or" or "not" or
                "eq" or "gt" or "gte" or "lt" or "lte")
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = $"{fieldName}.calculation.operation",
                    operation = directOperation,
                    reason = "DYNAMIC_FLOW_MAPPING_SCALAR_OPERATION_COLLECTION_INPUT"
                });
        }

        var inferredResultType = ValidateStructuredCalculation(
            calculation,
            calculationKind,
            inputDataTypes,
            fieldName + ".calculation");
        if (!string.IsNullOrWhiteSpace(declaredResultType) &&
            !string.IsNullOrWhiteSpace(inferredResultType) &&
            !MappingTypesCompatible(declaredResultType, inferredResultType))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = $"{fieldName}.calculation.resultDataType",
                    declaredResultType,
                    inferredResultType,
                    reason = "DYNAMIC_FLOW_MAPPING_RESULT_TYPE_MISMATCH"
                });
        }

        var resultType = FirstNonBlank(declaredResultType, inferredResultType);
        if (!string.IsNullOrWhiteSpace(resultType) &&
            !string.IsNullOrWhiteSpace(targetDataType) &&
            !MappingTypesCompatible(resultType, targetDataType))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = $"{fieldName}.target.dataType",
                    resultType,
                    targetDataType,
                    reason = "DYNAMIC_FLOW_MAPPING_TARGET_TYPE_MISMATCH"
                });
        }

        var targetReference = targetKind == "FIELD"
            ? refs?.ResolveFieldIdentity(
                  fieldName + ".target",
                  target,
                  ReadString(target, "fieldId"),
                  ReadString(target, "fieldKey"),
                  isSource: false,
                  expectedDataType: targetDataType)
              ?? FirstNonBlank(ReadString(target, "fieldId"), ReadString(target, "fieldKey"))!
            : $"{ReadString(target, "blockId")?.Trim()}:{ReadString(target, "columnKey")?.Trim().ToLowerInvariant()}";
        return BuildMappingTargetKey(
            ReadString(target, "dynamicFormTemplateId", "formTemplateId"),
            ReadString(target, "stepId"),
            ReadString(target, "stepCode"),
            targetKind,
            targetReference);
    }

    private static string ValidateStructuredEndpoint(
        JsonObject endpoint,
        string fieldName,
        DynamicFlowStepReferenceIndex stepRefs,
        DynamicFlowFormReferenceIndex? refs,
        bool isSource,
        bool allowConstant,
        string declaredDataType)
    {
        var kind = ReadString(endpoint, "kind")?.ToUpperInvariant();
        var allowedKinds = allowConstant
            ? new[] { "FIELD", "TABLE_COLUMN", "CONSTANT" }
            : new[] { "FIELD", "TABLE_COLUMN" };
        if (string.IsNullOrWhiteSpace(kind) || !allowedKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.kind", kind, allowedKinds, reason = "DYNAMIC_FLOW_MAPPING_ENDPOINT_KIND_INVALID" });
        }

        EnsureStringInSetIfPresent(endpoint, fieldName, "dataType", MappingDataTypes);
        if (kind == "CONSTANT")
            return kind;

        var formId = ReadString(endpoint, "dynamicFormTemplateId", "formTemplateId");
        var stepId = ReadString(endpoint, "stepId");
        var stepCode = ReadString(endpoint, "stepCode");
        if (string.IsNullOrWhiteSpace(formId) ||
            (string.IsNullOrWhiteSpace(stepId) && string.IsNullOrWhiteSpace(stepCode)) ||
            string.Equals(stepId, "*", StringComparison.Ordinal) ||
            string.Equals(stepCode, "*", StringComparison.Ordinal))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_ENDPOINT_CONTEXT_REQUIRED" });
        }

        ValidateStepValue(endpoint, fieldName, "stepId", stepRefs.StepIds, "DYNAMIC_FLOW_MAPPING_STEP_ID_UNKNOWN");
        ValidateStepValue(endpoint, fieldName, "stepCode", stepRefs.StepCodes, "DYNAMIC_FLOW_MAPPING_STEP_CODE_UNKNOWN");
        stepRefs.EnsureSameStep(fieldName, stepId, stepCode);

        var scope = new JsonObject
        {
            [isSource ? "sourceDynamicFormTemplateId" : "targetDynamicFormTemplateId"] = formId,
            [isSource ? "sourceStepId" : "targetStepId"] = stepId,
            [isSource ? "sourceStepCode" : "targetStepCode"] = stepCode
        };
        if (kind == "FIELD")
        {
            var fieldId = ReadString(endpoint, "fieldId");
            var fieldKey = ReadString(endpoint, "fieldKey");
            if (string.IsNullOrWhiteSpace(fieldId) && string.IsNullOrWhiteSpace(fieldKey))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_FIELD_REQUIRED" });
            }

            refs?.EnsureFieldExists(fieldName, scope, fieldId, fieldKey, isSource, declaredDataType);
            return kind;
        }

        var blockId = ReadString(endpoint, "blockId");
        var columnKey = ReadString(endpoint, "columnKey");
        if (string.IsNullOrWhiteSpace(blockId) || string.IsNullOrWhiteSpace(columnKey))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = fieldName, reason = "DYNAMIC_FLOW_MAPPING_TABLE_COLUMN_REQUIRED" });
        }

        refs?.EnsureTableColumnExists(fieldName, scope, blockId, columnKey, isSource, declaredDataType);
        return kind;
    }

    private static string? ValidateStructuredCalculation(
        JsonObject calculation,
        string calculationKind,
        IReadOnlyDictionary<string, string> inputDataTypes,
        string fieldName)
    {
        try
        {
            if (calculationKind == "EXPRESSION")
                return DynamicFlowMappingExpressionEvaluator.Validate(calculation["expression"], inputDataTypes);

            if (calculationKind == "REGISTERED_FUNCTION")
            {
                var functionArguments = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                if (calculation["arguments"] is JsonObject arguments)
                {
                    foreach (var argument in arguments)
                        functionArguments[argument.Key] = argument.Value;
                }

                return DynamicFlowRegisteredFunctionRegistry.Validate(
                    ReadString(calculation, "functionCode"),
                    ReadInt(calculation, "functionVersion"),
                    functionArguments,
                    inputDataTypes);
            }

            var operation = ReadString(calculation, "operation") ?? "copy";
            var expression = new JsonObject
            {
                ["op"] = operation,
                ["args"] = new JsonArray(inputDataTypes.Keys.Select(key => (JsonNode)new JsonObject { ["input"] = key }).ToArray())
            };
            return DynamicFlowMappingExpressionEvaluator.Validate(expression, inputDataTypes);
        }
        catch (DynamicFlowMappingEvaluationException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = fieldName, reason = ex.Reason, ex.Message });
        }
    }

    private static bool MappingTypesCompatible(string left, string right)
    {
        left = left.Trim().ToUpperInvariant();
        right = right.Trim().ToUpperInvariant();
        if (left == right)
            return true;
        return left is ("DATE" or "FULL_DATE") && right is ("DATE" or "FULL_DATE");
    }

    private static string NormalizeMappingCardinality(string? value)
        => value?.Trim().ToUpperInvariant() switch
        {
            "MANY" or "COLLECTION" => "MANY",
            _ => "ONE"
        };

    private static void EnsureMappingLiteralType(JsonNode? value, string expectedDataType, string fieldName)
    {
        if (value is null || MappingLiteralMatches(value, expectedDataType))
            return;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                field = fieldName,
                expectedDataType,
                reason = "DYNAMIC_FLOW_MAPPING_LITERAL_TYPE_MISMATCH"
            });
    }

    private static bool MappingLiteralMatches(JsonNode value, string expectedDataType)
    {
        try
        {
            DynamicFlowMappingFieldValueContract.ValidateJsonNative(
                value,
                expectedDataType,
                allowedChoiceCodes: null,
                allowNull: false);
            return true;
        }
        catch (DynamicFlowMappingEvaluationException)
        {
            return false;
        }
    }

    private static string BuildMappingTargetKey(
        string? formId,
        string? stepId,
        string? stepCode,
        string targetKind,
        string targetReference)
        => $"{formId ?? "*"}|{stepId ?? stepCode ?? "*"}|{targetKind}|{targetReference}";

    private static void EnsureUniqueMappingTarget(
        ISet<string> targetWrites,
        string targetWrite,
        string fieldName)
    {
        if (targetWrites.Add(targetWrite))
            return;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { field = fieldName, target = targetWrite, reason = "DYNAMIC_FLOW_MAPPING_TARGET_DUPLICATE" });
    }

    private static void ValidateStepScope(
        JsonObject policy,
        DynamicFlowStepReferenceIndex stepRefs,
        string fieldName)
    {
        var stepId = ReadString(policy, "stepId");
        if (!string.IsNullOrWhiteSpace(stepId) &&
            !string.Equals(stepId, "*", StringComparison.Ordinal) &&
            !stepRefs.StepIds.Contains(stepId))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.stepId", stepId, reason = "DYNAMIC_FLOW_POLICY_STEP_ID_UNKNOWN" });
        }

        var stepCode = ReadString(policy, "stepCode");
        if (!string.IsNullOrWhiteSpace(stepCode) &&
            !string.Equals(stepCode, "*", StringComparison.Ordinal) &&
            !stepRefs.StepCodes.Contains(stepCode))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.stepCode", stepCode, reason = "DYNAMIC_FLOW_POLICY_STEP_CODE_UNKNOWN" });
        }

        stepRefs.EnsureSameStep(fieldName, stepId, stepCode);
    }

    private static void ValidateActorRole(JsonObject policy, string fieldName)
    {
        var actorRole = ReadString(policy, "actorRole");
        if (string.IsNullOrWhiteSpace(actorRole) ||
            string.Equals(actorRole, "*", StringComparison.Ordinal) ||
            ActorRoles.Contains(actorRole.Trim().ToUpperInvariant(), StringComparer.Ordinal))
        {
            return;
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new
            {
                field = $"{fieldName}.actorRole",
                actorRole,
                allowedValues = ActorRoles,
                reason = "DYNAMIC_FLOW_POLICY_ACTOR_ROLE_UNSUPPORTED"
            });
    }

    private static void EnsureUniquePolicyScope(
        ISet<string> scopes,
        JsonObject policy,
        string fieldName,
        string targetKey)
    {
        var scope = string.Join(
            "|",
            targetKey,
            ReadString(policy, "stepId") ?? "*",
            ReadString(policy, "stepCode") ?? "*",
            ReadString(policy, "actorRole") ?? "*");
        if (scopes.Add(scope))
            return;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { field = fieldName, scope, reason = "DYNAMIC_FLOW_POLICY_SCOPE_DUPLICATE" });
    }

    private static void ValidateMappingStepScope(
        JsonObject rule,
        DynamicFlowStepReferenceIndex stepRefs,
        string fieldName)
    {
        ValidateStepValue(rule, fieldName, "sourceStepId", stepRefs.StepIds, "DYNAMIC_FLOW_MAPPING_SOURCE_STEP_ID_UNKNOWN");
        ValidateStepValue(rule, fieldName, "targetStepId", stepRefs.StepIds, "DYNAMIC_FLOW_MAPPING_TARGET_STEP_ID_UNKNOWN");
        ValidateStepValue(rule, fieldName, "sourceStepCode", stepRefs.StepCodes, "DYNAMIC_FLOW_MAPPING_SOURCE_STEP_CODE_UNKNOWN");
        ValidateStepValue(rule, fieldName, "targetStepCode", stepRefs.StepCodes, "DYNAMIC_FLOW_MAPPING_TARGET_STEP_CODE_UNKNOWN");
        stepRefs.EnsureSameStep(fieldName + ".source", ReadString(rule, "sourceStepId"), ReadString(rule, "sourceStepCode"));
        stepRefs.EnsureSameStep(fieldName + ".target", ReadString(rule, "targetStepId"), ReadString(rule, "targetStepCode"));
    }

    private static void ValidateStepValue(
        JsonObject root,
        string fieldName,
        string propertyName,
        HashSet<string> knownValues,
        string reason)
    {
        var value = ReadString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "*", StringComparison.Ordinal) ||
            knownValues.Contains(value))
        {
            return;
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { field = $"{fieldName}.{propertyName}", value, reason });
    }

    private static IEnumerable<JsonObject> ReadRequiredObjectArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] is not JsonArray array)
            yield break;

        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonObject obj)
            {
                yield return obj;
                continue;
            }

            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{propertyName}[{index}]", reason = "DYNAMIC_FLOW_POLICY_OBJECT_REQUIRED" });
        }
    }

    private static void EnsureBooleanIfPresent(JsonObject root, string fieldName, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetPropertyValue(propertyName, out var node) || node is null)
                continue;

            if (node is JsonValue value && value.TryGetValue<bool>(out _))
                continue;

            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.{propertyName}", reason = "DYNAMIC_FLOW_POLICY_BOOLEAN_REQUIRED" });
        }
    }

    private static void EnsureStringInSetIfPresent(
        JsonObject root,
        string fieldName,
        string propertyName,
        params string[] allowedValues)
    {
        if (!root.TryGetPropertyValue(propertyName, out var node) || node is null)
            return;

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = $"{fieldName}.{propertyName}", reason = "DYNAMIC_FLOW_MAPPING_STRING_REQUIRED" });
        }

        var normalized = text.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized) ||
            allowedValues.Contains(normalized, StringComparer.Ordinal))
        {
            return;
        }

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { field = $"{fieldName}.{propertyName}", value = text, allowedValues, reason = "DYNAMIC_FLOW_MAPPING_VALUE_UNSUPPORTED" });
    }

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
                node is JsonValue value)
            {
                if (value.TryGetValue<int>(out var intValue))
                    return intValue;
                if (value.TryGetValue<long>(out var longValue) &&
                    longValue >= int.MinValue &&
                    longValue <= int.MaxValue)
                {
                    return (int)longValue;
                }
            }
        }

        return null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed class DynamicFlowStepReferenceIndex
    {
        public HashSet<string> StepIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StepCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> StepCodeById { get; } = new(StringComparer.Ordinal);
        private Dictionary<string, string> StepIdByCode { get; } = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> StepFormIdById { get; } = new(StringComparer.Ordinal);
        private HashSet<string> ConnectedStepPairs { get; } = new(StringComparer.Ordinal);

        public static DynamicFlowStepReferenceIndex From(JsonObject root)
        {
            var result = new DynamicFlowStepReferenceIndex();
            if (root["steps"] is JsonArray steps)
            {
                foreach (var item in steps)
                {
                    if (item is not JsonObject step)
                        continue;

                    result.AddStep(
                        ReadString(step, "stepId", "id"),
                        ReadString(step, "stepCode", "code"),
                        ReadString(
                            step,
                            "dynamicFormTemplateId",
                            "formTemplateId"));
                }
            }

            var formIdByNodeId = root["formNodes"] is JsonArray formNodes
                ? formNodes
                    .OfType<JsonObject>()
                    .Select(node => new
                    {
                        FormNodeId = ReadString(node, "formNodeId"),
                        FormId = ReadString(
                            node,
                            "dynamicFormTemplateId",
                            "formTemplateId")
                    })
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item.FormNodeId) &&
                        !string.IsNullOrWhiteSpace(item.FormId))
                    .ToDictionary(
                        item => item.FormNodeId!,
                        item => item.FormId!,
                        StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            if (root["nodes"] is JsonArray nodes)
            {
                foreach (var node in nodes.OfType<JsonObject>())
                {
                    var formNodeId = ReadString(node, "formNodeId");
                    result.AddStep(
                        ReadString(node, "nodeId", "stepId", "id"),
                        ReadString(node, "nodeCode", "stepCode", "code"),
                        !string.IsNullOrWhiteSpace(formNodeId) &&
                        formIdByNodeId.TryGetValue(formNodeId, out var formId)
                            ? formId
                            : ReadString(
                                node,
                                "dynamicFormTemplateId",
                                "formTemplateId"));
                }
            }

            if (root["transitions"] is JsonArray transitions)
            {
                foreach (var item in transitions.OfType<JsonObject>())
                {
                    var fromStepId = result.ResolveStepId(
                        ReadString(item, "fromStepId", "sourceStepId"),
                        ReadString(item, "fromStepCode", "sourceStepCode"));
                    var toStepId = result.ResolveStepId(
                        ReadString(item, "toStepId", "targetStepId"),
                        ReadString(item, "toStepCode", "targetStepCode"));
                    if (string.IsNullOrWhiteSpace(fromStepId) || string.IsNullOrWhiteSpace(toStepId))
                        continue;

                    result.ConnectedStepPairs.Add(BuildStepPair(fromStepId, toStepId));
                    result.ConnectedStepPairs.Add(BuildStepPair(toStepId, fromStepId));
                }
            }
            if (root["edges"] is JsonArray edges)
            {
                foreach (var item in edges.OfType<JsonObject>())
                {
                    var fromStepId = result.ResolveStepId(
                        ReadString(
                            item,
                            "fromNodeId",
                            "fromStepId",
                            "sourceStepId"),
                        ReadString(
                            item,
                            "fromNodeCode",
                            "fromStepCode",
                            "sourceStepCode"));
                    var toStepId = result.ResolveStepId(
                        ReadString(
                            item,
                            "toNodeId",
                            "toStepId",
                            "targetStepId"),
                        ReadString(
                            item,
                            "toNodeCode",
                            "toStepCode",
                            "targetStepCode"));
                    if (string.IsNullOrWhiteSpace(fromStepId) ||
                        string.IsNullOrWhiteSpace(toStepId))
                    {
                        continue;
                    }

                    result.ConnectedStepPairs.Add(
                        BuildStepPair(fromStepId, toStepId));
                    result.ConnectedStepPairs.Add(
                        BuildStepPair(toStepId, fromStepId));
                }
            }

            return result;
        }

        private void AddStep(
            string? stepId,
            string? stepCode,
            string? formId)
        {
            AddIfNotBlank(StepIds, stepId);
            AddIfNotBlank(StepCodes, stepCode);
            if (!string.IsNullOrWhiteSpace(stepId) &&
                !string.IsNullOrWhiteSpace(stepCode))
            {
                StepCodeById[stepId] = stepCode;
                StepIdByCode[stepCode] = stepId;
            }

            if (!string.IsNullOrWhiteSpace(stepId) &&
                !string.IsNullOrWhiteSpace(formId))
            {
                StepFormIdById[stepId] = formId;
            }
        }

        public void CanonicalizeLegacyMappingScope(JsonObject rule, string fieldName)
        {
            var sourceStepId = ResolveStepId(
                ReadString(rule, "sourceStepId"),
                ReadString(rule, "sourceStepCode"));
            var targetStepId = ResolveStepId(
                ReadString(rule, "targetStepId"),
                ReadString(rule, "targetStepCode"));

            if (sourceStepId is null && StepIds.Count == 1)
                sourceStepId = StepIds.First();
            if (targetStepId is null && StepIds.Count == 1)
                targetStepId = StepIds.First();

            if (string.IsNullOrWhiteSpace(sourceStepId) || string.IsNullOrWhiteSpace(targetStepId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = fieldName,
                        reason = "DYNAMIC_FLOW_MAPPING_LEGACY_STEP_CONTEXT_REQUIRED"
                    });
            }

            CanonicalizeLegacyMappingEndpoint(rule, fieldName, "source", sourceStepId);
            CanonicalizeLegacyMappingEndpoint(rule, fieldName, "target", targetStepId);
            EnsureMappingStepsConnected(
                fieldName,
                sourceStepId,
                StepCodeById.GetValueOrDefault(sourceStepId),
                targetStepId,
                StepCodeById.GetValueOrDefault(targetStepId));
        }

        private void CanonicalizeLegacyMappingEndpoint(
            JsonObject rule,
            string fieldName,
            string direction,
            string stepId)
        {
            var stepCode = StepCodeById.GetValueOrDefault(stepId);
            var stepFormId = StepFormIdById.GetValueOrDefault(stepId);
            var formProperty = direction == "source"
                ? "sourceDynamicFormTemplateId"
                : "targetDynamicFormTemplateId";
            var explicitFormId = ReadString(
                rule,
                formProperty,
                direction == "source" ? "sourceFormTemplateId" : "targetFormTemplateId");
            if (!string.IsNullOrWhiteSpace(explicitFormId) &&
                !string.IsNullOrWhiteSpace(stepFormId) &&
                !string.Equals(explicitFormId, stepFormId, StringComparison.Ordinal))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = fieldName,
                        dynamicFormTemplateId = explicitFormId,
                        stepDynamicFormTemplateId = stepFormId,
                        reason = "DYNAMIC_FLOW_FORM_STEP_REFERENCE_MISMATCH"
                    });
            }

            rule[$"{direction}StepId"] = stepId;
            if (!string.IsNullOrWhiteSpace(stepCode))
                rule[$"{direction}StepCode"] = stepCode;
            if (!string.IsNullOrWhiteSpace(stepFormId))
                rule[formProperty] = stepFormId;
        }

        public void EnsureMappingStepsConnected(
            string fieldName,
            string? sourceStepId,
            string? sourceStepCode,
            string? targetStepId,
            string? targetStepCode)
        {
            var source = ResolveStepId(sourceStepId, sourceStepCode);
            var target = ResolveStepId(targetStepId, targetStepCode);
            if (!string.IsNullOrWhiteSpace(source) &&
                !string.IsNullOrWhiteSpace(target) &&
                (string.Equals(source, target, StringComparison.Ordinal) ||
                 ConnectedStepPairs.Contains(BuildStepPair(source, target))))
            {
                return;
            }

            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    field = fieldName,
                    sourceStepId,
                    sourceStepCode,
                    targetStepId,
                    targetStepCode,
                    reason = "DYNAMIC_FLOW_MAPPING_STEPS_NOT_CONNECTED"
                });
        }

        public void EnsureSameStep(string fieldName, string? stepId, string? stepCode)
        {
            if (string.IsNullOrWhiteSpace(stepId) ||
                string.IsNullOrWhiteSpace(stepCode) ||
                string.Equals(stepId, "*", StringComparison.Ordinal) ||
                string.Equals(stepCode, "*", StringComparison.Ordinal))
            {
                return;
            }

            if (StepCodeById.TryGetValue(stepId, out var expectedCode) &&
                string.Equals(expectedCode, stepCode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = fieldName, stepId, stepCode, reason = "DYNAMIC_FLOW_STEP_ID_CODE_MISMATCH" });
        }

        private string? ResolveStepId(string? stepId, string? stepCode)
        {
            if (!string.IsNullOrWhiteSpace(stepId) && StepIds.Contains(stepId))
                return stepId;
            return !string.IsNullOrWhiteSpace(stepCode) && StepIdByCode.TryGetValue(stepCode, out var resolved)
                ? resolved
                : null;
        }

        private static string BuildStepPair(string fromStepId, string toStepId)
            => $"{fromStepId}\n{toStepId}";
    }

    private sealed class DynamicFlowFormReferenceIndex
    {
        private readonly Dictionary<string, DynamicFormPolicyReferenceIndex> _refsByFormId;
        private readonly Dictionary<string, string> _formIdByStepId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _formIdByStepCode = new(StringComparer.OrdinalIgnoreCase);

        private DynamicFlowFormReferenceIndex(Dictionary<string, DynamicFormPolicyReferenceIndex> refsByFormId)
        {
            _refsByFormId = refsByFormId;
        }

        public static DynamicFlowFormReferenceIndex? From(
            JsonObject root,
            IReadOnlyDictionary<string, DynamicFormTemplate>? dynamicFormTemplates)
        {
            if (dynamicFormTemplates is null || dynamicFormTemplates.Count == 0)
                return null;

            var result = new DynamicFlowFormReferenceIndex(
                dynamicFormTemplates.ToDictionary(
                    x => x.Key,
                    x => DynamicFormPolicyReferenceIndex.From(x.Value),
                    StringComparer.Ordinal));

            if (root["steps"] is JsonArray steps)
            {
                foreach (var item in steps)
                {
                    if (item is not JsonObject step)
                        continue;

                    var formId = ReadString(step, "dynamicFormTemplateId", "formTemplateId");
                    if (string.IsNullOrWhiteSpace(formId))
                        continue;

                    AddStepForm(result._formIdByStepId, ReadString(step, "stepId", "id"), formId);
                    AddStepForm(result._formIdByStepCode, ReadString(step, "stepCode", "code"), formId);
                }
            }

            return result;
        }

        public void EnsureFieldExists(
            string fieldName,
            JsonObject scope,
            string? fieldId,
            string? fieldKey,
            bool? isSource = null,
            string? expectedDataType = null)
        {
            var refs = ResolveRefs(scope, isSource);
            refs?.EnsureFieldExists(fieldName, fieldId, fieldKey, expectedDataType);
        }

        public void CanonicalizeFieldReference(string fieldName, JsonObject scope)
        {
            var refs = ResolveRefs(scope, isSource: null);
            refs?.CanonicalizeFieldReference(fieldName, scope);
        }

        public string? ResolveFieldIdentity(
            string fieldName,
            JsonObject scope,
            string? fieldId,
            string? fieldKey,
            bool? isSource = null,
            string? expectedDataType = null)
        {
            var refs = ResolveRefs(scope, isSource);
            return refs?.ResolveFieldIdentity(fieldName, fieldId, fieldKey, expectedDataType)
                   ?? FirstNonBlank(fieldId, fieldKey);
        }

        public void EnsureTableColumnExists(
            string fieldName,
            JsonObject scope,
            string blockId,
            string columnKey,
            bool? isSource = null,
            string? expectedDataType = null)
        {
            var refs = ResolveRefs(scope, isSource);
            refs?.EnsureTableColumnExists(fieldName, blockId, columnKey, expectedDataType);
        }

        public void EnsureSectionExists(
            string fieldName,
            JsonObject scope,
            bool isSource,
            string? sectionId,
            string? sectionCode)
        {
            var refs = ResolveRefs(scope, isSource);
            refs?.EnsureSectionExists(fieldName, sectionId, sectionCode);
        }

        private DynamicFormPolicyReferenceIndex? ResolveRefs(JsonObject scope, bool? isSource)
        {
            var explicitFormId = isSource switch
            {
                true => ReadString(scope, "sourceDynamicFormTemplateId", "sourceFormTemplateId"),
                false => ReadString(scope, "targetDynamicFormTemplateId", "targetFormTemplateId"),
                _ => ReadString(scope, "dynamicFormTemplateId", "formTemplateId")
            };

            if (!string.IsNullOrWhiteSpace(explicitFormId))
            {
                var stepFormId = ResolveStepFormId(scope, isSource);
                if (!string.IsNullOrWhiteSpace(stepFormId) &&
                    !string.Equals(stepFormId, explicitFormId, StringComparison.Ordinal))
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new
                        {
                            dynamicFormTemplateId = explicitFormId,
                            stepDynamicFormTemplateId = stepFormId,
                            reason = "DYNAMIC_FLOW_FORM_STEP_REFERENCE_MISMATCH"
                        });
                }

                return ResolveExplicitRefs(explicitFormId);
            }

            var stepId = isSource switch
            {
                true => ReadString(scope, "sourceStepId"),
                false => ReadString(scope, "targetStepId"),
                _ => ReadString(scope, "stepId")
            };
            if (!string.IsNullOrWhiteSpace(stepId) &&
                !string.Equals(stepId, "*", StringComparison.Ordinal) &&
                _formIdByStepId.TryGetValue(stepId, out var formIdByStepId))
            {
                return ResolveExplicitRefs(formIdByStepId);
            }

            var stepCode = isSource switch
            {
                true => ReadString(scope, "sourceStepCode"),
                false => ReadString(scope, "targetStepCode"),
                _ => ReadString(scope, "stepCode")
            };
            if (!string.IsNullOrWhiteSpace(stepCode) &&
                !string.Equals(stepCode, "*", StringComparison.Ordinal) &&
                _formIdByStepCode.TryGetValue(stepCode, out var formIdByStepCode))
            {
                return ResolveExplicitRefs(formIdByStepCode);
            }

            return _refsByFormId.Count == 1
                ? _refsByFormId.Values.First()
                : null;
        }

        private string? ResolveStepFormId(JsonObject scope, bool? isSource)
        {
            var stepId = isSource switch
            {
                true => ReadString(scope, "sourceStepId"),
                false => ReadString(scope, "targetStepId"),
                _ => ReadString(scope, "stepId")
            };
            if (!string.IsNullOrWhiteSpace(stepId) &&
                !string.Equals(stepId, "*", StringComparison.Ordinal) &&
                _formIdByStepId.TryGetValue(stepId, out var formIdByStepId))
            {
                return formIdByStepId;
            }

            var stepCode = isSource switch
            {
                true => ReadString(scope, "sourceStepCode"),
                false => ReadString(scope, "targetStepCode"),
                _ => ReadString(scope, "stepCode")
            };
            return !string.IsNullOrWhiteSpace(stepCode) &&
                   !string.Equals(stepCode, "*", StringComparison.Ordinal) &&
                   _formIdByStepCode.TryGetValue(stepCode, out var formIdByStepCode)
                ? formIdByStepCode
                : null;
        }

        private DynamicFormPolicyReferenceIndex ResolveExplicitRefs(string formId)
        {
            if (_refsByFormId.TryGetValue(formId, out var refs))
                return refs;

            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { dynamicFormTemplateId = formId, reason = "DYNAMIC_FLOW_FORM_REFERENCE_UNKNOWN" });
        }

        private static void AddStepForm(IDictionary<string, string> target, string? stepValue, string formId)
        {
            if (!string.IsNullOrWhiteSpace(stepValue))
                target[stepValue.Trim()] = formId;
        }
    }

    private sealed class DynamicFormPolicyReferenceIndex
    {
        private readonly HashSet<string> _sectionIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sectionCodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fieldIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fieldKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _fieldDataTypeById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _fieldDataTypeByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _fieldKeyById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _fieldIdByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _tableColumnsByBlockId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _tableColumnDataTypes = new(StringComparer.OrdinalIgnoreCase);

        public static DynamicFormPolicyReferenceIndex From(DynamicFormTemplate template)
        {
            var result = new DynamicFormPolicyReferenceIndex();
            result.ReadSections(template.SectionsJson);
            result.ReadFields(template.FieldsJson);
            result.ReadBlocks(template.BlocksJson);
            return result;
        }

        public void EnsureSectionExists(string fieldName, string? sectionId, string? sectionCode)
        {
            if (!string.IsNullOrWhiteSpace(sectionId) && !_sectionIds.Contains(sectionId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.sectionId", sectionId, reason = "DYNAMIC_FLOW_MAPPING_SECTION_UNKNOWN" });
            }

            if (!string.IsNullOrWhiteSpace(sectionCode) && !_sectionCodes.Contains(sectionCode))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.sectionCode", sectionCode, reason = "DYNAMIC_FLOW_MAPPING_SECTION_UNKNOWN" });
            }
        }

        public void EnsureFieldExists(
            string fieldName,
            string? fieldId,
            string? fieldKey,
            string? expectedDataType = null)
        {
            if (!string.IsNullOrWhiteSpace(fieldId) && !_fieldIds.Contains(fieldId))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.fieldId", fieldId, reason = "DYNAMIC_FLOW_FIELD_POLICY_FIELD_UNKNOWN" });
            }

            if (!string.IsNullOrWhiteSpace(fieldKey) && !_fieldKeys.Contains(fieldKey))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.fieldKey", fieldKey, reason = "DYNAMIC_FLOW_FIELD_POLICY_FIELD_UNKNOWN" });
            }

            if (!string.IsNullOrWhiteSpace(fieldId) &&
                !string.IsNullOrWhiteSpace(fieldKey) &&
                _fieldKeyById.TryGetValue(fieldId, out var expectedFieldKey) &&
                !string.Equals(expectedFieldKey, fieldKey, StringComparison.OrdinalIgnoreCase))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = fieldName,
                        fieldId,
                        fieldKey,
                        expectedFieldKey,
                        reason = "DYNAMIC_FLOW_FIELD_REFERENCE_MISMATCH"
                    });
            }

            if (string.IsNullOrWhiteSpace(expectedDataType))
                return;

            var actualDataType = !string.IsNullOrWhiteSpace(fieldId) && _fieldDataTypeById.TryGetValue(fieldId, out var byId)
                ? byId
                : !string.IsNullOrWhiteSpace(fieldKey) && _fieldDataTypeByKey.TryGetValue(fieldKey, out var byKey)
                    ? byKey
                    : null;
            if (!string.IsNullOrWhiteSpace(actualDataType) &&
                !MappingTypesCompatible(actualDataType, expectedDataType))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = fieldName,
                        fieldId,
                        fieldKey,
                        actualDataType,
                        expectedDataType,
                        reason = "DYNAMIC_FLOW_MAPPING_SOURCE_FIELD_TYPE_MISMATCH"
                    });
            }
        }

        public void CanonicalizeFieldReference(string fieldName, JsonObject scope)
        {
            var fieldId = ReadString(scope, "fieldId");
            var fieldKey = ReadString(scope, "fieldKey");
            EnsureFieldExists(fieldName, fieldId, fieldKey);

            if (string.IsNullOrWhiteSpace(fieldId) &&
                !string.IsNullOrWhiteSpace(fieldKey) &&
                _fieldIdByKey.TryGetValue(fieldKey, out var resolvedFieldId))
            {
                scope["fieldId"] = resolvedFieldId;
                fieldId = resolvedFieldId;
            }

            if (string.IsNullOrWhiteSpace(fieldKey) &&
                !string.IsNullOrWhiteSpace(fieldId) &&
                _fieldKeyById.TryGetValue(fieldId, out var resolvedFieldKey))
            {
                scope["fieldKey"] = resolvedFieldKey;
            }
        }

        public string ResolveFieldIdentity(
            string fieldName,
            string? fieldId,
            string? fieldKey,
            string? expectedDataType = null)
        {
            EnsureFieldExists(fieldName, fieldId, fieldKey, expectedDataType);

            if (!string.IsNullOrWhiteSpace(fieldId))
                return $"id:{fieldId.Trim()}";
            if (!string.IsNullOrWhiteSpace(fieldKey) && _fieldIdByKey.TryGetValue(fieldKey, out var resolvedFieldId))
                return $"id:{resolvedFieldId}";
            return $"key:{fieldKey?.Trim().ToLowerInvariant()}";
        }

        public void EnsureTableColumnExists(
            string fieldName,
            string blockId,
            string columnKey,
            string? expectedDataType = null)
        {
            if (!_tableColumnsByBlockId.TryGetValue(blockId, out var columns))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.blockId", blockId, reason = "DYNAMIC_FLOW_TABLE_COLUMN_POLICY_BLOCK_UNKNOWN" });
            }

            var normalizedColumnKey = NormalizeColumnKey(columnKey) ?? string.Empty;
            if (!columns.Contains(normalizedColumnKey))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = $"{fieldName}.columnKey", blockId, columnKey, reason = "DYNAMIC_FLOW_TABLE_COLUMN_POLICY_COLUMN_UNKNOWN" });
            }


            if (!string.IsNullOrWhiteSpace(expectedDataType) &&
                _tableColumnDataTypes.TryGetValue($"{blockId}:{normalizedColumnKey}", out var actualDataType) &&
                !MappingTypesCompatible(actualDataType, expectedDataType))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new
                    {
                        field = fieldName,
                        blockId,
                        columnKey,
                        actualDataType,
                        expectedDataType,
                        reason = "DYNAMIC_FLOW_MAPPING_TABLE_COLUMN_TYPE_MISMATCH"
                    });
            }
        }

        private void ReadFields(string? fieldsJson)
        {
            if (string.IsNullOrWhiteSpace(fieldsJson))
                return;

            var fields = ParseArray(fieldsJson, "FieldsJson");
            foreach (var item in fields)
            {
                if (item is not JsonObject field)
                    continue;

                var fieldId = ReadString(field, "id", "fieldId");
                var fieldKey = ReadString(field, "key", "fieldKey");
                AddIfNotBlank(_fieldIds, fieldId);
                AddIfNotBlank(_fieldKeys, fieldKey);
                if (!string.IsNullOrWhiteSpace(fieldId) && !string.IsNullOrWhiteSpace(fieldKey))
                {
                    _fieldKeyById[fieldId] = fieldKey;
                    _fieldIdByKey[fieldKey] = fieldId;
                }
                var dataType = NormalizeFieldDataType(ReadString(field, "dataType", "type", "fieldType"));
                if (!string.IsNullOrWhiteSpace(dataType))
                {
                    if (!string.IsNullOrWhiteSpace(fieldId))
                        _fieldDataTypeById[fieldId] = dataType;
                    if (!string.IsNullOrWhiteSpace(fieldKey))
                        _fieldDataTypeByKey[fieldKey] = dataType;
                }
            }
        }

        private static string? NormalizeFieldDataType(string? value)
        {
            var normalized = value?.Trim().ToUpperInvariant();
            return normalized switch
            {
                "SHORT_TEXT" or "SHORTTEXT" or "STRING" or "TEXTAREA" => "TEXT",
                "FULLDATE" or "STRICT_DATE" => "FULL_DATE",
                "MULTISELECT" => "MULTI_SELECT",
                "SINGLESELECT" or "STRING_LIST" or "STRINGLIST" => "SINGLE_SELECT",
                "TEXT" or "NUMBER" or "BOOLEAN" or "DATE" or "FULL_DATE" or
                    "SINGLE_SELECT" or "MULTI_SELECT" or "JSON" => normalized,
                _ => null
            };
        }

        private void ReadSections(string? sectionsJson)
        {
            if (string.IsNullOrWhiteSpace(sectionsJson))
                return;

            var sections = ParseArray(sectionsJson, "SectionsJson");
            foreach (var item in sections)
            {
                if (item is not JsonObject section)
                    continue;

                AddIfNotBlank(_sectionIds, ReadString(section, "id", "sectionId"));
                AddIfNotBlank(_sectionCodes, ReadString(section, "code", "sectionCode"));
            }
        }

        private void ReadBlocks(string? blocksJson)
        {
            if (string.IsNullOrWhiteSpace(blocksJson))
                return;

            var blocks = ParseArray(blocksJson, "BlocksJson");
            var blockIndex = 0;
            foreach (var item in blocks)
            {
                if (item is not JsonObject block)
                {
                    blockIndex++;
                    continue;
                }

                var blockId = ReadString(block, "blockId", "id") ?? $"block_{blockIndex + 1}";
                var columns = GetOrAddColumns(blockId);
                AddColumnsFromDataRect(blockId, block, columns);
                blockIndex++;
            }
        }

        private HashSet<string> GetOrAddColumns(string blockId)
        {
            if (_tableColumnsByBlockId.TryGetValue(blockId, out var columns))
                return columns;

            columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _tableColumnsByBlockId[blockId] = columns;
            return columns;
        }

        private void AddColumnsFromDataRect(string blockId, JsonObject block, ISet<string> columns)
        {
            var tableMode = ReadString(block, "tableMode")?.ToUpperInvariant();
            if (tableMode is "APPEND_COLUMNS" or "SUMMARY_TEMPLATE")
                return;

            if (block["dataRect"] is not JsonObject dataRect)
                return;

            var c0 = ReadInt(dataRect, "c0");
            var c1 = ReadInt(dataRect, "c1");
            if (!c0.HasValue || !c1.HasValue || c1.Value < c0.Value)
                return;
            var r0 = ReadInt(dataRect, "r0");
            var r1 = ReadInt(dataRect, "r1");

            var width = c1.Value - c0.Value + 1;
            if (width is <= 0 or > 1000)
                return;

            for (var columnOffset = 0; columnOffset < width; columnOffset++)
            {
                var absoluteColumn = c0.Value + columnOffset;
                if (!r0.HasValue || !r1.HasValue || r1.Value < r0.Value ||
                    ColumnHasInputCell(block, absoluteColumn, r0.Value, r1.Value))
                {
                    var columnKey = $"col_{columnOffset + 1}";
                    columns.Add(columnKey);
                    _tableColumnDataTypes[$"{blockId}:{columnKey}"] = ResolveTableColumnDataType(
                        block,
                        columnOffset,
                        absoluteColumn,
                        r0,
                        r1);
                }
            }
        }

        private static string ResolveTableColumnDataType(
            JsonObject block,
            int columnOffset,
            int absoluteColumn,
            int? dataRowStart,
            int? dataRowEnd)
        {
            var defaultType = NormalizeFieldDataType(ReadString(block, "defaultDataType", "dataType")) ?? "NUMBER";
            if (block["dataTypeOverrides"] is not JsonArray overrides)
                return defaultType;

            var specKind = ReadString(block, "excelSpecKind", "kind")?.ToUpperInvariant();
            var columnOverride = overrides
                .OfType<JsonObject>()
                .Where(item => string.Equals(ReadString(item, "scope"), "COLUMN", StringComparison.OrdinalIgnoreCase) &&
                               ReadInt(item, "index") == columnOffset)
                .Select(item => NormalizeFieldDataType(ReadString(item, "dataType", "type")))
                .LastOrDefault(type => !string.IsNullOrWhiteSpace(type));
            if (specKind == "TOP")
                return columnOverride ?? defaultType;

            var types = new HashSet<string>(StringComparer.Ordinal) { defaultType };
            if (!string.IsNullOrWhiteSpace(columnOverride))
                types.Add(columnOverride);

            if (specKind == "LEFT" && dataRowStart.HasValue && dataRowEnd.HasValue)
            {
                var rowCount = dataRowEnd.Value - dataRowStart.Value + 1;
                var overriddenRows = new HashSet<int>();
                var rowTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in overrides.OfType<JsonObject>())
                {
                    if (!string.Equals(ReadString(item, "scope"), "ROW", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var rowOffset = ReadInt(item, "index");
                    var dataType = NormalizeFieldDataType(ReadString(item, "dataType", "type"));
                    if (!rowOffset.HasValue || rowOffset.Value < 0 || rowOffset.Value >= rowCount || string.IsNullOrWhiteSpace(dataType))
                        continue;

                    overriddenRows.Add(rowOffset.Value);
                    rowTypes.Add(dataType);
                    types.Add(dataType);
                }

                if (overriddenRows.Count == rowCount)
                    return rowTypes.Count == 1 ? rowTypes.First() : "JSON";
                return types.Count == 1 ? types.First() : "JSON";
            }

            var coveredRanges = new List<(int Start, int End)>();
            var rangeTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in overrides.OfType<JsonObject>())
            {
                if (!string.Equals(ReadString(item, "scope"), "RANGE", StringComparison.OrdinalIgnoreCase))
                    continue;

                var c0 = ReadInt(item, "c0", "C0");
                var c1 = ReadInt(item, "c1", "C1");
                var r0 = ReadInt(item, "r0", "R0");
                var r1 = ReadInt(item, "r1", "R1");
                var dataType = NormalizeFieldDataType(ReadString(item, "dataType", "type"));
                if (!c0.HasValue || !c1.HasValue || !r0.HasValue || !r1.HasValue ||
                    absoluteColumn < c0.Value || absoluteColumn > c1.Value ||
                    string.IsNullOrWhiteSpace(dataType))
                {
                    continue;
                }

                rangeTypes.Add(dataType);
                types.Add(dataType);
                if (dataRowStart.HasValue && dataRowEnd.HasValue)
                {
                    var start = Math.Max(dataRowStart.Value, r0.Value);
                    var end = Math.Min(dataRowEnd.Value, r1.Value);
                    if (start <= end)
                        coveredRanges.Add((start, end));
                }
            }

            if (specKind == "MATRIX" &&
                dataRowStart.HasValue &&
                dataRowEnd.HasValue &&
                IntervalsCover(coveredRanges, dataRowStart.Value, dataRowEnd.Value))
            {
                return rangeTypes.Count == 1 ? rangeTypes.First() : "JSON";
            }

            return types.Count == 1 ? types.First() : "JSON";
        }

        private static bool IntervalsCover(
            IEnumerable<(int Start, int End)> intervals,
            int requiredStart,
            int requiredEnd)
        {
            var next = requiredStart;
            foreach (var interval in intervals.OrderBy(item => item.Start).ThenBy(item => item.End))
            {
                if (interval.Start > next)
                    return false;
                next = Math.Max(next, interval.End + 1);
                if (next > requiredEnd)
                    return true;
            }

            return next > requiredEnd;
        }

        private static bool ColumnHasInputCell(
            JsonObject block,
            int absoluteColumn,
            int dataRowStart,
            int dataRowEnd)
        {
            if (block["specialRanges"] is not JsonArray ranges)
                return true;

            var maskedIntervals = new List<(int Start, int End)>();
            foreach (var item in ranges.OfType<JsonObject>())
            {
                var role = NormalizeNonInputRangeRole(ReadString(item, "role", "kind", "type"));
                if (role is null)
                    continue;

                var r0 = ReadInt(item, "r0", "R0");
                var c0 = ReadInt(item, "c0", "C0");
                var r1 = ReadInt(item, "r1", "R1");
                var c1 = ReadInt(item, "c1", "C1");
                if (!r0.HasValue || !c0.HasValue || !r1.HasValue || !c1.HasValue ||
                    r1.Value < r0.Value || c1.Value < c0.Value ||
                    absoluteColumn < c0.Value || absoluteColumn > c1.Value)
                {
                    continue;
                }

                var start = Math.Max(dataRowStart, r0.Value);
                var end = Math.Min(dataRowEnd, r1.Value);
                if (start <= end)
                    maskedIntervals.Add((start, end));
            }

            var nextInputRow = dataRowStart;
            foreach (var interval in maskedIntervals.OrderBy(item => item.Start).ThenBy(item => item.End))
            {
                if (interval.Start > nextInputRow)
                    return true;
                nextInputRow = Math.Max(nextInputRow, interval.End + 1);
                if (nextInputRow > dataRowEnd)
                    return false;
            }

            return nextInputRow <= dataRowEnd;
        }

        private static string? NormalizeNonInputRangeRole(string? value)
        {
            var role = value?.Trim().ToUpperInvariant();
            if (role == "FORMULAR")
                role = "FORMULA";
            if (role == "HEADER")
                role = "TITLE";
            if (role is "STYLE" or "EMPTY" or "EMPTY_INPUT")
                role = "BLANK";
            return role is "FORMULA" or "TITLE" or "BLANK" ? role : null;
        }

        private static JsonArray ParseArray(string json, string fieldName)
        {
            try
            {
                return JsonNode.Parse(json) as JsonArray
                    ?? throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { field = fieldName, reason = "DYNAMIC_FLOW_FORM_REFERENCE_ARRAY_REQUIRED" });
            }
            catch (JsonException ex)
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_FORM_REFERENCE_JSON_INVALID", ex.Message });
            }
        }

        private static string? NormalizeColumnKey(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static void AddIfNotBlank(ISet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value.Trim());
    }
}
