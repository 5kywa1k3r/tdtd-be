using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Common.Capabilities;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowRuntimeMaterializationContractTests
{
    public static void Run()
    {
        CanonicalScheduleAndFrozenParticipantsAreStable();
        CoreIntentUsesOneTransactionAndSessionAwareWriters();
        MaterializerNeverUsesLegacyCreateThenPatch();
        OutboxLeaseAndRecoveryContractsAreExplicit();
        DurableReplayAndOperationsRemainReachable();
        PositiveBoundaryAddsOnlyOwnedP6Archetypes();
        SequentialOwnershipProofAcceptsSharedBranchPrefixes();
        SequentialReconcileRequiresTerminalMaterialization();
        StoredCandidateProvenanceAllowsHistoricalCatalogPins();
        CandidateActivationSeparatesP5AndP6();
    }

    private static void CanonicalScheduleAndFrozenParticipantsAreStable()
    {
        var familyId = Oid(1);
        var versionId = Oid(2);
        var formId = Oid(3);
        var formFamilyId = Oid(4);
        var issuerId = Oid(5);
        var issuerUnitId = Oid(6);
        var targetUnitId = Oid(7);
        var userId = Oid(8);
        var payload = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            $$"""
            {
              "schemaVersion":2,
              "archetypeId":"FLOW-T01",
              "entryStepId":"entry",
              "rootDynamicFormTemplateId":"{{formId}}",
              "catalogVersion":"{{DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion}}",
              "catalogSemanticHash":"{{DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256}}",
              "formNodes":[{
                "formNodeId":"root",
                "role":"ROOT",
                "dynamicFormTemplateId":"{{formId}}",
                "dynamicFormFamilyId":"{{formFamilyId}}",
                "dynamicFormVersionNo":3,
                "dynamicFormSchemaHash":"{{new string('a', 64)}}",
                "dynamicFormSnapshotHash":"{{new string('a', 64)}}"
              }],
              "nodes":[{
                "nodeId":"entry",
                "nodeCode":"ENTRY",
                "nodeKind":"FORM_STEP",
                "formNodeId":"root",
                "declaredRoles":["OWNER"]
              }],
              "edges":[],
              "actorPolicies":[],
              "fieldPolicies":[],
              "tableColumnPolicies":[],
              "mappingRules":[],
              "rollbackPolicy":{},
              "finalResultPolicy":{},
              "statisticProfile":{}
            }
            """,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true));
        var family = new DynamicFlowTemplate
        {
            Id = familyId,
            RootDynamicFormTemplateId = formId
        };
        var version = new DynamicFlowTemplateVersion
        {
            Id = versionId,
            TemplateId = familyId,
            VersionNo = 2,
            PayloadJson = payload.CanonicalJson,
            PayloadHash = payload.PayloadHash,
            CatalogVersion = DynamicFlowRuntimeCatalogCandidate.Version,
            CatalogSemanticHash = DynamicFlowRuntimeCatalogCandidate.SemanticHash
        };
        var participants = new Dictionary<string, IReadOnlyList<DynamicFlowParticipantUserSnapshotDto>>(
            StringComparer.Ordinal)
        {
            [targetUnitId] =
            [
                new DynamicFlowParticipantUserSnapshotDto
                {
                    UserId = userId,
                    Username = "frozen-user",
                    FullName = "Frozen User",
                    UnitId = targetUnitId,
                    UnitSymbol = "UNIT-FROZEN",
                    PositionCode = "CV01",
                    PositionName = "Frozen Position"
                }
            ]
        };
        DynamicFlowPreflightRequest Request(string schedule) => new()
        {
            FlowTemplateVersionId = versionId,
            CommandId = "canonical-command",
            TargetUnitIds = [targetUnitId],
            PeriodKey = "PERIOD-001",
            ScheduleIdentityJson = schedule
        };

        var first = DynamicFlowRuntimePreflightContract.Build(
            Oid(9),
            "TASK",
            family,
            version,
            Request("""{"z":{"b":2,"a":1},"a":true}"""),
            issuerId,
            issuerUnitId,
            participants);
        var reordered = DynamicFlowRuntimePreflightContract.Build(
            Oid(9),
            "TASK",
            family,
            version,
            Request("""{"a":true,"z":{"a":1,"b":2}}"""),
            issuerId,
            issuerUnitId,
            participants);

        Require(first.RequestHash == reordered.RequestHash, "equivalent schedule JSON must share request hash");
        Require(first.SnapshotToken == reordered.SnapshotToken, "equivalent schedule JSON must share snapshot token");
        Require(first.ScheduleIdentityJson == reordered.ScheduleIdentityJson, "schedule canonical bytes drift");
        Require(first.IssuerUnitId == issuerUnitId, "issuer unit was not frozen");
        var frozen = first.Targets.Single().Participants.Single();
        Require(frozen.Username == "frozen-user" && frozen.UnitSymbol == "UNIT-FROZEN",
            "participant display/unit snapshot was not frozen");
    }

    private static void CoreIntentUsesOneTransactionAndSessionAwareWriters()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs");
        var confirm = Slice(
            source,
            "public async Task<DynamicFlowConfirmResponse> ConfirmAndMaterializeAsync(",
            "public async Task<int> ProcessPendingAsync(");
        Require(confirm.Contains("_transactions.ExecuteAsync(", StringComparison.Ordinal),
            "core intent must use the frozen Mongo transaction runner");
        foreach (var writer in new[]
                 {
                     "InsertReceiptAsync(session",
                     "InsertInstanceAsync(session",
                     "InsertParticipantSnapshotAsync(",
                     "InsertStepAsync(session",
                     "AppendEventAsync(session",
                     "EnqueueAsync(session"
                 })
        {
            Require(confirm.Contains(writer, StringComparison.Ordinal), $"core intent writer missing: {writer}");
        }
    }

    private static void MaterializerNeverUsesLegacyCreateThenPatch()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs");
        Require(!source.Contains("_assignments.CreateAsync", StringComparison.Ordinal),
            "durable path must not call legacy assignment CreateAsync");
        Require(!source.Contains("ApplyFlowMetadataAsync", StringComparison.Ordinal),
            "durable path must not patch Flow metadata after create");
        Require(source.Contains("StableObjectId", StringComparison.Ordinal),
            "branch/assignment identities must be deterministic");
        Require(source.Contains("DynamicFormVersionNo = step.FormVersionNo", StringComparison.Ordinal) &&
                source.Contains("DynamicFormSchemaHash = step.FormSchemaHash", StringComparison.Ordinal),
            "assignment must preserve exact Form version/hash pins");
        Require(source.Contains("Participants = target.Participants", StringComparison.Ordinal),
            "durable participant snapshot must preserve frozen participant identities");
    }

    private static void OutboxLeaseAndRecoveryContractsAreExplicit()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs");
        Require(source.Contains("HeldClaim(item)", StringComparison.Ordinal) &&
                source.Contains("filter.Eq(x => x.LeaseId, item.LeaseId)", StringComparison.Ordinal) &&
                source.Contains("filter.Eq(x => x.RepairEpoch, item.RepairEpoch)", StringComparison.Ordinal),
            "complete/fail must use lease-id and repair-epoch CAS");
        Require(source.Contains("TerminalizeFailureAsync", StringComparison.Ordinal) &&
                source.Contains("_transactions.ExecuteAsync(", StringComparison.Ordinal) &&
                source.Contains("x.Status == DynamicFlowRuntimeCommandStatuses.Pending", StringComparison.Ordinal),
            "terminal failure must atomically fence the outbox and pending receipt");
        Require(source.Contains("CompleteCompensationAsync", StringComparison.Ordinal) &&
                source.Contains("x.CompensatedAtUtc == null", StringComparison.Ordinal) &&
                source.Contains("\\nevent\\ncompensation-applied", StringComparison.Ordinal),
            "compensation completion must use one durable CAS and deterministic event");
        Require(source.Contains("DynamicFlowInstanceStates.Partial", StringComparison.Ordinal) &&
                source.Contains("DynamicFlowInstanceStates.Retrying", StringComparison.Ordinal) &&
                source.Contains("DynamicFlowInstanceStates.Reconciled", StringComparison.Ordinal),
            "durable recovery state chain is incomplete");
        Require(source.Contains("CompensateOwnedArtifactsAsync", StringComparison.Ordinal) &&
                source.Contains("x.FlowInstanceId == flowInstanceId", StringComparison.Ordinal),
            "scoped compensation contract missing");
        Require(DynamicFlowRuntimeFaultPoints.All.Count >= 20,
            "fault injector must cover core and materializer boundaries");
    }

    private static void PositiveBoundaryAddsOnlyOwnedP6Archetypes()
    {
        var source = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs");
        Require(
            source.Contains(
                "DynamicFlowSequentialTopologyContract.ArchetypeId",
                StringComparison.Ordinal) &&
            source.Contains(
                "MaterializeSequentialAssignment",
                StringComparison.Ordinal) &&
            source.Contains(
                "SequentialForwardAccepted",
                StringComparison.Ordinal) &&
            source.Contains(
                "SequentialAssignmentMaterialized",
                StringComparison.Ordinal),
            "materializer must explicitly validate the P6-01 sequential path");
        Require(
            source.Contains(
                "DynamicFlowSubflowTopologyContract.ArchetypeId",
                StringComparison.Ordinal),
            "P6-07 immutable proof must explicitly admit only the owned T09 path");
        Require(
            source.Contains(
                "DynamicFlowFinalizeTopologyContract.ArchetypeId",
                StringComparison.Ordinal) &&
            source.Contains(
                "RequireFinalizeMaterializationIntentPinsAsync",
                StringComparison.Ordinal),
            "P6-10 materializer must admit only the typed T12 epoch path");
    }

    private static void DurableReplayAndOperationsRemainReachable()
    {
        var service = ReadSource("Services/DynamicFlows/DynamicFlowRuntimeService.cs");
        var confirm = Slice(
            service,
            "public async Task<DynamicFlowConfirmResponse> ConfirmAsync(",
            "public async Task<DynamicFlowInstanceLaunchResponse> CreateInstanceAsync(");
        var receiptLookup = confirm.IndexOf(
            "DynamicFlowRuntimeCommandReceipts",
            StringComparison.Ordinal);
        var livePreflight = confirm.IndexOf("PreflightAsync(workId", StringComparison.Ordinal);
        Require(
            receiptLookup >= 0 && livePreflight > receiptLookup,
            "existing receipt replay must be resolved before mutable live preflight");
        Require(
            confirm.Contains("BuildCommandIdentityHash", StringComparison.Ordinal) &&
            confirm.Contains("ResumeExistingAsync", StringComparison.Ordinal),
            "durable command identity replay path is missing");

        var model = ReadSource("Models/DynamicFlowRuntimePersistence.cs");
        Require(
            model.Contains("CommandIdentityHash", StringComparison.Ordinal) &&
            model.Contains("SnapshotToken", StringComparison.Ordinal),
            "receipt must persist the durable replay envelope");
        var operations = ReadSource("Controllers/DynamicFlowRuntimeOperationsController.cs");
        Require(
            operations.Contains("ReconcileAsync", StringComparison.Ordinal) &&
            operations.Contains("CompensateOwnedArtifactsAsync", StringComparison.Ordinal) &&
            operations.Contains("RoleGuard.RequireSystemAdmin", StringComparison.Ordinal),
            "authorized reconcile/compensation operations are unreachable");
    }

    private static void SequentialOwnershipProofAcceptsSharedBranchPrefixes()
    {
        var formA = Oid(31);
        var formB = Oid(32);
        var formC = Oid(33);
        var familyA = Oid(34);
        var familyB = Oid(35);
        var familyC = Oid(36);
        var schemaA = new string('a', 64);
        var schemaB = new string('b', 64);
        var schemaC = new string('c', 64);
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            $$"""
            {
              "schemaVersion":2,
              "archetypeId":"FLOW-T03",
              "entryStepId":"a",
              "rootDynamicFormTemplateId":"{{formA}}",
              "resultOwnerStepId":"c",
              "resultOwnerFormNodeId":"form-c",
              "statisticsOwnerStepId":"c",
              "statisticsOwnerFormNodeId":"form-c",
              "catalogVersion":"{{DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion}}",
              "catalogSemanticHash":"{{DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256}}",
              "formNodes":[
                {
                  "formNodeId":"form-a",
                  "role":"ROOT",
                  "dynamicFormTemplateId":"{{formA}}",
                  "dynamicFormFamilyId":"{{familyA}}",
                  "dynamicFormVersionNo":1,
                  "dynamicFormSchemaHash":"{{schemaA}}",
                  "dynamicFormSnapshotHash":"{{schemaA}}"
                },
                {
                  "formNodeId":"form-b",
                  "role":"DETAIL",
                  "dynamicFormTemplateId":"{{formB}}",
                  "dynamicFormFamilyId":"{{familyB}}",
                  "dynamicFormVersionNo":2,
                  "dynamicFormSchemaHash":"{{schemaB}}",
                  "dynamicFormSnapshotHash":"{{schemaB}}"
                },
                {
                  "formNodeId":"form-c",
                  "role":"RESULT",
                  "dynamicFormTemplateId":"{{formC}}",
                  "dynamicFormFamilyId":"{{familyC}}",
                  "dynamicFormVersionNo":3,
                  "dynamicFormSchemaHash":"{{schemaC}}",
                  "dynamicFormSnapshotHash":"{{schemaC}}"
                }
              ],
              "nodes":[
                {
                  "nodeId":"a",
                  "nodeCode":"A",
                  "nodeKind":"FORM_STEP",
                  "formNodeId":"form-a",
                  "declaredRoles":["OWNER"]
                },
                {
                  "nodeId":"b",
                  "nodeCode":"B",
                  "nodeKind":"FORM_STEP",
                  "formNodeId":"form-b",
                  "declaredRoles":["OWNER"]
                },
                {
                  "nodeId":"c",
                  "nodeCode":"C",
                  "nodeKind":"FORM_STEP",
                  "formNodeId":"form-c",
                  "declaredRoles":["OWNER"]
                }
              ],
              "edges":[
                {"transitionId":"a-b","fromNodeId":"a","toNodeId":"b"},
                {"transitionId":"b-c","fromNodeId":"b","toNodeId":"c"}
              ],
              "actorPolicies":[],
              "fieldPolicies":[],
              "tableColumnPolicies":[],
              "mappingRules":[],
              "rollbackPolicy":{},
              "finalResultPolicy":{},
              "statisticProfile":{}
            }
            """,
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        var topology = DynamicFlowSequentialTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        var instanceId = Oid(40);
        var targetUnitId = Oid(41);
        var issuerUserId = Oid(42);
        var issuerUnitId = Oid(43);
        var participantUserId = Oid(44);
        var snapshotId = Oid(45);
        var flowVersionId = Oid(46);
        var branchId = DynamicFlowSequentialTopologyContract.BuildRootBranchId(
            instanceId,
            DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
            targetUnitId);
        var instance = new DynamicFlowInstance
        {
            Id = instanceId,
            WorkId = Oid(47),
            FlowTemplateId = Oid(48),
            FlowTemplateVersionId = flowVersionId,
            FlowTemplateVersionNo = 1,
            FlowPayloadHash = topology.TopologyHash,
            CatalogVersion = DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            CatalogSemanticHash = DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            ArchetypeId = DynamicFlowSequentialTopologyContract.ArchetypeId,
            DefinitionRevision =
                DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                    flowVersionId,
                    1,
                    topology.TopologyHash),
            TopologySnapshotJson = topology.CanonicalJson,
            TopologySnapshotHash = topology.TopologyHash,
            ExecutionEpoch =
                DynamicFlowSequentialTopologyContract.InitialExecutionEpoch,
            EntryFlowStepId = "a",
            PeriodKey = "P6-01",
            ScheduleIdentityHash = new string('d', 64),
            ParticipantSnapshotId = snapshotId,
            IssuerUserId = issuerUserId,
            IssuerUnitId = issuerUnitId
        };
        var snapshot = new DynamicFlowParticipantSnapshot
        {
            Id = snapshotId,
            FlowInstanceId = instanceId,
            IssuerUserId = issuerUserId,
            IssuerUnitId = issuerUnitId,
            Bindings =
            [
                new DynamicFlowParticipantBinding
                {
                    TargetUnitId = targetUnitId,
                    AssigneeUserIds = [participantUserId],
                    Participants =
                    [
                        new DynamicFlowParticipantUserSnapshot
                        {
                            UserId = participantUserId,
                            Username = "p6-owner",
                            FullName = "P6 Owner",
                            UnitId = targetUnitId
                        }
                    ],
                    RoleCodes = [DynamicFlowRuntimePlanner.AssignmentFlowRole]
                }
            ],
            SourceRevisionTokens = new SortedDictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["catalog"] = instance.CatalogSemanticHash,
                ["flow"] = instance.FlowPayloadHash,
                ["topology"] = instance.TopologySnapshotHash,
                ["forms"] = new string('e', 64),
                ["schedule"] = instance.ScheduleIdentityHash
            }
        };
        var hashMethod = typeof(DynamicFlowRuntimeMaterializer).GetMethod(
            "ParticipantSnapshotHash",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "participant snapshot hash oracle missing");
        snapshot.SnapshotHash = (string)hashMethod.Invoke(null, [snapshot])!;
        instance.ParticipantSnapshotHash = snapshot.SnapshotHash;

        DynamicFlowStepInstance Step(int index)
        {
            var node = topology.OrderedNodes[index];
            var form = topology.FormsByNodeId[node.NodeId];
            var stepId =
                DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
                    instanceId,
                    instance.ExecutionEpoch,
                    node.NodeId,
                    branchId,
                    1);
            var nextNodeIds = topology.OutgoingByNodeId.TryGetValue(
                node.NodeId,
                out var outgoing)
                ? new List<string> { outgoing.ToNodeId }
                : new List<string>();
            var incomingTransitionId = index == 0
                ? null
                : topology.OutgoingByNodeId[
                    topology.OrderedNodes[index - 1].NodeId].TransitionId;
            return new DynamicFlowStepInstance
            {
                Id = stepId,
                FlowInstanceId = instanceId,
                FlowStepId = node.NodeId,
                FlowStepCode = node.NodeCode,
                ExecutionEpoch = instance.ExecutionEpoch,
                DefinitionRevision = instance.DefinitionRevision,
                StepOrder = index + 1,
                FormNodeId = form.FormNodeId,
                FormFamilyId = form.DynamicFormFamilyId!,
                FormVersionId = form.DynamicFormTemplateId,
                FormVersionNo = form.DynamicFormVersionNo!.Value,
                FormSchemaHash = form.DynamicFormSchemaHash!,
                FormSnapshotHash = form.DynamicFormSnapshotHash!,
                TargetUnitId = targetUnitId,
                ParticipantUserIds = [participantUserId],
                ParticipantSnapshotId = snapshotId,
                AttemptNo = 1,
                BranchId = branchId,
                ActivatedByTransitionId = incomingTransitionId,
                NextNodeIds = nextNodeIds,
                IsTerminalNode = nextNodeIds.Count == 0,
                ResultOwnerIdentity =
                    DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                        instanceId,
                        instance.ExecutionEpoch,
                        "c",
                        branchId,
                        "result"),
                StatisticOwnerIdentity =
                    DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                        instanceId,
                        instance.ExecutionEpoch,
                        "c",
                        branchId,
                        "statistics"),
                AssignmentId =
                    DynamicFlowSequentialTopologyContract.BuildAssignmentId(
                        stepId),
                State = DynamicFlowStepStates.Assigned,
                Revision = 2
            };
        }

        var steps = new[] { Step(0), Step(1), Step(2) };
        var proofMethod = typeof(DynamicFlowRuntimeMaterializer).GetMethod(
            "TryBuildOwnershipProof",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ownership proof oracle missing");
        bool Proves(IReadOnlyList<DynamicFlowStepInstance> candidate, int count)
        {
            object?[] arguments = [instance, snapshot, candidate, null];
            var proven = (bool)proofMethod.Invoke(null, arguments)!;
            var actualCount = arguments[3] is null
                ? 0
                : (int)arguments[3]!.GetType()
                    .GetProperty("Count")!
                    .GetValue(arguments[3])!;
            return proven && actualCount == count;
        }

        Require(
            Proves(steps.Take(2).ToArray(), 2),
            "A-to-B ownership prefix must share one target branch");
        Require(
            Proves(steps, 3),
            "A-to-B-to-C ownership prefix must share one target branch");
        Require(
            !Proves([steps[0], steps[2]], 2),
            "ownership proof must reject a skipped sequential node");
        var canonicalBranchId = steps[1].BranchId;
        steps[1].BranchId = Oid(49);
        Require(
            !Proves(steps, 3),
            "ownership proof must reject branch drift");
        steps[1].BranchId = canonicalBranchId;
    }

    private static void CandidateActivationSeparatesP5AndP6()
    {
        Require(DynamicFlowRuntimeCatalogCandidate.ActivationEnabled,
            "catalog successor must be release-enabled at P5-08 closeout");
        var enabledConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DynamicFlowRuntimeActivationPolicy.TestingActivationKey] = "true",
                [DynamicFlowRuntimeActivationPolicy.TestingActivationThroughKey] = "7"
            })
            .Build();
        var disabledConfiguration = new ConfigurationBuilder().Build();
        var activationWithoutRangeConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DynamicFlowRuntimeActivationPolicy.TestingActivationKey] = "true"
            })
            .Build();
        IConfiguration TestingRange(int activationThrough) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DynamicFlowRuntimeActivationPolicy.TestingActivationKey] =
                        "true",
                    [DynamicFlowRuntimeActivationPolicy.TestingActivationThroughKey] =
                        activationThrough.ToString()
                })
                .Build();
        var generatedCurrentPolicy = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Development"),
            enabledConfiguration);
        var productionSealed = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Production"),
            disabledConfiguration,
            sealedP6CatalogActivationEnabled: true);
        var productionP6RolledBackUnderP7 = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Production"),
            enabledConfiguration,
            sealedP6CatalogActivationEnabled: false);
        var testingDisabledPreseal = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Testing"),
            disabledConfiguration,
            sealedP6CatalogActivationEnabled: false);
        var testingEnabledPreseal = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Testing"),
            enabledConfiguration,
            sealedP6CatalogActivationEnabled: false);
        var testingWithoutRangePreseal = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Testing"),
            activationWithoutRangeConfiguration,
            sealedP6CatalogActivationEnabled: false);
        var testingSealed = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Testing"),
            enabledConfiguration,
            sealedP6CatalogActivationEnabled: true);
        var testingThroughThree = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Testing"),
            TestingRange(3),
            sealedP6CatalogActivationEnabled: false);
        var testingThroughTwelve = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Testing"),
            TestingRange(12),
            sealedP6CatalogActivationEnabled: false);
        var testingInvalidLow = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Testing"),
            TestingRange(2),
            sealedP6CatalogActivationEnabled: false);
        var testingInvalidHigh = new DynamicFlowRuntimeActivationPolicy(
            new TestHostEnvironment("Testing"),
            TestingRange(13),
            sealedP6CatalogActivationEnabled: false);

        Require(
            generatedCurrentPolicy.CandidateExecutionEnabled &&
            productionSealed.CandidateExecutionEnabled &&
            productionP6RolledBackUnderP7.CandidateExecutionEnabled &&
            testingEnabledPreseal.CandidateExecutionEnabled,
            "P5 candidate activation must remain release-enabled");
        Require(
            generatedCurrentPolicy.P6CandidateExecutionEnabled ==
                DynamicFlowP6CatalogCandidate.ActivationEnabled,
            "default P6 runtime activation must follow the generated CURRENT pin");
        Require(
            generatedCurrentPolicy.P7MappingCandidateExecutionEnabled ==
                DynamicFlowP7CatalogCandidate.ActivationEnabled &&
            generatedCurrentPolicy.CanExecuteP7MappingPin(
                DynamicFlowP7CatalogCandidate.Version,
                DynamicFlowP7CatalogCandidate.SemanticHash),
            "default runtime activation must open the exact generated CURRENT v1.4 mapping pin");
        Require(
            productionSealed.P6CandidateExecutionEnabled &&
            testingSealed.P6CandidateExecutionEnabled,
            "an exact sealed v1.3 CURRENT must enable production execution");
        for (var number = 1; number <= 12; number++)
        {
            Require(
                productionSealed.IsP6CandidateArchetypeEnabled(
                    $"FLOW-T{number:00}"),
                $"sealed production must open FLOW-T{number:00}");
            Require(
                testingSealed.IsP6CandidateArchetypeEnabled(
                    $"FLOW-T{number:00}"),
                $"sealed Testing must not narrow FLOW-T{number:00}");
        }
        Require(
            !productionSealed.IsP6CandidateArchetypeEnabled("FLOW-T13"),
            "sealed production must remain bounded to T01-through-T12");
        Require(
            !productionSealed.IsP6CandidateArchetypeEnabled("FLOW-T00") &&
            !productionSealed.IsP6CandidateArchetypeEnabled(null) &&
            !productionSealed.IsP6CandidateArchetypeEnabled("flow-t03"),
            "sealed production must reject null and malformed archetype pins");
        Require(
            !productionP6RolledBackUnderP7.P6CandidateExecutionEnabled &&
            productionP6RolledBackUnderP7.P7MappingCandidateExecutionEnabled &&
            productionP6RolledBackUnderP7.IsP6CandidateArchetypeEnabled("FLOW-T01") &&
            productionP6RolledBackUnderP7.IsP6CandidateArchetypeEnabled("FLOW-T03") &&
            productionP6RolledBackUnderP7.IsP6CandidateArchetypeEnabled("FLOW-T12"),
            "CURRENT v1.4 must supersede the P6 activation flag while keeping its topology archetypes open");
        Require(
            !testingDisabledPreseal.P6CandidateExecutionEnabled &&
            !testingWithoutRangePreseal.P6CandidateExecutionEnabled &&
            testingEnabledPreseal.P6CandidateExecutionEnabled,
            "preseal execution must require both Testing activation fields");
        Require(
            testingEnabledPreseal.IsP6CandidateArchetypeEnabled("FLOW-T02") &&
            testingEnabledPreseal.IsP6CandidateArchetypeEnabled("FLOW-T03") &&
            testingEnabledPreseal.IsP6CandidateArchetypeEnabled("FLOW-T07") &&
            testingEnabledPreseal.IsP6CandidateArchetypeEnabled("FLOW-T08") &&
            testingDisabledPreseal.IsP6CandidateArchetypeEnabled("FLOW-T03") &&
            testingWithoutRangePreseal.IsP6CandidateArchetypeEnabled("FLOW-T03"),
            "official P7 activation must supersede historical Testing range narrowing for valid topology archetypes");
        Require(
            testingThroughThree.P6CandidateExecutionEnabled &&
            testingThroughThree.IsP6CandidateArchetypeEnabled("FLOW-T03") &&
            testingThroughThree.IsP6CandidateArchetypeEnabled("FLOW-T04") &&
            testingThroughTwelve.P6CandidateExecutionEnabled &&
            testingThroughTwelve.IsP6CandidateArchetypeEnabled("FLOW-T12") &&
            testingThroughTwelve.IsP6CandidateArchetypeEnabled("FLOW-T02") &&
            !testingInvalidLow.P6CandidateExecutionEnabled &&
            !testingInvalidHigh.P6CandidateExecutionEnabled,
            "historical Testing activation flags remain bounded while official P7 keeps valid topology archetypes open");
        Require(
            testingEnabledPreseal.CanExecuteP6CandidatePin(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T07") &&
            testingEnabledPreseal.CanExecuteP6CandidatePin(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T08") &&
            !testingEnabledPreseal.CanExecuteP6CandidatePin(
                DynamicFlowP6CatalogCandidate.Version,
                new string('f', 64),
                "FLOW-T07") &&
            !productionP6RolledBackUnderP7.CanExecuteP6CandidatePin(
                DynamicFlowP6CatalogCandidate.Version,
                DynamicFlowP6CatalogCandidate.SemanticHash,
                "FLOW-T03"),
            "stored operations must apply exact catalog, SHA, archetype, and rollback gates");

        var model = ReadSource("Models/DynamicFlowRuntimePersistence.cs");
        var materialization = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs");
        var forward = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeService.cs");
        var epochCommands = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeEpochCommands.cs");
        var runtimeReads = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeReadService.cs");
        var resumeMethod = Slice(
            materialization,
            "private async Task ResumeReceiptAsync(",
            "public async Task<int> ProcessPendingAsync(");
        var claimMethod = Slice(
            materialization,
            "private async Task<DynamicFlowRuntimeOutboxItem?> ClaimNextAsync(",
            "private async Task ProcessClaimedAsync(");
        var resumeGuard = resumeMethod.IndexOf(
            "if (!await CanExecuteStoredInstanceAsync(flowInstanceId, ct))",
            StringComparison.Ordinal);
        var resumeSideEffect = resumeMethod.IndexOf(
            "var recovery = await PreparePendingReplayAsync(",
            resumeGuard,
            StringComparison.Ordinal);
        var claimGuard = claimMethod.IndexOf(
            "if (!await CanExecuteStoredInstanceAsync(",
            StringComparison.Ordinal);
        var claimSideEffect = claimMethod.IndexOf(
            "RuntimeMaterializationFenceFilter(",
            claimGuard,
            StringComparison.Ordinal);
        Require(
            resumeGuard >= 0 &&
            resumeSideEffect > resumeGuard &&
            claimGuard > resumeSideEffect &&
            claimSideEffect > claimGuard,
            "rollback must gate pending receipt and outbox work before their first side effect");
        Require(
            forward.Contains(
                "CanExecuteP6CandidatePin(",
                StringComparison.Ordinal) &&
            epochCommands.Contains(
                "CanExecuteP6CandidatePin(",
                StringComparison.Ordinal) &&
            runtimeReads.Contains(
                "CanExecuteP6CandidatePin(",
                StringComparison.Ordinal) &&
            !forward.Contains(
                "_runtimeActivation.P6CandidateExecutionEnabled",
                StringComparison.Ordinal) &&
            !epochCommands.Contains(
                "_runtimeActivation.P6CandidateExecutionEnabled",
                StringComparison.Ordinal) &&
            !runtimeReads.Contains(
                "_runtimeActivation.P6CandidateExecutionEnabled",
                StringComparison.Ordinal),
            "stored runtime commands and capabilities must use the exact per-archetype pin gate");
        Require(
            model.Contains("ScheduleIdentityJson", StringComparison.Ordinal) &&
            forward.Contains(
                "{ \"scheduleIdentityJson\", instance.ScheduleIdentityJson }",
                StringComparison.Ordinal) &&
            materialization.Contains(
                "scheduleIdentityJson != instance.ScheduleIdentityJson",
                StringComparison.Ordinal) &&
            materialization.Contains(
                "Hash(scheduleIdentityJson)",
                StringComparison.Ordinal),
            "every T03 outbox must preserve and verify the exact immutable schedule JSON/hash pin");
        var intentValidation = materialization.IndexOf(
            "await RequireMaterializationIntentPinsAsync(",
            StringComparison.Ordinal);
        var firstAssignmentWrite = materialization.IndexOf(
            "DynamicFlowRuntimeFaultPoints.BeforeAssignmentWrite",
            intentValidation,
            StringComparison.Ordinal);
        Require(
            intentValidation >= 0 &&
            firstAssignmentWrite > intentValidation &&
            materialization.Contains(
                "DYNAMIC_FLOW_RUNTIME_OUTBOX_INTENT_PIN_DRIFT",
                StringComparison.Ordinal) &&
            materialization.Contains(
                "BuildForwardReceiptId(",
                StringComparison.Ordinal),
            "materialization worker must bind exact committed intent/receipt pins before its first side effect");
    }

    private static void SequentialReconcileRequiresTerminalMaterialization()
    {
        var method = typeof(DynamicFlowRuntimeMaterializer).GetMethod(
            "ExpectedRuntimeInstanceState",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "runtime instance completion oracle missing");
        var instance = new DynamicFlowInstance
        {
            ArchetypeId = DynamicFlowSequentialTopologyContract.ArchetypeId
        };
        var first = new DynamicFlowStepInstance
        {
            BranchId = Oid(61),
            State = DynamicFlowStepStates.Completed,
            IsTerminalNode = false
        };
        var second = new DynamicFlowStepInstance
        {
            BranchId = first.BranchId,
            State = DynamicFlowStepStates.Completed,
            IsTerminalNode = false
        };
        var terminal = new DynamicFlowStepInstance
        {
            BranchId = first.BranchId,
            State = DynamicFlowStepStates.Completed,
            IsTerminalNode = true
        };
        string Expected(params DynamicFlowStepInstance[] steps)
            => (string)method.Invoke(null, [instance, steps])!;

        Require(
            Expected(first) == DynamicFlowInstanceStates.Active &&
            Expected(first, second) == DynamicFlowInstanceStates.Active,
            "a completed non-terminal T03 prefix must keep the instance active");
        Require(
            Expected(first, second, terminal) ==
            DynamicFlowInstanceStates.Completed,
            "a fully completed T03 branch must require its materialized terminal");
        var secondBranchPrefix = new DynamicFlowStepInstance
        {
            BranchId = Oid(62),
            State = DynamicFlowStepStates.Completed,
            IsTerminalNode = false
        };
        Require(
            Expected(first, second, terminal, secondBranchPrefix) ==
            DynamicFlowInstanceStates.Active,
            "every T03 target branch must materialize and complete its terminal");

        var source = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs");
        Require(
            source.Contains(
                "long StepRecoveryRevisionDelta(",
                StringComparison.Ordinal) &&
            source.Contains(
                "item.ToState == DynamicFlowStepStates.Completed",
                StringComparison.Ordinal),
            "recovery revision deltas must not be charged to a step completed before recovery");
        var finalizer = Slice(
            source,
            "private async Task TryFinalizeInstanceAsync(",
            "private async Task<DynamicFlowConfirmResponse> BuildResponseAsync(");
        Require(
            finalizer.Contains(
                ".Set(x => x.ResumeState, null)",
                StringComparison.Ordinal),
            "successful recovery finalization must clear the instance resume state");

        instance.ArchetypeId = "FLOW-T01";
        Require(
            Expected(first) == DynamicFlowInstanceStates.Completed,
            "legacy T01/T02 completion semantics must remain unchanged");
    }

    private static void StoredCandidateProvenanceAllowsHistoricalCatalogPins()
    {
        var source = ReadSource(
            "Services/DynamicFlows/DynamicFlowRuntimeMaterialization.cs");
        var provenance = Slice(
            source,
            "private async Task<bool> HasExactRuntimeImmutableAuthorityAsync(",
            "private static bool SequentialStepPinsMatch(");
        Require(
            provenance.Contains(
                "AllowHistoricalCatalogPins: true",
                StringComparison.Ordinal),
            "stored immutable P6 candidate provenance must accept its pinned historical catalog");
    }

    private sealed class TestHostEnvironment(string environmentName)
        : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tdtd-be.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static string Oid(int seed) => seed.ToString("x24");

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + Math.Max(1, start.Length), StringComparison.Ordinal);
        Require(from >= 0 && to > from, $"source slice not found: {start} .. {end}");
        return source[from..to];
    }

    private static string ReadSource(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (File.Exists(direct))
                return File.ReadAllText(direct);
            var nested = Path.Combine(directory.FullName, "tdtd-be", relative);
            if (File.Exists(nested))
                return File.ReadAllText(nested);
        }
        throw new FileNotFoundException(relativePath);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
