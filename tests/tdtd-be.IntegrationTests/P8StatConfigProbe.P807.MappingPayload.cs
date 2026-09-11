using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private JsonObject BuildP807MappedFlowPayload()
        => new()
        {
            ["schemaVersion"] = 2,
            ["archetypeId"] = "FLOW-T03",
            ["entryStepId"] = "step_a",
            ["rootDynamicFormTemplateId"] = _flowRootFormId,
            ["resultOwnerStepId"] = "step_b",
            ["resultOwnerFormNodeId"] = "child_form",
            ["statisticsOwnerStepId"] = "step_b",
            ["statisticsOwnerFormNodeId"] = "child_form",
            ["formNodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["formNodeId"] = "root_form",
                    ["role"] = "ROOT",
                    ["dynamicFormTemplateId"] = _flowRootFormId
                },
                new JsonObject
                {
                    ["formNodeId"] = "child_form",
                    ["role"] = "CHILD",
                    ["dynamicFormTemplateId"] = _flowChildFormId
                }
            },
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["nodeId"] = "step_a",
                    ["nodeCode"] = "A",
                    ["nodeKind"] = "FORM_STEP",
                    ["formNodeId"] = "root_form",
                    ["declaredRoles"] = new JsonArray("ASSIGNEE")
                },
                new JsonObject
                {
                    ["nodeId"] = "step_b",
                    ["nodeCode"] = "B",
                    ["nodeKind"] = "FORM_STEP",
                    ["formNodeId"] = "child_form",
                    ["declaredRoles"] = new JsonArray("ASSIGNEE")
                }
            },
            ["edges"] = new JsonArray
            {
                new JsonObject
                {
                    ["transitionId"] = "edge_a_b",
                    ["fromNodeId"] = "step_a",
                    ["toNodeId"] = "step_b"
                }
            },
            ["actorPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "p7-actor-a",
                    ["stepId"] = "step_a",
                    ["stepCode"] = "A",
                    ["actorRole"] = "ASSIGNEE",
                    ["allowForward"] = true
                },
                new JsonObject
                {
                    ["policyId"] = "p7-actor-b",
                    ["stepId"] = "step_b",
                    ["stepCode"] = "B",
                    ["actorRole"] = "ASSIGNEE",
                    ["canFinalize"] = true
                }
            },
            ["fieldPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "p7-source-note-wildcard-deny",
                    ["dynamicFormTemplateId"] = _flowRootFormId,
                    ["stepId"] = "step_a",
                    ["stepCode"] = "A",
                    ["actorRole"] = "*",
                    ["fieldId"] = "field_note",
                    ["fieldKey"] = "note",
                    ["read"] = false,
                    ["write"] = false
                },
                new JsonObject
                {
                    ["policyId"] = "p7-source-note-assignee-allow",
                    ["dynamicFormTemplateId"] = _flowRootFormId,
                    ["stepId"] = "step_a",
                    ["stepCode"] = "A",
                    ["actorRole"] = "ASSIGNEE",
                    ["fieldId"] = "field_note",
                    ["fieldKey"] = "note",
                    ["read"] = true,
                    ["write"] = true
                },
                new JsonObject
                {
                    ["policyId"] = "p7-target-child-wildcard-deny",
                    ["dynamicFormTemplateId"] = _flowChildFormId,
                    ["stepId"] = "step_b",
                    ["stepCode"] = "B",
                    ["actorRole"] = "*",
                    ["fieldId"] = "field_child_value",
                    ["fieldKey"] = "child_value",
                    ["read"] = false,
                    ["write"] = false
                },
                new JsonObject
                {
                    ["policyId"] = "p7-target-child-assignee-allow",
                    ["dynamicFormTemplateId"] = _flowChildFormId,
                    ["stepId"] = "step_b",
                    ["stepCode"] = "B",
                    ["actorRole"] = "ASSIGNEE",
                    ["fieldId"] = "field_child_value",
                    ["fieldKey"] = "child_value",
                    ["read"] = true,
                    ["write"] = true
                }
            },
            ["tableColumnPolicies"] = new JsonArray(),
            ["mappingRules"] = new JsonArray
            {
                new JsonObject
                {
                    ["mappingId"] = "p7-note-to-child",
                    ["mappingVersion"] = 1,
                    ["mappingKind"] = "FIELD",
                    ["dataType"] = "TEXT",
                    ["conflictPolicy"] = "OVERWRITE",
                    ["contributionPolicy"] = "INCLUDE",
                    ["evaluationGrain"] = "FLOW_INSTANCE",
                    ["errorPolicy"] = "BLOCK_APPLY",
                    ["inputs"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["inputKey"] = "source_note",
                            ["dataType"] = "TEXT",
                            ["cardinality"] = "ONE",
                            ["nullPolicy"] = "ERROR",
                            ["source"] = new JsonObject
                            {
                                ["kind"] = "FIELD",
                                ["dynamicFormTemplateId"] =
                                    _flowRootFormId,
                                ["stepId"] = "step_a",
                                ["stepCode"] = "A",
                                ["fieldId"] = "field_note",
                                ["fieldKey"] = "note",
                                ["dataType"] = "TEXT"
                            }
                        }
                    },
                    ["target"] = new JsonObject
                    {
                        ["kind"] = "FIELD",
                        ["dynamicFormTemplateId"] = _flowChildFormId,
                        ["stepId"] = "step_b",
                        ["stepCode"] = "B",
                        ["fieldId"] = "field_child_value",
                        ["fieldKey"] = "child_value",
                        ["dataType"] = "TEXT"
                    },
                    ["calculation"] = new JsonObject
                    {
                        ["kind"] = "EXPRESSION",
                        ["operation"] = "copy",
                        ["resultDataType"] = "TEXT",
                        ["expression"] = new JsonObject
                        {
                            ["op"] = "copy",
                            ["args"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["input"] = "source_note"
                                }
                            }
                        }
                    }
                }
            },
            ["rollbackPolicy"] = new JsonObject(),
            ["finalResultPolicy"] = new JsonObject(),
            ["statisticProfile"] = new JsonObject()
        };
}
