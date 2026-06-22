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

        foreach (var policy in ReadPolicyArray(root, "fieldPolicies"))
        {
            if (!PolicyMatches(policy, context))
                continue;

            var fieldId = ReadString(policy, "fieldId");
            var fieldKey = ReadString(policy, "fieldKey");
            var targetKey = FirstNonBlank(fieldId, fieldKey);
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

        foreach (var policy in ReadPolicyArray(root, "tableColumnPolicies"))
        {
            if (!PolicyMatches(policy, context))
                continue;

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

    private static bool ScopeMatches(string? policyValue, string? contextValue)
        => string.IsNullOrWhiteSpace(policyValue) ||
           string.Equals(policyValue, "*", StringComparison.Ordinal) ||
           (!string.IsNullOrWhiteSpace(contextValue) &&
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
}

internal static class DynamicFlowPolicyValidator
{
    public static void ValidatePayload(JsonObject root, DynamicFormTemplate? dynamicFormTemplate)
    {
        var refs = dynamicFormTemplate is null
            ? null
            : DynamicFormPolicyReferenceIndex.From(dynamicFormTemplate);
        var stepRefs = DynamicFlowStepReferenceIndex.From(root);

        ValidateActorPolicies(root, stepRefs);
        ValidateFieldPolicies(root, stepRefs, refs);
        ValidateTableColumnPolicies(root, stepRefs, refs);
        ValidateMappingRules(root, stepRefs, refs);
    }

    private static void ValidateActorPolicies(JsonObject root, DynamicFlowStepReferenceIndex stepRefs)
    {
        var index = 0;
        foreach (var policy in ReadRequiredObjectArray(root, "actorPolicies"))
        {
            ValidateStepScope(policy, stepRefs, $"actorPolicies[{index}]");
            EnsureBooleanIfPresent(policy, $"actorPolicies[{index}]", "allowSubFlow", "allowForward", "canFinalize");
            index++;
        }
    }

    private static void ValidateFieldPolicies(
        JsonObject root,
        DynamicFlowStepReferenceIndex stepRefs,
        DynamicFormPolicyReferenceIndex? refs)
    {
        var index = 0;
        foreach (var policy in ReadRequiredObjectArray(root, "fieldPolicies"))
        {
            var fieldName = $"fieldPolicies[{index}]";
            ValidateStepScope(policy, stepRefs, fieldName);
            EnsureBooleanIfPresent(policy, fieldName, "read", "canRead", "write", "canWrite", "required", "isRequired",
                "hidden", "isHidden", "locked", "isLocked", "lockedAfterSubmit");

            var fieldId = ReadString(policy, "fieldId");
            var fieldKey = ReadString(policy, "fieldKey");
            if (string.IsNullOrWhiteSpace(fieldId) && string.IsNullOrWhiteSpace(fieldKey))
            {
                throw AppExceptionFactory.BadRequest(
                    AppErrorCode.COMMON_VALIDATION_FAILED,
                    new { field = fieldName, reason = "DYNAMIC_FLOW_FIELD_POLICY_TARGET_REQUIRED" });
            }

            if (refs is not null)
                refs.EnsureFieldExists(fieldName, fieldId, fieldKey);

            index++;
        }
    }

    private static void ValidateTableColumnPolicies(
        JsonObject root,
        DynamicFlowStepReferenceIndex stepRefs,
        DynamicFormPolicyReferenceIndex? refs)
    {
        var index = 0;
        foreach (var policy in ReadRequiredObjectArray(root, "tableColumnPolicies"))
        {
            var fieldName = $"tableColumnPolicies[{index}]";
            ValidateStepScope(policy, stepRefs, fieldName);
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

            refs?.EnsureTableColumnExists(fieldName, blockId, columnKey);
            index++;
        }
    }

    private static void ValidateMappingRules(
        JsonObject root,
        DynamicFlowStepReferenceIndex stepRefs,
        DynamicFormPolicyReferenceIndex? refs)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
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

            var sourceFieldId = ReadString(rule, "sourceFieldId");
            var sourceFieldKey = ReadString(rule, "sourceFieldKey");
            var sourceBlockId = ReadString(rule, "sourceBlockId");
            var sourceColumnKey = ReadString(rule, "sourceColumnKey");
            var targetFieldId = ReadString(rule, "targetFieldId");
            var targetFieldKey = ReadString(rule, "targetFieldKey");
            var targetBlockId = ReadString(rule, "targetBlockId");
            var targetColumnKey = ReadString(rule, "targetColumnKey");

            var hasSourceField = !string.IsNullOrWhiteSpace(sourceFieldId) || !string.IsNullOrWhiteSpace(sourceFieldKey);
            var hasSourceTable = !string.IsNullOrWhiteSpace(sourceBlockId) || !string.IsNullOrWhiteSpace(sourceColumnKey);
            var hasTargetField = !string.IsNullOrWhiteSpace(targetFieldId) || !string.IsNullOrWhiteSpace(targetFieldKey);
            var hasTargetTable = !string.IsNullOrWhiteSpace(targetBlockId) || !string.IsNullOrWhiteSpace(targetColumnKey);

            if (!hasSourceField && !hasSourceTable)
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

            if (!hasTargetField && !hasTargetTable)
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

            if (refs is not null)
            {
                if (hasSourceField)
                    refs.EnsureFieldExists(fieldName, sourceFieldId, sourceFieldKey);
                if (hasTargetField)
                    refs.EnsureFieldExists(fieldName, targetFieldId, targetFieldKey);
                if (hasSourceTable)
                    refs.EnsureTableColumnExists(fieldName, sourceBlockId!, sourceColumnKey!);
                if (hasTargetTable)
                    refs.EnsureTableColumnExists(fieldName, targetBlockId!, targetColumnKey!);
            }

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

    private sealed class DynamicFlowStepReferenceIndex
    {
        public HashSet<string> StepIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StepCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static DynamicFlowStepReferenceIndex From(JsonObject root)
        {
            var result = new DynamicFlowStepReferenceIndex();
            if (root["steps"] is not JsonArray steps)
                return result;

            foreach (var item in steps)
            {
                if (item is not JsonObject step)
                    continue;

                AddIfNotBlank(result.StepIds, ReadString(step, "stepId", "id"));
                AddIfNotBlank(result.StepCodes, ReadString(step, "stepCode", "code"));
            }

            return result;
        }
    }

    private sealed class DynamicFormPolicyReferenceIndex
    {
        private readonly HashSet<string> _fieldIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fieldKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _tableColumnsByBlockId = new(StringComparer.Ordinal);

        public static DynamicFormPolicyReferenceIndex From(DynamicFormTemplate template)
        {
            var result = new DynamicFormPolicyReferenceIndex();
            result.ReadFields(template.FieldsJson);
            result.ReadBlocks(template.BlocksJson);
            return result;
        }

        public void EnsureFieldExists(string fieldName, string? fieldId, string? fieldKey)
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
        }

        public void EnsureTableColumnExists(string fieldName, string blockId, string columnKey)
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

                AddIfNotBlank(_fieldIds, ReadString(field, "id"));
                AddIfNotBlank(_fieldKeys, ReadString(field, "key"));
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
                AddColumnsFromArray(block, columns, "statisticColumns");
                AddColumnsFromArray(block, columns, "columns");
                AddColumnsFromArray(block, columns, "indexMap");
                AddColumnsFromArray(block, columns, "columnDefinitions");
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

        private static void AddColumnsFromArray(JsonObject block, ISet<string> columns, string propertyName)
        {
            if (block[propertyName] is not JsonArray array)
                return;

            foreach (var item in array)
            {
                if (item is not JsonObject column)
                    continue;

                AddIfNotBlank(columns, NormalizeColumnKey(ReadString(
                    column,
                    "columnKey",
                    "key",
                    "id",
                    "columnInstanceId",
                    "header") ?? ReadColumnIndexKey(column)));
            }
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

        private static string? ReadColumnIndexKey(JsonObject column)
        {
            if (!column.TryGetPropertyValue("columnIndex", out var node) ||
                node is not JsonValue value ||
                !value.TryGetValue<int>(out var index) ||
                index < 0)
            {
                return null;
            }

            return $"col_{index + 1}";
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
