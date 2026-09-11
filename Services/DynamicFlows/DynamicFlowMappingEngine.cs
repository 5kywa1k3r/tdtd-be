using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services;

namespace tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowMappingTableFailureReasons
{
    public const string FixedGridIdentityInvalid =
        "DYNAMIC_FLOW_MAPPING_FIXED_GRID_IDENTITY_INVALID";
    public const string DuplicateRowKey =
        "DYNAMIC_FLOW_MAPPING_DUPLICATE_ROW_KEY";
    public const string MatrixCoordinateInvalid =
        "DYNAMIC_FLOW_MAPPING_MATRIX_COORDINATE_INVALID";
    public const string GroupIntentionalBlock =
        "DYNAMIC_FLOW_MAPPING_GROUP_INTENTIONAL_BLOCK";
    public const string CustomJoinKeyIntentionalBlock =
        "DYNAMIC_FLOW_MAPPING_CUSTOM_JOIN_KEY_INTENTIONAL_BLOCK";
    public const string AppendColumnsTargetIntentionalBlock =
        "DYNAMIC_FLOW_MAPPING_APPEND_COLUMNS_TARGET_INTENTIONAL_BLOCK";
    public const string ScalarToRowIntentionalBlock =
        "DYNAMIC_FLOW_MAPPING_SCALAR_TO_ROW_INTENTIONAL_BLOCK";
    public const string RowToReportIntentionalBlock =
        "DYNAMIC_FLOW_MAPPING_ROW_TO_REPORT_INTENTIONAL_BLOCK";
    public const string SummaryTemplateReadOnly =
        "DYNAMIC_FLOW_MAPPING_SUMMARY_TEMPLATE_READ_ONLY";
}

