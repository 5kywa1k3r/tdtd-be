using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowDefinitionPayloadContractTests
{
    public static void Run()
    {
        StoredV12AndV13CatalogPinsRemainExactlyReadable();
        CanonicalHashRecursivelySortsObjectKeys();
        LegacyAdapterIsDeterministicAndVersioned();
        CanonicalUnknownFieldIsRejectedWithExactPath();
        TopologyOwnerArchetypeAndConditionFailuresUseStableCodes();
        TopologyBudgetsAcceptNAndRejectNPlusOne();
        PayloadByteBudgetAcceptsNAndRejectsNPlusOne();
        ConditionBudgetsAcceptNAndRejectNPlusOne();
        PolicyArraysCanonicalizeIndependentOfOrder();
        PolicyCoverageCountsExplicitDenyAndRejectsSpecificityConflict();
        PolicySelectorsRequireLiteralWildcard();
        MappingDefinitionValidatesReferencesAndEndpointCompatibility();
        CommandReceiptContainsHashesAndTokensButNoPayload();
        EveryP4ErrorCodeHasAStableCatalogDescriptor();
    }

    private static void StoredV12AndV13CatalogPinsRemainExactlyReadable()
    {
        AssertTrue(
            DynamicFlowStoredCatalogPinPolicy.HistoricalV12Version == "1.2" &&
            DynamicFlowStoredCatalogPinPolicy.HistoricalV12SemanticHash ==
                "b26549d5de7a3d93bd6fc9bab7bfdfbdaffb66a01347039b2c3629692b60068f" &&
            DynamicFlowStoredCatalogPinPolicy.HistoricalV13Version == "1.3" &&
            DynamicFlowStoredCatalogPinPolicy.HistoricalV13SemanticHash ==
                "55cfa0a4420e01db6707011ffc7a0271088c5b01b63edb8f2d21978edd3e2497",
            "historical stored catalog literals must remain independently pinned");
        AssertTrue(
            DynamicFlowStoredCatalogPinPolicy.IsHistoricalV12(
                DynamicFlowRuntimeCatalogCandidate.Version,
                DynamicFlowRuntimeCatalogCandidate.SemanticHash),
            "stored v1.2 pin must remain known after v1.3 promotion");
        AssertTrue(
            DynamicFlowStoredCatalogPinPolicy.IsHistoricalV13(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash),
            "stored v1.3 pin must remain known after rollback");
        AssertTrue(
            DynamicFlowStoredCatalogPinPolicy.IsKnownStored(
                DynamicFlowRuntimeCatalogCandidate.Version,
                DynamicFlowRuntimeCatalogCandidate.SemanticHash) &&
            DynamicFlowStoredCatalogPinPolicy.IsKnownStored(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash),
            "stored v1.2/v1.3 readability must not depend on the active CURRENT pointer");
        AssertTrue(
            !DynamicFlowStoredCatalogPinPolicy.IsKnownStored(
                DynamicFlowRuntimeCatalogCandidate.Version,
                new string('f', 64)) &&
            !DynamicFlowStoredCatalogPinPolicy.IsKnownStored(
                DynamicFlowP6CatalogCandidate.Version,
                new string('f', 64)),
            "stored catalog pins must fail closed on semantic SHA-256 drift");
    }

    private static void CanonicalHashRecursivelySortsObjectKeys()
    {
        const string first = """
        {
          "schemaVersion": 2,
          "archetypeId": "FLOW-T01",
          "entryStepId": "step-a",
          "formNodes": [{"formNodeId":"form-a","role":"ROOT","dynamicFormTemplateId":"form-version-a"}],
          "nodes": [{"nodeId":"step-a","nodeCode":"START","nodeKind":"FORM_STEP","formNodeId":"form-a","declaredRoles":["OWNER"]}],
          "edges": [],
          "rollbackPolicy": {"z":{"b":2,"a":1},"a":true}
        }
        """;
        const string second = """
        {
          "rollbackPolicy": {"a":true,"z":{"a":1,"b":2}},
          "edges": [],
          "nodes": [{"declaredRoles":["OWNER"],"formNodeId":"form-a","nodeKind":"FORM_STEP","nodeCode":"START","nodeId":"step-a"}],
          "formNodes": [{"dynamicFormTemplateId":"form-version-a","role":"ROOT","formNodeId":"form-a"}],
          "entryStepId": "step-a",
          "archetypeId": "FLOW-T01",
          "schemaVersion": 2
        }
        """;

        var a = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(first);
        var b = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(second);

        AssertEqual(a.CanonicalJson, b.CanonicalJson, "recursive canonical JSON");
        AssertEqual(a.PayloadHash, b.PayloadHash, "recursive canonical SHA-256");
        AssertEqual(64, a.PayloadHash.Length, "SHA-256 hex length");
    }

    private static void LegacyAdapterIsDeterministicAndVersioned()
    {
        const string legacy = """
        {
          "schemaVersion": 1,
          "formNodes": [
            {"formNodeId":"form-a","role":"OWNER","dynamicFormTemplateId":"form-version-a"},
            {"formNodeId":"form-b","role":"REVIEWER","dynamicFormTemplateId":"form-version-b"}
          ],
          "steps": [
            {"stepId":"step-a","stepCode":"A","stepName":"Alpha","formNodeId":"form-a"},
            {"stepId":"step-b","stepCode":"B","stepName":"Beta","formNodeId":"form-b"}
          ],
          "transitions": [{"fromStepCode":"A","toStepCode":"B"}]
        }
        """;

        var first = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(legacy);
        var second = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(legacy);

        AssertEqual(1, first.SourceSchemaVersion, "legacy source version");
        AssertEqual(2, first.Payload.SchemaVersion, "canonical schema version");
        AssertEqual("FLOW-T03", first.Payload.ArchetypeId, "deterministic inferred archetype");
        AssertEqual("step-a", first.Payload.EntryStepId, "deterministic inferred entry");
        AssertTrue(first.Payload.Edges[0].TransitionId.StartsWith("tr-", StringComparison.Ordinal), "transition id should be generated deterministically");
        AssertEqual(first.Payload.Edges[0].TransitionId, second.Payload.Edges[0].TransitionId, "generated transition id");
        AssertEqual(first.PayloadHash, second.PayloadHash, "legacy canonical hash");
    }

    private static void CanonicalUnknownFieldIsRejectedWithExactPath()
    {
        const string payload = """
        {
          "schemaVersion": 2,
          "archetypeId": "FLOW-T01",
          "entryStepId": "step-a",
          "formNodes": [{"formNodeId":"form-a","role":"ROOT","dynamicFormTemplateId":"form-version-a"}],
          "nodes": [{"nodeId":"step-a","nodeCode":"START","nodeKind":"FORM_STEP","formNodeId":"form-a","declaredRoles":["OWNER"],"script":"return true"}],
          "edges": []
        }
        """;

        var error = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_PAYLOAD_UNKNOWN_FIELD,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(payload));
        AssertContains(JsonSerializer.Serialize(error.Details), "nodes[0].script", "unknown field path");
    }

    private static void TopologyOwnerArchetypeAndConditionFailuresUseStableCodes()
    {
        var duplicate = SingleStepPayload();
        duplicate.Nodes.Add(new DynamicFlowTopologyNodeDto
        {
            NodeId = "step-a",
            NodeCode = "OTHER",
            NodeKind = DynamicFlowNodeKinds.FormStep,
            FormNodeId = "form-a",
            DeclaredRoles = new List<string> { "OWNER" }
        });
        AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_NODE_ID_DUPLICATE,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(duplicate, null));

        var invalidOwner = SingleStepPayload();
        invalidOwner.ResultOwnerStepId = "step-a";
        AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_OWNER_INVALID,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(invalidOwner, null));

        var unknownArchetype = SingleStepPayload();
        unknownArchetype.ArchetypeId = "FLOW-UNKNOWN";
        AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_ARCHETYPE_UNKNOWN,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(unknownArchetype, null));

        const string scriptedCondition = """
        {
          "schemaVersion":2,
          "archetypeId":"FLOW-T07",
          "entryStepId":"step-a",
          "formNodes":[
            {"formNodeId":"form-a","role":"ROOT","dynamicFormTemplateId":"form-version-a"},
            {"formNodeId":"form-b","role":"CHILD","dynamicFormTemplateId":"form-version-b"}
          ],
          "nodes":[
            {"nodeId":"step-a","nodeCode":"A","nodeKind":"FORM_STEP","formNodeId":"form-a","declaredRoles":["OWNER"]},
            {"nodeId":"gateway-a","nodeCode":"G","nodeKind":"GATEWAY","declaredRoles":[],"gateway":{"kind":"CONDITION"}},
            {"nodeId":"step-b","nodeCode":"B","nodeKind":"FORM_STEP","formNodeId":"form-b","declaredRoles":["OWNER"]}
          ],
          "edges":[
            {"transitionId":"t1","fromNodeId":"step-a","toNodeId":"gateway-a"},
            {"transitionId":"t2","fromNodeId":"gateway-a","toNodeId":"step-b","condition":{"operator":"TRUE","script":"return true"}}
          ]
        }
        """;
        var conditionError = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_CONDITION_INVALID,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(scriptedCondition));
        AssertContains(JsonSerializer.Serialize(conditionError.Details), "edges[1].condition.script", "script condition path");
    }

    private static void TopologyBudgetsAcceptNAndRejectNPlusOne()
    {
        var exact = ExactTopologyBudgetPayload();
        var exactResult = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(exact, null);
        AssertEqual(DynamicFlowDefinitionPayloadContract.MaxTopologyNodes, exactResult.Payload.Nodes.Count, "exact node budget");
        AssertEqual(DynamicFlowDefinitionPayloadContract.MaxTopologyEdges, exactResult.Payload.Edges.Count, "exact edge budget");

        var tooManyNodes = new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId = "FLOW-T01",
            EntryStepId = "n0",
            Nodes = Enumerable.Range(0, DynamicFlowDefinitionPayloadContract.MaxTopologyNodes + 1)
                .Select(index => new DynamicFlowTopologyNodeDto { NodeId = $"n{index}" })
                .ToList()
        };
        AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_NODE_BUDGET_EXCEEDED,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(tooManyNodes, null));

        var tooManyEdges = new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId = "FLOW-T01",
            EntryStepId = "n0",
            Edges = Enumerable.Range(0, DynamicFlowDefinitionPayloadContract.MaxTopologyEdges + 1)
                .Select(index => new DynamicFlowTopologyEdgeDto { TransitionId = $"t{index}" })
                .ToList()
        };
        AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_EDGE_BUDGET_EXCEEDED,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(tooManyEdges, null));
    }

    private static void PayloadByteBudgetAcceptsNAndRejectsNPlusOne()
    {
        var payload = SingleStepPayload();
        payload.Nodes[0].Name = string.Empty;
        var baseline = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(payload, null);
        payload.Nodes[0].Name = new string(
            'x',
            DynamicFlowDefinitionPayloadContract.MaxCanonicalPayloadUtf8Bytes - baseline.Utf8ByteCount);
        var exact = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(payload, null);
        AssertEqual(
            DynamicFlowDefinitionPayloadContract.MaxCanonicalPayloadUtf8Bytes,
            exact.Utf8ByteCount,
            "exact canonical payload byte budget");

        payload.Nodes[0].Name += "x";
        AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_PAYLOAD_BUDGET_EXCEEDED,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(payload, null));
    }

    private static void ConditionBudgetsAcceptNAndRejectNPlusOne()
    {
        var accepted = ConditionalPayload(ConditionChain(depth: 12));
        DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(accepted, null);

        var rejected = ConditionalPayload(ConditionChain(depth: 13));
        var error = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_CONDITION_BUDGET_EXCEEDED,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(rejected, null));
        AssertContains(JsonSerializer.Serialize(error.Details), "edges[1].condition", "condition budget path");

        var exactNodes = ConditionalPayload(new DynamicFlowConditionDto
        {
            Operator = "OR",
            Children = Enumerable.Range(0, DynamicFlowDefinitionPayloadContract.MaxConditionNodes - 1)
                .Select(_ => new DynamicFlowConditionDto { Operator = "TRUE" })
                .ToList()
        });
        DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(exactNodes, null);

        var tooManyConditionNodes = ConditionalPayload(new DynamicFlowConditionDto
        {
            Operator = "OR",
            Children = Enumerable.Range(0, DynamicFlowDefinitionPayloadContract.MaxConditionNodes)
                .Select(_ => new DynamicFlowConditionDto { Operator = "TRUE" })
                .ToList()
        });
        AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_CONDITION_BUDGET_EXCEEDED,
            () => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(tooManyConditionNodes, null));
    }

    private static void PolicyCoverageCountsExplicitDenyAndRejectsSpecificityConflict()
    {
        var payload = SingleStepPayload();
        payload.ActorPolicies.Add(new DynamicFlowActorPolicyDto
        {
            PolicyId = "actor-deny",
            StepId = "step-a",
            StepCode = "*",
            ActorRole = "OWNER",
            AllowForward = false
        });
        payload.FieldPolicies.Add(new DynamicFlowFieldPolicyDto
        {
            PolicyId = "field-deny",
            DynamicFormTemplateId = "*",
            StepId = "step-a",
            StepCode = "*",
            ActorRole = "OWNER",
            FieldId = "field-a",
            FieldKey = "*",
            Read = false,
            Write = false
        });
        var catalogs = new Dictionary<string, DynamicFlowFormEndpointCatalog>(StringComparer.Ordinal)
        {
            ["form-a"] = new(
                "form-a",
                new[] { new DynamicFlowScalarEndpoint("field-a") },
                Array.Empty<DynamicFlowTableColumnEndpoint>())
        };

        var summary = DynamicFlowDefinitionPayloadContract.ValidatePolicyCoverage(payload, catalogs);
        AssertEqual(1, summary.ActorRoleEndpoints, "actor coverage count");
        AssertEqual(1, summary.ScalarFieldEndpoints, "explicit deny scalar coverage count");

        payload.TableColumnPolicies.Add(new DynamicFlowTableColumnPolicyDto
        {
            PolicyId = "summary-write",
            DynamicFormTemplateId = "*",
            StepId = "step-a",
            StepCode = "*",
            ActorRole = "OWNER",
            BlockId = "summary-a",
            ColumnKey = "metric-a",
            Read = true,
            Write = true
        });
        catalogs["form-a"] = new(
            "form-a",
            new[] { new DynamicFlowScalarEndpoint("field-a") },
            new[] { new DynamicFlowTableColumnEndpoint("summary-a", "metric-a", "SUMMARY_TEMPLATE") });
        var summaryError = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_TABLE_POLICY_ENDPOINT_INCOMPATIBLE,
            () => DynamicFlowDefinitionPayloadContract.ValidatePolicyCoverage(payload, catalogs));
        AssertContains(JsonSerializer.Serialize(summaryError.Details), "tableColumnPolicies[0].write", "summary write path");
        payload.TableColumnPolicies.Clear();
        catalogs["form-a"] = new(
            "form-a",
            new[] { new DynamicFlowScalarEndpoint("field-a") },
            Array.Empty<DynamicFlowTableColumnEndpoint>());

        payload.FieldPolicies.Add(new DynamicFlowFieldPolicyDto
        {
            PolicyId = "field-allow-same-specificity",
            DynamicFormTemplateId = "*",
            StepId = "step-a",
            StepCode = "*",
            ActorRole = "OWNER",
            FieldId = "field-a",
            FieldKey = "*",
            Read = true,
            Write = false
        });
        AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_POLICY_SPECIFICITY_CONFLICT,
            () => DynamicFlowDefinitionPayloadContract.ValidatePolicyCoverage(payload, catalogs));
    }

    private static void PolicyArraysCanonicalizeIndependentOfOrder()
    {
        var payload = SingleStepPayload();
        payload.ActorPolicies.AddRange(new[]
        {
            new DynamicFlowActorPolicyDto
            {
                PolicyId = "actor-specific",
                StepId = "step-a",
                StepCode = "*",
                ActorRole = "owner",
                AllowForward = true
            },
            new DynamicFlowActorPolicyDto
            {
                PolicyId = "actor-broad",
                StepId = "*",
                StepCode = "*",
                ActorRole = "*",
                AllowForward = false
            }
        });
        payload.FieldPolicies.AddRange(new[]
        {
            new DynamicFlowFieldPolicyDto
            {
                PolicyId = "field-specific",
                DynamicFormTemplateId = "*",
                StepId = "step-a",
                StepCode = "*",
                ActorRole = "owner",
                FieldId = "field-a",
                FieldKey = "*",
                Read = true,
                Write = true
            },
            new DynamicFlowFieldPolicyDto
            {
                PolicyId = "field-broad",
                DynamicFormTemplateId = "*",
                StepId = "*",
                StepCode = "*",
                ActorRole = "*",
                FieldId = "field-a",
                FieldKey = "*",
                Read = true,
                Write = false
            }
        });
        payload.TableColumnPolicies.AddRange(new[]
        {
            new DynamicFlowTableColumnPolicyDto
            {
                PolicyId = "table-specific",
                DynamicFormTemplateId = "*",
                StepId = "step-a",
                StepCode = "*",
                ActorRole = "owner",
                BlockId = "block-a",
                ColumnKey = "amount",
                Read = true,
                Write = true
            },
            new DynamicFlowTableColumnPolicyDto
            {
                PolicyId = "table-broad",
                DynamicFormTemplateId = "*",
                StepId = "*",
                StepCode = "*",
                ActorRole = "*",
                BlockId = "block-a",
                ColumnKey = "amount",
                Read = true,
                Write = false
            }
        });

        var first = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(payload, null);
        payload.ActorPolicies.Reverse();
        payload.FieldPolicies.Reverse();
        payload.TableColumnPolicies.Reverse();
        var reordered = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(payload, null);

        AssertEqual(first.CanonicalJson, reordered.CanonicalJson, "policy array canonical order");
        AssertEqual(first.PayloadHash, reordered.PayloadHash, "policy array canonical hash");
        AssertEqual(
            "OWNER",
            reordered.Payload.FieldPolicies.Single(policy => policy.PolicyId == "field-specific").ActorRole,
            "policy role normalization");
    }

    private static void PolicySelectorsRequireLiteralWildcard()
    {
        var payload = SingleStepPayload();
        payload.ActorPolicies.Add(new DynamicFlowActorPolicyDto
        {
            PolicyId = "actor-explicit",
            StepId = "step-a",
            StepCode = " ",
            ActorRole = "OWNER",
            AllowForward = true
        });
        var catalogs = new Dictionary<string, DynamicFlowFormEndpointCatalog>(StringComparer.Ordinal)
        {
            ["form-a"] = new(
                "form-a",
                new[] { new DynamicFlowScalarEndpoint("field-a", "amount") },
                Array.Empty<DynamicFlowTableColumnEndpoint>())
        };

        var missingStepSelector = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN,
            () => DynamicFlowDefinitionPayloadContract.ValidatePolicyCoverage(payload, catalogs));
        AssertContains(
            JsonSerializer.Serialize(missingStepSelector.Details),
            "actorPolicies[0].actorRole",
            "blank selector must not become an actor wildcard");

        payload.ActorPolicies[0].StepCode = "*";
        payload.FieldPolicies.Add(new DynamicFlowFieldPolicyDto
        {
            PolicyId = "field-explicit",
            DynamicFormTemplateId = null,
            StepId = "step-a",
            StepCode = "*",
            ActorRole = "OWNER",
            FieldId = "field-a",
            FieldKey = "*",
            Read = true,
            Write = true
        });
        var missingFormSelector = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_POLICY_FIELD_UNKNOWN,
            () => DynamicFlowDefinitionPayloadContract.ValidatePolicyCoverage(payload, catalogs));
        AssertContains(
            JsonSerializer.Serialize(missingFormSelector.Details),
            "fieldPolicies[0].fieldId",
            "null selector must not become a field wildcard");

        payload.FieldPolicies[0].DynamicFormTemplateId = "*";
        var covered = DynamicFlowDefinitionPayloadContract.ValidatePolicyCoverage(payload, catalogs);
        AssertEqual(1, covered.ActorRoleEndpoints, "literal actor wildcard coverage");
        AssertEqual(1, covered.ScalarFieldEndpoints, "literal field wildcard coverage");
    }

    private static void MappingDefinitionValidatesReferencesAndEndpointCompatibility()
    {
        var payload = SingleStepPayload();
        payload.MappingRules.AddRange(new[]
        {
            new DynamicFlowMappingRuleDto
            {
                MappingId = "field-copy",
                MappingVersion = 1,
                MappingKind = "FIELD",
                SourceDynamicFormTemplateId = "form-version-a",
                SourceStepId = "step-a",
                SourceFieldId = "field-a",
                SourceFieldKey = "amount",
                TargetDynamicFormTemplateId = "form-version-a",
                TargetStepCode = "START",
                TargetFieldKey = "total"
            },
            new DynamicFlowMappingRuleDto
            {
                MappingId = "table-copy",
                MappingVersion = 1,
                MappingKind = "TABLE_COLUMN",
                SourceStepId = "step-a",
                SourceBlockId = "block-a",
                SourceColumnKey = "amount",
                TargetStepId = "step-a",
                TargetBlockId = "block-a",
                TargetColumnKey = "total"
            }
        });
        var catalogs = MappingCatalog("APPEND_ROWS");

        var summary = DynamicFlowDefinitionPayloadContract.ValidateMappingDefinition(payload, catalogs);
        AssertEqual(2, summary.MappingRules, "mapping rule count");
        AssertEqual(2, summary.SourceEndpoints, "mapping source endpoint count");
        AssertEqual(2, summary.TargetEndpoints, "mapping target endpoint count");

        payload.MappingRules[0].SourceFieldKey = "total";
        var fieldMismatch = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_ENDPOINT_INCOMPATIBLE,
            () => DynamicFlowDefinitionPayloadContract.ValidateMappingDefinition(payload, catalogs));
        AssertContains(JsonSerializer.Serialize(fieldMismatch.Details), "mappingRules[0].sourceFieldId", "mapping field id/key mismatch path");
        payload.MappingRules[0].SourceFieldKey = "amount";

        payload.MappingRules[0].SourceStepId = "missing-step";
        var stepUnknown = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_DEFINITION_INVALID,
            () => DynamicFlowDefinitionPayloadContract.ValidateMappingDefinition(payload, catalogs));
        AssertContains(JsonSerializer.Serialize(stepUnknown.Details), "mappingRules[0].sourceStepId", "mapping step reference path");
        payload.MappingRules[0].SourceStepId = "step-a";

        payload.MappingRules[1].MappingId = "field-copy";
        var duplicateId = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_DEFINITION_INVALID,
            () => DynamicFlowDefinitionPayloadContract.ValidateMappingDefinition(payload, catalogs));
        AssertContains(JsonSerializer.Serialize(duplicateId.Details), "mappingRules[1].mappingId", "mapping duplicate id path");
        payload.MappingRules[1].MappingId = "table-copy";

        var appendColumns = MappingCatalog("APPEND_COLUMNS");
        var incompatibleTarget = AssertThrows(
            AppErrorCode.DYNAMIC_FLOW_MAPPING_ENDPOINT_INCOMPATIBLE,
            () => DynamicFlowDefinitionPayloadContract.ValidateMappingDefinition(payload, appendColumns));
        AssertContains(JsonSerializer.Serialize(incompatibleTarget.Details), "mappingRules[1].targetBlockId", "append-columns target path");
        AssertContains(JsonSerializer.Serialize(incompatibleTarget.Details), "APPEND_COLUMNS", "append-columns target mode");
    }

    private static IReadOnlyDictionary<string, DynamicFlowFormEndpointCatalog> MappingCatalog(string tableMode)
        => new Dictionary<string, DynamicFlowFormEndpointCatalog>(StringComparer.Ordinal)
        {
            ["form-a"] = new(
                "form-a",
                new[]
                {
                    new DynamicFlowScalarEndpoint("field-a", "amount"),
                    new DynamicFlowScalarEndpoint("field-b", "total")
                },
                new[]
                {
                    new DynamicFlowTableColumnEndpoint("block-a", "amount", tableMode),
                    new DynamicFlowTableColumnEndpoint("block-a", "total", tableMode)
                })
        };

    private static void CommandReceiptContainsHashesAndTokensButNoPayload()
    {
        var receipt = new DynamicFlowDefinitionCommandReceipt
        {
            Id = "100000000000000000000001",
            ActorUserId = "200000000000000000000001",
            CommandKind = "SAVE_DRAFT",
            CommandId = "command-1",
            RequestHash = new string('a', 64),
            FamilyId = "300000000000000000000001",
            VersionId = "400000000000000000000001",
            ResultFamilyRevision = 2,
            ResultDraftRevision = 3,
            ResultPayloadHash = new string('b', 64),
            CorrelationId = "correlation-1",
            Outcome = DynamicFlowDefinitionCommandOutcomes.Succeeded
        };

        var document = receipt.ToBsonDocument();
        AssertEqual("command-1", document["commandId"].AsString, "receipt command id");
        AssertEqual(2, document["resultFamilyRevision"].AsInt32, "receipt family revision");
        AssertFalse(document.Contains("payload"), "receipt must not contain payload");
        AssertFalse(document.Contains("payloadJson"), "receipt must not contain payloadJson");
    }

    private static void EveryP4ErrorCodeHasAStableCatalogDescriptor()
    {
        foreach (var code in Enum.GetValues<AppErrorCode>()
                     .Where(code => code.ToString().StartsWith("DYNAMIC_FLOW_", StringComparison.Ordinal)))
        {
            var descriptor = AppErrorCatalog.Get(code);
            AssertEqual(code, descriptor.Code, $"error catalog descriptor for {code}");
            AssertEqual("DYNAMIC_FLOW", descriptor.Service, $"error catalog service for {code}");
        }
    }

    private static DynamicFlowTemplatePayloadDto SingleStepPayload()
        => new()
        {
            ArchetypeId = "FLOW-T01",
            EntryStepId = "step-a",
            FormNodes = new List<DynamicFlowFormNodeDto>
            {
                new() { FormNodeId = "form-a", Role = "ROOT", DynamicFormTemplateId = "form-version-a" }
            },
            Nodes = new List<DynamicFlowTopologyNodeDto>
            {
                new()
                {
                    NodeId = "step-a",
                    NodeCode = "START",
                    NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "form-a",
                    DeclaredRoles = new List<string> { "OWNER" }
                }
            }
        };

    private static DynamicFlowTemplatePayloadDto ExactTopologyBudgetPayload()
    {
        var payload = new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId = "FLOW-T03",
            EntryStepId = "n0"
        };
        for (var index = 0; index < DynamicFlowDefinitionPayloadContract.MaxTopologyNodes; index++)
        {
            payload.FormNodes.Add(new DynamicFlowFormNodeDto
            {
                FormNodeId = $"f{index}",
                Role = "STEP",
                DynamicFormTemplateId = $"form-version-{index}"
            });
            payload.Nodes.Add(new DynamicFlowTopologyNodeDto
            {
                NodeId = $"n{index}",
                NodeCode = $"N{index}",
                NodeKind = DynamicFlowNodeKinds.FormStep,
                FormNodeId = $"f{index}",
                DeclaredRoles = new List<string> { "OWNER" }
            });
            if (index > 0)
            {
                payload.Edges.Add(new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = $"chain-{index - 1}-{index}",
                    FromNodeId = $"n{index - 1}",
                    ToNodeId = $"n{index}"
                });
            }
        }

        for (var from = 0; from < payload.Nodes.Count && payload.Edges.Count < DynamicFlowDefinitionPayloadContract.MaxTopologyEdges; from++)
        {
            for (var to = from + 2; to < payload.Nodes.Count && payload.Edges.Count < DynamicFlowDefinitionPayloadContract.MaxTopologyEdges; to++)
            {
                payload.Edges.Add(new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = $"extra-{from}-{to}",
                    FromNodeId = $"n{from}",
                    ToNodeId = $"n{to}"
                });
            }
        }
        return payload;
    }

    private static DynamicFlowTemplatePayloadDto ConditionalPayload(DynamicFlowConditionDto condition)
        => new()
        {
            ArchetypeId = "FLOW-T07",
            EntryStepId = "step-a",
            FormNodes = new List<DynamicFlowFormNodeDto>
            {
                new() { FormNodeId = "form-a", Role = "ROOT", DynamicFormTemplateId = "form-version-a" },
                new() { FormNodeId = "form-b", Role = "CHILD", DynamicFormTemplateId = "form-version-b" }
            },
            Nodes = new List<DynamicFlowTopologyNodeDto>
            {
                new()
                {
                    NodeId = "step-a", NodeCode = "A", NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "form-a", DeclaredRoles = new List<string> { "OWNER" }
                },
                new()
                {
                    NodeId = "gateway-a", NodeCode = "G", NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto { Kind = DynamicFlowGatewayKinds.Condition }
                },
                new()
                {
                    NodeId = "step-b", NodeCode = "B", NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "form-b", DeclaredRoles = new List<string> { "OWNER" }
                }
            },
            Edges = new List<DynamicFlowTopologyEdgeDto>
            {
                new() { TransitionId = "t1", FromNodeId = "step-a", ToNodeId = "gateway-a" },
                new() { TransitionId = "t2", FromNodeId = "gateway-a", ToNodeId = "step-b", Condition = condition }
            }
        };

    private static DynamicFlowConditionDto ConditionChain(int depth)
    {
        var current = new DynamicFlowConditionDto { Operator = "TRUE" };
        for (var level = 1; level < depth; level++)
            current = new DynamicFlowConditionDto { Operator = "NOT", Children = new List<DynamicFlowConditionDto> { current } };
        return current;
    }

    private static AppException AssertThrows(AppErrorCode expected, Action action)
    {
        try
        {
            action();
        }
        catch (AppException error) when (error.Code == expected)
        {
            return error;
        }
        catch (AppException error)
        {
            throw new InvalidOperationException($"Expected {expected}, got {error.Code}.", error);
        }
        throw new InvalidOperationException($"Expected {expected}, but no exception was thrown.");
    }

    private static void AssertContains(string value, string expected, string context)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{expected}' in '{value}'.");
    }

    private static void AssertTrue(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool value, string message)
    {
        if (value)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }
}
