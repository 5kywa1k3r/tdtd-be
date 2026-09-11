using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowSupplementalTopologyContractTests
{
    public static void Run()
    {
        ExactAllowListAndIdentityAreFrozen();
        RequiredAndOptionalCompletionPoliciesAreExact();
        CapReceiptAuditAndCandidateBoundaryAreFrozen();
    }

    private static void ExactAllowListAndIdentityAreFrozen()
    {
        var canonical = Canonical(Supplemental());
        var topology = DynamicFlowSupplementalTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.NodesByFormNodeId["entry-form"].NodeId == "entry" &&
            topology.FormsByFormNodeId["entry-form"]
                .DynamicFormVersionNo == 1,
            "node/form allow-list must come from the exact locked definition");

        var a =
            DynamicFlowSupplementalTopologyContract.BuildSupplementalStepId(
                "100000000000000000000001",
                3,
                "command-a");
        var b =
            DynamicFlowSupplementalTopologyContract.BuildSupplementalStepId(
                "100000000000000000000001",
                3,
                "command-a");
        var nextEpoch =
            DynamicFlowSupplementalTopologyContract.BuildSupplementalStepId(
                "100000000000000000000001",
                4,
                "command-a");
        Require(
            a == b &&
            a != nextEpoch &&
            DynamicFlowSupplementalTopologyContract.BuildAssignmentId(a) ==
            DynamicFlowSupplementalTopologyContract.BuildAssignmentId(b),
            "supplemental identity must be replay-stable and epoch-bound");

        var missingRole = Supplemental();
        missingRole.Nodes[0].DeclaredRoles.Clear();
        AssertThrows(missingRole, "blank actor allow-list must fail closed");
    }

    private static void RequiredAndOptionalCompletionPoliciesAreExact()
    {
        var canonical = Step("canonical", false, true, DynamicFlowStepStates.Completed);
        var required = Step("required", true, true, DynamicFlowStepStates.Assigned);
        var optional = Step("optional", true, false, DynamicFlowStepStates.Assigned);
        Require(
            !DynamicFlowSupplementalTopologyContract.CompletionSatisfied(
                [canonical, required, optional]),
            "active required supplemental step must block completion");
        Require(
            DynamicFlowSupplementalTopologyContract.CompletionSatisfied(
                [canonical, optional]),
            "active optional supplemental step must not change canonical gate");
        required.State = DynamicFlowStepStates.CancelledByGateway;
        Require(
            DynamicFlowSupplementalTopologyContract.CompletionSatisfied(
                [canonical, required, optional]),
            "cancelled required supplemental step must release completion gate");
    }

    private static void CapReceiptAuditAndCandidateBoundaryAreFrozen()
    {
        Require(
            DynamicFlowSupplementalTopologyContract.MaxStepsPerEpoch == 50,
            "per-epoch cap");
        var t11 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowSupplementalTopologyContract.ArchetypeId);
        var t12 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T12");
        Require(
            t11.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate &&
            t12.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-10 must preserve T11 and open the final owned T12 boundary");

        var root = AppContext.BaseDirectory;
        while (root is not null &&
               !Directory.Exists(Path.Combine(root, "Services")))
        {
            root = Directory.GetParent(root)?.FullName;
        }
        Require(root is not null, "backend source root");
        var service = File.ReadAllText(Path.Combine(
            root!,
            "Services",
            "DynamicFlows",
            "DynamicFlowRuntimeService.cs"));
        var index = File.ReadAllText(Path.Combine(
            root!,
            "Data",
            "Indexes",
            "MongoIndexInitializer.cs"));
        Require(
            service.Contains(
                "DynamicFlowSupplementalTopologyContract.AddCommand",
                StringComparison.Ordinal) &&
            service.Contains(
                "DynamicFlowSupplementalTopologyContract.CancelCommand",
                StringComparison.Ordinal) &&
            service.Contains(
                "DynamicFlowSupplementalTopologyContract.AddedEvent",
                StringComparison.Ordinal) &&
            service.Contains(
                "DynamicFlowSupplementalTopologyContract.CancelledEvent",
                StringComparison.Ordinal),
            "add/cancel receipt and timeline audit");
        Require(
            index.Contains(
                "ux_dynamicFlowStepInstances_supplemental_identity",
                StringComparison.Ordinal),
            "unique supplemental identity index");
    }

    private static DynamicFlowStepInstance Step(
        string id,
        bool supplemental,
        bool required,
        string state)
        => new()
        {
            Id = id,
            IsSupplemental = supplemental,
            CompletionRequired = required,
            State = state
        };

    private static DynamicFlowTemplatePayloadDto Supplemental()
    {
        var hash = new string('b', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId =
                DynamicFlowSupplementalTopologyContract.ArchetypeId,
            EntryStepId = "entry",
            RootDynamicFormTemplateId =
                "300000000000000000000011",
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
                        "300000000000000000000011",
                    DynamicFormFamilyId =
                        "310000000000000000000011",
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

    private static void AssertThrows(
        DynamicFlowTemplatePayloadDto payload,
        string message)
    {
        try
        {
            var canonical = Canonical(payload);
            _ = DynamicFlowSupplementalTopologyContract.Require(
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
