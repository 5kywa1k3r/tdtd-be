using System.Text.Json;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowTypedConditionalTopologyContractTests
{
    public static void Run()
    {
        ExactTopologySelectsRuleAndOrderedDefault();
        TruthTableCoversStringDecimalBoolDateAndNull();
        MissingFactsAreNullAndTypeMismatchFailsClosed();
        SnapshotAndDecisionIdentityAreDeterministic();
        ArbitraryFactPathsAndDefaultDriftFailClosed();
        P6CandidateKeepsT07EligibleAtP606Boundary();
    }

    private static void ExactTopologySelectsRuleAndOrderedDefault()
    {
        var canonical = Canonical(Conditional(Condition(
            "EQ",
            "report.status",
            Json("\"APPROVED\""))));
        var topology = DynamicFlowTypedConditionalTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        var matched = Snapshot(
            ("report.status", Fact(DynamicFlowTypedFactKind.String, "APPROVED")));
        var selected =
            DynamicFlowTypedConditionalTopologyContract.SelectBranch(
                topology,
                matched);
        Require(selected.SelectedEdgeId == "edge-g-b", "rule edge must win");
        Require(selected.ReasonCode == "RULE_MATCHED", "rule reason");

        var missing = Snapshot(
            ("report.status", Fact(DynamicFlowTypedFactKind.Null, null)));
        var fallback =
            DynamicFlowTypedConditionalTopologyContract.SelectBranch(
                topology,
                missing);
        Require(
            fallback.SelectedEdgeId == "edge-g-z-default",
            "ordered TRUE edge must be the deterministic default");
        Require(
            fallback.ReasonCode == "DEFAULT_BRANCH_SELECTED",
            "default reason");
    }

    private static void TruthTableCoversStringDecimalBoolDateAndNull()
    {
        var snapshot = Snapshot(
            ("report.status", Fact(DynamicFlowTypedFactKind.String, "alpha")),
            ("report.payloadRevision", Fact(DynamicFlowTypedFactKind.Decimal, 12.5m)),
            ("report.exists", Fact(DynamicFlowTypedFactKind.Bool, true)),
            ("report.reportDate", Fact(
                DynamicFlowTypedFactKind.Date,
                DateTime.Parse(
                    "2026-07-27T00:00:00Z").ToUniversalTime())),
            ("review.outcome", Fact(DynamicFlowTypedFactKind.Null, null)));

        Require(Evaluate("EQ", "report.status", Json("\"alpha\""), snapshot), "string EQ");
        Require(Evaluate("GT", "report.payloadRevision", Json("10"), snapshot), "decimal GT");
        Require(Evaluate("EQ", "report.exists", Json("true"), snapshot), "bool EQ");
        Require(
            Evaluate(
                "GTE",
                "report.reportDate",
                Json("\"2026-07-26T23:59:59Z\""),
                snapshot),
            "date GTE");
        Require(Evaluate("IS_NULL", "review.outcome", null, snapshot), "null predicate");
        Require(
            Evaluate(
                "IN",
                "report.status",
                null,
                snapshot,
                Json("\"alpha\""),
                Json("\"beta\"")),
            "typed IN");
    }

    private static void MissingFactsAreNullAndTypeMismatchFailsClosed()
    {
        var facts = new Dictionary<string, DynamicFlowTypedFact>(
            StringComparer.Ordinal)
        {
            ["report.status"] = Fact(DynamicFlowTypedFactKind.Null, null),
            ["report.payloadRevision"] =
                Fact(DynamicFlowTypedFactKind.Decimal, 2m)
        };
        var snapshot = new DynamicFlowTypedFactSnapshot(
            facts,
            "{}",
            new string('a', 64));
        var missing = DynamicFlowTypedConditionalTopologyContract.Evaluate(
            Condition("IS_NULL", "review.outcome"),
            snapshot);
        Require(missing.Matched, "missing fact must behave as null");
        var mismatch = DynamicFlowTypedConditionalTopologyContract.Evaluate(
            Condition(
                "GT",
                "report.payloadRevision",
                Json("\"two\"")),
            snapshot);
        Require(
            mismatch.ErrorCode ==
            DynamicFlowTypedConditionalTopologyContract.TypeMismatch,
            "type mismatch must carry the stable fail-closed code");
    }

    private static void SnapshotAndDecisionIdentityAreDeterministic()
    {
        var assignment = new WorkAssignment
        {
            IsActive = true,
            FlowAttemptNo = 3
        };
        var step = new DynamicFlowStepInstance
        {
            AttemptNo = 1,
            State = DynamicFlowStepStates.Approved
        };
        var instance = new DynamicFlowInstance
        {
            PeriodKey = "2026-07"
        };
        var report = new WorkAssignmentReport
        {
            Status = WorkAssignmentReportStatus.Approved,
            PayloadRevision = 7,
            ReportDate = DateTime.SpecifyKind(
                new DateTime(2026, 7, 27),
                DateTimeKind.Utc),
            ReviewerEvaluation = "PASS"
        };
        var first =
            DynamicFlowTypedConditionalTopologyContract.BuildFactSnapshot(
                assignment,
                step,
                instance,
                report);
        var second =
            DynamicFlowTypedConditionalTopologyContract.BuildFactSnapshot(
                assignment,
                step,
                instance,
                report);
        Require(first.SnapshotHash == second.SnapshotHash, "snapshot replay hash");
        Require(first.CanonicalJson == second.CanonicalJson, "snapshot canonical form");
        Require(
            DynamicFlowTypedConditionalTopologyContract.BuildDecisionLedgerId(
                "100000000000000000000001",
                1) ==
            DynamicFlowTypedConditionalTopologyContract.BuildDecisionLedgerId(
                "100000000000000000000001",
                1),
            "decision ledger identity");
    }

    private static void ArbitraryFactPathsAndDefaultDriftFailClosed()
    {
        var arbitrary = Conditional(Condition(
            "EQ",
            "report.raw.secret",
            Json("\"x\"")));
        AssertThrows(arbitrary, "raw/arbitrary fact path must fail closed");

        var misplacedDefault = Conditional(new DynamicFlowConditionDto
        {
            Operator = "TRUE"
        });
        var defaultEdge = misplacedDefault.Edges.Single(edge =>
            edge.TransitionId == "edge-g-z-default");
        defaultEdge.Condition = Condition(
            "EQ",
            "report.status",
            Json("\"APPROVED\""));
        AssertThrows(
            misplacedDefault,
            "default must remain one ordered terminal TRUE branch");
    }

    private static void P6CandidateKeepsT07EligibleAtP606Boundary()
    {
        var t07 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowTypedConditionalTopologyContract.ArchetypeId);
        Require(
            t07.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-05 must open T07 on the Testing-only candidate");
        var t08 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowReviewLoopTopologyContract.ArchetypeId);
        Require(
            t08.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-06 must open T08 without regressing T07");
        var t09 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowSubflowTopologyContract.ArchetypeId);
        Require(
            t09.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-07 must open T09 without regressing T07");
        Require(
            DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T12").Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-10 cumulative boundary opens T12");
    }

    private static bool Evaluate(
        string op,
        string field,
        JsonElement? value,
        DynamicFlowTypedFactSnapshot snapshot,
        params JsonElement[] values)
        => DynamicFlowTypedConditionalTopologyContract.Evaluate(
            new DynamicFlowConditionDto
            {
                Operator = op,
                Field = field,
                Value = value,
                Values = values.ToList()
            },
            snapshot).Matched;

    private static DynamicFlowTypedFactSnapshot Snapshot(
        params (string Name, DynamicFlowTypedFact Fact)[] facts)
        => new(
            facts.ToDictionary(
                pair => pair.Name,
                pair => pair.Fact,
                StringComparer.Ordinal),
            "{}",
            new string('b', 64));

    private static DynamicFlowTypedFact Fact(
        DynamicFlowTypedFactKind kind,
        object? value)
        => new(kind, value);

    private static JsonElement Json(string value)
        => JsonDocument.Parse(value).RootElement.Clone();

    private static DynamicFlowConditionDto Condition(
        string op,
        string? field = null,
        JsonElement? value = null)
        => new()
        {
            Operator = op,
            Field = field,
            Value = value
        };

    private static DynamicFlowCanonicalPayload Canonical(
        DynamicFlowTemplatePayloadDto payload)
        => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            payload,
            null,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));

    private static void AssertThrows(
        DynamicFlowTemplatePayloadDto payload,
        string message)
    {
        try
        {
            var canonical = Canonical(payload);
            DynamicFlowTypedConditionalTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static DynamicFlowTemplatePayloadDto Conditional(
        DynamicFlowConditionDto condition)
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var hashC = new string('c', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId =
                DynamicFlowTypedConditionalTopologyContract.ArchetypeId,
            EntryStepId = "step-a",
            RootDynamicFormTemplateId =
                "300000000000000000000001",
            CatalogVersion = DynamicFlowP6CatalogCandidate.Version,
            CatalogSemanticHash =
                DynamicFlowP6CatalogCandidate.SemanticHash,
            FormNodes =
            [
                Form("form-a", "ROOT", "300000000000000000000001", "310000000000000000000001", hashA),
                Form("form-b", "STEP", "300000000000000000000002", "310000000000000000000002", hashB),
                Form("form-c", "STEP", "300000000000000000000003", "310000000000000000000003", hashC)
            ],
            Nodes =
            [
                Node("step-a", "A", "form-a"),
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "gateway-g",
                    NodeCode = "G",
                    NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto
                    {
                        Kind = DynamicFlowGatewayKinds.Condition
                    }
                },
                Node("step-b", "B", "form-b"),
                Node("step-c", "C", "form-c")
            ],
            Edges =
            [
                Edge("edge-a-g", "step-a", "gateway-g"),
                Edge("edge-g-b", "gateway-g", "step-b", condition),
                Edge(
                    "edge-g-z-default",
                    "gateway-g",
                    "step-c",
                    new DynamicFlowConditionDto { Operator = "TRUE" })
            ]
        };
    }

    private static DynamicFlowFormNodeDto Form(
        string formNodeId,
        string role,
        string versionId,
        string familyId,
        string hash)
        => new()
        {
            FormNodeId = formNodeId,
            Role = role,
            DynamicFormTemplateId = versionId,
            DynamicFormFamilyId = familyId,
            DynamicFormVersionNo = 1,
            DynamicFormSchemaHash = hash,
            DynamicFormSnapshotHash = hash
        };

    private static DynamicFlowTopologyNodeDto Node(
        string nodeId,
        string code,
        string formNodeId)
        => new()
        {
            NodeId = nodeId,
            NodeCode = code,
            NodeKind = DynamicFlowNodeKinds.FormStep,
            FormNodeId = formNodeId,
            DeclaredRoles = ["OWNER"]
        };

    private static DynamicFlowTopologyEdgeDto Edge(
        string id,
        string from,
        string to,
        DynamicFlowConditionDto? condition = null)
        => new()
        {
            TransitionId = id,
            FromNodeId = from,
            ToNodeId = to,
            Condition = condition
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
