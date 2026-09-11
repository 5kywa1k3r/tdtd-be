using tdtd_be.Common.Capabilities;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowSequentialTopologyContractTests
{
    public static void Run()
    {
        StrictThreeNodeChainKeepsExactFormsAndEdges();
        RuntimeIdentitiesAreStableAndEpochScoped();
        ForksCyclesAndChangedPinsFailClosed();
        P6CandidateDoesNotChangeTheCurrentCatalogBoundary();
    }

    private static void StrictThreeNodeChainKeepsExactFormsAndEdges()
    {
        var canonical = Canonical(Chain());
        var topology = DynamicFlowSequentialTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);

        Require(
            topology.OrderedNodes.Select(node => node.NodeCode)
                .SequenceEqual(new[] { "A", "B", "C" }, StringComparer.Ordinal),
            "T03 must execute exactly A -> B -> C");
        Require(
            topology.OutgoingByNodeId["step-a"].ToNodeId == "step-b" &&
            topology.OutgoingByNodeId["step-b"].ToNodeId == "step-c" &&
            !topology.OutgoingByNodeId.ContainsKey("step-c"),
            "T03 edge ownership drift");
        Require(
            topology.FormsByNodeId["step-a"].FormNodeId == "form-a" &&
            topology.FormsByNodeId["step-b"].FormNodeId == "form-b" &&
            topology.FormsByNodeId["step-c"].FormNodeId == "form-c",
            "each T03 node must preserve its exact form pin");
        Require(
            topology.TopologyHash == canonical.PayloadHash &&
            topology.CanonicalJson == canonical.CanonicalJson,
            "topology must retain the immutable definition bytes and hash");
    }

    private static void RuntimeIdentitiesAreStableAndEpochScoped()
    {
        const string instanceId = "100000000000000000000001";
        const string targetUnitId = "200000000000000000000001";
        var branchEpoch1 =
            DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                instanceId,
                1,
                targetUnitId);
        var branchEpoch1Replay =
            DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                instanceId,
                1,
                targetUnitId);
        var branchEpoch2 =
            DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                instanceId,
                2,
                targetUnitId);
        Require(branchEpoch1 == branchEpoch1Replay, "branch replay identity must be stable");
        Require(branchEpoch1 != branchEpoch2, "branch identity must be execution-epoch scoped");

        var stepA = DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
            instanceId,
            1,
            "step-a",
            branchEpoch1,
            1);
        var stepB = DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
            instanceId,
            1,
            "step-b",
            branchEpoch1,
            1);
        Require(stepA != stepB, "logical nodes must have distinct step identities");
        Require(
            stepA ==
            DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
                instanceId,
                1,
                "step-a",
                branchEpoch1,
                1),
            "step replay identity must be stable");
        Require(
            DynamicFlowSequentialTopologyContract.BuildAssignmentId(stepA) !=
            DynamicFlowSequentialTopologyContract.BuildAssignmentId(stepB),
            "assignments must be owned by exact step identities");
        var otherTargetBranch =
            DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                instanceId,
                1,
                "200000000000000000000002");
        Require(
            DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                instanceId,
                1,
                "step-c",
                branchEpoch1,
                "result") !=
            DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                instanceId,
                1,
                "step-c",
                otherTargetBranch,
                "result"),
            "multi-target result ownership must remain branch/step scoped");
        Require(
            DynamicFlowSequentialTopologyContract.BuildForwardReceiptId(
                instanceId,
                "forward-a-b") ==
            DynamicFlowSequentialTopologyContract.BuildForwardReceiptId(
                instanceId,
                "forward-a-b"),
            "forward receipt replay identity must be stable");
    }

    private static void ForksCyclesAndChangedPinsFailClosed()
    {
        var fork = Chain();
        fork.Edges[1] = new DynamicFlowTopologyEdgeDto
        {
            TransitionId = "edge-a-c",
            FromNodeId = "step-a",
            ToNodeId = "step-c"
        };
        var forkCanonical = Canonical(fork);
        AssertThrows(
            () => DynamicFlowSequentialTopologyContract.Require(
                forkCanonical.CanonicalJson,
                forkCanonical.PayloadHash),
            "T03 fork must fail closed");

        var chainCanonical = Canonical(Chain());
        AssertThrows(
            () => DynamicFlowSequentialTopologyContract.Require(
                chainCanonical.CanonicalJson,
                new string('f', 64)),
            "changed topology pin must fail closed");

        var wrongShape = Chain();
        wrongShape.Nodes[2].NodeCode = "D";
        var wrongShapeCanonical = Canonical(wrongShape);
        AssertThrows(
            () => DynamicFlowSequentialTopologyContract.Require(
                wrongShapeCanonical.CanonicalJson,
                wrongShapeCanonical.PayloadHash),
            "T03 must retain the frozen A -> B -> C logical shape");
    }

    private static void P6CandidateDoesNotChangeTheCurrentCatalogBoundary()
    {
        var currentT03 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowRuntimeCatalogCandidate.Version,
            DynamicFlowRuntimeCatalogCandidate.SemanticHash,
            "FLOW-T03");
        var candidateT03 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T03");
        var candidateT04 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T04");

        Require(
            currentT03.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
            "current v1.2 must not activate T03");
        Require(
            candidateT03.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "explicit v1.3 candidate must expose T03 to the Testing gate");
        Require(
            candidateT04.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "T04 must be open after P6-02");
        Require(
            DynamicFlowP6CatalogCandidate.ActivationEnabled ==
            (DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion ==
                 DynamicFlowP6CatalogCandidate.Version &&
             DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256 ==
                 DynamicFlowP6CatalogCandidate.SemanticHash),
            "v1.3 activation must follow only the exact generated CURRENT pin");
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

    private static DynamicFlowTemplatePayloadDto Chain()
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var hashC = new string('c', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId = "FLOW-T03",
            EntryStepId = "step-a",
            RootDynamicFormTemplateId = "300000000000000000000001",
            CatalogVersion = DynamicFlowP6CatalogCandidate.Version,
            CatalogSemanticHash = DynamicFlowP6CatalogCandidate.SemanticHash,
            FormNodes =
            [
                Form(
                    "form-a",
                    "ROOT",
                    "300000000000000000000001",
                    "310000000000000000000001",
                    hashA),
                Form(
                    "form-b",
                    "STEP",
                    "300000000000000000000002",
                    "310000000000000000000002",
                    hashB),
                Form(
                    "form-c",
                    "STEP",
                    "300000000000000000000003",
                    "310000000000000000000003",
                    hashC)
            ],
            Nodes =
            [
                Node("step-a", "A", "form-a"),
                Node("step-b", "B", "form-b"),
                Node("step-c", "C", "form-c")
            ],
            Edges =
            [
                new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = "edge-a-b",
                    FromNodeId = "step-a",
                    ToNodeId = "step-b"
                },
                new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = "edge-b-c",
                    FromNodeId = "step-b",
                    ToNodeId = "step-c"
                }
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

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
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
