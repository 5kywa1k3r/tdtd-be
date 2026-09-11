using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

public sealed record DynamicFlowDefinitionValidationOptions(
    bool AllowLegacy = true,
    bool AllowServerManagedPins = false,
    bool RequireServerManagedPins = false,
    bool AllowHistoricalCatalogPins = false);

internal static class DynamicFlowStoredCatalogPinPolicy
{
    internal const string HistoricalV11Version = "1.1";
    internal const string HistoricalV11SemanticHash =
        "e8a0b15bb5c7cab81ed49ec5c213105366a1194faa2d78168a46226d9cc505cf";
    internal const string HistoricalV12Version = "1.2";
    internal const string HistoricalV12SemanticHash =
        "b26549d5de7a3d93bd6fc9bab7bfdfbdaffb66a01347039b2c3629692b60068f";
    internal const string HistoricalV13Version = "1.3";
    internal const string HistoricalV13SemanticHash =
        "55cfa0a4420e01db6707011ffc7a0271088c5b01b63edb8f2d21978edd3e2497";

    internal static bool IsCurrent(string? version, string? semanticHash)
        => string.Equals(
               version,
               DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
               StringComparison.Ordinal) &&
           string.Equals(
               semanticHash,
               DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
               StringComparison.Ordinal);

    internal static bool IsHistoricalV11(string? version, string? semanticHash)
        => string.Equals(version, HistoricalV11Version, StringComparison.Ordinal) &&
           string.Equals(semanticHash, HistoricalV11SemanticHash, StringComparison.Ordinal);

    internal static bool IsHistoricalV12(string? version, string? semanticHash)
        => string.Equals(version, HistoricalV12Version, StringComparison.Ordinal) &&
           string.Equals(semanticHash, HistoricalV12SemanticHash, StringComparison.Ordinal);

    internal static bool IsHistoricalV13(string? version, string? semanticHash)
        => string.Equals(version, HistoricalV13Version, StringComparison.Ordinal) &&
           string.Equals(semanticHash, HistoricalV13SemanticHash, StringComparison.Ordinal);

    internal static bool IsP7Candidate(string? version, string? semanticHash)
        => DynamicFlowRuntimeEligibilityPolicy.IsP7MappingCatalog(version, semanticHash);

    internal static bool IsKnownStored(string? version, string? semanticHash)
        => IsCurrent(version, semanticHash) ||
           IsHistoricalV11(version, semanticHash) ||
           IsHistoricalV12(version, semanticHash) ||
           IsHistoricalV13(version, semanticHash) ||
           IsP7Candidate(version, semanticHash);
}

public sealed record DynamicFlowCanonicalPayload(
    DynamicFlowTemplatePayloadDto Payload,
    string CanonicalJson,
    string PayloadHash,
    int Utf8ByteCount,
    int SourceSchemaVersion,
    int AdapterVersion,
    string BlockedUntilPhase);

public sealed record DynamicFlowScalarEndpoint(string FieldId, string? FieldKey = null);

public sealed record DynamicFlowTableColumnEndpoint(
    string BlockId,
    string ColumnKey,
    string TableMode = "APPEND_ROWS");

public sealed record DynamicFlowFormEndpointCatalog(
    string FormNodeId,
    IReadOnlyList<DynamicFlowScalarEndpoint> ScalarFields,
    IReadOnlyList<DynamicFlowTableColumnEndpoint> TableColumns);

public sealed record DynamicFlowPolicyCoverageSummary(
    int ActorRoleEndpoints,
    int ScalarFieldEndpoints,
    int TableColumnEndpoints);

public sealed record DynamicFlowMappingDefinitionSummary(
    int MappingRules,
    int SourceEndpoints,
    int TargetEndpoints);

/// <summary>
/// The single versioned ingress for Dynamic Flow definition payloads. It adapts
/// schema v1 exactly once, rejects unknown schema v2 fields, validates the
/// declarative graph, and returns deterministic canonical bytes and SHA-256.
/// It does not execute gateways, conditions, mappings, or policies.
/// </summary>
public static class DynamicFlowDefinitionPayloadContract
{
    public const int MaxTopologyNodes = 200;
    public const int MaxTopologyEdges = 400;
    public const int MaxCanonicalPayloadUtf8Bytes = 1_048_576;
    public const int MaxConditionDepth = 12;
    public const int MaxConditionNodes = 128;

    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private static readonly IReadOnlySet<string> CanonicalRootFields = Set(
        "schemaVersion", "archetypeId", "entryStepId", "rootDynamicFormTemplateId",
        "resultOwnerStepId", "resultOwnerFormNodeId", "statisticsOwnerStepId",
        "statisticsOwnerFormNodeId", "catalogVersion", "catalogSemanticHash", "formNodes",
        "nodes", "edges", "actorPolicies", "fieldPolicies", "tableColumnPolicies",
        "mappingRules", "rollbackPolicy", "finalResultPolicy", "statisticProfile");

    private static readonly IReadOnlySet<string> LegacyRootFields = Set(
        "schemaVersion", "archetypeId", "entryStepId", "rootStepId", "startStepId",
        "rootDynamicFormTemplateId", "resultOwnerStepId", "resultOwnerFormNodeId",
        "resultStepId", "resultFormNodeId", "statisticsOwnerStepId",
        "statisticsOwnerFormNodeId", "statisticOwnerStepId", "statisticOwnerFormNodeId",
        "catalogVersion", "catalogSemanticHash", "formNodes", "steps", "transitions",
        "nodes", "edges", "actorPolicies", "fieldPolicies", "tableColumnPolicies",
        "mappingRules", "rollbackPolicy", "finalResultPolicy", "statisticProfile");

    private static readonly IReadOnlySet<string> FormNodeFields = Set(
        "formNodeId", "role", "dynamicFormTemplateId", "dynamicFormFamilyId",
        "dynamicFormVersionNo", "dynamicFormSchemaHash", "dynamicFormSnapshotHash");

    private static readonly IReadOnlySet<string> CanonicalNodeFields = Set(
        "nodeId", "nodeCode", "nodeKind", "name", "formNodeId", "declaredRoles", "gateway");

    private static readonly IReadOnlySet<string> LegacyStepFields = Set(
        "stepId", "nodeId", "stepCode", "nodeCode", "stepName", "name", "stepOrder",
        "formNodeId", "dynamicFormTemplateId", "nodeKind", "declaredRoles", "role", "gateway");

    private static readonly IReadOnlySet<string> CanonicalEdgeFields = Set(
        "transitionId", "fromNodeId", "toNodeId", "condition");

    private static readonly IReadOnlySet<string> LegacyTransitionFields = Set(
        "transitionId", "fromStepId", "fromNodeId", "fromStepCode", "toStepId",
        "toNodeId", "toStepCode", "condition");

    private static readonly IReadOnlySet<string> GatewayFields = Set(
        "kind", "expectedIncomingNodeIds", "requiredIncomingCount", "reviewRole",
        "subflowFamilyId", "subflowVersionId", "scheduleKey", "rollbackTargetNodeId");

    private static readonly IReadOnlySet<string> ConditionFields = Set(
        "operator", "field", "value", "values", "children");

    private static readonly IReadOnlySet<string> ConditionOperators = Set(
        "AND", "OR", "NOT", "EQ", "NE", "GT", "GTE", "LT", "LTE", "IN", "NOT_IN",
        "EXISTS", "NOT_EXISTS", "IS_NULL", "IS_NOT_NULL", "TRUE", "FALSE");

    public static DynamicFlowCanonicalPayload CanonicalizeRequestPayload(
        JsonElement? payload,
        string? payloadJson,
        DynamicFlowDefinitionValidationOptions? options = null)
    {
        var hasTypedPayload = payload.HasValue &&
                              payload.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        if (hasTypedPayload && payloadJson is not null)
        {
            throw Error(
                AppErrorCode.DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS,
                "payload",
                new { legacyField = "payloadJson" });
        }

        return hasTypedPayload
            ? CanonicalizeAndValidate(payload!.Value.GetRawText(), options)
            : CanonicalizeAndValidate(payloadJson, options);
    }

    public static DynamicFlowCanonicalPayload CanonicalizeAndValidate(
        DynamicFlowTemplatePayloadDto? payload,
        string? payloadJson,
        DynamicFlowDefinitionValidationOptions? options = null)
    {
        if (payload is not null && payloadJson is not null)
        {
            throw Error(
                AppErrorCode.DYNAMIC_FLOW_TEMPLATE_PAYLOAD_AMBIGUOUS,
                "payload",
                new { legacyField = "payloadJson" });
        }

        if (payload is null)
            return CanonicalizeAndValidate(payloadJson, options);

        var requestNode = JsonSerializer.SerializeToNode(payload, StrictJsonOptions)?.AsObject()
                          ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "payload");

#pragma warning disable CS0618
        if (payload.Nodes.Count == 0 && payload.Steps.Count > 0)
            requestNode["schemaVersion"] = 1;
        if (payload.Steps.Count == 0)
            requestNode.Remove("steps");
        if (payload.Transitions.Count == 0)
            requestNode.Remove("transitions");
#pragma warning restore CS0618

