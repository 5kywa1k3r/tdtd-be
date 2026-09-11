using System.Text.Json;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowReviewLoopTopologyContractTests
{
    public static void Run()
    {
        ExactOneNodeTopologyDefaultsToTenCycles();
        MaxReviewCyclesAcceptsOneThroughFiftyAndRejectsDrift();
        AttemptsKeepTheLogicalTargetButNeverReuseIdentity();
        GraphCyclesAndAdditionalTargetsFailClosed();
        P6CandidateOpensT08ButKeepsT09ThroughT12Blocked();
    }

    private static void ExactOneNodeTopologyDefaultsToTenCycles()
    {
        var canonical = Canonical(ReviewLoop());
        var topology = DynamicFlowReviewLoopTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.MaxReviewCycles ==
            DynamicFlowReviewLoopTopologyContract.DefaultMaxReviewCycles,
            "default maxReviewCycles");
        Require(
            topology.ReviewNode.NodeId == "review-step" &&
            topology.ReviewForm.FormNodeId == "review-form",
            "exact pinned logical target");
        Require(
            topology.ReviewGateway.Gateway?.Kind ==
            DynamicFlowGatewayKinds.Review &&
            topology.Payload.Edges.Count == 1,
            "review marker gateway must be acyclic");
    }

    private static void MaxReviewCyclesAcceptsOneThroughFiftyAndRejectsDrift()
    {
        foreach (var value in new[] { 1, 10, 50 })
        {
            var payload = ReviewLoop(value);
            var canonical = Canonical(payload);
            var topology = DynamicFlowReviewLoopTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
            Require(
                topology.MaxReviewCycles == value,
                $"maxReviewCycles={value}");
        }

        foreach (var value in new[] { 0, 51 })
            AssertThrows(ReviewLoop(value), $"maxReviewCycles={value} must fail");

        var decimalValue = ReviewLoop();
        decimalValue.FinalResultPolicy["maxReviewCycles"] = Json("1.5");
        AssertThrows(decimalValue, "fractional maxReviewCycles must fail");

        var stringValue = ReviewLoop();
        stringValue.FinalResultPolicy["maxReviewCycles"] = Json("\"10\"");
        AssertThrows(stringValue, "string maxReviewCycles must fail");
    }

    private static void AttemptsKeepTheLogicalTargetButNeverReuseIdentity()
    {
        const string instanceId = "100000000000000000000001";
        const string targetUnitId = "200000000000000000000001";
        var branchId =
            DynamicFlowReviewLoopTopologyContract.BuildBranchId(
                instanceId,
                1,
                targetUnitId);
        var firstStep =
            DynamicFlowReviewLoopTopologyContract.BuildStepInstanceId(
                instanceId,
                1,
                "review-step",
                branchId,
                1);
        var secondStep =
            DynamicFlowReviewLoopTopologyContract.BuildStepInstanceId(
                instanceId,
                1,
                "review-step",
                branchId,
                2);
        var firstAssignment =
            DynamicFlowReviewLoopTopologyContract.BuildAssignmentId(
                firstStep);
        var secondAssignment =
            DynamicFlowReviewLoopTopologyContract.BuildAssignmentId(
                secondStep);

        Require(firstStep != secondStep, "attempt step identity must be new");
        Require(
            firstAssignment != secondAssignment,
            "attempt assignment identity must be new");
        Require(
            branchId ==
            DynamicFlowReviewLoopTopologyContract.BuildBranchId(
                instanceId,
                1,
                targetUnitId),
            "logical target branch must remain exact");
        Require(
            DynamicFlowReviewLoopTopologyContract.BuildOwnerIdentity(
                instanceId,
                1,
                "review-step",
                branchId,
                1,
                "result") !=
            DynamicFlowReviewLoopTopologyContract.BuildOwnerIdentity(
                instanceId,
                1,
                "review-step",
                branchId,
                2,
                "result"),
            "attempt result owner identity must not be reused");
        Require(
            DynamicFlowRuntimeMaterializationOperations
                .MaterializeReviewAttemptAssignment ==
            "MATERIALIZE_REVIEW_ATTEMPT_ASSIGNMENT",
            "durable attempt materialization operation");
    }

    private static void GraphCyclesAndAdditionalTargetsFailClosed()
    {
        var cycle = ReviewLoop();
        cycle.Edges.Add(new DynamicFlowTopologyEdgeDto
        {
            TransitionId = "review-cycle",
            FromNodeId = "review-step",
            ToNodeId = "review-step"
        });
        AssertThrows(cycle, "arbitrary graph cycle must fail");

        var additional = ReviewLoop();
        additional.Nodes.Add(new DynamicFlowTopologyNodeDto
        {
            NodeId = "other-step",
            NodeCode = "OTHER",
            NodeKind = DynamicFlowNodeKinds.FormStep,
            FormNodeId = "review-form"
        });
        AssertThrows(additional, "additional logical target must fail");
    }

    private static void P6CandidateOpensT08ButKeepsT09ThroughT12Blocked()
    {
        var t08 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowReviewLoopTopologyContract.ArchetypeId);
        Require(
            t08.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-06 must open T08 on the Testing-only candidate");
        var t09 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowSubflowTopologyContract.ArchetypeId);
        Require(
            t09.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-07 must open T09 without regressing T08");
        Require(
            DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T12").Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-10 cumulative boundary opens T12");
    }

    private static DynamicFlowTemplatePayloadDto ReviewLoop(
        int? maxReviewCycles = null)
    {
        var hash = new string('a', 64);
        var payload = new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId =
                DynamicFlowReviewLoopTopologyContract.ArchetypeId,
            EntryStepId = "review-step",
            RootDynamicFormTemplateId =
                "300000000000000000000001",
            CatalogVersion = DynamicFlowP6CatalogCandidate.Version,
            CatalogSemanticHash =
                DynamicFlowP6CatalogCandidate.SemanticHash,
            FormNodes =
            [
                new DynamicFlowFormNodeDto
                {
                    FormNodeId = "review-form",
                    Role = "ROOT",
                    DynamicFormTemplateId =
                        "300000000000000000000001",
                    DynamicFormFamilyId =
                        "310000000000000000000001",
                    DynamicFormVersionNo = 1,
                    DynamicFormSchemaHash = hash,
                    DynamicFormSnapshotHash = hash
                }
            ],
            Nodes =
            [
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "review-step",
                    NodeCode = "REVIEW",
                    NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "review-form",
                    DeclaredRoles = ["OWNER"]
                },
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "review-gateway",
                    NodeCode = "REVIEW_GATE",
                    NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto
                    {
                        Kind = DynamicFlowGatewayKinds.Review,
                        ReviewRole = "REVIEWER"
                    }
                }
            ],
            Edges =
            [
                new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = "review-marker",
                    FromNodeId = "review-step",
                    ToNodeId = "review-gateway"
                }
            ]
        };
        if (maxReviewCycles.HasValue)
        {
            payload.FinalResultPolicy["maxReviewCycles"] =
                Json(maxReviewCycles.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }
        return payload;
    }

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

    private static JsonElement Json(string value)
        => JsonDocument.Parse(value).RootElement.Clone();

    private static void AssertThrows(
        DynamicFlowTemplatePayloadDto payload,
        string message)
    {
        try
        {
            var canonical = Canonical(payload);
            DynamicFlowReviewLoopTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
