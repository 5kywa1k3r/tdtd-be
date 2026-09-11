using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-10 proof against real Kestrel and a Mongo replica set. It
/// verifies issuer-only epoch commands, exact receipt replay, CAS/race
/// exclusion, deterministic invalidation/replacement, a durable no-P8 rebuild
/// intent, finalization irreversibility, and the epoch uniqueness index.
/// </summary>
internal static partial class P5MaterializationProbe
{
    public static async Task<int> RunP610Async()
    {
        var runKey =
            $"p610_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        var exchanges = new List<ApiExchangeEvidence>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        ApiHarnessClient? raceApiClient = null;
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
                "backend-p6-epoch",
                12);
            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var raceApi = new ApiHarnessClient(backend.BaseUri);
            raceApiClient = raceApi;
            var (adminToken, _) = await BootstrapAndLoginAsync(
                api,
                backend,
                database,
                CancellationToken.None);
            var baseFixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);
            var seed = await SeedP601FixtureAsync(
                database,
                baseFixture,
                CancellationToken.None);
            var fixture = await ConvertP601FixtureToP610Async(
                database,
                seed,
                CancellationToken.None);
            await RenameP603FixtureAsync(
                database,
                seed,
                "P6_10_EXECUTION_EPOCH",
                "P6-10-EXECUTION-EPOCH",
                CancellationToken.None);

            var launch = await LaunchAsync(
                api,
                adminToken,
                baseFixture,
                fixture.WorkId,
                fixture.VersionId,
                "p6-10-launch-rollback",
                [fixture.TargetUnitId],
                P601PeriodKey,
                CancellationToken.None);
            RequireStatus(
                launch.Confirm,
                "SUCCEEDED",
                "P6-10 FLOW-T12 rollback launch");
            var instanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");
            var instances = database.GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances");
            var steps = database.GetCollection<DynamicFlowStepInstance>(
                "dynamic_flow_step_instances");
            var assignments = database.GetCollection<WorkAssignment>(
                "work_assignments");
            var reports = database.GetCollection<WorkAssignmentReport>(
                "work_assignment_report");
            var epochs = database.GetCollection<DynamicFlowExecutionEpoch>(
                "dynamic_flow_execution_epochs");
            var events = database.GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events");
            var receipts =
                database.GetCollection<DynamicFlowRuntimeCommandReceipt>(
                    "dynamic_flow_runtime_command_receipts");
            var outbox = database.GetCollection<DynamicFlowRuntimeOutboxItem>(
                "dynamic_flow_runtime_outbox");
            var initial = await instances
                .Find(item => item.Id == instanceId)
                .SingleAsync();
            var epochOneStep = await steps
                .Find(item =>
                    item.FlowInstanceId == instanceId &&
                    item.ExecutionEpoch == 1)
                .SingleAsync();
            var epochOneAssignmentId = epochOneStep.AssignmentId
                ?? throw new InvalidOperationException(
                    "P6-10 epoch-one assignment missing.");
            var beforePrematureFinalize = await CaptureP610LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var prematureFinalize = await api.PostAsync(
                EpochRoute(fixture.WorkId, instanceId, "finalize"),
                EpochRequest(
                    "p6-10-premature-finalize",
                    initial.ExecutionEpoch,
                    initial.Revision),
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                prematureFinalize,
                HttpStatusCode.BadRequest,
                "P6-10 premature finalize");
            Require(
                beforePrematureFinalize ==
                    await CaptureP610LedgerHashAsync(
                        database,
                        instanceId,
                        CancellationToken.None),
                "P6-10 premature finalize changed the direct-Mongo ledger.");
            epochOneStep = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                epochOneStep,
                fieldId: null,
                fieldValue: null,
                suffix: "p610-rollback",
                CancellationToken.None);
            var epochOneReportId = epochOneStep.ReportId
                ?? throw new InvalidOperationException(
                    "P6-10 epoch-one report missing.");
            initial = await instances
                .Find(item => item.Id == instanceId)
                .SingleAsync();
            var gatewayId = ObjectId.GenerateNewId().ToString();
            await database
                .GetCollection<DynamicFlowGatewayInstance>(
                    "dynamic_flow_gateway_instances")
                .InsertOneAsync(
                    new DynamicFlowGatewayInstance
                    {
                        Id = gatewayId,
                        FlowInstanceId = instanceId,
                        ExecutionEpoch = initial.ExecutionEpoch,
                        GatewayNodeId = "epoch_gate",
                        GatewayInstanceId =
                            ObjectId.GenerateNewId().ToString(),
                        GatewayVersion = 1,
                        GatewayKind =
                            DynamicFlowGatewayKinds.RollbackFinalize,
                        DownstreamNodeId = "final",
                        State = DynamicFlowGatewayStates.Collecting,
                        IsCanonicalEpoch = true,
                        Revision = 1,
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow,
                        UpdatedByUserId = initial.IssuerUserId
                    },
                    cancellationToken: CancellationToken.None);
            var statisticIds = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (var collectionName in new[]
                     {
                         "work_report_field_stat_values",
                         "work_report_table_stat_values",
                         "work_report_label_stat_values"
                     })
            {
                var statisticId = ObjectId.GenerateNewId().ToString();
                statisticIds.Add(collectionName, statisticId);
                await database
                    .GetCollection<BsonDocument>(collectionName)
                    .InsertOneAsync(
                        new BsonDocument
                        {
                            { "_id", ObjectId.Parse(statisticId) },
                            {
                                "workAssignmentId",
                                ObjectId.Parse(epochOneAssignmentId)
                            },
                            {
                                "flowInstanceId",
                                ObjectId.Parse(instanceId)
                            },
                            {
                                "workAssignmentReportId",
                                ObjectId.Parse(epochOneReportId)
                            },
                            {
                                "flowEffectiveStatus",
                                DynamicFlowEffectiveStatuses.Effective
                            },
                            { "isDeleted", false },
                            { "createdAtUtc", DateTime.UtcNow },
                            { "updatedAtUtc", DateTime.UtcNow },
                            {
                                "updatedByUserId",
                                ObjectId.Parse(initial.IssuerUserId)
                            }
                        },
                        cancellationToken: CancellationToken.None);
            }

            var outsiderToken = await PrepareP601ActorLoginAsync(
                api,
                backend,
                database,
                seed.OutsiderUserId,
                CancellationToken.None);
            var beforeForged = await CaptureP610LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var forged = await api.PostAsync(
                EpochRoute(fixture.WorkId, instanceId, "rollback"),
                EpochRequest(
                    "p6-10-forged-rollback",
                    initial.ExecutionEpoch,
                    initial.Revision,
                    fixture.EntryNodeId),
                outsiderToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                forged,
                HttpStatusCode.Forbidden,
                "P6-10 forged rollback");
            Require(
                beforeForged == await CaptureP610LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "P6-10 forged rollback changed the direct-Mongo ledger.");

            var rollbackRequest = EpochRequest(
                "p6-10-rollback-epoch-1",
                initial.ExecutionEpoch,
                initial.Revision,
                fixture.EntryNodeId);
            var rollback = await api.PostAsync(
                EpochRoute(fixture.WorkId, instanceId, "rollback"),
                rollbackRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                rollback,
                HttpStatusCode.OK,
                "P6-10 rollback epoch one");
            Require(
                ApiHarnessClient.RequiredInt(
                    rollback.Json,
                    "previousExecutionEpoch") == 1 &&
                ApiHarnessClient.RequiredInt(
                    rollback.Json,
                    "executionEpoch") == 2 &&
                ApiHarnessClient.RequiredBool(
                    rollback.Json,
                    "businessWritePerformed"),
                "P6-10 rollback response did not advance exactly one epoch.");
            var rollbackReplay = await api.PostAsync(
                EpochRoute(fixture.WorkId, instanceId, "rollback"),
                rollbackRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                rollbackReplay,
                HttpStatusCode.OK,
                "P6-10 rollback exact replay");
            Require(
                ApiHarnessClient.RequiredBool(
                    rollbackReplay.Json,
                    "replayed") &&
                ApiHarnessClient.RequiredString(
                    rollbackReplay.Json,
                    "eventId") ==
                ApiHarnessClient.RequiredString(
                    rollback.Json,
                    "eventId"),
                "P6-10 rollback replay changed its durable result identity.");

            var afterRollback = await instances
                .Find(item => item.Id == instanceId)
                .SingleAsync();
            var allSteps = await steps
                .Find(item => item.FlowInstanceId == instanceId)
                .SortBy(item => item.ExecutionEpoch)
                .ToListAsync();
            var allAssignments = await assignments
                .Find(item => item.FlowInstanceId == instanceId)
                .SortBy(item => item.FlowExecutionEpoch)
                .ToListAsync();
            var epochRows = await epochs
                .Find(item => item.FlowInstanceId == instanceId)
                .SortBy(item => item.ExecutionEpoch)
                .ToListAsync();
            var rebuildIntent = await outbox
                .Find(item =>
                    item.FlowInstanceId == instanceId &&
                    item.Operation ==
                        DynamicFlowFinalizeTopologyContract
                            .RebuildIntentOperation)
                .SingleAsync();
            var invalidatedGateway = await database
                .GetCollection<DynamicFlowGatewayInstance>(
                    "dynamic_flow_gateway_instances")
                .Find(item => item.Id == gatewayId)
                .SingleAsync();
            var invalidatedReport = await reports
                .Find(item => item.Id == epochOneReportId)
                .SingleAsync();
            var invalidatedStatistics = new List<BsonDocument>();
            foreach (var statistic in statisticIds)
            {
                invalidatedStatistics.Add(await database
                    .GetCollection<BsonDocument>(statistic.Key)
                    .Find(new BsonDocument(
                        "_id",
                        ObjectId.Parse(statistic.Value)))
                    .SingleAsync());
            }
            var rollbackEventId = ApiHarnessClient.RequiredString(
                rollback.Json,
                "eventId");
            Require(
                ApiHarnessClient.RequiredInt(
                    rollback.Json,
                    "invalidatedStepCount") == 1 &&
                ApiHarnessClient.RequiredInt(
                    rollback.Json,
                    "invalidatedGatewayCount") == 1 &&
                ApiHarnessClient.RequiredInt(
                    rollback.Json,
                    "invalidatedAssignmentCount") == 1 &&
                ApiHarnessClient.RequiredInt(
                    rollback.Json,
                    "invalidatedReportCount") == 1 &&
                afterRollback.ExecutionEpoch == 2 &&
                afterRollback.State == DynamicFlowInstanceStates.Active &&
                allSteps.Count == 2 &&
                allSteps[0].ExecutionEpoch == 1 &&
                allSteps[0].IsCanonicalEpoch == false &&
                allSteps[0].InvalidatedByFlowEventId == rollbackEventId &&
                allSteps[1].ExecutionEpoch == 2 &&
                allSteps[1].IsCanonicalEpoch == true &&
                allSteps[1].State == DynamicFlowStepStates.Assigned &&
                allAssignments.Count == 2 &&
                allAssignments[0].Id == epochOneAssignmentId &&
                !allAssignments[0].IsActive &&
                allAssignments[0].FlowEffectiveStatus ==
                    DynamicFlowEffectiveStatuses.Invalidated &&
                allAssignments[0].InvalidatedByFlowEventId ==
                    rollbackEventId &&
                allAssignments[1].IsActive &&
                allAssignments[1].FlowExecutionEpoch == 2 &&
                invalidatedGateway.IsCanonicalEpoch == false &&
                invalidatedGateway.InvalidatedByFlowEventId ==
                    rollbackEventId &&
                !invalidatedReport.IsCurrent &&
                !invalidatedReport.IsActive &&
                invalidatedReport.InvalidatedByFlowEventId ==
                    rollbackEventId &&
                invalidatedStatistics.All(statistic =>
                    statistic["flowEffectiveStatus"].AsString ==
                        DynamicFlowEffectiveStatuses.Invalidated &&
                    statistic["invalidatedByFlowEventId"].AsObjectId
                        .ToString() == rollbackEventId) &&
                epochRows.Count == 2 &&
                epochRows[0].State ==
                    DynamicFlowExecutionEpochStates.RolledBack &&
                !epochRows[0].IsCanonical &&
                epochRows[0].ReplacedByExecutionEpoch == 2 &&
                epochRows[1].State ==
                    DynamicFlowExecutionEpochStates.Active &&
                epochRows[1].IsCanonical &&
                rebuildIntent.Status ==
                    DynamicFlowRuntimeOutboxStatuses.Completed &&
                rebuildIntent.Payload["p8ExecutionEnabled"].IsBoolean &&
                !rebuildIntent.Payload["p8ExecutionEnabled"].AsBoolean,
                "P6-10 invalidation closure/replacement/no-P8 ledger drifted.");

            var rollbackOverview = await api.GetAsync(
                $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}",
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                rollbackOverview,
                HttpStatusCode.OK,
                "P6-10 rollback overview");
            var rollbackOverviewBody = ApiHarnessClient.RequiredObject(
                rollbackOverview.Json,
                "P6-10 rollback overview");
            var rollbackCapabilities = ApiHarnessClient.RequiredObject(
                rollbackOverviewBody["capabilities"],
                "P6-10 rollback capabilities");
            var rollbackReadEpochs = ApiHarnessClient.RequiredArray(
                rollbackOverviewBody["epochs"],
                "P6-10 rollback epochs");
            Require(
                ApiHarnessClient.RequiredInt(
                    rollbackOverviewBody,
                    "executionEpoch") == 2 &&
                ApiHarnessClient.RequiredString(
                    rollbackOverviewBody,
                    "state") == DynamicFlowInstanceStates.Active &&
                rollbackReadEpochs.Count == 2 &&
                rollbackReadEpochs[0] is JsonObject currentReadEpoch &&
                ApiHarnessClient.RequiredInt(
                    currentReadEpoch,
                    "executionEpoch") == 2 &&
                ApiHarnessClient.RequiredBool(
                    currentReadEpoch,
                    "isCanonical") &&
                rollbackReadEpochs[1] is JsonObject invalidatedReadEpoch &&
                ApiHarnessClient.RequiredInt(
                    invalidatedReadEpoch,
                    "executionEpoch") == 1 &&
                !ApiHarnessClient.RequiredBool(
                    invalidatedReadEpoch,
                    "isCanonical") &&
                ApiHarnessClient.RequiredBool(
                    rollbackCapabilities,
                    "canRollback") &&
                ApiHarnessClient.RequiredBool(
                    rollbackCapabilities,
                    "canTerminate") &&
                ApiHarnessClient.RequiredBool(
                    rollbackCapabilities,
                    "canRestart") &&
                !ApiHarnessClient.RequiredBool(
                    rollbackCapabilities,
                    "canFinalize"),
                "P6-10 rollback read projection/capabilities drifted.");
            var rollbackTimeline = await api.GetAsync(
                $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}/timeline?limit=50",
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                rollbackTimeline,
                HttpStatusCode.OK,
                "P6-10 rollback timeline");
            var rollbackTimelineItems = ApiHarnessClient.RequiredArray(
                ApiHarnessClient.RequiredObject(
                    rollbackTimeline.Json,
                    "P6-10 rollback timeline")["items"],
                "P6-10 rollback timeline items");
            var rollbackTimelineObserved = rollbackTimelineItems
                .OfType<JsonObject>()
                .Any(item =>
                    ApiHarnessClient.RequiredString(item, "eventId") ==
                        rollbackEventId &&
                    ApiHarnessClient.RequiredString(item, "eventType") ==
                        DynamicFlowFinalizeTopologyContract.RolledBackEvent);
            Require(
                rollbackTimelineObserved,
                "P6-10 rollback event was missing from the runtime timeline.");

            var beforeStale = await CaptureP610LedgerHashAsync(
                database,
                instanceId,
                CancellationToken.None);
            var stale = await api.PostAsync(
                EpochRoute(fixture.WorkId, instanceId, "terminate"),
                EpochRequest(
                    "p6-10-stale-terminate",
                    initial.ExecutionEpoch,
                    initial.Revision),
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                stale,
                HttpStatusCode.Conflict,
                "P6-10 stale epoch/revision");
            Require(
                beforeStale == await CaptureP610LedgerHashAsync(
                    database,
                    instanceId,
                    CancellationToken.None),
                "P6-10 stale command changed the direct-Mongo ledger.");

            var restartRequest = EpochRequest(
                "p6-10-race-restart",
                afterRollback.ExecutionEpoch,
                afterRollback.Revision,
                fixture.EntryNodeId);
            var terminateRequest = EpochRequest(
                "p6-10-race-terminate",
                afterRollback.ExecutionEpoch,
                afterRollback.Revision);
            var raceResponses = await Task.WhenAll(
                api.PostAsync(
                    EpochRoute(fixture.WorkId, instanceId, "restart"),
                    restartRequest,
                    adminToken,
                    ct: CancellationToken.None),
                raceApi.PostAsync(
                    EpochRoute(fixture.WorkId, instanceId, "terminate"),
                    terminateRequest,
                    adminToken,
                    ct: CancellationToken.None));
            var winners = raceResponses
                .Select((response, index) => new { response, index })
                .Where(item => item.response.StatusCode == HttpStatusCode.OK)
                .ToArray();
            var losers = raceResponses
                .Where(response =>
                    response.StatusCode == HttpStatusCode.Conflict)
                .ToArray();
            Require(
                winners.Length == 1 && losers.Length == 1,
                "P6-10 CAS race must have exactly one winner and one conflict.");
            var raceWinner = winners[0];
            var winnerAction = raceWinner.index == 0
                ? "restart"
                : "terminate";
            var winnerRequest = raceWinner.index == 0
                ? restartRequest
                : terminateRequest;
            var raceReplay = await api.PostAsync(
                EpochRoute(fixture.WorkId, instanceId, winnerAction),
                winnerRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                raceReplay,
                HttpStatusCode.OK,
                "P6-10 race winner replay");
            Require(
                ApiHarnessClient.RequiredBool(
                    raceReplay.Json,
                    "replayed") &&
                ApiHarnessClient.RequiredString(
                    raceReplay.Json,
                    "eventId") ==
                ApiHarnessClient.RequiredString(
                    raceWinner.response.Json,
                    "eventId"),
                "P6-10 race winner replay changed result identity.");

            var epochReceiptRows = await receipts
                .Find(item =>
                    item.FlowInstanceId == instanceId &&
                    (item.CommandType ==
                        DynamicFlowFinalizeTopologyContract.RollbackCommand ||
                     item.CommandType ==
                        DynamicFlowFinalizeTopologyContract.RestartCommand ||
                     item.CommandType ==
                        DynamicFlowFinalizeTopologyContract.TerminateCommand))
                .ToListAsync();
            var epochEventRows = await events
                .Find(item =>
                    item.FlowInstanceId == instanceId &&
                    (item.EventType ==
                        DynamicFlowFinalizeTopologyContract.RolledBackEvent ||
                     item.EventType ==
                        DynamicFlowFinalizeTopologyContract.RestartedEvent ||
                     item.EventType ==
                        DynamicFlowFinalizeTopologyContract.TerminatedEvent))
                .ToListAsync();
            Require(
                epochReceiptRows.Count == 2 &&
                epochEventRows.Count == 2 &&
                epochReceiptRows.Any(item =>
                    item.CommandId == rollbackRequest.CommandId) &&
                epochReceiptRows.Any(item =>
                    item.CommandId == winnerRequest.CommandId),
                "P6-10 replay/race created duplicate or loser receipts/events.");

            var finalizeLaunch = await LaunchAsync(
                api,
                adminToken,
                baseFixture,
                fixture.FinalizeWorkId,
                fixture.VersionId,
                "p6-10-launch-finalize",
                [fixture.TargetUnitId],
                P601PeriodKey,
                CancellationToken.None);
            RequireStatus(
                finalizeLaunch.Confirm,
                "SUCCEEDED",
                "P6-10 FLOW-T12 finalize launch");
            var finalizeInstanceId = ApiHarnessClient.RequiredString(
                finalizeLaunch.Confirm.Json,
                "flowInstanceId");
            var finalizeStep = await steps
                .Find(item =>
                    item.FlowInstanceId == finalizeInstanceId &&
                    item.ExecutionEpoch == 1)
                .SingleAsync();
            finalizeStep = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                finalizeStep,
                fieldId: null,
                fieldValue: null,
                suffix: "p610-finalize",
                CancellationToken.None);
            await CompleteP610AssignmentAsync(
                api,
                adminToken,
                database,
                finalizeStep,
                CancellationToken.None);
            var completed = await WaitForP610InstanceStateAsync(
                api,
                adminToken,
                database,
                finalizeInstanceId,
                DynamicFlowInstanceStates.Completed,
                CancellationToken.None);
            var finalizeRequest = EpochRequest(
                "p6-10-finalize-epoch-1",
                completed.ExecutionEpoch,
                completed.Revision);
            var finalize = await api.PostAsync(
                EpochRoute(
                    fixture.FinalizeWorkId,
                    finalizeInstanceId,
                    "finalize"),
                finalizeRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                finalize,
                HttpStatusCode.OK,
                "P6-10 finalize");
            var finalizeReplay = await api.PostAsync(
                EpochRoute(
                    fixture.FinalizeWorkId,
                    finalizeInstanceId,
                    "finalize"),
                finalizeRequest,
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                finalizeReplay,
                HttpStatusCode.OK,
                "P6-10 finalize replay");
            Require(
                ApiHarnessClient.RequiredBool(
                    finalizeReplay.Json,
                    "replayed"),
                "P6-10 finalized receipt did not replay.");
            var finalized = await instances
                .Find(item => item.Id == finalizeInstanceId)
                .SingleAsync();
            var finalizedEpoch = await epochs
                .Find(item =>
                    item.FlowInstanceId == finalizeInstanceId &&
                    item.ExecutionEpoch == 1)
                .SingleAsync();
            Require(
                finalized.State == DynamicFlowInstanceStates.Finalized &&
                finalized.FinalizedExecutionEpoch == 1 &&
                finalized.FinalizedAtUtc.HasValue &&
                finalized.FinalizedByEventId ==
                    ApiHarnessClient.RequiredString(
                        finalize.Json,
                        "eventId") &&
                finalizedEpoch.State ==
                    DynamicFlowExecutionEpochStates.Finalized &&
                finalizedEpoch.IsCanonical &&
                finalizedEpoch.TerminalEventId ==
                    finalized.FinalizedByEventId,
                "P6-10 finalize did not persist an irreversible canonical epoch.");
            var finalizedOverview = await api.GetAsync(
                $"api/works/{fixture.FinalizeWorkId}/dynamic-flows/instances/{finalizeInstanceId}",
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                finalizedOverview,
                HttpStatusCode.OK,
                "P6-10 finalized overview");
            var finalizedOverviewBody = ApiHarnessClient.RequiredObject(
                finalizedOverview.Json,
                "P6-10 finalized overview");
            var finalizedCapabilities = ApiHarnessClient.RequiredObject(
                finalizedOverviewBody["capabilities"],
                "P6-10 finalized capabilities");
            var finalizedReadEpochs = ApiHarnessClient.RequiredArray(
                finalizedOverviewBody["epochs"],
                "P6-10 finalized epochs");
            Require(
                ApiHarnessClient.RequiredString(
                    finalizedOverviewBody,
                    "state") == DynamicFlowInstanceStates.Finalized &&
                ApiHarnessClient.RequiredInt(
                    finalizedOverviewBody,
                    "finalizedExecutionEpoch") == 1 &&
                finalizedReadEpochs.Count == 1 &&
                finalizedReadEpochs[0] is JsonObject finalizedReadEpoch &&
                ApiHarnessClient.RequiredString(
                    finalizedReadEpoch,
                    "state") ==
                        DynamicFlowExecutionEpochStates.Finalized &&
                ApiHarnessClient.RequiredBool(
                    finalizedReadEpoch,
                    "isCanonical") &&
                !ApiHarnessClient.RequiredBool(
                    finalizedCapabilities,
                    "canFinalize") &&
                !ApiHarnessClient.RequiredBool(
                    finalizedCapabilities,
                    "canRollback") &&
                !ApiHarnessClient.RequiredBool(
                    finalizedCapabilities,
                    "canTerminate") &&
                !ApiHarnessClient.RequiredBool(
                    finalizedCapabilities,
                    "canRestart"),
                "P6-10 finalized read projection/capabilities drifted.");
            var finalizedTimeline = await api.GetAsync(
                $"api/works/{fixture.FinalizeWorkId}/dynamic-flows/instances/{finalizeInstanceId}/timeline?limit=50",
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                finalizedTimeline,
                HttpStatusCode.OK,
                "P6-10 finalized timeline");
            var finalizedTimelineItems = ApiHarnessClient.RequiredArray(
                ApiHarnessClient.RequiredObject(
                    finalizedTimeline.Json,
                    "P6-10 finalized timeline")["items"],
                "P6-10 finalized timeline items");
            var finalizedTimelineObserved = finalizedTimelineItems
                .OfType<JsonObject>()
                .Any(item =>
                    ApiHarnessClient.RequiredString(item, "eventId") ==
                        finalized.FinalizedByEventId &&
                    ApiHarnessClient.RequiredString(item, "eventType") ==
                        DynamicFlowFinalizeTopologyContract.FinalizedEvent);
            Require(
                finalizedTimelineObserved,
                "P6-10 finalized event was missing from the runtime timeline.");
            var beforeIrreversible = await CaptureP610LedgerHashAsync(
                database,
                finalizeInstanceId,
                CancellationToken.None);
            var irreversible = await api.PostAsync(
                EpochRoute(
                    fixture.FinalizeWorkId,
                    finalizeInstanceId,
                    "rollback"),
                EpochRequest(
                    "p6-10-rollback-after-finalize",
                    finalized.ExecutionEpoch,
                    finalized.Revision,
                    fixture.EntryNodeId),
                adminToken,
                ct: CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                irreversible,
                HttpStatusCode.BadRequest,
                "P6-10 finalized epoch irreversibility");
            Require(
                beforeIrreversible == await CaptureP610LedgerHashAsync(
                    database,
                    finalizeInstanceId,
                    CancellationToken.None),
                "P6-10 post-finalize rollback changed the ledger.");

            var duplicateRejected = false;
            try
            {
                await epochs.InsertOneAsync(new DynamicFlowExecutionEpoch
                {
                    Id = ObjectId.GenerateNewId().ToString(),
                    FlowInstanceId = finalizeInstanceId,
                    ExecutionEpoch = 1,
                    State = DynamicFlowExecutionEpochStates.Active,
                    CheckpointNodeId = fixture.EntryNodeId,
                    IsCanonical = true,
                    OpenedByCommandId = "p6-10-duplicate",
                    OpenedAtUtc = DateTime.UtcNow,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    CreatedByUserId = finalized.IssuerUserId,
                    UpdatedByUserId = finalized.IssuerUserId,
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
                "P6-10 execution epoch unique index accepted a duplicate.");

            directMongo = new
            {
                rollbackInstanceId = instanceId,
                rollbackEventId,
                rollbackEpochs = epochRows.Select(row => new
                {
                    row.ExecutionEpoch,
                    row.State,
                    row.IsCanonical,
                    row.ReplacedByExecutionEpoch
                }),
                invalidatedStepId = epochOneStep.Id,
                invalidatedGatewayId = gatewayId,
                invalidatedReportId = epochOneReportId,
                invalidatedStatisticIds = statisticIds.Values
                    .OrderBy(value => value, StringComparer.Ordinal),
                replacementStepId = allSteps[1].Id,
                rebuildIntentId = rebuildIntent.Id,
                p8ExecutionEnabled =
                    rebuildIntent.Payload["p8ExecutionEnabled"].AsBoolean,
                prematureFinalizeZeroWriteHash =
                    beforePrematureFinalize,
                forgedZeroWriteHash = beforeForged,
                staleZeroWriteHash = beforeStale,
                raceWinner = winnerAction,
                raceReceiptCount = epochReceiptRows.Count,
                raceEventCount = epochEventRows.Count,
                rollbackReadEpochCount = rollbackReadEpochs.Count,
                rollbackTimelineObserved,
                finalizeInstanceId,
                finalized.FinalizedExecutionEpoch,
                finalized.FinalizedByEventId,
                finalizedEpochState = finalizedEpoch.State,
                finalizedReadEpochCount = finalizedReadEpochs.Count,
                finalizedTimelineObserved,
                irreversibleZeroWriteHash = beforeIrreversible,
                uniqueDuplicateRejected = duplicateRejected
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
            if (raceApiClient is not null)
            {
                exchanges.AddRange(raceApiClient.Exchanges);
                raceApiClient.Dispose();
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
                "p6-10-epoch-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-10-epoch-direct-mongo.json"),
            new { runKey, directMongo });
        var resultPath = Path.Combine(
            iterationRoot,
            "p6-10-epoch-result.json");
        await EvidenceJson.WriteAsync(
            resultPath,
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-013", "P6-TOPO-014" },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-10 epoch probe passed; artifact={resultPath}"
                : $"[FAIL] P6-10 epoch probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={resultPath}");
        return passed ? 0 : 1;
    }

    private static DynamicFlowEpochCommandRequest EpochRequest(
        string commandId,
        int executionEpoch,
        long instanceRevision,
        string? checkpointNodeId = null)
        => new()
        {
            CommandId = commandId,
            ExpectedExecutionEpoch = executionEpoch,
            ExpectedInstanceRevision = instanceRevision,
            CheckpointNodeId = checkpointNodeId,
            Reason = "P6-10 real Mongo/Kestrel probe"
        };

    private static string EpochRoute(
        string workId,
        string instanceId,
        string action)
        => $"api/works/{workId}/dynamic-flows/instances/{instanceId}/{action}";

    private static async Task<DynamicFlowInstance>
        WaitForP610InstanceStateAsync(
            ApiHarnessClient api,
            string adminToken,
            IMongoDatabase database,
            string instanceId,
            string expectedState,
            CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var instance = await database
                .GetCollection<DynamicFlowInstance>(
                    "dynamic_flow_instances")
                .Find(item => item.Id == instanceId && !item.IsDeleted)
                .SingleAsync(ct);
            if (instance.State == expectedState)
                return instance;
            var worker = await api.PostAsync(
                "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
                body: null,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                worker,
                HttpStatusCode.OK,
                $"P6-10 lifecycle worker for {expectedState}");
            await Task.Delay(50, ct);
        }
        var terminal = await database
            .GetCollection<DynamicFlowInstance>(
                "dynamic_flow_instances")
            .Find(item => item.Id == instanceId && !item.IsDeleted)
            .SingleAsync(ct);
        throw new InvalidOperationException(
            $"Instance {instanceId} did not reach {expectedState}; actual={terminal.State}.");
    }

    private static async Task CompleteP610AssignmentAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        DynamicFlowStepInstance step,
        CancellationToken ct)
    {
        var assignmentId = step.AssignmentId
            ?? throw new InvalidOperationException(
                $"Step {step.Id} lacks assignment.");
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var response = await api.PostAsync(
                $"api/work-assignments/{assignmentId}/complete",
                new JsonObject
                {
                    ["completedDate"] = "2026-07-27T00:00:00Z",
                    ["note"] = "P6-10 finalize precondition completion"
                },
                adminToken,
                ct: ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                await WaitForP601StepStateAsync(
                    api,
                    adminToken,
                    database,
                    step.Id,
                    DynamicFlowStepStates.Completed,
                    ct);
                return;
            }
            Require(
                response.StatusCode == HttpStatusCode.Conflict,
                $"P6-10 completion expected 200/409, got {(int)response.StatusCode}.");
            var worker = await api.PostAsync(
                "api/admin/operations/job-runs/lifecycle-projection-outbox/process?maxReports=20",
                body: null,
                adminToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                worker,
                HttpStatusCode.OK,
                "P6-10 completion projection worker");
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException(
            $"P6-10 assignment {assignmentId} completion did not converge.");
    }

    private static async Task<string> CaptureP610LedgerHashAsync(
        IMongoDatabase database,
        string instanceId,
        CancellationToken ct)
    {
        var id = ObjectId.Parse(instanceId);
        var rows = new List<string>();
        var assignmentIds = await database
            .GetCollection<BsonDocument>("work_assignments")
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
                     "dynamic_flow_gateway_instances",
                     "dynamic_flow_execution_epochs",
                     "dynamic_flow_participant_snapshots",
                     "dynamic_flow_runtime_command_receipts",
                     "dynamic_flow_runtime_events",
                     "dynamic_flow_runtime_outbox",
                     "work_assignments"
                 })
        {
            await AddAsync(
                collection,
                new BsonDocument("flowInstanceId", id));
        }
        if (assignmentIdValues.Length > 0)
        {
            var inFilter = new BsonDocument(
                "$in",
                new BsonArray(assignmentIdValues));
            foreach (var collection in new[]
                     {
                         "work_template_assignees",
                         "work_assignment_report",
                         "work_report_field_stat_values",
                         "work_report_table_stat_values",
                         "work_report_label_stat_values"
                     })
            {
                var field = collection == "work_template_assignees" ||
                            collection == "work_assignment_report" ||
                            collection.StartsWith(
                                "work_report_",
                                StringComparison.Ordinal)
                    ? "workAssignmentId"
                    : "assignmentId";
                await AddAsync(
                    collection,
                    new BsonDocument(field, inFilter));
            }
        }
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join("\n", rows))))
            .ToLowerInvariant();

        async Task AddAsync(
            string collectionName,
            FilterDefinition<BsonDocument> filter)
        {
            var documents = await database
                .GetCollection<BsonDocument>(collectionName)
                .Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            rows.Add(collectionName);
            rows.AddRange(documents.Select(document => document.ToJson()));
        }
    }

    private static async Task<P610Fixture>
        ConvertP601FixtureToP610Async(
            IMongoDatabase database,
            P601Fixture seed,
            CancellationToken ct)
    {
        var rootForm = seed.ExpectedSteps
            .Single(step => step.NodeId == "step_a").Form;
        var payload = JsonNode.Parse(seed.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-10 seed payload is invalid.");
        payload["archetypeId"] =
            DynamicFlowFinalizeTopologyContract.ArchetypeId;
        payload["entryStepId"] = "step_a";
        payload["resultOwnerStepId"] = "step_a";
        payload["resultOwnerFormNodeId"] = rootForm.FormNodeId;
        payload["statisticsOwnerStepId"] = "step_a";
        payload["statisticsOwnerFormNodeId"] = rootForm.FormNodeId;
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
            new JsonObject
            {
                ["nodeId"] = "step_a",
                ["nodeCode"] = "EPOCH_ENTRY",
                ["nodeKind"] = DynamicFlowNodeKinds.FormStep,
                ["formNodeId"] = rootForm.FormNodeId,
                ["declaredRoles"] = new JsonArray("ASSIGNEE")
            },
            new JsonObject
            {
                ["nodeId"] = "epoch_gate",
                ["nodeCode"] = "EPOCH_GATE",
                ["nodeKind"] = DynamicFlowNodeKinds.Gateway,
                ["gateway"] = new JsonObject
                {
                    ["kind"] =
                        DynamicFlowGatewayKinds.RollbackFinalize,
                    ["rollbackTargetNodeId"] = "step_a"
                }
            },
            new JsonObject
            {
                ["nodeId"] = "final",
                ["nodeCode"] = "FINAL",
                ["nodeKind"] = DynamicFlowNodeKinds.Final
            });
        payload["edges"] = new JsonArray(
            new JsonObject
            {
                ["transitionId"] = "tr_entry_epoch",
                ["fromNodeId"] = "step_a",
                ["toNodeId"] = "epoch_gate"
            },
            new JsonObject
            {
                ["transitionId"] = "tr_epoch_final",
                ["fromNodeId"] = "epoch_gate",
                ["toNodeId"] = "final"
            });
        payload["actorPolicies"] = new JsonArray();
        payload["fieldPolicies"] = new JsonArray();
        payload["tableColumnPolicies"] = new JsonArray();
        payload["mappingRules"] = new JsonArray();
        payload["finalResultPolicy"] = new JsonObject();
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payload.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        var topology = DynamicFlowFinalizeTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
        Require(
            topology.RollbackTargetNodeId == "step_a",
            "P6-10 fixture did not freeze the exact rollback ancestor.");
        await database.GetCollection<DynamicFlowTemplateVersion>(
                "dynamic_flow_template_versions")
            .UpdateOneAsync(
                item => item.Id == seed.VersionId,
                Builders<DynamicFlowTemplateVersion>.Update
                    .Set(item => item.PayloadJson, canonical.CanonicalJson)
                    .Set(item => item.PayloadHash, canonical.PayloadHash),
                cancellationToken: ct);
        await database.GetCollection<DynamicFlowTemplate>(
                "dynamic_flow_templates")
            .UpdateOneAsync(
                item => item.Id == seed.FamilyId,
                Builders<DynamicFlowTemplate>.Update.Set(
                    item => item.CurrentVersionHash,
                    canonical.PayloadHash),
                cancellationToken: ct);
        return new P610Fixture(
            seed.WorkId,
            seed.OwnerOracleWorkId,
            seed.VersionId,
            seed.TargetUnitId,
            topology.EntryNode.NodeId);
    }

    private sealed record P610Fixture(
        string WorkId,
        string FinalizeWorkId,
        string VersionId,
        string TargetUnitId,
        string EntryNodeId);
}
