using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-01 executable evidence. The probe never mutates CURRENT. An
/// exact non-current v1.3 fixture requires explicit Testing activation, while
/// frozen v1.2 future barriers stay historical after P6 or successor promotion.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const string P601LaunchCommand = "p6-01-t03-launch";
    private const string P601ForwardACommand = "p6-01-t03-forward-a-b";
    private const string P601ForwardBCommand = "p6-01-t03-forward-b-c";
    private const string P601PeriodKey = "2026-07-P601";
    private const string P601ScheduleIdentityJson =
        "{\"anchor\":\"2026-07-23\",\"cadence\":\"once\",\"timezone\":\"Asia/Ho_Chi_Minh\"}";

    public static async Task<int> RunP601Async()
    {
        var runKey = $"p601_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        var exchanges = new List<ApiExchangeEvidence>();
        object? directMongoEvidence = null;
        object? barrierEvidence = null;
        object? replayEvidence = null;
        object? recoveryEvidence = null;
        object? regressionEvidence = null;
        object? branchOwnerEvidence = null;
        ApiHarnessClient? apiClient = null;
        string? failure = null;
        var passed = false;

        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                paths,
                iterationRoot,
                runKey,
                1,
                CancellationToken.None);
            var database = mongo.Client.GetDatabase(mongo.DatabaseName);
            var serverRoot = Path.Combine(iterationRoot, "backend-p6-sequential");
            Directory.CreateDirectory(serverRoot);
            backend = await BackendServerLease.StartAsync(
                paths,
                serverRoot,
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeCandidateActivationThrough = 3,
                    DynamicFlowRuntimeFaultCommandId = P601ForwardBCommand,
                    DynamicFlowRuntimeFaultBranchOrdinal = 3,
                    DynamicFlowRuntimeFaultPoints =
                    [
                        DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite
                    ]
                });

            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var (adminToken, adminPassword) = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var p5Fixture = await SeedFixtureAsync(database, CancellationToken.None);
            var fixture = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);

            regressionEvidence = await AssertP601P5RegressionAsync(
                api,
                adminToken,
                database,
                p5Fixture,
                CancellationToken.None);
            barrierEvidence = await AssertP601FutureBarrierAsync(
                api,
                adminToken,
                database,
                p5Fixture,
                CancellationToken.None);
            branchOwnerEvidence = await AssertP601BranchOwnerIdentityOracleAsync(
                api,
                adminToken,
                database,
                p5Fixture,
                fixture,
                CancellationToken.None);

            var launch = await LaunchAsync(
                api,
                adminToken,
                p5Fixture,
                fixture.WorkId,
                fixture.VersionId,
                P601LaunchCommand,
                [fixture.TargetUnitId],
                P601PeriodKey,
                CancellationToken.None);
            RequireStatus(launch.Confirm, "SUCCEEDED", "P6-01 FLOW-T03 launch");
            var instanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");

            var launchSteps = await LoadP601StepsAsync(
                database,
                instanceId,
                CancellationToken.None);
            Require(
                launchSteps.Count == 1 &&
                launchSteps.Single().FlowStepId == "step_a",
                "FLOW-T03 launch must materialize exactly entry step A.");
            var stepA = launchSteps.Single();
            var assignmentA = stepA.AssignmentId
                              ?? throw new InvalidOperationException(
                                  "FLOW-T03 step A lacks assignment.");

            var beforeUnapproved = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var launchInstance = await LoadP601InstanceAsync(
                database,
                instanceId,
                CancellationToken.None);
            var unapproved = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentA,
                "p6-01-unapproved-forward",
                launchInstance.Revision,
                stepA.Revision,
                CancellationToken.None);
            AssertP601Conflict(
                unapproved,
                "DYNAMIC_FLOW_FORWARD_PARENT_NOT_APPROVED",
                "unapproved sequential forward",
                HttpStatusCode.BadRequest);
            Require(
                beforeUnapproved == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "Unapproved forward changed the direct-Mongo ledger.");

            var outsiderToken = await PrepareP601ActorLoginAsync(
                api,
                backend,
                database,
                fixture.OutsiderUserId,
                CancellationToken.None);
            var outsiderSteps = await api.GetAsync(
                $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/steps?limit=50",
                outsiderToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                outsiderSteps,
                HttpStatusCode.NotFound,
                "P6-01 outsider hidden steps");

            stepA = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                stepA,
                "field_note",
                "P5_NOTE",
                "a",
                CancellationToken.None);
            var capabilityA = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                fixture.WorkId,
                instanceId,
                stepA.Id,
                expectedCanForward: true,
                CancellationToken.None);

            var beforeOutsider = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var outsiderForward = await ForwardP601Async(
                api,
                outsiderToken,
                fixture.WorkId,
                assignmentA,
                "p6-01-outsider-forward",
                capabilityA.InstanceRevision,
                capabilityA.StepRevision,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                outsiderForward,
                HttpStatusCode.Forbidden,
                "P6-01 outsider forward");
            Require(
                beforeOutsider == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "Outsider forward changed the direct-Mongo ledger.");

            var forwardA = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentA,
                P601ForwardACommand,
                capabilityA.InstanceRevision,
                capabilityA.StepRevision,
                CancellationToken.None);
            AssertP601ForwardSuccess(forwardA, replayed: false, "A -> B");
            var forwardAReplay = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentA,
                P601ForwardACommand,
                capabilityA.InstanceRevision,
                capabilityA.StepRevision,
                CancellationToken.None);
            AssertP601ForwardSuccess(forwardAReplay, replayed: true, "A -> B exact replay");
            AssertP601SameForwardResult(forwardA, forwardAReplay, "A -> B exact replay");

            var afterAReplay = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var changedReplay = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentA,
                P601ForwardACommand,
                capabilityA.InstanceRevision,
                capabilityA.StepRevision + 1,
                CancellationToken.None);
            AssertP601Conflict(
                changedReplay,
                "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
                "A -> B changed replay");
            Require(
                afterAReplay == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "Changed replay changed the direct-Mongo ledger.");

            var staleForward = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentA,
                "p6-01-stale-forward",
                capabilityA.InstanceRevision,
                capabilityA.StepRevision,
                CancellationToken.None);
            AssertP601Conflict(
                staleForward,
                "DYNAMIC_FLOW_REVISION_CONFLICT",
                "A -> B stale revision");
            Require(
                afterAReplay == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "Stale forward changed the direct-Mongo ledger.");

            await WaitForP601AssignmentAsync(
                database,
                ApiHarnessClient.RequiredString(forwardA.Json, "nextAssignmentId"),
                CancellationToken.None);
            var stepsAfterA = await LoadP601StepsAsync(
                database,
                instanceId,
                CancellationToken.None);
            Require(
                stepsAfterA.Count == 2 &&
                stepsAfterA.Any(step => step.FlowStepId == "step_b"),
                "A -> B did not converge to exactly two steps.");
            await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                fixture.WorkId,
                instanceId,
                stepA.Id,
                expectedCanForward: false,
                CancellationToken.None);

            var stepB = stepsAfterA.Single(step => step.FlowStepId == "step_b");
            stepB = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                stepB,
                null,
                null,
                "b",
                CancellationToken.None);
            var capabilityB = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                fixture.WorkId,
                instanceId,
                stepB.Id,
                expectedCanForward: true,
                CancellationToken.None);
            var assignmentB = stepB.AssignmentId
                              ?? throw new InvalidOperationException(
                                  "FLOW-T03 step B lacks assignment.");
            var forwardBTasks = Enumerable.Range(0, 2)
                .Select(_ => ForwardP601Async(
                    api,
                    adminToken,
                    fixture.WorkId,
                    assignmentB,
                    P601ForwardBCommand,
                    capabilityB.InstanceRevision,
                    capabilityB.StepRevision,
                    CancellationToken.None))
                .ToArray();
            var forwardBResults = await Task.WhenAll(forwardBTasks);
            foreach (var response in forwardBResults)
                AssertP601ForwardSuccess(response, replayed: null, "B -> C concurrent replay");
            AssertP601SameForwardResult(
                forwardBResults[0],
                forwardBResults[1],
                "B -> C concurrent replay");
            Require(
                forwardBResults.Count(response =>
                    ApiHarnessClient.RequiredBool(response.Json, "replayed")) == 1,
                "Concurrent B -> C must produce one winner and one exact replay.");

            var assignmentC = ApiHarnessClient.RequiredString(
                forwardBResults[0].Json,
                "nextAssignmentId");
            await WaitForP601AssignmentAsync(
                database,
                assignmentC,
                CancellationToken.None);
            var stepsAfterB = await LoadP601StepsAsync(
                database,
                instanceId,
                CancellationToken.None);
            Require(
                stepsAfterB.Count == 3 &&
                stepsAfterB.Select(step => step.FlowStepId)
                    .SequenceEqual(new[] { "step_a", "step_b", "step_c" }),
                "B -> C did not converge to the exact A -> B -> C sequence.");

            var injectedOutbox = await WaitForP601InjectedFailureAsync(
                database,
                instanceId,
                assignmentC,
                CancellationToken.None);

            exchanges.AddRange(api.Exchanges);
            api.Dispose();
            apiClient = null;
            await backend.StopAsync();
            await backend.DisposeAsync();
            backend = null;

            await database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                    "dynamic_flow_runtime_outbox")
                .UpdateOneAsync(
                    item =>
                        item.Id == injectedOutbox.Id &&
                        item.Status == DynamicFlowRuntimeOutboxStatuses.Failed,
                    Builders<DynamicFlowRuntimeOutboxItem>.Update
                        .Set(
                            item => item.NextAttemptAtUtc,
                            DateTime.UtcNow.AddMinutes(30)),
                    cancellationToken: CancellationToken.None);

            var restartedServerRoot = Path.Combine(
                iterationRoot,
                "backend-p6-sequential-restarted");
            Directory.CreateDirectory(restartedServerRoot);
            backend = await BackendServerLease.StartAsync(
                paths,
                restartedServerRoot,
                runKey,
                mongo,
                CancellationToken.None,
                new BackendServerOptions
                {
                    EnableDynamicFlowRuntimeCandidate = true,
                    DynamicFlowRuntimeCandidateActivationThrough = 3
                });
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);

            recoveryEvidence = await AssertP601ReconcileRecoveryAsync(
                api,
                adminToken,
                database,
                instanceId,
                assignmentC,
                injectedOutbox.Id,
                Path.Combine(
                    iterationRoot,
                    "p6-01-recovery-diagnostics.json"),
                CancellationToken.None);

            var stepC = (await LoadP601StepsAsync(
                    database,
                    instanceId,
                    CancellationToken.None))
                .Single(step => step.FlowStepId == "step_c");
            stepC = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                stepC,
                null,
                null,
                "c",
                CancellationToken.None);
            var terminalCapability = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                fixture.WorkId,
                instanceId,
                stepC.Id,
                expectedCanForward: false,
                CancellationToken.None);
            var beforeTerminal = await CaptureP601LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var terminalForward = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                assignmentC,
                "p6-01-terminal-forward",
                terminalCapability.InstanceRevision,
                terminalCapability.StepRevision,
                CancellationToken.None);
            AssertP601Conflict(
                terminalForward,
                "DYNAMIC_FLOW_FORWARD_TERMINAL_STEP",
                "terminal C invalid edge",
                HttpStatusCode.BadRequest);
            Require(
                beforeTerminal == await CaptureP601LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "Terminal-step forward changed the direct-Mongo ledger.");

            await CompleteP601AssignmentAsync(
                api,
                adminToken,
                database,
                stepC,
                CancellationToken.None);
            directMongoEvidence = await AssertP601DirectMongoAsync(
                database,
                fixture,
                instanceId,
                CancellationToken.None);
            replayEvidence = new
            {
                exactReplay = true,
                changedReplayHttpStatus = (int)changedReplay.StatusCode,
                staleRevisionHttpStatus = (int)staleForward.StatusCode,
                concurrentRequests = forwardBResults.Length,
                concurrentExactResults = true,
                outsiderReadHttpStatus = (int)outsiderSteps.StatusCode,
                outsiderForwardHttpStatus = (int)outsiderForward.StatusCode,
                terminalForwardHttpStatus = (int)terminalForward.StatusCode
            };
            passed = true;
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            if (apiClient is not null)
            {
                exchanges.AddRange(apiClient.Exchanges);
                apiClient.Dispose();
                apiClient = null;
            }

            if (backend is not null)
            {
                try
                {
                    await backend.StopAsync();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"backend-stop: {error.Message}");
                }
                await backend.DisposeAsync();
            }

            if (mongo is not null)
            {
                try
                {
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"database-drop: {error.Message}");
                }
                try
                {
                    await mongo.StopProcessAsync();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"mongo-stop: {error.Message}");
                }
                try
                {
                    mongo.RemoveDataDirectoryGuarded();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add($"mongo-data-remove: {error.Message}");
                }
                await mongo.DisposeAsync();
            }
        }

        passed = passed && cleanupErrors.Count == 0;
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-01-sequential-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-01-sequential-direct-mongo.json"),
            new
            {
                runKey,
                directMongoEvidence,
                barrierEvidence,
                replayEvidence,
                recoveryEvidence,
                regressionEvidence,
                branchOwnerEvidence
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "p6-01-sequential-result.json"),
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                candidate = new
                {
                    version = DynamicFlowP6CatalogCandidate.Version,
                    semanticHash = DynamicFlowP6CatalogCandidate.SemanticHash,
                    currentCatalogModified = false,
                    testingActivationOnly =
                        !DynamicFlowP6CatalogCandidate.ActivationEnabled
                },
                requirements = new[]
                {
                    "P6-TOPO-001",
                    "P6-TOPO-002",
                    "P6-TOPO-003",
                    "P6-TOPO-004",
                    "P6-TOPO-015",
                    "P6-TOPO-016",
                    "P6-TOPO-017"
                },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });

        Console.WriteLine(
            passed
                ? $"[PASS] P6-01 sequential Kestrel/Mongo probe passed; artifact={Path.Combine(iterationRoot, "p6-01-sequential-result.json")}"
                : $"[FAIL] P6-01 sequential Kestrel/Mongo probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, "p6-01-sequential-result.json")}");
        return passed ? 0 : 1;
    }

    private static async Task<P601Fixture> SeedP601FixtureAsync(
        IMongoDatabase database,
        ProbeFixture p5Fixture,
        CancellationToken ct)
    {
        var users = database.GetCollection<AppUser>("users");
        var units = database.GetCollection<Unit>("units");
        var admin = await users
            .Find(user => user.Username == "admin" && !user.IsDeleted)
            .SingleAsync(ct);
        var root = await units
            .Find(unit => unit.Id == admin.UnitId && !unit.IsDeleted)
            .SingleAsync(ct);
        var now = new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc);
        var rootForm = p5Fixture.FormPins.Single(pin => pin.Role == "ROOT");
        var childForm = p5Fixture.FormPins.Single(pin => pin.Role == "CHILD");
        var reviewForm = p5Fixture.FormPins.Single(pin => pin.Role == "REVIEWER");

        JsonObject FormNode(ProbeFormPinSnapshot pin)
            => new()
            {
                ["formNodeId"] = pin.FormNodeId,
                ["role"] = pin.Role,
                ["dynamicFormTemplateId"] = pin.FormVersionId,
                ["dynamicFormFamilyId"] = pin.FormFamilyId,
                ["dynamicFormVersionNo"] = pin.FormVersionNo,
                ["dynamicFormSchemaHash"] = pin.FormSchemaHash,
                ["dynamicFormSnapshotHash"] = pin.FormSchemaHash
            };
        JsonObject Step(string nodeId, string code, string formNodeId)
            => new()
            {
                ["nodeId"] = nodeId,
                ["nodeCode"] = code,
                ["nodeKind"] = DynamicFlowNodeKinds.FormStep,
                ["formNodeId"] = formNodeId,
                ["declaredRoles"] = new JsonArray("OWNER")
            };
        JsonObject Edge(string id, string from, string to)
            => new()
            {
                ["transitionId"] = id,
                ["fromNodeId"] = from,
                ["toNodeId"] = to
            };

        var payload = new JsonObject
        {
            ["schemaVersion"] = DynamicFlowDefinitionSchema.CurrentVersion,
            ["archetypeId"] = DynamicFlowSequentialTopologyContract.ArchetypeId,
            ["entryStepId"] = "step_a",
            ["rootDynamicFormTemplateId"] = rootForm.FormVersionId,
            ["resultOwnerStepId"] = "step_c",
            ["resultOwnerFormNodeId"] = reviewForm.FormNodeId,
            ["statisticsOwnerStepId"] = "step_c",
            ["statisticsOwnerFormNodeId"] = reviewForm.FormNodeId,
            ["catalogVersion"] = DynamicFlowP6CatalogCandidate.Version,
            ["catalogSemanticHash"] = DynamicFlowP6CatalogCandidate.SemanticHash,
            ["formNodes"] = new JsonArray(
                FormNode(rootForm),
                FormNode(childForm),
                FormNode(reviewForm)),
            ["nodes"] = new JsonArray(
                Step("step_c", "C", reviewForm.FormNodeId),
                Step("step_a", "A", rootForm.FormNodeId),
                Step("step_b", "B", childForm.FormNodeId)),
            ["edges"] = new JsonArray(
                Edge("tr_a_b", "step_a", "step_b"),
                Edge("tr_b_c", "step_b", "step_c")),
            ["actorPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "p6-sequential-forward",
                    ["stepId"] = "*",
                    ["stepCode"] = "*",
                    ["actorRole"] = "*",
                    ["allowForward"] = true
                }
            },
            ["fieldPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "p6-sequential-fields",
                    ["dynamicFormTemplateId"] = "*",
                    ["stepId"] = "*",
                    ["stepCode"] = "*",
                    ["actorRole"] = "*",
                    ["fieldId"] = "*",
                    ["fieldKey"] = "*",
                    ["read"] = true,
                    ["write"] = true
                }
            },
            ["tableColumnPolicies"] = new JsonArray(),
            ["mappingRules"] = new JsonArray(),
            ["rollbackPolicy"] = new JsonObject(),
            ["finalResultPolicy"] = new JsonObject(),
            ["statisticProfile"] = new JsonObject()
        };
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            payload.ToJsonString(),
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        var topology = DynamicFlowSequentialTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.OrderedNodes.Select(node => node.NodeId)
                .SequenceEqual(new[] { "step_a", "step_b", "step_c" }),
            "Seeded P6-01 topology is not exact A -> B -> C.");

        var familyId = ObjectId.GenerateNewId().ToString();
        var versionId = ObjectId.GenerateNewId().ToString();
        var family = new DynamicFlowTemplate
        {
            Id = familyId,
            Code = "P6_01_FLOW_T03_SEQUENTIAL",
            Name = "P6-01 FLOW-T03 sequential A-B-C",
            FamilyRevision = 1,
            OwnerUserId = admin.Id,
            RootDynamicFormTemplateId = rootForm.FormVersionId,
            Status = DynamicFlowTemplateStatuses.Active,
            CurrentVersionId = versionId,
            CurrentVersionNo = 1,
            CurrentVersionHash = canonical.PayloadHash,
            HasLockedVersion = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };
        var version = new DynamicFlowTemplateVersion
        {
            Id = versionId,
            TemplateId = familyId,
            RootDynamicFormTemplateId = rootForm.FormVersionId,
            VersionNo = 1,
            Status = DynamicFlowTemplateVersionStatuses.Locked,
            DraftRevision = 1,
            SchemaVersion = DynamicFlowDefinitionSchema.CurrentVersion,
            AdapterVersion = DynamicFlowDefinitionSchema.CurrentAdapterVersion,
            CatalogVersion = DynamicFlowP6CatalogCandidate.Version,
            CatalogSemanticHash = DynamicFlowP6CatalogCandidate.SemanticHash,
            PayloadJson = canonical.CanonicalJson,
            PayloadHash = canonical.PayloadHash,
            DefinitionLockable = true,
            ExecutionEligibility = DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase,
            ExecutionBlockedReason =
                DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented,
            BlockedUntilPhase = "P6",
            MigrationState = DynamicFlowDefinitionMigrationStates.Canonical,
            LockedAtUtc = now,
            LockedByUserId = admin.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };
        var work = new Work
        {
            Id = ObjectId.GenerateNewId().ToString(),
            AutoCode = "P6-01-T03-SEQUENTIAL",
            Code = "P6-01-T03",
            Name = "P6-01 sequential integration work",
            Status = WorkStatus.S1,
            Type = WorkType.TASK,
            Priority = WorkPriority.MEDIUM,
            LeaderDirectiveUserId = admin.Id,
            LeaderWatchUserIds = [],
            Owner = new UserRef
            {
                UserId = admin.Id,
                Username = admin.Username,
                FullName = admin.FullName,
                UnitId = admin.UnitId,
                UnitSymbol = root.Symbol,
                UnitShortName = root.ShortName,
                UnitName = root.FullName,
                PositionCode = admin.PositionCode
            },
            LeaderDirective = new UserRef
            {
                UserId = admin.Id,
                Username = admin.Username,
                FullName = admin.FullName,
                UnitId = admin.UnitId,
                UnitSymbol = root.Symbol,
                UnitShortName = root.ShortName,
                UnitName = root.FullName,
                PositionCode = admin.PositionCode
            },
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };
        var ownerOracleWork = new Work
        {
            Id = ObjectId.GenerateNewId().ToString(),
            AutoCode = "P6-01-T03-OWNER-ORACLE",
            Code = "P6-01-T03-OWNER",
            Name = "P6-01 two-target branch owner oracle",
            Status = WorkStatus.S1,
            Type = WorkType.TASK,
            Priority = WorkPriority.MEDIUM,
            LeaderDirectiveUserId = admin.Id,
            LeaderWatchUserIds = [],
            Owner = work.Owner,
            LeaderDirective = work.LeaderDirective,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            IsDeleted = false
        };
        await database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .InsertOneAsync(family, cancellationToken: ct);
        await database
            .GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .InsertOneAsync(version, cancellationToken: ct);
        await database.GetCollection<Work>("works")
            .InsertManyAsync([work, ownerOracleWork], cancellationToken: ct);

        // Once CURRENT advances to the P7 successor, keep the frozen v1.2
        // barrier fixtures historical so each P6 prompt-local activation
        // range remains observable without weakening production activation.
        if (!DynamicFlowP6CatalogCandidate.ActivationEnabled &&
            !DynamicFlowP7CatalogCandidate.ActivationEnabled)
        {
            foreach (var barrier in p5Fixture.BarrierFixtures
                         .Where(item => item.ArchetypeId is not "FLOW-T03"))
            {
                await PinP601BarrierVersionAsync(database, barrier.VersionId, ct);
            }
        }

        var targetUser = await users
            .Find(user =>
                user.UnitId == p5Fixture.TargetUnitIds[0] &&
                !user.IsDeleted)
            .SingleAsync(ct);
        var outsiderUser = await users
            .Find(user =>
                user.UnitId == p5Fixture.TargetUnitIds[1] &&
                !user.IsDeleted)
            .SingleAsync(ct);
        return new P601Fixture(
            work.Id,
            ownerOracleWork.Id,
            family.Id,
            version.Id,
            canonical.CanonicalJson,
            canonical.PayloadHash,
            p5Fixture.TargetUnitIds[0],
            targetUser.Id,
            outsiderUser.Id,
            new[]
            {
                new P601ExpectedStep(
                    "step_a",
                    "A",
                    rootForm,
                    null,
                    new[] { "step_b" },
                    false),
                new P601ExpectedStep(
                    "step_b",
                    "B",
                    childForm,
                    "tr_a_b",
                    new[] { "step_c" },
                    false),
                new P601ExpectedStep(
                    "step_c",
                    "C",
                    reviewForm,
                    "tr_b_c",
                    Array.Empty<string>(),
                    true)
            });
    }

    private static async Task PinP601BarrierVersionAsync(
        IMongoDatabase database,
        string versionId,
        CancellationToken ct)
    {
        var versions = database.GetCollection<DynamicFlowTemplateVersion>(
            "dynamic_flow_template_versions");
        var families = database.GetCollection<DynamicFlowTemplate>(
            "dynamic_flow_templates");
        var version = await versions.Find(item => item.Id == versionId).SingleAsync(ct);
        var payload = JsonNode.Parse(version.PayloadJson)?.AsObject()
                      ?? throw new InvalidOperationException(
                          $"Barrier version {versionId} payload is invalid.");
        payload["catalogVersion"] = DynamicFlowP6CatalogCandidate.Version;
        payload["catalogSemanticHash"] = DynamicFlowP6CatalogCandidate.SemanticHash;
        var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
            payload.ToJsonString(),
            new DynamicFlowDefinitionValidationOptions(
                AllowLegacy: false,
                AllowServerManagedPins: true,
                RequireServerManagedPins: true,
                AllowHistoricalCatalogPins: true));
        await versions.UpdateOneAsync(
            item => item.Id == version.Id,
            Builders<DynamicFlowTemplateVersion>.Update
                .Set(item => item.PayloadJson, canonical.CanonicalJson)
                .Set(item => item.PayloadHash, canonical.PayloadHash)
                .Set(item => item.CatalogVersion, DynamicFlowP6CatalogCandidate.Version)
                .Set(
                    item => item.CatalogSemanticHash,
                    DynamicFlowP6CatalogCandidate.SemanticHash)
                .Set(
                    item => item.ExecutionEligibility,
                    DynamicFlowExecutionEligibilities.BlockedUntilTargetPhase)
                .Set(
                    item => item.ExecutionBlockedReason,
                    DynamicFlowExecutionBlockedReasons.TargetPhaseNotImplemented)
                .Set(item => item.BlockedUntilPhase, "P6"),
            cancellationToken: ct);
        await families.UpdateOneAsync(
            item => item.Id == version.TemplateId,
            Builders<DynamicFlowTemplate>.Update
                .Set(item => item.CurrentVersionHash, canonical.PayloadHash),
            cancellationToken: ct);
    }

    private static async Task<object> AssertP601P5RegressionAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        var targets = fixture.TargetUnitIds.Take(1).ToArray();
        var t01 = await LaunchAsync(
            api,
            adminToken,
            fixture,
            fixture.T01WorkId,
            fixture.T01VersionId,
            "p6-01-regression-t01",
            targets,
            "2026-07-P601-T01",
            ct);
        RequireStatus(t01.Confirm, "SUCCEEDED", "P6-01 P5 FLOW-T01 regression");
        var t01State = await ValidateConvergedAsync(
            database,
            fixture,
            fixture.T01WorkId,
            "p6-01-regression-t01",
            fixture.T01VersionId,
            "FLOW-T01",
            targets,
            "2026-07-P601-T01",
            false,
            ct);

        var t02 = await LaunchAsync(
            api,
            adminToken,
            fixture,
            fixture.T02WorkId,
            fixture.T02VersionId,
            "p6-01-regression-t02",
            targets,
            "2026-07-P601-T02",
            ct);
        RequireStatus(t02.Confirm, "SUCCEEDED", "P6-01 P5 FLOW-T02 regression");
        var t02State = await ValidateConvergedAsync(
            database,
            fixture,
            fixture.T02WorkId,
            "p6-01-regression-t02",
            fixture.T02VersionId,
            "FLOW-T02",
            targets,
            "2026-07-P601-T02",
            false,
            ct);
        return new
        {
            t01 = new { passed = true, t01State.FlowInstanceId, t01State.StepCount },
            t02 = new { passed = true, t02State.FlowInstanceId, t02State.StepCount },
            catalogVersion = DynamicFlowRuntimeCatalogCandidate.Version
        };
    }

    private static async Task<object> AssertP601BranchOwnerIdentityOracleAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        ProbeFixture p5Fixture,
        P601Fixture fixture,
        CancellationToken ct)
    {
        var targetUnitIds = p5Fixture.TargetUnitIds
            .Take(2)
            .ToArray();
        Require(
            targetUnitIds.Length == 2 &&
            targetUnitIds.Distinct(StringComparer.Ordinal).Count() == 2,
            "P6-01 branch-owner oracle requires two distinct target units.");
        var launch = await LaunchAsync(
            api,
            adminToken,
            p5Fixture,
            fixture.OwnerOracleWorkId,
            fixture.VersionId,
            "p6-01-owner-branch-oracle",
            targetUnitIds,
            "2026-07-P601-OWNER",
            ct);
        RequireStatus(
            launch.Confirm,
            "SUCCEEDED",
            "P6-01 two-target branch-owner launch");
        var instanceId = ApiHarnessClient.RequiredString(
            launch.Confirm.Json,
            "flowInstanceId");
        var instance = await LoadP601InstanceAsync(database, instanceId, ct);
        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        Require(
            steps.Count == 2 &&
            steps.All(step =>
                step.FlowStepId == "step_a" &&
                step.State == DynamicFlowStepStates.Assigned &&
                step.AssignmentId is not null) &&
            steps.Select(step => step.TargetUnitId)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(targetUnitIds),
            "P6-01 two-target oracle did not materialize one entry branch per unit.");

        var expectedRows = targetUnitIds
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(targetUnitId =>
            {
                var branchId =
                    DynamicFlowSequentialTopologyContract.BuildRootBranchId(
                        instance.Id,
                        instance.ExecutionEpoch,
                        targetUnitId);
                var resultOwner =
                    DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                        instance.Id,
                        instance.ExecutionEpoch,
                        "step_c",
                        branchId,
                        "result");
                var statisticOwner =
                    DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                        instance.Id,
                        instance.ExecutionEpoch,
                        "step_c",
                        branchId,
                        "statistics");
                return new
                {
                    targetUnitId,
                    branchId,
                    resultOwner,
                    statisticOwner
                };
            })
            .ToArray();
        foreach (var expected in expectedRows)
        {
            var step = steps.Single(item =>
                item.TargetUnitId == expected.targetUnitId);
            Require(
                step.BranchId == expected.branchId &&
                step.ResultOwnerIdentity == expected.resultOwner &&
                step.StatisticOwnerIdentity == expected.statisticOwner,
                $"P6-01 branch-scoped owner identity drifted for unit {expected.targetUnitId}.");
        }
        Require(
            expectedRows.Select(row => row.resultOwner)
                .Distinct(StringComparer.Ordinal)
                .Count() == 2 &&
            expectedRows.Select(row => row.statisticOwner)
                .Distinct(StringComparer.Ordinal)
                .Count() == 2,
            "P6-01 branch owner identities collided across two target units.");
        return new
        {
            instanceId,
            targetCount = targetUnitIds.Length,
            branchCount = steps.Select(step => step.BranchId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            rows = expectedRows,
            resultOwnerIdentitiesDistinct = true,
            statisticOwnerIdentitiesDistinct = true
        };
    }

    private static async Task<object> AssertP601FutureBarrierAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var barrier in fixture.BarrierFixtures
                     .Where(item => item.ArchetypeId is not "FLOW-T03")
                     .OrderBy(item => item.ArchetypeId, StringComparer.Ordinal))
        {
            var commandId = $"p6-01-blocked-{barrier.ArchetypeId.ToLowerInvariant()}";
            var request = new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = barrier.VersionId,
                CommandId = commandId,
                TargetUnitIds = [fixture.TargetUnitIds[0]],
                PeriodKey = $"2026-07-{barrier.ArchetypeId}",
                ScheduleIdentityJson =
                    "{\"timezone\":\"Asia/Ho_Chi_Minh\",\"cadence\":\"once\",\"anchor\":\"2026-07-27\"}"
            };
            var before = await CaptureP601BusinessCountsAsync(database, ct);
            var preflight = await api.PostAsync(
                $"api/works/{barrier.WorkId}/dynamic-flows/preflight",
                request,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                preflight,
                HttpStatusCode.OK,
                $"{barrier.ArchetypeId} blocked preflight");
            Require(
                ApiHarnessClient.RequiredString(preflight.Json, "eligibility") ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
                $"{barrier.ArchetypeId} must remain P6 phase-blocked.");
            var confirm = await api.PostAsync(
                $"api/works/{barrier.WorkId}/dynamic-flows/confirm",
                new DynamicFlowConfirmRequest
                {
                    FlowTemplateVersionId = request.FlowTemplateVersionId,
                    CommandId = request.CommandId,
                    TargetUnitIds = request.TargetUnitIds,
                    PeriodKey = request.PeriodKey,
                    ScheduleIdentityJson = request.ScheduleIdentityJson,
                    SnapshotToken = ApiHarnessClient.RequiredString(
                        preflight.Json,
                        "snapshotToken")
                },
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                confirm,
                HttpStatusCode.OK,
                $"{barrier.ArchetypeId} blocked confirm");
            Require(
                ApiHarnessClient.RequiredString(confirm.Json, "status") ==
                "BLOCKED_UNTIL_TARGET_PHASE" &&
                !ApiHarnessClient.RequiredBool(confirm.Json, "businessWritePerformed"),
                $"{barrier.ArchetypeId} confirm crossed the P6-01 write boundary.");
            var after = await CaptureP601BusinessCountsAsync(database, ct);
            Require(
                before.All(pair =>
                    after.TryGetValue(pair.Key, out var count) &&
                    count == pair.Value),
                $"{barrier.ArchetypeId} blocked preflight/confirm changed business counts.");
            rows.Add(new
            {
                barrier.ArchetypeId,
                catalogVersion =
                    DynamicFlowP6CatalogCandidate.ActivationEnabled ||
                    DynamicFlowP7CatalogCandidate.ActivationEnabled
                        ? DynamicFlowRuntimeCatalogCandidate.Version
                        : DynamicFlowP6CatalogCandidate.Version,
                preflightEligibility = DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
                confirmStatus = "BLOCKED_UNTIL_TARGET_PHASE",
                businessWritePerformed = false,
                directMongoZeroWrite = true
            });
        }
        Require(rows.Count == 9, "P6-01 must verify the complete T04..T12 barrier.");
        return new { count = rows.Count, rows };
    }

    private static async Task<DynamicFlowStepInstance> ApproveP601StepAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        string adminToken,
        IMongoDatabase database,
        DynamicFlowStepInstance step,
        string? fieldId,
        string? fieldValue,
        string suffix,
        CancellationToken ct)
    {
        var assignmentId = step.AssignmentId
                           ?? throw new InvalidOperationException(
                               $"Step {step.Id} lacks assignment.");
        var period = await WaitForP601PeriodAsync(database, assignmentId, ct);
        var participantId = step.ParticipantUserIds.Single();
        var participantToken = await PrepareP601ActorLoginAsync(
            api,
            backend,
            database,
            participantId,
            ct);
        var open = await api.PostAsync(
            $"api/work-report-periods/{period.Id}/open",
            body: null,
            participantToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(open, HttpStatusCode.OK, $"P6-01 open report {suffix}");
        var reportId = ApiHarnessClient.RequiredString(open.Json, "id");
        var fieldValuesJson =
            !string.IsNullOrWhiteSpace(fieldId) &&
            !string.IsNullOrWhiteSpace(fieldValue)
                ? new JsonObject
                {
                    ["values"] = new JsonObject { [fieldId] = fieldValue }
                }.ToJsonString()
                : null;
        var submit = await api.PostAsync(
            $"api/work-assignment-reports/{reportId}/submit",
            new JsonObject
            {
                ["expectedPayloadRevision"] =
                    ApiHarnessClient.RequiredInt(open.Json, "payloadRevision"),
                ["expectedLifecycleRevision"] =
                    ApiHarnessClient.RequiredInt(open.Json, "lifecycleRevision"),
                ["commandId"] = $"p6-01-submit-{suffix}",
                ["values1D"] = new JsonArray(0),
                ["fieldValuesJson"] = fieldValuesJson,
                ["tableValuesJson"] = null,
                ["dataOrigin"] = "MANUAL_INPUT",
                ["cumulativeContributionMode"] = "INCLUDE"
            },
            participantToken,
            ct: ct);
        Require(
            submit.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
            $"P6-01 submit {suffix} expected HTTP 200/202, got {(int)submit.StatusCode}; " +
            $"error={ApiHarnessClient.FindStringRecursive(submit.Json, "errorCode") ?? ApiHarnessClient.FindStringRecursive(submit.Json, "code") ?? "<none>"}; " +
            $"reason={ApiHarnessClient.FindStringRecursive(submit.Json, "reason") ?? "<none>"}; " +
            $"scope={ApiHarnessClient.FindStringRecursive(submit.Json, "scope") ?? "<none>"}; " +
            $"field={ApiHarnessClient.FindStringRecursive(submit.Json, "fieldName") ?? "<none>"}.");
        await WaitForP601StepStateAsync(
            api,
            adminToken,
            database,
            step.Id,
            DynamicFlowStepStates.Submitted,
            ct);
        var report = await database
            .GetCollection<WorkAssignmentReport>("work_assignment_report")
            .Find(item => item.Id == reportId && !item.IsDeleted)
            .SingleAsync(ct);
        var approve = await api.PostAsync(
            $"api/work-assignment-review/reports/{reportId}/approve",
            new JsonObject
            {
                ["expectedPayloadRevision"] = report.PayloadRevision,
                ["expectedLifecycleRevision"] = report.LifecycleRevision,
                ["commandId"] = $"p6-01-approve-{suffix}",
                ["comment"] = $"P6-01 approve form {suffix.ToUpperInvariant()}",
                ["confirmHistoricalDataApproval"] = false
            },
            adminToken,
            ct: ct);
        Require(
            approve.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
            $"P6-01 approve {suffix} expected HTTP 200/202, got {(int)approve.StatusCode}.");
        return await WaitForP601StepStateAsync(
            api,
            adminToken,
            database,
            step.Id,
            DynamicFlowStepStates.Approved,
            ct);
    }

    private static async Task<DynamicFlowStepInstance> WaitForP601StepStateAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        string stepId,
        string expectedState,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var step = await database.GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(item => item.Id == stepId && !item.IsDeleted)
                .SingleAsync(ct);
            if (step.State == expectedState)
                return step;
            var worker = await api.PostAsync(
                "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
                body: null,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                worker,
                HttpStatusCode.OK,
                $"P6-01 lifecycle worker for {expectedState}");
            await Task.Delay(50, ct);
        }
        var terminal = await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => item.Id == stepId && !item.IsDeleted)
            .SingleAsync(ct);
        throw new InvalidOperationException(
            $"Step {stepId} did not reach {expectedState}; actual={terminal.State}.");
    }

    private static async Task<string> PrepareP601ActorLoginAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        IMongoDatabase database,
        string actorId,
        CancellationToken ct)
    {
        var users = database.GetCollection<AppUser>("users");
        var actor = await users
            .Find(user => user.Id == actorId && !user.IsDeleted)
            .SingleAsync(ct);
        var passwordHash = new PasswordHasher<AppUser>()
            .HashPassword(actor, backend.ActorPassword);
        await users.UpdateOneAsync(
            user => user.Id == actor.Id && !user.IsDeleted,
            Builders<AppUser>.Update
                .Set(user => user.PasswordHash, passwordHash)
                .Set(user => user.UpdatedAtUtc, DateTime.UtcNow),
            cancellationToken: ct);
        return await api.LoginAsync(actor.Username, backend.ActorPassword, ct);
    }

    private static async Task CompleteP601AssignmentAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        DynamicFlowStepInstance step,
        CancellationToken ct)
    {
        var assignmentId = step.AssignmentId
                           ?? throw new InvalidOperationException(
                               $"Step {step.Id} lacks assignment.");
        var response = await api.PostAsync(
            $"api/work-assignments/{assignmentId}/complete",
            new JsonObject
            {
                ["completedDate"] = "2026-07-27T00:00:00Z",
                ["note"] = "P6-01 terminal C completion"
            },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            "P6-01 terminal assignment completion");
        await WaitForP601StepStateAsync(
            api,
            adminToken,
            database,
            step.Id,
            DynamicFlowStepStates.Completed,
            ct);
    }

    private static Task<ApiHarnessResponse> ForwardP601Async(
        ApiHarnessClient api,
        string token,
        string workId,
        string assignmentId,
        string commandId,
        long instanceRevision,
        long stepRevision,
        CancellationToken ct)
        => api.PostAsync(
            $"api/works/{workId}/dynamic-flows/assignments/{assignmentId}/forward",
            new DynamicFlowBranchActionRequest
            {
                CommandId = commandId,
                ExpectedInstanceRevision = instanceRevision,
                ExpectedStepRevision = stepRevision,
                Reason = "P6-01 sequential integration probe"
            },
            token,
            ct: ct);

    private static void AssertP601ForwardSuccess(
        ApiHarnessResponse response,
        bool? replayed,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, context);
        Require(
            ApiHarnessClient.RequiredString(response.Json, "status") ==
            "ACCEPTED_PENDING_MATERIALIZATION",
            $"{context} returned an unexpected status.");
        Require(
            ApiHarnessClient.RequiredBool(response.Json, "businessWritePerformed"),
            $"{context} did not report its durable aggregate write.");
        if (replayed.HasValue)
        {
            Require(
                ApiHarnessClient.RequiredBool(response.Json, "replayed") == replayed.Value,
                $"{context} replay flag drifted.");
        }
    }

    private static void AssertP601SameForwardResult(
        ApiHarnessResponse left,
        ApiHarnessResponse right,
        string context)
    {
        var properties = new[]
        {
            "commandId",
            "status",
            "flowInstanceId",
            "stepInstanceId",
            "nextStepInstanceId",
            "nextAssignmentId",
            "activatedTransitionId",
            "eventId"
        };
        Require(
            properties.All(property =>
                ApiHarnessClient.RequiredString(left.Json, property) ==
                ApiHarnessClient.RequiredString(right.Json, property)),
            $"{context} changed its semantic result.");
        Require(
            RequiredP601Long(left.Json, "instanceRevision") ==
            RequiredP601Long(right.Json, "instanceRevision"),
            $"{context} changed its instance revision result.");
    }

    private static void AssertP601Conflict(
        ApiHarnessResponse response,
        string expectedReason,
        string context,
        HttpStatusCode expectedStatus = HttpStatusCode.Conflict)
    {
        ApiHarnessClient.ExpectStatus(response, expectedStatus, context);
        var actual = ApiHarnessClient.FindStringRecursive(response.Json, "reason") ??
                     ApiHarnessClient.FindStringRecursive(response.Json, "errorCode") ??
                     ApiHarnessClient.FindStringRecursive(response.Json, "code");
        Require(
            actual == expectedReason,
            $"{context} expected {expectedReason}, got {actual ?? "<missing>"}.");
    }

    private static async Task<P601Capability> ReadP601ForwardCapabilityAsync(
        ApiHarnessClient api,
        string token,
        string workId,
        string instanceId,
        string stepId,
        bool expectedCanForward,
        CancellationToken ct)
    {
        var overview = await api.GetAsync(
            $"api/works/{workId}/dynamic-flows/instances/{instanceId}",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            overview,
            HttpStatusCode.OK,
            "P6-01 instance overview");
        var steps = await api.GetAsync(
            $"api/works/{workId}/dynamic-flows/instances/{instanceId}/steps?limit=50",
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(steps, HttpStatusCode.OK, "P6-01 step read");
        var body = ApiHarnessClient.RequiredObject(steps.Json, "P6-01 step page");
        var row = ApiHarnessClient.RequiredArray(body["items"], "P6-01 step items")
            .Select(item => ApiHarnessClient.RequiredObject(item, "P6-01 step item"))
            .Single(item =>
                ApiHarnessClient.RequiredString(item, "stepInstanceId") == stepId);
        var capabilities = ApiHarnessClient.RequiredObject(
            row["capabilities"],
            "P6-01 step capabilities");
        Require(
            ApiHarnessClient.RequiredBool(capabilities, "canForward") ==
            expectedCanForward,
            $"Server-derived canForward drifted for step {stepId}.");
        return new P601Capability(
            RequiredP601Long(overview.Json, "revision"),
            RequiredP601Long(row, "revision"));
    }

    private static async Task<object> AssertP601ReconcileRecoveryAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        string instanceId,
        string assignmentId,
        string outboxId,
        string diagnosticsPath,
        CancellationToken ct)
    {
        var outboxCollection = database.GetCollection<DynamicFlowRuntimeOutboxItem>(
            "dynamic_flow_runtime_outbox");
        var candidate = await outboxCollection
            .Find(item => item.Id == outboxId)
            .SingleAsync(ct);
        Require(
            candidate.FlowInstanceId == instanceId &&
            candidate.Operation ==
            DynamicFlowRuntimeMaterializationOperations
                .MaterializeSequentialAssignment &&
            candidate.Payload.TryGetValue("commandId", out var commandId) &&
            commandId.IsString &&
            commandId.AsString == P601ForwardBCommand &&
            candidate.Status == DynamicFlowRuntimeOutboxStatuses.Failed &&
            candidate.AttemptCount == 1 &&
            candidate.LastErrorCode == "DYNAMIC_FLOW_RUNTIME_INJECTED_FAILURE" &&
            candidate.CompletedAtUtc is null,
            "Restart did not preserve the real injected outbox failure.");
        var beforeStepCount = await database
            .GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
            .CountDocumentsAsync(
                item => item.FlowInstanceId == instanceId && !item.IsDeleted,
                cancellationToken: ct);
        var beforeAssignmentCount = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(
                item => item.FlowInstanceId == instanceId && !item.IsDeleted,
                cancellationToken: ct);
        var assignmentObjectId = ObjectId.Parse(assignmentId);
        var beforeBindingCount = await database
            .GetCollection<BsonDocument>("work_template_assignees")
            .CountDocumentsAsync(
                new BsonDocument("workAssignmentId", assignmentObjectId),
                cancellationToken: ct);
        var beforePeriodCount = await database
            .GetCollection<BsonDocument>("work_report_periods")
            .CountDocumentsAsync(
                new BsonDocument("workAssignmentId", assignmentObjectId),
                cancellationToken: ct);
        Require(
            beforeStepCount == 3 &&
            beforeAssignmentCount == 3 &&
            beforeBindingCount == 0 &&
            beforePeriodCount == 0,
            "Restarted recovery fixture no longer represents an AfterAssignmentWrite partial side effect.");

        var operationPath =
            $"api/admin/operations/dynamic-flow-runtime/instances/{instanceId}/reconcile";
        var preview = await api.PostAsync(
            $"{operationPath}?apply=false",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            preview,
            HttpStatusCode.OK,
            "P6-01 recovery dry-run");
        Require(
            !ApiHarnessClient.RequiredBool(preview.Json, "converged"),
            "P6-01 recovery dry-run did not detect sequential outbox drift.");
        var applyAttempts = new List<object>();
        ApiHarnessResponse? applied = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await api.PostAsync(
                $"{operationPath}?apply=true",
                new { },
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.OK,
                $"P6-01 recovery apply {attempt}");
            var converged = ApiHarnessClient.RequiredBool(response.Json, "converged");
            var exact = ApiHarnessClient.RequiredBool(
                response.Json,
                "exactLedgerConverged");
            applyAttempts.Add(new { attempt, converged, exact });
            if (converged && exact)
            {
                applied = response;
                break;
            }
        }
        if (applied is null)
        {
            await EvidenceJson.WriteAsync(
                diagnosticsPath,
                await CaptureP601RecoveryDiagnosticsAsync(
                    database,
                    instanceId,
                    applyAttempts,
                    ct),
                ct);
            throw new InvalidOperationException(
                "P6-01 recovery apply did not converge.");
        }
        var repaired = await outboxCollection
            .Find(item => item.Id == candidate.Id)
            .SingleAsync(ct);
        var afterStepCount = await database
            .GetCollection<DynamicFlowStepInstance>("dynamic_flow_step_instances")
            .CountDocumentsAsync(
                item => item.FlowInstanceId == instanceId && !item.IsDeleted,
                cancellationToken: ct);
        var afterAssignmentCount = await database
            .GetCollection<WorkAssignment>("work_assignments")
            .CountDocumentsAsync(
                item => item.FlowInstanceId == instanceId && !item.IsDeleted,
                cancellationToken: ct);
        var afterBindingCount = await database
            .GetCollection<BsonDocument>("work_template_assignees")
            .CountDocumentsAsync(
                new BsonDocument("workAssignmentId", assignmentObjectId),
                cancellationToken: ct);
        var afterPeriodCount = await database
            .GetCollection<BsonDocument>("work_report_periods")
            .CountDocumentsAsync(
                new BsonDocument("workAssignmentId", assignmentObjectId),
                cancellationToken: ct);
        var repairedStep = await database
            .GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => item.Id == candidate.StepInstanceId && !item.IsDeleted)
            .SingleAsync(ct);
        var repairedInstance = await LoadP601InstanceAsync(database, instanceId, ct);
        Require(
            repaired.Status == DynamicFlowRuntimeOutboxStatuses.Completed &&
            repaired.CompletedAtUtc.HasValue &&
            repaired.LastErrorCode is null &&
            beforeStepCount == afterStepCount &&
            beforeAssignmentCount == afterAssignmentCount &&
            afterBindingCount == 1 &&
            afterPeriodCount == 1 &&
            repairedStep.State == DynamicFlowStepStates.Assigned &&
            repairedStep.ResumeState is null &&
            repairedStep.LastErrorCode is null &&
            repairedInstance.State == DynamicFlowInstanceStates.Active &&
            repairedInstance.ResumeState is null &&
            repairedInstance.LastErrorCode is null,
            "P6-01 recovery created duplicates or left outbox unconverged.");
        var replay = await api.PostAsync(
            $"{operationPath}?apply=true",
            new { },
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            replay,
            HttpStatusCode.OK,
            "P6-01 recovery replay");
        Require(
            ApiHarnessClient.RequiredBool(replay.Json, "converged") &&
            ApiHarnessClient.RequiredBool(replay.Json, "exactLedgerConverged"),
            "P6-01 recovery replay did not remain converged.");
        return new
        {
            faultInjected = true,
            faultPoint = DynamicFlowRuntimeFaultPoints.AfterAssignmentWrite,
            injectedErrorCode = candidate.LastErrorCode,
            injectedOutboxId = candidate.Id,
            serverRestarted = true,
            retryScheduleParkedForDeterministicReconcile = true,
            partialBoundary = new
            {
                assignmentCount = 1,
                bindingCount = beforeBindingCount,
                periodCount = beforePeriodCount
            },
            dryRunDetected = true,
            applyConverged = true,
            applyAttempts,
            replayConverged = true,
            stepCount = afterStepCount,
            assignmentCount = afterAssignmentCount,
            bindingCount = afterBindingCount,
            periodCount = afterPeriodCount,
            duplicateCount = 0
        };
    }

    private static async Task<object> CaptureP601RecoveryDiagnosticsAsync(
        IMongoDatabase database,
        string instanceId,
        IReadOnlyList<object> applyAttempts,
        CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(database, instanceId, ct);
        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        var events = await database.GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.CreatedAtUtc)
            .ToListAsync(ct);
        var outbox = await database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.CreatedAtUtc)
            .ToListAsync(ct);
        return new
        {
            instance = new
            {
                instance.Id,
                instance.State,
                instance.ResumeState,
                instance.Revision,
                instance.NextEventSequence,
                instance.LastErrorCode
            },
            steps = steps.Select(step => new
            {
                step.Id,
                step.FlowStepId,
                step.BranchId,
                step.AssignmentId,
                step.State,
                step.ResumeState,
                step.Revision,
                step.LastErrorCode
            }).ToArray(),
            events = events.Select(item => new
            {
                item.Sequence,
                item.Id,
                item.EventType,
                item.StepInstanceId,
                item.ExecutionEpoch,
                item.BranchId,
                item.AttemptNo,
                item.CommandId,
                item.SourceEventKey,
                item.FromState,
                item.ToState,
                item.FromRevision,
                item.ToRevision,
                item.ReasonCode,
                item.AffectedRefs,
                item.VisibleUnitIds,
                payload = ApiHarnessClient.RedactJson(
                    item.Payload.ToJson()),
                item.PayloadHash
            }).ToArray(),
            receipts = receipts.Select(item => new
            {
                item.Id,
                item.ScopeKind,
                item.CommandType,
                item.CommandId,
                item.ExpectedRevision,
                item.Status,
                resultSnapshot = ApiHarnessClient.RedactJson(
                    item.ResultSnapshot?.ToJson()),
                item.ResultSnapshotHash,
                item.ErrorCode,
                item.CompletedAtUtc
            }).ToArray(),
            outbox = outbox.Select(item => new
            {
                item.Id,
                item.StepInstanceId,
                item.Operation,
                item.Status,
                item.AttemptCount,
                item.RepairEpoch,
                item.LastErrorCode,
                item.CompletedAtUtc,
                payload = ApiHarnessClient.RedactJson(
                    item.Payload.ToJson())
            }).ToArray(),
            applyAttempts
        };
    }

    private static async Task<object> AssertP601DirectMongoAsync(
        IMongoDatabase database,
        P601Fixture fixture,
        string instanceId,
        CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(database, instanceId, ct);
        var topology = DynamicFlowSequentialTopologyContract.Require(
            instance.TopologySnapshotJson,
            instance.TopologySnapshotHash);
        var expectedScheduleIdentityHash =
            DynamicFlowSequentialTopologyContract.Hash(
                P601ScheduleIdentityJson);
        Require(
            instance.WorkId == fixture.WorkId &&
            instance.FlowTemplateId == fixture.FamilyId &&
            instance.FlowTemplateVersionId == fixture.VersionId &&
            instance.CatalogVersion == DynamicFlowP6CatalogCandidate.Version &&
            instance.CatalogSemanticHash == DynamicFlowP6CatalogCandidate.SemanticHash &&
            instance.ArchetypeId == DynamicFlowSequentialTopologyContract.ArchetypeId &&
            instance.ExecutionEpoch == DynamicFlowSequentialTopologyContract.InitialExecutionEpoch &&
            instance.FlowPayloadHash == fixture.PayloadHash &&
            instance.TopologySnapshotHash == fixture.PayloadHash &&
            instance.TopologySnapshotJson == fixture.CanonicalPayload &&
            instance.ScheduleIdentityJson == P601ScheduleIdentityJson &&
            instance.ScheduleIdentityHash == expectedScheduleIdentityHash &&
            instance.DefinitionRevision ==
            DynamicFlowSequentialTopologyContract.BuildDefinitionRevision(
                fixture.VersionId,
                1,
                fixture.PayloadHash),
            "P6-01 instance immutable pins drifted.");
        Require(
            topology.OrderedNodes.Select(node => node.NodeId)
                .SequenceEqual(new[] { "step_a", "step_b", "step_c" }),
            "P6-01 durable topology snapshot drifted.");

        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        var assignments = await database.GetCollection<WorkAssignment>("work_assignments")
            .Find(item => item.FlowInstanceId == instanceId && !item.IsDeleted)
            .ToListAsync(ct);
        Require(
            steps.Count == 3 &&
            assignments.Count == 3 &&
            steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() == 3 &&
            assignments.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == 3,
            "P6-01 duplicate/orphan step or assignment cardinality.");
        var branchId = DynamicFlowSequentialTopologyContract.BuildRootBranchId(
            instance.Id,
            instance.ExecutionEpoch,
            fixture.TargetUnitId);
        foreach (var expected in fixture.ExpectedSteps)
        {
            var step = steps.Single(item => item.FlowStepId == expected.NodeId);
            var expectedStepId = DynamicFlowSequentialTopologyContract.BuildStepInstanceId(
                instance.Id,
                instance.ExecutionEpoch,
                expected.NodeId,
                branchId,
                1);
            var expectedAssignmentId =
                DynamicFlowSequentialTopologyContract.BuildAssignmentId(expectedStepId);
            var expectedResultOwnerIdentity =
                DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                    instance.Id,
                    instance.ExecutionEpoch,
                    "step_c",
                    branchId,
                    "result");
            var expectedStatisticOwnerIdentity =
                DynamicFlowSequentialTopologyContract.BuildStepOwnerIdentity(
                    instance.Id,
                    instance.ExecutionEpoch,
                    "step_c",
                    branchId,
                    "statistics");
            var assignment = assignments.Single(item => item.Id == expectedAssignmentId);
            Require(
                step.Id == expectedStepId &&
                step.AssignmentId == expectedAssignmentId &&
                step.BranchId == branchId &&
                step.ExecutionEpoch == instance.ExecutionEpoch &&
                step.AttemptNo == 1 &&
                step.DefinitionRevision == instance.DefinitionRevision &&
                step.FlowStepCode == expected.NodeCode &&
                step.FormNodeId == expected.Form.FormNodeId &&
                step.FormFamilyId == expected.Form.FormFamilyId &&
                step.FormVersionId == expected.Form.FormVersionId &&
                step.FormVersionNo == expected.Form.FormVersionNo &&
                step.FormSchemaHash == expected.Form.FormSchemaHash &&
                step.FormSnapshotHash == expected.Form.FormSchemaHash &&
                step.ActivatedByTransitionId == expected.ActivatedByTransitionId &&
                step.NextNodeIds.SequenceEqual(expected.NextNodeIds, StringComparer.Ordinal) &&
                step.IsTerminalNode == expected.IsTerminalNode &&
                step.ResultOwnerIdentity == expectedResultOwnerIdentity &&
                step.StatisticOwnerIdentity == expectedStatisticOwnerIdentity &&
                assignment.FlowInstanceId == instance.Id &&
                assignment.FlowStepId == step.FlowStepId &&
                assignment.FlowBranchId == branchId &&
                assignment.FlowAttemptNo == 1 &&
                assignment.DynamicFormTemplateId == step.FormVersionId &&
                assignment.DynamicFormFamilyId == step.FormFamilyId &&
                assignment.DynamicFormVersionNo == step.FormVersionNo &&
                assignment.DynamicFormSchemaHash == step.FormSchemaHash &&
                assignment.IsFlowFinalNode == step.IsTerminalNode,
                $"P6-01 deterministic identity/form pin drifted at {expected.NodeId}.");
        }
        Require(
            steps.Select(step => step.AssignmentId)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(assignments.Select(item => item.Id)),
            "P6-01 contains an orphan step or assignment.");

        var receipts = await database
            .GetCollection<DynamicFlowRuntimeCommandReceipt>(
                "dynamic_flow_runtime_command_receipts")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var forwardReceipts = receipts
            .Where(item => item.CommandType == "FORWARD")
            .OrderBy(item => item.CommandId, StringComparer.Ordinal)
            .ToList();
        Require(
            forwardReceipts.Count == 2 &&
            forwardReceipts.All(item =>
                item.Status == DynamicFlowRuntimeCommandStatuses.Succeeded &&
                item.ResultSnapshot is not null &&
                item.ResultSnapshotHash ==
                DynamicFlowSequentialTopologyContract.Hash(
                    item.ResultSnapshot.ToJson()) &&
                item.Id ==
                DynamicFlowSequentialTopologyContract.BuildForwardReceiptId(
                    instance.Id,
                    item.CommandId)),
            "P6-01 forward receipt ledger drifted.");
        var events = await database.GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        Require(
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.SequentialForwardAccepted) == 2 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeEventTypes.SequentialAssignmentMaterialized) == 2 &&
            events.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() == events.Count &&
            events.All(item =>
                item.ExecutionEpoch == instance.ExecutionEpoch &&
                item.PayloadHash ==
                DynamicFlowSequentialTopologyContract.Hash(item.Payload.ToJson())) &&
            events.Select(item => item.Sequence)
                .SequenceEqual(Enumerable.Range(1, events.Count).Select(value => (long)value)) &&
            instance.NextEventSequence == events.Count + 1,
            "P6-01 runtime event chain is not exact/contiguous.");
        var outbox = await database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        Require(
            outbox.Count == 3 &&
            outbox.Count(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations.MaterializeEntryAssignment) == 1 &&
            outbox.Count(item =>
                item.Operation ==
                DynamicFlowRuntimeMaterializationOperations
                    .MaterializeSequentialAssignment) == 2 &&
            outbox.All(item =>
                item.Status == DynamicFlowRuntimeOutboxStatuses.Completed &&
                item.CompletedAtUtc.HasValue &&
                item.Payload.TryGetValue(
                    "scheduleIdentityJson",
                    out var scheduleIdentityJson) &&
                scheduleIdentityJson.IsString &&
                scheduleIdentityJson.AsString == instance.ScheduleIdentityJson &&
                DynamicFlowSequentialTopologyContract.Hash(
                    scheduleIdentityJson.AsString) ==
                instance.ScheduleIdentityHash &&
                item.Payload.TryGetValue(
                    "scheduleIdentityHash",
                    out var scheduleIdentityHash) &&
                scheduleIdentityHash.IsString &&
                scheduleIdentityHash.AsString == instance.ScheduleIdentityHash &&
                item.PayloadHash ==
                DynamicFlowSequentialTopologyContract.Hash(item.Payload.ToJson()) &&
                steps.Any(step => step.Id == item.StepInstanceId)) &&
            outbox.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() == outbox.Count &&
            outbox.Select(item => item.DedupeKey)
                .Distinct(StringComparer.Ordinal)
                .Count() == outbox.Count,
            "P6-01 outbox ledger has duplicates, orphans, or incomplete rows.");
        Require(
            instance.State == DynamicFlowInstanceStates.Completed &&
            steps.All(step => step.State == DynamicFlowStepStates.Completed),
            "P6-01 A -> B -> C did not complete its terminal aggregate.");
        return new
        {
            instanceId,
            instance.ExecutionEpoch,
            instance.DefinitionRevision,
            instance.TopologySnapshotHash,
            instance.ScheduleIdentityJson,
            instance.ScheduleIdentityHash,
            branchId,
            stepIds = steps.Select(step => step.Id).ToArray(),
            assignmentIds = assignments.Select(item => item.Id)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            receiptCount = receipts.Count,
            forwardReceiptCount = forwardReceipts.Count,
            eventCount = events.Count,
            sequentialAcceptedEventCount = 2,
            sequentialMaterializedEventCount = 2,
            outboxCount = outbox.Count,
            duplicateCount = 0,
            orphanCount = 0,
            exactPins = true,
            branchScopedOwnerIdentity = true,
            terminalState = instance.State
        };
    }

    private static async Task<Dictionary<string, long>> CaptureP601BusinessCountsAsync(
        IMongoDatabase database,
        CancellationToken ct)
    {
        var names = new[]
        {
            "dynamic_flow_instances",
            "dynamic_flow_step_instances",
            "dynamic_flow_participant_snapshots",
            "dynamic_flow_runtime_command_receipts",
            "dynamic_flow_runtime_events",
            "dynamic_flow_runtime_outbox",
            "work_assignments",
            "work_template_assignees",
            "work_report_periods",
            "work_assignment_queue",
            "doc_roles",
            "assignment_list_doc_roles",
            "my_report_period_list_doc_roles",
            "review_report_list_doc_roles"
        };
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            counts[name] = await database.GetCollection<BsonDocument>(name)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
        }
        return counts;
    }

    private static async Task<string> CaptureP601LedgerHashAsync(
        IMongoDatabase database,
        string instanceId,
        CancellationToken ct)
    {
        var id = ObjectId.Parse(instanceId);
        var rows = new List<string>();
        var assignmentIds = await database.GetCollection<BsonDocument>("work_assignments")
            .Find(new BsonDocument("flowInstanceId", id))
            .Project(new BsonDocument("_id", 1))
            .ToListAsync(ct);
        var assignmentIdValues = assignmentIds
            .Select(item => item["_id"])
            .ToArray();

        await AddAsync(
            "dynamic_flow_instances",
            new BsonDocument("_id", id));
        foreach (var collection in new[]
                 {
                     "dynamic_flow_step_instances",
                     "dynamic_flow_participant_snapshots",
                     "dynamic_flow_runtime_command_receipts",
                     "dynamic_flow_runtime_events",
                     "dynamic_flow_runtime_outbox",
                     "work_assignments"
                 })
        {
            await AddAsync(collection, new BsonDocument("flowInstanceId", id));
        }
        if (assignmentIdValues.Length > 0)
        {
            var inFilter = new BsonDocument(
                "$in",
                new BsonArray(assignmentIdValues));
            foreach (var collection in new[]
                     {
                         "work_template_assignees",
                         "work_report_periods",
                         "work_assignment_queue",
                         "work_assignment_report",
                         "assignment_list_doc_roles",
                         "my_report_period_list_doc_roles",
                         "review_report_list_doc_roles"
                     })
            {
                var field = collection is "assignment_list_doc_roles" or
                    "my_report_period_list_doc_roles" or
                    "review_report_list_doc_roles"
                    ? "assignmentId"
                    : "workAssignmentId";
                await AddAsync(collection, new BsonDocument(field, inFilter));
            }
        }
        return Hash(string.Join("\n", rows));

        async Task AddAsync(
            string collectionName,
            FilterDefinition<BsonDocument> filter)
        {
            var documents = await database.GetCollection<BsonDocument>(collectionName)
                .Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            rows.Add(collectionName);
            rows.AddRange(documents.Select(document => document.ToJson()));
        }
    }

    private static async Task<DynamicFlowInstance> LoadP601InstanceAsync(
        IMongoDatabase database,
        string instanceId,
        CancellationToken ct)
        => await database.GetCollection<DynamicFlowInstance>("dynamic_flow_instances")
            .Find(item => item.Id == instanceId && !item.IsDeleted)
            .SingleAsync(ct);

    private static async Task<List<DynamicFlowStepInstance>> LoadP601StepsAsync(
        IMongoDatabase database,
        string instanceId,
        CancellationToken ct)
        => await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => item.FlowInstanceId == instanceId && !item.IsDeleted)
            .SortBy(item => item.StepOrder)
            .ToListAsync(ct);

    private static async Task WaitForP601AssignmentAsync(
        IMongoDatabase database,
        string assignmentId,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var exists = await database.GetCollection<WorkAssignment>("work_assignments")
                .Find(item => item.Id == assignmentId && !item.IsDeleted)
                .AnyAsync(ct);
            if (exists)
                return;
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException(
            $"Sequential assignment {assignmentId} did not materialize.");
    }

    private static async Task<DynamicFlowRuntimeOutboxItem>
        WaitForP601InjectedFailureAsync(
            IMongoDatabase database,
            string instanceId,
            string assignmentId,
            CancellationToken ct)
    {
        var outboxCollection = database.GetCollection<DynamicFlowRuntimeOutboxItem>(
            "dynamic_flow_runtime_outbox");
        DynamicFlowRuntimeOutboxItem? observed = null;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var candidates = await outboxCollection
                .Find(item =>
                    item.FlowInstanceId == instanceId &&
                    item.Operation ==
                    DynamicFlowRuntimeMaterializationOperations
                        .MaterializeSequentialAssignment)
                .ToListAsync(ct);
            observed = candidates.SingleOrDefault(item =>
                item.Payload.TryGetValue("commandId", out var commandId) &&
                commandId.IsString &&
                commandId.AsString == P601ForwardBCommand);
            if (observed?.Status == DynamicFlowRuntimeOutboxStatuses.Failed)
                break;
            await Task.Delay(25, ct);
        }

        var failedOutbox = observed
                           ?? throw new InvalidOperationException(
                               "P6-01 did not preserve the real injected sequential worker failure.");
        Require(
            failedOutbox.Status == DynamicFlowRuntimeOutboxStatuses.Failed &&
            failedOutbox.AttemptCount == 1 &&
            failedOutbox.LastErrorCode == "DYNAMIC_FLOW_RUNTIME_INJECTED_FAILURE" &&
            failedOutbox.CompletedAtUtc is null,
            "P6-01 did not preserve the real injected sequential worker failure.");
        var step = await database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances")
            .Find(item => item.Id == failedOutbox.StepInstanceId && !item.IsDeleted)
            .SingleAsync(ct);
        var instance = await LoadP601InstanceAsync(database, instanceId, ct);
        var assignmentObjectId = ObjectId.Parse(assignmentId);
        var assignmentCount = await database
            .GetCollection<BsonDocument>("work_assignments")
            .CountDocumentsAsync(
                new BsonDocument("_id", assignmentObjectId),
                cancellationToken: ct);
        var bindingCount = await database
            .GetCollection<BsonDocument>("work_template_assignees")
            .CountDocumentsAsync(
                new BsonDocument("workAssignmentId", assignmentObjectId),
                cancellationToken: ct);
        var periodCount = await database
            .GetCollection<BsonDocument>("work_report_periods")
            .CountDocumentsAsync(
                new BsonDocument("workAssignmentId", assignmentObjectId),
                cancellationToken: ct);
        Require(
            step.State == DynamicFlowStepStates.Partial &&
            step.ResumeState == DynamicFlowStepStates.Assigned &&
            step.LastErrorCode == "DYNAMIC_FLOW_RUNTIME_INJECTED_FAILURE" &&
            instance.State == DynamicFlowInstanceStates.Partial &&
            instance.ResumeState == DynamicFlowInstanceStates.Active &&
            instance.LastErrorCode == "DYNAMIC_FLOW_RUNTIME_INJECTED_FAILURE" &&
            assignmentCount == 1 &&
            bindingCount == 0 &&
            periodCount == 0,
            "Injected AfterAssignmentWrite fault did not stop at the exact partial-write boundary.");
        return failedOutbox;
    }

    private static async Task<WorkReportPeriod> WaitForP601PeriodAsync(
        IMongoDatabase database,
        string assignmentId,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var period = await database.GetCollection<WorkReportPeriod>(
                    "work_report_periods")
                .Find(item =>
                    item.WorkAssignmentId == assignmentId &&
                    item.PeriodKey == P601PeriodKey &&
                    !item.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (period is not null)
                return period;
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException(
            $"Sequential assignment {assignmentId} lacks its report period.");
    }

    private static long RequiredP601Long(JsonNode? node, string property)
    {
        if (node is JsonObject obj &&
            obj[property] is JsonValue value &&
            value.TryGetValue<long>(out var number))
        {
            return number;
        }
        if (node is JsonObject fallback &&
            fallback[property] is JsonValue intValue &&
            intValue.TryGetValue<int>(out var intNumber))
        {
            return intNumber;
        }
        throw new InvalidOperationException(
            $"Response property '{property}' is missing or not an integer. Body={node?.ToJsonString() ?? "<null>"}");
    }

    private sealed record P601Fixture(
        string WorkId,
        string OwnerOracleWorkId,
        string FamilyId,
        string VersionId,
        string CanonicalPayload,
        string PayloadHash,
        string TargetUnitId,
        string TargetUserId,
        string OutsiderUserId,
        IReadOnlyList<P601ExpectedStep> ExpectedSteps);

    private sealed record P601ExpectedStep(
        string NodeId,
        string NodeCode,
        ProbeFormPinSnapshot Form,
        string? ActivatedByTransitionId,
        IReadOnlyList<string> NextNodeIds,
        bool IsTerminalNode);

    private sealed record P601Capability(
        long InstanceRevision,
        long StepRevision);
}



