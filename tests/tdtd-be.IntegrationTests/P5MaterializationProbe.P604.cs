using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Bounded P6-04 executable evidence for FLOW-T06 JOIN_ANY. The probe proves
/// transactional rollback, one CAS winner, gateway cancellation, stable late
/// audit, the T07..T12 zero-write barrier, direct Mongo state and cleanup.
/// </summary>
internal static partial class P5MaterializationProbe
{
    public static async Task<int> RunP604Async()
    {
        var runKey =
            $"p604_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var paths = HarnessPaths.Create(runKey);
        var iterationRoot = paths.IterationRoot(1);
        var cleanupErrors = new List<string>();
        var exchanges = new List<ApiExchangeEvidence>();
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? apiClient = null;
        object? barrierEvidence = null;
        object? winnerEvidence = null;
        object? directMongoEvidence = null;
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
                "backend-p6-join-quorum",
                6);
            var api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            var (adminToken, adminPassword) =
                await BootstrapAndLoginAsync(
                    api,
                    backend,
                    database,
                    CancellationToken.None);
            var p5Fixture = await SeedFixtureAsync(
                database,
                CancellationToken.None);
            var seed = await SeedP601FixtureAsync(
                database,
                p5Fixture,
                CancellationToken.None);
            var fixture = await ConvertP601FixtureToP604Async(
                database,
                seed,
                CancellationToken.None);
            barrierEvidence = await AssertP604FutureBarrierAsync(
                api,
                adminToken,
                database,
                p5Fixture,
                CancellationToken.None);

            var launch = await LaunchAsync(
                api,
                adminToken,
                p5Fixture,
                fixture.WorkId,
                fixture.VersionId,
                "p6-04-t06-launch",
                [fixture.TargetUnitId],
                P603PeriodKey,
                CancellationToken.None);
            RequireStatus(
                launch.Confirm,
                "SUCCEEDED",
                "P6-04 FLOW-T06 launch");
            var instanceId = ApiHarnessClient.RequiredString(
                launch.Confirm.Json,
                "flowInstanceId");
            await ReconcileP603Async(
                api,
                adminToken,
                instanceId,
                CancellationToken.None);
            var entry = await ApproveP601StepAsync(
                api,
                backend,
                adminToken,
                database,
                (await LoadP601StepsAsync(
                    database,
                    instanceId,
                    CancellationToken.None)).Single(),
                "field_note",
                "P5_NOTE",
                "p604-a",
                CancellationToken.None);
            var capability = await ReadP601ForwardCapabilityAsync(
                api,
                adminToken,
                fixture.WorkId,
                instanceId,
                entry.Id,
                expectedCanForward: true,
                CancellationToken.None);
            var forward = await ForwardP601Async(
                api,
                adminToken,
                fixture.WorkId,
                entry.AssignmentId!,
                "p6-04-t06-fanout",
                capability.InstanceRevision,
                capability.StepRevision,
                CancellationToken.None);
            AssertP601ForwardSuccess(
                forward,
                replayed: false,
                "T06 A -> B/C");
            var branches = await WaitForP603BranchesAsync(
                database,
                instanceId,
                CancellationToken.None);
            for (var index = 0; index < branches.Count; index++)
            {
                branches[index] = await ApproveP601StepAsync(
                    api,
                    backend,
                    adminToken,
                    database,
                    branches[index],
                    null,
                    null,
                    $"p604-{branches[index].FlowStepCode.ToLowerInvariant()}",
                    CancellationToken.None);
            }

