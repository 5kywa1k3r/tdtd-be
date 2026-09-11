using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowFinalizeTopologyContractTests
{
    public static void Run()
    {
        ExactTopologyAndPinsAreFrozen();
        EpochIdentitiesAreDeterministicAndIsolated();
        CommandsClosureReplayAndP8BarrierAreReachable();
        CandidateBoundaryEndsAtT12();
    }

    private static void ExactTopologyAndPinsAreFrozen()
    {
        var canonical = Canonical(Finalize());
        var topology = DynamicFlowFinalizeTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.EntryNode.NodeId == "entry" &&
            topology.GatewayNode.NodeId == "epoch-gateway" &&
            topology.FinalNode.NodeId == "final" &&
            topology.RollbackTargetNodeId == "entry" &&
            topology.EntryForm.FormNodeId == "entry-form",
            "T12 exact entry/gateway/final topology drifted");

        var wrongTarget = Finalize();
        wrongTarget.Nodes.Single(node =>
                node.NodeId == "epoch-gateway")
            .Gateway!.RollbackTargetNodeId = "final";
        AssertThrows(
            wrongTarget,
            "rollback checkpoint outside the pinned ancestor must fail closed");
    }

    private static void EpochIdentitiesAreDeterministicAndIsolated()
    {
        const string instanceId = "100000000000000000000001";
        const string unitId = "200000000000000000000001";
        var branch1 = DynamicFlowFinalizeTopologyContract.BuildBranchId(
            instanceId,
            1,
            unitId);
        var step1 = DynamicFlowFinalizeTopologyContract.BuildStepInstanceId(
            instanceId,
            1,
            "entry",
            branch1);
        var replayStep1 =
            DynamicFlowFinalizeTopologyContract.BuildStepInstanceId(
                instanceId,
                1,
                "entry",
                branch1);
        var branch2 = DynamicFlowFinalizeTopologyContract.BuildBranchId(
            instanceId,
            2,
            unitId);
        var step2 = DynamicFlowFinalizeTopologyContract.BuildStepInstanceId(
            instanceId,
            2,
            "entry",
            branch2);
        Require(
            step1 == replayStep1 &&
            step1 != step2 &&
            branch1 != branch2 &&
            DynamicFlowFinalizeTopologyContract.BuildAssignmentId(step1) !=
            DynamicFlowFinalizeTopologyContract.BuildAssignmentId(step2) &&
            DynamicFlowFinalizeTopologyContract.BuildEpochId(instanceId, 1) !=
            DynamicFlowFinalizeTopologyContract.BuildEpochId(instanceId, 2),
            "replacement epoch identities must be replay-stable and epoch-isolated");
    }

    private static void CommandsClosureReplayAndP8BarrierAreReachable()
    {
        var root = SourceRoot();
        var service = File.ReadAllText(Path.Combine(
            root,
            "Services",
            "DynamicFlows",
            "DynamicFlowRuntimeEpochCommands.cs"));
        var read = File.ReadAllText(Path.Combine(
            root,
            "Services",
            "DynamicFlows",
            "DynamicFlowRuntimeReadService.cs"));
        var stateProjection = File.ReadAllText(Path.Combine(
            root,
            "Services",
            "DynamicFlows",
            "DynamicFlowRuntimeStateProjection.cs"));
        var controller = File.ReadAllText(Path.Combine(
            root,
            "Controllers",
            "DynamicFlowRuntimeController.cs"));
        var indexes = File.ReadAllText(Path.Combine(
            root,
            "Data",
            "Indexes",
            "MongoIndexInitializer.cs"));

        Require(
            service.Contains("FinalizeEpochAsync", StringComparison.Ordinal) &&
            service.Contains("RollbackEpochAsync", StringComparison.Ordinal) &&
            service.Contains("TerminateEpochAsync", StringComparison.Ordinal) &&
            service.Contains("RestartEpochAsync", StringComparison.Ordinal) &&
            service.Contains("RequireEpochReplay", StringComparison.Ordinal) &&
            service.Contains("ExpectedInstanceRevision", StringComparison.Ordinal) &&
            service.Contains(
                "instance.State != DynamicFlowInstanceStates.Completed",
                StringComparison.Ordinal) &&
            service.Contains(
                "DYNAMIC_FLOW_FINALIZE_PRECONDITION_NOT_MET",
                StringComparison.Ordinal),
            "T12 commands must retain typed routes, exact replay, and revision CAS");
        Require(
            service.Contains("InvalidateEpochClosureAsync", StringComparison.Ordinal) &&
            service.Contains("InvalidateStatisticRowsAsync", StringComparison.Ordinal) &&
            service.Contains("IsCanonicalEpoch, false", StringComparison.Ordinal) &&
            service.Contains("p8ExecutionEnabled\", false", StringComparison.Ordinal) &&
            service.Contains(
                "RebuildIntentOperation",
                StringComparison.Ordinal),
            "step/assignment/report/stat invalidation and no-P8 rebuild intent drifted");
        Require(
            stateProjection.Contains(
                "DynamicFlowFinalizeTopologyContract",
                StringComparison.Ordinal) &&
            stateProjection.Contains(
                "candidate.ExecutionEpoch ==",
                StringComparison.Ordinal) &&
            stateProjection.Contains(
                "candidate.IsCanonicalEpoch != false",
                StringComparison.Ordinal) &&
            stateProjection.Contains(
                "\"FLOW-T12\"",
                StringComparison.Ordinal),
            "T12 completion must remain scoped to the current canonical epoch");
        Require(
            controller.Contains("/finalize", StringComparison.Ordinal) &&
            controller.Contains("/rollback", StringComparison.Ordinal) &&
            controller.Contains("/terminate", StringComparison.Ordinal) &&
            controller.Contains("/restart", StringComparison.Ordinal) &&
            read.Contains("CanRollback", StringComparison.Ordinal) &&
            read.Contains("CanTerminate", StringComparison.Ordinal) &&
            read.Contains("CanRestart", StringComparison.Ordinal) &&
            read.Contains(
                "DynamicFlowFinalizeTopologyContract.FinalizedEvent",
                StringComparison.Ordinal) &&
            read.Contains(
                "DynamicFlowFinalizeTopologyContract.RolledBackEvent",
                StringComparison.Ordinal) &&
            read.Contains(
                "DynamicFlowFinalizeTopologyContract.TerminatedEvent",
                StringComparison.Ordinal) &&
            read.Contains(
                "DynamicFlowFinalizeTopologyContract.RestartedEvent",
                StringComparison.Ordinal),
            "T12 route/read/capability surface drifted");
        Require(
            indexes.Contains(
                "ux_dynamicFlowExecutionEpochs_instance_epoch",
                StringComparison.Ordinal),
            "execution epoch uniqueness index drifted");
    }

    private static void CandidateBoundaryEndsAtT12()
    {
        var t12 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowFinalizeTopologyContract.ArchetypeId);
        var future = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T13");
        Require(
            t12.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate &&
            future.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
            "P6 candidate must open exactly through T12 and keep future scope blocked");
    }

    private static DynamicFlowTemplatePayloadDto Finalize()
    {
        var hash = new string('c', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId =
                DynamicFlowFinalizeTopologyContract.ArchetypeId,
            EntryStepId = "entry",
            RootDynamicFormTemplateId =
                "300000000000000000000012",
            ResultOwnerStepId = "entry",
            ResultOwnerFormNodeId = "entry-form",
            StatisticsOwnerStepId = "entry",
            StatisticsOwnerFormNodeId = "entry-form",
            CatalogVersion = DynamicFlowP6CatalogCandidate.Version,
            CatalogSemanticHash =
                DynamicFlowP6CatalogCandidate.SemanticHash,
            FormNodes =
            [
                new DynamicFlowFormNodeDto
                {
                    FormNodeId = "entry-form",
                    Role = "ROOT",
                    DynamicFormTemplateId =
                        "300000000000000000000012",
                    DynamicFormFamilyId =
                        "310000000000000000000012",
                    DynamicFormVersionNo = 1,
                    DynamicFormSchemaHash = hash,
                    DynamicFormSnapshotHash = hash
                }
            ],
            Nodes =
            [
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "entry",
                    NodeCode = "ENTRY",
                    NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "entry-form",
                    DeclaredRoles = ["ASSIGNEE"]
                },
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "epoch-gateway",
                    NodeCode = "EPOCH_GATEWAY",
                    NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto
                    {
                        Kind = DynamicFlowGatewayKinds.RollbackFinalize,
                        RollbackTargetNodeId = "entry"
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
                new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = "entry-to-gateway",
                    FromNodeId = "entry",
                    ToNodeId = "epoch-gateway"
                },
                new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = "gateway-to-final",
                    FromNodeId = "epoch-gateway",
                    ToNodeId = "final"
                }
            ]
        };
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

    private static string SourceRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null &&
               !Directory.Exists(Path.Combine(root, "Services")))
        {
            root = Directory.GetParent(root)?.FullName;
        }
        return root ?? throw new InvalidOperationException(
            "backend source root");
    }

    private static void AssertThrows(
        DynamicFlowTemplatePayloadDto payload,
        string message)
    {
        try
        {
            var canonical = Canonical(payload);
            _ = DynamicFlowFinalizeTopologyContract.Require(
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
