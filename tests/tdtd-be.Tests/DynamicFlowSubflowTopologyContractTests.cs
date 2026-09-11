using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowSubflowTopologyContractTests
{
    public static void Run()
    {
        ExactPinnedBlockingTopologyIsRequired();
        ParentChildLineageIsFrozenAndDeterministic();
        DepthCycleAndPinDriftFailClosed();
        P6CandidateOpensOnlyT09AtThisBoundary();
    }

    private static void ExactPinnedBlockingTopologyIsRequired()
    {
        var canonical = Canonical(Subflow());
        var topology = DynamicFlowSubflowTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.ParentNode.NodeId == "parent-step" &&
            topology.SubflowGateway.Gateway?.Kind ==
                DynamicFlowGatewayKinds.Subflow,
            "exact parent and SUBFLOW gateway");
        Require(
            topology.ChildFlowFamilyId ==
                "400000000000000000000001" &&
            topology.ChildFlowVersionId ==
                "500000000000000000000001",
            "child family and version must be exact pins");

        var missingVersion = Subflow();
        missingVersion.Nodes[1].Gateway!.SubflowVersionId = null;
        AssertThrows(missingVersion, "latest child version must fail closed");

        var outgoing = Subflow();
        outgoing.Edges.Add(new DynamicFlowTopologyEdgeDto
        {
            TransitionId = "after-child",
            FromNodeId = "subflow-gateway",
            ToNodeId = "parent-step"
        });
        AssertThrows(outgoing, "subflow gateway must remain terminal and blocking");
    }

    private static void ParentChildLineageIsFrozenAndDeterministic()
    {
        var canonical = Canonical(Subflow());
        var topology = DynamicFlowSubflowTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        var parent = Parent();
        var step = ParentStep(parent.Id);
        var context = DynamicFlowSubflowTopologyContract.BuildLaunchContext(
            parent,
            step,
            topology,
            topology.ChildFlowFamilyId,
            topology.ChildFlowVersionId);
        Require(
            context.ParentInstanceId == parent.Id &&
            context.ParentStepInstanceId == step.Id &&
            context.RootInstanceId == parent.Id &&
            context.AncestryPath.SequenceEqual(new[] { parent.Id }) &&
            context.AncestryFlowFamilyIds.SequenceEqual(
                new[] { parent.FlowTemplateId }),
            "parent/root/ancestry lineage");

        var childA = DynamicFlowSubflowTopologyContract.BuildChildInstanceId(
            parent.WorkId,
            topology.ChildFlowVersionId,
            "p607-command");
        var childB = DynamicFlowSubflowTopologyContract.BuildChildInstanceId(
            parent.WorkId,
            topology.ChildFlowVersionId,
            "p607-command");
        Require(childA == childB, "replay must reuse child identity");
        Require(
            childA !=
            DynamicFlowSubflowTopologyContract.BuildChildInstanceId(
                parent.WorkId,
                topology.ChildFlowVersionId,
                "p607-command-other"),
            "a distinct command must not reuse child identity");
    }

    private static void DepthCycleAndPinDriftFailClosed()
    {
        var canonical = Canonical(Subflow());
        var topology = DynamicFlowSubflowTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        var parent = Parent();
        var step = ParentStep(parent.Id);

        AssertLaunchThrows(
            parent,
            step,
            topology,
            topology.ChildFlowFamilyId,
            "500000000000000000000099",
            DynamicFlowSubflowTopologyContract.ChildPinDrift);

        parent.AncestryPath = Enumerable.Range(
                1,
                DynamicFlowSubflowTopologyContract.MaxDepth)
            .Select(index => $"600000000000000000{index:000}")
            .ToList();
        parent.AncestryFlowFamilyIds = Enumerable.Range(
                1,
                DynamicFlowSubflowTopologyContract.MaxDepth)
            .Select(index => $"700000000000000000{index:000}")
            .ToList();
        AssertLaunchThrows(
            parent,
            step,
            topology,
            topology.ChildFlowFamilyId,
            topology.ChildFlowVersionId,
            DynamicFlowSubflowTopologyContract.DepthExceeded);

        parent = Parent();
        parent.AncestryPath = ["600000000000000000000001"];
        parent.AncestryFlowFamilyIds = [topology.ChildFlowFamilyId];
        AssertLaunchThrows(
            parent,
            step,
            topology,
            topology.ChildFlowFamilyId,
            topology.ChildFlowVersionId,
            DynamicFlowSubflowTopologyContract.AncestryCycle);
    }

    private static void P6CandidateOpensOnlyT09AtThisBoundary()
    {
        var t09 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowSubflowTopologyContract.ArchetypeId);
        Require(
            t09.Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-07 must open T09 on the Testing-only candidate");
        Require(
            DynamicFlowRuntimeEligibilityPolicy.Evaluate(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T12").Eligibility ==
            DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-10 cumulative boundary opens T12");
    }

    private static DynamicFlowTemplatePayloadDto Subflow()
    {
        var hash = new string('a', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId = DynamicFlowSubflowTopologyContract.ArchetypeId,
            EntryStepId = "parent-step",
            RootDynamicFormTemplateId = "300000000000000000000001",
            CatalogVersion = DynamicFlowP6CatalogCandidate.Version,
            CatalogSemanticHash = DynamicFlowP6CatalogCandidate.SemanticHash,
            FormNodes =
            [
                new DynamicFlowFormNodeDto
                {
                    FormNodeId = "parent-form",
                    Role = "ROOT",
                    DynamicFormTemplateId = "300000000000000000000001",
                    DynamicFormFamilyId = "310000000000000000000001",
                    DynamicFormVersionNo = 1,
                    DynamicFormSchemaHash = hash,
                    DynamicFormSnapshotHash = hash
                }
            ],
            Nodes =
            [
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "parent-step",
                    NodeCode = "PARENT",
                    NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "parent-form",
                    DeclaredRoles = ["OWNER"]
                },
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "subflow-gateway",
                    NodeCode = "CHILD",
                    NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto
                    {
                        Kind = DynamicFlowGatewayKinds.Subflow,
                        SubflowFamilyId = "400000000000000000000001",
                        SubflowVersionId = "500000000000000000000001"
                    }
                }
            ],
            Edges =
            [
                new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = "launch-child",
                    FromNodeId = "parent-step",
                    ToNodeId = "subflow-gateway"
                }
            ]
        };
    }

    private static DynamicFlowInstance Parent()
        => new()
        {
            Id = "100000000000000000000001",
            WorkId = "200000000000000000000001",
            FlowTemplateId = "300000000000000000000009",
            Revision = 7,
            NextEventSequence = 11
        };

    private static DynamicFlowStepInstance ParentStep(string parentId)
        => new()
        {
            Id = "100000000000000000000002",
            FlowInstanceId = parentId,
            Revision = 3
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
            DynamicFlowSubflowTopologyContract.Require(
                canonical.CanonicalJson,
                canonical.PayloadHash);
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void AssertLaunchThrows(
        DynamicFlowInstance parent,
        DynamicFlowStepInstance step,
        DynamicFlowSubflowTopology topology,
        string familyId,
        string versionId,
        string expected)
    {
        try
        {
            DynamicFlowSubflowTopologyContract.BuildLaunchContext(
                parent,
                step,
                topology,
                familyId,
                versionId);
        }
        catch (InvalidOperationException error)
            when (error.Message == expected)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected {expected} to fail closed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
