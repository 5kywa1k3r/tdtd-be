using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowJoinQuorumTopologyContractTests
{
    public static void Run()
    {
        AnyAndNOfMFreezeExactQuorum();
        InvalidThresholdsAndContributorDriftFailClosed();
        QuorumAndLateIdentitiesAreStable();
        ImpossibleQuorumPolicyIsDeterministic();
        P6CandidateOpensT06ButKeepsLaterBoundaryBlocked();
    }

    private static void AnyAndNOfMFreezeExactQuorum()
    {
        var anyCanonical = Canonical(JoinQuorum(
            DynamicFlowGatewayKinds.JoinAny,
            null));
        var any = DynamicFlowJoinQuorumTopologyContract.Require(
            anyCanonical.CanonicalJson,
            anyCanonical.PayloadHash);
        Require(
            DynamicFlowJoinQuorumTopologyContract
                .ResolveRequiredContributionCount(
                    any.ForkNode.Gateway!) == 1,
            "JOIN_ANY must freeze a one-of-two threshold");
        Require(
            any.ForkNode.Gateway!.ExpectedIncomingNodeIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    new[] { "step-b", "step-c" },
                    StringComparer.Ordinal),
            "JOIN_ANY must freeze the exact immutable contributor set");

        var nOfMCanonical = Canonical(JoinQuorum(
            DynamicFlowGatewayKinds.JoinNOfM,
            2));
        var nOfM = DynamicFlowJoinQuorumTopologyContract.Require(
            nOfMCanonical.CanonicalJson,
            nOfMCanonical.PayloadHash);
        Require(
            DynamicFlowJoinQuorumTopologyContract
                .ResolveRequiredContributionCount(
                    nOfM.ForkNode.Gateway!) == 2,
            "JOIN_N_OF_M must retain its explicit threshold");
    }

    private static void InvalidThresholdsAndContributorDriftFailClosed()
    {
        AssertThrows(
            JoinQuorum(DynamicFlowGatewayKinds.JoinNOfM, 0),
            "zero quorum must fail closed");
        AssertThrows(
            JoinQuorum(DynamicFlowGatewayKinds.JoinNOfM, 3),
            "N greater than M must fail closed");
        AssertThrows(
            JoinQuorum(DynamicFlowGatewayKinds.JoinAny, 2),
            "JOIN_ANY cannot declare a threshold greater than one");

        var missing = JoinQuorum(
            DynamicFlowGatewayKinds.JoinNOfM,
            1);
        missing.Nodes.Single(node => node.NodeId == "join-j")
            .Gateway!.ExpectedIncomingNodeIds = ["step-b"];
        AssertThrows(
            missing,
            "changed contributor set must fail closed");

        var joinAll = JoinQuorum(
            DynamicFlowGatewayKinds.JoinNOfM,
            1);
        joinAll.Nodes.Single(node => node.NodeId == "join-j")
            .Gateway!.Kind = DynamicFlowGatewayKinds.JoinAll;
        AssertThrows(
            joinAll,
            "JOIN_ALL remains owned by P6-03");
    }

    private static void QuorumAndLateIdentitiesAreStable()
    {
        const string gatewayId = "100000000000000000000001";
        const string contributionId = "200000000000000000000001";
        Require(
            DynamicFlowJoinQuorumTopologyContract.BuildReleaseEventId(
                gatewayId,
                1) ==
            DynamicFlowJoinQuorumTopologyContract.BuildReleaseEventId(
                gatewayId,
                1),
            "quorum winner release identity must be replay stable");
        Require(
            DynamicFlowJoinQuorumTopologyContract.BuildLateEventId(
                gatewayId,
                1,
                contributionId) ==
            DynamicFlowJoinQuorumTopologyContract.BuildLateEventId(
                gatewayId,
                1,
                contributionId),
            "late ignored audit identity must be replay stable");
        Require(
            DynamicFlowJoinQuorumTopologyContract.BuildLateEventId(
                gatewayId,
                1,
                contributionId) !=
            DynamicFlowJoinQuorumTopologyContract.BuildContributionEventId(
                gatewayId,
                1,
                contributionId),
            "late and effective contribution ledgers must not alias");
    }

    private static void ImpossibleQuorumPolicyIsDeterministic()
    {
        Require(
            !DynamicFlowJoinQuorumTopologyContract.IsQuorumImpossible(
                2,
                new[] { "a" },
                new[] { "b" }),
            "one arrived plus one possible contribution can still reach two");
        Require(
            DynamicFlowJoinQuorumTopologyContract.IsQuorumImpossible(
                2,
                new[] { "a" },
                Array.Empty<string>()),
            "one arrived with no remaining contributor cannot reach two");
        Require(
            !DynamicFlowJoinQuorumTopologyContract.IsQuorumImpossible(
                1,
                Array.Empty<string>(),
                new[] { "b" }),
            "ANY remains possible while one contributor is effective");
    }

    private static void P6CandidateOpensT06ButKeepsLaterBoundaryBlocked()
    {
        var t06 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowJoinQuorumTopologyContract.ArchetypeId);
        Require(
            t06.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-04 must open T06 on the Testing-only candidate");
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
            DynamicFlowJoinQuorumTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static DynamicFlowTemplatePayloadDto JoinQuorum(
        string kind,
        int? required)
    {
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var hashC = new string('c', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId =
                DynamicFlowJoinQuorumTopologyContract.ArchetypeId,
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
                Node("step-b", "B", "form-b"),
                Node("step-c", "C", "form-c"),
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "join-j",
                    NodeCode = "J",
                    NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto
                    {
                        Kind = kind,
                        RequiredIncomingCount = required,
                        ExpectedIncomingNodeIds =
                            ["step-b", "step-c"]
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