        return CanonicalizeAndValidate(requestNode.ToJsonString(StrictJsonOptions), options);
    }

    public static DynamicFlowCanonicalPayload CanonicalizeAndValidate(
        string? payloadJson,
        DynamicFlowDefinitionValidationOptions? options = null)
    {
        options ??= new DynamicFlowDefinitionValidationOptions();
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "payload");

        JsonObject input;
        try
        {
            input = JsonNode.Parse(
                        payloadJson,
                        documentOptions: new JsonDocumentOptions
                        {
                            AllowTrailingCommas = false,
                            CommentHandling = JsonCommentHandling.Disallow,
                            MaxDepth = 256
                        }) as JsonObject
                    ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "payload");
        }
        catch (JsonException error)
        {
            throw Error(
                AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID,
                NormalizeJsonPath(error.Path),
                new { error.BytePositionInLine });
        }

        var sourceSchemaVersion = ReadSchemaVersion(input);
        JsonObject canonicalInput;
        if (sourceSchemaVersion == DynamicFlowDefinitionSchema.CurrentVersion)
        {
            EnsureAllowedFields(input, CanonicalRootFields, string.Empty);
            canonicalInput = (JsonObject)input.DeepClone();
        }
        else if (sourceSchemaVersion is 0 or 1 && options.AllowLegacy)
        {
            canonicalInput = AdaptLegacy(input);
        }
        else
        {
            throw Error(
                AppErrorCode.DYNAMIC_FLOW_SCHEMA_VERSION_UNSUPPORTED,
                "schemaVersion",
                new { schemaVersion = sourceSchemaVersion });
        }

        ValidateStrictShape(canonicalInput);
        ValidateServerManagedPins(canonicalInput, options);

        DynamicFlowTemplatePayloadDto payload;
        try
        {
            payload = canonicalInput.Deserialize<DynamicFlowTemplatePayloadDto>(StrictJsonOptions)
                      ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "payload");
        }
        catch (JsonException error)
        {
            var path = NormalizeJsonPath(error.Path);
            var code = path.Split('.', '[', ']')
                    .Where(segment => !string.IsNullOrWhiteSpace(segment))
                    .LastOrDefault() is { } finalSegment && IsCallerAuthorityField(finalSegment)
                ? AppErrorCode.DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN
                : error.Message.Contains("unmapped", StringComparison.OrdinalIgnoreCase)
                    ? AppErrorCode.DYNAMIC_FLOW_PAYLOAD_UNKNOWN_FIELD
                    : AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID;
            throw Error(
                code,
                path,
                code == AppErrorCode.DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN
                    ? new { reason = "DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN" }
                    : null);
        }

        NormalizeIdentifiers(payload);
        var archetype = ValidateArchetype(payload);
        ValidateTopology(payload);
        ValidateOwners(payload);
        ValidateArchetypeTopology(payload);
        ValidateStoredPins(payload, options);

        var canonicalNode = JsonSerializer.SerializeToNode(payload, StrictJsonOptions)?.AsObject()
                            ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "payload");
        canonicalNode.Remove("steps");
        canonicalNode.Remove("transitions");
        canonicalNode["schemaVersion"] = DynamicFlowDefinitionSchema.CurrentVersion;

        var canonicalJson = CanonicalizeNode(canonicalNode);
        var utf8ByteCount = Encoding.UTF8.GetByteCount(canonicalJson);
        if (utf8ByteCount > MaxCanonicalPayloadUtf8Bytes)
        {
            throw Error(
                AppErrorCode.DYNAMIC_FLOW_PAYLOAD_BUDGET_EXCEEDED,
                "payload",
                new { limit = MaxCanonicalPayloadUtf8Bytes, actual = utf8ByteCount });
        }

        var hash = Sha256Utf8(canonicalJson);
        return new DynamicFlowCanonicalPayload(
            payload,
            canonicalJson,
            hash,
            utf8ByteCount,
            sourceSchemaVersion,
            sourceSchemaVersion == DynamicFlowDefinitionSchema.CurrentVersion
                ? DynamicFlowDefinitionSchema.CurrentAdapterVersion
                : 1,
            archetype.TargetPhase ?? "P6");
    }

    public static string CanonicalizeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 256
            });
            return CanonicalizeElement(document.RootElement);
        }
        catch (JsonException error)
        {
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, NormalizeJsonPath(error.Path));
        }
    }

    public static string ComputeCanonicalSha256(string json)
        => Sha256Utf8(CanonicalizeJson(json));

    /// <summary>
    /// Lock-time coverage validation. Form endpoints must come from exact,
    /// server-verified published Form snapshots. A matching explicit deny is
    /// coverage; two equally-specific effective policies with conflicting
    /// outcomes are rejected.
    /// </summary>
    public static DynamicFlowPolicyCoverageSummary ValidatePolicyCoverage(
        DynamicFlowTemplatePayloadDto payload,
        IReadOnlyDictionary<string, DynamicFlowFormEndpointCatalog> endpointCatalogs)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(endpointCatalogs);

        var steps = payload.Nodes
            .Where(node => node.NodeKind == DynamicFlowNodeKinds.FormStep)
            .ToArray();
        ValidateConfiguredPolicyRoles(payload, steps);
        ValidateConfiguredPolicyEndpoints(payload, endpointCatalogs);

        var actorEndpointCount = 0;
        var scalarEndpointCount = 0;
        var tableEndpointCount = 0;
        foreach (var step in steps)
        {
            if (step.FormNodeId is null || !endpointCatalogs.TryGetValue(step.FormNodeId, out var catalog))
                throw Error(AppErrorCode.DYNAMIC_FLOW_POLICY_FIELD_UNKNOWN, $"nodes[{payload.Nodes.IndexOf(step)}].formNodeId");

            foreach (var role in step.DeclaredRoles)
            {
                var actorMatches = payload.ActorPolicies
                    .Select((policy, index) => (Policy: policy, Index: index))
                    .Where(item => MatchesStep(item.Policy.StepId, item.Policy.StepCode, step) && MatchesRole(item.Policy.ActorRole, role))
                    .ToArray();
                if (actorMatches.Length == 0)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_ACTOR_POLICY_COVERAGE_INCOMPLETE, "actorPolicies", new { stepId = step.NodeId, role });
                EnsureNoEqualSpecificityConflict(
                    actorMatches,
                    item => ActorSpecificity(item.Policy),
                    item => ActorOutcome(item.Policy),
                    "actorPolicies");
                actorEndpointCount++;

                foreach (var field in catalog.ScalarFields)
                {
                    var matches = payload.FieldPolicies
                        .Select((policy, index) => (Policy: policy, Index: index))
                        .Where(item => MatchesFieldPolicy(item.Policy, step, role, field, payload.FormNodes))
                        .ToArray();
                    if (matches.Length == 0)
                    {
                        throw Error(
                            AppErrorCode.DYNAMIC_FLOW_FIELD_POLICY_COVERAGE_INCOMPLETE,
                            "fieldPolicies",
                            new { stepId = step.NodeId, role, field.FieldId });
                    }
                    EnsureNoEqualSpecificityConflict(
                        matches,
                        item => FieldSpecificity(item.Policy),
                        item => FieldOutcome(item.Policy),
                        "fieldPolicies");
                    scalarEndpointCount++;
                }

                foreach (var column in catalog.TableColumns)
                {
                    var matches = payload.TableColumnPolicies
                        .Select((policy, index) => (Policy: policy, Index: index))
                        .Where(item => MatchesTablePolicy(item.Policy, step, role, column, payload.FormNodes))
                        .ToArray();
                    if (matches.Length == 0)
                    {
                        throw Error(
                            AppErrorCode.DYNAMIC_FLOW_TABLE_POLICY_COVERAGE_INCOMPLETE,
                            "tableColumnPolicies",
                            new { stepId = step.NodeId, role, column.BlockId, column.ColumnKey });
                    }
                    var incompatible = matches.FirstOrDefault(item =>
                        string.Equals(column.TableMode, "SUMMARY_TEMPLATE", StringComparison.OrdinalIgnoreCase) &&
                        (item.Policy.Write == true || item.Policy.Required == true));
                    if (incompatible.Policy is not null)
                    {
                        var property = incompatible.Policy.Write == true ? "write" : "required";
                        throw Error(
                            AppErrorCode.DYNAMIC_FLOW_TABLE_POLICY_ENDPOINT_INCOMPATIBLE,
                            $"tableColumnPolicies[{incompatible.Index}].{property}",
                            new { column.BlockId, column.ColumnKey, column.TableMode });
                    }
                    EnsureNoEqualSpecificityConflict(
                        matches,
                        item => TableSpecificity(item.Policy),
                        item => TableOutcome(item.Policy),
                        "tableColumnPolicies");
                    tableEndpointCount++;
                }
            }
        }

        return new DynamicFlowPolicyCoverageSummary(actorEndpointCount, scalarEndpointCount, tableEndpointCount);
    }

    /// <summary>
    /// Validates only the design-time identity and endpoint contract of mapping
    /// metadata. Mapping evaluation remains outside the P4 definition boundary.
    /// Catalogs must be built from the exact published Form snapshots pinned by
    /// the caller immediately before lock.
    /// </summary>
    public static DynamicFlowMappingDefinitionSummary ValidateMappingDefinition(
        DynamicFlowTemplatePayloadDto payload,
        IReadOnlyDictionary<string, DynamicFlowFormEndpointCatalog> endpointCatalogs)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(endpointCatalogs);

        var steps = payload.Nodes
            .Where(node => node.NodeKind == DynamicFlowNodeKinds.FormStep)
            .ToArray();
        var formNodes = payload.FormNodes.ToDictionary(node => node.FormNodeId, StringComparer.Ordinal);
        var mappingIds = new HashSet<string>(StringComparer.Ordinal);
        var targetWrites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceEndpointCount = 0;
        var targetEndpointCount = 0;

        for (var ruleIndex = 0; ruleIndex < payload.MappingRules.Count; ruleIndex++)
        {
            var rule = payload.MappingRules[ruleIndex];
            var rulePath = $"mappingRules[{ruleIndex}]";
            var mappingId = NormalizeNullable(rule.MappingId);
            if (mappingId is null)
            {
                throw MappingDefinitionError(
                    $"{rulePath}.mappingId",
                    "DYNAMIC_FLOW_MAPPING_ID_REQUIRED");
            }
            if (!mappingIds.Add(mappingId))
            {
                throw MappingDefinitionError(
                    $"{rulePath}.mappingId",
                    "DYNAMIC_FLOW_MAPPING_ID_DUPLICATE",
                    new { mappingId });
            }
            if (rule.MappingVersion < 1)
            {
                throw MappingDefinitionError(
                    $"{rulePath}.mappingVersion",
                    "DYNAMIC_FLOW_MAPPING_VERSION_INVALID",
                    new { rule.MappingVersion });
            }

            var structured = rule.Inputs is not null || rule.Target is not null || rule.Calculation is not null;
            string targetWrite;
            if (structured)
            {
                if (HasFlatMappingEndpointMetadata(rule))
                {
                    throw MappingDefinitionError(
                        rulePath,
                        "DYNAMIC_FLOW_MAPPING_ENDPOINT_SHAPE_AMBIGUOUS");
                }
                if (rule.Inputs is not { Count: > 0 })
                {
                    throw MappingDefinitionError(
                        $"{rulePath}.inputs",
                        "DYNAMIC_FLOW_MAPPING_INPUT_REQUIRED");
                }
                if (rule.Target is null)
                {
                    throw MappingDefinitionError(
                        $"{rulePath}.target",
                        "DYNAMIC_FLOW_MAPPING_TARGET_REQUIRED");
                }

                var inputKeys = new HashSet<string>(StringComparer.Ordinal);
                for (var inputIndex = 0; inputIndex < rule.Inputs.Count; inputIndex++)
                {
                    var input = rule.Inputs[inputIndex];
                    var inputPath = $"{rulePath}.inputs[{inputIndex}]";
                    if (input is null)
                        throw MappingDefinitionError(inputPath, "DYNAMIC_FLOW_MAPPING_INPUT_REQUIRED");
                    var inputKey = NormalizeNullable(input.InputKey);
                    if (inputKey is null || !inputKeys.Add(inputKey))
                    {
                        throw MappingDefinitionError(
                            $"{inputPath}.inputKey",
                            inputKey is null
                                ? "DYNAMIC_FLOW_MAPPING_INPUT_KEY_REQUIRED"
                                : "DYNAMIC_FLOW_MAPPING_INPUT_KEY_DUPLICATE",
                            inputKey is null ? null : new { inputKey });
                    }

                    var sourceKind = NormalizeUpperNullable(input.Source?.Kind);
                    if (sourceKind == "CONSTANT")
                    {
                        ValidateConstantMappingEndpoint(input.Source!, inputPath + ".source");
                        continue;
                    }

                    ValidateStructuredMappingEndpointKind(sourceKind, inputPath + ".source", allowConstant: true);
                    _ = ValidateMappingEndpoint(
                        steps,
                        formNodes,
                        endpointCatalogs,
                        MappingEndpoint.FromStructured(input.Source!, inputPath + ".source", sourceKind!),
                        isTarget: false);
                    sourceEndpointCount++;
                }

                var targetKind = NormalizeUpperNullable(rule.Target.Kind);
                ValidateStructuredMappingEndpointKind(targetKind, rulePath + ".target", allowConstant: false);
                EnsureMappingKindMatchesEndpoint(rule.MappingKind, targetKind!, rulePath + ".mappingKind");
                targetWrite = ValidateMappingEndpoint(
                    steps,
                    formNodes,
                    endpointCatalogs,
                    MappingEndpoint.FromStructured(rule.Target, rulePath + ".target", targetKind!),
                    isTarget: true);
                targetEndpointCount++;
            }
            else
            {
                var source = MappingEndpoint.FromFlat(rule, rulePath, isSource: true);
                var target = MappingEndpoint.FromFlat(rule, rulePath, isSource: false);
                EnsureFlatMappingKindCompatibility(rule.MappingKind, source, target, rulePath);
                _ = ValidateMappingEndpoint(
                    steps,
                    formNodes,
                    endpointCatalogs,
                    source,
                    isTarget: false);
                targetWrite = ValidateMappingEndpoint(
                    steps,
                    formNodes,
                    endpointCatalogs,
                    target,
                    isTarget: true);
                sourceEndpointCount++;
                targetEndpointCount++;
            }

            if (!targetWrites.Add(targetWrite))
            {
                throw MappingDefinitionError(
                    rulePath,
                    "DYNAMIC_FLOW_MAPPING_TARGET_DUPLICATE",
                    new { mappingId, target = targetWrite });
            }
        }

        return new DynamicFlowMappingDefinitionSummary(
            payload.MappingRules.Count,
            sourceEndpointCount,
            targetEndpointCount);
    }

    private static int ReadSchemaVersion(JsonObject input)
    {
        if (!input.TryGetPropertyValue("schemaVersion", out var value) || value is null)
            return 0;
        if (value is not JsonValue scalar || !scalar.TryGetValue<int>(out var version))
            throw Error(AppErrorCode.DYNAMIC_FLOW_SCHEMA_VERSION_UNSUPPORTED, "schemaVersion");
        return version;
    }

    private static JsonObject AdaptLegacy(JsonObject legacy)
    {
        EnsureAllowedFields(legacy, LegacyRootFields, string.Empty);
        if (legacy.ContainsKey("nodes") && legacy.ContainsKey("steps"))
            throw Error(AppErrorCode.DYNAMIC_FLOW_LEGACY_ALIAS_AMBIGUOUS, "nodes");
        if (legacy.ContainsKey("edges") && legacy.ContainsKey("transitions"))
            throw Error(AppErrorCode.DYNAMIC_FLOW_LEGACY_ALIAS_AMBIGUOUS, "edges");

        var steps = RequireArray(legacy, legacy.ContainsKey("nodes") ? "nodes" : "steps", "steps");
        if (steps.Count == 0)
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "steps");

        var legacyFormNodes = CloneOrDefaultArray(legacy, "formNodes");
        var legacyActorPolicies = CloneOrDefaultArray(legacy, "actorPolicies");
        var legacyFieldPolicies = CloneOrDefaultArray(legacy, "fieldPolicies");
        var legacyTableColumnPolicies = CloneOrDefaultArray(legacy, "tableColumnPolicies");
        MaterializeLegacyPolicyWildcards(
            legacyActorPolicies,
            "stepId",
            "stepCode",
            "actorRole");
        MaterializeLegacyPolicyWildcards(
            legacyFieldPolicies,
            "dynamicFormTemplateId",
            "stepId",
            "stepCode",
            "actorRole",
            "fieldId",
            "fieldKey");
        MaterializeLegacyPolicyWildcards(
            legacyTableColumnPolicies,
            "dynamicFormTemplateId",
            "stepId",
            "stepCode",
            "actorRole",
            "blockId",
            "columnKey");
        var legacyRoleByFormNodeId = legacyFormNodes
            .OfType<JsonObject>()
            .Select(node => (Id: ReadOptionalString(node, "formNodeId"), Role: ReadOptionalString(node, "role")))
            .Where(pair => pair.Id is not null && pair.Role is not null)
            .ToDictionary(pair => pair.Id!, pair => pair.Role!, StringComparer.Ordinal);

        var canonicalNodes = new JsonArray();
        var nodeIdByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index] as JsonObject
                       ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, $"steps[{index}]");
            EnsureAllowedFields(step, LegacyStepFields, $"steps[{index}]");
            var nodeId = ResolveStringAlias(step, $"steps[{index}]", true, "stepId", "nodeId")!;
            var nodeCode = ResolveStringAlias(step, $"steps[{index}]", true, "stepCode", "nodeCode")!;
            if (!nodeIdByCode.TryAdd(nodeCode, nodeId))
                throw Error(AppErrorCode.DYNAMIC_FLOW_NODE_CODE_DUPLICATE, $"steps[{index}].stepCode");

            var node = new JsonObject
            {
                ["nodeId"] = nodeId,
                ["nodeCode"] = nodeCode,
                ["nodeKind"] = ReadOptionalString(step, "nodeKind") ?? DynamicFlowNodeKinds.FormStep
            };
            CopyAlias(step, node, "name", "stepName", "name");
            Copy(step, node, "formNodeId");
            Copy(step, node, "gateway");

            if (step.TryGetPropertyValue("declaredRoles", out var declaredRoles) && declaredRoles is not null)
            {
                node["declaredRoles"] = declaredRoles.DeepClone();
            }
            else
            {
                var role = ReadOptionalString(step, "role");
                if (role is null && ReadOptionalString(step, "formNodeId") is { } formNodeId)
                    legacyRoleByFormNodeId.TryGetValue(formNodeId, out role);
                node["declaredRoles"] = role is null ? new JsonArray() : new JsonArray(role);
            }
            canonicalNodes.Add(node);
        }

        var transitionProperty = legacy.ContainsKey("edges") ? "edges" : "transitions";
        var transitions = legacy.TryGetPropertyValue(transitionProperty, out var transitionValue)
            ? transitionValue as JsonArray
              ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, transitionProperty)
            : new JsonArray();
        var canonicalEdges = new JsonArray();
        for (var index = 0; index < transitions.Count; index++)
        {
            var transition = transitions[index] as JsonObject
                             ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, $"{transitionProperty}[{index}]");
            EnsureAllowedFields(transition, LegacyTransitionFields, $"{transitionProperty}[{index}]");
            var fromNodeId = ResolveNodeReference(
                transition,
                nodeIdByCode,
                $"{transitionProperty}[{index}]",
                "fromStepId",
                "fromNodeId",
                "fromStepCode");
            var toNodeId = ResolveNodeReference(
                transition,
                nodeIdByCode,
                $"{transitionProperty}[{index}]",
                "toStepId",
                "toNodeId",
                "toStepCode");
            var transitionId = ReadOptionalString(transition, "transitionId")
                               ?? DeterministicTransitionId(fromNodeId, toNodeId, index);
            var edge = new JsonObject
            {
                ["transitionId"] = transitionId,
                ["fromNodeId"] = fromNodeId,
                ["toNodeId"] = toNodeId
            };
            Copy(transition, edge, "condition");
            canonicalEdges.Add(edge);
        }

        var canonical = new JsonObject
        {
            ["schemaVersion"] = DynamicFlowDefinitionSchema.CurrentVersion,
            ["archetypeId"] = ReadOptionalString(legacy, "archetypeId")
                              ?? (canonicalNodes.Count == 1 ? "FLOW-T01" : "FLOW-T03"),
            ["formNodes"] = legacyFormNodes,
            ["nodes"] = canonicalNodes,
            ["edges"] = canonicalEdges,
            ["actorPolicies"] = legacyActorPolicies,
            ["fieldPolicies"] = legacyFieldPolicies,
            ["tableColumnPolicies"] = legacyTableColumnPolicies,
            ["mappingRules"] = CloneOrDefaultArray(legacy, "mappingRules"),
            ["rollbackPolicy"] = CloneOrDefaultObject(legacy, "rollbackPolicy"),
            ["finalResultPolicy"] = CloneOrDefaultObject(legacy, "finalResultPolicy"),
            ["statisticProfile"] = CloneOrDefaultObject(legacy, "statisticProfile")
        };

        Copy(legacy, canonical, "rootDynamicFormTemplateId");
        Copy(legacy, canonical, "catalogVersion");
        Copy(legacy, canonical, "catalogSemanticHash");
        CopyAlias(legacy, canonical, "resultOwnerStepId", "resultOwnerStepId", "resultStepId");
        CopyAlias(legacy, canonical, "resultOwnerFormNodeId", "resultOwnerFormNodeId", "resultFormNodeId");
        CopyAlias(legacy, canonical, "statisticsOwnerStepId", "statisticsOwnerStepId", "statisticOwnerStepId");
        CopyAlias(legacy, canonical, "statisticsOwnerFormNodeId", "statisticsOwnerFormNodeId", "statisticOwnerFormNodeId");

        var entryStepId = ResolveStringAlias(legacy, string.Empty, false, "entryStepId", "rootStepId", "startStepId");
        canonical["entryStepId"] = entryStepId ?? InferSingleRootId(canonicalNodes, canonicalEdges);
        return canonical;
    }

    private static void ValidateStrictShape(JsonObject root)
    {
        EnsureAllowedFields(root, CanonicalRootFields, string.Empty);
        ValidateArrayObjects(root, "formNodes", FormNodeFields, ValidateNoNestedShape);
        ValidateArrayObjects(root, "nodes", CanonicalNodeFields, ValidateNodeShape);
        ValidateArrayObjects(root, "edges", CanonicalEdgeFields, ValidateEdgeShape);
    }

    private static void ValidateNoNestedShape(JsonObject _, string __)
    {
    }

    private static void ValidateNodeShape(JsonObject node, string path)
    {
        if (node.TryGetPropertyValue("gateway", out var gatewayValue) && gatewayValue is not null)
        {
            var gateway = gatewayValue as JsonObject
                          ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, $"{path}.gateway");
            EnsureAllowedFields(gateway, GatewayFields, $"{path}.gateway");
        }
    }

    private static void ValidateEdgeShape(JsonObject edge, string path)
    {
        if (edge.TryGetPropertyValue("condition", out var conditionValue) && conditionValue is not null)
        {
            var condition = conditionValue as JsonObject
                            ?? throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, $"{path}.condition");
            ValidateConditionShape(condition, $"{path}.condition");
        }
    }

    private static void ValidateConditionShape(JsonObject condition, string path)
    {
        foreach (var property in condition)
        {
            if (!ConditionFields.Contains(property.Key))
                throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, JoinPath(path, property.Key));
        }
        if (!condition.TryGetPropertyValue("children", out var childrenValue) || childrenValue is null)
            return;
        var children = childrenValue as JsonArray
                       ?? throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, $"{path}.children");
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index] as JsonObject
                        ?? throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, $"{path}.children[{index}]");
            ValidateConditionShape(child, $"{path}.children[{index}]");
        }
    }

    private static void ValidateServerManagedPins(
        JsonObject root,
        DynamicFlowDefinitionValidationOptions options)
    {
        if (options.AllowServerManagedPins)
            return;
        if (root.ContainsKey("catalogVersion") || root.ContainsKey("catalogSemanticHash"))
            throw Error(AppErrorCode.DYNAMIC_FLOW_CATALOG_PIN_FORBIDDEN, "catalogVersion");

        if (root["formNodes"] is not JsonArray formNodes)
            return;
        for (var index = 0; index < formNodes.Count; index++)
        {
            if (formNodes[index] is not JsonObject formNode)
                continue;
            foreach (var pin in new[]
                     {
                         "dynamicFormFamilyId", "dynamicFormVersionNo", "dynamicFormSchemaHash",
                         "dynamicFormSnapshotHash"
                     })
            {
                if (formNode.ContainsKey(pin))
                    throw Error(AppErrorCode.DYNAMIC_FLOW_FORM_PIN_FORBIDDEN, $"formNodes[{index}].{pin}");
            }
        }
    }

    private static DynamicFormFlowCapabilityItemMetadata ValidateArchetype(DynamicFlowTemplatePayloadDto payload)
    {
        var archetype = DynamicFormFlowCapabilityCatalogMetadata.DynamicFlowArchetypes
            .SingleOrDefault(item => string.Equals(item.Id, payload.ArchetypeId, StringComparison.Ordinal));
        return archetype
               ?? throw Error(AppErrorCode.DYNAMIC_FLOW_ARCHETYPE_UNKNOWN, "archetypeId");
    }

    private static void ValidateTopology(DynamicFlowTemplatePayloadDto payload)
    {
        if (payload.Nodes.Count > MaxTopologyNodes)
            throw Error(AppErrorCode.DYNAMIC_FLOW_NODE_BUDGET_EXCEEDED, "nodes", new { limit = MaxTopologyNodes, actual = payload.Nodes.Count });
        if (payload.Edges.Count > MaxTopologyEdges)
            throw Error(AppErrorCode.DYNAMIC_FLOW_EDGE_BUDGET_EXCEEDED, "edges", new { limit = MaxTopologyEdges, actual = payload.Edges.Count });

        var formNodeById = new Dictionary<string, DynamicFlowFormNodeDto>(StringComparer.Ordinal);
        for (var index = 0; index < payload.FormNodes.Count; index++)
        {
            var formNode = payload.FormNodes[index];
            if (string.IsNullOrWhiteSpace(formNode.FormNodeId))
                throw Error(AppErrorCode.DYNAMIC_FLOW_STEP_FORM_NODE_MISMATCH, $"formNodes[{index}].formNodeId");
            if (!formNodeById.TryAdd(formNode.FormNodeId, formNode))
                throw Error(AppErrorCode.DYNAMIC_FLOW_FORM_NODE_ID_DUPLICATE, $"formNodes[{index}].formNodeId");
            if (string.IsNullOrWhiteSpace(formNode.DynamicFormTemplateId))
                throw Error(AppErrorCode.DYNAMIC_FLOW_STEP_FORM_NODE_MISMATCH, $"formNodes[{index}].dynamicFormTemplateId");
        }

        var nodeById = new Dictionary<string, DynamicFlowTopologyNodeDto>(StringComparer.Ordinal);
        var codeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < payload.Nodes.Count; index++)
        {
            var node = payload.Nodes[index];
            if (string.IsNullOrWhiteSpace(node.NodeId) || !nodeById.TryAdd(node.NodeId, node))
                throw Error(AppErrorCode.DYNAMIC_FLOW_NODE_ID_DUPLICATE, $"nodes[{index}].nodeId");
            if (string.IsNullOrWhiteSpace(node.NodeCode) || !codeSet.Add(node.NodeCode))
                throw Error(AppErrorCode.DYNAMIC_FLOW_NODE_CODE_DUPLICATE, $"nodes[{index}].nodeCode");
            if (!DynamicFlowNodeKinds.All.Contains(node.NodeKind))
                throw Error(AppErrorCode.DYNAMIC_FLOW_NODE_KIND_UNSUPPORTED, $"nodes[{index}].nodeKind");

            if (node.NodeKind == DynamicFlowNodeKinds.FormStep)
            {
                if (string.IsNullOrWhiteSpace(node.FormNodeId) || !formNodeById.ContainsKey(node.FormNodeId))
                    throw Error(AppErrorCode.DYNAMIC_FLOW_STEP_FORM_NODE_MISMATCH, $"nodes[{index}].formNodeId");
                if (node.DeclaredRoles.Count == 0 || node.DeclaredRoles.Any(string.IsNullOrWhiteSpace))
                    throw Error(AppErrorCode.DYNAMIC_FLOW_STEP_ROLE_REQUIRED, $"nodes[{index}].declaredRoles");
                if (node.DeclaredRoles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != node.DeclaredRoles.Count)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN, $"nodes[{index}].declaredRoles");
                if (node.Gateway is not null)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_NODE_KIND_UNSUPPORTED, $"nodes[{index}].gateway");
            }
            else if (node.NodeKind == DynamicFlowNodeKinds.Gateway)
            {
                if (node.Gateway is null || !DynamicFlowGatewayKinds.All.Contains(node.Gateway.Kind))
                    throw Error(AppErrorCode.DYNAMIC_FLOW_NODE_KIND_UNSUPPORTED, $"nodes[{index}].gateway.kind");
                if (node.FormNodeId is not null)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_STEP_FORM_NODE_MISMATCH, $"nodes[{index}].formNodeId");
            }
            else if (node.Gateway is not null || node.FormNodeId is not null)
            {
                throw Error(AppErrorCode.DYNAMIC_FLOW_FINAL_NODE_INVALID, $"nodes[{index}]");
            }
        }

        var incoming = payload.Nodes.ToDictionary(node => node.NodeId, _ => 0, StringComparer.Ordinal);
        var outgoing = payload.Nodes.ToDictionary(node => node.NodeId, _ => new List<int>(), StringComparer.Ordinal);
        var transitionIds = new HashSet<string>(StringComparer.Ordinal);
        var endpointPairs = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < payload.Edges.Count; index++)
        {
            var edge = payload.Edges[index];
            if (string.IsNullOrWhiteSpace(edge.TransitionId) || !transitionIds.Add(edge.TransitionId))
                throw Error(AppErrorCode.DYNAMIC_FLOW_TRANSITION_ID_DUPLICATE, $"edges[{index}].transitionId");
            if (!nodeById.ContainsKey(edge.FromNodeId))
                throw Error(AppErrorCode.DYNAMIC_FLOW_EDGE_NODE_UNKNOWN, $"edges[{index}].fromNodeId");
            if (!nodeById.ContainsKey(edge.ToNodeId))
                throw Error(AppErrorCode.DYNAMIC_FLOW_EDGE_NODE_UNKNOWN, $"edges[{index}].toNodeId");
            if (edge.FromNodeId == edge.ToNodeId)
                throw Error(AppErrorCode.DYNAMIC_FLOW_EDGE_SELF_LOOP, $"edges[{index}]");
            if (!endpointPairs.Add(edge.FromNodeId + "\0" + edge.ToNodeId))
                throw Error(AppErrorCode.DYNAMIC_FLOW_EDGE_DUPLICATE, $"edges[{index}]");
            incoming[edge.ToNodeId]++;
            outgoing[edge.FromNodeId].Add(index);
            if (edge.Condition is not null)
            {
                var conditionNodeCount = 0;
                ValidateCondition(edge.Condition, $"edges[{index}].condition", 1, ref conditionNodeCount);
            }
        }

        var roots = incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key).ToArray();
        if (roots.Length != 1)
            throw Error(AppErrorCode.DYNAMIC_FLOW_ROOT_COUNT_INVALID, "nodes", new { actual = roots.Length });

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(roots[0]);
        while (queue.TryDequeue(out var current))
        {
            if (!reached.Add(current))
                continue;
            foreach (var edgeIndex in outgoing[current])
                queue.Enqueue(payload.Edges[edgeIndex].ToNodeId);
        }
        for (var index = 0; index < payload.Nodes.Count; index++)
        {
            if (!reached.Contains(payload.Nodes[index].NodeId))
                throw Error(AppErrorCode.DYNAMIC_FLOW_NODE_UNREACHABLE, $"nodes[{index}]");
        }

        var remainingIncoming = new Dictionary<string, int>(incoming, StringComparer.Ordinal);
        var acyclicQueue = new Queue<string>(roots);
        var visitedCount = 0;
        while (acyclicQueue.TryDequeue(out var current))
        {
            visitedCount++;
            foreach (var edgeIndex in outgoing[current])
            {
                var target = payload.Edges[edgeIndex].ToNodeId;
                if (--remainingIncoming[target] == 0)
                    acyclicQueue.Enqueue(target);
            }
        }
        if (visitedCount != payload.Nodes.Count)
        {
            var cycleEdgeIndex = payload.Edges.FindIndex(edge => remainingIncoming[edge.FromNodeId] > 0 && remainingIncoming[edge.ToNodeId] > 0);
            throw Error(AppErrorCode.DYNAMIC_FLOW_CYCLE_FORBIDDEN, cycleEdgeIndex < 0 ? "edges" : $"edges[{cycleEdgeIndex}]");
        }

        if (!nodeById.TryGetValue(payload.EntryStepId, out var entry) ||
            entry.NodeKind != DynamicFlowNodeKinds.FormStep ||
            !reached.Contains(entry.NodeId))
        {
            throw Error(AppErrorCode.DYNAMIC_FLOW_ENTRY_STEP_INVALID, "entryStepId");
        }

        var terminalCount = 0;
        for (var index = 0; index < payload.Nodes.Count; index++)
        {
            var node = payload.Nodes[index];
            if (outgoing[node.NodeId].Count == 0)
                terminalCount++;
            if (node.NodeKind == DynamicFlowNodeKinds.Final && outgoing[node.NodeId].Count != 0)
                throw Error(AppErrorCode.DYNAMIC_FLOW_FINAL_NODE_INVALID, $"nodes[{index}]");
        }
        if (terminalCount == 0)
            throw Error(AppErrorCode.DYNAMIC_FLOW_FINAL_NODE_INVALID, "nodes");

        ValidateGatewayCardinality(payload, incoming, outgoing);
        ValidateRootForm(payload, entry, formNodeById);
    }

    private static void ValidateCondition(
        DynamicFlowConditionDto condition,
        string path,
        int depth,
        ref int totalNodeCount)
    {
        totalNodeCount++;
        if (depth > MaxConditionDepth || totalNodeCount > MaxConditionNodes)
            throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_BUDGET_EXCEEDED, path);
        if (!ConditionOperators.Contains(condition.Operator))
            throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, $"{path}.operator");

        var hasField = !string.IsNullOrWhiteSpace(condition.Field);
        var hasValue = condition.Value.HasValue;
        var hasValues = condition.Values.Count > 0;
        var childCount = condition.Children.Count;
        switch (condition.Operator)
        {
            case "AND":
            case "OR":
                if (childCount < 2 || hasField || hasValue || hasValues)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, path);
                break;
            case "NOT":
                if (childCount != 1 || hasField || hasValue || hasValues)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, path);
                break;
            case "IN":
            case "NOT_IN":
                if (!hasField || !hasValues || childCount != 0 || hasValue)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, path);
                break;
            case "EXISTS":
            case "NOT_EXISTS":
            case "IS_NULL":
            case "IS_NOT_NULL":
                if (!hasField || hasValue || hasValues || childCount != 0)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, path);
                break;
            case "TRUE":
            case "FALSE":
                if (hasField || hasValue || hasValues || childCount != 0)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, path);
                break;
            default:
                if (!hasField || !hasValue || hasValues || childCount != 0 ||
                    condition.Value!.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    throw Error(AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID, path);
                }
                break;
        }

        for (var index = 0; index < condition.Children.Count; index++)
            ValidateCondition(condition.Children[index], $"{path}.children[{index}]", depth + 1, ref totalNodeCount);
    }

    private static void ValidateGatewayCardinality(
        DynamicFlowTemplatePayloadDto payload,
        IReadOnlyDictionary<string, int> incoming,
        IReadOnlyDictionary<string, List<int>> outgoing)
    {
        for (var index = 0; index < payload.Nodes.Count; index++)
        {
            var node = payload.Nodes[index];
            if (node.Gateway is null)
                continue;
            var path = $"nodes[{index}].gateway";
            var gateway = node.Gateway;
            if (gateway.Kind == DynamicFlowGatewayKinds.Fork && outgoing[node.NodeId].Count < 2)
                throw Error(AppErrorCode.DYNAMIC_FLOW_FORK_INVALID, path);

            if (gateway.Kind is DynamicFlowGatewayKinds.JoinAll or DynamicFlowGatewayKinds.JoinAny or DynamicFlowGatewayKinds.JoinNOfM)
            {
                if (incoming[node.NodeId] < 2)
                    throw Error(AppErrorCode.DYNAMIC_FLOW_JOIN_INVALID, path);
                if (gateway.ExpectedIncomingNodeIds.Count > 0)
                {
                    var actual = payload.Edges
                        .Where(edge => edge.ToNodeId == node.NodeId)
                        .Select(edge => edge.FromNodeId)
                        .ToHashSet(StringComparer.Ordinal);
                    if (!actual.SetEquals(gateway.ExpectedIncomingNodeIds))
                        throw Error(AppErrorCode.DYNAMIC_FLOW_JOIN_INVALID, $"{path}.expectedIncomingNodeIds");
                }
                if (gateway.Kind == DynamicFlowGatewayKinds.JoinNOfM &&
                    (gateway.RequiredIncomingCount is null or < 1 || gateway.RequiredIncomingCount > incoming[node.NodeId]))
                {
                    throw Error(AppErrorCode.DYNAMIC_FLOW_JOIN_INVALID, $"{path}.requiredIncomingCount");
                }
            }
        }
    }

    private static void ValidateRootForm(
        DynamicFlowTemplatePayloadDto payload,
        DynamicFlowTopologyNodeDto entry,
        IReadOnlyDictionary<string, DynamicFlowFormNodeDto> formNodeById)
    {
        if (string.IsNullOrWhiteSpace(payload.RootDynamicFormTemplateId))
            return;
        if (entry.FormNodeId is null ||
            !formNodeById.TryGetValue(entry.FormNodeId, out var entryForm) ||
            !string.Equals(entryForm.DynamicFormTemplateId, payload.RootDynamicFormTemplateId, StringComparison.Ordinal))
        {
            throw Error(AppErrorCode.DYNAMIC_FLOW_STEP_FORM_NODE_MISMATCH, "rootDynamicFormTemplateId");
        }
    }

    private static void ValidateOwners(DynamicFlowTemplatePayloadDto payload)
    {
        ValidateOwnerPair(
            payload,
            payload.ResultOwnerStepId,
            payload.ResultOwnerFormNodeId,
            "resultOwnerStepId",
            "resultOwnerFormNodeId");
        ValidateOwnerPair(
            payload,
            payload.StatisticsOwnerStepId,
            payload.StatisticsOwnerFormNodeId,
            "statisticsOwnerStepId",
            "statisticsOwnerFormNodeId");
    }

    private static void ValidateOwnerPair(
        DynamicFlowTemplatePayloadDto payload,
        string? stepId,
        string? formNodeId,
        string stepPath,
        string formPath)
    {
        if (stepId is null && formNodeId is null)
            return;
        if (string.IsNullOrWhiteSpace(stepId) || string.IsNullOrWhiteSpace(formNodeId))
            throw Error(AppErrorCode.DYNAMIC_FLOW_OWNER_INVALID, stepId is null ? stepPath : formPath);
        var step = payload.Nodes.SingleOrDefault(node => node.NodeId == stepId);
        if (step is null || step.NodeKind != DynamicFlowNodeKinds.FormStep || step.FormNodeId != formNodeId)
            throw Error(AppErrorCode.DYNAMIC_FLOW_OWNER_INVALID, stepPath);
    }

    private static void ValidateArchetypeTopology(DynamicFlowTemplatePayloadDto payload)
    {
        var gatewayKinds = payload.Nodes
            .Where(node => node.Gateway is not null)
            .Select(node => node.Gateway!.Kind)
            .ToHashSet(StringComparer.Ordinal);
        var valid = payload.ArchetypeId switch
        {
            "FLOW-T01" => payload.Nodes.Count == 1 && payload.Edges.Count == 0 &&
                          payload.Nodes[0].NodeKind == DynamicFlowNodeKinds.FormStep,
            "FLOW-T02" => payload.Nodes.Count == 1 && payload.Nodes[0].NodeKind == DynamicFlowNodeKinds.FormStep,
            "FLOW-T03" => payload.Nodes.Count >= 2 && gatewayKinds.Count == 0,
            "FLOW-T04" => gatewayKinds.Contains(DynamicFlowGatewayKinds.Fork),
            "FLOW-T05" => gatewayKinds.Contains(DynamicFlowGatewayKinds.JoinAll),
            "FLOW-T06" => gatewayKinds.Contains(DynamicFlowGatewayKinds.JoinAny) || gatewayKinds.Contains(DynamicFlowGatewayKinds.JoinNOfM),
            "FLOW-T07" => gatewayKinds.Contains(DynamicFlowGatewayKinds.Condition) || payload.Edges.Any(edge => edge.Condition is not null),
            "FLOW-T08" => gatewayKinds.Contains(DynamicFlowGatewayKinds.Review),
            "FLOW-T09" => gatewayKinds.Contains(DynamicFlowGatewayKinds.Subflow),
            "FLOW-T10" => gatewayKinds.Contains(DynamicFlowGatewayKinds.Schedule),
            "FLOW-T11" => payload.Nodes.Any(node => node.NodeKind == DynamicFlowNodeKinds.FormStep),
            "FLOW-T12" => gatewayKinds.Contains(DynamicFlowGatewayKinds.RollbackFinalize),
            _ => false
        };
        if (!valid)
            throw Error(AppErrorCode.DYNAMIC_FLOW_ARCHETYPE_TOPOLOGY_INVALID, "archetypeId");
    }

    private static void ValidateStoredPins(
        DynamicFlowTemplatePayloadDto payload,
        DynamicFlowDefinitionValidationOptions options)
    {
        if (!options.AllowServerManagedPins)
            return;

        var isCurrent = DynamicFlowStoredCatalogPinPolicy.IsCurrent(
            payload.CatalogVersion,
            payload.CatalogSemanticHash);
        var isAllowedNonCurrent =
            options.AllowHistoricalCatalogPins &&
            (DynamicFlowStoredCatalogPinPolicy.IsHistoricalV11(
                 payload.CatalogVersion,
                 payload.CatalogSemanticHash) ||
             DynamicFlowStoredCatalogPinPolicy.IsHistoricalV12(
                 payload.CatalogVersion,
                 payload.CatalogSemanticHash) ||
             DynamicFlowStoredCatalogPinPolicy.IsHistoricalV13(
                 payload.CatalogVersion,
                 payload.CatalogSemanticHash) ||
             DynamicFlowStoredCatalogPinPolicy.IsP7Candidate(
                 payload.CatalogVersion,
                 payload.CatalogSemanticHash));
        var mentionsAnyPin =
            payload.CatalogVersion is not null ||
            payload.CatalogSemanticHash is not null;
        if (mentionsAnyPin && !isCurrent && !isAllowedNonCurrent)
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "catalogVersion");
        if (options.RequireServerManagedPins &&
            !isCurrent &&
            !isAllowedNonCurrent)
        {
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "catalogVersion");
        }

        for (var index = 0; index < payload.FormNodes.Count; index++)
        {
            var formNode = payload.FormNodes[index];
            if (!options.RequireServerManagedPins)
                continue;
            var anyPin = formNode.DynamicFormFamilyId is not null || formNode.DynamicFormVersionNo is not null ||
                         formNode.DynamicFormSchemaHash is not null || formNode.DynamicFormSnapshotHash is not null;
            if (!anyPin && !options.RequireServerManagedPins)
                continue;
            if (string.IsNullOrWhiteSpace(formNode.DynamicFormFamilyId) || formNode.DynamicFormVersionNo is null or < 1 ||
                !IsSha256(formNode.DynamicFormSchemaHash) || !IsSha256(formNode.DynamicFormSnapshotHash))
            {
                throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, $"formNodes[{index}]");
            }
        }
    }

    private static void NormalizeIdentifiers(DynamicFlowTemplatePayloadDto payload)
    {
        payload.ArchetypeId = NormalizeUpper(payload.ArchetypeId);
        payload.EntryStepId = Normalize(payload.EntryStepId);
        payload.RootDynamicFormTemplateId = NormalizeNullable(payload.RootDynamicFormTemplateId);
        payload.ResultOwnerStepId = NormalizeNullable(payload.ResultOwnerStepId);
        payload.ResultOwnerFormNodeId = NormalizeNullable(payload.ResultOwnerFormNodeId);
        payload.StatisticsOwnerStepId = NormalizeNullable(payload.StatisticsOwnerStepId);
        payload.StatisticsOwnerFormNodeId = NormalizeNullable(payload.StatisticsOwnerFormNodeId);
        foreach (var formNode in payload.FormNodes)
        {
            formNode.FormNodeId = Normalize(formNode.FormNodeId);
            formNode.Role = Normalize(formNode.Role);
            formNode.DynamicFormTemplateId = Normalize(formNode.DynamicFormTemplateId);
            formNode.DynamicFormFamilyId = NormalizeNullable(formNode.DynamicFormFamilyId);
            formNode.DynamicFormSchemaHash = NormalizeLowerNullable(formNode.DynamicFormSchemaHash);
            formNode.DynamicFormSnapshotHash = NormalizeLowerNullable(formNode.DynamicFormSnapshotHash);
        }
        foreach (var node in payload.Nodes)
        {
            node.NodeId = Normalize(node.NodeId);
            node.NodeCode = Normalize(node.NodeCode);
            node.NodeKind = NormalizeUpper(node.NodeKind);
            node.FormNodeId = NormalizeNullable(node.FormNodeId);
            node.DeclaredRoles = node.DeclaredRoles.Select(NormalizeUpper).ToList();
            if (node.Gateway is not null)
            {
                node.Gateway.Kind = NormalizeUpper(node.Gateway.Kind);
                node.Gateway.ExpectedIncomingNodeIds = node.Gateway.ExpectedIncomingNodeIds.Select(Normalize).ToList();
            }
        }
        foreach (var edge in payload.Edges)
        {
            edge.TransitionId = Normalize(edge.TransitionId);
            edge.FromNodeId = Normalize(edge.FromNodeId);
            edge.ToNodeId = Normalize(edge.ToNodeId);
            if (edge.Condition is not null)
                NormalizeCondition(edge.Condition);
        }

        NormalizeAndSortPolicies(payload);
    }

    private static void NormalizeAndSortPolicies(DynamicFlowTemplatePayloadDto payload)
    {
        foreach (var policy in payload.ActorPolicies)
        {
            policy.PolicyId = NormalizeNullable(policy.PolicyId);
            policy.StepId = NormalizeNullable(policy.StepId);
            policy.StepCode = NormalizeUpperNullable(policy.StepCode);
            policy.ActorRole = NormalizeUpperNullable(policy.ActorRole);
        }
        foreach (var policy in payload.FieldPolicies)
        {
            policy.PolicyId = NormalizeNullable(policy.PolicyId);
            policy.DynamicFormTemplateId = NormalizeNullable(policy.DynamicFormTemplateId);
            policy.StepId = NormalizeNullable(policy.StepId);
            policy.StepCode = NormalizeUpperNullable(policy.StepCode);
            policy.ActorRole = NormalizeUpperNullable(policy.ActorRole);
            policy.FieldId = NormalizeNullable(policy.FieldId);
            policy.FieldKey = NormalizeNullable(policy.FieldKey);
        }
        foreach (var policy in payload.TableColumnPolicies)
        {
            policy.PolicyId = NormalizeNullable(policy.PolicyId);
            policy.DynamicFormTemplateId = NormalizeNullable(policy.DynamicFormTemplateId);
            policy.StepId = NormalizeNullable(policy.StepId);
            policy.StepCode = NormalizeUpperNullable(policy.StepCode);
            policy.ActorRole = NormalizeUpperNullable(policy.ActorRole);
            policy.BlockId = NormalizeNullable(policy.BlockId);
            policy.ColumnKey = NormalizeNullable(policy.ColumnKey);
        }

        payload.ActorPolicies = payload.ActorPolicies
            .OrderBy(CanonicalPolicySortKey, StringComparer.Ordinal)
            .ToList();
        payload.FieldPolicies = payload.FieldPolicies
            .OrderBy(CanonicalPolicySortKey, StringComparer.Ordinal)
            .ToList();
        payload.TableColumnPolicies = payload.TableColumnPolicies
            .OrderBy(CanonicalPolicySortKey, StringComparer.Ordinal)
            .ToList();
    }

    private static string CanonicalPolicySortKey<TPolicy>(TPolicy policy)
        => CanonicalizeNode(
            JsonSerializer.SerializeToNode(policy, StrictJsonOptions)
            ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "policy"));

    private static void NormalizeCondition(DynamicFlowConditionDto condition)
    {
        condition.Operator = NormalizeUpper(condition.Operator);
        condition.Field = NormalizeNullable(condition.Field);
        foreach (var child in condition.Children)
            NormalizeCondition(child);
    }

    private static void ValidateConfiguredPolicyRoles(
        DynamicFlowTemplatePayloadDto payload,
        IReadOnlyList<DynamicFlowTopologyNodeDto> steps)
    {
        for (var index = 0; index < payload.ActorPolicies.Count; index++)
        {
            var policy = payload.ActorPolicies[index];
            if (!steps.Any(step => MatchesStep(policy.StepId, policy.StepCode, step) &&
                                   (IsWildcard(policy.ActorRole) || step.DeclaredRoles.Contains(policy.ActorRole!, StringComparer.OrdinalIgnoreCase))))
            {
                throw Error(AppErrorCode.DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN, $"actorPolicies[{index}].actorRole");
            }
        }
        for (var index = 0; index < payload.FieldPolicies.Count; index++)
        {
            var policy = payload.FieldPolicies[index];
            if (!steps.Any(step => MatchesStep(policy.StepId, policy.StepCode, step) &&
                                   (IsWildcard(policy.ActorRole) || step.DeclaredRoles.Contains(policy.ActorRole!, StringComparer.OrdinalIgnoreCase))))
            {
                throw Error(AppErrorCode.DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN, $"fieldPolicies[{index}].actorRole");
            }
        }
        for (var index = 0; index < payload.TableColumnPolicies.Count; index++)
        {
            var policy = payload.TableColumnPolicies[index];
            if (!steps.Any(step => MatchesStep(policy.StepId, policy.StepCode, step) &&
                                   (IsWildcard(policy.ActorRole) || step.DeclaredRoles.Contains(policy.ActorRole!, StringComparer.OrdinalIgnoreCase))))
            {
                throw Error(AppErrorCode.DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN, $"tableColumnPolicies[{index}].actorRole");
            }
        }
    }

    private static void ValidateConfiguredPolicyEndpoints(
        DynamicFlowTemplatePayloadDto payload,
        IReadOnlyDictionary<string, DynamicFlowFormEndpointCatalog> endpointCatalogs)
    {
        var steps = payload.Nodes.Where(node => node.NodeKind == DynamicFlowNodeKinds.FormStep).ToArray();
        for (var index = 0; index < payload.FieldPolicies.Count; index++)
        {
            var policy = payload.FieldPolicies[index];
            if (IsWildcard(policy.FieldId) && IsWildcard(policy.FieldKey))
                continue;
            var known = steps.Any(step =>
                MatchesStep(policy.StepId, policy.StepCode, step) &&
                MatchesDynamicForm(policy.DynamicFormTemplateId, step, payload.FormNodes) &&
                step.FormNodeId is not null && endpointCatalogs.TryGetValue(step.FormNodeId, out var catalog) &&
                catalog.ScalarFields.Any(field => MatchesSelector(policy.FieldId, field.FieldId) && MatchesSelector(policy.FieldKey, field.FieldKey)));
            if (!known)
                throw Error(AppErrorCode.DYNAMIC_FLOW_POLICY_FIELD_UNKNOWN, $"fieldPolicies[{index}].fieldId");
        }

        for (var index = 0; index < payload.TableColumnPolicies.Count; index++)
        {
            var policy = payload.TableColumnPolicies[index];
            if (IsWildcard(policy.BlockId) && IsWildcard(policy.ColumnKey))
                continue;
            var known = steps.Any(step =>
                MatchesStep(policy.StepId, policy.StepCode, step) &&
                MatchesDynamicForm(policy.DynamicFormTemplateId, step, payload.FormNodes) &&
                step.FormNodeId is not null && endpointCatalogs.TryGetValue(step.FormNodeId, out var catalog) &&
                catalog.TableColumns.Any(column => MatchesSelector(policy.BlockId, column.BlockId) && MatchesSelector(policy.ColumnKey, column.ColumnKey)));
            if (!known)
                throw Error(AppErrorCode.DYNAMIC_FLOW_POLICY_FIELD_UNKNOWN, $"tableColumnPolicies[{index}].blockId");
        }
    }

    private static bool MatchesFieldPolicy(
        DynamicFlowFieldPolicyDto policy,
        DynamicFlowTopologyNodeDto step,
        string role,
        DynamicFlowScalarEndpoint field,
        IReadOnlyList<DynamicFlowFormNodeDto> formNodes)
        => MatchesStep(policy.StepId, policy.StepCode, step) &&
           MatchesRole(policy.ActorRole, role) &&
           MatchesDynamicForm(policy.DynamicFormTemplateId, step, formNodes) &&
           MatchesSelector(policy.FieldId, field.FieldId) &&
           MatchesSelector(policy.FieldKey, field.FieldKey);

    private static bool MatchesTablePolicy(
        DynamicFlowTableColumnPolicyDto policy,
        DynamicFlowTopologyNodeDto step,
        string role,
        DynamicFlowTableColumnEndpoint column,
        IReadOnlyList<DynamicFlowFormNodeDto> formNodes)
        => MatchesStep(policy.StepId, policy.StepCode, step) &&
           MatchesRole(policy.ActorRole, role) &&
           MatchesDynamicForm(policy.DynamicFormTemplateId, step, formNodes) &&
           MatchesSelector(policy.BlockId, column.BlockId) &&
           MatchesSelector(policy.ColumnKey, column.ColumnKey);

    private static bool MatchesStep(string? policyStepId, string? policyStepCode, DynamicFlowTopologyNodeDto step)
        => MatchesSelector(policyStepId, step.NodeId) && MatchesSelector(policyStepCode, step.NodeCode);

    private static bool MatchesRole(string? policyRole, string role)
        => MatchesSelector(policyRole, role);

    private static bool MatchesDynamicForm(
        string? policyDynamicFormTemplateId,
        DynamicFlowTopologyNodeDto step,
        IReadOnlyList<DynamicFlowFormNodeDto> formNodes)
    {
        if (IsWildcard(policyDynamicFormTemplateId))
            return true;
        if (string.IsNullOrWhiteSpace(policyDynamicFormTemplateId))
            return false;
        var formNode = formNodes.SingleOrDefault(node => node.FormNodeId == step.FormNodeId);
        return formNode is not null && string.Equals(
            policyDynamicFormTemplateId,
            formNode.DynamicFormTemplateId,
            StringComparison.Ordinal);
    }

    private static bool MatchesSelector(string? selector, string? value)
        => !string.IsNullOrWhiteSpace(selector) &&
           (IsWildcard(selector) ||
            string.Equals(selector, value, StringComparison.OrdinalIgnoreCase));

    private static bool IsWildcard(string? value)
        => string.Equals(value, "*", StringComparison.Ordinal);

    private static int ActorSpecificity(DynamicFlowActorPolicyDto policy)
        => SelectorSpecificity(policy.StepId, 4) + SelectorSpecificity(policy.StepCode, 2) + SelectorSpecificity(policy.ActorRole, 1);

    private static int FieldSpecificity(DynamicFlowFieldPolicyDto policy)
        => SelectorSpecificity(policy.DynamicFormTemplateId, 16) + SelectorSpecificity(policy.StepId, 8) +
           SelectorSpecificity(policy.StepCode, 4) + SelectorSpecificity(policy.ActorRole, 2) +
           Math.Max(SelectorSpecificity(policy.FieldId, 1), SelectorSpecificity(policy.FieldKey, 1));

    private static int TableSpecificity(DynamicFlowTableColumnPolicyDto policy)
        => SelectorSpecificity(policy.DynamicFormTemplateId, 32) + SelectorSpecificity(policy.StepId, 16) +
           SelectorSpecificity(policy.StepCode, 8) + SelectorSpecificity(policy.ActorRole, 4) +
           SelectorSpecificity(policy.BlockId, 2) + SelectorSpecificity(policy.ColumnKey, 1);

    private static int SelectorSpecificity(string? selector, int weight)
        => string.IsNullOrWhiteSpace(selector) || IsWildcard(selector) ? 0 : weight;

    private static string ActorOutcome(DynamicFlowActorPolicyDto policy)
        => Outcome(policy.AllowSubFlow, policy.AllowForward, policy.CanFinalize);

    private static string FieldOutcome(DynamicFlowFieldPolicyDto policy)
        => Outcome(policy.Read, policy.Write, policy.Required, policy.Hidden, policy.Locked, policy.LockedAfterSubmit);

    private static string TableOutcome(DynamicFlowTableColumnPolicyDto policy)
        => Outcome(policy.Read, policy.Write, policy.Required, policy.Hidden, policy.Locked, policy.LockedAfterSubmit);

    private static string Outcome(params bool?[] values)
        => string.Join('|', values.Select(value => value is null ? "_" : value.Value ? "1" : "0"));

    private static void EnsureNoEqualSpecificityConflict<TPolicy>(
        IReadOnlyList<(TPolicy Policy, int Index)> matches,
        Func<(TPolicy Policy, int Index), int> getSpecificity,
        Func<(TPolicy Policy, int Index), string> getOutcome,
        string path)
    {
        var maximum = matches.Max(getSpecificity);
        var effective = matches.Where(item => getSpecificity(item) == maximum).ToArray();
        if (effective.Select(getOutcome).Distinct(StringComparer.Ordinal).Count() <= 1)
            return;
        throw Error(
            AppErrorCode.DYNAMIC_FLOW_POLICY_SPECIFICITY_CONFLICT,
            $"{path}[{effective[1].Index}]",
            new { firstPolicyIndex = effective[0].Index, secondPolicyIndex = effective[1].Index });
    }

    private static bool HasFlatMappingEndpointMetadata(DynamicFlowMappingRuleDto rule)
        => new[]
        {
            rule.SourceDynamicFormTemplateId,
            rule.SourceStepId,
            rule.SourceStepCode,
            rule.SourceSectionId,
            rule.SourceSectionCode,
            rule.SourceFieldId,
            rule.SourceFieldKey,
            rule.SourceBlockId,
            rule.SourceColumnKey,
            rule.TargetDynamicFormTemplateId,
            rule.TargetStepId,
            rule.TargetStepCode,
            rule.TargetSectionId,
            rule.TargetSectionCode,
            rule.TargetFieldId,
            rule.TargetFieldKey,
            rule.TargetBlockId,
            rule.TargetColumnKey
        }.Any(value => !string.IsNullOrWhiteSpace(value));

    private static void ValidateStructuredMappingEndpointKind(
        string? kind,
        string path,
        bool allowConstant)
    {
        if (kind is "FIELD" or "TABLE_COLUMN" || allowConstant && kind == "CONSTANT")
            return;
        throw MappingDefinitionError(
            $"{path}.kind",
            "DYNAMIC_FLOW_MAPPING_ENDPOINT_KIND_INVALID",
            new { kind });
    }

    private static void ValidateConstantMappingEndpoint(
        DynamicFlowMappingEndpointDto endpoint,
        string path)
    {
        if (new[]
            {
                endpoint.DynamicFormTemplateId,
                endpoint.StepId,
                endpoint.StepCode,
                endpoint.SectionId,
                endpoint.SectionCode,
                endpoint.FieldId,
                endpoint.FieldKey,
                endpoint.BlockId,
                endpoint.ColumnKey,
                endpoint.RowKey
            }.Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            throw MappingDefinitionError(
                path,
                "DYNAMIC_FLOW_MAPPING_CONSTANT_ENDPOINT_CONTEXT_FORBIDDEN");
        }
    }

    private static void EnsureMappingKindMatchesEndpoint(
        string? mappingKind,
        string targetKind,
        string path)
    {
        var normalized = NormalizeUpperNullable(mappingKind);
        if (normalized is null)
            return;
        var matches = normalized switch
        {
            "FIELD" => targetKind == "FIELD",
            "TABLE" or "TABLE_COLUMN" => targetKind == "TABLE_COLUMN",
            _ => false
        };
        if (!matches)
        {
            throw MappingDefinitionError(
                path,
                "DYNAMIC_FLOW_MAPPING_KIND_ENDPOINT_MISMATCH",
                new { mappingKind = normalized, targetKind });
        }
    }

    private static void EnsureFlatMappingKindCompatibility(
        string? mappingKind,
        MappingEndpoint source,
        MappingEndpoint target,
        string rulePath)
    {
        if (source.Kind.Length == 0)
            throw MappingDefinitionError(rulePath, "DYNAMIC_FLOW_MAPPING_SOURCE_REQUIRED");
        if (target.Kind.Length == 0)
            throw MappingDefinitionError(rulePath, "DYNAMIC_FLOW_MAPPING_TARGET_REQUIRED");
        if (source.Kind == "AMBIGUOUS")
            throw MappingDefinitionError(rulePath, "DYNAMIC_FLOW_MAPPING_SOURCE_AMBIGUOUS");
        if (target.Kind == "AMBIGUOUS")
            throw MappingDefinitionError(rulePath, "DYNAMIC_FLOW_MAPPING_TARGET_AMBIGUOUS");

        var normalized = NormalizeUpperNullable(mappingKind);
        if (normalized is not null)
        {
            var matches = normalized switch
            {
                "FIELD" => source.Kind == "FIELD" && target.Kind == "FIELD",
                "TABLE" or "TABLE_COLUMN" => source.Kind == "TABLE_COLUMN" && target.Kind == "TABLE_COLUMN",
                _ => false
            };
            if (!matches)
            {
                throw MappingDefinitionError(
                    $"{rulePath}.mappingKind",
                    "DYNAMIC_FLOW_MAPPING_KIND_ENDPOINT_MISMATCH",
                    new { mappingKind = normalized, sourceKind = source.Kind, targetKind = target.Kind });
            }
        }

        if (target.Kind == "TABLE_COLUMN" && source.Kind != "TABLE_COLUMN")
        {
            throw MappingDefinitionError(
                rulePath,
                "DYNAMIC_FLOW_MAPPING_TABLE_TARGET_ROW_CONTEXT_REQUIRED");
        }
    }

    private static string ValidateMappingEndpoint(
        IReadOnlyList<DynamicFlowTopologyNodeDto> steps,
        IReadOnlyDictionary<string, DynamicFlowFormNodeDto> formNodes,
        IReadOnlyDictionary<string, DynamicFlowFormEndpointCatalog> endpointCatalogs,
        MappingEndpoint endpoint,
        bool isTarget)
    {
        var context = ResolveMappingContext(steps, formNodes, endpointCatalogs, endpoint);
        if (endpoint.Kind == "FIELD")
        {
            if (string.IsNullOrWhiteSpace(endpoint.FieldId) && string.IsNullOrWhiteSpace(endpoint.FieldKey))
            {
                throw MappingDefinitionError(
                    endpoint.Path,
                    "DYNAMIC_FLOW_MAPPING_FIELD_REQUIRED");
            }
            if (!string.IsNullOrWhiteSpace(endpoint.BlockId) || !string.IsNullOrWhiteSpace(endpoint.ColumnKey))
            {
                throw MappingDefinitionError(
                    endpoint.Path,
                    "DYNAMIC_FLOW_MAPPING_ENDPOINT_KIND_MISMATCH");
            }

            var matches = context.Catalog.ScalarFields
                .Where(field =>
                    (string.IsNullOrWhiteSpace(endpoint.FieldId) ||
                     string.Equals(endpoint.FieldId.Trim(), field.FieldId, StringComparison.Ordinal)) &&
                    (string.IsNullOrWhiteSpace(endpoint.FieldKey) ||
                     string.Equals(endpoint.FieldKey.Trim(), field.FieldKey, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (matches.Length != 1)
            {
                var path = !string.IsNullOrWhiteSpace(endpoint.FieldId)
                    ? endpoint.PropertyPath("fieldId")
                    : endpoint.PropertyPath("fieldKey");
                throw MappingEndpointError(
                    path,
                    "DYNAMIC_FLOW_MAPPING_FIELD_ENDPOINT_UNKNOWN_OR_MISMATCH",
                    new { endpoint.FieldId, endpoint.FieldKey, stepId = context.Step.NodeId });
            }

            return $"{context.Step.NodeId}\nFIELD\n{matches[0].FieldId}";
        }

        if (endpoint.Kind != "TABLE_COLUMN")
            throw MappingDefinitionError(endpoint.Path, "DYNAMIC_FLOW_MAPPING_ENDPOINT_KIND_INVALID");
        if (string.IsNullOrWhiteSpace(endpoint.BlockId) || string.IsNullOrWhiteSpace(endpoint.ColumnKey))
        {
            throw MappingDefinitionError(
                endpoint.Path,
                "DYNAMIC_FLOW_MAPPING_TABLE_COLUMN_REQUIRED");
        }
        if (!string.IsNullOrWhiteSpace(endpoint.FieldId) || !string.IsNullOrWhiteSpace(endpoint.FieldKey))
        {
            throw MappingDefinitionError(
                endpoint.Path,
                "DYNAMIC_FLOW_MAPPING_ENDPOINT_KIND_MISMATCH");
        }

        var columns = context.Catalog.TableColumns
            .Where(column =>
                string.Equals(endpoint.BlockId.Trim(), column.BlockId, StringComparison.Ordinal) &&
                string.Equals(endpoint.ColumnKey.Trim(), column.ColumnKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (columns.Length != 1)
        {
            throw MappingEndpointError(
                endpoint.PropertyPath("blockId"),
                "DYNAMIC_FLOW_MAPPING_TABLE_ENDPOINT_UNKNOWN_OR_MISMATCH",
                new { endpoint.BlockId, endpoint.ColumnKey, stepId = context.Step.NodeId });
        }

        var column = columns[0];
        if (isTarget && column.TableMode is "APPEND_COLUMNS" or "SUMMARY_TEMPLATE")
        {
            throw MappingEndpointError(
                endpoint.PropertyPath("blockId"),
                "DYNAMIC_FLOW_MAPPING_TARGET_TABLE_MODE_UNSUPPORTED",
                new { column.BlockId, column.ColumnKey, column.TableMode });
        }

        return $"{context.Step.NodeId}\nTABLE_COLUMN\n{column.BlockId}\n{column.ColumnKey.ToLowerInvariant()}";
    }

    private static ResolvedMappingContext ResolveMappingContext(
        IReadOnlyList<DynamicFlowTopologyNodeDto> steps,
        IReadOnlyDictionary<string, DynamicFlowFormNodeDto> formNodes,
        IReadOnlyDictionary<string, DynamicFlowFormEndpointCatalog> endpointCatalogs,
        MappingEndpoint endpoint)
    {
        DynamicFlowTopologyNodeDto? byId = null;
        DynamicFlowTopologyNodeDto? byCode = null;
        if (!string.IsNullOrWhiteSpace(endpoint.StepId))
        {
            byId = steps.SingleOrDefault(step =>
                string.Equals(step.NodeId, endpoint.StepId.Trim(), StringComparison.Ordinal));
            if (byId is null)
            {
                throw MappingDefinitionError(
                    endpoint.PropertyPath("stepId"),
                    "DYNAMIC_FLOW_MAPPING_STEP_ID_UNKNOWN",
                    new { endpoint.StepId });
            }
        }
        if (!string.IsNullOrWhiteSpace(endpoint.StepCode))
        {
            byCode = steps.SingleOrDefault(step =>
                string.Equals(step.NodeCode, endpoint.StepCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byCode is null)
            {
                throw MappingDefinitionError(
                    endpoint.PropertyPath("stepCode"),
                    "DYNAMIC_FLOW_MAPPING_STEP_CODE_UNKNOWN",
                    new { endpoint.StepCode });
            }
        }
        if (byId is not null && byCode is not null && !ReferenceEquals(byId, byCode))
        {
            throw MappingDefinitionError(
                endpoint.Path,
                "DYNAMIC_FLOW_MAPPING_STEP_ID_CODE_MISMATCH",
                new { endpoint.StepId, endpoint.StepCode });
        }

        var step = byId ?? byCode;
        if (step is null)
        {
            var candidates = steps
                .Where(candidate =>
                    string.IsNullOrWhiteSpace(endpoint.DynamicFormTemplateId) ||
                    candidate.FormNodeId is not null &&
                    formNodes.TryGetValue(candidate.FormNodeId, out var formNode) &&
                    string.Equals(
                        formNode.DynamicFormTemplateId,
                        endpoint.DynamicFormTemplateId.Trim(),
                        StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length != 1)
            {
                throw MappingDefinitionError(
                    endpoint.PropertyPath("stepId"),
                    "DYNAMIC_FLOW_MAPPING_STEP_CONTEXT_REQUIRED",
                    new { endpoint.DynamicFormTemplateId, matchingSteps = candidates.Length });
            }
            step = candidates[0];
        }

        if (step.FormNodeId is null || !formNodes.TryGetValue(step.FormNodeId, out var resolvedFormNode))
        {
            throw MappingDefinitionError(
                endpoint.PropertyPath("stepId"),
                "DYNAMIC_FLOW_MAPPING_STEP_FORM_REFERENCE_INVALID",
                new { stepId = step.NodeId });
        }
        if (!string.IsNullOrWhiteSpace(endpoint.DynamicFormTemplateId) &&
            !string.Equals(
                endpoint.DynamicFormTemplateId.Trim(),
                resolvedFormNode.DynamicFormTemplateId,
                StringComparison.Ordinal))
        {
            throw MappingDefinitionError(
                endpoint.PropertyPath("dynamicFormTemplateId"),
                "DYNAMIC_FLOW_MAPPING_FORM_STEP_REFERENCE_MISMATCH",
                new
                {
                    dynamicFormTemplateId = endpoint.DynamicFormTemplateId,
                    stepDynamicFormTemplateId = resolvedFormNode.DynamicFormTemplateId
                });
        }
        if (!endpointCatalogs.TryGetValue(resolvedFormNode.FormNodeId, out var catalog))
        {
            throw MappingEndpointError(
                endpoint.PropertyPath("dynamicFormTemplateId"),
                "DYNAMIC_FLOW_MAPPING_FORM_ENDPOINT_CATALOG_MISSING",
                new { resolvedFormNode.FormNodeId, resolvedFormNode.DynamicFormTemplateId });
        }

        return new ResolvedMappingContext(step, resolvedFormNode, catalog);
    }

    private static AppException MappingDefinitionError(string path, string detailReason, object? context = null)
        => Error(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_DEFINITION_INVALID,
            path,
            new { detailReason, context });

    private static AppException MappingEndpointError(string path, string detailReason, object? context = null)
        => Error(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_ENDPOINT_INCOMPATIBLE,
            path,
            new { detailReason, context });

    private sealed record ResolvedMappingContext(
        DynamicFlowTopologyNodeDto Step,
        DynamicFlowFormNodeDto FormNode,
        DynamicFlowFormEndpointCatalog Catalog);

    private sealed record MappingEndpoint(
        string Kind,
        string? DynamicFormTemplateId,
        string? StepId,
        string? StepCode,
        string? FieldId,
        string? FieldKey,
        string? BlockId,
        string? ColumnKey,
        string Path,
        string? FlatPrefix)
    {
        public string PropertyPath(string propertyName)
        {
            if (FlatPrefix is null)
                return $"{Path}.{propertyName}";
            return $"{Path}.{FlatPrefix}{char.ToUpperInvariant(propertyName[0])}{propertyName[1..]}";
        }

        public static MappingEndpoint FromStructured(
            DynamicFlowMappingEndpointDto endpoint,
            string path,
            string kind)
            => new(
                kind,
                NormalizeNullable(endpoint.DynamicFormTemplateId),
                NormalizeNullable(endpoint.StepId),
                NormalizeNullable(endpoint.StepCode),
                NormalizeNullable(endpoint.FieldId),
                NormalizeNullable(endpoint.FieldKey),
                NormalizeNullable(endpoint.BlockId),
                NormalizeNullable(endpoint.ColumnKey),
                path,
                FlatPrefix: null);

        public static MappingEndpoint FromFlat(
            DynamicFlowMappingRuleDto rule,
            string path,
            bool isSource)
        {
            var fieldId = isSource ? rule.SourceFieldId : rule.TargetFieldId;
            var fieldKey = isSource ? rule.SourceFieldKey : rule.TargetFieldKey;
            var blockId = isSource ? rule.SourceBlockId : rule.TargetBlockId;
            var columnKey = isSource ? rule.SourceColumnKey : rule.TargetColumnKey;
            var hasField = !string.IsNullOrWhiteSpace(fieldId) || !string.IsNullOrWhiteSpace(fieldKey);
            var hasTable = !string.IsNullOrWhiteSpace(blockId) || !string.IsNullOrWhiteSpace(columnKey);
            var kind = hasField && hasTable
                ? "AMBIGUOUS"
                : hasField
                    ? "FIELD"
                    : hasTable
                        ? "TABLE_COLUMN"
                        : string.Empty;
            return new MappingEndpoint(
                kind,
                NormalizeNullable(isSource
                    ? rule.SourceDynamicFormTemplateId
                    : rule.TargetDynamicFormTemplateId),
                NormalizeNullable(isSource ? rule.SourceStepId : rule.TargetStepId),
                NormalizeNullable(isSource ? rule.SourceStepCode : rule.TargetStepCode),
                NormalizeNullable(fieldId),
                NormalizeNullable(fieldKey),
                NormalizeNullable(blockId),
                NormalizeNullable(columnKey),
                path,
                isSource ? "source" : "target");
        }
    }

    private static string CanonicalizeNode(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString(StrictJsonOptions));
        return CanonicalizeElement(document.RootElement);
    }

    private static string CanonicalizeElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                    writer.WriteNumberValue(integer);
                else if (element.TryGetDecimal(out var decimalValue))
                    writer.WriteNumberValue(decimalValue);
                else
                    writer.WriteNumberValue(element.GetDouble());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, "payload");
        }
    }

    private static void ValidateArrayObjects(
        JsonObject root,
        string property,
        IReadOnlySet<string> allowed,
        Action<JsonObject, string> nestedValidator)
    {
        if (!root.TryGetPropertyValue(property, out var value) || value is not JsonArray array)
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, property);
        for (var index = 0; index < array.Count; index++)
        {
            var item = array[index] as JsonObject
                       ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, $"{property}[{index}]");
            var path = $"{property}[{index}]";
            EnsureAllowedFields(item, allowed, path);
            nestedValidator(item, path);
        }
    }

    private static void EnsureAllowedFields(JsonObject value, IReadOnlySet<string> allowed, string path)
    {
        foreach (var property in value)
        {
            if (!allowed.Contains(property.Key))
            {
                var propertyPath = JoinPath(path, property.Key);
                if (IsCallerAuthorityField(property.Key))
                {
                    throw Error(
                        AppErrorCode.DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN,
                        propertyPath,
                        new { reason = "DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN" });
                }
                throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_UNKNOWN_FIELD, propertyPath);
            }
        }
    }

    private static bool IsCallerAuthorityField(string name)
        => name.Equals("callerRole", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("callerRoles", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ownerUserId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("ownerUnitId", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("executeGrant", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("canRead", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("canManage", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("canExecute", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("permissions", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("permission", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("grant", StringComparison.OrdinalIgnoreCase);

    private static JsonArray RequireArray(JsonObject value, string property, string errorPath)
    {
        if (!value.TryGetPropertyValue(property, out var node) || node is not JsonArray array)
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, errorPath);
        return array;
    }

    private static JsonArray CloneOrDefaultArray(JsonObject value, string property)
    {
        if (!value.TryGetPropertyValue(property, out var node) || node is null)
            return new JsonArray();
        return node is JsonArray array
            ? (JsonArray)array.DeepClone()
            : throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, property);
    }

    private static void MaterializeLegacyPolicyWildcards(
        JsonArray policies,
        params string[] selectorProperties)
    {
        foreach (var policy in policies.OfType<JsonObject>())
        {
            foreach (var property in selectorProperties)
            {
                if (!policy.TryGetPropertyValue(property, out var selector) ||
                    selector is null ||
                    selector is JsonValue scalar &&
                    scalar.TryGetValue<string>(out var text) &&
                    string.IsNullOrWhiteSpace(text))
                {
                    policy[property] = "*";
                }
            }
        }
    }

    private static JsonObject CloneOrDefaultObject(JsonObject value, string property)
    {
        if (!value.TryGetPropertyValue(property, out var node) || node is null)
            return new JsonObject();
        return node is JsonObject child
            ? (JsonObject)child.DeepClone()
            : throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, property);
    }

    private static string? ResolveStringAlias(JsonObject value, string path, bool required, params string[] aliases)
    {
        string? resolved = null;
        string? resolvedAlias = null;
        foreach (var alias in aliases)
        {
            var candidate = ReadOptionalString(value, alias);
            if (candidate is null)
                continue;
            if (resolved is not null && !string.Equals(resolved, candidate, StringComparison.Ordinal))
                throw Error(AppErrorCode.DYNAMIC_FLOW_LEGACY_ALIAS_AMBIGUOUS, JoinPath(path, alias), new { firstAlias = resolvedAlias });
            resolved = candidate;
            resolvedAlias = alias;
        }
        if (required && string.IsNullOrWhiteSpace(resolved))
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, JoinPath(path, aliases[0]));
        return resolved;
    }

    private static string ResolveNodeReference(
        JsonObject transition,
        IReadOnlyDictionary<string, string> nodeIdByCode,
        string path,
        string idAlias,
        string nodeAlias,
        string codeAlias)
    {
        var byId = ResolveStringAlias(transition, path, false, idAlias, nodeAlias);
        var code = ReadOptionalString(transition, codeAlias);
        if (code is null)
            return byId ?? throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, JoinPath(path, idAlias));
        if (!nodeIdByCode.TryGetValue(code, out var byCode))
            throw Error(AppErrorCode.DYNAMIC_FLOW_EDGE_NODE_UNKNOWN, JoinPath(path, codeAlias));
        if (byId is not null && !string.Equals(byId, byCode, StringComparison.Ordinal))
            throw Error(AppErrorCode.DYNAMIC_FLOW_LEGACY_ALIAS_AMBIGUOUS, JoinPath(path, codeAlias));
        return byId ?? byCode;
    }

    private static string InferSingleRootId(JsonArray nodes, JsonArray edges)
    {
        var ids = nodes
            .OfType<JsonObject>()
            .Select(node => ReadOptionalString(node, "nodeId"))
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray();
        var targets = edges
            .OfType<JsonObject>()
            .Select(edge => ReadOptionalString(edge, "toNodeId"))
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        var roots = ids.Where(id => !targets.Contains(id)).ToArray();
        return roots.Length == 1
            ? roots[0]
            : throw Error(AppErrorCode.DYNAMIC_FLOW_ENTRY_STEP_INVALID, "entryStepId");
    }

    private static string DeterministicTransitionId(string fromNodeId, string toNodeId, int index)
        => "tr-" + Sha256Utf8($"{fromNodeId}\0{toNodeId}\0{index}")[..16];

    private static void Copy(JsonObject source, JsonObject target, string property)
    {
        if (source.TryGetPropertyValue(property, out var value) && value is not null)
            target[property] = value.DeepClone();
    }

    private static void CopyAlias(
        JsonObject source,
        JsonObject target,
        string targetProperty,
        params string[] aliases)
    {
        var value = ResolveStringAlias(source, string.Empty, false, aliases);
        if (value is not null)
            target[targetProperty] = value;
    }

    private static string? ReadOptionalString(JsonObject value, string property)
    {
        if (!value.TryGetPropertyValue(property, out var node) || node is null)
            return null;
        if (node is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            throw Error(AppErrorCode.DYNAMIC_FLOW_PAYLOAD_INVALID, property);
        return text.Trim();
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string Normalize(string value) => value.Trim();
    private static string NormalizeUpper(string value) => value.Trim().ToUpperInvariant();
    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeUpperNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    private static string? NormalizeLowerNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string Sha256Utf8(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IReadOnlySet<string> Set(params string[] values)
        => new HashSet<string>(values, StringComparer.Ordinal);

    private static string JoinPath(string path, string property)
        => string.IsNullOrEmpty(path) ? property : $"{path}.{property}";

    private static string NormalizeJsonPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$")
            return "payload";
        return path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');
    }

    private static AppException Error(AppErrorCode code, string path, object? context = null)
        => AppExceptionFactory.Create(code, new
        {
            path,
            reason = code.ToString(),
            context
        });
}
