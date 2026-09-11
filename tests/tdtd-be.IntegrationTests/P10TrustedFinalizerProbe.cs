using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Focused executable coverage for the trusted initial-finalization seam. It
/// uses an owned local replica set and deletes its isolated .build directory;
/// it neither reads nor writes P10 evidence artifacts.
/// </summary>
internal static class P10TrustedFinalizerProbe
{
    internal const string CommandLineSwitch = "--p10-trusted-finalizer";
    private const string ChainId = "p10_chain_20260810002129_9f56";
    private const string ComparisonBinding =
        "615bdb6f6ddee2c0acb6ab68089dc387a52604c705f2af977330e2362fa6db3f";

    internal static async Task<int> RunAsync()
    {
        var backendRoot = FindBackendRoot();
        var workspaceRoot = Directory.GetParent(backendRoot)?.FullName
            ?? throw new InvalidOperationException("WORKSPACE_ROOT_MISSING");
        var runKey =
            $"p10finalizer_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var runRoot = Path.Combine(
            backendRoot,
            ".build",
            "p10-trusted-finalizer",
            runKey);
        var iterationRoot = Path.Combine(runRoot, "iteration-01");
        Directory.CreateDirectory(iterationRoot);
        var paths = new HarnessPaths(
            workspaceRoot,
            backendRoot,
            Path.Combine(backendRoot, ".build", "p10-trusted-finalizer"),
            runRoot);

        MongoReplicaSetLease? mongo = null;
        Exception? failure = null;
        var cleanupErrors = new List<string>();
        try
        {
            mongo = await MongoReplicaSetLease.StartP10Async(
                paths,
                iterationRoot,
                runKey,
                1,
                CancellationToken.None);
            await ExecuteAsync(mongo);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (mongo is not null)
            {
                try
                {
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add("database:" + exception.Message);
                }

                try
                {
                    await mongo.StopProcessAsync();
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add("process:" + exception.Message);
                }

                try
                {
                    mongo.RemoveDataDirectoryGuarded();
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add("data:" + exception.Message);
                }

                try
                {
                    await mongo.DisposeAsync();
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add("dispose:" + exception.Message);
                }
            }

            try
            {
                RemoveRunRootGuarded(backendRoot, runRoot);
            }
            catch (Exception exception)
            {
                cleanupErrors.Add("run-root:" + exception.Message);
            }
        }

        if (failure is not null || cleanupErrors.Count != 0)
        {
            Console.Error.WriteLine(failure);
            foreach (var cleanup in cleanupErrors)
                Console.Error.WriteLine("CLEANUP " + cleanup);
            Console.WriteLine("FAIL P10-FINALIZER-INTEGRATION");
            return 1;
        }

