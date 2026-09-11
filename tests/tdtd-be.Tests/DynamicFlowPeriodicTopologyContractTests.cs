using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowPeriodicTopologyContractTests
{
    public static void Run()
    {
        ExactScheduleGatewayIsRequired();
        TimeZoneAndDstResolutionAreDeterministic();
        OccurrenceIdentityAndTwoRunFixtureAreStable();
        PersistenceAndWorkerContractsAreFrozen();
        P6CandidateOpensT10AndTheCompletedP6Boundary();
    }

    private static void ExactScheduleGatewayIsRequired()
    {
        var canonical = Canonical(Periodic());
        var topology = DynamicFlowPeriodicTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.ScheduleKey == "daily-report" &&
            topology.ScheduleGateway.Gateway?.Kind ==
                DynamicFlowGatewayKinds.Schedule,
            "exact SCHEDULE gateway and key");

        var missingKey = Periodic();
        missingKey.Nodes[1].Gateway!.ScheduleKey = " ";
        AssertThrows(
            missingKey,
            "blank schedule key must fail closed");

        var outgoing = Periodic();
        outgoing.Edges.Add(new DynamicFlowTopologyEdgeDto
        {
            TransitionId = "invalid-outgoing",
            FromNodeId = "schedule",
            ToNodeId = "entry"
        });
        AssertThrows(
            outgoing,
            "schedule gateway must be terminal");
    }

    private static void TimeZoneAndDstResolutionAreDeterministic()
    {
        var normalizedUtc =
            DynamicFlowPeriodicTopologyContract.NormalizeTimeZoneId(
                "UTC");
        Require(
            DynamicFlowPeriodicTopologyContract.NormalizeTimeZoneId(
                normalizedUtc) == normalizedUtc &&
            TimeZoneInfo.FindSystemTimeZoneById(
                normalizedUtc).BaseUtcOffset == TimeSpan.Zero,
            "UTC normalization");
        var eastern =
            DynamicFlowPeriodicTopologyContract.NormalizeTimeZoneId(
                "America/New_York");
        var overlap =
            DynamicFlowPeriodicTopologyContract.ResolveScheduledAtUtc(
                new DateOnly(2026, 11, 1),
                new TimeOnly(1, 30),
                eastern);
        Require(
            overlap == new DateTime(
                2026,
                11,
                1,
                5,
                30,
                0,
                DateTimeKind.Utc),
            "DST overlap must deterministically choose earliest UTC instant");

        try
        {
            _ = DynamicFlowPeriodicTopologyContract.ResolveScheduledAtUtc(
                new DateOnly(2026, 3, 8),
                new TimeOnly(2, 30),
                eastern);
            throw new InvalidOperationException(
                "DST gap must fail closed");
        }
        catch (InvalidOperationException error)
            when (error.Message ==
                  DynamicFlowPeriodicTopologyContract.DstInvalidLocalTime)
        {
        }
    }

    private static void OccurrenceIdentityAndTwoRunFixtureAreStable()
    {
        var scheduleId =
            DynamicFlowPeriodicTopologyContract.BuildScheduleId(
                "100000000000000000000001",
                "200000000000000000000001",
                "daily-report");
        var first =
            DynamicFlowPeriodicTopologyContract.NextDueAtUtc(
                new DateTime(
                    2026,
                    7,
                    27,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
                new TimeOnly(8, 15),
                "UTC");
        var second =
            DynamicFlowPeriodicTopologyContract.NextDueAtUtc(
                new DateTime(
                    2026,
                    7,
                    27,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
                new TimeOnly(8, 15),
                "UTC");
        Require(first == second, "two-run time fixture");
        var periodKey =
            DynamicFlowPeriodicTopologyContract.PeriodKey(
                first,
                "UTC");
        var occurrenceA =
            DynamicFlowPeriodicTopologyContract.BuildOccurrenceId(
                scheduleId,
                periodKey);
        var occurrenceB =
            DynamicFlowPeriodicTopologyContract.BuildOccurrenceId(
                scheduleId,
                periodKey);
        Require(
            occurrenceA == occurrenceB &&
            DynamicFlowPeriodicTopologyContract.BuildInstanceId(
                scheduleId,
                periodKey) ==
            DynamicFlowPeriodicTopologyContract.BuildInstanceId(
                scheduleId,
                periodKey),
            "schedule/period replay identity");
    }

    private static void PersistenceAndWorkerContractsAreFrozen()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null &&
               !Directory.Exists(Path.Combine(root, "Services")))
        {
            root = Directory.GetParent(root)?.FullName;
        }
        Require(root is not null, "backend source root");
        var indexSource = File.ReadAllText(
            Path.Combine(
                root!,
                "Data",
                "Indexes",
                "MongoIndexInitializer.cs"));
        var workerSource = File.ReadAllText(
            Path.Combine(
                root!,
                "Services",
                "DynamicFlows",
                "DynamicFlowPeriodicService.cs"));
        var materializerSource = File.ReadAllText(
            Path.Combine(
                root!,
                "Services",
                "DynamicFlows",
                "DynamicFlowRuntimeMaterialization.cs"));
        Require(
            indexSource.Contains(
                "ux_dynamicFlowPeriodicOccurrences_schedule_period",
                StringComparison.Ordinal) &&
            indexSource.Contains(
                "ux_dynamicFlowInstances_periodic_occurrence",
                StringComparison.Ordinal),
            "unique occurrence and instance indexes");
        Require(
            workerSource.Contains(
                "MissedNoCatchUp",
                StringComparison.Ordinal) &&
            workerSource.Contains(
                "MissedOverlap",
                StringComparison.Ordinal) &&
            workerSource.Contains(
                "DynamicFlowPeriodicOccurrenceStates.Launching",
                StringComparison.Ordinal),
            "no-catch-up, overlap and expired lease recovery");
        Require(
            materializerSource.Contains(
                "ConfirmAndMaterializePeriodicAsync",
                StringComparison.Ordinal) &&
            materializerSource.Contains(
                "DYNAMIC_FLOW_PERIODIC_OCCURRENCE_LEASE_LOST",
                StringComparison.Ordinal),
            "occurrence/runtime transaction fence");
    }

    private static void
        P6CandidateOpensT10AndTheCompletedP6Boundary()
    {
        var t10 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            DynamicFlowPeriodicTopologyContract.ArchetypeId);
        Require(
            t10.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-08 must open T10");
        var t12 = DynamicFlowRuntimeEligibilityPolicy.Evaluate(
            DynamicFlowP6CatalogCandidate.Version,
            DynamicFlowP6CatalogCandidate.SemanticHash,
            "FLOW-T12");
        Require(
            t12.Eligibility ==
                DynamicFlowRuntimeEligibilityPolicy.EligibleCandidate,
            "P6-10 must open the final owned FLOW-T12 boundary");
    }

    private static DynamicFlowTemplatePayloadDto Periodic()
    {
        var hash = new string('a', 64);
        return new DynamicFlowTemplatePayloadDto
        {
            ArchetypeId =
                DynamicFlowPeriodicTopologyContract.ArchetypeId,
            EntryStepId = "entry",
            RootDynamicFormTemplateId =
                "300000000000000000000001",
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
                    NodeId = "entry",
                    NodeCode = "ENTRY",
                    NodeKind = DynamicFlowNodeKinds.FormStep,
                    FormNodeId = "entry-form",
                    DeclaredRoles = ["OWNER"]
                },
                new DynamicFlowTopologyNodeDto
                {
                    NodeId = "schedule",
                    NodeCode = "SCHEDULE",
                    NodeKind = DynamicFlowNodeKinds.Gateway,
                    Gateway = new DynamicFlowGatewayDefinitionDto
                    {
                        Kind = DynamicFlowGatewayKinds.Schedule,
                        ScheduleKey = "daily-report"
                    }
                }
            ],
            Edges =
            [
                new DynamicFlowTopologyEdgeDto
                {
                    TransitionId = "scheduled-entry",
                    FromNodeId = "entry",
                    ToNodeId = "schedule"
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
            _ = DynamicFlowPeriodicTopologyContract.Require(
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
