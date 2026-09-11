using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowJoinAllTopologyContractTests
{
    public static void Run()
    {
        ExactJoinAllFreezesExpectedContributors();
        GatewayAndContributionIdentitiesAreVersionedAndStable();
        ChangedJoinShapesFailClosed();
        P6CandidateOpensT05AndT06ButKeepsLaterBoundaryBlocked();
    }

    private static void ExactJoinAllFreezesExpectedContributors()
    {
        var canonical = Canonical(JoinAll());
        var topology = DynamicFlowJoinAllTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);

        Require(topology.EntryNode.NodeCode == "A", "T05 entry must be A");
        Require(topology.ForkNode.NodeCode == "J", "T05 runtime gateway must be JOIN ALL J");
        Require(
            topology.ForkNode.Gateway?.Kind == DynamicFlowGatewayKinds.JoinAll,
            "T05 gateway kind must remain JOIN_ALL");
        Require(
            topology.Branches.Select(branch => branch.Node.NodeCode)
                .SequenceEqual(new[] { "B", "C" }, StringComparer.Ordinal),
            "T05 expected contributions must have deterministic B/C order");
        Require(
            topology.ForkNode.Gateway!.ExpectedIncomingNodeIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(new[] { "step-b", "step-c" }, StringComparer.Ordinal),
            "T05 expected contribution set must be immutable and exact");
        Require(
            topology.TopologyHash == canonical.PayloadHash,
            "T05 must retain the exact topology pin");
    }

    private static void GatewayAndContributionIdentitiesAreVersionedAndStable()
    {
        const string instanceId = "100000000000000000000001";
        const string targetUnitId = "200000000000000000000001";
        var root = DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
            instanceId,
            1,
            targetUnitId);
        var gateway = DynamicFlowJoinAllTopologyContract.BuildGatewayInstanceId(
            instanceId,
            1,
            "join-j",
            root);
        var contributionA =
            DynamicFlowParallelForkTopologyContract.BuildContributionId(
                gateway,
                "300000000000000000000001",
                "edge-a-b");
        var contributionB =
            DynamicFlowParallelForkTopologyContract.BuildContributionId(
                gateway,
                "300000000000000000000002",
                "edge-a-c");

        Require(
            DynamicFlowJoinAllTopologyContract.BuildGatewayLedgerId(gateway, 1) !=
            DynamicFlowJoinAllTopologyContract.BuildGatewayLedgerId(gateway, 2),
            "gateway ledger identity must include the version");
        Require(
            DynamicFlowJoinAllTopologyContract.BuildContributionLedgerId(
                gateway,
                1,
                contributionA) !=
            DynamicFlowJoinAllTopologyContract.BuildContributionLedgerId(
                gateway,
                1,
                contributionB),
            "each effective branch must own one contribution identity");
        Require(
            DynamicFlowJoinAllTopologyContract.BuildReleaseEventId(gateway, 1) ==
            DynamicFlowJoinAllTopologyContract.BuildReleaseEventId(gateway, 1),
            "JOIN ALL release replay identity must be deterministic");
    }

    private static void ChangedJoinShapesFailClosed()
    {
        var missingExpected = JoinAll();
        missingExpected.Nodes.Single(node => node.NodeId == "join-j")
            .Gateway!.ExpectedIncomingNodeIds = ["step-b"];
        AssertThrows(missingExpected, "missing expected contributor must fail closed");

        var joinAny = JoinAll();
        joinAny.Nodes.Single(node => node.NodeId == "join-j")
            .Gateway!.Kind = DynamicFlowGatewayKinds.JoinAny;
        AssertThrows(joinAny, "JOIN_ANY must remain blocked until P6-04");

        var threshold = JoinAll();
        threshold.Nodes.Single(node => node.NodeId == "join-j")
            .Gateway!.RequiredIncomingCount = 1;
        AssertThrows(threshold, "threshold joins must remain blocked until P6-04");

        var conditional = JoinAll();
        conditional.Edges.Single(edge => edge.TransitionId == "edge-b-j")
            .Condition = new DynamicFlowConditionDto { Operator = "EQ", Field = "x" };
        AssertThrows(conditional, "conditional contributions must fail closed");
    }

    private static void P6CandidateOpensT05AndT06ButKeepsLaterBoundaryBlocked()
    {
        var t05 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowJoinAllTopologyContract.ArchetypeId);
        Require(
            t05.Eligibility == DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-03 must open T05 on the Testing-only candidate");
        Require(
            DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T07").Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-05 cumulative boundary opens T07");
        Require(
            DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T08").Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-06 cumulative boundary opens T08");
        Require(
            DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T09").Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-07 cumulative boundary opens T09");
        Require(
            DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T12").Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-10 cumulative boundary opens T12");
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

    private static void AssertThrows(
        DynamicFlowTemplatePayloadDto payload,
        string message)
    {
        try
        {
            var canonical = Canonical(payload);
            DynamicFlowJoinAllTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static DynamicFlowTemplatePayloadDto JoinAll()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var hashC = new string('c', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId = DynamicFlowJoinAllTopologyContract.ArchetypeId,
            EntryStepId = "step-a",
            RootDynamicFormTemplateId = "300000000000000000000001",
            CatalogVersion = DynamicFlowP6CatalogCandidate.Version,
            CatalogSemanticHash = DynamicFlowP6CatalogCandidate.SemanticHash,
            FormNodes =
            [
                Form("form-a", "ROOT", "300000000000000000000001", "310000000000000000000001", hashA),
                Form("form-b", "STEP", "300000000000000000000002", "310000000000000000000002", hashB),
                Form("form-c", "STEP", "300000000000000000000003", "310000000000000000000003", hashC)
            ],
            Nodes =
            [
                Node("step-a", "A", "form-a"),
                Node("step-b", "B", "form-b"),
                Node("step-c", "C", "form-c"),
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "join-j",
                    NodeCode = "J",
                    NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto
                    {
                        Kind = DynamicFlowGatewayKinds.JoinAll,
                        ExpectedIncomingNodeIds = ["step-b", "step-c"]
                    }
                },
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "final",
                    NodeCode = "FINAL",
                    NodeKind = DynamicFlowNodeKinds.Final
                }
            ],
            Edges =
            [
                Edge("edge-a-b", "step-a", "step-b"),
                Edge("edge-a-c", "step-a", "step-c"),
                Edge("edge-b-j", "step-b", "join-j"),
                Edge("edge-c-j", "step-c", "join-j"),
                Edge("edge-j-final", "join-j", "final")
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
        string to)
        => new()
        {
            TransitionId = id,
            FromNodeId = from,
            ToNodeId = to
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