        Console.WriteLine(
            "PASS P10-FINALIZER-INTEGRATION cases=5 " +
            "commitPending=true exactOneWinnerCas=true exactReplay=true " +
            "staleCasZeroVerdict=true changedCurrentConflict=true " +
            "finalVerdict=true reviewPost=true p9Writes=0 cleanup=true");
        return 0;
    }

    private static async Task ExecuteAsync(MongoReplicaSetLease mongo)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MongoOptions
        {
            ConnectionString = mongo.ConnectionString,
            Database = mongo.DatabaseName
        });
        var context = new MongoDbContext(options);
        var activation = new FinalizerActivation();
        var time = new FixedTimeService(
            new DateTime(2026, 8, 12, 4, 0, 0, DateTimeKind.Utc));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "p10-finalizer-focused-test-signing-key",
                ["StatisticReconciliation:MaxRetryCount"] = "5",
                ["StatisticReconciliation:LeaseSeconds"] = "60",
                ["StatisticReconciliation:SlaSeconds"] = "600"
            })
            .Build();
        var runService = new StatisticReconciliationRunService(
            context,
            activation,
            new FinalizerStatRunActivation(),
            time,
            configuration,
            cache);
        var verdictBackend = new StatisticReconciliationReviewMongoBackend(
            context);
        var verdictPublisher =
            new StatisticReconciliationFinalVerdictPublisher(verdictBackend);
        var finalizer = new StatisticReconciliationTrustedFinalizer(
            context,
            activation,
            time,
            verdictPublisher,
            verdictBackend);

        await CreateFinalVerdictActualTupleIndexAsync(context);

        var systemActor = Actor("000000000000000000000001", "system-writer");
        var success = await CreateCommittedPendingAsync(
            context,
            runService,
            activation.Binding,
            time.UtcNow,
            systemActor);
        Console.WriteLine(
            $"PASS P10-FINALIZER-01 committed={success.Generation.GenerationId} " +
            $"pendingStateRevision={success.Pending.StateRevision}");

        var trustedVerdict = MatchedVerdict(
            success.Run.Id,
            success.Generation.GenerationId,
            success.Generation.GenerationSemanticSha256);
        var command = new StatisticReconciliationTrustedFinalizeCommand(
            success.Run.Id,
            success.Pending.StateRevision,
            success.Pending.StateHash,
            success.Pending.GenerationPublishRevision,
            trustedVerdict);

        // Model a process crash after append and before the run CAS. Retrying
        // through the trusted finalizer must replay the one durable verdict and
        // converge on exactly one pending-to-current winner.
        await verdictPublisher.PublishAsync(trustedVerdict, time.UtcNow);
        var durableBeforeCas = await LoadRunAsync(context, success.Run.Id);
        Require(durableBeforeCas.CurrentGenerationId is null &&
                durableBeforeCas.PendingGenerationId ==
                success.Generation.GenerationId &&
                await CountFinalVerdictsAsync(context, success.Run.Id) == 1,
            "VERDICT_DURABLE_BEFORE_CAS_FIXTURE_INVALID");

        var race = await Task.WhenAll(
            finalizer.FinalizePendingAsync(command, systemActor),
            finalizer.FinalizePendingAsync(command, systemActor));
        Require(race.Count(result => !result.IsReplay) == 1 &&
                race.Count(result => result.IsReplay) == 1,
            "EXACT_ONE_FINALIZE_CAS_WINNER");
        var winner = race.Single(result => !result.IsReplay);
        Require(winner.Status == StatisticReconciliationRunStatuses.Matched &&
                winner.StateRevision == success.Pending.StateRevision + 1 &&
                winner.GenerationPublishRevision ==
                success.Pending.GenerationPublishRevision &&
                winner.CurrentGenerationId ==
                success.Generation.GenerationId &&
                winner.CurrentGenerationHash ==
                success.Generation.GenerationSemanticSha256,
            "PROMOTION_POSTIMAGE_INVALID");
        var promoted = await LoadRunAsync(context, success.Run.Id);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            promoted);
        Require(promoted.PendingGenerationId is null &&
                promoted.PendingGenerationHash is null &&
                !promoted.PendingGenerationPublishedAtUtc.HasValue &&
                promoted.CurrentGenerationId ==
                success.Generation.GenerationId &&
                promoted.CurrentGenerationHash ==
                success.Generation.GenerationSemanticSha256,
            "PENDING_TRIPLE_NOT_ATOMICALLY_CLEARED");
        Require(await CountFinalVerdictsAsync(context, success.Run.Id) == 1,
            "FINAL_VERDICT_CARDINALITY_AFTER_RACE");
        Console.WriteLine(
            $"PASS P10-FINALIZER-02 stateRevision={promoted.StateRevision} " +
            $"publishRevision={promoted.GenerationPublishRevision} " +
            "verdictDurableBeforeCas=true oneWinner=true");

        var replay = await finalizer.FinalizePendingAsync(command, systemActor);
        Require(replay.IsReplay &&
                replay.StateRevision == promoted.StateRevision &&
                replay.StateHash == promoted.StateHash &&
                await CountFinalVerdictsAsync(context, success.Run.Id) == 1,
            "EXACT_REPLAY_NOT_ZERO_WRITE");
        var changed = trustedVerdict with
        {
            DeltaManifestSha256 = Sha("changed-current-delta")
        };
        await ExpectConflictAsync(
            () => finalizer.FinalizePendingAsync(
                command with { TrustedVerdict = changed },
                systemActor),
            "FINALIZE_REPLAY_MISMATCH");
        Require(await CountFinalVerdictsAsync(context, success.Run.Id) == 1,
            "CHANGED_CURRENT_APPENDED_VERDICT");
        Console.WriteLine(
            "PASS P10-FINALIZER-03 exactReplay=true changedCurrentHardConflict=true");

        var stale = await CreateCommittedPendingAsync(
            context,
            runService,
            activation.Binding,
            time.UtcNow,
            systemActor);
        var staleVerdict = MatchedVerdict(
            stale.Run.Id,
            stale.Generation.GenerationId,
            stale.Generation.GenerationSemanticSha256);
        await ExpectConflictAsync(
            () => finalizer.FinalizePendingAsync(
                new StatisticReconciliationTrustedFinalizeCommand(
                    stale.Run.Id,
                    stale.Pending.StateRevision,
                    Sha("stale-state-hash"),
                    stale.Pending.GenerationPublishRevision,
                    staleVerdict),
                systemActor),
            "PENDING_FINALIZE_CAS_MISMATCH");
        var stalePost = await LoadRunAsync(context, stale.Run.Id);
        Require(stalePost.CurrentGenerationId is null &&
                stalePost.PendingGenerationId ==
                stale.Generation.GenerationId &&
                stalePost.StateRevision == stale.Pending.StateRevision &&
                stalePost.StateHash == stale.Pending.StateHash &&
                await CountFinalVerdictsAsync(context, stale.Run.Id) == 0,
            "STALE_CAS_MUST_PRESERVE_PENDING_WITH_ZERO_VERDICT");
        Console.WriteLine(
            "PASS P10-FINALIZER-04 staleCas=true pendingPreserved=true verdictWrites=0");

        await InsertReviewScopeAsync(context, success.Run, time.UtcNow);
        var reviewer = Actor("000000000000000000000009", "reviewer");
        var reviewGate = new ReviewGate(activation.Binding.ChainId);
        var reviewBackend =
            new StatisticReconciliationIndependentReviewMongoBackend(context);
        var owner = new StatisticReconciliationIndependentReviewOwner(
            context,
            reviewGate,
            verdictBackend,
            new StatisticReconciliationIndependentReviewService(reviewBackend),
            new FixedCurrentReviewValidator(),
            time);
        var http = new DefaultHttpContext();
        http.Items[MeAccessor.MeItemKey] = reviewer;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            commandId = "focused-review-post-01",
            gate = StatisticReconciliationReviewGates.Form,
            decision = StatisticReconciliationReviewDecisions.Approve,
            expectedStateRevision = promoted.StateRevision
        });
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = body.Length;
        http.Request.Body = new MemoryStream(body, writable: false);
        var controller = new StatisticReconciliationIndependentReviewController(
            new MeAccessor(new HttpContextAccessor { HttpContext = http }),
            owner)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        var response = await controller.SubmitAsync(
            success.Run.WorkId,
            success.Run.ScopeAssignmentId,
            success.Run.Id,
            CancellationToken.None);
        var ok = response as OkObjectResult;
        var submission = ok?.Value as StatisticReconciliationReviewSubmissionDto;
        Require(ok?.StatusCode is null or StatusCodes.Status200OK &&
                submission is not null &&
                !submission.Replayed &&
                submission.GenerationId == winner.Verdict.VerdictGenerationId &&
                submission.Gate == StatisticReconciliationReviewGates.Form &&
                submission.Decision ==
                StatisticReconciliationReviewDecisions.Approve &&
                submission.Status == StatisticReconciliationReviewStatuses.Active,
            "INDEPENDENT_REVIEW_POST_FAILED");
        Require(await CountReviewDecisionsAsync(context, success.Run.Id) == 1,
            "REVIEW_POST_CARDINALITY");
        await RequireP9WritesZeroAsync(context);
        Console.WriteLine(
            $"PASS P10-FINALIZER-05 verdict={winner.Verdict.VerdictGenerationId} " +
            $"reviewPost=200 gate={submission!.Gate} p9Writes=0");
    }

    private static async Task<CommittedPendingFixture>
        CreateCommittedPendingAsync(
            MongoDbContext context,
            IStatisticReconciliationRunService runService,
            StatisticReconciliationCandidateBinding binding,
            DateTime now,
            MeResponse actor)
    {
        var run = BuildRunningRun(binding, now, actor);
        await context.StatisticReconciliationRuns.InsertOneAsync(run);
        var boundary = BuildBoundary(run);
        var generation = await new StatisticReconciliationActualCoherentCaptureCoordinator()
            .CaptureAsync(
                run.Id,
                new StableBoundaryReader(boundary),
                BuildCaptureSteps(boundary));
        var pendingCas = new PendingRunServiceCas(runService);
        var publisher = new StatisticReconciliationActualGenerationPublisher(
            new StatisticReconciliationActualObservationMongoBackend(context),
            pendingCas);
        var appended = await publisher.PublishCoherentAsync(
            generation,
            BuildPublicationContext(run, boundary, generation),
            now,
            run.LeaseOwnerId!,
            run.ClaimToken!,
            actor);
        Require(appended.PublishedByCas && pendingCas.Calls == 1 &&
                appended.GenerationId != generation.GenerationId &&
                appended.GenerationSemanticSha256 !=
                generation.GenerationSemanticSha256,
            "V6_COMMIT_BINDING_OR_PENDING_CAS_INVALID");
        var pending = await LoadRunAsync(context, run.Id);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            pending);
        Require(pending.Status == StatisticReconciliationRunStatuses.Queued &&
                pending.PendingGenerationId == appended.GenerationId &&
                pending.PendingGenerationHash ==
                appended.GenerationSemanticSha256 &&
                pending.PendingGenerationPublishedAtUtc.HasValue &&
                pending.CurrentGenerationId is null &&
                pending.CurrentGenerationHash is null &&
                pending.GenerationPublishRevision == 1,
            "PENDING_PUBLICATION_POSTIMAGE_INVALID");
        return new(run, pending, appended);
    }

    private static StatisticReconciliationRun BuildRunningRun(
        StatisticReconciliationCandidateBinding binding,
        DateTime now,
        MeResponse actor)
    {
        string Id() => ObjectId.GenerateNewId().ToString();
        var runId = Id();
        var run = new StatisticReconciliationRun
        {
            Id = runId,
            ReceiptId = Sha("receipt:" + runId),
            CommandId = "focused-finalizer-create",
            ActorUserId = actor.Id,
            TenantUnitId = actor.UnitId,
            PermissionCodes = ["ROLE:SYSTEM_ADMIN", "STAT_RECONCILIATION_READ"],
            AuthorizationSnapshotHash = Sha("authorization"),
            WorkId = Id(),
            ScopeAssignmentId = Id(),
            P9ResultKind = StatisticReconciliationP9ResultKinds.Direct,
            P9ResultId = Id(),
            P9RunId = Id(),
            P9GenerationId = Sha("p9-generation-id"),
            P9GenerationHash = Sha("p9-generation-hash"),
            P9RunKind = "DIRECT_FIELD",
            P9CapabilityId = "P9_DIRECT_FIELD",
            P9RouteId = "P9_DIRECT_FIELD_READ",
            P9CandidateChainId = "p9-chain",
            P9CandidatePromptId = "P9-12",
            SourceReportId = Id(),
            SourcePayloadRevision = 7,
            SourcePayloadHash = Sha("source-payload"),
            SourceLifecycleRevision = 9,
            SourceLifecycleEventKey = "source-lifecycle-event",
            SourceLifecycleHash = Sha("source-lifecycle"),
            SourceLifecycleStatus = "APPROVED",
            DynamicFormFamilyId = Id(),
            DynamicFormVersionId = Id(),
            DynamicFormVersionNo = 4,
            DynamicFormSchemaHash = Sha("dynamic-form-schema"),
            FlowTemplateId = Id(),
            FlowFamilyRevision = 3,
            FlowTemplateVersionId = Id(),
            FlowPayloadHash = Sha("flow-payload"),
            FlowInstanceId = Id(),
            FlowInstanceRevision = 6,
            FlowExecutionEpoch = 2,
            FlowExecutionEpochId = Id(),
            FlowExecutionEpochRevision = 5,
            FlowStepId = "step-final",
            FlowBranchId = Id(),
            FlowStepInstanceId = Id(),
            FlowStepInstanceRevision = 8,
            FlowContributionPolicy = "INCLUDE",
            FlowContributionPolicyHash = Sha("flow-contribution-policy"),
            FlowEffectiveStatus = "EFFECTIVE",
            FlowContributionProvenanceHash =
                Sha("flow-contribution-provenance"),
            P8ConfigOwnerId = Id(),
            P8ConfigId = Id(),
            P8ConfigVersionId = Id(),
            P8ConfigVersionNo = 2,
            P8ConfigRevision = 11,
            P8ConfigHash = Sha("p8-config"),
            P9CatalogVersion = "1.6",
            P9CatalogRawSha256 = Sha("p9-catalog-raw"),
            P9CatalogSemanticSha256 = Sha("p9-catalog-semantic"),
            P9SchemaRawSha256 = Sha("p9-schema-raw"),
            P9SchemaSemanticSha256 = Sha("p9-schema-semantic"),
            P9StageLockSha256 = Sha("p9-stage-lock"),
            CandidateChainId = binding.ChainId,
            CandidatePromptId = binding.PromptId,
            CandidateStage = binding.Stage,
            CandidateCatalogVersion = binding.CatalogVersion,
            CandidateCatalogRawSha256 = binding.CatalogRawSha256,
            CandidateCatalogSemanticSha256 = binding.CatalogSemanticSha256,
            CandidateSchemaRawSha256 = binding.SchemaRawSha256,
            CandidateSchemaSemanticSha256 = binding.SchemaSemanticSha256,
            CandidateStageLockSha256 = binding.StageLockSha256,
            PeriodKey = "2026-08",
            PeriodInstanceKey = "month:2026-08",
            PeriodKind = "MONTH",
            PeriodStartUtc = now.Date,
            PeriodEndUtc = now.Date.AddMonths(1).AddTicks(-1),
            ConceptKey = "focused-finalizer",
            Grain = "MONTH",
            TimeAxis = "MONTH",
            PageContractHash = Sha("page-contract"),
            FilterHash = Sha("filter"),
            CanonicalFilterJson = null,
            ActualCapturePlan = null,
            ActualCapturePlanSha256 = null,
            ActualConfigurationBundleSha256 = null,
            SourceSetSha256 = null,
            ExpectedAlgorithmRevision = null,
            ExpectedAlgorithmSha256 = null,
            Status = StatisticReconciliationRunStatuses.Running,
            StateRevision = 2,
            RetryCount = 0,
            MaxRetryCount = 5,
            NextRetryAtUtc = null,
            LeaseOwnerId = "worker-finalizer",
            ClaimToken = Sha("claim-token"),
            LeaseUntilUtc = now.AddMinutes(5),
            LastHeartbeatAtUtc = now,
            DeadlineAtUtc = now.AddMinutes(10),
            DiagnosticCode = null,
            PendingGenerationId = null,
            PendingGenerationHash = null,
            PendingGenerationPublishedAtUtc = null,
            CurrentGenerationId = null,
            CurrentGenerationHash = null,
            GenerationPublishRevision = 0,
            OperationReceipts = [],
            LatestWriterUserId = actor.Id,
            CreatedAtUtc = now.AddMinutes(-1),
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            IsDeleted = false
        };
        run.P8ConfigBundleHash = StatisticReconciliationCanonicalJson.HashObject(
            new
            {
                schema = "P10_P8_CONFIG_BUNDLE_PIN_V1",
                ownerId = run.P8ConfigOwnerId,
                configId = run.P8ConfigId,
                configVersionId = run.P8ConfigVersionId,
                configVersionNo = run.P8ConfigVersionNo,
                configRevision = run.P8ConfigRevision,
                configHash = run.P8ConfigHash
            });
        var requestBinding = InvokePrivate(
            "BuildCreateRequestBinding",
            [typeof(StatisticReconciliationRun)],
            run);
        run.RequestHash = StatisticReconciliationCanonicalJson.HashObject(
            requestBinding);
        run.OperationReceiptHistoryHash = (string)InvokePrivate(
            "BuildOperationReceiptHistoryHash",
            [typeof(IEnumerable<StatisticReconciliationOperationReceipt>)],
            (object)Array.Empty<StatisticReconciliationOperationReceipt>());
        run.ImmutableIdentityHash = (string)InvokePrivate(
            "BuildImmutableIdentityHash",
            [typeof(StatisticReconciliationRun)],
            run);
        run.ImmutableHeaderHash = (string)InvokePrivate(
            "BuildImmutableHeaderHash",
            [typeof(StatisticReconciliationRun)],
            run);
        run.ReceiptResponseHash = (string)InvokePrivate(
            "BuildAcceptedResponseHash",
            [typeof(StatisticReconciliationRun)],
            run);
        var state = InvokePrivate(
            "StateOf",
            [typeof(StatisticReconciliationRun)],
            run);
        run.StateHash = (string)InvokePrivate(
            "BuildStateHash",
            [typeof(string), state.GetType()],
            run.Id,
            state);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        return run;
    }

    private static StatisticReconciliationActualCoherentBoundary BuildBoundary(
        StatisticReconciliationRun run)
        => StatisticReconciliationActualCoherentCaptureCoordinator.CreateBoundary(
            run.Id,
            run.WorkId,
            run.ScopeAssignmentId,
            Sha("source-set-" + run.Id),
            run.ActualConfigurationBundleSha256 ?? run.P8ConfigBundleHash,
            run.FilterHash,
            run.AuthorizationSnapshotHash,
            StatisticReconciliationActualBoundaryDomains.Required.Select(
                (domain, ordinal) =>
                    new StatisticReconciliationActualBoundaryPin(
                        domain,
                        "owner-" + domain.ToLowerInvariant(),
                        "generation-p9",
                        ordinal + 1,
                        Sha("boundary-" + domain + run.Id))));

    private static ImmutableArray<StatisticReconciliationActualCoherentCaptureStep>
        BuildCaptureSteps(StatisticReconciliationActualCoherentBoundary boundary)
        => [.. StatisticReconciliationActualCoherentLayers.RequiredOrder.Select(
            (layer, ordinal) =>
            {
                var ownerId = $"owner-{ordinal}";
                var ownerHash = Sha($"owner-version-{ordinal}");
                var typed = layer ==
                            StatisticReconciliationActualCoherentLayers.DirectProjection
                    ? ImmutableArray.Create(
                        StatisticReconciliationActualTypedObservationCanonical.Create(
                            0,
                            layer,
                            ownerId,
                            ownerHash,
                            "DIRECT",
                            "FIELD",
                            "metric-count",
                            "2026-08",
                            "COUNT",
                            "NUMBER",
                            "1",
                            occurrenceCount: 1,
                            reportCount: 1,
                            fieldId: "field-count"))
                    : ImmutableArray<StatisticReconciliationActualTypedObservation>.Empty;
                return new StatisticReconciliationActualCoherentCaptureStep(
                    layer,
                    (_, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return Task.FromResult(
                            new StatisticReconciliationActualLayerCapture(
                                layer,
                                boundary.BoundarySemanticSha256,
                                Sha($"capture-{ordinal}"),
                                1,
                                StatisticReconciliationActualCoherentCaptureStates.Ready,
                                null,
                                ownerId,
                                ownerHash,
                                typed));
                    });
            })];

    private static StatisticReconciliationActualPublicationContext
        BuildPublicationContext(
            StatisticReconciliationRun run,
            StatisticReconciliationActualCoherentBoundary boundary,
            StatisticReconciliationActualCoherentGeneration generation)
    {
        var values = new[]
        {
            run.P9CatalogVersion,
            run.P9CatalogRawSha256,
            run.P9CatalogSemanticSha256,
            run.P9SchemaRawSha256,
            run.P9SchemaSemanticSha256,
            run.P9StageLockSha256,
            run.CandidateChainId,
            run.CandidatePromptId,
            run.CandidateCatalogVersion,
            run.CandidateCatalogRawSha256,
            run.CandidateCatalogSemanticSha256,
            run.CandidateSchemaRawSha256,
            run.CandidateSchemaSemanticSha256,
            run.CandidateStageLockSha256
        };
        var pinSet = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CATALOG_PIN_SET_V1",
            values);
        var context = new StatisticReconciliationActualPublicationContext(
            run.Id,
            run.ImmutableIdentityHash,
            run.ImmutableHeaderHash,
            run.WorkId,
            run.ScopeAssignmentId,
            run.PeriodKey,
            run.PeriodInstanceKey,
            run.ConceptKey,
            run.Grain,
            run.TimeAxis,
            run.FilterHash,
            run.DynamicFormVersionId,
            run.DynamicFormSchemaHash!,
            run.FlowTemplateVersionId!,
            run.FlowPayloadHash!,
            run.FlowInstanceId!,
            run.FlowExecutionEpochId!,
            run.FlowExecutionEpoch!.Value,
            run.FlowExecutionEpochRevision!.Value,
            run.P8ConfigOwnerId!,
            run.P8ConfigBundleHash,
            run.ActualConfigurationBundleSha256 ?? run.P8ConfigBundleHash,
            boundary.CatalogPinSetSha256,
            new StatisticReconciliationActualPublicationCatalogPins(
                values[0], values[1], values[2], values[3], values[4], values[5],
                values[6], values[7], values[8], values[9], values[10], values[11],
                values[12], values[13], pinSet),
            RuntimeKind: StatisticReconciliationExpectedRuntimeKinds.Flow,
            SummaryPlanBinding: BuildSummaryPlanBinding(run),
            ActualMembershipSemanticSha256:
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_INCLUDED_SOURCE_MEMBERSHIP_V1", []),
            ActualSourceDecisionManifestSha256:
                StatisticReconciliationActualCanonical.HashSequence(
                    "P10_ACTUAL_PUBLICATION_SOURCE_DECISION_MANIFEST_V1", []),
            ActualSourceDecisions: ImmutableArray<
                StatisticReconciliationActualPublishedSourceDecision>.Empty,
            LifecycleMetricScopeSha256: Sha("lifecycle-metric-scope"));
        return PublicationV7Fixture.PrepareAsync(
                generation, context, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static StatisticReconciliationActualSummaryPlanBinding
        BuildSummaryPlanBinding(StatisticReconciliationRun run)
    {
        var operations = ImmutableArray.Create("COUNT");
        var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
            .Compile(new ExpectedMetricIdentityRequest(
                "DIRECT", "FIELD", "metric-count", run.PeriodKey,
                fieldId: "field-count"));
        var entrySha = StatisticReconciliationExpectedMetricPlanIntegrity
            .BuildEntrySha256(
                identity, "/fieldValues/values/count", "NUMBER",
                unordered: false, expandArray: false, operations,
                StatisticReconciliationExpectedDiffTransitionModes.None,
                null, null, null, null);
        var descriptor =
            StatisticReconciliationActualSummaryIdentityDescriptor.Create(
                new StatisticReconciliationExpectedValueFreeMetricPlanDescriptor(
                    StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
                    identity, "/fieldValues/values/count", "NUMBER",
                    false, null, false, operations,
                    StatisticReconciliationExpectedDiffTransitionModes.None,
                    null, null, null, null, ["NONE"], entrySha));
        return StatisticReconciliationActualSummaryPlanBinding.Create(
            new StatisticReconciliationExpectedGenerationBinding(
                run.Id, Sha("expected-generation-id"),
                Sha("expected-generation"), Sha("expected-metric-plan"), 1,
                Sha("expected-manifest"), 8,
                Sha("expected-membership"),
                StatisticReconciliationExpectedRuntimeKinds.Flow),
            [descriptor]);
    }

    private static StatisticReconciliationFinalVerdictRequest MatchedVerdict(
        string reconciliationId,
        string actualGenerationId,
        string actualGenerationSha256)
    {
        var classifier = new StatisticReconciliationRootCauseClassifier();
        var permission = classifier.CreatePermissionEvidence(
            StatisticReconciliationRootCausePermissionStates.Authorized,
            Sha("verdict-authorization"));
        var root = new StatisticReconciliationRootCauseRequest(
            ComparisonBinding,
            permission,
            Freshness(),
            [.. Enumerable.Range(0, 8).Select(ordinal =>
                classifier.CreateLayerEvidence(
                    ordinal,
                    StatisticReconciliationRootCauseLayers.Ordered[ordinal],
                    ComparisonBinding,
                    true,
                    Sha("expected-layer"),
                    Sha("actual-layer"),
                    Sha("layer-delta"),
                    1,
                    0,
                    0,
                    0,
                    0,
                    StatisticReconciliationRootCauseLayerDeltaStates.Zero,
                    StatisticReconciliationRootCauseAttributionStates.NotRequired,
                    null,
                    null,
                    []))]);
        var request = new StatisticReconciliationFinalVerdictRequest(
            reconciliationId,
            ComparisonBinding,
            Sha("expected-generation-id"),
            Sha("expected-generation-sha"),
            actualGenerationId,
            actualGenerationSha256,
            Sha("delta-manifest"),
            permission,
            root,
            StatisticReconciliationFinalVerdictFailureKinds.None,
            null);
        var evaluated = new StatisticReconciliationFinalVerdictEvaluator()
            .Evaluate(request);
        Require(evaluated.Verdict ==
                StatisticReconciliationFinalVerdicts.Matched &&
                evaluated.CompleteEvidence &&
                evaluated.AllRequiredLayersZero &&
                evaluated.Signable,
            "TRUSTED_MATCHED_FIXTURE_INVALID");
        return request;
    }

    private static StatisticReconciliationFreshnessAssessment Freshness()
    {
        var pins = new StatisticReconciliationActualFreshnessPins(
            new StatisticReconciliationComparisonBindingPins(
                new string('1', 64),
                new string('2', 64),
                new string('3', 64),
                new string('4', 64),
                new string('5', 64)),
            new string('6', 64),
            new string('7', 64),
            new string('8', 64));
        return new StatisticReconciliationFreshnessEvaluator().Evaluate(
            new StatisticReconciliationFreshnessRequest(
                pins.Binding,
                pins,
                pins,
                true,
                true,
                true));
    }

    private static async Task CreateFinalVerdictActualTupleIndexAsync(
        MongoDbContext context)
    {
        var index = new CreateIndexModel<StatisticReconciliationReview>(
            Builders<StatisticReconciliationReview>.IndexKeys
                .Ascending(value => value.ReconciliationId)
                .Ascending(value => value.ActualGenerationId)
                .Ascending(value => value.ActualGenerationSha256),
            new CreateIndexOptions<StatisticReconciliationReview>
            {
                Name = "ux_statisticReconciliationReviews_actualGeneration",
                Unique = true,
                PartialFilterExpression =
                    Builders<StatisticReconciliationReview>.Filter.Eq(
                        value => value.RecordKind,
                        StatisticReconciliationReviewKinds.FinalVerdict)
            });
        await context.StatisticReconciliationReviews.Indexes.CreateOneAsync(index);
    }

    private static async Task InsertReviewScopeAsync(
        MongoDbContext context,
        StatisticReconciliationRun run,
        DateTime now)
        => await context.WorkAssignments.InsertOneAsync(new WorkAssignment
        {
            Id = run.ScopeAssignmentId,
            WorkId = run.WorkId,
            RootAssignmentId = run.ScopeAssignmentId,
            Code = "P10-REVIEW",
            Name = "P10 review scope",
            Path = "/p10-review",
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = run.ActorUserId,
            UpdatedByUserId = run.ActorUserId,
            IsDeleted = false
        });

    private static async Task<StatisticReconciliationRun> LoadRunAsync(
        MongoDbContext context,
        string reconciliationId)
        => await context.StatisticReconciliationRuns
               .Find(value => value.Id == reconciliationId)
               .SingleAsync()
           ?? throw new InvalidOperationException("RUN_NOT_FOUND");

    private static async Task<long> CountFinalVerdictsAsync(
        MongoDbContext context,
        string reconciliationId)
        => await context.Db
            .GetCollection<BsonDocument>(
                context.Options.StatisticReconciliationReviewCollection)
            .CountDocumentsAsync(new BsonDocument
            {
                { "reconciliationId", reconciliationId },
                { "recordKind", StatisticReconciliationReviewKinds.FinalVerdict }
            });

    private static async Task<long> CountReviewDecisionsAsync(
        MongoDbContext context,
        string reconciliationId)
        => await context.Db
            .GetCollection<BsonDocument>(
                context.Options.StatisticReconciliationReviewCollection)
            .CountDocumentsAsync(new BsonDocument
            {
                { "reconciliationId", reconciliationId },
                {
                    "recordKind",
                    StatisticReconciliationReviewRecordKinds.Decision
                }
            });

    private static async Task RequireP9WritesZeroAsync(MongoDbContext context)
    {
        var protectedCollections = new[]
        {
            context.Options.WorkReportStatisticRebuildJobCollection,
            context.Options.WorkReportFieldStatValueCollection,
            context.Options.WorkReportFieldStatAggregateCollection,
            context.Options.WorkReportTableStatValueCollection,
            context.Options.WorkReportTableStatAggregateCollection,
            context.Options.WorkReportLabelStatValueCollection,
            context.Options.WorkReportLabelStatAggregateCollection,
            context.Options.WorkReportStatisticDiffConfigCollection,
            context.Options.WorkReportStatisticExportCollection,
            context.Options.WorkReportStatisticDiffExportCollection
        };
        foreach (var collectionName in protectedCollections)
        {
            var count = await context.Db.GetCollection<BsonDocument>(collectionName)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Require(count == 0, "P9_WRITE_DETECTED:" + collectionName);
        }
    }

    private static async Task ExpectConflictAsync(
        Func<Task> action,
        string expectedReason)
    {
        try
        {
            await action();
        }
        catch (AppException exception)
        {
            var reason = exception.Details?.GetType()
                .GetProperty("reason")?.GetValue(exception.Details)?.ToString();
            Require(exception.Code ==
                    AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT &&
                    reason == expectedReason,
                $"EXPECTED_CONFLICT:{expectedReason}:ACTUAL:{exception.Code}:{reason}");
            return;
        }

        throw new InvalidOperationException(
            "EXPECTED_CONFLICT_NOT_THROWN:" + expectedReason);
    }

    private static object InvokePrivate(
        string name,
        Type[] parameterTypes,
        params object?[] arguments)
    {
        var method = typeof(StatisticReconciliationRunService).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            parameterTypes,
            null) ?? throw new MissingMethodException(
            typeof(StatisticReconciliationRunService).FullName,
            name);
        try
        {
            return method.Invoke(null, arguments)
                   ?? throw new InvalidOperationException(name + "_RETURNED_NULL");
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static MeResponse Actor(string id, string username)
        => new(
            id,
            username,
            username,
            ["SYSTEM"],
            "000000000000000000000002",
            "SYS",
            "System",
            "SYS",
            ["SYSTEM_ADMIN"],
            "ADMIN",
            false,
            "SYSTEM_ADMIN");

    private static string Sha(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    private static string FindBackendRoot()
    {
        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "tdtd-be.csproj")))
                    return current.FullName;
                var nested = Path.Combine(current.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nested, "tdtd-be.csproj")))
                    return nested;
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("BACKEND_ROOT_NOT_FOUND");
    }

    private static void RemoveRunRootGuarded(
        string backendRoot,
        string runRoot)
    {
        var allowed = Path.GetFullPath(Path.Combine(
                backendRoot,
                ".build",
                "p10-trusted-finalizer"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(runRoot);
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(target).StartsWith(
                "p10finalizer_",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "REFUSING_UNGUARDED_TEST_RUN_ROOT_DELETE:" + target);
        }

        IOException? last = null;
        for (var attempt = 0; attempt < 20 && Directory.Exists(target); attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
                last = null;
            }
            catch (IOException exception)
            {
                last = exception;
                Thread.Sleep(100);
            }
        }

        if (Directory.Exists(target))
            throw new IOException("TEST_RUN_ROOT_DELETE_FAILED", last);
    }

    private sealed record CommittedPendingFixture(
        StatisticReconciliationRun Run,
        StatisticReconciliationRun Pending,
        StatisticReconciliationActualAppendResult Generation);

    private sealed class StableBoundaryReader(
        StatisticReconciliationActualCoherentBoundary boundary)
        : IStatisticReconciliationActualCoherentBoundaryReader
    {
        public Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(boundary);
        }
    }

    private sealed class PendingRunServiceCas(
        IStatisticReconciliationRunService runService)
        : IStatisticReconciliationActualGenerationCas
    {
        internal int Calls { get; private set; }

        public async Task PublishAsync(
            string reconciliationId,
            string generationId,
            string generationSemanticSha256,
            string workerId,
            string claimToken,
            MeResponse actor,
            CancellationToken cancellationToken)
        {
            Calls++;
            await runService.PublishPendingAsync(
                reconciliationId,
                new tdtd_be.DTOs.StatisticsReconciliation
                    .StatisticReconciliationPendingPublishRequest
                {
                    WorkerId = workerId,
                    ClaimToken = claimToken,
                    GenerationId = generationId,
                    GenerationHash = generationSemanticSha256
                },
                actor,
                cancellationToken);
        }
    }

    private sealed class FinalizerActivation
        : IStatisticReconciliationCandidateActivation
    {
        internal StatisticReconciliationCandidateBinding Binding { get; } = new(
            ChainId,
            "P10-01",
            0,
            "1.7",
            Sha("candidate-catalog-raw"),
            Sha("candidate-catalog-semantic"),
            Sha("candidate-schema-raw"),
            Sha("candidate-schema-semantic"),
            Sha("candidate-stage-lock"),
            "tdtd_p10_focused",
            [],
            "p10-focused-stage",
            Sha("candidate-evidence"),
            Sha("candidate-generator"));

        public StatisticReconciliationCandidateBinding RequireFoundation(
            string capabilityId,
            string routeId)
        {
            var expected = routeId ==
                           StatisticReconciliationRouteRegistry.WorkerFinalize
                ? StatisticReconciliationCapabilities.ExpectedActualDelta
                : StatisticReconciliationCapabilities.SourceToResultReconciliation;
            Require(capabilityId == expected && routeId is
                    StatisticReconciliationRouteRegistry.WorkerPublish or
                    StatisticReconciliationRouteRegistry.WorkerFinalize,
                "UNEXPECTED_FINALIZER_FOUNDATION_ROUTE");
            return Binding;
        }

        public StatisticReconciliationCandidateEvaluation EvaluateFoundation(
            string capabilityId,
            string routeId)
            => new(true, null, routeId, capabilityId, Binding);

        public StatisticReconciliationCandidateBinding RequireCapability(
            string capabilityId,
            string routeId)
            => Binding;

        public StatisticReconciliationCandidateEvaluation EvaluateCapability(
            string capabilityId,
            string routeId)
            => new(true, null, routeId, capabilityId, Binding);
    }

    private sealed class FinalizerStatRunActivation
        : IStatRunCandidateActivation
    {
        private StatRunCandidateBinding Binding { get; } = new(
            "p9-focused-chain",
            "P9-12",
            9,
            "1.6",
            Sha("p9-candidate-catalog-raw"),
            Sha("p9-candidate-catalog-semantic"),
            Sha("p9-candidate-schema-raw"),
            Sha("p9-candidate-schema-semantic"),
            Sha("p9-candidate-stage-lock"),
            "tdtd_p10_focused",
            []);

        public StatRunCandidateBinding RequireFoundation(
            string capabilityId,
            string routeId)
            => Binding;

        public StatRunCandidateEvaluation EvaluateFoundation(
            string capabilityId,
            string routeId)
            => new(true, null, routeId, capabilityId, Binding);

        public StatRunCandidateBinding RequireCapability(
            string capabilityId,
            string routeId)
            => Binding;

        public StatRunCandidateEvaluation EvaluateCapability(
            string capabilityId,
            string routeId)
            => new(true, null, routeId, capabilityId, Binding);
    }
    private sealed class ReviewGate(string chainId)
        : IStatisticReconciliationIndependentReviewCandidateGate
    {
        public StatisticReconciliationIndependentReviewCandidateBinding Require(
            string routeId)
        {
            P10TrustedFinalizerProbe.Require(
                routeId == StatisticReconciliationRouteRegistry.ReviewSubmit,
                "UNEXPECTED_REVIEW_ROUTE");
            return new(
                chainId,
                "p10-review-focused-stage",
                Sha("review-stage-lock"),
                Sha("review-catalog-raw"),
                Sha("review-catalog-semantic"));
        }
    }

    private sealed class FixedCurrentReviewValidator
        : IStatisticReconciliationCurrentReviewValidator
    {
        public Task<StatisticReconciliationCurrentReviewValidation>
            ValidateAsync(
                StatisticReconciliationRun persistedRun,
                StatisticReconciliationReview verdict,
                CancellationToken cancellationToken = default)
            => Task.FromResult(new StatisticReconciliationCurrentReviewValidation(
                Sha("focused-current-review-validation")));
    }

    private sealed class FixedTimeService(DateTime utcNow) : IAppTimeService
    {
        private readonly AppTimeService _inner = new();
        public DateTime UtcNow { get; } = utcNow;
        public TimeZoneInfo ApplicationTimeZone => _inner.ApplicationTimeZone;
        public DateTime ToUtc(DateTime value) => _inner.ToUtc(value);
        public DateTime? ToUtc(DateTime? value) => _inner.ToUtc(value);
        public DateTime NormalizeUtcDate(DateTime value)
            => _inner.NormalizeUtcDate(value);
        public DateTime EndOfUtcDate(DateTime value)
            => _inner.EndOfUtcDate(value);
        public DateTime NextLocalMidnightUtc(DateTime value)
            => _inner.NextLocalMidnightUtc(value);
        public bool IsLastSundayOfMonth(DateTime value)
            => _inner.IsLastSundayOfMonth(value);
        public AppUtcDateRange NormalizeMonthRange(
            DateTime? fromUtc,
            DateTime? toUtc)
            => _inner.NormalizeMonthRange(fromUtc, toUtc);
    }
}
