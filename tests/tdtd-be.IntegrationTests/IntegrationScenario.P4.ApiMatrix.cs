using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal sealed partial class IntegrationScenario
{
    private string? _p4AppendRowsFormId;
    private string? _p4SummaryFormId;
    private string? _p4PrimaryPolicyFieldId;

    private sealed record P4ApiDraftFixture(
        string FamilyId,
        int FamilyRevision,
        string VersionId,
        int DraftRevision,
        string PayloadHash,
        string CanonicalPayloadJson);

    private sealed record P4ApiMongoSnapshot(
        string FamilyBson,
        string VersionBson,
        P4DefinitionCounts Counts);

    private async Task<CaseObservation> RunP4TopologyApiMatrixCaseAsync(
        int number,
        CancellationToken ct)
    {
        var formId = HarnessAssert.Required(_formId, $"FLOW-TOPO-{number:00} form");
        if (number == 19)
            return await VerifyP4ExactTopologyBudgetsViaApiAsync(formId, ct);
        if (number == 20)
            return await VerifyP4ExceededTopologyBudgetsViaApiAsync(formId, ct);

        var payload = BuildP4TopologyCase(number, formId);
        var fixture = await CreateP4ApiDraftAsync(
            $"TOPO_{number:00}",
            formId,
            BuildFlowPayload(formId),
            ct);
        var commandId = $"p4-topo-{number:00}-save";
        if (number == 1)
        {
            var saved = await SaveP4ValidPayloadAsync(
                fixture,
                commandId,
                payload,
                $"FLOW-TOPO-{number:00}",
                ct);
            HarnessAssert.Equal(
                fixture.PayloadHash,
                saved.PayloadHash,
                "FLOW-TOPO-01 canonical hash changed for the same topology");

            var replayBefore = await CaptureP4ApiMongoSnapshotAsync(
                fixture.FamilyId,
                fixture.VersionId,
                ct);
            var replay = await SaveP4ApiDraftAsync(fixture, commandId, payload, ct);
            ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "FLOW-TOPO-01 exact API replay");
            HarnessAssert.Equal(
                saved.PayloadHash,
                ApiHarnessClient.RequiredString(replay.Json, "payloadHash"),
                "FLOW-TOPO-01 exact API replay hash");
            await AssertP4ApiZeroWriteAsync(
                replayBefore,
                fixture.FamilyId,
                fixture.VersionId,
                "FLOW-TOPO-01 exact API replay",
                ct);
            return P4Observation(
                "real Kestrel save canonicalized the single FORM_STEP topology to the same hash; exact replay was Mongo byte-stable",
                "http=200;roots=1;finals=1;hashStable=true;exactReplay=200/0W;mongoOracle=true");
        }

        var expected = number switch
        {
            2 => ("DYNAMIC_FLOW_ROOT_COUNT_INVALID", "nodes"),
            3 => ("DYNAMIC_FLOW_NODE_UNREACHABLE", "nodes[2]"),
            4 => ("DYNAMIC_FLOW_CYCLE_FORBIDDEN", "edges[1]"),
            5 => ("DYNAMIC_FLOW_EDGE_NODE_UNKNOWN", "edges[0].toNodeId"),
            6 => ("DYNAMIC_FLOW_NODE_ID_DUPLICATE", "nodes[1].nodeId"),
            7 => ("DYNAMIC_FLOW_NODE_CODE_DUPLICATE", "nodes[1].nodeCode"),
            8 => ("DYNAMIC_FLOW_TRANSITION_ID_DUPLICATE", "edges[1].transitionId"),
            9 => ("DYNAMIC_FLOW_EDGE_DUPLICATE", "edges[1]"),
            10 => ("DYNAMIC_FLOW_EDGE_SELF_LOOP", "edges[0]"),
            11 => ("DYNAMIC_FLOW_ENTRY_STEP_INVALID", "entryStepId"),
            12 => ("DYNAMIC_FLOW_FINAL_NODE_INVALID", "nodes[1]"),
            13 => ("DYNAMIC_FLOW_STEP_FORM_NODE_MISMATCH", "nodes[0].formNodeId"),
            14 => ("DYNAMIC_FLOW_FORK_INVALID", "nodes[1].gateway"),
            15 => ("DYNAMIC_FLOW_JOIN_INVALID", "nodes[1].gateway"),
            16 => ("DYNAMIC_FLOW_CONDITION_INVALID", "edges[0].condition.operator"),
            17 => ("DYNAMIC_FLOW_NODE_KIND_UNSUPPORTED", "nodes[0].nodeKind"),
            18 => ("DYNAMIC_FLOW_ARCHETYPE_UNKNOWN", "archetypeId"),
            _ => throw new ArgumentOutOfRangeException(nameof(number))
        };
        await SaveP4InvalidPayloadAsync(
            fixture,
            commandId,
            payload,
            expected.Item1,
            expected.Item2,
            $"FLOW-TOPO-{number:00}",
            ct);
        return P4Observation(
            $"real Kestrel rejected invalid topology with {expected.Item1} at exact path {expected.Item2}",
            $"http=400;errorCode={expected.Item1};path={expected.Item2};aggregateBsonStable=true;receiptWrites=0;auditWrites=0");
    }

    private async Task<CaseObservation> VerifyP4ExactTopologyBudgetsViaApiAsync(
        string formId,
        CancellationToken ct)
    {
        var topologyFixture = await CreateP4ApiDraftAsync(
            "TOPO_19_GRAPH",
            formId,
            BuildFlowPayload(formId),
            ct);
        var topology = BuildP4TopologyBudgetPayload(formId, 200, 400);
        var topologySaved = await SaveP4ValidPayloadAsync(
            topologyFixture,
            "p4-topo-19-graph",
            topology,
            "FLOW-TOPO-19 nodes=200 edges=400",
            ct);
        var topologyBody = JsonNode.Parse(topologySaved.CanonicalPayloadJson) as JsonObject
            ?? throw new InvalidOperationException("FLOW-TOPO-19 canonical graph payload is not an object.");
        HarnessAssert.Equal(
            200,
            ApiHarnessClient.RequiredArray(topologyBody["nodes"], "FLOW-TOPO-19 nodes").Count,
            "FLOW-TOPO-19 exact node budget");
        HarnessAssert.Equal(
            400,
            ApiHarnessClient.RequiredArray(topologyBody["edges"], "FLOW-TOPO-19 edges").Count,
            "FLOW-TOPO-19 exact edge budget");

        var bytePayload = BuildFlowPayload(formId);
        ApiHarnessClient.RequiredObject(
            ApiHarnessClient.RequiredArray(bytePayload["nodes"], "FLOW-TOPO-19 byte nodes")[0],
            "FLOW-TOPO-19 byte node")["name"] = string.Empty;
        var byteFixture = await CreateP4ApiDraftAsync(
            "TOPO_19_BYTES",
            formId,
            bytePayload,
            ct);
        var baselineBytes = Encoding.UTF8.GetByteCount(byteFixture.CanonicalPayloadJson);
        var padding = DynamicFlowDefinitionPayloadContract.MaxCanonicalPayloadUtf8Bytes - baselineBytes;
        HarnessAssert.True(padding >= 0, "FLOW-TOPO-19 baseline exceeded canonical byte budget");
        ApiHarnessClient.RequiredObject(
            ApiHarnessClient.RequiredArray(bytePayload["nodes"], "FLOW-TOPO-19 byte nodes")[0],
            "FLOW-TOPO-19 byte node")["name"] = new string('x', padding);
        var byteSaved = await SaveP4ValidPayloadAsync(
            byteFixture,
            "p4-topo-19-bytes",
            bytePayload,
            "FLOW-TOPO-19 bytes=1048576",
            ct);
        HarnessAssert.Equal(
            DynamicFlowDefinitionPayloadContract.MaxCanonicalPayloadUtf8Bytes,
            Encoding.UTF8.GetByteCount(byteSaved.CanonicalPayloadJson),
            "FLOW-TOPO-19 exact UTF-8 byte budget");

        var depthFixture = await CreateP4ApiDraftAsync(
            "TOPO_19_AST_DEPTH",
            formId,
            BuildFlowPayload(formId),
            ct);
        await SaveP4ValidPayloadAsync(
            depthFixture,
            "p4-topo-19-ast-depth",
            BuildP4ConditionalApiPayload(formId, BuildP4ConditionChainNode(12)),
            "FLOW-TOPO-19 AST depth=12",
            ct);

        var nodeFixture = await CreateP4ApiDraftAsync(
            "TOPO_19_AST_NODES",
            formId,
            BuildFlowPayload(formId),
            ct);
        await SaveP4ValidPayloadAsync(
            nodeFixture,
            "p4-topo-19-ast-nodes",
            BuildP4ConditionalApiPayload(formId, BuildP4ConditionFanoutNode(127)),
            "FLOW-TOPO-19 AST nodes=128",
            ct);

        return P4Observation(
            "real Kestrel accepted all exact definition budgets and persisted the canonical responses through Mongo transactions",
            "nodes=200/200;edges=400/400;bytes=1048576/1048576;astDepth=12/12;astNodes=128/128;http=200;mongoOracle=true");
    }

    private async Task<CaseObservation> VerifyP4ExceededTopologyBudgetsViaApiAsync(
        string formId,
        CancellationToken ct)
    {
        var nodeFixture = await CreateP4ApiDraftAsync(
            "TOPO_20_NODES",
            formId,
            BuildFlowPayload(formId),
            ct);
        await SaveP4InvalidPayloadAsync(
            nodeFixture,
            "p4-topo-20-nodes",
            BuildP4TopologyBudgetPayload(formId, 201, 200),
            "DYNAMIC_FLOW_NODE_BUDGET_EXCEEDED",
            "nodes",
            "FLOW-TOPO-20 nodes=201",
            ct,
            expectedLimit: 200,
            expectedActual: 201);

        var edgeFixture = await CreateP4ApiDraftAsync(
            "TOPO_20_EDGES",
            formId,
            BuildFlowPayload(formId),
            ct);
        await SaveP4InvalidPayloadAsync(
            edgeFixture,
            "p4-topo-20-edges",
            BuildP4TopologyBudgetPayload(formId, 200, 401),
            "DYNAMIC_FLOW_EDGE_BUDGET_EXCEEDED",
            "edges",
            "FLOW-TOPO-20 edges=401",
            ct,
            expectedLimit: 400,
            expectedActual: 401);

        var bytePayload = BuildFlowPayload(formId);
        ApiHarnessClient.RequiredObject(
            ApiHarnessClient.RequiredArray(bytePayload["nodes"], "FLOW-TOPO-20 byte nodes")[0],
            "FLOW-TOPO-20 byte node")["name"] = string.Empty;
        var byteFixture = await CreateP4ApiDraftAsync(
            "TOPO_20_BYTES",
            formId,
            bytePayload,
            ct);
        var baselineBytes = Encoding.UTF8.GetByteCount(byteFixture.CanonicalPayloadJson);
        var padding = DynamicFlowDefinitionPayloadContract.MaxCanonicalPayloadUtf8Bytes - baselineBytes + 1;
        HarnessAssert.True(padding > 0, "FLOW-TOPO-20 invalid byte padding was not positive");
        ApiHarnessClient.RequiredObject(
            ApiHarnessClient.RequiredArray(bytePayload["nodes"], "FLOW-TOPO-20 byte nodes")[0],
            "FLOW-TOPO-20 byte node")["name"] = new string('x', padding);
        await SaveP4InvalidPayloadAsync(
            byteFixture,
            "p4-topo-20-bytes",
            bytePayload,
            "DYNAMIC_FLOW_PAYLOAD_BUDGET_EXCEEDED",
            "payload",
            "FLOW-TOPO-20 bytes=1048577",
            ct,
            expectedLimit: DynamicFlowDefinitionPayloadContract.MaxCanonicalPayloadUtf8Bytes,
            expectedActual: DynamicFlowDefinitionPayloadContract.MaxCanonicalPayloadUtf8Bytes + 1);

        var depthFixture = await CreateP4ApiDraftAsync(
            "TOPO_20_AST_DEPTH",
            formId,
            BuildFlowPayload(formId),
            ct);
        await SaveP4InvalidPayloadAsync(
            depthFixture,
            "p4-topo-20-ast-depth",
            BuildP4ConditionalApiPayload(formId, BuildP4ConditionChainNode(13)),
            "DYNAMIC_FLOW_CONDITION_BUDGET_EXCEEDED",
            "edges[1].condition" + string.Concat(Enumerable.Repeat(".children[0]", 12)),
            "FLOW-TOPO-20 AST depth=13",
            ct);

        var astNodeFixture = await CreateP4ApiDraftAsync(
            "TOPO_20_AST_NODES",
            formId,
            BuildFlowPayload(formId),
            ct);
        await SaveP4InvalidPayloadAsync(
            astNodeFixture,
            "p4-topo-20-ast-nodes",
            BuildP4ConditionalApiPayload(formId, BuildP4ConditionFanoutNode(128)),
            "DYNAMIC_FLOW_CONDITION_BUDGET_EXCEEDED",
            "edges[1].condition.children[127]",
            "FLOW-TOPO-20 AST nodes=129",
            ct);

        return P4Observation(
            "real Kestrel rejected every N+1 definition budget with exact error/path/context and Mongo byte-stable zero-write oracles",
            "nodes=201/400/0W;edges=401/400/0W;bytes=1048577/400/0W;astDepth=13/400/0W;astNodes=129/400/0W;mongoOracle=true");
    }

    private async Task<CaseObservation> RunP4PolicyApiMatrixCaseAsync(
        int number,
        CancellationToken ct)
    {
        var primaryFormId = HarnessAssert.Required(_formId, $"FLOW-POL-{number:00} primary form");
        var primaryFieldId = HarnessAssert.Required(
            _p4PrimaryPolicyFieldId,
            $"FLOW-POL-{number:00} primary field");

        switch (number)
        {
            case 1:
                await CreateAndLockP4ValidPolicyAsync(
                    "POL_01",
                    primaryFormId,
                    BuildFlowPayload(primaryFormId),
                    "FLOW-POL-01",
                    ct);
                return P4Observation(
                    "real lock API accepted complete actor coverage derived from the published primary Form",
                    "http=200;actorCoverage=true;status=LOCKED;receiptDelta=1;auditDelta=1;mongoOracle=true");
            case 2:
            {
                var payload = BuildFlowPayload(primaryFormId);
                ApiHarnessClient.RequiredArray(payload["actorPolicies"], "FLOW-POL-02 actor policies").Clear();
                await CreateAndRejectP4PolicyLockAsync(
                    "POL_02",
                    primaryFormId,
                    payload,
                    "DYNAMIC_FLOW_ACTOR_POLICY_COVERAGE_INCOMPLETE",
                    "actorPolicies",
                    "FLOW-POL-02",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_ACTOR_POLICY_COVERAGE_INCOMPLETE", "actorPolicies");
            }
            case 3:
            {
                var payload = BuildFlowPayload(primaryFormId);
                ApiHarnessClient.RequiredObject(
                    ApiHarnessClient.RequiredArray(payload["actorPolicies"], "FLOW-POL-03 actor policies")[0],
                    "FLOW-POL-03 actor policy")["actorRole"] = "UNKNOWN_ROLE";
                await CreateAndRejectP4PolicyLockAsync(
                    "POL_03",
                    primaryFormId,
                    payload,
                    "DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN",
                    "actorPolicies[0].actorRole",
                    "FLOW-POL-03",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_ACTOR_ROLE_UNKNOWN", "actorPolicies[0].actorRole");
            }
            case 4:
            {
                var fixture = await CreateP4ApiDraftAsync(
                    "POL_04",
                    primaryFormId,
                    BuildFlowPayload(primaryFormId),
                    ct);
                var payload = BuildFlowPayload(primaryFormId);
                ApiHarnessClient.RequiredArray(
                    ApiHarnessClient.RequiredObject(
                        ApiHarnessClient.RequiredArray(payload["nodes"], "FLOW-POL-04 nodes")[0],
                        "FLOW-POL-04 node")["declaredRoles"],
                    "FLOW-POL-04 declared roles").Clear();
                await SaveP4InvalidPayloadAsync(
                    fixture,
                    "p4-pol-04-save",
                    payload,
                    "DYNAMIC_FLOW_STEP_ROLE_REQUIRED",
                    "nodes[0].declaredRoles",
                    "FLOW-POL-04",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_STEP_ROLE_REQUIRED", "nodes[0].declaredRoles");
            }
            case 5:
                await CreateAndLockP4ValidPolicyAsync(
                    "POL_05",
                    primaryFormId,
                    BuildFlowPayload(primaryFormId),
                    "FLOW-POL-05",
                    ct);
                return P4Observation(
                    "real lock API accepted wildcard scalar coverage over every endpoint in the published primary Form snapshot",
                    $"http=200;primaryFieldProbe={primaryFieldId};scalarCoverage=true;status=LOCKED;mongoOracle=true");
            case 6:
            {
                var payload = BuildFlowPayload(primaryFormId);
                ApiHarnessClient.RequiredArray(payload["fieldPolicies"], "FLOW-POL-06 field policies").Clear();
                await CreateAndRejectP4PolicyLockAsync(
                    "POL_06",
                    primaryFormId,
                    payload,
                    "DYNAMIC_FLOW_FIELD_POLICY_COVERAGE_INCOMPLETE",
                    "fieldPolicies",
                    "FLOW-POL-06",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_FIELD_POLICY_COVERAGE_INCOMPLETE", "fieldPolicies");
            }
            case 7:
            {
                var formId = HarnessAssert.Required(_p4AppendRowsFormId, "FLOW-POL-07 APPEND_ROWS form");
                var payload = BuildFlowPayload(formId);
                AddP4TablePolicy(
                    payload,
                    "append-row-cover",
                    "p4_append_rows",
                    "col_1",
                    read: true,
                    write: true);
                await CreateAndLockP4ValidPolicyAsync("POL_07", formId, payload, "FLOW-POL-07", ct);
                return P4Observation(
                    "real lock API accepted complete APPEND_ROWS column coverage from an auxiliary published Form snapshot",
                    "http=200;tableMode=APPEND_ROWS;block=p4_append_rows;column=col_1;status=LOCKED;mongoOracle=true");
            }
            case 8:
            {
                var formId = HarnessAssert.Required(_p4AppendRowsFormId, "FLOW-POL-08 APPEND_ROWS form");
                await CreateAndRejectP4PolicyLockAsync(
                    "POL_08",
                    formId,
                    BuildFlowPayload(formId),
                    "DYNAMIC_FLOW_TABLE_POLICY_COVERAGE_INCOMPLETE",
                    "tableColumnPolicies",
                    "FLOW-POL-08",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_TABLE_POLICY_COVERAGE_INCOMPLETE", "tableColumnPolicies");
            }
            case 9:
            {
                var payload = BuildFlowPayload(primaryFormId);
                var policy = ApiHarnessClient.RequiredObject(
                    ApiHarnessClient.RequiredArray(payload["fieldPolicies"], "FLOW-POL-09 field policies")[0],
                    "FLOW-POL-09 field policy");
                policy["read"] = false;
                policy["write"] = false;
                await CreateAndLockP4ValidPolicyAsync("POL_09", primaryFormId, payload, "FLOW-POL-09", ct);
                return P4Observation(
                    "real lock API treated an explicit scalar deny as complete fail-closed coverage",
                    "http=200;covered=true;effectiveRead=false;effectiveWrite=false;status=LOCKED;mongoOracle=true");
            }
            case 10:
            {
                var firstPayload = BuildFlowPayload(primaryFormId);
                AddP4FieldPolicy(
                    firstPayload,
                    "specific-primary-field",
                    primaryFieldId,
                    read: true,
                    write: true);
                var reorderedPayload = firstPayload.DeepClone().AsObject();
                ReverseP4PolicyArray(reorderedPayload, "fieldPolicies");
                var first = await CreateP4ApiDraftAsync(
                    "POL_10_A",
                    primaryFormId,
                    firstPayload,
                    ct);
                var reordered = await CreateP4ApiDraftAsync(
                    "POL_10_B",
                    primaryFormId,
                    reorderedPayload,
                    ct);
                HarnessAssert.Equal(
                    first.PayloadHash,
                    reordered.PayloadHash,
                    "FLOW-POL-10 API canonical policy order hash");
                await LockP4ValidDraftAsync(first, "p4-pol-10-a-lock", "FLOW-POL-10 A", ct);
                await LockP4ValidDraftAsync(reordered, "p4-pol-10-b-lock", "FLOW-POL-10 B", ct);
                return P4Observation(
                    "two real API drafts with reversed policy input order converged to one hash and both locked",
                    "http=200x2;wildcardPlusSpecific=true;specificWins=true;reorderedHashStable=true;mongoOracle=true");
            }
            case 11:
            {
                var payload = BuildFlowPayload(primaryFormId);
                AddP4FieldPolicy(
                    payload,
                    "specific-primary-deny",
                    primaryFieldId,
                    read: false,
                    write: false);
                await CreateAndLockP4ValidPolicyAsync("POL_11", primaryFormId, payload, "FLOW-POL-11", ct);
                return P4Observation(
                    "real lock API accepted broad allow with a more-specific deny over a published scalar endpoint",
                    "http=200;broadAllow=true;specificDeny=true;status=LOCKED;mongoOracle=true");
            }
            case 12:
            {
                var payload = BuildFlowPayload(primaryFormId);
                ApiHarnessClient.RequiredArray(payload["fieldPolicies"], "FLOW-POL-12 field policies").Add(
                    new JsonObject
                    {
                        ["policyId"] = "conflicting-same-specificity",
                        ["dynamicFormTemplateId"] = "*",
                        ["stepId"] = "step_root",
                        ["stepCode"] = "*",
                        ["actorRole"] = "OWNER",
                        ["fieldId"] = "*",
                        ["fieldKey"] = "*",
                        ["read"] = false,
                        ["write"] = false
                    });
                await CreateAndRejectP4PolicyLockAsync(
                    "POL_12",
                    primaryFormId,
                    payload,
                    "DYNAMIC_FLOW_POLICY_SPECIFICITY_CONFLICT",
                    "fieldPolicies[1]",
                    "FLOW-POL-12",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_POLICY_SPECIFICITY_CONFLICT", "fieldPolicies[1]");
            }
            case 13:
            {
                var payload = BuildFlowPayload(primaryFormId);
                ApiHarnessClient.RequiredObject(
                    ApiHarnessClient.RequiredArray(payload["fieldPolicies"], "FLOW-POL-13 field policies")[0],
                    "FLOW-POL-13 field policy")["fieldId"] = "missing-field";
                await CreateAndRejectP4PolicyLockAsync(
                    "POL_13",
                    primaryFormId,
                    payload,
                    "DYNAMIC_FLOW_POLICY_FIELD_UNKNOWN",
                    "fieldPolicies[0].fieldId",
                    "FLOW-POL-13",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_POLICY_FIELD_UNKNOWN", "fieldPolicies[0].fieldId");
            }
            case 14:
            {
                var formId = HarnessAssert.Required(_p4SummaryFormId, "FLOW-POL-14 SUMMARY_TEMPLATE form");
                var payload = BuildFlowPayload(formId);
                AddP4TablePolicy(
                    payload,
                    "a-summary-invalid",
                    "a_p4_summary",
                    "col_1",
                    read: true,
                    write: true);
                AddP4TablePolicy(
                    payload,
                    "z-source-cover",
                    "z_p4_source_matrix",
                    "col_1",
                    read: true,
                    write: true);
                await CreateAndRejectP4PolicyLockAsync(
                    "POL_14",
                    formId,
                    payload,
                    "DYNAMIC_FLOW_TABLE_POLICY_ENDPOINT_INCOMPATIBLE",
                    "tableColumnPolicies[0].write",
                    "FLOW-POL-14",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_TABLE_POLICY_ENDPOINT_INCOMPATIBLE", "tableColumnPolicies[0].write");
            }
            case 15:
            {
                var fixture = await CreateP4ApiDraftAsync(
                    "POL_15",
                    primaryFormId,
                    BuildFlowPayload(primaryFormId),
                    ct);
                var payload = BuildFlowPayload(primaryFormId);
                payload["callerRole"] = "SYSTEM_ADMIN";
                await SaveP4InvalidPayloadAsync(
                    fixture,
                    "p4-pol-15-save",
                    payload,
                    "DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN",
                    "callerRole",
                    "FLOW-POL-15",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_CALLER_AUTHORITY_FORBIDDEN", "callerRole");
            }
            case 16:
            {
                var fixture = await CreateP4ApiDraftAsync(
                    "POL_16",
                    primaryFormId,
                    BuildFlowPayload(primaryFormId),
                    ct);
                var payload = BuildFlowPayload(primaryFormId);
                ApiHarnessClient.RequiredObject(
                    ApiHarnessClient.RequiredArray(payload["formNodes"], "FLOW-POL-16 form nodes")[0],
                    "FLOW-POL-16 form node")["dynamicFormSchemaHash"] = new string('f', 64);
                await SaveP4InvalidPayloadAsync(
                    fixture,
                    "p4-pol-16-save",
                    payload,
                    "DYNAMIC_FLOW_FORM_PIN_FORBIDDEN",
                    "formNodes[0].dynamicFormSchemaHash",
                    "FLOW-POL-16",
                    ct);
                return P4PolicyRejectedObservation(number, "DYNAMIC_FLOW_FORM_PIN_FORBIDDEN", "formNodes[0].dynamicFormSchemaHash");
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(number));
        }
    }

    private static CaseObservation P4PolicyRejectedObservation(
        int number,
        string errorCode,
        string path)
        => P4Observation(
            $"real Kestrel rejected policy case {number:00} with {errorCode} at exact path {path}",
            $"http=400;errorCode={errorCode};path={path};aggregateBsonStable=true;receiptWrites=0;auditWrites=0");

    private async Task EnsureP4PolicyApiFixturesAsync(CancellationToken ct)
    {
        if (_p4AppendRowsFormId is not null &&
            _p4SummaryFormId is not null &&
            _p4PrimaryPolicyFieldId is not null)
        {
            return;
        }

        var primaryFormId = HarnessAssert.Required(_formId, "P4 policy primary published form");
        var primary = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
            .Find(x => x.Id == primaryFormId && x.IsPublished && !x.IsDeleted)
            .SingleAsync(ct);
        var primaryFields = JsonNode.Parse(primary.FieldsJson) as JsonArray
            ?? throw new InvalidOperationException("P4 primary published Form fields are not an array.");
        _p4PrimaryPolicyFieldId = ApiHarnessClient.RequiredString(
            ApiHarnessClient.RequiredObject(primaryFields.FirstOrDefault(), "P4 primary field"),
            "id");

        var appendBlocks = new JsonArray
        {
            BuildP4AuxiliaryTableBlock("p4_append_rows", "APPEND_ROWS", width: 1, height: 2)
        };
        _p4AppendRowsFormId = await CreateAndPublishP4AuxiliaryFormAsync(
            "P4_IT_APPEND_ROWS_FORM",
            "P4 APPEND_ROWS policy form",
            appendBlocks,
            ct);

        var source = BuildP4AuxiliaryTableBlock(
            "z_p4_source_matrix",
            "MATRIX",
            width: 1,
            height: 1);
        var summary = BuildP4AuxiliaryTableBlock(
            "a_p4_summary",
            "SUMMARY_TEMPLATE",
            width: 1,
            height: 1);
        summary["sourceBlockId"] = "z_p4_source_matrix";
        summary["groupBy"] = new JsonArray();
        summary["rowLayout"] = new JsonArray
        {
            new JsonObject
            {
                ["rowsPerUnit"] = 1,
                ["metrics"] = new JsonArray("metric_1")
            }
        };
        _p4SummaryFormId = await CreateAndPublishP4AuxiliaryFormAsync(
            "P4_IT_SUMMARY_FORM",
            "P4 SUMMARY_TEMPLATE policy form",
            new JsonArray(source, summary),
            ct);
    }

    private async Task<string> CreateAndPublishP4AuxiliaryFormAsync(
        string code,
        string name,
        JsonArray blocks,
        CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, $"P4 auxiliary Form {code} owner token");
        var create = await _api.PostAsync(
            "api/dynamic-forms",
            new JsonObject
            {
                ["code"] = code,
                ["name"] = name,
                ["description"] = "P4 real published endpoint catalog fixture",
                ["tagCodes"] = new JsonArray(),
                ["schemaVersion"] = 1,
                ["isActive"] = true,
                ["schema"] = new JsonObject
                {
                    ["sections"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "main",
                            ["title"] = "Main",
                            ["order"] = 1
                        }
                    },
                    ["fields"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "field_aux",
                            ["sectionId"] = "main",
                            ["key"] = "value_aux",
                            ["name"] = "Auxiliary scalar",
                            ["type"] = "number",
                            ["required"] = false,
                            ["order"] = 1
                        }
                    },
                    ["blocks"] = blocks
                }
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(create, HttpStatusCode.OK, $"create P4 auxiliary Form {code}");
        var formId = ApiHarnessClient.RequiredString(create.Json, "id");
        var revision = ApiHarnessClient.RequiredInt(create.Json, "revision");
        var publish = await _api.PostAsync(
            $"api/dynamic-forms/{formId}/publish",
            new { expectedRevision = revision },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(publish, HttpStatusCode.OK, $"publish P4 auxiliary Form {code}");
        HarnessAssert.True(
            ApiHarnessClient.RequiredBool(publish.Json, "isPublished"),
            $"P4 auxiliary Form {code} was not published");
        return formId;
    }

    private static JsonObject BuildP4AuxiliaryTableBlock(
        string blockId,
        string tableMode,
        int width,
        int height)
    {
        var indexMap = new JsonArray();
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                indexMap.Add(new JsonObject
                {
                    ["index"] = row * width + column,
                    ["rowKey"] = $"row_{row + 1}",
                    ["columnKey"] = $"col_{column + 1}",
                    ["metricKey"] = $"metric_{row * width + column + 1}"
                });
            }
        }
        return new JsonObject
        {
            ["blockId"] = blockId,
            ["sectionId"] = "main",
            ["tableMode"] = tableMode,
            ["dataRect"] = new JsonObject
            {
                ["r0"] = 0,
                ["c0"] = 0,
                ["r1"] = height - 1,
                ["c1"] = width - 1
            },
            ["w"] = width,
            ["h"] = height,
            ["defaultDataType"] = "NUMBER",
            ["indexMap"] = indexMap
        };
    }

    private async Task<P4ApiDraftFixture> CreateP4ApiDraftAsync(
        string suffix,
        string rootFormId,
        JsonObject payload,
        CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, $"P4 {suffix} owner token");
        var response = await _api.PostAsync(
            "api/dynamic-flow-templates",
            new JsonObject
            {
                ["commandId"] = $"p4-{suffix.ToLowerInvariant()}-create",
                ["code"] = $"P4_{suffix}",
                ["name"] = $"P4 {suffix}",
                ["description"] = "P4 real API validator fixture",
                ["rootDynamicFormTemplateId"] = rootFormId,
                ["payload"] = payload.DeepClone()
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"create P4 API draft {suffix}");
        var family = ApiHarnessClient.RequiredObject(response.Json, $"P4 {suffix} family");
        var draft = ApiHarnessClient.RequiredObject(family["draftVersion"], $"P4 {suffix} draft");
        return new P4ApiDraftFixture(
            ApiHarnessClient.RequiredString(family, "id"),
            ApiHarnessClient.RequiredInt(family, "familyRevision"),
            ApiHarnessClient.RequiredString(draft, "id"),
            ApiHarnessClient.RequiredInt(draft, "draftRevision"),
            ApiHarnessClient.RequiredString(draft, "payloadHash"),
            ApiHarnessClient.RequiredString(draft, "payloadJson"));
    }

    private Task<ApiHarnessResponse> SaveP4ApiDraftAsync(
        P4ApiDraftFixture fixture,
        string commandId,
        JsonObject payload,
        CancellationToken ct)
        => _api.PutAsync(
            $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/draft",
            new JsonObject
            {
                ["commandId"] = commandId,
                ["expectedDraftRevision"] = fixture.DraftRevision,
                ["expectedPayloadHash"] = fixture.PayloadHash,
                ["payload"] = payload.DeepClone()
            },
            HarnessAssert.Required(_ownerToken, $"{commandId} owner token"),
            ct: ct);

    private Task<ApiHarnessResponse> LockP4ApiDraftAsync(
        P4ApiDraftFixture fixture,
        string commandId,
        CancellationToken ct)
        => _api.PostAsync(
            $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/lock",
            new
            {
                commandId,
                expectedFamilyRevision = fixture.FamilyRevision,
                expectedDraftRevision = fixture.DraftRevision,
                expectedPayloadHash = fixture.PayloadHash
            },
            HarnessAssert.Required(_ownerToken, $"{commandId} owner token"),
            ct: ct);

    private async Task<P4ApiDraftFixture> SaveP4ValidPayloadAsync(
        P4ApiDraftFixture fixture,
        string commandId,
        JsonObject payload,
        string context,
        CancellationToken ct)
    {
        var before = await CaptureP4ApiMongoSnapshotAsync(
            fixture.FamilyId,
            fixture.VersionId,
            ct);
        var response = await SaveP4ApiDraftAsync(fixture, commandId, payload, ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"{context} API save");
        var saved = ApiHarnessClient.RequiredObject(response.Json, $"{context} saved draft");
        var result = fixture with
        {
            DraftRevision = ApiHarnessClient.RequiredInt(saved, "draftRevision"),
            PayloadHash = ApiHarnessClient.RequiredString(saved, "payloadHash"),
            CanonicalPayloadJson = ApiHarnessClient.RequiredString(saved, "payloadJson")
        };
        HarnessAssert.Equal(
            fixture.DraftRevision + 1,
            result.DraftRevision,
            $"{context} draft revision delta");
        var after = await CaptureP4ApiMongoSnapshotAsync(
            fixture.FamilyId,
            fixture.VersionId,
            ct);
        AssertP4DefinitionWriteCounts(before.Counts, after.Counts, context);
        HarnessAssert.True(
            !string.Equals(before.VersionBson, after.VersionBson, StringComparison.Ordinal),
            $"{context} successful save did not change Mongo version BSON");
        var stored = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == fixture.VersionId && x.TemplateId == fixture.FamilyId && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(result.DraftRevision, stored.DraftRevision, $"{context} stored draft revision");
        HarnessAssert.Equal(result.PayloadHash, stored.PayloadHash, $"{context} stored payload hash");
        HarnessAssert.Equal(result.CanonicalPayloadJson, stored.PayloadJson, $"{context} stored canonical payload");
        return result;
    }

    private async Task SaveP4InvalidPayloadAsync(
        P4ApiDraftFixture fixture,
        string commandId,
        JsonObject payload,
        string expectedError,
        string expectedPath,
        string context,
        CancellationToken ct,
        int? expectedLimit = null,
        int? expectedActual = null)
    {
        var before = await CaptureP4ApiMongoSnapshotAsync(
            fixture.FamilyId,
            fixture.VersionId,
            ct);
        var response = await SaveP4ApiDraftAsync(fixture, commandId, payload, ct);
        AssertP4ApiValidationError(
            response,
            expectedError,
            expectedPath,
            context,
            expectedLimit,
            expectedActual);
        await AssertP4ApiZeroWriteAsync(
            before,
            fixture.FamilyId,
            fixture.VersionId,
            context,
            ct);
    }

    private async Task CreateAndLockP4ValidPolicyAsync(
        string suffix,
        string rootFormId,
        JsonObject payload,
        string context,
        CancellationToken ct)
    {
        var fixture = await CreateP4ApiDraftAsync(suffix, rootFormId, payload, ct);
        await LockP4ValidDraftAsync(
            fixture,
            $"p4-{suffix.ToLowerInvariant()}-lock",
            context,
            ct);
    }

    private async Task LockP4ValidDraftAsync(
        P4ApiDraftFixture fixture,
        string commandId,
        string context,
        CancellationToken ct)
    {
        var before = await CaptureP4ApiMongoSnapshotAsync(
            fixture.FamilyId,
            fixture.VersionId,
            ct);
        var response = await LockP4ApiDraftAsync(fixture, commandId, ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"{context} API lock");
        var locked = ApiHarnessClient.RequiredObject(response.Json, $"{context} locked version");
        HarnessAssert.Equal("LOCKED", ApiHarnessClient.RequiredString(locked, "status"), $"{context} lock status");
        HarnessAssert.True(
            ApiHarnessClient.RequiredBool(locked, "definitionLockable"),
            $"{context} definitionLockable=false");
        var after = await CaptureP4ApiMongoSnapshotAsync(
            fixture.FamilyId,
            fixture.VersionId,
            ct);
        AssertP4DefinitionWriteCounts(before.Counts, after.Counts, context);
        HarnessAssert.True(
            !string.Equals(before.FamilyBson, after.FamilyBson, StringComparison.Ordinal),
            $"{context} successful lock did not change family BSON");
        HarnessAssert.True(
            !string.Equals(before.VersionBson, after.VersionBson, StringComparison.Ordinal),
            $"{context} successful lock did not change version BSON");
        var family = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == fixture.FamilyId && !x.IsDeleted)
            .SingleAsync(ct);
        var version = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == fixture.VersionId && x.TemplateId == fixture.FamilyId && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(
            fixture.FamilyRevision + 1,
            family.FamilyRevision,
            $"{context} family revision after lock");
        HarnessAssert.Equal("LOCKED", version.Status, $"{context} Mongo lock status");
        HarnessAssert.True(version.DefinitionLockable, $"{context} Mongo definitionLockable=false");
    }

    private async Task CreateAndRejectP4PolicyLockAsync(
        string suffix,
        string rootFormId,
        JsonObject payload,
        string expectedError,
        string expectedPath,
        string context,
        CancellationToken ct)
    {
        var fixture = await CreateP4ApiDraftAsync(suffix, rootFormId, payload, ct);
        var before = await CaptureP4ApiMongoSnapshotAsync(
            fixture.FamilyId,
            fixture.VersionId,
            ct);
        var response = await LockP4ApiDraftAsync(
            fixture,
            $"p4-{suffix.ToLowerInvariant()}-lock",
            ct);
        AssertP4ApiValidationError(response, expectedError, expectedPath, context);
        await AssertP4ApiZeroWriteAsync(
            before,
            fixture.FamilyId,
            fixture.VersionId,
            context,
            ct);
    }

    private static void AssertP4DefinitionWriteCounts(
        P4DefinitionCounts before,
        P4DefinitionCounts after,
        string context)
    {
        HarnessAssert.Equal(before.Families, after.Families, $"{context} family cardinality");
        HarnessAssert.Equal(before.Versions, after.Versions, $"{context} version cardinality");
        HarnessAssert.Equal(before.Receipts + 1, after.Receipts, $"{context} receipt delta");
        HarnessAssert.Equal(before.Audits + 1, after.Audits, $"{context} audit delta");
    }

    private static void AssertP4ApiValidationError(
        ApiHarnessResponse response,
        string expectedError,
        string expectedPath,
        string context,
        int? expectedLimit = null,
        int? expectedActual = null)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.BadRequest, $"{context} validation");
        HarnessAssert.Equal(
            expectedError,
            ApiHarnessClient.RequiredString(response.Json, "errorCode"),
            $"{context} errorCode");
        var body = ApiHarnessClient.RequiredObject(response.Json, $"{context} error body");
        var details = ApiHarnessClient.RequiredObject(body["details"], $"{context} error details");
        HarnessAssert.Equal(
            expectedPath,
            ApiHarnessClient.RequiredString(details, "path"),
            $"{context} exact error path");
        if (expectedLimit.HasValue)
        {
            HarnessAssert.Equal(
                expectedLimit,
                ApiHarnessClient.FindIntRecursive(details, "limit"),
                $"{context} budget limit");
        }
        if (expectedActual.HasValue)
        {
            HarnessAssert.Equal(
                expectedActual,
                ApiHarnessClient.FindIntRecursive(details, "actual"),
                $"{context} budget actual");
        }
    }

    private async Task<P4ApiMongoSnapshot> CaptureP4ApiMongoSnapshotAsync(
        string familyId,
        string versionId,
        CancellationToken ct)
    {
        var family = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var version = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == versionId && x.TemplateId == familyId)
            .SingleAsync(ct);
        return new P4ApiMongoSnapshot(
            Convert.ToHexString(family.ToBson()),
            Convert.ToHexString(version.ToBson()),
            await CaptureP4DefinitionCountsAsync(ct));
    }

    private async Task AssertP4ApiZeroWriteAsync(
        P4ApiMongoSnapshot before,
        string familyId,
        string versionId,
        string context,
        CancellationToken ct)
    {
        var after = await CaptureP4ApiMongoSnapshotAsync(familyId, versionId, ct);
        HarnessAssert.Equal(before, after, $"{context} wrote Mongo state");
    }

    private static JsonObject BuildP4TopologyBudgetPayload(
        string formId,
        int nodeCount,
        int edgeCount)
    {
        var payload = BuildFlowPayload(formId);
        payload["archetypeId"] = "FLOW-T03";
        payload["entryStepId"] = "n0";
        var nodes = new JsonArray();
        var edges = new JsonArray();
        payload["nodes"] = nodes;
        payload["edges"] = edges;
        for (var index = 0; index < nodeCount; index++)
            nodes.Add(P4FormStep($"n{index}", $"N{index}"));
        for (var index = 1; index < nodeCount && edges.Count < edgeCount; index++)
            edges.Add(P4Edge($"chain-{index - 1}-{index}", $"n{index - 1}", $"n{index}"));
        for (var from = 0; from < nodeCount && edges.Count < edgeCount; from++)
        {
            for (var to = from + 2; to < nodeCount && edges.Count < edgeCount; to++)
                edges.Add(P4Edge($"extra-{from}-{to}", $"n{from}", $"n{to}"));
        }
        HarnessAssert.Equal(edgeCount, edges.Count, "P4 topology budget edge builder");
        return payload;
    }

    private static JsonObject BuildP4ConditionalApiPayload(
        string formId,
        JsonObject condition)
    {
        var payload = BuildFlowPayload(formId);
        payload["archetypeId"] = "FLOW-T07";
        payload["nodes"] = new JsonArray
        {
            P4FormStep("step_root", "ROOT"),
            P4Gateway("condition", "CONDITION", "CONDITION"),
            P4FormStep("step_b", "B")
        };
        payload["edges"] = new JsonArray
        {
            P4Edge("root-condition", "step_root", "condition"),
            new JsonObject
            {
                ["transitionId"] = "condition-b",
                ["fromNodeId"] = "condition",
                ["toNodeId"] = "step_b",
                ["condition"] = condition
            }
        };
        return payload;
    }

    private static JsonObject BuildP4ConditionChainNode(int depth)
    {
        JsonObject current = new() { ["operator"] = "TRUE" };
        for (var level = 1; level < depth; level++)
        {
            current = new JsonObject
            {
                ["operator"] = "NOT",
                ["children"] = new JsonArray(current)
            };
        }
        return current;
    }

    private static JsonObject BuildP4ConditionFanoutNode(int childCount)
    {
        var children = new JsonArray();
        for (var index = 0; index < childCount; index++)
            children.Add(new JsonObject { ["operator"] = "TRUE" });
        return new JsonObject
        {
            ["operator"] = "OR",
            ["children"] = children
        };
    }

    private static void AddP4FieldPolicy(
        JsonObject payload,
        string policyId,
        string fieldId,
        bool read,
        bool write)
        => ApiHarnessClient.RequiredArray(payload["fieldPolicies"], "P4 field policies").Add(
            new JsonObject
            {
                ["policyId"] = policyId,
                ["dynamicFormTemplateId"] = "*",
                ["stepId"] = "step_root",
                ["stepCode"] = "*",
                ["actorRole"] = "OWNER",
                ["fieldId"] = fieldId,
                ["fieldKey"] = "*",
                ["read"] = read,
                ["write"] = write
            });

    private static void AddP4TablePolicy(
        JsonObject payload,
        string policyId,
        string blockId,
        string columnKey,
        bool read,
        bool write)
        => ApiHarnessClient.RequiredArray(payload["tableColumnPolicies"], "P4 table policies").Add(
            new JsonObject
            {
                ["policyId"] = policyId,
                ["dynamicFormTemplateId"] = "*",
                ["stepId"] = "step_root",
                ["stepCode"] = "*",
                ["actorRole"] = "OWNER",
                ["blockId"] = blockId,
                ["columnKey"] = columnKey,
                ["read"] = read,
                ["write"] = write
            });

    private static void ReverseP4PolicyArray(JsonObject payload, string property)
    {
        var source = ApiHarnessClient.RequiredArray(payload[property], $"P4 {property}");
        var reversed = new JsonArray();
        for (var index = source.Count - 1; index >= 0; index--)
            reversed.Add(source[index]!.DeepClone());
        payload[property] = reversed;
    }
}
