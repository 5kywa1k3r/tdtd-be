using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRaceProbe
{
    private async Task<string> PrepareFoundationHistoricalContentRootAsync(
        CancellationToken ct)
    {
        var constructor = typeof(P10ReconciliationCoreProbe).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(HarnessPaths), typeof(string)],
            modifiers: null) ?? throw new InvalidOperationException(
                "P10_RACE_CORE_CONSTRUCTOR_MISSING");
        var bridge = (P10ReconciliationCoreProbe)(constructor.Invoke(
            [_paths, _runKey]) ?? throw new InvalidOperationException(
                "P10_RACE_CORE_CONSTRUCTOR_NULL"));
        var prepare = typeof(P10ReconciliationCoreProbe).GetMethod(
            "PrepareHistoricalContentRootAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "P10_RACE_HISTORICAL_CONTENT_PREPARE_MISSING");
        try
        {
            var task = prepare.Invoke(bridge, [ct]) as Task ??
                throw new InvalidOperationException(
                    "P10_RACE_HISTORICAL_CONTENT_TASK_MISSING");
            await task.ConfigureAwait(false);
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }

        _foundationHistoricalWorkspaceRoot = (string)(typeof(

                P10ReconciliationCoreProbe)

            .GetField(

                "_historicalWorkspaceRoot",

                BindingFlags.Instance | BindingFlags.NonPublic)?

            .GetValue(bridge) ?? throw new InvalidOperationException(

                "P10_RACE_HISTORICAL_WORKSPACE_ROOT_MISSING"));

        var historicalContentRoot = (string)(typeof(P10ReconciliationCoreProbe)
            .GetField(
                "_historicalContentRoot",
                BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(bridge) ?? throw new InvalidOperationException(
                "P10_RACE_HISTORICAL_CONTENT_ROOT_MISSING"));
        await MirrorPublishedStatRunContractAsync(
            _foundationHistoricalWorkspaceRoot,
            historicalContentRoot,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json",
            "a790be94e4598208de08af39f8782a267ce434db2c20242a811991429932233c",
            ct);
        await MirrorPublishedStatRunContractAsync(
            _foundationHistoricalWorkspaceRoot,
            historicalContentRoot,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json",
            "603304c9798805c972370494d3939bdc7da324939ba9ab98a82800241f1b6940",
            ct);
        return historicalContentRoot;
    }

    private static async Task MirrorPublishedStatRunContractAsync(
        string historicalWorkspaceRoot,
        string historicalContentRoot,
        string fileName,
        string expectedRawSha256,
        CancellationToken ct)
    {
        var source = Path.Combine(historicalWorkspaceRoot, "docs", "features", fileName);
        var target = Path.Combine(historicalContentRoot, "Contracts", "DynamicFormFlow", fileName);
        if (!File.Exists(source) ||
            !string.Equals(HashFile(source), expectedRawSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "P10_RACE_PUBLISHED_STAT_RUN_SOURCE_INVALID:" + fileName);
        }
        if (File.Exists(target))
            throw new InvalidOperationException(
                "P10_RACE_PUBLISHED_STAT_RUN_TARGET_EXISTS:" + fileName);

        var bytes = await File.ReadAllBytesAsync(source, ct);
        await using (var stream = new FileStream(
                         target, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 4096, useAsync: true))
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        if (!string.Equals(HashFile(target), expectedRawSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "P10_RACE_PUBLISHED_STAT_RUN_TARGET_INVALID:" + fileName);
        }
    }
    private async Task<P10ReconciliationCandidateOptions>
        LoadFoundationCandidateAsync(CancellationToken ct)
    {
        var workspaceRoot = _foundationHistoricalWorkspaceRoot ??
            throw new InvalidOperationException(
                "P10_RACE_HISTORICAL_WORKSPACE_ROOT_MISSING");
        var root = Path.Combine(workspaceRoot, ".p10-artifacts",
            "catalog-candidate", ChainId, "P10-01");
        var stagePath = Path.Combine(root, "stage-lock.json");
        var bytes = await File.ReadAllBytesAsync(stagePath, ct);
        using var document = JsonDocument.Parse(bytes);
        var stage = document.RootElement;
        if (stage.GetProperty("stagePrompt").GetString() != "P10-01" ||
            stage.GetProperty("sealed").GetBoolean() ||
            stage.GetProperty("promotionIds").GetArrayLength() != 0)
            throw new InvalidOperationException("P10_RACE_FOUNDATION_STAGE_INVALID");
        var catalog = stage.GetProperty("catalog");
        var schema = stage.GetProperty("schema");
        var evidence = stage.GetProperty("evidence");
        string Full(string relative) => Path.GetFullPath(Path.Combine(
            workspaceRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        string Text(JsonElement value, string property) =>
            value.GetProperty(property).GetString() ??
            throw new InvalidOperationException("P10_RACE_CANDIDATE_FIELD_MISSING:" + property);
        return new P10ReconciliationCandidateOptions(
            true,
            ChainId,
            _mongo?.DatabaseName ?? throw new InvalidOperationException("P10_RACE_MONGO_REQUIRED"),
            "tdtd_p10_",
            Full(Text(catalog, "path")),
            Text(catalog, "rawSha256"),
            Text(catalog, "semanticSha256"),
            Full(Text(schema, "path")),
            Text(schema, "rawSha256"),
            Text(schema, "semanticSha256"),
            Full(Text(evidence, "path")),
            Text(evidence, "sha256"),
            stagePath,
            HashFile(stagePath));
    }

    private async Task AdoptCoreFixtureAsync(CancellationToken ct)
    {
        var constructor = typeof(P10ReconciliationCoreProbe).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(HarnessPaths), typeof(string)],
            modifiers: null) ?? throw new InvalidOperationException(
                "P10_RACE_CORE_CONSTRUCTOR_MISSING");
        _coreBridge = (P10ReconciliationCoreProbe)(constructor.Invoke(
            [_paths, _runKey]) ?? throw new InvalidOperationException(
                "P10_RACE_CORE_CONSTRUCTOR_NULL"));

        static void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException(
                    "P10_RACE_CORE_FIELD_MISSING:" + name);
            field.SetValue(target, value);
        }

        Set(_coreBridge, "_mongo", _mongo ?? throw new InvalidOperationException(
            "P10_RACE_MONGO_REQUIRED"));
        Set(_coreBridge, "_backend", Backend());
        Set(_coreBridge, "_api", Api());
        Set(_coreBridge, "_database", Database());
        var bootstrapMethod = typeof(P10ReconciliationCoreProbe).GetMethod(
            "BootstrapAndSeedFixtureAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "P10_RACE_CORE_BOOTSTRAP_MISSING");
        try
        {
            var task = bootstrapMethod.Invoke(_coreBridge, [ct]) as Task ??
                throw new InvalidOperationException(
                    "P10_RACE_CORE_BOOTSTRAP_TASK_MISSING");
            await task.ConfigureAwait(false);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }

        T Read<T>(string name) => (T)(typeof(P10ReconciliationCoreProbe)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(_coreBridge) ?? throw new InvalidOperationException(
                "P10_RACE_CORE_FIELD_EMPTY:" + name));
        _coreFixture = Read<P10Fixture>("_fixture");
        _coreActors = new Dictionary<string, P10Actor>(
            Read<Dictionary<string, P10Actor>>("_actors"),
            StringComparer.Ordinal);
        _bootstrapPassword = Read<string>("_bootstrapPassword");
        _adminToken = CoreActor("admin").Token;
        _executorToken = CoreActor("executor").Token;
        _outsiderToken = CoreActor("outsider").Token;
        _workId = _coreFixture.WorkId;
        _scopeId = _coreFixture.ScopeAssignmentId;
        _secrets.AddRange(_coreActors.Values.Select(value => value.Token));
        _secrets.Add(_bootstrapPassword);
    }

    private P10Actor CoreActor(string key) =>
        _coreActors?.TryGetValue(key, out var actor) == true
            ? actor
            : throw new InvalidOperationException(
                "P10_RACE_CORE_ACTOR_MISSING:" + key);

    private P10Fixture CoreFixture() => _coreFixture ??
        throw new InvalidOperationException("P10_RACE_CORE_FIXTURE_MISSING");

    private async Task BootstrapSecurityFixtureAsync(CancellationToken ct)
    {
        var bootstrap = await Api().PostAsync(
            "api/system/bootstrap",
            new { },
            headers: new Dictionary<string, string>
            {
                ["X-System-Bootstrap-Key"] = Backend().BootstrapKey
            },
            ct: ct);
        ApiHarnessClient.ExpectStatus(bootstrap, HttpStatusCode.OK,
            "P10-RACE system bootstrap");
        _bootstrapPassword = ApiHarnessClient.RequiredString(
            bootstrap.Json, "defaultPassword");
        _secrets.Add(_bootstrapPassword);
        _adminToken = await Api().LoginAsync("admin", _bootstrapPassword, ct);
        _secrets.Add(_adminToken);

        var users = Database().GetCollection<AppUser>("users");
        var units = Database().GetCollection<Unit>("units");
        var assignments = Database().GetCollection<WorkAssignment>("work_assignments");
        var admin = await users.Find(value => value.Username == "admin" && !value.IsDeleted)
            .SingleAsync(ct);
        var root = await units.Find(value => value.Id == admin.UnitId && !value.IsDeleted)
            .SingleAsync(ct);
        var unitId = ObjectId.GenerateNewId().ToString();
        var outsiderId = ObjectId.GenerateNewId().ToString();
        _workId = ObjectId.GenerateNewId().ToString();
        _scopeId = ObjectId.GenerateNewId().ToString();
        await units.InsertOneAsync(new Unit
        {
            Id = unitId,
            FullName = "P10 Race Outsider Unit",
            ShortName = "P10 Race Outsider",
            Symbol = "P10R",
            Code = "P10R",
            Level = root.Level + 1,
            Version = 1,
            ParentUnitId = root.Id,
            UnitTypeCodes = [],
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            CreatedAtUtc = FixedUtc,
            UpdatedAtUtc = FixedUtc,
            IsDeleted = false
        }, cancellationToken: ct);
        var outsider = new AppUser
        {
            Id = outsiderId,
            Username = $"p10_race_outsider_{_rootId.ToLowerInvariant()}",
            FullName = "P10 Race Outsider",
            UnitId = unitId,
            AccountKind = "NORMAL_USER",
            Roles = ["MANAGER_LEVEL"],
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            CreatedAtUtc = FixedUtc,
            UpdatedAtUtc = FixedUtc,
            IsDeleted = false
        };
        outsider.PasswordHash = new PasswordHasher<AppUser>()
            .HashPassword(outsider, Backend().ActorPassword);
        await users.InsertOneAsync(outsider, cancellationToken: ct);
        await assignments.InsertOneAsync(new WorkAssignment
        {
            Id = _scopeId,
            WorkId = _workId,
            RootAssignmentId = _scopeId,
            Code = "P10-RACE-SCOPE",
            Name = "P10 Race Scope",
            Path = "/p10-race",
            IsActive = true,
            IssuedByUnitId = admin.UnitId,
            TargetUnitIds = [admin.UnitId!],
            CreatedByUserId = admin.Id,
            UpdatedByUserId = admin.Id,
            CreatedAtUtc = FixedUtc,
            UpdatedAtUtc = FixedUtc,
            IsDeleted = false
        }, cancellationToken: ct);
        _outsiderToken = await Api().LoginAsync(
            outsider.Username, Backend().ActorPassword, ct);
        _secrets.Add(_outsiderToken);
    }

    private string SourceFingerprint()
    {
        var relatives = new List<string>
        {
            "tdtd-be/Program.cs",
            "tdtd-be/Common/Time/AppTimeService.cs",
            "tdtd-be/Controllers/StatisticReconciliationController.cs",
            "tdtd-be/Controllers/StatisticReconciliationRecheckController.cs",
            "tdtd-be/Controllers/StatisticReconciliationIndependentReviewController.cs",
            "tdtd-be/Controllers/StatisticReconciliationEvidenceController.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Helpers.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Worker.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.ActualCapturePlan.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.ActualCaptureApiPlan.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.ActualCaptureIntegrityAccess.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Recheck.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.RecheckCaptureBinding.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.RecheckReceipts.cs",
            "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationTrustedFinalizer.cs",
            "tdtd-be/Services/StatisticsReconciliation/ActualObservation/StatisticReconciliationActualPublication.cs",
            "tdtd-be/Services/StatisticsReconciliation/ActualObservation/StatisticReconciliationActualCoherentCapture.cs",
            "tdtd-be/Services/StatisticsReconciliation/ActualObservation/StatisticReconciliationActualBoundaryRegistry.cs",
            "tdtd-be/Services/StatisticsReconciliation/IndependentReview/StatisticReconciliationIndependentReviewService.cs",
            "tdtd-be/Services/StatisticsReconciliation/IndependentReview/StatisticReconciliationIndependentReviewMongoBackend.cs",
            "tdtd-be/Services/StatisticsReconciliation/TypedDelta/StatisticReconciliationFinalVerdictPublisher.cs",
            "tdtd-be/Services/StatisticsReconciliation/EvidenceExport/StatisticReconciliationEvidenceExportService.cs",
            "tdtd-be/Services/StatisticsReconciliation/EvidenceExport/StatisticReconciliationEvidenceCanonical.cs",
            "tdtd-be/Services/StatisticsReconciliation/ExpectedLedger/StatisticReconciliationExpectedObservationStore.cs",
            "tdtd-be/tests/tdtd-be.IntegrationTests/BackendServerLease.cs",
            "tdtd-be/tests/tdtd-be.IntegrationTests/MongoReplicaSetLease.cs",
            "tdtd-be/tests/tdtd-be.IntegrationTests/P10TrustedFinalizerProbe.cs",
            "tdtd-be/tests/tdtd-be.P10T26Integration/PublicationV7Fixture.cs",
            "tdtd-be/tests/tdtd-be.IntegrationTests/Program.cs"
        };
        var probeDirectory = Path.Combine(_paths.BackendRoot, "tests", "tdtd-be.IntegrationTests");
        relatives.AddRange(Directory.GetFiles(probeDirectory,
                "P10ReconciliationRaceProbe*.cs", SearchOption.TopDirectoryOnly)
            .Select(value => Path.GetRelativePath(_paths.WorkspaceRoot, value)
                .Replace(Path.DirectorySeparatorChar, '/')));
        relatives.AddRange(Directory.GetFiles(probeDirectory,
                "P10ReconciliationCoreProbe*.cs", SearchOption.TopDirectoryOnly)
            .Select(value => Path.GetRelativePath(_paths.WorkspaceRoot, value)
                .Replace(Path.DirectorySeparatorChar, '/')));
        var pins = relatives.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => value + "=" + HashFile(Path.Combine(
                _paths.WorkspaceRoot,
                value.Replace('/', Path.DirectorySeparatorChar))));
        return SemanticSha(pins);
    }

    private async Task<P10DurableReviewFixture> CreateReviewFixtureAsync(
        string label,
        CancellationToken ct)
    {
        var binding = RaceCandidateBinding();
        var initiator = RaceActor(ObjectId.GenerateNewId().ToString(),
            "p10-race-initiator-" + label);
        var run = InvokeTrustedFixture<StatisticReconciliationRun>(
            "BuildRunningRun", binding, FixedUtc, initiator);
        var actualGenerationId = Sha(label + ":actual-generation-id");
        var actualGenerationSha = Sha(label + ":actual-generation-semantic");
        run.GenerationPublishRevision = 1;
        var nextRevision = checked(run.StateRevision + 1);
        var stateHash = StatisticReconciliationRunService
            .BuildTrustedFinalizedStateHash(
                run,
                StatisticReconciliationRunStatuses.Matched,
                nextRevision,
                actualGenerationId,
                actualGenerationSha,
                null,
                null);
        run.Status = StatisticReconciliationRunStatuses.Matched;
        run.StateRevision = nextRevision;
        run.StateHash = stateHash;
        run.NextRetryAtUtc = null;
        run.LeaseOwnerId = null;
        run.ClaimToken = null;
        run.LeaseUntilUtc = null;
        run.LastHeartbeatAtUtc = null;
        run.PendingGenerationId = null;
        run.PendingGenerationHash = null;
        run.PendingGenerationPublishedAtUtc = null;
        run.CurrentGenerationId = actualGenerationId;
        run.CurrentGenerationHash = actualGenerationSha;
        run.DiagnosticCode = null;
        run.FailedAtUtc = null;
        run.UpdatedAtUtc = FixedUtc;
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        await Context().StatisticReconciliationRuns.InsertOneAsync(run,
            cancellationToken: ct);

        var verdictRequest = InvokeTrustedFixture<
            StatisticReconciliationFinalVerdictRequest>(
            "MatchedVerdict", run.Id, actualGenerationId, actualGenerationSha);
        var verdictBackend = new StatisticReconciliationReviewMongoBackend(Context());
        var verdict = await new
            StatisticReconciliationFinalVerdictPublisher(verdictBackend)
            .PublishAsync(verdictRequest, FixedUtc, ct);
        var reviewBackend =
            new StatisticReconciliationIndependentReviewMongoBackend(Context());
        var service = new StatisticReconciliationIndependentReviewService(reviewBackend);
        var columns = new StatisticReconciliationEightColumnRecord(
            Sha(label + ":identity"),
            Sha(label + ":config"),
            verdict.ExpectedGenerationSha256,
            verdict.ActualGenerationSha256,
            verdict.DeltaManifestSha256,
            verdict.FreshnessAssessmentSha256 ?? Sha(label + ":freshness"),
            verdict.AuthorizationEvidenceSha256,
            verdict.Verdict);
        var generation = new StatisticReconciliationReviewGeneration(
            run.Id,
            verdict.VerdictGenerationId,
            verdict.VerdictGenerationSha256,
            verdict.DocumentSemanticSha256,
            columns,
            StatisticReconciliationIndependentReviewCanonical
                .ReviewRecordHash(columns),
            verdict.Verdict,
            true,
            true,
            false,
            run.ActorUserId,
            run.LatestWriterUserId,
            run.StateRevision);
        return new(run, verdict, generation, service, reviewBackend);
    }

    private async Task<StatisticReconciliationReviewGeneration>
        RefreshReviewGenerationAsync(
            P10DurableReviewFixture fixture,
            CancellationToken ct)
    {
        var run = await Context().StatisticReconciliationRuns
            .Find(value => value.Id == fixture.Run.Id)
            .SingleAsync(ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        return fixture.Generation with { StateRevision = run.StateRevision };
    }

    private static StatisticReconciliationReviewPermission ReviewPermission(
        string actorId,
        bool canReview = true,
        bool authenticated = true,
        bool scopeAuthorized = true,
        bool canViewOperator = true)
        => new(
            true,
            authenticated,
            scopeAuthorized,
            canReview,
            canViewOperator,
            actorId,
            Sha("permission:" + actorId),
            1,
            canViewOperator ? 1 : 0);

    private static StatisticReconciliationReviewCommand ReviewCommand(
        StatisticReconciliationReviewGeneration generation,
        string commandId,
        string gate)
    {
        var command = new StatisticReconciliationReviewCommand(
            commandId,
            new string('0', 64),
            generation.ReconciliationId,
            generation.GenerationId,
            generation.GenerationSha256,
            generation.SemanticVerdictSha256,
            gate,
            StatisticReconciliationReviewDecisions.Approve,
            generation.StateRevision);
        return command with
        {
            RequestSha256 = StatisticReconciliationIndependentReviewCanonical
                .CommandHash(command)
        };
    }

    private static StatisticReconciliationEvidenceExport CompileEvidence(
        string label,
        string format = StatisticReconciliationEvidenceFormats.Json,
        bool operatorDetail = false,
        string identity = "metric-identity")
    {
        var workId = ObjectId.GenerateNewId().ToString();
        var scopeId = ObjectId.GenerateNewId().ToString();
        var reconciliationId = ObjectId.GenerateNewId().ToString();
        var row = new StatisticReconciliationEvidenceRow(
            identity,
            "config-v1",
            new("DECIMAL", "VALUE", "1"),
            new("DECIMAL", "VALUE", "1"),
            new("DECIMAL", "VALUE", "0"),
            "FRESH",
            "AUTHORIZED",
            "MATCHED",
            ImmutableArray.Create("source-secret-stable-id"));
        var snapshot = new StatisticReconciliationEvidenceSnapshot(
            workId,
            scopeId,
            reconciliationId,
            Sha(label + ":generation-id"),
            Sha(label + ":generation"),
            Sha(label + ":verdict"),
            Sha(label + ":review"),
            Sha(label + ":approval"),
            FixedUtc,
            ImmutableArray.Create(row));
        return StatisticReconciliationEvidenceCanonical.Compile(
            snapshot,
            new StatisticReconciliationEvidenceCompileCommand(
                "export-" + label,
                format,
                operatorDetail,
                ObjectId.GenerateNewId().ToString(),
                Sha(label + ":permission"),
                operatorDetail,
                FixedUtc,
                TimeSpan.FromDays(1)));
    }

    private static StatisticReconciliationCandidateBinding RaceCandidateBinding()
        => new(
            ChainId,
            "P10-01",
            0,
            "1.7",
            Sha("race-candidate-catalog-raw"),
            Sha("race-candidate-catalog-semantic"),
            Sha("race-candidate-schema-raw"),
            Sha("race-candidate-schema-semantic"),
            Sha("race-candidate-stage-lock"),
            "tdtd_p10_race",
            [],
            "p10-race-stage",
            Sha("race-candidate-evidence"),
            Sha("race-candidate-generator"));

    private static MeResponse RaceActor(string id, string username)
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

    private static T InvokeTrustedFixture<T>(string name, params object[] arguments)
    {
        var methods = typeof(P10TrustedFinalizerProbe).GetMethods(
            BindingFlags.NonPublic | BindingFlags.Static);
        var method = methods.Single(value => value.Name == name &&
            value.GetParameters().Length == arguments.Length);
        try
        {
            return (T)(method.Invoke(null, arguments) ??
                       throw new InvalidOperationException(name + "_RETURNED_NULL"));
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static StatisticReconciliationExpectedCompiledGeneration
        BuildExpectedGeneration()
    {
        var context = new ExpectedLedgerCompilationContextPin(
            "p10-race-reconciliation",
            ExpectedSha("immutable-identity"),
            ExpectedSha("immutable-header"),
            "tenant-1",
            "work-1",
            "assignment-1",
            ChainId,
            "P10-01",
            "MONTH:2026-08",
            "period-instance-1",
            "race-concept",
            "MONTH",
            "APPROVED_AT",
            ExpectedSha("filter"),
            "form-version-1",
            ExpectedSha("form-schema"),
            "flow-template-version-1",
            ExpectedSha("flow-payload"),
            "flow-instance-1",
            "epoch-1",
            "p8-owner-1",
            ExpectedSha("p8-bundle"));
        const string payloadJson = "{\"value\":7}";
        var canonicalPayload = StatisticReconciliationExpectedLedgerCanonicalizer
            .NormalizeObject(payloadJson, 16 * 1024 * 1024, "$.race.payload",
                StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadJsonInvalid);
        var identity = new ExpectedSourceIdentityPin(
            "race-source-1",
            "race-report-1",
            context.WorkId,
            context.ScopeAssignmentId,
            1,
            ExpectedSha("payload-owner"),
            canonicalPayload.Sha256,
            1,
            ExpectedSha("lifecycle"),
            context.DynamicFormVersionId,
            context.FlowInstanceId!,
            context.ExecutionEpochId!);
        var candidate = new ExpectedLifecycleRevisionCandidate(
            "race-source-1",
            identity,
            "race-payload-1",
            canonicalPayload.Value,
            StatisticReconciliationExpectedLifecycleStatuses.Approved,
            true,
            true,
            StatisticReconciliationExpectedRuntimeDispositions.Current);
        var membership = new CurrentEpochFlowMembership(
            context,
            context.FlowTemplateVersionId!,
            context.FlowPayloadSha256!,
            context.FlowInstanceId!,
            context.ExecutionEpochId!,
            1,
            1,
            ["race-source-1"]);
        var configurationJson = JsonSerializer.Serialize(new
        {
            expectedMetrics = new[]
            {
                new
                {
                    family = StatisticReconciliationExpectedMetricFamilies.Direct,
                    kind = StatisticReconciliationExpectedMetricKinds.Field,
                    metricId = "value",
                    fieldId = "field-value",
                    jsonPointer = "/value",
                    valueType = StatisticReconciliationExpectedValueTypes.Number,
                    operations = new[] { StatisticReconciliationExpectedMetricOperations.Sum },
                    expandArray = false,
                    unordered = false
                }
            }
        });
        var canonicalConfiguration = StatisticReconciliationExpectedLedgerCanonicalizer
            .NormalizeObject(configurationJson, 4 * 1024 * 1024, "$.race.config",
                StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid);
        var configuration = new LockedP8Configuration(
            context,
            context.P8ConfigurationOwnerId,
            context.P8ConfigurationBundleSha256,
            canonicalConfiguration.Sha256,
            canonicalConfiguration.Value,
            [new LockedP8ConfigurationPin(
                StatisticReconciliationExpectedLedgerConfigurationKinds.Field,
                context.P8ConfigurationOwnerId,
                "config-field",
                "config-field-v1",
                1,
                1,
                ExpectedSha("config-field"))]);
        var lineage = new P5P7RuntimeMappingContributionLineage(
            context,
            StatisticReconciliationExpectedLedgerLineageLayers.Required.Select(
                (layer, index) => new ExpectedLedgerLineagePin(
                    layer,
                    $"owner-{index}",
                    $"version-{index}",
                    index + 1,
                    ExpectedSha($"lineage-{index}"))));
        var contribution = new ExpectedContributionCandidate(
            "race-source-1",
            StatisticReconciliationExpectedContributionPolicies.Include,
            "contribution-version-1",
            1,
            ExpectedSha("contribution-policy"),
            "contribution-provenance-1",
            ExpectedSha("contribution-provenance"),
            true);
        var plan = new StatisticReconciliationExpectedSourcePlanner(
                new StatisticReconciliationExpectedLedgerCompiler())
            .Plan(new ExpectedAuthoritativeSourceSnapshot(
                context,
                [candidate],
                membership,
                configuration,
                lineage,
                [contribution]));
        var pins = StatisticReconciliationExpectedCatalogPins.Create(
            "P9-CATALOG-V1",
            ExpectedSha("p9-catalog-raw"),
            ExpectedSha("p9-catalog-semantic"),
            ExpectedSha("p9-schema-raw"),
            ExpectedSha("p9-schema-semantic"),
            ExpectedSha("p9-stage-lock"),
            ChainId,
            "P10-01",
            "P10-CANDIDATE-V1",
            ExpectedSha("candidate-catalog-raw"),
            ExpectedSha("candidate-catalog-semantic"),
            ExpectedSha("candidate-schema-raw"),
            ExpectedSha("candidate-schema-semantic"),
            ExpectedSha("candidate-stage-lock"));
        return new StatisticReconciliationExpectedTypedCompiler(
                new StatisticReconciliationExpectedMetricIdentityCompiler())
            .Compile(plan, pins);
    }

    private static string ExpectedSha(string value) =>
        StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            "P10_RACE_EXPECTED_FIXTURE_V1", value);
}

internal sealed record P10DurableReviewFixture(
    StatisticReconciliationRun Run,
    StatisticReconciliationReview Verdict,
    StatisticReconciliationReviewGeneration Generation,
    StatisticReconciliationIndependentReviewService Service,
    StatisticReconciliationIndependentReviewMongoBackend Backend);