internal static class DynamicFlowMappingEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static List<DynamicFlowMappingRuleDto> ReadRulesFromPayloadJson(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new List<DynamicFlowMappingRuleDto>();

        var normalizedPayload = DynamicFlowTemplateService.NormalizePayloadJson(payloadJson, requireLockable: false);
        var root = JsonNode.Parse(normalizedPayload) as JsonObject;
        if (root?["mappingRules"] is not JsonArray rules)
            return new List<DynamicFlowMappingRuleDto>();

        return JsonSerializer.Deserialize<List<DynamicFlowMappingRuleDto>>(rules.ToJsonString(JsonOptions), JsonOptions)
               ?? new List<DynamicFlowMappingRuleDto>();
    }

    public static List<DynamicFlowMappingRuleDto> ReadRulesFromJson(string? mappingRulesJson)
    {
        if (string.IsNullOrWhiteSpace(mappingRulesJson))
            return new List<DynamicFlowMappingRuleDto>();

        var node = JsonNode.Parse(mappingRulesJson);
        if (node is JsonObject root && root["mappingRules"] is JsonArray nested)
            node = nested;

        return node is JsonArray array
            ? JsonSerializer.Deserialize<List<DynamicFlowMappingRuleDto>>(array.ToJsonString(JsonOptions), JsonOptions)
              ?? new List<DynamicFlowMappingRuleDto>()
            : new List<DynamicFlowMappingRuleDto>();
    }

    public static DynamicFlowMappingPreviewResponse Preview(
        WorkAssignmentReport targetReport,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        IReadOnlyList<DynamicFlowMappingRuleDto> rules,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DateTime nowUtc,
        string? targetStepId = null,
        string? targetStepCode = null,
        bool enforceP7Contract = false)
    {
        var targetFields = ParseObject(targetReport.FieldValuesJson) ?? new JsonObject();
        var targetValues = EnsureValuesObject(targetFields);
        var targetTables = ParseTableValuesRoot(targetReport.TableValuesJson) ?? new JsonObject();
        EnsureBlocksArray(targetTables);

        var response = new DynamicFlowMappingPreviewResponse
        {
            TargetReportId = targetReport.Id,
            TargetAssignmentId = targetReport.WorkAssignmentId,
            DataOrigin = WorkReportDataOrigin.PartialMapping,
            CumulativeContributionMode = WorkReportCumulativeContributionMode.Exclude,
            SourceReports = sourceReports
                .OrderBy(x => x.Report.Id, StringComparer.Ordinal)
                .Select(x => new DynamicFlowMappingSourceReportDto
            {
                ReportId = x.Report.Id,
                WorkAssignmentId = x.Report.WorkAssignmentId,
                FlowInstanceId = x.RuntimeInstance?.Id,
                ExecutionEpoch = x.RuntimeStep?.ExecutionEpoch,
                StepInstanceId = x.RuntimeStep?.Id,
                BranchId = x.RuntimeStep?.BranchId,
                AttemptNo = x.RuntimeStep?.AttemptNo,
                FlowStepId = x.FlowStepId,
                FlowStepCode = x.FlowStepCode,
                DynamicFormTemplateId = x.Report.DynamicFormTemplateId,
                FormFamilyId = x.RuntimeStep?.FormFamilyId ?? x.Report.DynamicFormFamilyId,
                FormVersionId = x.RuntimeStep?.FormVersionId,
                FormVersionNo = x.RuntimeStep?.FormVersionNo ?? x.Report.DynamicFormVersionNo,
                FormSchemaHash = x.RuntimeStep?.FormSchemaHash ?? x.Report.DynamicFormSchemaHash,
                PayloadRevision = x.Report.PayloadRevision,
                PayloadHash = x.Report.PayloadHash,
                LifecycleRevision = x.Report.LifecycleRevision,
                LifecycleStatus = x.Report.Status.ToString().ToUpperInvariant(),
                PeriodInstanceKey = x.Report.PeriodInstanceKey
            }).ToList()
        };

        foreach (var rule in rules)
        {
            var normalizedRule = NormalizeRule(rule);
            if (enforceP7Contract)
                ValidateP7RuleContract(normalizedRule);
            if (!TargetContextMatches(normalizedRule, targetReport.DynamicFormTemplateId, targetStepId, targetStepCode))
                continue;

            try
            {
                if (normalizedRule.Inputs is { Count: > 0 })
                {
                    ApplyStructuredRule(
                        targetValues,
                        targetTables,
                        sourceReports,
                        normalizedRule,
                        requestConflictPolicy,
                        requestContributionPolicy,
                        response,
                        enforceP7Contract);
                    continue;
                }

                var matchingSources = sourceReports
                    .Where(source => SourceStepMatches(normalizedRule, source))
                    .ToList();
                if (matchingSources.Count == 0)
                {
                    response.Changes.Add(NoSourceChange(normalizedRule, "DYNAMIC_FLOW_MAPPING_SOURCE_REPORT_NOT_FOUND"));
                    continue;
                }

                var sourceValues = matchingSources
                    .SelectMany(source => ExtractSourceValues(normalizedRule, source, enforceP7Contract))
                    .Where(value => !IsBlank(value.Value))
                    .ToList();
                if (sourceValues.Count == 0)
                {
                    response.Changes.Add(NoSourceChange(normalizedRule, "DYNAMIC_FLOW_MAPPING_SOURCE_VALUE_EMPTY"));
                    continue;
                }

                var targetKind = ResolveTargetKind(normalizedRule);
                if (targetKind == "FIELD")
                {
                    var nextValue = TransformForField(sourceValues, normalizedRule);
                    var firstSource = sourceValues[0];
                    ApplyFieldTarget(
                        targetValues,
                        normalizedRule,
                        nextValue,
                        firstSource,
                        requestConflictPolicy,
                        requestContributionPolicy,
                        response);
                    continue;
                }

                var tableValues = TransformForTable(sourceValues, normalizedRule);
                ApplyTableTarget(
                    targetTables,
                    normalizedRule,
                    tableValues,
                    requestConflictPolicy,
                    requestContributionPolicy,
                    response,
                    enforceP7Contract);
            }
            catch (DynamicFlowMappingEvaluationException ex)
            {
                response.Changes.Add(EvaluationErrorChange(normalizedRule, ex.Reason));
            }
        }

        response.FieldValuesJson = targetFields.ToJsonString(JsonOptions);
        response.TableValuesJson = targetTables.ToJsonString(JsonOptions);
        response.CumulativeContributionPolicyJson = BuildContributionPolicyJson(response.Changes, requestContributionPolicy);
        response.SummarySourceJson = BuildSummarySourceJson(
            targetReport,
            sourceReports,
            rules,
            response.Changes);
        response.HasBlockingConflicts = response.Changes.Any(x => string.Equals(x.Status, "CONFLICT", StringComparison.Ordinal));
        return response;
    }

    private static DynamicFlowMappingRuleDto NormalizeRule(DynamicFlowMappingRuleDto rule)
    {
        var inputs = (rule.Inputs ?? new List<DynamicFlowMappingInputDto>())
            .Where(input => input is not null)
            .Select(NormalizeInput)
            .ToList();
        var target = rule.Target is null ? null : NormalizeEndpoint(rule.Target);
        var calculation = rule.Calculation is null ? null : NormalizeCalculation(rule.Calculation);
        var firstSource = inputs.FirstOrDefault()?.Source;

        return new DynamicFlowMappingRuleDto
        {
            MappingId = NormalizeText(rule.MappingId) ?? string.Empty,
            MappingVersion = rule.MappingVersion <= 0 ? 1 : rule.MappingVersion,
            MappingKind = NormalizeUpper(rule.MappingKind) ?? target?.Kind,
            SourceDynamicFormTemplateId = NormalizeText(rule.SourceDynamicFormTemplateId) ?? firstSource?.DynamicFormTemplateId,
            SourceStepId = NormalizeText(rule.SourceStepId) ?? firstSource?.StepId,
            SourceStepCode = NormalizeText(rule.SourceStepCode) ?? firstSource?.StepCode,
            SourceSectionId = NormalizeText(rule.SourceSectionId) ?? firstSource?.SectionId,
            SourceSectionCode = NormalizeText(rule.SourceSectionCode) ?? firstSource?.SectionCode,
            SourceFieldId = NormalizeText(rule.SourceFieldId) ?? firstSource?.FieldId,
            SourceFieldKey = NormalizeText(rule.SourceFieldKey) ?? firstSource?.FieldKey,
            SourceBlockId = NormalizeText(rule.SourceBlockId) ?? firstSource?.BlockId,
            SourceColumnKey = NormalizeText(rule.SourceColumnKey) ?? firstSource?.ColumnKey,
            TargetDynamicFormTemplateId = NormalizeText(rule.TargetDynamicFormTemplateId) ?? target?.DynamicFormTemplateId,
            TargetStepId = NormalizeText(rule.TargetStepId) ?? target?.StepId,
            TargetStepCode = NormalizeText(rule.TargetStepCode) ?? target?.StepCode,
            TargetSectionId = NormalizeText(rule.TargetSectionId) ?? target?.SectionId,
            TargetSectionCode = NormalizeText(rule.TargetSectionCode) ?? target?.SectionCode,
            TargetFieldId = NormalizeText(rule.TargetFieldId) ?? target?.FieldId,
            TargetFieldKey = NormalizeText(rule.TargetFieldKey) ?? target?.FieldKey,
            TargetBlockId = NormalizeText(rule.TargetBlockId) ?? target?.BlockId,
            TargetColumnKey = NormalizeText(rule.TargetColumnKey) ?? target?.ColumnKey,
            ConceptCode = NormalizeText(rule.ConceptCode),
            DataType = NormalizeUpper(rule.DataType) ?? target?.DataType ?? calculation?.ResultDataType,
            JoinKey = NormalizeText(rule.JoinKey),
            ValueTransform = NormalizeUpper(rule.ValueTransform),
            ConflictPolicy = NormalizeUpper(rule.ConflictPolicy),
            ContributionPolicy = NormalizeUpper(rule.ContributionPolicy),
            EvaluationGrain = NormalizeUpper(rule.EvaluationGrain) ?? "FLOW_INSTANCE",
            ErrorPolicy = NormalizeUpper(rule.ErrorPolicy) ?? "BLOCK_APPLY",
            Inputs = inputs,
            Target = target,
            Calculation = calculation
        };
    }

    internal static void ValidateP7Rules(IReadOnlyCollection<DynamicFlowMappingRuleDto> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_RULES_REQUIRED");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceRule in rules)
        {
            var rule = NormalizeRule(sourceRule);
            if (string.IsNullOrWhiteSpace(rule.MappingId) ||
                rule.MappingVersion < 1 ||
                !identities.Add($"{rule.MappingId}@{rule.MappingVersion}"))
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_RULE_IDENTITY_INVALID",
                    rule.MappingId);
            }

            ValidateP7RuleContract(rule);
        }
    }

    private static void ValidateP7RuleContract(DynamicFlowMappingRuleDto rule)
    {
        RequireExactScope(rule.TargetDynamicFormTemplateId, "target.dynamicFormTemplateId");
        RequireExactScope(rule.TargetStepId, "target.stepId");

        var targetKind = ResolveTargetKind(rule);
        var targetIdentity = targetKind == "FIELD"
            ? FirstNonBlank(rule.TargetFieldId, rule.TargetFieldKey)
            : string.IsNullOrWhiteSpace(rule.TargetBlockId) ||
              string.IsNullOrWhiteSpace(rule.TargetColumnKey)
                ? null
                : $"{rule.TargetBlockId}:{rule.TargetColumnKey}";
        if (string.IsNullOrWhiteSpace(targetIdentity))
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_TARGET_IDENTITY_REQUIRED",
                rule.MappingId);
        }

        if (!string.IsNullOrWhiteSpace(rule.JoinKey) &&
            !string.Equals(rule.JoinKey, "ROW_KEY", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rule.JoinKey, "CANONICAL_ROW_KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingTableFailureReasons.CustomJoinKeyIntentionalBlock,
                rule.JoinKey);
        }

        var grain = NormalizeUpper(rule.EvaluationGrain) ?? "FLOW_INSTANCE";
        if (grain == "GROUP")
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingTableFailureReasons.GroupIntentionalBlock);
        }
        if (grain is not "FLOW_INSTANCE" and not "SOURCE_REPORT" and not "TABLE_ROW")
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_EVALUATION_GRAIN_UNSUPPORTED",
                grain);
        }
        if (targetKind == "FIELD" && grain is "SOURCE_REPORT" or "TABLE_ROW")
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingTableFailureReasons.RowToReportIntentionalBlock,
                rule.MappingId);
        }
        if (targetKind == "TABLE_COLUMN" && grain == "FLOW_INSTANCE")
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingTableFailureReasons.ScalarToRowIntentionalBlock,
                rule.MappingId);
        }

        var inputs = rule.Inputs ?? new List<DynamicFlowMappingInputDto>();
        if (inputs.Count == 0)
        {
            RequireExactScope(rule.SourceDynamicFormTemplateId, "source.dynamicFormTemplateId");
            RequireExactScope(rule.SourceStepId, "source.stepId");
        }
        else
        {
            var inputKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input.InputKey) ||
                    !inputKeys.Add(input.InputKey))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_INPUT_IDENTITY_INVALID",
                        input.InputKey);
                }

                if (string.Equals(input.Source.Kind, "CONSTANT", StringComparison.Ordinal))
                    continue;
                if (input.Source.Kind is not "FIELD" and not "TABLE_COLUMN")
                {
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_SOURCE_KIND_UNSUPPORTED",
                        input.Source.Kind);
                }
                RequireExactScope(
                    input.Source.DynamicFormTemplateId,
                    $"inputs.{input.InputKey}.dynamicFormTemplateId");
                RequireExactScope(
                    input.Source.StepId,
                    $"inputs.{input.InputKey}.stepId");
            }
        }

        var dataType = NormalizeUpper(rule.DataType ?? rule.Target?.DataType);
        if (dataType is not null &&
            dataType is not "TEXT" and
            not "SHORT_TEXT" and
            not "LONG_TEXT" and
            not "DATE" and
            not "FULL_DATE" and
            not "NUMBER" and
            not "BOOLEAN" and
            not "CHOICE" and
            not "SINGLE_SELECT" and
            not "MULTI_SELECT" and
            not "JSON")
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_TARGET_TYPE_UNSUPPORTED",
                dataType);
        }
    }

    private static void RequireExactScope(string? value, string scope)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "*", StringComparison.Ordinal))
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_EXACT_SCOPE_REQUIRED",
                scope);
        }
    }

    private static DynamicFlowMappingInputDto NormalizeInput(DynamicFlowMappingInputDto input)
        => new()
        {
            InputKey = NormalizeText(input.InputKey) ?? string.Empty,
            Source = NormalizeEndpoint(input.Source ?? new DynamicFlowMappingEndpointDto()),
            DataType = NormalizeUpper(input.DataType),
            Cardinality = NormalizeUpper(input.Cardinality) switch
            {
                "COLLECTION" => "MANY",
                "SCALAR" => "ONE",
                "MANY" => "MANY",
                _ => "ONE"
            },
            NullPolicy = NormalizeUpper(input.NullPolicy) ?? "KEEP_NULL",
            ConstantValue = input.ConstantValue?.DeepClone()
        };

    private static DynamicFlowMappingEndpointDto NormalizeEndpoint(DynamicFlowMappingEndpointDto endpoint)
        => new()
        {
            Kind = NormalizeUpper(endpoint.Kind),
            DynamicFormTemplateId = NormalizeText(endpoint.DynamicFormTemplateId),
            StepId = NormalizeText(endpoint.StepId),
            StepCode = NormalizeText(endpoint.StepCode),
            SectionId = NormalizeText(endpoint.SectionId),
            SectionCode = NormalizeText(endpoint.SectionCode),
            FieldId = NormalizeText(endpoint.FieldId),
            FieldKey = NormalizeText(endpoint.FieldKey),
            BlockId = NormalizeText(endpoint.BlockId),
            ColumnKey = NormalizeText(endpoint.ColumnKey),
            RowKey = NormalizeText(endpoint.RowKey),
            DataType = NormalizeUpper(endpoint.DataType)
        };

    private static DynamicFlowMappingCalculationDto NormalizeCalculation(DynamicFlowMappingCalculationDto calculation)
        => new()
        {
            Kind = NormalizeUpper(calculation.Kind) ?? "DIRECT",
            Operation = NormalizeText(calculation.Operation),
            ResultDataType = NormalizeUpper(calculation.ResultDataType),
            Expression = calculation.Expression?.DeepClone(),
            FunctionCode = NormalizeUpper(calculation.FunctionCode),
            FunctionVersion = calculation.FunctionVersion,
            Arguments = calculation.Arguments?.ToDictionary(
                item => item.Key.Trim(),
                item => item.Value?.DeepClone(),
                StringComparer.Ordinal),
            DefaultValue = calculation.DefaultValue?.DeepClone()
        };

    private static void ApplyStructuredRule(
        JsonObject targetValues,
        JsonObject targetTables,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        DynamicFlowMappingRuleDto rule,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response,
        bool enforceP7Contract)
    {
        var evaluatedValues = EvaluateStructuredRule(rule, sourceReports, enforceP7Contract);
        if (evaluatedValues.Count == 0)
        {
            response.Changes.Add(NoSourceChange(rule, "DYNAMIC_FLOW_MAPPING_SOURCE_VALUE_EMPTY"));
            return;
        }

        if (ResolveTargetKind(rule) == "FIELD")
        {
            if (evaluatedValues.Count > 1)
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_TARGET_CARDINALITY_INVALID");
            }

            var value = evaluatedValues[0];
            ApplyFieldTarget(
                targetValues,
                rule,
                value.Value,
                value,
                requestConflictPolicy,
                requestContributionPolicy,
                response);
            return;
        }

        ApplyTableTarget(
            targetTables,
            rule,
            evaluatedValues,
            requestConflictPolicy,
            requestContributionPolicy,
            response,
            enforceP7Contract);
    }

    private static List<SourceValue> EvaluateStructuredRule(
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        bool enforceP7Contract)
    {
        var grain = NormalizeUpper(rule.EvaluationGrain) ?? "FLOW_INSTANCE";
        if (grain == "SOURCE_REPORT")
        {
            if ((rule.Inputs ?? new List<DynamicFlowMappingInputDto>())
                .All(input => string.Equals(input.Source.Kind, "CONSTANT", StringComparison.Ordinal)))
            {
                var constantValue = EvaluateStructuredGroup(
                    rule,
                    Array.Empty<DynamicFlowMappingSourceReport>(),
                    rowKey: null,
                    enforceP7Contract);
                return constantValue is null ? new List<SourceValue>() : new List<SourceValue> { constantValue };
            }

            var relevantSources = sourceReports
                .Where(source => (rule.Inputs ?? new List<DynamicFlowMappingInputDto>())
                    .Any(input => !string.Equals(input.Source.Kind, "CONSTANT", StringComparison.Ordinal) &&
                                  SourceEndpointMatches(input.Source, source)))
                .OrderBy(source => source.Report.Id, StringComparer.Ordinal)
                .ToList();
            return relevantSources
                .Select(source =>
                {
                    var value = EvaluateStructuredGroup(
                        rule,
                        new[] { source },
                        rowKey: null,
                        enforceP7Contract);
                    return value is null
                        ? null
                        : value with
                        {
                            RowKey = source.Report.Id,
                            SourceKey = $"sourceReport:{source.Report.Id}"
                        };
                })
                .Where(value => value is not null)
                .Select(value => value!)
                .ToList();
        }

        if (grain == "TABLE_ROW")
        {
            var rowKeys = ResolveStructuredRowKeys(rule, sourceReports, enforceP7Contract);
            return rowKeys
                .Select(rowKey => EvaluateStructuredGroup(
                    rule,
                    sourceReports,
                    rowKey,
                    enforceP7Contract))
                .Where(value => value is not null)
                .Select(value => value!)
                .ToList();
        }

        var evaluated = EvaluateStructuredGroup(
            rule,
            sourceReports,
            rowKey: null,
            enforceP7Contract);
        return evaluated is null ? new List<SourceValue>() : new List<SourceValue> { evaluated };
    }

    private static List<string> ResolveStructuredRowKeys(
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        bool enforceP7Contract)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sourceReportsByRowKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var rowKeys = new List<string>();
        foreach (var input in rule.Inputs ?? new List<DynamicFlowMappingInputDto>())
        {
            if (!string.Equals(input.Source.Kind, "TABLE_COLUMN", StringComparison.Ordinal))
                continue;

            var resolvedInputValues =
                ResolveInputValues(input, sourceReports, enforceP7Contract);
            if (enforceP7Contract)
            {
                var duplicateRowKey = resolvedInputValues
                    .Where(value => !string.IsNullOrWhiteSpace(value.Value.RowKey))
                    .GroupBy(value => value.Value.RowKey!, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1)
                    ?.Key;
                if (!string.IsNullOrWhiteSpace(duplicateRowKey))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        DynamicFlowMappingTableFailureReasons.DuplicateRowKey,
                        duplicateRowKey);
                }
            }

            foreach (var resolved in resolvedInputValues)
            {
                var rowKey = resolved.Value.RowKey;
                if (string.IsNullOrWhiteSpace(rowKey))
                    continue;

                var sourceReportId = resolved.Source?.Report.Id ?? resolved.Value.SourceReportId;
                if (!string.IsNullOrWhiteSpace(sourceReportId))
                {
                    if (!sourceReportsByRowKey.TryGetValue(rowKey, out var owners))
                    {
                        owners = new HashSet<string>(StringComparer.Ordinal);
                        sourceReportsByRowKey[rowKey] = owners;
                    }

                    owners.Add(sourceReportId);
                    if (owners.Count > 1)
                    {
                        throw new DynamicFlowMappingEvaluationException(
                            enforceP7Contract
                                ? DynamicFlowMappingTableFailureReasons.DuplicateRowKey
                                : "DYNAMIC_FLOW_MAPPING_TABLE_ROW_KEY_AMBIGUOUS",
                            rowKey);
                    }
                }

                if (seen.Add(rowKey))
                    rowKeys.Add(rowKey);
            }
        }

        return enforceP7Contract
            ? rowKeys.OrderBy(rowKey => rowKey, StringComparer.Ordinal).ToList()
            : rowKeys;
    }

    private static SourceValue? EvaluateStructuredGroup(
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        string? rowKey,
        bool enforceP7Contract)
    {
        var inputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var provenance = new List<DynamicFlowMappingInputProvenanceDto>();

        foreach (var input in rule.Inputs ?? new List<DynamicFlowMappingInputDto>())
        {
            var resolvedValues = ResolveInputValues(input, sourceReports, enforceP7Contract)
                .Where(value => rowKey is null ||
                                value.Value.RowKey is null ||
                                string.Equals(value.Value.RowKey, rowKey, StringComparison.Ordinal))
                .ToList();
            if (enforceP7Contract)
            {
                var inputDataType = NormalizeP7FieldDataType(input.DataType);
                foreach (var resolvedValue in resolvedValues)
                {
                    DynamicFlowMappingFieldValueContract.ValidateJsonNative(
                        resolvedValue.Value.Value,
                        inputDataType,
                        allowedChoiceCodes: null,
                        allowNull: true);
                }
            }

            var normalizedValues = ApplyNullPolicy(input, resolvedValues.Select(value => value.Value.Value).ToList());
            if (normalizedValues.SkipEvaluation)
                return null;

            JsonNode? inputValue;
            if (string.Equals(input.Cardinality, "MANY", StringComparison.Ordinal))
            {
                inputValue = new JsonArray(normalizedValues.Values.Select(value => value?.DeepClone()).ToArray());
            }
            else
            {
                if (normalizedValues.Values.Count > 1)
                {
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_INPUT_CARDINALITY_INVALID",
                        input.InputKey);
                }

                inputValue = normalizedValues.Values.FirstOrDefault()?.DeepClone();
            }

            inputs[input.InputKey] = inputValue;
            var provenanceValues = string.Equals(input.NullPolicy, "SKIP", StringComparison.Ordinal)
                ? resolvedValues.Where(value => !IsBlank(value.Value.Value))
                : resolvedValues;
            provenance.AddRange(provenanceValues.Select(value => BuildInputProvenance(input, value)));
        }

        JsonNode? result;
        try
        {
            result = EvaluateCalculation(rule, inputs);
        }
        catch (DynamicFlowMappingEvaluationException) when (
            string.Equals(rule.ErrorPolicy, "USE_DEFAULT", StringComparison.Ordinal) &&
            rule.Calculation?.DefaultValue is not null)
        {
            result = rule.Calculation.DefaultValue.DeepClone();
        }

        if (enforceP7Contract)
        {
            DynamicFlowMappingFieldValueContract.ValidateJsonNative(
                result,
                NormalizeP7FieldDataType(
                    rule.Calculation?.ResultDataType ??
                    rule.Target?.DataType ??
                    rule.DataType),
                allowedChoiceCodes: null,
                allowNull: true);
        }

        var firstSource = provenance.FirstOrDefault();
        return new SourceValue(
            firstSource?.SourceReportId,
            firstSource?.SourceKey ?? string.Join("+", inputs.Keys),
            rowKey,
            null,
            result,
            provenance);
    }

    private static List<ResolvedInputValue> ResolveInputValues(
        DynamicFlowMappingInputDto input,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        bool enforceP7Contract)
    {
        if (string.Equals(input.Source.Kind, "CONSTANT", StringComparison.Ordinal))
        {
            return new List<ResolvedInputValue>
            {
                new(
                    null,
                    new SourceValue(null, $"constant:{input.InputKey}", null, null, input.ConstantValue?.DeepClone()))
            };
        }

        var result = new List<ResolvedInputValue>();
        foreach (var source in sourceReports.Where(source => SourceEndpointMatches(input.Source, source)))
        {
            foreach (var value in ExtractEndpointValues(input.Source, source, enforceP7Contract))
                result.Add(new ResolvedInputValue(source, value));
        }

        return result;
    }

    private static IEnumerable<SourceValue> ExtractEndpointValues(
        DynamicFlowMappingEndpointDto endpoint,
        DynamicFlowMappingSourceReport source,
        bool enforceP7Contract)
    {
        if (string.Equals(endpoint.Kind, "FIELD", StringComparison.Ordinal))
        {
            var values = ExtractFieldValues(source.FieldValuesJson);
            foreach (var key in DistinctKeys(endpoint.FieldId, endpoint.FieldKey))
            {
                if (values.TryGetValue(key, out var value))
                {
                    yield return new SourceValue(source.Report.Id, key, null, null, value?.DeepClone());
                    yield break;
                }
            }

            yield break;
        }

        if (!string.Equals(endpoint.Kind, "TABLE_COLUMN", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(endpoint.BlockId) ||
            string.IsNullOrWhiteSpace(endpoint.ColumnKey))
        {
            yield break;
        }

        foreach (var value in ExtractTableColumnValues(
                     source.Report.Id,
                     source.TableValuesJson,
                     endpoint.BlockId,
                     endpoint.ColumnKey,
                     enforceP7Contract))
        {
            if (string.IsNullOrWhiteSpace(endpoint.RowKey) ||
                string.Equals(endpoint.RowKey, value.RowKey, StringComparison.Ordinal))
            {
                yield return value;
            }
        }
    }

    private static NullPolicyResult ApplyNullPolicy(
        DynamicFlowMappingInputDto input,
        IReadOnlyList<JsonNode?> values)
    {
        var policy = NormalizeUpper(input.NullPolicy) ?? "KEEP_NULL";
        var sourceValues = values.Count == 0 ? new JsonNode?[] { null } : values.ToArray();
        var result = new List<JsonNode?>();
        foreach (var value in sourceValues)
        {
            if (!IsBlank(value))
            {
                result.Add(value?.DeepClone());
                continue;
            }

            switch (policy)
            {
                case "SKIP":
                    continue;
                case "ZERO":
                    result.Add(JsonValue.Create(0m));
                    break;
                case "EMPTY_TEXT":
                    result.Add(JsonValue.Create(string.Empty));
                    break;
                case "ERROR":
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_INPUT_NULL",
                        input.InputKey);
                default:
                    result.Add(null);
                    break;
            }
        }

        return new NullPolicyResult(
            policy == "SKIP" && result.Count == 0,
            result);
    }

    private static JsonNode? EvaluateCalculation(
        DynamicFlowMappingRuleDto rule,
        IReadOnlyDictionary<string, JsonNode?> inputs)
    {
        var calculation = rule.Calculation;
        var kind = NormalizeUpper(calculation?.Kind) ?? "DIRECT";
        if (kind == "EXPRESSION")
            return DynamicFlowMappingExpressionEvaluator.Evaluate(calculation?.Expression, inputs);

        if (kind == "REGISTERED_FUNCTION")
        {
            var arguments = (calculation?.Arguments ?? new Dictionary<string, JsonNode?>())
                .ToDictionary(
                    item => item.Key,
                    item => DynamicFlowMappingExpressionEvaluator.Evaluate(item.Value, inputs),
                    StringComparer.Ordinal);
            return DynamicFlowRegisteredFunctionRegistry.Execute(
                calculation?.FunctionCode,
                calculation?.FunctionVersion,
                arguments);
        }

        var operation = NormalizeText(calculation?.Operation) ?? "copy";
        var expression = new JsonObject
        {
            ["op"] = operation,
            ["args"] = new JsonArray(inputs.Keys.Select(key => (JsonNode)new JsonObject { ["input"] = key }).ToArray())
        };
        return DynamicFlowMappingExpressionEvaluator.Evaluate(expression, inputs);
    }

    private static DynamicFlowMappingInputProvenanceDto BuildInputProvenance(
        DynamicFlowMappingInputDto input,
        ResolvedInputValue resolved)
        => new()
        {
            InputKey = input.InputKey,
            SourceDynamicFormTemplateId = resolved.Source?.Report.DynamicFormTemplateId,
            SourceStepId = resolved.Source?.FlowStepId,
            SourceStepCode = resolved.Source?.FlowStepCode,
            SourceAssignmentId = resolved.Source?.Report.WorkAssignmentId,
            SourceReportId = resolved.Source?.Report.Id,
            SourcePayloadRevision = resolved.Source?.Report.PayloadRevision,
            SourcePayloadHash = resolved.Source?.Report.PayloadHash,
            SourceLifecycleRevision = resolved.Source?.Report.LifecycleRevision,
            SourceKey = resolved.Value.SourceKey,
            RowKey = resolved.Value.RowKey,
            ValueJson = StableJson(resolved.Value.Value)
        };

    private static List<DynamicFlowMappingInputProvenanceDto> ResolveProvenance(SourceValue source)
    {
        if (source.Provenance is { Count: > 0 })
        {
            return source.Provenance.Select(item => new DynamicFlowMappingInputProvenanceDto
            {
                InputKey = item.InputKey,
                SourceDynamicFormTemplateId = item.SourceDynamicFormTemplateId,
                SourceStepId = item.SourceStepId,
                SourceStepCode = item.SourceStepCode,
                SourceAssignmentId = item.SourceAssignmentId,
                SourceReportId = item.SourceReportId,
                SourcePayloadRevision = item.SourcePayloadRevision,
                SourcePayloadHash = item.SourcePayloadHash,
                SourceLifecycleRevision = item.SourceLifecycleRevision,
                SourceKey = item.SourceKey,
                RowKey = item.RowKey,
                ValueJson = item.ValueJson
            }).ToList();
        }

        return new List<DynamicFlowMappingInputProvenanceDto>
        {
            new()
            {
                InputKey = "source",
                SourceReportId = source.SourceReportId,
                SourceKey = source.SourceKey,
                RowKey = source.RowKey,
                ValueJson = StableJson(source.Value)
            }
        };
    }

    private static bool TargetContextMatches(
        DynamicFlowMappingRuleDto rule,
        string? targetDynamicFormTemplateId,
        string? targetStepId,
        string? targetStepCode)
        => ScopeMatches(rule.TargetDynamicFormTemplateId, targetDynamicFormTemplateId, StringComparison.Ordinal)
           && ScopeMatches(rule.TargetStepId, targetStepId, StringComparison.Ordinal)
           && ScopeMatches(rule.TargetStepCode, targetStepCode, StringComparison.OrdinalIgnoreCase);

    private static bool SourceEndpointMatches(
        DynamicFlowMappingEndpointDto endpoint,
        DynamicFlowMappingSourceReport source)
        => ScopeMatches(endpoint.DynamicFormTemplateId, source.Report.DynamicFormTemplateId, StringComparison.Ordinal)
           && ScopeMatches(endpoint.StepId, source.FlowStepId, StringComparison.Ordinal)
           && ScopeMatches(endpoint.StepCode, source.FlowStepCode, StringComparison.OrdinalIgnoreCase);

    private static bool ScopeMatches(string? expected, string? actual, StringComparison comparison)
        => string.IsNullOrWhiteSpace(expected) ||
           string.Equals(expected, "*", StringComparison.Ordinal) ||
           (!string.IsNullOrWhiteSpace(actual) && string.Equals(expected, actual, comparison));

    private static bool SourceStepMatches(DynamicFlowMappingRuleDto rule, DynamicFlowMappingSourceReport source)
    {
        if (!ScopeMatches(
                rule.SourceDynamicFormTemplateId,
                source.Report.DynamicFormTemplateId,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.SourceStepId) &&
            !string.Equals(rule.SourceStepId, "*", StringComparison.Ordinal) &&
            !string.Equals(rule.SourceStepId, source.FlowStepId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.SourceStepCode) &&
            !string.Equals(rule.SourceStepCode, "*", StringComparison.Ordinal) &&
            !string.Equals(rule.SourceStepCode, source.FlowStepCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<SourceValue> ExtractSourceValues(
        DynamicFlowMappingRuleDto rule,
        DynamicFlowMappingSourceReport source,
        bool enforceP7Contract)
    {
        if (!string.IsNullOrWhiteSpace(rule.SourceFieldId) || !string.IsNullOrWhiteSpace(rule.SourceFieldKey))
        {
            var values = ExtractFieldValues(source.FieldValuesJson);
            foreach (var key in DistinctKeys(rule.SourceFieldId, rule.SourceFieldKey))
            {
                if (values.TryGetValue(key, out var value))
                {
                    yield return new SourceValue(source.Report.Id, key, null, null, value?.DeepClone());
                    yield break;
                }
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(rule.SourceBlockId) || string.IsNullOrWhiteSpace(rule.SourceColumnKey))
            yield break;

        foreach (var value in ExtractTableColumnValues(
                     source.Report.Id,
                     source.TableValuesJson,
                     rule.SourceBlockId,
                     rule.SourceColumnKey,
                     enforceP7Contract))
        {
            yield return value;
        }
    }

    private static void ApplyFieldTarget(
        JsonObject targetValues,
        DynamicFlowMappingRuleDto rule,
        JsonNode? nextValue,
        SourceValue source,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response)
    {
        var targetKey = FirstNonBlank(rule.TargetFieldId, rule.TargetFieldKey) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetKey))
            return;

        targetValues.TryGetPropertyValue(targetKey, out var previousValue);
        var conflictPolicy = ResolveConflictPolicy(rule, requestConflictPolicy);
        var status = ResolveWriteStatus(previousValue, nextValue, conflictPolicy, out var reason);
        if (status == "APPLIED")
            targetValues[targetKey] = nextValue?.DeepClone();

        response.Changes.Add(new DynamicFlowMappingChangeDto
        {
            MappingId = rule.MappingId,
            MappingVersion = rule.MappingVersion,
            TargetKind = "FIELD",
            TargetKey = targetKey,
            SourceReportId = source.SourceReportId,
            SourceKey = source.SourceKey,
            PreviousValueJson = StableJson(previousValue),
            NextValueJson = StableJson(nextValue),
            Status = status,
            Reason = reason,
            ConceptCode = rule.ConceptCode,
            ContributionPolicy = ResolveContributionPolicy(rule, requestContributionPolicy),
            Sources = ResolveProvenance(source)
        });
    }

    private static void ApplyTableTarget(
        JsonObject targetTables,
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<SourceValue> values,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response,
        bool enforceP7Contract)
    {
        var blockId = rule.TargetBlockId ?? string.Empty;
        var columnKey = rule.TargetColumnKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(blockId) || string.IsNullOrWhiteSpace(columnKey))
            return;

        var block = enforceP7Contract
            ? FindTableBlock(targetTables, blockId)
              ?? throw new DynamicFlowMappingEvaluationException(
                  "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_BLOCK_NOT_FOUND",
                  blockId)
            : GetOrCreateTableBlock(targetTables, blockId);
        var tableMode = NormalizeUpper(ReadString(block, "tableMode"));
        if (tableMode == "APPEND_COLUMNS")
        {
            throw new DynamicFlowMappingEvaluationException(
                enforceP7Contract
                    ? DynamicFlowMappingTableFailureReasons.AppendColumnsTargetIntentionalBlock
                    : "DYNAMIC_FLOW_MAPPING_APPEND_COLUMNS_ROW_AXIS_REQUIRED",
                blockId);
        }

        if (tableMode == "SUMMARY_TEMPLATE")
        {
            throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingTableFailureReasons.SummaryTemplateReadOnly,
                blockId);
        }

        if (enforceP7Contract)
        {
            if (tableMode is "FIXED_GRID" or "MATRIX")
            {
                if (block["values1D"] is not JsonArray)
                {
                    throw new DynamicFlowMappingEvaluationException(
                        tableMode == "FIXED_GRID"
                            ? DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid
                            : DynamicFlowMappingTableFailureReasons.MatrixCoordinateInvalid,
                        $"{blockId}:values1D");
                }

                ApplyFixedGridTarget(
                    block,
                    rule,
                    values,
                    requestConflictPolicy,
                    requestContributionPolicy,
                    response,
                    enforceP7Contract: true);
                return;
            }

            if (tableMode == "APPEND_ROWS")
            {
                ApplyAppendRowsTarget(
                    block,
                    rule,
                    values,
                    requestConflictPolicy,
                    requestContributionPolicy,
                    response,
                    enforceP7Contract: true);
                return;
            }

            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_TABLE_MODE_UNSUPPORTED",
                tableMode ?? "-");
        }

        var useFixedGrid = block["values1D"] is JsonArray &&
                           (block["valueSlots"] is JsonArray || block["indexMap"] is JsonArray) &&
                           tableMode is not "APPEND_ROWS" and not "APPEND_COLUMNS";
        if (useFixedGrid)
        {
            ApplyFixedGridTarget(
                block,
                rule,
                values,
                requestConflictPolicy,
                requestContributionPolicy,
                response,
                enforceP7Contract);
            return;
        }

        ApplyAppendRowsTarget(
            block,
            rule,
            values,
            requestConflictPolicy,
            requestContributionPolicy,
            response,
            enforceP7Contract);
    }

    private static void ApplyAppendRowsTarget(
        JsonObject block,
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<SourceValue> values,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response,
        bool enforceP7Contract)
    {
        var blockId = rule.TargetBlockId ?? string.Empty;
        var columnKey = rule.TargetColumnKey ?? string.Empty;
        if (enforceP7Contract)
        {
            ValidateSourceRowKeys(values);
            ValidateP7AppendTargetRows(block);
        }

        var rows = EnsureArray(block, "rows");
        var valueArray = block["values1D"] as JsonArray;
        var allTargetSlots = enforceP7Contract
            ? ReadAndValidateP7Slots(
                block,
                DynamicFlowMappingTableFailureReasons.DuplicateRowKey,
                requireSlots: false,
                requireValueAtIndex: false)
            : ReadValueSlots(block);
        var targetSlots = allTargetSlots
            .Where(slot => slot.Index >= 0 && string.Equals(slot.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(slot => slot.Index)
            .ToList();

        for (var index = 0; index < values.Count; index++)
        {
            var source = values[index];
            var row = ResolveAppendRow(
                rows,
                source.RowKey,
                index,
                allowPositionFallback: !enforceP7Contract,
                mappingId: enforceP7Contract ? rule.MappingId : null,
                mappingVersion: enforceP7Contract ? rule.MappingVersion : null);
            var cells = EnsureObject(row, "cells");
            cells.TryGetPropertyValue(columnKey, out var rowPreviousValue);
            var targetSlot = ResolveTargetSlot(
                targetSlots,
                source.RowKey,
                index,
                allowPositionFallback: !enforceP7Contract);
            JsonNode? previousValue = rowPreviousValue;
            if (targetSlot is not null && valueArray is not null)
            {
                while (valueArray.Count <= targetSlot.Index)
                    valueArray.Add(null);
                previousValue = valueArray[targetSlot.Index];
            }

            var conflictPolicy = ResolveConflictPolicy(rule, requestConflictPolicy);
            var status = ResolveWriteStatus(previousValue, source.Value, conflictPolicy, out var reason);
            if (status == "APPLIED")
            {
                cells[columnKey] = source.Value?.DeepClone();
                if (targetSlot is not null && valueArray is not null)
                    valueArray[targetSlot.Index] = source.Value?.DeepClone();
            }
            else if (status == "UNCHANGED" && StableJson(rowPreviousValue) != StableJson(source.Value))
            {
                cells[columnKey] = source.Value?.DeepClone();
            }

            response.Changes.Add(new DynamicFlowMappingChangeDto
            {
                MappingId = rule.MappingId,
                MappingVersion = rule.MappingVersion,
                TargetKind = "TABLE_COLUMN",
                TargetKey = $"{blockId}:{columnKey}",
                SourceReportId = source.SourceReportId,
                SourceKey = source.SourceKey,
                PreviousValueJson = StableJson(previousValue),
                NextValueJson = StableJson(source.Value),
                Status = status,
                Reason = reason,
                ConceptCode = rule.ConceptCode,
                ContributionPolicy = ResolveContributionPolicy(rule, requestContributionPolicy),
                Sources = ResolveProvenance(source)
            });
        }

        if (enforceP7Contract)
            SortP7AppendRows(rows);
    }

    private static void ApplyFixedGridTarget(
        JsonObject block,
        DynamicFlowMappingRuleDto rule,
        IReadOnlyList<SourceValue> values,
        string? requestConflictPolicy,
        string? requestContributionPolicy,
        DynamicFlowMappingPreviewResponse response,
        bool enforceP7Contract)
    {
        var blockId = rule.TargetBlockId ?? string.Empty;
        var columnKey = rule.TargetColumnKey ?? string.Empty;
        var valueArray = EnsureArray(block, "values1D");
        var tableMode = NormalizeUpper(ReadString(block, "tableMode"));
        var identityFailureReason = tableMode == "MATRIX"
            ? DynamicFlowMappingTableFailureReasons.MatrixCoordinateInvalid
            : DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid;
        var allTargetSlots = enforceP7Contract
            ? ReadAndValidateP7Slots(
                block,
                identityFailureReason,
                requireSlots: true,
                requireValueAtIndex: false)
            : ReadValueSlots(block);
        var targetSlots = allTargetSlots
            .Where(slot => string.Equals(slot.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(slot => slot.Index)
            .ToList();
        if (targetSlots.Count == 0)
        {
            throw new DynamicFlowMappingEvaluationException(
                enforceP7Contract
                    ? identityFailureReason
                    : "DYNAMIC_FLOW_MAPPING_TARGET_COLUMN_SLOT_NOT_FOUND",
                $"{blockId}:{columnKey}");
        }

        if (enforceP7Contract)
        {
            ValidateSourceRowKeys(values, identityFailureReason);
            if (tableMode == "MATRIX")
                ValidateP7MatrixCells(block);
        }

        var bindings = new List<(SourceValue Source, TableIndexMapItem TargetSlot)>();
        for (var index = 0; index < values.Count; index++)
        {
            var source = values[index];
            var targetSlot = ResolveTargetSlot(
                targetSlots,
                source.RowKey,
                index,
                allowPositionFallback: !enforceP7Contract);
            if (targetSlot is null)
            {
                throw new DynamicFlowMappingEvaluationException(
                    enforceP7Contract
                        ? identityFailureReason
                        : "DYNAMIC_FLOW_MAPPING_TARGET_ROW_SLOT_NOT_FOUND",
                    source.RowKey ?? index.ToString(CultureInfo.InvariantCulture));
            }

            bindings.Add((source, targetSlot));
        }

        foreach (var binding in bindings)
        {
            var source = binding.Source;
            var targetSlot = binding.TargetSlot;
            var targetIndex = targetSlot.Index;
            while (valueArray.Count <= targetIndex)
                valueArray.Add(null);

            var previousValue = valueArray[targetIndex];
            var conflictPolicy = ResolveConflictPolicy(rule, requestConflictPolicy);
            var status = ResolveWriteStatus(previousValue, source.Value, conflictPolicy, out var reason);
            if (status == "APPLIED")
            {
                valueArray[targetIndex] = source.Value?.DeepClone();
                SyncMatrixCellProjection(block, targetSlot, source.Value);
            }
            else if (status == "UNCHANGED")
            {
                SyncMatrixCellProjection(block, targetSlot, source.Value);
            }

            response.Changes.Add(new DynamicFlowMappingChangeDto
            {
                MappingId = rule.MappingId,
                MappingVersion = rule.MappingVersion,
                TargetKind = "TABLE_COLUMN",
                TargetKey = $"{blockId}:{columnKey}",
                SourceReportId = source.SourceReportId,
                SourceKey = source.SourceKey,
                PreviousValueJson = StableJson(previousValue),
                NextValueJson = StableJson(source.Value),
                Status = status,
                Reason = reason,
                ConceptCode = rule.ConceptCode,
                ContributionPolicy = ResolveContributionPolicy(rule, requestContributionPolicy),
                Sources = ResolveProvenance(source)
            });
        }
    }

    private static JsonNode? TransformForField(List<SourceValue> values, DynamicFlowMappingRuleDto rule)
    {
        var transform = ResolveTransform(rule);
        if (transform == "SUM")
            return JsonValue.Create(values.Sum(x => ReadRequiredDecimal(x.Value, rule.MappingId)));
        if (transform == "COUNT")
            return JsonValue.Create(values.Count(x => !IsBlank(x.Value)));
        if (transform == "TEXT_JOIN")
            return JsonValue.Create(string.Join(", ", values.Select(x => ToDisplayText(x.Value)).Where(x => !string.IsNullOrWhiteSpace(x))));
        if (transform == "JSON")
            return new JsonArray(values.Select(x => x.Value?.DeepClone()).ToArray());

        var first = values.FirstOrDefault(x => !IsBlank(x.Value));
        return first?.Value?.DeepClone();
    }

    private static IReadOnlyList<SourceValue> TransformForTable(List<SourceValue> values, DynamicFlowMappingRuleDto rule)
    {
        var transform = ResolveTransform(rule);
        if (transform is "SUM" or "COUNT" or "TEXT_JOIN" or "JSON")
        {
            return new List<SourceValue>
            {
                values[0] with { Value = TransformForField(values, rule) }
            };
        }

        return values.Select(x => x with { Value = x.Value?.DeepClone() }).ToList();
    }

    private static Dictionary<string, JsonNode?> ExtractFieldValues(string? json)
    {
        var root = ParseObject(json);
        var values = root?["values"] as JsonObject ?? root;
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
            return result;

        foreach (var item in values)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
                result[item.Key.Trim()] = item.Value?.DeepClone();
        }

        return result;
    }

    private static IEnumerable<SourceValue> ExtractTableColumnValues(
        string sourceReportId,
        string? tableValuesJson,
        string blockId,
        string columnKey,
        bool enforceP7Contract)
    {
        var root = ParseTableValuesRoot(tableValuesJson);
        if (root?["blocks"] is not JsonArray blocks)
            yield break;

        foreach (var blockNode in blocks.OfType<JsonObject>())
        {
            var itemBlockId = ReadString(blockNode, "blockId", "id");
            if (!string.Equals(itemBlockId, blockId, StringComparison.Ordinal))
                continue;

            var tableMode = NormalizeUpper(ReadString(blockNode, "tableMode"));
            if (enforceP7Contract)
            {
                switch (tableMode)
                {
                    case "FIXED_GRID":
                        foreach (var value in ExtractFixedGridValues(
                                     sourceReportId,
                                     blockNode,
                                     blockId,
                                     columnKey,
                                     enforceP7Contract: true))
                        {
                            yield return value;
                        }

                        yield break;
                    case "APPEND_ROWS":
                        if (blockNode["rows"] is JsonArray rowsProjection &&
                            rowsProjection.Count > 0)
                        {
                            foreach (var value in ExtractAppendRowValues(
                                         sourceReportId,
                                         blockNode,
                                         blockId,
                                         columnKey,
                                         enforceP7Contract: true))
                            {
                                yield return value;
                            }
                        }
                        else
                        {
                            foreach (var value in ExtractAppendRowSlotValues(
                                         sourceReportId,
                                         blockNode,
                                         blockId,
                                         columnKey,
                                         enforceP7Contract: true))
                            {
                                yield return value;
                            }
                        }

                        yield break;
                    case "APPEND_COLUMNS":
                        foreach (var value in ExtractAppendColumnValues(
                                     sourceReportId,
                                     blockNode,
                                     blockId,
                                     columnKey,
                                     enforceP7Contract: true))
                        {
                            yield return value;
                        }

                        yield break;
                    case "MATRIX":
                        if (blockNode["values1D"] is JsonArray)
                        {
                            foreach (var value in ExtractFixedGridValues(
                                         sourceReportId,
                                         blockNode,
                                         blockId,
                                         columnKey,
                                         enforceP7Contract: true))
                            {
                                yield return value;
                            }
                        }
                        else
                        {
                            foreach (var value in ExtractMatrixCellValues(
                                         sourceReportId,
                                         blockNode,
                                         blockId,
                                         columnKey,
                                         enforceP7Contract: true))
                            {
                                yield return value;
                            }
                        }

                        yield break;
                    default:
                        throw new DynamicFlowMappingEvaluationException(
                            "DYNAMIC_FLOW_MAPPING_SOURCE_TABLE_MODE_UNSUPPORTED",
                            tableMode ?? "-");
                }
            }

            if (tableMode == "APPEND_ROWS" && blockNode["rows"] is JsonArray rows && rows.Count > 0)
            {
                foreach (var value in ExtractAppendRowValues(
                             sourceReportId,
                             blockNode,
                             blockId,
                             columnKey,
                             enforceP7Contract))
                    yield return value;
                yield break;
            }

            if (tableMode == "APPEND_ROWS" && blockNode["values1D"] is JsonArray)
            {
                foreach (var value in ExtractAppendRowSlotValues(
                             sourceReportId,
                             blockNode,
                             blockId,
                             columnKey,
                             enforceP7Contract))
                    yield return value;
                yield break;
            }

            if (tableMode == "APPEND_COLUMNS" &&
                blockNode["columns"] is JsonArray columns &&
                columns.OfType<JsonObject>().Any(column => string.Equals(
                    ReadString(column, "columnKey", "key", "columnInstanceId"),
                    columnKey,
                    StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var value in ExtractAppendColumnValues(
                             sourceReportId,
                             blockNode,
                             blockId,
                             columnKey,
                             enforceP7Contract: false))
                    yield return value;
                yield break;
            }

            if (blockNode["values1D"] is JsonArray &&
                ReadValueSlots(blockNode, allowLegacy: !enforceP7Contract)
                    .Any(slot => string.Equals(slot.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var value in ExtractFixedGridValues(
                             sourceReportId,
                             blockNode,
                             blockId,
                             columnKey,
                             enforceP7Contract))
                    yield return value;
                yield break;
            }

            foreach (var value in ExtractMatrixCellValues(
                         sourceReportId,
                         blockNode,
                         blockId,
                         columnKey,
                         enforceP7Contract))
                yield return value;
            yield break;
        }
    }

    private static IEnumerable<SourceValue> ExtractFixedGridValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey,
        bool enforceP7Contract)
    {
        if (block["values1D"] is not JsonArray values)
            yield break;

        var identityFailureReason =
            NormalizeUpper(ReadString(block, "tableMode")) == "MATRIX"
                ? DynamicFlowMappingTableFailureReasons.MatrixCoordinateInvalid
                : DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid;
        var slots = enforceP7Contract
            ? ReadAndValidateP7Slots(
                block,
                identityFailureReason,
                requireSlots: true,
                requireValueAtIndex: true)
            : ReadValueSlots(block);
        foreach (var item in slots
                     .Where(x => string.Equals(x.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x.RowKey, StringComparer.Ordinal)
                     .ThenBy(x => x.Index))
        {
            if (item.Index < 0 || item.Index >= values.Count)
                continue;

            yield return new SourceValue(
                sourceReportId,
                $"{blockId}:{columnKey}",
                item.RowKey,
                item.Index,
                values[item.Index]?.DeepClone());
        }
    }

    private static IEnumerable<SourceValue> ExtractAppendRowValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey,
        bool enforceP7Contract)
    {
        if (block["rows"] is not JsonArray rows)
            yield break;

        var indexedRows = rows
            .Select((node, index) => (Row: node as JsonObject, Index: index))
            .Where(item => item.Row is not null)
            .Select(item => (Row: item.Row!, item.Index))
            .ToList();
        if (enforceP7Contract)
        {
            var rowKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in indexedRows)
            {
                var rowKey = ReadString(item.Row, "rowKey", "sourceRowKey");
                if (string.IsNullOrWhiteSpace(rowKey))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_SOURCE_ROW_KEY_REQUIRED",
                        $"{blockId}:{item.Index}");
                }

                if (!rowKeys.Add(rowKey))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        DynamicFlowMappingTableFailureReasons.DuplicateRowKey,
                        rowKey);
                }
            }

            indexedRows = indexedRows
                .OrderBy(item => ReadString(item.Row, "rowKey", "sourceRowKey"), StringComparer.Ordinal)
                .ToList();
        }

        foreach (var item in indexedRows)
        {
            var row = item.Row;
            var index = item.Index;

            JsonNode? value;
            if (row["cells"] is JsonObject cells &&
                TryGetPropertyCaseInsensitive(cells, columnKey, out var cellValue))
            {
                value = cellValue;
            }
            else if (!TryGetPropertyCaseInsensitive(row, columnKey, out value))
            {
                continue;
            }

            var mappingRowKey = ReadString(row, "rowKey", "sourceRowKey", "businessKey");
            if (string.IsNullOrWhiteSpace(mappingRowKey))
            {
                if (enforceP7Contract)
                {
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_SOURCE_ROW_KEY_REQUIRED",
                        $"{blockId}:{index}");
                }

                var rowOrder = ReadInt(row, "rowOrder") ?? index + 1;
                mappingRowKey = $"row_{Math.Max(1, rowOrder)}";
            }
            yield return new SourceValue(
                sourceReportId,
                $"{blockId}:{columnKey}",
                mappingRowKey,
                index,
                value?.DeepClone());
        }
    }

    private static IEnumerable<SourceValue> ExtractAppendRowSlotValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey,
        bool enforceP7Contract)
    {
        if (block["values1D"] is not JsonArray values)
            yield break;

        var slots = enforceP7Contract
            ? ReadAndValidateP7Slots(
                block,
                DynamicFlowMappingTableFailureReasons.DuplicateRowKey,
                requireSlots: true,
                requireValueAtIndex: true)
            : ReadValueSlots(block);
        var rows = slots
            .Where(slot => slot.Index >= 0 && !string.IsNullOrWhiteSpace(slot.RowKey))
            .GroupBy(slot => slot.RowKey!, StringComparer.Ordinal)
            .OrderBy(group => enforceP7Contract ? group.Key : null, StringComparer.Ordinal)
            .ThenBy(group => group.Min(slot => slot.Index));
        foreach (var row in rows)
        {
            var active = row.Any(slot =>
                slot.Index < values.Count &&
                !IsBlank(values[slot.Index]));
            if (!active)
                continue;

            var targetSlot = row.FirstOrDefault(slot => string.Equals(
                slot.ColumnKey,
                columnKey,
                StringComparison.OrdinalIgnoreCase));
            if (targetSlot is null || targetSlot.Index >= values.Count)
                continue;

            yield return new SourceValue(
                sourceReportId,
                $"{blockId}:{columnKey}",
                row.Key,
                targetSlot.Index,
                values[targetSlot.Index]?.DeepClone());
        }
    }

    private static IEnumerable<SourceValue> ExtractAppendColumnValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey,
        bool enforceP7Contract)
    {
        if (block["columns"] is not JsonArray columns)
            yield break;

        var matchingColumns = columns
            .OfType<JsonObject>()
            .Where(column => string.Equals(
                ReadString(column, "columnKey", "key", "columnInstanceId"),
                columnKey,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (enforceP7Contract && matchingColumns.Count > 1)
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_APPEND_COLUMNS_SOURCE_IDENTITY_INVALID",
                $"{blockId}:{columnKey}");
        }

        foreach (var column in matchingColumns)
        {
            if (column["cells"] is not JsonObject cells)
            {
                continue;
            }

            var rowIndex = 0;
            IEnumerable<KeyValuePair<string, JsonNode?>> orderedCells = enforceP7Contract
                ? cells.OrderBy(cell => cell.Key, StringComparer.Ordinal)
                : cells;
            foreach (var cell in orderedCells)
            {
                if (enforceP7Contract && string.IsNullOrWhiteSpace(cell.Key))
                {
                    throw new DynamicFlowMappingEvaluationException(
                        "DYNAMIC_FLOW_MAPPING_APPEND_COLUMNS_SOURCE_IDENTITY_INVALID",
                        $"{blockId}:{columnKey}");
                }

                yield return new SourceValue(
                    sourceReportId,
                    $"{blockId}:{columnKey}",
                    cell.Key,
                    rowIndex++,
                    cell.Value?.DeepClone());
            }
        }
    }

    private static IEnumerable<SourceValue> ExtractMatrixCellValues(
        string sourceReportId,
        JsonObject block,
        string blockId,
        string columnKey,
        bool enforceP7Contract)
    {
        if (block["cells"] is not JsonArray cells)
            yield break;

        if (enforceP7Contract)
            ValidateP7MatrixCells(block);

        var selectedCells = cells
            .OfType<JsonObject>()
            .Where(cell => string.Equals(
                ReadString(cell, "columnKey"),
                columnKey,
                StringComparison.OrdinalIgnoreCase));
        if (enforceP7Contract)
        {
            selectedCells = selectedCells
                .OrderBy(cell => ReadString(cell, "rowKey"), StringComparer.Ordinal)
                .ThenBy(cell => ReadString(cell, "columnKey"), StringComparer.OrdinalIgnoreCase);
        }

        var index = 0;
        foreach (var cell in selectedCells)
        {
            var rowKey = ReadString(cell, "rowKey");
            if (string.IsNullOrWhiteSpace(rowKey) && enforceP7Contract)
            {
                throw new DynamicFlowMappingEvaluationException(
                    DynamicFlowMappingTableFailureReasons.MatrixCoordinateInvalid,
                    $"{blockId}:{columnKey}:{index}");
            }

            yield return new SourceValue(
                sourceReportId,
                $"{blockId}:{columnKey}",
                rowKey ?? index.ToString(CultureInfo.InvariantCulture),
                index++,
                cell["value"]?.DeepClone());
        }
    }

    private static JsonObject? ParseTableValuesRoot(string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return null;

        var expanded = Values1DCompression.ExpandTableValuesJson(tableValuesJson, JsonOptions) ?? tableValuesJson;
        return ParseObject(expanded);
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject EnsureValuesObject(JsonObject root)
    {
        if (root["values"] is JsonObject values)
            return values;

        var next = new JsonObject();
        foreach (var item in root.ToList())
        {
            root.Remove(item.Key);
            next[item.Key] = item.Value;
        }

        root["values"] = next;
        return next;
    }

    private static JsonArray EnsureBlocksArray(JsonObject root)
        => EnsureArray(root, "blocks");

    private static JsonArray EnsureArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonArray array)
            return array;

        array = new JsonArray();
        root[propertyName] = array;
        return array;
    }

    private static JsonObject EnsureObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject obj)
            return obj;

        obj = new JsonObject();
        root[propertyName] = obj;
        return obj;
    }

    private static JsonObject GetOrCreateTableBlock(JsonObject root, string blockId)
    {
        var blocks = EnsureBlocksArray(root);
        var existing = FindTableBlock(root, blockId);
        if (existing is not null)
            return existing;

        var next = new JsonObject
        {
            ["blockId"] = blockId,
            ["tableMode"] = "APPEND_ROWS",
            ["rows"] = new JsonArray()
        };
        blocks.Add(next);
        return next;
    }

    private static JsonObject? FindTableBlock(JsonObject root, string blockId)
        => root["blocks"] is JsonArray blocks
            ? blocks
                .OfType<JsonObject>()
                .FirstOrDefault(block => string.Equals(
                    ReadString(block, "blockId", "id"),
                    blockId,
                    StringComparison.Ordinal))
            : null;

    private static JsonObject ResolveAppendRow(
        JsonArray rows,
        string? rowKey,
        int rowIndex,
        bool allowPositionFallback,
        string? mappingId = null,
        int? mappingVersion = null)
    {
        if (!string.IsNullOrWhiteSpace(mappingId) &&
            mappingVersion is > 0 &&
            !string.IsNullOrWhiteSpace(rowKey))
        {
            var businessKey = BuildP7AppendRowBusinessKey(
                mappingId,
                mappingVersion.Value,
                rowKey);
            var matches = rows
                .OfType<JsonObject>()
                .Where(row => string.Equals(
                    ReadString(row, "businessKey"),
                    businessKey,
                    StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (matches.Count > 1)
            {
                throw new DynamicFlowMappingEvaluationException(
                    DynamicFlowMappingTableFailureReasons.DuplicateRowKey,
                    businessKey);
            }

            return matches.FirstOrDefault() ??
                   AddAppendRow(
                       rows,
                       rowKey,
                       mappingId: mappingId,
                       mappingVersion: mappingVersion);
        }

        if (!string.IsNullOrWhiteSpace(rowKey))
        {
            JsonObject? matched = null;
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index] is not JsonObject row)
                    continue;
                var existing = ReadString(row, "rowKey");
                var rowOrder = ReadInt(row, "rowOrder") ?? index + 1;
                var canonicalRowKey = $"row_{Math.Max(1, rowOrder)}";
                if (string.Equals(existing, rowKey, StringComparison.Ordinal) ||
                    (allowPositionFallback &&
                     string.Equals(canonicalRowKey, rowKey, StringComparison.Ordinal)))
                {
                    if (matched is not null)
                    {
                        throw new DynamicFlowMappingEvaluationException(
                            "DYNAMIC_FLOW_MAPPING_TARGET_ROW_KEY_DUPLICATE",
                            rowKey);
                    }

                    matched = row;
                }
            }

            if (matched is not null)
                return matched;
        }

        if (allowPositionFallback &&
            rowIndex >= 0 &&
            rowIndex < rows.Count &&
            rows[rowIndex] is JsonObject byIndex)
        {
            return byIndex;
        }

        return AddAppendRow(
            rows,
            rowKey,
            preferCanonicalRowOrder: allowPositionFallback);
    }

    private static JsonObject AddAppendRow(
        JsonArray rows,
        string? rowKey,
        bool preferCanonicalRowOrder = false,
        string? mappingId = null,
        int? mappingVersion = null)
    {
        var rowOrder = preferCanonicalRowOrder
            ? ReadCanonicalRowOrder(rowKey) ?? rows.Count + 1
            : rows.Count + 1;
        var row = new JsonObject
        {
            ["rowOrder"] = rowOrder,
            ["cells"] = new JsonObject()
        };
        if (!string.IsNullOrWhiteSpace(rowKey))
            row["rowKey"] = rowKey;
        if (!string.IsNullOrWhiteSpace(mappingId) &&
            mappingVersion is > 0 &&
            !string.IsNullOrWhiteSpace(rowKey))
        {
            row["sourceRowKey"] = rowKey;
            row["mappingId"] = mappingId;
            row["mappingVersion"] = mappingVersion.Value;
            row["businessKey"] = BuildP7AppendRowBusinessKey(
                mappingId,
                mappingVersion.Value,
                rowKey);
        }

        rows.Add(row);
        return row;
    }

    private static string BuildP7AppendRowBusinessKey(
        string mappingId,
        int mappingVersion,
        string sourceRowKey)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"p7:{mappingId.Length}:{mappingId}:{mappingVersion}:{sourceRowKey.Length}:{sourceRowKey}");

    private static void SortP7AppendRows(JsonArray rows)
    {
        var rowObjects = rows.OfType<JsonObject>().ToList();
        var manualRows = rowObjects
            .Where(row => string.IsNullOrWhiteSpace(ReadString(row, "businessKey")))
            .ToList();
        var mappingRows = rowObjects
            .Where(row => !string.IsNullOrWhiteSpace(ReadString(row, "businessKey")))
            .OrderBy(row => ReadString(row, "businessKey"), StringComparer.Ordinal)
            .ToList();

        rows.Clear();
        foreach (var row in manualRows.Concat(mappingRows))
        {
            row["rowOrder"] = rows.Count + 1;
            rows.Add(row);
        }
    }

    private static void ValidateP7AppendTargetRows(JsonObject block)
    {
        if (block["rows"] is null)
            return;
        if (block["rows"] is not JsonArray rows)
        {
            throw new DynamicFlowMappingEvaluationException(
                "DYNAMIC_FLOW_MAPPING_APPEND_ROWS_IDENTITY_INVALID",
                "rows");
        }

        var businessKeys = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var node in rows)
        {
            if (node is not JsonObject row)
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_APPEND_ROWS_IDENTITY_INVALID",
                    index.ToString(CultureInfo.InvariantCulture));
            }

            var businessKey = ReadString(row, "businessKey");
            if (!string.IsNullOrWhiteSpace(businessKey) &&
                !businessKeys.Add(businessKey))
            {
                throw new DynamicFlowMappingEvaluationException(
                    DynamicFlowMappingTableFailureReasons.DuplicateRowKey,
                    businessKey);
            }

            index++;
        }
    }

    private static int? ReadCanonicalRowOrder(string? rowKey)
    {
        if (string.IsNullOrWhiteSpace(rowKey) ||
            !rowKey.StartsWith("row_", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(rowKey.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var rowOrder) ||
            rowOrder <= 0)
        {
            return null;
        }

        return rowOrder;
    }

    private static TableIndexMapItem? ResolveTargetSlot(
        IReadOnlyList<TableIndexMapItem> targetSlots,
        string? rowKey,
        int valueIndex,
        bool allowPositionFallback)
    {
        if (!string.IsNullOrWhiteSpace(rowKey))
        {
            var matches = targetSlots
                .Where(slot => string.Equals(slot.RowKey, rowKey, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            if (matches.Count > 1)
            {
                throw new DynamicFlowMappingEvaluationException(
                    "DYNAMIC_FLOW_MAPPING_TARGET_ROW_SLOT_DUPLICATE",
                    rowKey);
            }

            return matches.FirstOrDefault();
        }

        return allowPositionFallback &&
               valueIndex >= 0 &&
               valueIndex < targetSlots.Count
            ? targetSlots[valueIndex]
            : null;
    }

    private static void SyncMatrixCellProjection(
        JsonObject block,
        TableIndexMapItem targetSlot,
        JsonNode? value)
    {
        if (!string.Equals(NormalizeUpper(ReadString(block, "tableMode")), "MATRIX", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(targetSlot.RowKey) ||
            string.IsNullOrWhiteSpace(targetSlot.ColumnKey))
        {
            return;
        }

        var cells = EnsureArray(block, "cells");
        var cell = cells
            .OfType<JsonObject>()
            .FirstOrDefault(item =>
                string.Equals(ReadString(item, "rowKey"), targetSlot.RowKey, StringComparison.Ordinal) &&
                string.Equals(ReadString(item, "columnKey"), targetSlot.ColumnKey, StringComparison.OrdinalIgnoreCase));
        if (cell is null)
        {
            cell = new JsonObject
            {
                ["rowAxisKey"] = "row",
                ["rowKey"] = targetSlot.RowKey,
                ["columnAxisKey"] = "column",
                ["columnKey"] = targetSlot.ColumnKey
            };
            cells.Add(cell);
        }

        if (!string.IsNullOrWhiteSpace(targetSlot.MetricKey))
            cell["metricKey"] = targetSlot.MetricKey;
        cell["value"] = value?.DeepClone();
    }

    private static List<TableIndexMapItem> ReadValueSlots(
        JsonObject block,
        bool allowLegacy = true)
    {
        var valueSlots = ReadIndexItems(block, "valueSlots");
        if (valueSlots.Count > 0)
            return valueSlots;

        var indexMap = ReadIndexMap(block);
        if (indexMap.Count > 0)
            return indexMap;

        return allowLegacy
            ? BuildLegacyRowMajorSlots(block)
            : new List<TableIndexMapItem>();
    }

    private static List<TableIndexMapItem> ReadAndValidateP7Slots(
        JsonObject block,
        string failureReason,
        bool requireSlots,
        bool requireValueAtIndex)
    {
        var valueSlots = ReadIndexItems(block, "valueSlots");
        var indexMap = ReadIndexMap(block);
        if (valueSlots.Count > 0 &&
            indexMap.Count > 0 &&
            !CanonicalSlotIdentities(valueSlots)
                .SequenceEqual(CanonicalSlotIdentities(indexMap), StringComparer.Ordinal))
        {
            throw new DynamicFlowMappingEvaluationException(
                failureReason,
                "valueSlots:indexMap");
        }

        var slots = valueSlots.Count > 0 ? valueSlots : indexMap;
        if (requireSlots && slots.Count == 0)
        {
            throw new DynamicFlowMappingEvaluationException(
                failureReason,
                "slots:missing");
        }

        var indices = new HashSet<int>();
        var coordinates = new HashSet<string>(StringComparer.Ordinal);
        var values = block["values1D"] as JsonArray;
        foreach (var slot in slots)
        {
            var coordinate = $"{slot.RowKey}\u001f{slot.ColumnKey}";
            if (slot.Index < 0 ||
                string.IsNullOrWhiteSpace(slot.RowKey) ||
                string.IsNullOrWhiteSpace(slot.ColumnKey) ||
                !indices.Add(slot.Index) ||
                !coordinates.Add(coordinate) ||
                requireValueAtIndex && (values is null || slot.Index >= values.Count))
            {
                throw new DynamicFlowMappingEvaluationException(
                    failureReason,
                    coordinate);
            }
        }

        return slots.OrderBy(slot => slot.Index).ToList();
    }

    private static IEnumerable<string> CanonicalSlotIdentities(
        IEnumerable<TableIndexMapItem> slots)
        => slots
            .Select(slot => string.Join(
                "\u001f",
                slot.Index.ToString(CultureInfo.InvariantCulture),
                slot.RowKey,
                slot.ColumnKey,
                slot.MetricKey))
            .OrderBy(identity => identity, StringComparer.Ordinal);

    private static void ValidateSourceRowKeys(
        IReadOnlyList<SourceValue> values,
        string failureReason = DynamicFlowMappingTableFailureReasons.DuplicateRowKey)
    {
        var rowKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var rowKey = values[index].RowKey;
            if (string.IsNullOrWhiteSpace(rowKey))
            {
                throw new DynamicFlowMappingEvaluationException(
                    failureReason == DynamicFlowMappingTableFailureReasons.DuplicateRowKey
                        ? "DYNAMIC_FLOW_MAPPING_SOURCE_ROW_KEY_REQUIRED"
                        : failureReason,
                    index.ToString(CultureInfo.InvariantCulture));
            }

            if (!rowKeys.Add(rowKey))
            {
                throw new DynamicFlowMappingEvaluationException(
                    failureReason,
                    rowKey);
            }
        }
    }

    private static void ValidateP7MatrixCells(JsonObject block)
    {
        if (block["cells"] is not JsonArray cells)
            return;

        var coordinates = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var node in cells)
        {
            if (node is not JsonObject cell)
            {
                throw new DynamicFlowMappingEvaluationException(
                    DynamicFlowMappingTableFailureReasons.MatrixCoordinateInvalid,
                    index.ToString(CultureInfo.InvariantCulture));
            }

            var rowKey = ReadString(cell, "rowKey");
            var columnKey = ReadString(cell, "columnKey");
            var coordinate = $"{rowKey}\u001f{columnKey}";
            if (string.IsNullOrWhiteSpace(rowKey) ||
                string.IsNullOrWhiteSpace(columnKey) ||
                !coordinates.Add(coordinate))
            {
                throw new DynamicFlowMappingEvaluationException(
                    DynamicFlowMappingTableFailureReasons.MatrixCoordinateInvalid,
                    coordinate);
            }

            index++;
        }
    }

    private static List<TableIndexMapItem> BuildLegacyRowMajorSlots(JsonObject block)
    {
        if (ReadBool(block, "hasSpecialRanges") == true || block["values1D"] is not JsonArray values)
            return new List<TableIndexMapItem>();

        var dataRect = block["dataRect"] as JsonObject;
        var width = dataRect is null
            ? ReadInt(block, "w", "W") ?? 0
            : (ReadInt(dataRect, "c1") ?? -1) - (ReadInt(dataRect, "c0") ?? 0) + 1;
        if (width <= 0)
            return new List<TableIndexMapItem>();

        var result = new List<TableIndexMapItem>();
        for (var index = 0; index < values.Count; index++)
        {
            var rowOffset = index / width;
            var columnOffset = index % width;
            result.Add(new TableIndexMapItem(
                index,
                $"row_{rowOffset + 1}",
                $"col_{columnOffset + 1}",
                null));
        }

        return result;
    }

    private static List<TableIndexMapItem> ReadIndexMap(JsonObject block)
        => ReadIndexItems(block, "indexMap");

    private static List<TableIndexMapItem> ReadIndexItems(JsonObject block, string propertyName)
    {
        var result = new List<TableIndexMapItem>();
        if (block[propertyName] is not JsonArray indexMap)
            return result;

        foreach (var item in indexMap.OfType<JsonObject>())
        {
            var index = ReadInt(item, "index", "i") ?? -1;
            result.Add(new TableIndexMapItem(
                index,
                ReadString(item, "rowKey", "row", "r"),
                ReadString(item, "columnKey", "column", "col", "c"),
                ReadString(item, "metricKey", "metric")));
        }

        return result;
    }

    private static string ResolveTargetKind(DynamicFlowMappingRuleDto rule)
        => !string.IsNullOrWhiteSpace(rule.TargetFieldId) || !string.IsNullOrWhiteSpace(rule.TargetFieldKey)
            ? "FIELD"
            : "TABLE_COLUMN";

    private static string ResolveTransform(DynamicFlowMappingRuleDto rule)
        => NormalizeUpper(rule.ValueTransform) switch
        {
            "FIRST" => "FIRST_NON_BLANK",
            "SUM" => "SUM",
            "COUNT" => "COUNT",
            "TEXT_JOIN" => "TEXT_JOIN",
            "JSON" => "JSON",
            _ => "COPY"
        };

    private static string ResolveConflictPolicy(DynamicFlowMappingRuleDto rule, string? requestConflictPolicy)
    {
        var policy = NormalizeUpper(rule.ConflictPolicy) ?? NormalizeUpper(requestConflictPolicy);
        return policy switch
        {
            "TARGET_WINS" => "TARGET_WINS",
            "KEEP_TARGET" => "TARGET_WINS",
            "ERROR" => "ERROR_ON_CONFLICT",
            "ERROR_ON_CONFLICT" => "ERROR_ON_CONFLICT",
            "APPEND" => "APPEND",
            "MERGE" => "MERGE",
            "SOURCE_WINS" => "OVERWRITE",
            _ => "OVERWRITE"
        };
    }

    private static string ResolveContributionPolicy(DynamicFlowMappingRuleDto rule, string? requestContributionPolicy)
        => NormalizeUpper(rule.ContributionPolicy) ?? NormalizeUpper(requestContributionPolicy) ?? "EXCLUDE";

    private static string ResolveWriteStatus(
        JsonNode? previousValue,
        JsonNode? nextValue,
        string conflictPolicy,
        out string? reason)
    {
        reason = null;
        if (StableJson(previousValue) == StableJson(nextValue))
            return "UNCHANGED";

        if (!IsBlank(previousValue) && !IsBlank(nextValue))
        {
            if (conflictPolicy == "TARGET_WINS")
            {
                reason = "DYNAMIC_FLOW_MAPPING_TARGET_VALUE_KEPT";
                return "SKIPPED";
            }

            if (conflictPolicy == "ERROR_ON_CONFLICT")
            {
                reason = "DYNAMIC_FLOW_MAPPING_TARGET_VALUE_CONFLICT";
                return "CONFLICT";
            }
        }

        return "APPLIED";
    }

    private static DynamicFlowMappingChangeDto NoSourceChange(DynamicFlowMappingRuleDto rule, string reason)
        => new()
        {
            MappingId = rule.MappingId,
            MappingVersion = rule.MappingVersion,
            TargetKind = ResolveTargetKind(rule),
            TargetKey = ResolveTargetKind(rule) == "FIELD"
                ? FirstNonBlank(rule.TargetFieldId, rule.TargetFieldKey) ?? string.Empty
                : $"{rule.TargetBlockId}:{rule.TargetColumnKey}",
            Status = "SKIPPED",
            Reason = reason,
            ConceptCode = rule.ConceptCode,
            ContributionPolicy = ResolveContributionPolicy(rule, null)
        };

    private static DynamicFlowMappingChangeDto EvaluationErrorChange(
        DynamicFlowMappingRuleDto rule,
        string reason)
    {
        var targetKind = ResolveTargetKind(rule);
        return new DynamicFlowMappingChangeDto
        {
            MappingId = rule.MappingId,
            MappingVersion = rule.MappingVersion,
            TargetKind = targetKind,
            TargetKey = targetKind == "FIELD"
                ? FirstNonBlank(rule.TargetFieldId, rule.TargetFieldKey) ?? string.Empty
                : $"{rule.TargetBlockId}:{rule.TargetColumnKey}",
            Status = string.Equals(rule.ErrorPolicy, "SKIP_RULE", StringComparison.Ordinal)
                ? "SKIPPED"
                : "CONFLICT",
            Reason = reason,
            ConceptCode = rule.ConceptCode,
            ContributionPolicy = ResolveContributionPolicy(rule, null)
        };
    }

    private static string? BuildContributionPolicyJson(
        IReadOnlyList<DynamicFlowMappingChangeDto> changes,
        string? requestContributionPolicy)
    {
        var rules = new JsonArray();
        foreach (var change in changes.Where(x => x.Status is "APPLIED" or "UNCHANGED"))
        {
            var policy = NormalizeUpper(change.ContributionPolicy) ?? NormalizeUpper(requestContributionPolicy) ?? "EXCLUDE";
            var mode = policy == "INCLUDE"
                ? WorkReportCumulativeContributionMode.Include
                : WorkReportCumulativeContributionMode.Exclude;
            if (mode == WorkReportCumulativeContributionMode.Include)
                continue;

            if (change.TargetKind == "FIELD")
            {
                rules.Add(new JsonObject
                {
                    ["targetKind"] = "FIELD",
                    ["targetKey"] = change.TargetKey,
                    ["mode"] = mode,
                    ["source"] = "DYNAMIC_FLOW_MAPPING",
                    ["mappingId"] = change.MappingId
                });
                continue;
            }

            var parts = change.TargetKey.Split(':', 2);
            rules.Add(new JsonObject
            {
                ["targetKind"] = "TABLE",
                ["blockId"] = parts.Length > 0 ? parts[0] : null,
                ["columnKey"] = parts.Length > 1 ? parts[1] : null,
                ["mode"] = mode,
                ["source"] = "DYNAMIC_FLOW_MAPPING",
                ["mappingId"] = change.MappingId
            });
        }

        if (rules.Count == 0)
            return null;

        return new JsonObject
        {
            ["defaultMode"] = WorkReportCumulativeContributionMode.Exclude,
            ["rules"] = rules
        }.ToJsonString(JsonOptions);
    }

    private static string BuildSummarySourceJson(
        WorkAssignmentReport targetReport,
        IReadOnlyList<DynamicFlowMappingSourceReport> sourceReports,
        IReadOnlyList<DynamicFlowMappingRuleDto> rules,
        IReadOnlyList<DynamicFlowMappingChangeDto> changes)
        => JsonSerializer.Serialize(new
        {
            kind = "DYNAMIC_FLOW_MAPPING",
            mapKind = "DYNAMIC_FLOW_MAPPING",
            targetReportId = targetReport.Id,
            targetAssignmentId = targetReport.WorkAssignmentId,
            sourceReportIds = sourceReports.Select(x => x.Report.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList(),
            sourceAssignmentIds = sourceReports.Select(x => x.Report.WorkAssignmentId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList(),
            mappingRules = rules
                .OrderBy(x => x.MappingId, StringComparer.Ordinal)
                .ThenBy(x => x.MappingVersion)
                .Select(x => new
            {
                x.MappingId,
                x.MappingVersion,
                x.MappingKind,
                x.SourceDynamicFormTemplateId,
                x.SourceStepId,
                x.SourceStepCode,
                x.SourceSectionId,
                x.SourceSectionCode,
                x.SourceFieldId,
                x.SourceFieldKey,
                x.SourceBlockId,
                x.SourceColumnKey,
                x.TargetDynamicFormTemplateId,
                x.TargetStepId,
                x.TargetStepCode,
                x.TargetFieldId,
                x.TargetFieldKey,
                x.TargetSectionId,
                x.TargetSectionCode,
                x.TargetBlockId,
                x.TargetColumnKey,
                x.ConceptCode,
                x.DataType,
                x.JoinKey,
                x.ValueTransform,
                x.ConflictPolicy,
                x.ContributionPolicy,
                x.EvaluationGrain,
                x.ErrorPolicy,
                x.Inputs,
                x.Target,
                x.Calculation
            }).ToList(),
            changes = changes
                .OrderBy(x => x.MappingId, StringComparer.Ordinal)
                .ThenBy(x => x.MappingVersion)
                .ThenBy(x => x.TargetKey, StringComparer.Ordinal)
                .Select(x => new
            {
                x.MappingId,
                x.MappingVersion,
                x.TargetKind,
                x.TargetKey,
                x.SourceReportId,
                x.SourceKey,
                x.Status,
                x.Reason,
                x.ConceptCode,
                x.ContributionPolicy,
                sources = x.Sources
                    .OrderBy(source => source.SourceReportId, StringComparer.Ordinal)
                    .ThenBy(
                        source => source.SourceAssignmentId,
                        StringComparer.Ordinal)
                    .ThenBy(source => source.SourceStepId, StringComparer.Ordinal)
                    .ThenBy(source => source.InputKey, StringComparer.Ordinal)
                    .ThenBy(source => source.SourceKey, StringComparer.Ordinal)
                    .ThenBy(source => source.RowKey, StringComparer.Ordinal)
                    .Select(source => new
                    {
                        source.InputKey,
                        source.SourceDynamicFormTemplateId,
                        source.SourceStepId,
                        source.SourceStepCode,
                        source.SourceAssignmentId,
                        source.SourceReportId,
                        source.SourcePayloadRevision,
                        source.SourcePayloadHash,
                        source.SourceLifecycleRevision,
                        source.SourceKey,
                        source.RowKey
                    }).ToList()
            }).ToList()
        }, JsonOptions);

    private static bool TryGetPropertyCaseInsensitive(JsonObject obj, string key, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(key, out value))
            return true;

        foreach (var item in obj)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static IEnumerable<string> DistinctKeys(params string?[] keys)
        => keys
            .Select(NormalizeText)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal);

    private static bool IsBlank(JsonNode? value)
    {
        if (value is null)
            return true;

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var text))
                return string.IsNullOrWhiteSpace(text);
        }

        if (value is JsonArray array)
            return array.Count == 0;

        return string.Equals(value.ToJsonString(JsonOptions), "null", StringComparison.Ordinal);
    }

    private static decimal? ToDecimal(JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
            return null;

        if (jsonValue.TryGetValue<decimal>(out var decimalValue))
            return decimalValue;
        if (jsonValue.TryGetValue<double>(out var doubleValue))
            return (decimal)doubleValue;
        if (jsonValue.TryGetValue<string>(out var text) &&
            decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static decimal ReadRequiredDecimal(JsonNode? value, string mappingId)
        => ToDecimal(value)
           ?? throw new DynamicFlowMappingEvaluationException(
               "DYNAMIC_FLOW_MAPPING_NUMBER_INVALID",
               mappingId);

    private static string? ToDisplayText(JsonNode? value)
    {
        if (value is null)
            return null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return text;
        return value.ToJsonString(JsonOptions);
    }

    private static string? StableJson(JsonNode? node)
        => node is null
            ? null
            : DynamicFlowDefinitionPayloadContract.CanonicalizeJson(
                node.ToJsonString(JsonOptions));

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeUpper(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string NormalizeP7FieldDataType(string? value)
        => NormalizeUpper(value) switch
        {
            "SHORT_TEXT" or "LONG_TEXT" => "TEXT",
            "CHOICE" => "SINGLE_SELECT",
            { } normalized => normalized,
            _ => throw new DynamicFlowMappingEvaluationException(
                DynamicFlowMappingFieldFailureReasons.DataTypeRequired)
        };

    private static string? ReadString(JsonObject root, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (root.TryGetPropertyValue(property, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonObject root, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (root.TryGetPropertyValue(property, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<int>(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonObject root, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (root.TryGetPropertyValue(property, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }
        }

        return null;
    }

    private sealed record SourceValue(
        string? SourceReportId,
        string SourceKey,
        string? RowKey,
        int? RowIndex,
        JsonNode? Value,
        IReadOnlyList<DynamicFlowMappingInputProvenanceDto>? Provenance = null);

    private sealed record ResolvedInputValue(
        DynamicFlowMappingSourceReport? Source,
        SourceValue Value);

    private sealed record NullPolicyResult(
        bool SkipEvaluation,
        IReadOnlyList<JsonNode?> Values);

    private sealed record TableIndexMapItem(
        int Index,
        string? RowKey,
        string? ColumnKey,
        string? MetricKey);
}

internal sealed record DynamicFlowMappingSourceReport(
    WorkAssignmentReport Report,
    string? FlowStepId,
    string? FlowStepCode,
    string? FieldValuesJson,
    string? TableValuesJson,
    DynamicFlowInstance? RuntimeInstance = null,
    DynamicFlowStepInstance? RuntimeStep = null,
    WorkAssignment? Assignment = null);
