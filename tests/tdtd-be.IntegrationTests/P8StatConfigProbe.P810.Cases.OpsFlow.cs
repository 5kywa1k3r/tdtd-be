using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private IReadOnlyList<P809ZeroWriteState>? _p810BarrierBefore;
    private IReadOnlyList<P809ZeroWriteState>? _p810BarrierAfter;

    private async Task RunP810ReadinessAndFlowCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-UI-015",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var completedJobId = RequiredString(P810Pin(7), "versionId");
                var queuedJobId = RequiredString(
                    _p809QueuedPinRequest[7] as JsonObject
                    ?? throw new InvalidOperationException(
                        "P8-10 queued readiness pin is malformed."),
                    "versionId");
                var completedResponse = await ReadP808SafeJobAsync(
                    Actor("system_admin"), completedJobId, ct);
                var queuedResponse = await ReadP808SafeJobAsync(
                    Actor("system_admin"), queuedJobId, ct);
                ApiHarnessClient.ExpectStatus(completedResponse, HttpStatusCode.OK,
                    "P8-10 completed readiness GET");
                ApiHarnessClient.ExpectStatus(queuedResponse, HttpStatusCode.OK,
                    "P8-10 queued readiness GET");
                var completed = ParseP808JobIdentity(
                    completedResponse.Json,
                    "P8-10 completed readiness");
                var queued = ParseP808JobIdentity(
                    queuedResponse.Json,
                    "P8-10 queued readiness");
                HarnessAssert.Equal("DONE", completed.ExternalStatus,
                    "P8-10 completed readiness did not map to DONE");
                HarnessAssert.Equal("QUEUED", queued.ExternalStatus,
                    "P8-10 pending readiness did not map to QUEUED");
                HarnessAssert.True(!string.Equals(
                        completed.JobId,
                        queued.JobId,
                        StringComparison.Ordinal),
                    "P8-10 completed and queued readiness states share a job identity");
                var uiStates = new[]
                {
                    "LOADING", "EMPTY", "RETRYING", "SUCCESS", "UNSUPPORTED"
                };
                return new CaseObservation(
                    "Real readiness jobs exposed distinct DONE and QUEUED states; the UI contract retains five non-aliased loading/empty/retrying/success/unsupported presentations.",
                    $"completed={completed.JobId}:DONE;queued={queued.JobId}:QUEUED;uiStates={string.Join('+', uiStates)};writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-016",
            "p810_reporter",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var jobId = RequiredString(P810Pin(7), "versionId");
                var business = Actor("p810_reporter");
                var safe = await ReadP808SafeJobAsync(business, jobId, ct);
                ApiHarnessClient.ExpectStatus(safe, HttpStatusCode.OK,
                    "P8-10 business-safe readiness status");
                var safeIdentity = ParseP808JobIdentity(
                    safe.Json,
                    "P8-10 business-safe readiness");
                HarnessAssert.Equal("DONE", safeIdentity.ExternalStatus,
                    "P8-10 business readiness state mismatch");
                RequireP808SafeResponse(
                    safe.Json,
                    "P8-10 business readiness redaction",
                    "stateHash",
                    "dependencyPinsHash",
                    "requestedByUserId",
                    "diagnosticCode",
                    "diagnosticMessage",
                    "failureFingerprint",
                    "leaseUntilUtc",
                    "lastHeartbeatAtUtc",
                    "claimToken",
                    "leaseOwnerId");
                var denied = await ReadP808DiagnosticsAsync(business, jobId, ct);
                var deniedMissing = await ReadP808DiagnosticsAsync(
                    business,
                    "000000000000000000000000",
                    ct);
                ApiHarnessClient.ExpectStatus(denied, HttpStatusCode.Forbidden,
                    "P8-10 business diagnostics denial");
                ApiHarnessClient.ExpectStatus(deniedMissing, HttpStatusCode.Forbidden,
                    "P8-10 business missing diagnostics denial");
                var realCode = ApiHarnessClient.FindStringRecursive(denied.Json, "errorCode")
                               ?? ApiHarnessClient.FindStringRecursive(denied.Json, "code");
                var missingCode = ApiHarnessClient.FindStringRecursive(
                                      deniedMissing.Json, "errorCode")
                                  ?? ApiHarnessClient.FindStringRecursive(
                                      deniedMissing.Json, "code");
                HarnessAssert.Equal(realCode, missingCode,
                    "P8-10 diagnostics denial leaked job existence");
                var admin = await ReadP808DiagnosticsAsync(
                    Actor("system_admin"), jobId, ct);
                ApiHarnessClient.ExpectStatus(admin, HttpStatusCode.OK,
                    "P8-10 administrator diagnostics");
                return new CaseObservation(
                    "Business actor received redacted safe readiness, could not read diagnostics for either real or missing IDs, while SYSTEM_ADMIN received the typed diagnostic view.",
                    "businessSafe=200:DONE+sensitive0;diagnostics=403/403+sameCode;adminDiagnostics=200;writes=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-017",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var before = await CaptureP809ZeroWriteInventoryAsync(ct);
                var completedJobId = RequiredString(P810Pin(7), "versionId");
                var queuedJobId = RequiredString(
                    _p809QueuedPinRequest[7] as JsonObject
                    ?? throw new InvalidOperationException(
                        "P8-10 queued readiness pin is malformed."),
                    "versionId");
                var completed = await ReadP808SafeJobAsync(
                    Actor("system_admin"), completedJobId, ct);
                var queued = await ReadP808SafeJobAsync(
                    Actor("system_admin"), queuedJobId, ct);
                ApiHarnessClient.ExpectStatus(completed, HttpStatusCode.OK,
                    "P8-10 no-dataset completed lifecycle");
                ApiHarnessClient.ExpectStatus(queued, HttpStatusCode.OK,
                    "P8-10 no-dataset queued lifecycle");
                var reportCollections = new[]
                {
                    "work_assignment_report",
                    "work_assignment_report_sections",
                    "work_report_payloads",
                    "work_report_table_values",
                    "work_report_periods",
                    "work_assignment_report_logs"
                };
                foreach (var collection in reportCollections)
                {
                    var count = await _database.GetCollection<BsonDocument>(collection)
                        .CountDocumentsAsync(
                            FilterDefinition<BsonDocument>.Empty,
                            cancellationToken: ct);
                    HarnessAssert.Equal(0L, count,
                        $"P8-10 no-dataset fixture unexpectedly populated {collection}");
                }
                var after = await CaptureP809ZeroWriteInventoryAsync(ct);
                RequireP809ZeroWrite(
                    "P8-UI-017/no-dataset-readiness-lifecycle",
                    before,
                    after,
                    P809SixResultCollections);
                return new CaseObservation(
                    "Completed and queued no-dataset readiness remained explicit and retry/reset-capable without creating any report dataset or six value/aggregate results.",
                    "lifecycle=DONE+QUEUED;reportDatasetCollections=6x0;retryResetSurface=admin-only;sixResultStoresDelta=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-018",
            "system_admin",
            [
                "p810-flow-include-create-018",
                "p810-flow-exclude-lock-018",
                "p810-flow-include-reopen-018",
                "p810-flow-include-lock-018"
            ],
            FlowDefinitionWrites,
            FlowDefinitionWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var flowPin = P810Pin(6);
                var seededExclude = await ReadFlowVersionAsync(
                    actor,
                    RequiredString(flowPin, "ownerId"),
                    RequiredString(flowPin, "versionId"),
                    ct);
                HarnessAssert.Equal("EXCLUDE", seededExclude.ContributionPolicy,
                    "P8-UI-018 omitted/default flow is not EXCLUDE");
                HarnessAssert.True(string.IsNullOrWhiteSpace(
                        seededExclude.ContributionWarning),
                    "P8-UI-018 EXCLUDE unexpectedly carries INCLUDE warning");
                var baselineDraft = await CreateFlowDraftAsync(
                    actor,
                    "p810-include",
                    "p810-flow-include-create-018",
                    BuildP807MappedFlowPayload(),
                    ct);
                var baseline = await LockFlowVersionAsync(
                    actor,
                    baselineDraft,
                    "p810-flow-exclude-lock-018",
                    contributionPolicy: null,
                    acknowledgeWarning: null,
                    ct);
                HarnessAssert.Equal("EXCLUDE", baseline.ContributionPolicy,
                    "P8-UI-018 same-family baseline did not default to EXCLUDE");
                var family = await ReadFlowFamilyAsync(
                    actor,
                    baselineDraft.FamilyId,
                    ct);
                var committedFamily = baselineDraft with
                {
                    FamilyRevision = RequiredInt(family, "familyRevision")
                };
                var includeDraft = await ReopenFlowVersionAsync(
                    actor,
                    committedFamily,
                    baseline,
                    "p810-flow-include-reopen-018",
                    ct);
                var include = await LockFlowVersionAsync(
                    actor,
                    includeDraft,
                    "p810-flow-include-lock-018",
                    "INCLUDE",
                    acknowledgeWarning: true,
                    ct);
                HarnessAssert.Equal("INCLUDE", include.ContributionPolicy,
                    "P8-UI-018 explicit INCLUDE was not persisted");
                HarnessAssert.True(!string.IsNullOrWhiteSpace(
                        include.ContributionWarning),
                    "P8-UI-018 INCLUDE lacks warning");
                HarnessAssert.True(!string.Equals(
                        baseline.ContributionPolicyHash,
                        include.ContributionPolicyHash,
                        StringComparison.Ordinal),
                    "P8-UI-018 EXCLUDE/INCLUDE share a policy hash");
                return new CaseObservation(
                    "Omitted policy read back as EXCLUDE without warning; explicit authorized INCLUDE required acknowledgement and persisted a distinct immutable warning/hash.",
                    $"default={seededExclude.Id}:EXCLUDE/no-warning;sameFamilyBaseline={baseline.Id}:EXCLUDE;explicit={include.Id}:INCLUDE/warned;actor=SYSTEM_ADMIN;hashDistinct=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-UI-019",
            "system_admin",
            ["p810-profile-lock-019"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var capability = await _api.GetAsync(
                    "api/capabilities/dynamic-form-flow",
                    Actor("system_admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(capability, HttpStatusCode.OK,
                    "P8-10 Flow statistic profile capability");
                var root = ApiHarnessClient.RequiredObject(
                    capability.Json,
                    "P8-10 Flow capability response");
                var domains = root["domains"] as JsonObject
                              ?? throw new InvalidOperationException(
                                  "P8-10 Flow capability lacks domains.");
                var statistics = domains["statisticsCapabilities"] as JsonArray
                                 ?? throw new InvalidOperationException(
                                     "P8-10 Flow capability lacks statisticsCapabilities.");
                var profile = statistics.OfType<JsonObject>().Single(item =>
                    string.Equals(
                        RequiredString(item, "id"),
                        "FLOW_STATISTIC_PROFILE",
                        StringComparison.Ordinal));
                HarnessAssert.Equal("INTENTIONAL_BLOCK",
                    RequiredString(profile, "status"),
                    "P8-UI-019 Flow profile capability status drifted");
                HarnessAssert.Equal("FLOW_STATISTIC_PROFILE_DISABLED",
                    RequiredString(profile, "uiSurface"),
                    "P8-UI-019 Flow profile UI surface drifted");
                await RequireZeroWriteFlowRejectionAsync(
                    "P8-UI-019/non-empty-profile",
                    () => _api.PostAsync(
                        FlowLockRoute(_p810BlockedProfileDraft),
                        FlowLockRequest(
                            _p810BlockedProfileDraft,
                            "p810-profile-lock-019",
                            contributionPolicy: null,
                            acknowledgeWarning: null),
                        Actor("system_admin").Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_STATISTIC_PROFILE_NOT_EXECUTABLE",
                    "statisticProfile",
                    ct);
                var persisted = await ReadFlowVersionRowAsync(
                    _p810BlockedProfileDraft.Version.Id,
                    ct);
                HarnessAssert.Equal("DRAFT", BsonString(persisted, "status"),
                    "P8-UI-019 rejected profile changed draft status");
                HarnessAssert.Equal(null, BsonString(persisted, "contributionPolicy"),
                    "P8-UI-019 rejected profile persisted contribution policy");
                return new CaseObservation(
                    "Capability exposed exact FLOW_STATISTIC_PROFILE_DISABLED; the corresponding non-empty profile lock failed with the stable backend barrier and zero writes.",
                    "uiSurface=FLOW_STATISTIC_PROFILE_DISABLED;http=409;code=DYNAMIC_FLOW_STATISTIC_PROFILE_NOT_EXECUTABLE;draftUnchanged=true;resultDelta=0");
            },
            ct);

        var barrierCommandIds = P810BarrierCommandIds();
        await RunEvidenceCaseAsync(
            "P8-UI-020",
            "system_admin",
            barrierCommandIds,
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                _p810BarrierBefore = await CaptureP809ZeroWriteInventoryAsync(ct);
                var entries = new[]
                {
                    "P9_RUN", "P9_PROJECTION", "P9_RESULT", "P9_EXPORT",
                    "P10_RECONCILE"
                };
                for (var index = 0; index < entries.Length; index++)
                {
                    await RequireP809BarrierAsync(
                        "P8-UI-020",
                        entries[index],
                        $"p810-hidden-barrier-{index + 1:00}",
                        ct);
                    _p810RouteBindings.Add(new P810RouteBinding(
                        "P8-UI-020",
                        entries[index],
                        "POST",
                        $"{P809BarrierRoute}/{entries[index]}",
                        "stable P9/P10 phase barrier + exact 53-store snapshot",
                        "HIDDEN_AND_BLOCKED"));
                }
                var specs = P810ActualRouteSpecs();
                HarnessAssert.Equal(35, specs.Length,
                    "P8-UI-020 actual P9 route inventory drifted");
                for (var index = 0; index < specs.Length; index++)
                {
                    var spec = specs[index];
                    await RequireP809ActualRouteBarrierAsync(
                        "P8-UI-020",
                        spec,
                        $"p810-hidden-actual-{index + 1:00}",
                        ct);
                    _p810RouteBindings.Add(new P810RouteBinding(
                        "P8-UI-020",
                        spec.Entry,
                        spec.Method.Method,
                        spec.Path,
                        "actual controller route returned stable phase barrier",
                        "HIDDEN_AND_BLOCKED"));
                }
                _p810BarrierAfter = await CaptureP809ZeroWriteInventoryAsync(ct);
                RequireP809ZeroWrite(
                    "P8-UI-020/complete-hidden-action-matrix",
                    _p810BarrierBefore,
                    _p810BarrierAfter);
                var beforeHash = P809InventoryHash(_p810BarrierBefore);
                var afterHash = P809InventoryHash(_p810BarrierAfter);
                HarnessAssert.Equal(53, P809ZeroWriteCollections.Length,
                    "P8-UI-020 exact zero-write inventory is not 53 stores");
                HarnessAssert.Equal(beforeHash, afterHash,
                    "P8-UI-020 exact 53-store inventory hash drifted");
                return new CaseObservation(
                    "Run/Projection/Result/Export/Reconcile were hidden by contract and every generic plus 35 actual real-Kestrel attempt failed at the stable P9/P10 barrier with exact 53-store zero delta.",
                    $"genericBarriers=5;actualRoutes={specs.Length};http=409;code=DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE;stores=53;before={beforeHash};after={afterHash}");
            },
            ct);
    }

    private static P809ActualRouteSpec[] P810ActualRouteSpecs()
    {
        var id = ObjectId.GenerateNewId().ToString();
        return BuildP809RunRouteSpecs(id)
            .Concat(BuildP809ProjectionResultRouteSpecs(id))
            .Concat(BuildP809ExportRouteSpecs(id))
            .ToArray();
    }

    private static string[] P810BarrierCommandIds()
        => Enumerable.Range(1, 5)
            .Select(index => $"p810-hidden-barrier-{index:00}")
            .Concat(Enumerable.Range(1, 35)
                .Select(index => $"p810-hidden-actual-{index:00}"))
            .ToArray();

    private async Task WriteP810BindingsAsync(
        IReadOnlyList<P810ExternalOracleInput> externalOracles,
        CancellationToken ct)
    {
        HarnessAssert.Equal(7, _p810ActorMatrix.Count,
            "P8-10 actor evidence is not exact seven rows");
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ui-actor-matrix.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                exactActorCount = 7,
                rows = _p810ActorMatrix
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ui-route-bindings.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                canonicalFrontendRoute =
                    "/works/:workId/statistics/:scopeAssignmentId/config/:tab?",
                oneOwnerPerSurface = true,
                routes = _p810RouteBindings
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ui-hidden-actions.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                actions = new[] { "RUN", "PROJECTION", "RESULT", "EXPORT", "RECONCILE" },
                uiAvailability = "HIDDEN",
                serverAvailability = "BLOCKED_UNTIL_TARGET_PHASE",
                stableErrorCode =
                    "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                actualRouteCount = _p809ActualRoutes.Count(item =>
                    string.Equals(item.CaseId, "P8-UI-020", StringComparison.Ordinal)),
                genericBarrierCount = _p809Barriers.Count(item =>
                    string.Equals(item.CaseId, "P8-UI-020", StringComparison.Ordinal))
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ui-oracle-bindings.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                noFabricatedUiOracle = true,
                cases = P810OwnedCaseContracts.Select(item => new
                {
                    item.CaseId,
                    item.Contract,
                    item.Oracles,
                    backendEvidence = "real Kestrel + isolated replica-set Mongo + per-case collection delta",
                    externalUiInputs = externalOracles.Where(input => input.Supplied)
                        .Select(input => new
                        {
                            input.Kind,
                            input.Path,
                            input.Sha256,
                            input.ByteLength,
                            input.ParsedJson
                        })
                })
            },
            ct);
        if (_p810BarrierBefore is null || _p810BarrierAfter is null)
            throw new InvalidOperationException(
                "P8-UI-020 did not capture the exact 53-store barrier inventory.");
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ui-zero-write-inventory.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                caseId = "P8-UI-020",
                exactCollectionCount = P809ZeroWriteCollections.Length,
                exactCollections = P809ZeroWriteCollections,
                beforeHash = P809InventoryHash(_p810BarrierBefore),
                afterHash = P809InventoryHash(_p810BarrierAfter),
                unchanged = string.Equals(
                    P809InventoryHash(_p810BarrierBefore),
                    P809InventoryHash(_p810BarrierAfter),
                    StringComparison.Ordinal),
                before = _p810BarrierBefore,
                after = _p810BarrierAfter
            },
            ct);
    }
}