            var winner = branches[0];
            var loser = branches[1];
            var winnerAssignmentId = winner.AssignmentId!;
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
                "backend-p6-join-quorum-fault",
                6,
                $"assignment-completed:{winnerAssignmentId}",
                [
                    DynamicFlowRuntimeStateProjectionFaultPoints
                        .AfterJoinGatewayWrite
                ]);
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);
            var failedWinner = await CompleteP604AssignmentAsync(
                api,
                adminToken,
                winnerAssignmentId,
                winner.FlowStepCode,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                failedWinner,
                HttpStatusCode.InternalServerError,
                "P6-04 injected quorum failure");
            var rolledBackGateway = await LoadP603GatewayAsync(
                database,
                instanceId,
                CancellationToken.None);
            Require(
                rolledBackGateway.State ==
                    DynamicFlowGatewayStates.Collecting &&
                rolledBackGateway.ArrivedContributionIds.Count == 0 &&
                rolledBackGateway.CancelledContributionIds.Count == 0,
                "P6-04 gateway writes survived the injected transaction rollback.");

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
                "backend-p6-join-quorum-restarted",
                6);
            api = new ApiHarnessClient(backend.BaseUri);
            apiClient = api;
            adminToken = await api.LoginAsync(
                "admin",
                adminPassword,
                CancellationToken.None);
            var recoveredWinner = await CompleteP604AssignmentAsync(
                api,
                adminToken,
                winnerAssignmentId,
                winner.FlowStepCode,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                recoveredWinner,
                HttpStatusCode.OK,
                "P6-04 recovered quorum winner");
            await WaitForP602InstanceStateAsync(
                api,
                adminToken,
                database,
                instanceId,
                DynamicFlowInstanceStates.Completed,
                CancellationToken.None);
            await WaitForP601StepStateAsync(
                api,
                adminToken,
                database,
                loser.Id,
                DynamicFlowStepStates.CancelledByGateway,
                CancellationToken.None);
            var gatewayAfterWinner = await LoadP603GatewayAsync(
                database,
                instanceId,
                CancellationToken.None);
            Require(
                gatewayAfterWinner.State ==
                    DynamicFlowGatewayStates.Satisfied &&
                gatewayAfterWinner.ArrivedContributionIds.Count == 1 &&
                gatewayAfterWinner.CancelledContributionIds.Count == 1 &&
                gatewayAfterWinner.LateContributionIds.Count == 0 &&
                gatewayAfterWinner.WinnerContributionId ==
                    winner.ContributionId,
                "P6-04 did not converge to one exact ANY winner.");
            var winnerReleasedAt = gatewayAfterWinner.ReleasedAtUtc;
            winnerEvidence = new
            {
                injectedRollback = true,
                gatewayAfterWinner.State,
                gatewayAfterWinner.WinnerContributionId,
                arrived = gatewayAfterWinner.ArrivedContributionIds,
                cancelled = gatewayAfterWinner.CancelledContributionIds
            };

            var lateCompletion = await CompleteP604AssignmentAsync(
                api,
                adminToken,
                loser.AssignmentId!,
                loser.FlowStepCode,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                lateCompletion,
                HttpStatusCode.OK,
                "P6-04 late ignored completion");
            var lateReplay = await CompleteP604AssignmentAsync(
                api,
                adminToken,
                loser.AssignmentId!,
                loser.FlowStepCode,
                CancellationToken.None);
            ApiHarnessClient.ExpectStatus(
                lateReplay,
                HttpStatusCode.OK,
                "P6-04 late completion replay");
            var gatewayAfterLate = await LoadP603GatewayAsync(
                database,
                instanceId,
                CancellationToken.None);
            Require(
                gatewayAfterLate.WinnerContributionId ==
                    gatewayAfterWinner.WinnerContributionId &&
                gatewayAfterLate.ReleasedAtUtc == winnerReleasedAt &&
                gatewayAfterLate.ArrivedContributionIds.SequenceEqual(
                    gatewayAfterWinner.ArrivedContributionIds,
                    StringComparer.Ordinal) &&
                gatewayAfterLate.LateContributionIds.SequenceEqual(
                    new[] { loser.ContributionId! },
                    StringComparer.Ordinal),
                "LATE_IGNORED changed the canonical winner or release.");
            directMongoEvidence = await AssertP604DirectMongoAsync(
                api,
                adminToken,
                database,
                fixture,
                instanceId,
                winner.ContributionId!,
                loser.ContributionId!,
                CancellationToken.None);
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
                    cleanupErrors.Add($"backend-stop: {error.Message}");
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
                    cleanupErrors.Add($"database-drop: {error.Message}");
                }
                try { await mongo.StopProcessAsync(); }
                catch (Exception error)
                {
                    cleanupErrors.Add($"mongo-stop: {error.Message}");
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
                "p6-04-join-quorum-api-exchanges.json"),
            exchanges);
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-04-join-quorum-direct-mongo.json"),
            new
            {
                runKey,
                barrierEvidence,
                winnerEvidence,
                directMongoEvidence
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(
                iterationRoot,
                "p6-04-join-quorum-result.json"),
            new
            {
                runKey,
                verdict = passed ? "PASS" : "FAIL",
                requirements = new[] { "P6-TOPO-007" },
                failure,
                cleanupErrors,
                artifactRoot = iterationRoot,
                completedAtUtc = DateTime.UtcNow
            });
        Console.WriteLine(
            passed
                ? $"[PASS] P6-04 JOIN quorum probe passed; artifact={Path.Combine(iterationRoot, "p6-04-join-quorum-result.json")}"
                : $"[FAIL] P6-04 JOIN quorum probe failed: {failure ?? string.Join("; ", cleanupErrors)}; artifact={Path.Combine(iterationRoot, "p6-04-join-quorum-result.json")}");
        return passed ? 0 : 1;
    }

    private static async Task<P602Fixture> ConvertP601FixtureToP604Async(
        IMongoDatabase database,
        P601Fixture seed,
        CancellationToken ct)
    {
        var fixture = await ConvertP601FixtureToP603Async(
            database,
            seed,
            ct);
        var payload = JsonNode.Parse(fixture.CanonicalPayload)?.AsObject()
                      ?? throw new InvalidOperationException(
                          "P6-04 seed payload is invalid.");
        payload["archetypeId"] =
            DynamicFlowJoinQuorumTopologyContract.ArchetypeId;
        var gateway = payload["nodes"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node =>
                node["nodeId"]!.GetValue<string>() == "join_j")
            ["gateway"]!.AsObject();
        gateway["kind"] = DynamicFlowGatewayKinds.JoinAny;
        gateway["requiredIncomingCount"] = 1;
        var canonical =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                payload.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        DynamicFlowJoinQuorumTopologyContract.Require(
            canonical.CanonicalJson,
            canonical.PayloadHash);
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
        return fixture with
        {
            CanonicalPayload = canonical.CanonicalJson,
            PayloadHash = canonical.PayloadHash
        };
    }

    private static async Task<object> AssertP604FutureBarrierAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        ProbeFixture fixture,
        CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var barrier in fixture.BarrierFixtures
                     .Where(item =>
                         string.CompareOrdinal(
                             item.ArchetypeId,
                             "FLOW-T07") >= 0)
                     .OrderBy(
                         item => item.ArchetypeId,
                         StringComparer.Ordinal))
        {
            var request = new DynamicFlowPreflightRequest
            {
                FlowTemplateVersionId = barrier.VersionId,
                CommandId =
                    $"p6-04-blocked-{barrier.ArchetypeId.ToLowerInvariant()}",
                TargetUnitIds = [fixture.TargetUnitIds[0]],
                PeriodKey = $"2026-07-{barrier.ArchetypeId}",
                ScheduleIdentityJson = "{}"
            };
            var before = await CaptureP601BusinessCountsAsync(
                database,
                ct);
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
                ApiHarnessClient.RequiredString(
                    preflight.Json,
                    "eligibility") ==
                DynamicFlowRuntimeEligibilityPolicy.BlockedPhase,
                $"{barrier.ArchetypeId} must remain blocked.");
            var confirm = await api.PostAsync(
                $"api/works/{barrier.WorkId}/dynamic-flows/confirm",
                new DynamicFlowConfirmRequest
                {
                    FlowTemplateVersionId = request.FlowTemplateVersionId,
                    CommandId = request.CommandId,
                    TargetUnitIds = request.TargetUnitIds,
                    PeriodKey = request.PeriodKey,
                    ScheduleIdentityJson =
                        request.ScheduleIdentityJson,
                    SnapshotToken = ApiHarnessClient.RequiredString(
                        preflight.Json,
                        "snapshotToken")
                },
                adminToken,
                ct: ct);
            var after = await CaptureP601BusinessCountsAsync(database, ct);
            Require(
                ApiHarnessClient.RequiredString(
                    confirm.Json,
                    "status") ==
                "BLOCKED_UNTIL_TARGET_PHASE" &&
                before.All(pair =>
                    after.TryGetValue(pair.Key, out var count) &&
                    count == pair.Value),
                $"{barrier.ArchetypeId} crossed the zero-write barrier.");
            rows.Add(new { barrier.ArchetypeId, zeroWrite = true });
        }
        Require(
            rows.Count == 6,
            "P6-04 must verify the complete T07..T12 barrier.");
        return new { count = rows.Count, rows };
    }

    private static Task<ApiHarnessResponse> CompleteP604AssignmentAsync(
        ApiHarnessClient api,
        string adminToken,
        string assignmentId,
        string nodeCode,
        CancellationToken ct)
        => api.PostAsync(
            $"api/work-assignments/{assignmentId}/complete",
            new JsonObject
            {
                ["completedDate"] = "2026-07-27T00:00:00Z",
                ["note"] =
                    $"P6-04 JOIN quorum contribution {nodeCode}"
            },
            adminToken,
            ct: ct);

    private static async Task<object> AssertP604DirectMongoAsync(
        ApiHarnessClient api,
        string adminToken,
        IMongoDatabase database,
        P602Fixture fixture,
        string instanceId,
        string winnerContributionId,
        string lateContributionId,
        CancellationToken ct)
    {
        var instance = await LoadP601InstanceAsync(
            database,
            instanceId,
            ct);
        var gateway = await LoadP603GatewayAsync(
            database,
            instanceId,
            ct);
        var contributions = await database
            .GetCollection<DynamicFlowGatewayContribution>(
                "dynamic_flow_gateway_contributions")
            .Find(item => item.FlowInstanceId == instanceId)
            .ToListAsync(ct);
        var steps = await LoadP601StepsAsync(database, instanceId, ct);
        var events = await database.GetCollection<DynamicFlowRuntimeEvent>(
                "dynamic_flow_runtime_events")
            .Find(item => item.FlowInstanceId == instanceId)
            .SortBy(item => item.Sequence)
            .ToListAsync(ct);
        Require(
            instance.ArchetypeId ==
                DynamicFlowJoinQuorumTopologyContract.ArchetypeId &&
            instance.TopologySnapshotHash == fixture.PayloadHash &&
            instance.State == DynamicFlowInstanceStates.Completed,
            "P6-04 immutable pins or terminal state drifted.");
        Require(
            gateway.GatewayKind == DynamicFlowGatewayKinds.JoinAny &&
            gateway.RequiredContributionCount == 1 &&
            gateway.State == DynamicFlowGatewayStates.Satisfied &&
            gateway.WinnerContributionId == winnerContributionId &&
            gateway.ArrivedContributionIds.SequenceEqual(
                new[] { winnerContributionId },
                StringComparer.Ordinal) &&
            gateway.CancelledContributionIds.SequenceEqual(
                new[] { lateContributionId },
                StringComparer.Ordinal) &&
            gateway.LateContributionIds.SequenceEqual(
                new[] { lateContributionId },
                StringComparer.Ordinal),
            "P6-04 gateway quorum/cancel/late ledger drifted.");
        Require(
            contributions.Count == 2 &&
            contributions.Count(item =>
                item.EffectiveStatus ==
                DynamicFlowGatewayContributionOutcomes.Effective) == 1 &&
            contributions.Count(item =>
                item.EffectiveStatus ==
                DynamicFlowGatewayContributionOutcomes.LateIgnored) == 1 &&
            steps.Single(item =>
                    item.ContributionId == lateContributionId)
                .State ==
                DynamicFlowStepStates.CancelledByGateway,
            "P6-04 contribution or cancelled step ledger drifted.");
        Require(
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes
                    .JoinContributionAccepted) == 1 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes
                    .JoinQuorumSatisfied) == 1 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes
                    .JoinBranchCancelled) == 1 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes
                    .JoinLateIgnored) == 1 &&
            events.Count(item =>
                item.EventType ==
                DynamicFlowRuntimeStateProjectionEventTypes
                    .InstanceCompleted) == 1 &&
            events.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() ==
                events.Count &&
            events.Select(item => item.Sequence)
                .SequenceEqual(
                    Enumerable.Range(1, events.Count)
                        .Select(value => (long)value)),
            "P6-04 event chain has a duplicate, gap or orphan.");

        var overview = await api.GetAsync(
            $"api/works/{fixture.WorkId}/dynamic-flows/instances/{instanceId}",
            adminToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            overview,
            HttpStatusCode.OK,
            "P6-04 gateway read model");
        var row = ApiHarnessClient.RequiredArray(
            ApiHarnessClient.RequiredObject(
                overview.Json,
                "P6-04 overview")["gateways"],
            "P6-04 gateways").Single();
        Require(
            row!["requiredContributionCount"]!.GetValue<int>() == 1 &&
            ApiHarnessClient.RequiredArray(
                row["cancelledContributionIds"],
                "P6-04 cancelled read").Count == 1 &&
            ApiHarnessClient.RequiredArray(
                row["lateContributionIds"],
                "P6-04 late read").Count == 1 &&
            row["winnerContributionId"]!.GetValue<string>() ==
                winnerContributionId,
            "P6-04 read model omitted quorum/cancel/late/winner.");
        return new
        {
            instanceId,
            gateway.GatewayInstanceId,
            gateway.RequiredContributionCount,
            gateway.WinnerContributionId,
            arrived = gateway.ArrivedContributionIds,
            cancelled = gateway.CancelledContributionIds,
            late = gateway.LateContributionIds,
            contributionLedgerCount = contributions.Count,
            eventCount = events.Count,
            duplicateCount = 0,
            orphanCount = 0,
            readProjectionVerified = true
        };
    }
}
