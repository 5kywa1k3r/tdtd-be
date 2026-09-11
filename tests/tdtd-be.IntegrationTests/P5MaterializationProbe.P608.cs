using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-08 proof against a real replica set and Kestrel. It launches an
/// on-time occurrence, restarts the backend, verifies replay dedupe, audits a
/// missed occurrence, reruns it twice with distinct manual commands, and
/// validates the unique schedule/period index directly in Mongo.
/// </summary>
internal static partial class P5MaterializationProbe
{
    public static async Task<int> RunP608Async()
    {
        var runKey =
            $"p608_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        var exchanges = new List<ApiExchangeEvidence>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        object? directMongo = null;
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
            backend = await StartP603BackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "backend-p6-periodic",
                10);
            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var (adminToken, adminPassword) =
                await BootstrapAndLoginAsync(
                    api,
                    backend,
                    database,
                    CancellationToken.None);
            var baseFixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);

            var onTimeSeed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var onTime = await ConvertP601FixtureToP608Async(
                database,
                onTimeSeed,
                "daily-on-time",
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                onTimeSeed,
                "P6_08_ON_TIME",
                "P6-08-ON-TIME",
                CancellationToken.None);

            var missedSeed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var missed = await ConvertP601FixtureToP608Async(
                database,
                missedSeed,
                "daily-missed",
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                missedSeed,
                "P6_08_MISSED",
                "P6-08-MISSED",
                CancellationToken.None);

            var onTimeCreateRequest =
                new DynamicFlowPeriodicScheduleCreateRequest
                {
                    FlowTemplateVersionId = onTime.VersionId,
                    CommandId = "p6-08-create-on-time",
                    TargetUnitIds = [onTime.TargetUnitId],
                    TimeZoneId = "UTC",
                    LocalTime = "08:00",
                    EffectiveFromUtc = new DateTime(
                        2026,
                        7,
                        27,
                        8,
                        0,
                        0,
                        DateTimeKind.Utc)
                };
            var created = await api.PostAsync(
                $"api/works/{onTime.WorkId}/dynamic-flows/periodic-schedules",
                onTimeCreateRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                created,
                HttpStatusCode.OK,
                "P6-08 create on-time schedule");
            var scheduleId = ApiHarnessClient.RequiredString(
                created.Json,
                "scheduleId");
            var createReplay = await api.PostAsync(
                $"api/works/{onTime.WorkId}/dynamic-flows/periodic-schedules",
                onTimeCreateRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                createReplay,
                HttpStatusCode.OK,
                "P6-08 create schedule replay");
            Require(
                ApiHarnessClient.RequiredString(
                    createReplay.Json,
                    "scheduleId") == scheduleId,
                "P6-08 schedule replay changed identity.");

            var firstProcess = await api.PostAsync(
                "api/admin/operations/dynamic-flow-runtime/periodic/process",
                new DynamicFlowPeriodicProcessRequest
                {
                    ObservedAtUtc = new DateTime(
                        2026,
                        7,
                        27,
                        8,
                        1,
                        0,
                        DateTimeKind.Utc),
                    MaxItems = 20
                },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                firstProcess,
                HttpStatusCode.OK,
                "P6-08 process on-time");
            Require(
                ApiHarnessClient.RequiredInt(
                    firstProcess.Json,
                    "launchedCount") == 1,
                "P6-08 on-time fixture did not launch exactly once.");

            exchanges.AddRange(api.Exchanges);
            api.Dispose();
            apiClient = null;
            await backend.StopAsync();
            await backend.DisposeAsync();
            backend = await StartP603BackendAsync(
                paths,
                iterationRoot,
                runKey,
                mongo,
                "backend-p6-periodic-restart",
                10);
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);

            var afterRestart = await api.PostAsync(
                "api/admin/operations/dynamic-flow-runtime/periodic/process",
                new DynamicFlowPeriodicProcessRequest
                {
                    ObservedAtUtc = new DateTime(
                        2026,
                        7,
                        27,
                        8,
                        1,
                        0,
                        DateTimeKind.Utc),
                    MaxItems = 20
                },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                afterRestart,
                HttpStatusCode.OK,
                "P6-08 restart replay process");
            Require(
                ApiHarnessClient.RequiredInt(
                    afterRestart.Json,
                    "launchedCount") == 0,
                "P6-08 restart created a second instance.");

            var missedCreate = await api.PostAsync(
                $"api/works/{missed.WorkId}/dynamic-flows/periodic-schedules",
                new DynamicFlowPeriodicScheduleCreateRequest
                {
                    FlowTemplateVersionId = missed.VersionId,
                    CommandId = "p6-08-create-missed",
                    TargetUnitIds = [missed.TargetUnitId],
                    TimeZoneId = "UTC",
                    LocalTime = "08:00",
                    EffectiveFromUtc = new DateTime(
                        2026,
                        7,
                        25,
                        8,
                        0,
                        0,
                        DateTimeKind.Utc)
                },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                missedCreate,
                HttpStatusCode.OK,
                "P6-08 create missed schedule");
            var missedScheduleId =
                ApiHarnessClient.RequiredString(
                    missedCreate.Json,
                    "scheduleId");
            var missedProcess = await api.PostAsync(
                "api/admin/operations/dynamic-flow-runtime/periodic/process",
                new DynamicFlowPeriodicProcessRequest
                {
                    ObservedAtUtc = new DateTime(
                        2026,
                        7,
                        27,
                        8,
                        10,
                        0,
                        DateTimeKind.Utc),
                    MaxItems = 20
                },
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                missedProcess,
                HttpStatusCode.OK,
                "P6-08 process missed");
            Require(
                ApiHarnessClient.RequiredInt(
                    missedProcess.Json,
                    "missedCount") == 1,
                "P6-08 no-catch-up fixture was not audited missed.");

            var periodKey = "2026-07-25";
            foreach (var commandId in new[]
                     {
                         "p6-08-manual-rerun-1",
                         "p6-08-manual-rerun-2"
                     })
            {
                var rerun = await api.PostAsync(
                    $"api/works/{missed.WorkId}/dynamic-flows/periodic-schedules/{missedScheduleId}/occurrences/{periodKey}/rerun",
                    new DynamicFlowPeriodicManualRerunRequest
                    {
                        CommandId = commandId
                    },
                    adminToken,
                    ct: CancellationToken.None);
                ApiHarnessClient.ExpectStatus(
                    rerun,
                    HttpStatusCode.OK,
                    $"P6-08 {commandId}");
                Require(
                    ApiHarnessClient.RequiredString(
                        rerun.Json,
                        "state") ==
                    DynamicFlowPeriodicOccurrenceStates.Launched,
                    "P6-08 manual rerun did not converge to LAUNCHED.");
            }

            var schedules = database.GetCollection<
                DynamicFlowPeriodicSchedule>(
                "dynamic_flow_periodic_schedules");
            var occurrences = database.GetCollection<
                DynamicFlowPeriodicOccurrence>(
                "dynamic_flow_periodic_occurrences");
            var instances = database.GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances");
            var versions = database.GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions");
            var onTimeOccurrences = await occurrences
                .Find(item => item.ScheduleId == scheduleId)
                .ToListAsync();
            var missedOccurrences = await occurrences
                .Find(item => item.ScheduleId == missedScheduleId)
                .ToListAsync();
            var periodicInstances = await instances
                .Find(item =>
                    item.PeriodicScheduleId == scheduleId ||
                    item.PeriodicScheduleId == missedScheduleId)
                .ToListAsync();
            Require(
                onTimeOccurrences.Count == 1 &&
                missedOccurrences.Count == 1 &&
                periodicInstances.Count == 2,
                "P6-08 direct Mongo count drifted.");
            var pinnedVersions = await versions
                .Find(item =>
                    item.Id == onTime.VersionId ||
                    item.Id == missed.VersionId)
                .ToListAsync();
            Require(
                pinnedVersions.Count == 2 &&
                pinnedVersions.All(item =>
                    item.CatalogVersion ==
                        DynamicFlowP6CatalogCandidate.Version &&
                    item.CatalogSemanticHash ==
                        DynamicFlowP6CatalogCandidate.SemanticHash),
                "P6-08 direct Mongo candidate pin drifted.");
            var pinnedPayloadHashes = pinnedVersions
                .Select(item =>
                {
                    var canonical =
                        DynamicFlowDefinitionPayloadContract
                            .CanonicalizeAndValidate(
                                item.PayloadJson,
                                new DynamicFlowDefinitionValidationOptions(
                                    AllowLegacy: false,
                                    AllowServerManagedPins: true,
                                    RequireServerManagedPins: true,
                                    AllowHistoricalCatalogPins: true));
                    Require(
                        canonical.Payload.ArchetypeId ==
                            DynamicFlowPeriodicTopologyContract.ArchetypeId &&
                        canonical.PayloadHash == item.PayloadHash,
                        "P6-08 direct Mongo topology/payload pin drifted.");
                    return canonical.PayloadHash;
                })
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var periodicInstanceIds = periodicInstances
                .Select(item => item.Id)
                .ToArray();
            var participantSnapshots = await database
                .GetCollection<DynamicFlowParticipantSnapshot>(
                    "dynamic_flow_participant_snapshots")
                .Find(item => periodicInstanceIds.Contains(item.FlowInstanceId))
                .ToListAsync();
            var steps = await database
                .GetCollection<DynamicFlowStepInstance>(
                    "dynamic_flow_step_instances")
                .Find(item => periodicInstanceIds.Contains(item.FlowInstanceId))
                .ToListAsync();
            var events = await database
                .GetCollection<DynamicFlowRuntimeEvent>(
                    "dynamic_flow_runtime_events")
                .Find(item => periodicInstanceIds.Contains(item.FlowInstanceId))
                .ToListAsync();
            var outbox = await database
                .GetCollection<DynamicFlowRuntimeOutboxItem>(
                    "dynamic_flow_runtime_outbox")
                .Find(item => periodicInstanceIds.Contains(item.FlowInstanceId))
                .ToListAsync();
            var assignments = await database
                .GetCollection<WorkAssignment>("work_assignments")
                .Find(item =>
                    item.FlowInstanceId != null &&
                    periodicInstanceIds.Contains(item.FlowInstanceId) &&
                    !item.IsDeleted)
                .ToListAsync();
            var assignmentIds = assignments
                .Select(item => item.Id)
                .ToArray();
            var bindings = await database
                .GetCollection<WorkTemplateAssignee>(
                    "work_template_assignees")
                .Find(item =>
                    assignmentIds.Contains(item.WorkAssignmentId) &&
                    !item.IsDeleted)
                .ToListAsync();
            var periods = await database
                .GetCollection<WorkReportPeriod>(
                    "work_report_periods")
                .Find(item =>
                    assignmentIds.Contains(item.WorkAssignmentId) &&
                    !item.IsDeleted)
                .ToListAsync();
            var reports = await database
                .GetCollection<WorkAssignmentReport>(
                    "work_assignment_report")
                .Find(item =>
                    assignmentIds.Contains(item.WorkAssignmentId) &&
                    !item.IsDeleted)
                .ToListAsync();
            Require(
                participantSnapshots.Count == 2 &&
                steps.Count == 2 &&
                assignments.Count == 2 &&
                bindings.Count == 2 &&
                periods.Count == 2 &&
                reports.Count == 0,
                "P6-08 snapshot/step/assignment/binding/period/report cardinality drifted: " +
                $"snapshots={participantSnapshots.Count};steps={steps.Count};" +
                $"assignments={assignments.Count};bindings={bindings.Count};" +
                $"periods={periods.Count};reports={reports.Count}.");
            Require(
                periodicInstances.All(item =>
                    item.State == DynamicFlowInstanceStates.Materializing &&
                    item.NextEventSequence == 3) &&
                steps.All(item =>
                    item.State == DynamicFlowStepStates.Assigned &&
                    item.AssignmentId is not null &&
                    item.ReportId is null &&
                    !string.IsNullOrWhiteSpace(item.ResultOwnerIdentity) &&
                    !string.IsNullOrWhiteSpace(
                        item.StatisticOwnerIdentity)) &&
                steps.Select(item => item.ResultOwnerIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == 2 &&
                steps.Select(item => item.StatisticOwnerIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == 2,
                "P6-08 instance/step revision, state, or owner identity drifted: " +
                $"instances=[{string.Join(",", periodicInstances.Select(item => $"{item.State}/next={item.NextEventSequence}"))}];" +
                $"steps=[{string.Join(",", steps.Select(item => $"{item.State}/assignment={item.AssignmentId is not null}/report={item.ReportId is not null}/result={item.ResultOwnerIdentity}/stats={item.StatisticOwnerIdentity}"))}].");
            Require(
                events.Count == 4 &&
                periodicInstanceIds.All(instanceId =>
                    events.Count(item =>
                        item.FlowInstanceId == instanceId &&
                        item.EventType ==
                            DynamicFlowRuntimeEventTypes
                                .LaunchIntentCommitted) == 1 &&
                    events.Count(item =>
                        item.FlowInstanceId == instanceId &&
                        item.EventType ==
                            DynamicFlowRuntimeEventTypes
                                .EntryAssignmentMaterialized) == 1) &&
                outbox.Count == 2 &&
                outbox.All(item =>
                    item.Operation ==
                        DynamicFlowRuntimeMaterializationOperations
                            .MaterializeEntryAssignment &&
                    item.Status ==
                        DynamicFlowRuntimeOutboxStatuses.Completed &&
                    item.CompletedAtUtc.HasValue),
                "P6-08 event/outbox ledger drifted.");
            Require(
                steps.All(step =>
                    assignments.Any(assignment =>
                        assignment.Id == step.AssignmentId &&
                        assignment.FlowInstanceId == step.FlowInstanceId &&
                        assignment.FlowStepId == step.FlowStepId &&
                        assignment.FlowBranchId == step.BranchId &&
                        assignment.FlowAttemptNo == step.AttemptNo &&
                        assignment.FlowExecutionEpoch ==
                            step.ExecutionEpoch)) &&
                assignments.All(assignment =>
                    bindings.Count(item =>
                        item.WorkAssignmentId == assignment.Id) == 1 &&
                    periods.Count(item =>
                        item.WorkAssignmentId == assignment.Id &&
                        item.CurrentReportId == null) == 1),
                "P6-08 assignment/report-owner lineage contains a duplicate or orphan.");
            Require(
                onTimeOccurrences[0].FlowInstanceId is not null &&
                missedOccurrences[0].FlowInstanceId is not null &&
                periodicInstances.All(item =>
                    item.PeriodicOccurrenceId is not null &&
                    item.TimeZoneId is not null &&
                    item.SchedulePolicyVersion ==
                        DynamicFlowPeriodicTopologyContract.PolicyVersion),
                "P6-08 orphan or schedule pin drift detected.");

            var duplicateRejected = false;
            try
            {
                await occurrences.InsertOneAsync(
                    new DynamicFlowPeriodicOccurrence
                    {
                        Id = MongoDB.Bson.ObjectId.GenerateNewId()
                            .ToString(),
                        ScheduleId = scheduleId,
                        WorkId = onTime.WorkId,
                        PeriodKey = onTimeOccurrences[0].PeriodKey,
                        TimeZoneId = onTimeOccurrences[0].TimeZoneId,
                        PolicyVersion =
                            onTimeOccurrences[0].PolicyVersion,
                        ScheduledAtUtc =
                            onTimeOccurrences[0].ScheduledAtUtc,
                        ObservedAtUtc =
                            onTimeOccurrences[0].ObservedAtUtc,
                        State =
                            DynamicFlowPeriodicOccurrenceStates.Pending,
                        Revision = 1,
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow,
                        IsDeleted = false
                    });
            }
            catch (MongoWriteException error)
                when (error.WriteError?.Category ==
                      ServerErrorCategory.DuplicateKey)
            {
                duplicateRejected = true;
            }
            Require(
                duplicateRejected,
                "P6-08 unique schedule/period index did not reject duplicate.");
            directMongo = new
            {
                scheduleCount = await schedules.CountDocumentsAsync(
                    item => !item.IsDeleted),
                occurrenceCount =
                    onTimeOccurrences.Count + missedOccurrences.Count,
                instanceCount = periodicInstances.Count,
                archetypeId =
                    DynamicFlowPeriodicTopologyContract.ArchetypeId,
                catalogVersion =
                    DynamicFlowP6CatalogCandidate.Version,
                catalogSemanticHash =
                    DynamicFlowP6CatalogCandidate.SemanticHash,
                exactPins = true,
                payloadHashes = pinnedPayloadHashes,
                participantSnapshotCount =
                    participantSnapshots.Count,
                stepCount = steps.Count,
                eventCount = events.Count,
                outboxCount = outbox.Count,
                assignmentCount = assignments.Count,
                bindingCount = bindings.Count,
                periodCount = periods.Count,
                reportCount = reports.Count,
                ownerIdentitiesDistinct = true,
                instanceState =
                    DynamicFlowInstanceStates.Materializing,
                reportOwnerState =
                    "NOT_MATERIALIZED_UNTIL_REPORT_OPEN",
                uniqueDuplicateRejected = duplicateRejected,
                onTimePeriodKey = onTimeOccurrences[0].PeriodKey,
                missedPeriodKey = missedOccurrences[0].PeriodKey,
                manualCommands =
                    missedOccurrences[0].ManualCommandIds,
                noOrphans = true
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
            }
            if (backend is not null)
            {
                try { await backend.StopAsync(); }
                catch (Exception error)
                {
                    cleanupErrors.Add(
                        $"backend-stop: {error.Message}");
                }
                await backend.DisposeAsync();
            }
            if (mongo is not null)
            {
                try
                {
                    await mongo.DropDatabaseGuardedAsync(
                        CancellationToken.None);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(
                        $"mongo-drop: {error.Message}");
                }
                try { await mongo.StopProcessAsync(); }
                catch (Exception error)
                {
                    cleanupErrors.Add(
                        $"mongo-stop: {error.Message}");
                }
                try { mongo.RemoveDataDirectoryGuarded(); }
                catch (Exception error)
                {
                    cleanupErrors.Add(
                        $"mongo-data-remove: {error.Message}");
                }
                await mongo.DisposeAsync();
            }
        }

        passed = passed && cleanupErrors.Count == 0;
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-08-periodic-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-08-periodic-direct-mongo.json"),
            new { runKey, directMongo });
        var resultPath = Path.Combine(
            iterationRoot,
            "p6-08-periodic-result.json");
        await EvidenceJson.WriteAsync(
            resultPath,
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-011" },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-08 periodic probe passed; artifact={resultPath}"
                : $"[FAIL] P6-08 periodic probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={resultPath}");
        return passed ? 0 : 1;
    }

    private static async Task<P608Fixture>
        ConvertP601FixtureToP608Async(
            IMongoDatabase database,
            P601Fixture seed,
            string scheduleKey,
            CancellationToken ct)
    {
        var rootForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_a").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-08 seed payload is invalid.");
        payload["archetypeId"] =
            DynamicFlowPeriodicTopologyContract.ArchetypeId;
        payload.Remove("resultOwnerStepId");
        payload.Remove("resultOwnerFormNodeId");
        payload.Remove("statisticsOwnerStepId");
        payload.Remove("statisticsOwnerFormNodeId");
        payload["formNodes"] = new JsonArray(
            new JsonObject
            {
                ["formNodeId"] = rootForm.FormNodeId,
                ["role"] = "ROOT",
                ["dynamicFormTemplateId"] =
                    rootForm.FormVersionId,
                ["dynamicFormFamilyId"] =
                    rootForm.FormFamilyId,
                ["dynamicFormVersionNo"] =
                    rootForm.FormVersionNo,
                ["dynamicFormSchemaHash"] =
                    rootForm.FormSchemaHash,
                ["dynamicFormSnapshotHash"] =
                    rootForm.FormSchemaHash
            });
        payload["nodes"] = new JsonArray(
            P602Step(
                "step_a",
                "PERIODIC_ENTRY",
                rootForm.FormNodeId),
            new JsonObject
            {
                ["nodeId"] = "schedule_gateway",
                ["nodeCode"] = "SCHEDULE",
                ["nodeKind"] =
                    DynamicFlowNodeKinds.Gateway,
                ["gateway"] = new JsonObject
                {
                    ["kind"] =
                        DynamicFlowGatewayKinds.Schedule,
                    ["scheduleKey"] = scheduleKey
                }
            });
        payload["edges"] = new JsonArray(
            P602Edge(
                "edge-schedule",
                "step_a",
                "schedule_gateway"));
        payload["finalResultPolicy"] = new JsonObject();
        var canonical =
            DynamicFlowDefinitionPayloadContract
                .CanonicalizeAndValidate(
                    payload.ToJsonString(),
                    new DynamicFlowDefinitionValidationOptions(
                        AllowLegacy: false,
                        AllowServerManagedPins: true,
                        RequireServerManagedPins: true,
                        AllowHistoricalCatalogPins: true));
        _ = DynamicFlowPeriodicTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        await database.GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .UpdateOneAsync(
                item => item.Id == seed.VersionId,
                Builders<DynamicFlowTemplateVersion>.Update
                    .Set(
                        item => item.PayloadJson,
                        canonical.CanonicalJson)
                    .Set(
                        item => item.PayloadHash,
                        canonical.PayloadHash),
                cancellationToken: ct);
        await database.GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates")
            .UpdateOneAsync(
                item => item.Id == seed.FamilyId,
                Builders<DynamicFlowTemplate>.Update.Set(
                    item => item.CurrentVersionHash,
                    canonical.PayloadHash),
                cancellationToken: ct);
        return new P608Fixture(
            seed.WorkId,
            seed.VersionId,
            seed.TargetUnitId);
    }

    private sealed record P608Fixture(
        string WorkId,
        string VersionId,
        string TargetUnitId);
}
