using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowParallelForkTopologyContractTests
{
    public static void Run()
    {
        ExactForkKeepsDeterministicBranchesAndPins();
        ForkRuntimeIdentitiesAreStableAndIndependent();
        JoinConditionalAndChangedShapesFailClosed();
        P6CandidateOpensOnlyT04AtThisPrompt();
    }

    private static void ExactForkKeepsDeterministicBranchesAndPins()
    {
        var canonical = Canonical(Fork());
        var topology = DynamicFlowParallelForkTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);

        Require(topology.EntryNode.NodeCode == "A", "T04 entry must be A");
        Require(topology.ForkNode.NodeCode == "F", "T04 gateway must be F");
        Require(
            topology.Branches.Select(branch => branch.Node.NodeCode)
                .SequenceEqual(new[] { "B", "C" }, StringComparer.Ordinal),
            "T04 branches must have deterministic B/C order");
        Require(
            topology.Branches.All(branch => branch.Node.NodeKind == DynamicFlowNodeKinds.FormStep),
            "T04 branch targets must be form steps");
        Require(
            topology.TopologyHash == canonical.PayloadHash &&
            topology.CanonicalJson == canonical.CanonicalJson,
            "T04 must retain exact canonical topology pins");
    }

    private static void ForkRuntimeIdentitiesAreStableAndIndependent()
    {
        const string instanceId = "100000000000000000000001";
        const string targetUnitId = "200000000000000000000001";
        var root = DynamicFlowParallelForkTopologyContract.BuildRootBranchId(
            instanceId,
            1,
            targetUnitId);
        var gateway = DynamicFlowParallelForkTopologyContract.BuildGatewayInstanceId(
            instanceId,
            1,
            "fork-f",
            root);
        var branchB = DynamicFlowParallelForkTopologyContract.BuildBranchId(
            instanceId,
            1,
            gateway,
            "edge-f-b",
            targetUnitId);
        var branchC = DynamicFlowParallelForkTopologyContract.BuildBranchId(
            instanceId,
            1,
            gateway,
            "edge-f-c",
            targetUnitId);
        Require(branchB != branchC, "fork topology branches must be independent");
        Require(
            branchB == DynamicFlowParallelForkTopologyContract.BuildBranchId(
                instanceId,
                1,
                gateway,
                "edge-f-b",
                targetUnitId),
            "fork branch replay identity must be stable");
        Require(
            DynamicFlowParallelForkTopologyContract.BuildContributionId(
                gateway,
                branchB,
                "edge-f-b") !=
            DynamicFlowParallelForkTopologyContract.BuildContributionId(
                gateway,
                branchC,
                "edge-f-c"),
            "fork contributions must be independently owned");
        Require(
            DynamicFlowParallelForkTopologyContract.BuildStepOwnerIdentity(
                instanceId,
                1,
                "step-b",
                branchB,
                "result") !=
            DynamicFlowParallelForkTopologyContract.BuildStepOwnerIdentity(
                instanceId,
                1,
                "step-c",
                branchC,
                "result"),
            "result owner must remain exact step/branch scoped");
    }

    private static void JoinConditionalAndChangedShapesFailClosed()
    {
        var join = Fork();
        join.Nodes[1].Gateway!.Kind = DynamicFlowGatewayKinds.JoinAll;
        AssertThrows(join, "T04 JOIN must remain blocked until P6-03");

        var conditional = Fork();
        conditional.Edges[1].Condition = new DynamicFlowConditionDto
        {
            Operator = "EQ",
            Field = "x"
        };
        AssertThrows(conditional, "T04 conditional fan-out must fail closed");

        var thirdBranch = Fork();
        thirdBranch.Nodes.Add(Node("step-d", "D", "form-c"));
        thirdBranch.Edges.Add(Edge("edge-f-d", "fork-f", "step-d"));
        AssertThrows(thirdBranch, "T04 must retain the exact two-branch shape");
    }

    private static void P6CandidateOpensOnlyT04AtThisPrompt()
    {
        var current = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowRuntimeCatalogCandidate.Version,
            DynamicFlowRuntimeCatalogCandidate.SemanticHash,
            "FLOW-T04");
        var candidateT04 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T04");
        var candidateT05 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T05");
        Require(
            current.Eligibility == DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
            "current v1.2 must remain unchanged");
        Require(
            candidateT04.Eligibility == DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-02 must open T04 on the Testing-only candidate");
        Require(
            candidateT05.Eligibility == DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-03 must promote T05 without changing the exact T04 fork contract");
    }

    private static DynamicFlowCanonicalPayload Canonical(DynamicFlowTemplatePayloadDto payload)
        => DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            payload,
            null,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));

    private static void AssertThrows(DynamicFlowTemplatePayloadDto payload, string message)
    {
        try
        {
            var canonical = Canonical(payload);
            DynamicFlowParallelForkTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
        }
        catch
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static DynamicFlowTemplatePayloadDto Fork()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var hashC = new string('c', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId = DynamicFlowParallelForkTopologyContract.ArchetypeId,
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
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "fork-f",
                    NodeCode = "F",
                    NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto
                    {
                        Kind = DynamicFlowGatewayKinds.Fork
                    }
                },
                Node("step-b", "B", "form-b"),
                Node("step-c", "C", "form-c")
            ],
            Edges =
            [
                Edge("edge-a-f", "step-a", "fork-f"),
                Edge("edge-f-c", "fork-f", "step-c"),
                Edge("edge-f-b", "fork-f", "step-b")
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
