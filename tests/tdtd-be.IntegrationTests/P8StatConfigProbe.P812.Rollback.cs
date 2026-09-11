using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    internal const string RollbackCommandLineSwitch = "--p8-rollback-probe";
    private const string P812RollbackPromptId = "P8-12";

    internal static async Task<int> RunP812RollbackProbeAsync(string[] args)
    {
        if (args.Length != 1 || !string.Equals(
                args[0],
                RollbackCommandLineSwitch,
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "P8 rollback probe accepts only --p8-rollback-probe.");
            return 2;
        }

        try
        {
            AssertP812RollbackCatalog();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        return await new P8StatConfigProbe(12)
            .ExecuteP812RollbackProbeAsync(args, cancellation.Token);
    }

    private async Task<int> ExecuteP812RollbackProbeAsync(
        string[] args,
        CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8))
            .ToLowerInvariant();
        var runKey =
            $"p812_rollback_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{nonce[..8]}";
        _paths = HarnessPaths.CreateP8(runKey, ChainId, P812RollbackPromptId);
        _iterationRoot = _paths.IterationRoot(1);
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? api = null;
        IReadOnlyDictionary<string, P8CollectionState>? globalBefore = null;
        IReadOnlyDictionary<string, P8CollectionState>? globalAfter = null;
        P812RollbackFixtures? fixtures = null;
        var outcomes = new List<P812RollbackOutcome>();
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                chainId = ChainId,
                promptId = P812RollbackPromptId,
                runKey,
                state = "ALLOCATING",
                ownedRoots = new[] { _paths.RunRoot },
                startedAtUtc
            },
            ct);

        try
        {
            mongo = await MongoReplicaSetLease.StartAsync(
                _paths,
                _iterationRoot,
                runKey,
                1,
                ct);
            _mongo = mongo;
            _database = mongo.Client.GetDatabase(mongo.DatabaseName);
            backend = await BackendServerLease.StartAsync(
                _paths,
                _iterationRoot,
                runKey,
                mongo,
                ct);
            _backend = backend;
            RememberArtifactSecret("bootstrap-key", backend.BootstrapKey);
            RememberArtifactSecret("actor-password", backend.ActorPassword);
            api = new ApiHarnessClient(backend.BaseUri);
            _api = api;

            await BootstrapAndSeedActorsAsync(ct);
            fixtures = await SeedP812RollbackFixturesAsync(ct);
            // Hangfire.Mongo creates its isolated schema lazily on the first
            // summary-service resolution. Establish and hash-stabilize that
            // run-owned infrastructure before any rollback case snapshot so
            // every later delta belongs only to the attempted P8 mutation.
            await AwaitP809InfrastructureBaselineAsync(ct);
            globalBefore = await CaptureDatabaseSnapshotAsync(ct);
            await RunP812RollbackMutationCasesAsync(
                fixtures,
                outcomes,
                ct);
            globalAfter = await CaptureDatabaseSnapshotAsync(ct);
            var globalDelta = BuildDeltas(globalBefore, globalAfter);
            HarnessAssert.True(
                globalDelta.All(delta => !delta.Changed),
                "P8 rollback probe changed Mongo outside a case boundary: " +
                string.Join(", ", globalDelta.Where(delta => delta.Changed)
                    .Select(delta => delta.Collection)));
        }
        catch (Exception error)
        {
            fatalFailure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            api?.Dispose();
            if (backend is not null)
            {
                try { await backend.StopAsync(); }
                catch (Exception error) { cleanupErrors.Add($"backend-stop: {error.Message}"); }
                try { await backend.DisposeAsync(); }
                catch (Exception error) { cleanupErrors.Add($"backend-dispose: {error.Message}"); }
            }
            if (mongo is not null)
            {
                try { await mongo.DropDatabaseGuardedAsync(CancellationToken.None); }
                catch (Exception error) { cleanupErrors.Add($"mongo-drop: {error.Message}"); }
                try { await mongo.StopProcessAsync(); }
                catch (Exception error) { cleanupErrors.Add($"mongo-stop: {error.Message}"); }
                try { mongo.RemoveDataDirectoryGuarded(); }
                catch (Exception error) { cleanupErrors.Add($"mongo-data: {error.Message}"); }
                try { await mongo.DisposeAsync(); }
                catch (Exception error) { cleanupErrors.Add($"mongo-dispose: {error.Message}"); }
            }
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "api-exchanges.json"),
            new
            {
                chainId = ChainId,
                promptId = P812RollbackPromptId,
                runKey,
                apiExchanges = api?.Exchanges ?? []
            });
        var securityScan = await RunArtifactSecurityScanAsync();
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "security-scan.json"),
            new { chainId = ChainId, promptId = P812RollbackPromptId, runKey, securityScan });

        var results = _cases.Results
            .OrderBy(item => item.CaseId, StringComparer.Ordinal)
            .ToArray();
        var expectedCaseIds = P812RollbackCaseIds;
        var exactCaseIds = results.Select(item => item.CaseId)
            .SequenceEqual(expectedCaseIds, StringComparer.Ordinal);
        var globalDeltas = globalBefore is null || globalAfter is null
            ? Array.Empty<P8CollectionDelta>()
            : BuildDeltas(globalBefore, globalAfter).ToArray();
        var cleanupSucceeded = cleanupErrors.Count == 0 &&
                               backend?.StopVerified == true &&
                               backend.PortReleaseVerified &&
                               mongo?.DatabaseDropVerified == true &&
                               mongo.ProcessStopVerified &&
                               mongo.PortReleaseVerified &&
                               mongo.DataDirectoryRemovalVerified;
        var passed = fatalFailure is null &&
                     exactCaseIds &&
                     results.Length == expectedCaseIds.Length &&
                     results.All(item => item.Verdict == HarnessVerdict.DAT) &&
                     outcomes.Count == expectedCaseIds.Length &&
                     outcomes.All(item => item.DeltaZero) &&
                     globalDeltas.All(item => !item.Changed) &&
                     cleanupSucceeded &&
                     securityScan.Passed;
        var failureReason = fatalFailure;
        if (failureReason is null && !exactCaseIds)
            failureReason = "Rollback probe case IDs drifted.";
        if (failureReason is null && results.Any(item => item.Verdict != HarnessVerdict.DAT))
            failureReason = "One or more rollback mutations did not fail closed.";
        if (failureReason is null && globalDeltas.Any(item => item.Changed))
            failureReason = "Rollback mutations changed Mongo persistence.";
        if (failureReason is null && !cleanupSucceeded)
            failureReason = "Rollback probe cleanup was not fully verified.";
        if (failureReason is null && !securityScan.Passed)
            failureReason = "Rollback artifact security scan failed.";

        var completedAtUtc = DateTime.UtcNow;
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-12-rollback-v1.4-zero-write.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = P812RollbackPromptId,
                runKey,
                commandLine = args,
                startedAtUtc,
                completedAtUtc,
                generatedCurrent = P812CatalogEvidence(),
                realInfrastructure = new
                {
                    kestrel = true,
                    mongoReplicaSet = true,
                    directMongoHashOracle = true,
                    backendProcessId = backend?.ProcessId,
                    mongoProcessId = mongo?.ProcessId,
                    databaseName = mongo?.DatabaseName
                },
                p7Availability = new
                {
                    activationEnabled = DynamicFlowP7CatalogCandidate.ActivationEnabled,
                    fixtureCreatedThroughRealKestrel = fixtures is not null,
                    familyId = fixtures?.Flow.FamilyId,
                    versionId = fixtures?.Flow.Version.Id,
                    mappingCapabilityCount = DynamicFormFlowCapabilityCatalogMetadata
                        .DynamicFlowMappingCapabilities.Count
                },
                exactExpectedCaseCount = expectedCaseIds.Length,
                actualCaseCount = results.Length,
                exactCaseIds,
                normalizedSemanticSha256 = _cases.BuildNormalizedSha256(),
                results,
                outcomes,
                globalDocumentSetHashDeltaZero = globalDeltas.All(item => !item.Changed),
                globalDeltas,
                watchedCollections = globalDeltas.Select(item => item.Collection).ToArray(),
                configReceiptJobAuditOutboxResultDeltaZero = globalDeltas.All(item => !item.Changed),
                cleanupSucceeded,
                artifactSecurityScanPassed = securityScan.Passed,
                fatalFailure,
                passed,
                failureReason
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                chainId = ChainId,
                promptId = P812RollbackPromptId,
                runKey,
                state = cleanupSucceeded ? "CLEANED" : "CLEANUP_FAILED",
                backend = backend is null ? null : new
                {
                    backend.ProcessId,
                    backend.Port,
                    backend.StopVerified,
                    backend.PortReleaseVerified
                },
                mongo = mongo is null ? null : new
                {
                    mongo.DatabaseName,
                    mongo.ProcessId,
                    mongo.Port,
                    mongo.DatabaseDropVerified,
                    mongo.ProcessStopVerified,
                    mongo.PortReleaseVerified,
                    mongo.DataDirectoryRemovalVerified
                },
                cleanupSucceeded,
                cleanupErrors,
                completedAtUtc
            });

        Console.WriteLine(
            passed
                ? $"[DAT] P8-12 rollback v1.4 zero-write probe passed {results.Length}/{expectedCaseIds.Length}; artifact={Path.Combine(_paths.RunRoot, "p8-12-rollback-v1.4-zero-write.json")}" 
                : $"[KHONG_DAT] P8-12 rollback probe failed: {failureReason}; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private async Task<P812RollbackFixtures> SeedP812RollbackFixturesAsync(
        CancellationToken ct)
    {
        var actor = Actor("system_admin");
        var field = NewFieldFixture(
            "rollback",
            [FixtureField("number", "number")]);
        var table = NewTableFixture(
            "rollback",
            [TableBlock(
                "rollback-table",
                "FIXED_GRID",
                [TableMetricFixture("metric:number", "NUMBER")])]);
        var basicForm = BuildBasicDynamicFormFixture(
            "P812_ROLLBACK_BASIC",
            "P8-12 rollback Basic",
            includeLegacyRowLabelDeclaration: false);
        var basic = BuildBasicAssignmentFixture(
            "rollback",
            0,
            basicForm);
        var advancedForm = BuildAdvancedDynamicFormFixture();
        var advanced = BuildAdvancedAssignmentFixture(
            "rollback",
            0,
            advancedForm,
            "system_admin");
        var diff = BuildDiffAssignmentFixture(
            "rollback",
            0,
            advancedForm);
        var fixedAt = new DateTime(2026, 8, 3, 0, 12, 0, DateTimeKind.Utc);
        var flowRoot = BuildP807PublishedForm(
            actor,
            fixedAt,
            "P812_ROLLBACK_FLOW_ROOT",
            "field_note",
            "note");
        var flowChild = BuildP807PublishedForm(
            actor,
            fixedAt.AddSeconds(1),
            "P812_ROLLBACK_FLOW_CHILD",
            "field_child_value",
            "child_value");
        _flowRootFormId = flowRoot.Id;
        _flowChildFormId = flowChild.Id;

        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertManyAsync(
                [
                    field.Template,
                    table.Form.Template,
                    basicForm,
                    advancedForm,
                    flowRoot,
                    flowChild
                ],
                cancellationToken: ct);
        await _database.GetCollection<WorkAssignment>("work_assignments")
            .InsertManyAsync(
                [basic.Assignment, advanced.Assignment, diff.Assignment],
                cancellationToken: ct);

        var label = await SeedP812RollbackLabelAsync(actor, fixedAt, ct);
        var resetJob = BuildP812RollbackJob(
            label,
            actor,
            "reset",
            StatConfigValidationJobStatuses.Failed,
            7,
            new string('b', 64),
            fixedAt);
        var cancelJob = BuildP812RollbackJob(
            label,
            actor,
            "cancel",
            StatConfigValidationJobStatuses.Pending,
            3,
            new string('c', 64),
            fixedAt.AddSeconds(1));
        await _database.GetCollection<StatConfigValidationJob>(
                "stat_config_validation_jobs")
            .InsertManyAsync([resetJob, cancelJob], cancellationToken: ct);

        var quota = await ReadTokenQuotaAsync(actor, _unitAId, ct);
        var compensable = new WorkSummaryTokenLedger
        {
            Id = ObjectId.GenerateNewId().ToString(),
            RecordKind = WorkSummaryTokenLedgerRecordKinds.Entry,
            OwnerUnitId = _unitAId,
            ActorUserId = actor.Id,
            IssuerUserId = actor.Id,
            TokenKind = WorkSummaryTokenKinds.AdvancedSummaryConfigLock,
            Direction = WorkSummaryTokenDirections.Consume,
            Units = 1,
            MonthlyQuota = quota.MonthlyQuota,
            BaseMonthlyQuota = quota.BaseMonthlyQuota,
            GrantedUnits = quota.GrantedUnits,
            UsedUnits = 1,
            Revision = quota.Revision,
            PoolHash = quota.PoolHash,
            PeriodMonthKey = quota.PeriodMonthKey,
            WorkId = advanced.Assignment.WorkId,
            WorkAssignmentId = advanced.Assignment.Id,
            DynamicFormTemplateId = advanced.DynamicFormTemplateId,
            SectionId = advanced.SectionId,
            ConfigId = ObjectId.GenerateNewId().ToString(),
            ConfigVersionNo = 1,
            ConfigHash = new string('d', 64),
            Outcome = WorkSummaryTokenOutcomes.Success,
            Reason = "P812_ROLLBACK_COMPENSATION_FIXTURE",
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        await _database.GetCollection<WorkSummaryTokenLedger>(
                TokenLedgersCollection)
            .InsertOneAsync(compensable, cancellationToken: ct);

        var flow = await CreateFlowDraftAsync(
            actor,
            "rollback-p7-available",
            "p812-rollback-p7-create",
            BuildP807MappedFlowPayload(),
            ct);
        return new P812RollbackFixtures(
            label,
            field,
            table,
            basic,
            advanced,
            diff,
            flow,
            resetJob,
            cancelJob,
            quota,
            compensable);
    }

    private async Task<LabelCatalogItem> SeedP812RollbackLabelAsync(
        P8Actor actor,
        DateTime fixedAt,
        CancellationToken ct)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var label = new LabelCatalogItem
        {
            Id = id,
            Code = "p812.rollback.label",
            Name = "P8-12 rollback label",
            NameLower = "p8-12 rollback label",
            Description = "Exact v1.4 fail-closed fixture",
            Color = "#335577",
            GroupCode = "p812",
            Usage = LabelUsages.Statistic,
            DataType = LabelDataTypes.Number,
            ValueSourceType = LabelValueSourceTypes.None,
            ScopeType = LabelScopeTypes.Global,
            IsActive = true,
            ConfigId = ObjectId.GenerateNewId().ToString(),
            VersionId = ObjectId.GenerateNewId().ToString(),
            VersionNo = 1,
            Revision = 1,
            DependencyPins = [],
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        label.ConfigHash = StatConfigCanonicalJson.HashObject(new
        {
            labelId = label.Id,
            label.Code,
            label.Name,
            label.Description,
            label.Color,
            label.GroupCode,
            label.Usage,
            label.DataType,
            label.ValueSourceType,
            valueOptions = Array.Empty<object>(),
            label.ValueSourceCatalogId,
            label.ValueSourceCatalogCode,
            label.ValueSourceCatalogName,
            label.ScopeType,
            label.ScopeId,
            label.IsActive,
            status = "ACTIVE",
            dependencyPins = Array.Empty<string>()
        });
        label.VersionSnapshots =
        [
            new LabelConfigVersionSnapshot
            {
                LabelId = label.Id,
                VersionId = label.VersionId,
                VersionNo = 1,
                Revision = 1,
                Status = "ACTIVE",
                ConfigHash = label.ConfigHash,
                Code = label.Code,
                Name = label.Name,
                Usage = label.Usage,
                DataType = label.DataType,
                ValueSourceType = label.ValueSourceType,
                ScopeType = label.ScopeType,
                IsActive = true,
                DependencyPins = [],
                CreatedAtUtc = fixedAt,
                CreatedByUserId = actor.Id
            }
        ];
        await _database.GetCollection<LabelCatalogItem>(LabelsCollection)
            .InsertOneAsync(label, cancellationToken: ct);
        return label;
    }

    private static StatConfigValidationJob BuildP812RollbackJob(
        LabelCatalogItem label,
        P8Actor actor,
        string suffix,
        string status,
        long stateRevision,
        string stateHash,
        DateTime fixedAt)
        => new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            QueueName = StatConfigValidationQueue.Name,
            DedupeKey = Sha256(System.Text.Encoding.UTF8.GetBytes($"p812:{suffix}")),
            OwnerKind = "LABEL",
            OwnerId = label.Id,
            ConfigId = label.ConfigId!,
            VersionId = label.VersionId!,
            VersionNo = label.VersionNo,
            ConfigRevision = label.Revision,
            ConfigHash = label.ConfigHash!,
            DependencyPins = [],
            DependencyPinsHash = Sha256(Array.Empty<byte>()),
            BundleHash = Sha256(System.Text.Encoding.UTF8.GetBytes($"bundle:{suffix}")),
            EnqueueCommandId = $"p812-rollback-{suffix}-seed",
            CommandReceiptId = Sha256(System.Text.Encoding.UTF8.GetBytes($"receipt:{suffix}")),
            AuditOutboxId = Sha256(System.Text.Encoding.UTF8.GetBytes($"audit:{suffix}")),
            Status = status,
            IsActive = status == StatConfigValidationJobStatuses.Pending,
            StateRevision = stateRevision,
            StateHash = stateHash,
            RequestedByUserId = actor.Id,
            CorrelationId = $"p812-rollback-{suffix}",
            RetryCount = status == StatConfigValidationJobStatuses.Failed ? 1 : 0,
            MaxRetryCount = 3,
            FailedAtUtc = status == StatConfigValidationJobStatuses.Failed
                ? fixedAt
                : null,
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };

    private async Task RunP812RollbackMutationCasesAsync(
        P812RollbackFixtures fixtures,
        List<P812RollbackOutcome> outcomes,
        CancellationToken ct)
    {
        var admin = Actor("system_admin");
        var labelPayload = LabelPayload(
            fixtures.Label.Code,
            "P8-12 rollback label changed",
            "GLOBAL",
            null,
            usage: "STATISTIC",
            dataType: "NUMBER");

        await ProbeP812RollbackMutationAsync(
            "P8-RBK-001",
            "LABEL_CREATE",
            () => _api.PostAsync(
                "api/labels/config",
                Envelope(
                    "p812-rbk-label-create",
                    0,
                    EmptyConfigHash,
                    LabelPayload(
                        "p812.rollback.new",
                        "P8-12 rollback new label",
                        "GLOBAL",
                        null,
                        usage: "STATISTIC",
                        dataType: "NUMBER")),
                admin.Token,
                ct: ct),
            outcomes,
            ct);
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-002",
            "LABEL_UPDATE",
            () => _api.PutAsync(
                $"api/labels/{fixtures.Label.Id}/config",
                Envelope(
                    "p812-rbk-label-update",
                    fixtures.Label.Revision,
                    fixtures.Label.ConfigHash!,
                    labelPayload),
                admin.Token,
                ct: ct),
            outcomes,
            ct);
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-003",
            "LABEL_TOMBSTONE",
            () => _api.PostAsync(
                $"api/labels/{fixtures.Label.Id}/config/tombstone",
                Envelope(
                    "p812-rbk-label-tombstone",
                    fixtures.Label.Revision,
                    fixtures.Label.ConfigHash!,
                    new JsonObject()),
                admin.Token,
                ct: ct),
            outcomes,
            ct);

        var field = fixtures.Field.Fields.Single();
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-004",
            "FIELD_METADATA_PATCH",
            () => _api.PatchAsync(
                $"api/dynamic-forms/{fixtures.Field.Id}/statistics",
                Envelope(
                    "p812-rbk-field",
                    0,
                    EmptyConfigHash,
                    FieldPayload(FieldPatch(field.Id, ["COUNT", "SUM"]))),
                admin.Token,
                ct: ct),
            outcomes,
            ct);

        var block = fixtures.Table.Blocks.Single();
        var metric = block.Metrics.Single();
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-005",
            "TABLE_METADATA_PATCH",
            () => _api.PatchAsync(
                $"api/dynamic-forms/{fixtures.Table.Form.Id}/statistics",
                Envelope(
                    "p812-rbk-table",
                    0,
                    EmptyConfigHash,
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [TableMetric(metric.MetricKey, metric.DataType, ["COUNT", "SUM"])]))),
                admin.Token,
                ct: ct),
            outcomes,
            ct);

        var basicTarget = BasicTarget(
            "FIELD",
            "basic_number_sum",
            "NUMBER",
            "SUM");
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-006",
            "BASIC_SUMMARY_CONFIG",
            () => _api.PutAsync(
                BasicConfigRoute(fixtures.Basic),
                Envelope(
                    "p812-rbk-basic",
                    0,
                    EmptyConfigHash,
                    BasicDirectPayload([basicTarget])),
                admin.Token,
                ct: ct),
            outcomes,
            ct);
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-007",
            "FLOW_SCOPE_CONFIG",
            () => _api.PutAsync(
                BasicConfigRoute(fixtures.Basic),
                Envelope(
                    "p812-rbk-flow-scope",
                    0,
                    EmptyConfigHash,
                    BasicPayload(
                        BasicFlowSourceScope(fixtures.Basic, "FLOW_STEP"),
                        BasicPeriodRule("ALL_PERIODS"),
                        [],
                        BasicDetailHints(),
                        [basicTarget])),
                admin.Token,
                ct: ct),
            outcomes,
            ct);

        await ProbeP812RollbackMutationAsync(
            "P8-RBK-008",
            "ADVANCED_SUMMARY_CONFIG",
            () => _api.PutAsync(
                AdvancedConfigRoute(fixtures.Advanced),
                Envelope(
                    "p812-rbk-advanced",
                    0,
                    EmptyConfigHash,
                    AdvancedPayload(fixtures.Advanced)),
                admin.Token,
                ct: ct),
            outcomes,
            ct);

        await ProbeP812RollbackMutationAsync(
            "P8-RBK-009",
            "WORK_SUMMARY_TOKEN_GRANT",
            () => _api.PostAsync(
                TokenPoolGrantRoute(_unitAId),
                Envelope(
                    "p812-rbk-token-grant",
                    fixtures.Quota.Revision,
                    fixtures.Quota.PoolHash,
                    TokenGrantPayload(1, "P812_ROLLBACK")),
                admin.Token,
                ct: ct),
            outcomes,
            ct);
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-010",
            "WORK_SUMMARY_TOKEN_COMPENSATION",
            () => _api.PostAsync(
                TokenCompensationRoute(fixtures.Compensable.Id),
                Envelope(
                    "p812-rbk-token-compensate",
                    fixtures.Quota.Revision,
                    fixtures.Quota.PoolHash,
                    TokenCompensationPayload("P812_ROLLBACK")),
                admin.Token,
                ct: ct),
            outcomes,
            ct);

        await ProbeP812RollbackMutationAsync(
            "P8-RBK-011",
            "DIFF_CONFIG",
            () => _api.PutAsync(
                DiffConfigRoute(fixtures.Diff),
                Envelope(
                    "p812-rbk-diff",
                    0,
                    EmptyConfigHash,
                    DiffPayload(DiffSelector(
                        "FIELD",
                        "adv_number_0001",
                        "adv_number_0001"))),
                admin.Token,
                ct: ct),
            outcomes,
            ct);

        await ProbeP812RollbackMutationAsync(
            "P8-RBK-012",
            "FLOW_CONTRIBUTION_EXPLICIT_INCLUDE_LOCK",
            () => _api.PostAsync(
                $"api/dynamic-flow-templates/{fixtures.Flow.FamilyId}/versions/" +
                $"{fixtures.Flow.Version.Id}/lock",
                new JsonObject
                {
                    ["commandId"] = "p812-rbk-flow-include",
                    ["expectedFamilyRevision"] = fixtures.Flow.FamilyRevision,
                    ["expectedDraftRevision"] = fixtures.Flow.Version.DraftRevision,
                    ["expectedPayloadHash"] = fixtures.Flow.Version.PayloadHash,
                    ["contributionPolicy"] = "INCLUDE",
                    ["acknowledgeContributionWarning"] = true
                },
                admin.Token,
                ct: ct),
            outcomes,
            ct);

        await ProbeP812RollbackMutationAsync(
            "P8-RBK-013",
            "READINESS_ENQUEUE",
            () => _api.PostAsync(
                $"api/stat-config/owners/LABEL/{fixtures.Label.Id}/readiness-jobs",
                Envelope(
                    "p812-rbk-readiness-enqueue",
                    fixtures.Label.Revision,
                    fixtures.Label.ConfigHash!,
                    new JsonObject
                    {
                        ["configId"] = fixtures.Label.ConfigId,
                        ["versionId"] = fixtures.Label.VersionId,
                        ["versionNo"] = fixtures.Label.VersionNo
                    }),
                admin.Token,
                ct: ct),
            outcomes,
            ct);
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-014",
            "READINESS_RESET",
            () => _api.PostAsync(
                $"api/admin/operations/job-runs/stat-config-readiness-jobs/{fixtures.ResetJob.Id}/reset",
                Envelope(
                    "p812-rbk-readiness-reset",
                    fixtures.ResetJob.StateRevision,
                    fixtures.ResetJob.StateHash,
                    new JsonObject { ["reason"] = "P812_ROLLBACK" }),
                admin.Token,
                ct: ct),
            outcomes,
            ct);
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-015",
            "READINESS_CANCEL",
            () => _api.PostAsync(
                $"api/admin/operations/job-runs/stat-config-readiness-jobs/{fixtures.CancelJob.Id}/cancel",
                Envelope(
                    "p812-rbk-readiness-cancel",
                    fixtures.CancelJob.StateRevision,
                    fixtures.CancelJob.StateHash,
                    new JsonObject { ["reason"] = "P812_ROLLBACK" }),
                admin.Token,
                ct: ct),
            outcomes,
            ct);
        await ProbeP812RollbackMutationAsync(
            "P8-RBK-016",
            "READINESS_CLEANUP",
            () => _api.PostAsync(
                "api/admin/operations/job-runs/stat-config-readiness-jobs/cleanup",
                Envelope(
                    "p812-rbk-readiness-cleanup",
                    0,
                    EmptyConfigHash,
                    new JsonObject
                    {
                        ["completedBeforeUtc"] = "2026-08-03T00:00:00Z",
                        ["limit"] = 100,
                        ["dryRun"] = true
                    }),
                admin.Token,
                ct: ct),
            outcomes,
            ct);
    }

    private async Task ProbeP812RollbackMutationAsync(
        string caseId,
        string surface,
        Func<Task<ApiHarnessResponse>> request,
        List<P812RollbackOutcome> outcomes,
        CancellationToken ct)
    {
        await _cases.RunAsync(caseId, async () =>
        {
            var before = await CaptureDatabaseSnapshotAsync(ct);
            var response = await request();
            AssertP812RollbackConflict(response, surface);
            var after = await CaptureDatabaseSnapshotAsync(ct);
            var deltas = BuildDeltas(before, after);
            var changed = deltas.Where(item => item.Changed)
                .Select(item => item.Collection)
                .ToArray();
            HarnessAssert.True(
                changed.Length == 0,
                $"{surface} changed Mongo collections: {string.Join(", ", changed)}");
            outcomes.Add(new P812RollbackOutcome(
                caseId,
                surface,
                (int)response.StatusCode,
                ApiHarnessClient.FindStringRecursive(response.Json, "errorCode")!,
                ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
                ApiHarnessClient.FindStringRecursive(response.Json, "actualCatalogVersion"),
                ApiHarnessClient.FindStringRecursive(response.Json, "requiredCatalogVersion"),
                true,
                changed));
            return new CaseObservation(
                $"{surface} failed closed at exact v1.4 before every Mongo write.",
                $"surface={surface};http=409;code=STAT_CONFIG_CAPABILITY_CONFLICT;delta=0");
        });
    }

    private static void AssertP812RollbackConflict(
        ApiHarnessResponse response,
        string surface)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            $"P8-12 rollback {surface}");
        HarnessAssert.Equal(
            "STAT_CONFIG_CAPABILITY_CONFLICT",
            ApiHarnessClient.FindStringRecursive(response.Json, "errorCode"),
            $"{surface} capability error code drifted");
        HarnessAssert.Equal(
            "STAT_CONFIG_CAPABILITY_CONFLICT",
            ApiHarnessClient.FindStringRecursive(response.Json, "reason"),
            $"{surface} capability reason drifted");
        HarnessAssert.Equal(
            "1.4",
            ApiHarnessClient.FindStringRecursive(response.Json, "actualCatalogVersion"),
            $"{surface} actual catalog version drifted");
        HarnessAssert.Equal(
            "1.5",
            ApiHarnessClient.FindStringRecursive(response.Json, "requiredCatalogVersion"),
            $"{surface} required catalog version drifted");
        HarnessAssert.Equal(
            0,
            ApiHarnessClient.FindIntRecursive(response.Json, "writes") ?? -1,
            $"{surface} did not expose writes=0");
    }

    private static void AssertP812RollbackCatalog()
    {
        HarnessAssert.Equal(
            "1.4",
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            "P8 rollback probe requires generated CURRENT v1.4");
        HarnessAssert.Equal(
            DynamicFlowP7CatalogCandidate.SemanticHash,
            DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            "P8 rollback probe requires the exact immutable v1.4 semantic hash");
        HarnessAssert.True(
            DynamicFlowP7CatalogCandidate.ActivationEnabled,
            "P7 must remain active under exact v1.4 rollback");
        HarnessAssert.True(
            !DynamicFlowP8CatalogCandidate.ActivationEnabled &&
            !StatConfigCatalogActivation.ActivationEnabled,
            "P8 mutation activation must be false under exact v1.4 rollback");
        HarnessAssert.Equal(
            0,
            DynamicFormFlowCapabilityCatalogMetadata
                .StatisticsConfigurationCapabilities.Count,
            "v1.4 must expose zero P8 statistics-configuration capabilities");

        var expectedMapping = new[]
        {
            "FIELD_TYPED|SUPPORTED|P7",
            "FIXED_GRID|SUPPORTED|P7",
            "APPEND_ROWS|SUPPORTED|P7",
            "MATRIX_SPARSE|SUPPORTED|P7",
            "SOURCE_REPORT_GRAIN|SUPPORTED|P7",
            "GROUP_GRAIN|INTENTIONAL_BLOCK|<null>",
            "CUSTOM_JOIN_KEY|INTENTIONAL_BLOCK|<null>",
            "APPEND_COLUMNS_TARGET|INTENTIONAL_BLOCK|<null>",
            "SCALAR_TO_ROW|INTENTIONAL_BLOCK|<null>",
            "ROW_TO_REPORT|INTENTIONAL_BLOCK|<null>"
        };
        var actualMapping = DynamicFormFlowCapabilityCatalogMetadata
            .DynamicFlowMappingCapabilities
            .Select(item => $"{item.Id}|{item.Status}|{item.TargetPhase ?? "<null>"}")
            .ToArray();
        HarnessAssert.True(
            actualMapping.SequenceEqual(expectedMapping, StringComparer.Ordinal),
            "Exact v1.4 mapping capability metadata drifted");

        var expectedStatistics = new[]
        {
            "DIRECT_FIELD_TABLE_LABEL|PATCH_REQUIRED|P9",
            "BASIC_SUMMARY|PATCH_REQUIRED|P9",
            "ADVANCED_SUMMARY|PATCH_REQUIRED|P9",
            "DIFF|PATCH_REQUIRED|P9",
            "FLOW_SCOPES|PATCH_REQUIRED|P9",
            "FLOW_STATISTIC_PROFILE|INTENTIONAL_BLOCK|<null>"
        };
        var actualStatistics = DynamicFormFlowCapabilityCatalogMetadata
            .StatisticsCapabilities
            .Select(item => $"{item.Id}|{item.Status}|{item.TargetPhase ?? "<null>"}")
            .ToArray();
        HarnessAssert.True(
            actualStatistics.SequenceEqual(expectedStatistics, StringComparer.Ordinal),
            "Exact v1.4 six-entry statistics executor/result metadata drifted");
    }

    private static object P812CatalogEvidence()
        => new
        {
            version = DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            semanticHash = DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
            schemaHash = DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256,
            p7ActivationEnabled = DynamicFlowP7CatalogCandidate.ActivationEnabled,
            p8ActivationEnabled = DynamicFlowP8CatalogCandidate.ActivationEnabled,
            statConfigActivationEnabled = StatConfigCatalogActivation.ActivationEnabled,
            statisticsConfigurationCapabilityCount =
                DynamicFormFlowCapabilityCatalogMetadata
                    .StatisticsConfigurationCapabilities.Count,
            dynamicFlowMappingCapabilities = DynamicFormFlowCapabilityCatalogMetadata
                .DynamicFlowMappingCapabilities
                .Select(item => new { item.Id, item.Status, item.TargetPhase })
                .ToArray(),
            statisticsCapabilities = DynamicFormFlowCapabilityCatalogMetadata
                .StatisticsCapabilities
                .Select(item => new { item.Id, item.Status, item.TargetPhase })
                .ToArray()
        };

    private static readonly string[] P812RollbackCaseIds =
        Enumerable.Range(1, 16)
            .Select(index => $"P8-RBK-{index:000}")
            .ToArray();
}

internal sealed record P812RollbackFixtures(
    LabelCatalogItem Label,
    P8FormFixture Field,
    P8TableFixture Table,
    P8BasicFixture Basic,
    P8AdvancedFixture Advanced,
    P8DiffFixture Diff,
    P8FlowDraftFixture Flow,
    StatConfigValidationJob ResetJob,
    StatConfigValidationJob CancelJob,
    P8TokenQuotaIdentity Quota,
    WorkSummaryTokenLedger Compensable);

internal sealed record P812RollbackOutcome(
    string CaseId,
    string Surface,
    int StatusCode,
    string ErrorCode,
    string? Reason,
    string? ActualCatalogVersion,
    string? RequiredCatalogVersion,
    bool DeltaZero,
    IReadOnlyList<string> ChangedCollections);