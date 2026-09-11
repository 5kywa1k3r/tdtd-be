using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    internal const string CommandLineSwitch = "--p8-stat-config-gate";
    internal const string BrowserFixtureSwitch = "--p8-browser-fixture";
    private const string ChainId = "p8_chain_20260802002233_677d";
    private const string EmptyConfigHash =
        "74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b";
    private const string LabelsCollection = "labels";
    private const string ReceiptsCollection = "stat_config_command_receipts";
    private const string DynamicFormsCollection = "dynamic_form_templates";

    private static readonly string[] CoreCaseIds = Enumerable.Range(1, 20)
        .Select(value => $"P8-CORE-{value:000}")
        .ToArray();
    private static readonly string[] LabelCaseIds = Enumerable.Range(1, 16)
        .Select(value => $"P8-LBL-{value:000}")
        .ToArray();
    private static readonly string[] P801CaseIds = CoreCaseIds
        .Concat(LabelCaseIds)
        .ToArray();
    private static readonly string[] FieldCaseIds = Enumerable.Range(1, 14)
        .Select(value => $"P8-FLD-{value:000}")
        .ToArray();
    private static readonly string[] TableCaseIds = Enumerable.Range(1, 18)
        .Select(value => $"P8-TBL-{value:000}")
        .ToArray();

    private static readonly string[] BasicCaseIds = Enumerable.Range(1, 16)
        .Select(value => $"P8-BAS-{value:000}")
        .ToArray();
    private static readonly string[] AdvancedCaseIds = Enumerable.Range(1, 18)
        .Select(value => $"P8-ADV-{value:000}")
        .ToArray();
    private static readonly string[] DiffCaseIds = Enumerable.Range(1, 16)
        .Select(value => $"P8-DIF-{value:000}")
        .ToArray();
    private static readonly string[] FlowCaseIds = Enumerable.Range(1, 12)
        .Select(value => $"P8-FLW-{value:000}")
        .ToArray();
    private static readonly string[] OperationsCaseIds = Enumerable.Range(1, 20)
        .Select(value => $"P8-OPS-{value:000}")
        .ToArray();
    private static readonly string[] BundleCaseIds = Enumerable.Range(1, 14)
        .Select(value => $"P8-BND-{value:000}")
        .ToArray();
    private static readonly string[] UiCaseIds = Enumerable.Range(1, 20)
        .Select(value => $"P8-UI-{value:000}")
        .ToArray();
    private static readonly string[] RaceCaseIds = Enumerable.Range(1, 16)
        .Select(value => $"P8-RACE-{value:000}")
        .ToArray();
    private static readonly string[] AllP8CaseIds = P801CaseIds
        .Concat(FieldCaseIds)
        .Concat(TableCaseIds)
        .Concat(BasicCaseIds)
        .Concat(AdvancedCaseIds)
        .Concat(DiffCaseIds)
        .Concat(FlowCaseIds)
        .Concat(OperationsCaseIds)
        .Concat(BundleCaseIds)
        .Concat(UiCaseIds)
        .Concat(RaceCaseIds)
        .OrderBy(caseId => caseId, StringComparer.Ordinal)
        .ToArray();

    private static readonly string[] ProhibitedCollections =
    [
        "work_report_field_stat_values",
        "work_report_field_stat_aggregates",
        "work_report_table_stat_values",
        "work_report_table_stat_aggregates",
        "work_report_label_stat_values",
        "work_report_label_stat_aggregates",
        "work_assignment_basic_summary_snapshots",
        "work_assignment_advanced_summary_day_nodes",
        "work_assignment_advanced_summary_month_nodes",
        "work_assignment_advanced_summary_year_nodes",
        "work_report_statistic_rebuild_jobs",
        "work_report_statistic_diff_results",
        "work_report_statistic_diff_exports",
        "work_report_statistic_exports",
        "stat_config_outbox",
        "dynamic_flow_instances",
        "dynamic_flow_step_instances",
        "dynamic_flow_gateway_instances",
        "dynamic_flow_gateway_contributions",
        "dynamic_flow_participant_snapshots",
        "dynamic_flow_execution_epochs",
        "dynamic_flow_runtime_command_receipts",
        "dynamic_flow_runtime_events",
        "dynamic_flow_mapping_apply_receipts",
        "dynamic_flow_mapping_provenance",
        "dynamic_flow_mapping_events",
        "dynamic_flow_periodic_schedules",
        "dynamic_flow_periodic_occurrences",
        "dynamic_flow_runtime_outbox",
        "dynamic_flow_mapping_outbox"
    ];

    private static readonly string[] ContractCollections =
    [
        LabelsCollection,
        DynamicFormsCollection,
        ReceiptsCollection,
        "user_action_logs",
        "work_assignment_aggregate_configs",
        "work_assignment_basic_summary_configs",
        "work_assignment_advanced_summary_configs",
        "work_report_statistic_diff_configs",
        "work_summary_token_ledgers",
        "dynamic_flow_templates",
        "dynamic_flow_template_versions",
        "dynamic_flow_definition_command_receipts",
        "stat_config_validation_jobs",
        "stat_config_audit_outbox",
        "notifications",
        .. ProhibitedCollections,
        .. P809ZeroWriteCollections
    ];

    private readonly int _through;
    private readonly string _promptId;
    private IReadOnlyList<string> ExpectedCaseIds => _through switch
    {
        1 => P801CaseIds,
        2 => FieldCaseIds,
        3 => TableCaseIds,
        4 => BasicCaseIds,
        5 => AdvancedCaseIds,
        6 => DiffCaseIds,
        7 => FlowCaseIds,
        8 => OperationsCaseIds,
        9 => BundleCaseIds,
        10 => UiCaseIds,
        11 => AllP8CaseIds,
        _ => []
    };
    private (string Group, string[] CaseIds)[] ExpectedGroups => _through switch
    {
        1 => [("P8-CORE", CoreCaseIds), ("P8-LBL", LabelCaseIds)],
        2 => [("P8-FLD", FieldCaseIds)],
        3 => [("P8-TBL", TableCaseIds)],
        4 => [("P8-BAS", BasicCaseIds)],
        5 => [("P8-ADV", AdvancedCaseIds)],
        6 => [("P8-DIF", DiffCaseIds)],
        7 => [("P8-FLW", FlowCaseIds)],
        8 => [("P8-OPS", OperationsCaseIds)],
        9 => [("P8-BND", BundleCaseIds)],
        10 => [("P8-UI", UiCaseIds)],
        11 =>
        [
            ("P8-CORE", CoreCaseIds),
            ("P8-LBL", LabelCaseIds),
            ("P8-FLD", FieldCaseIds),
            ("P8-TBL", TableCaseIds),
            ("P8-BAS", BasicCaseIds),
            ("P8-ADV", AdvancedCaseIds),
            ("P8-DIF", DiffCaseIds),
            ("P8-FLW", FlowCaseIds),
            ("P8-OPS", OperationsCaseIds),
            ("P8-BND", BundleCaseIds),
            ("P8-UI", UiCaseIds),
            ("P8-RACE", RaceCaseIds)
        ],
        _ => []
    };

    private P8StatConfigProbe(int through)
    {
        _through = through;
        _promptId = $"P8-{through:00}";
    }

    private readonly HarnessCaseRunner _cases = new();
    private readonly List<P8CaseEvidence> _caseEvidence = [];
    private readonly Dictionary<string, P8Actor> _actors =
        new(StringComparer.Ordinal);
    private readonly List<(string Kind, string Value)> _artifactSecrets = [];
    private HarnessPaths _paths = default!;
    private string _iterationRoot = default!;
    private MongoReplicaSetLease _mongo = default!;
    private BackendServerLease _backend = default!;
    private IMongoDatabase _database = default!;
    private ApiHarnessClient _api = default!;
    private string _adminId = default!;
    private string _rootUnitId = default!;
    private string _levelUnitId = default!;
    private string _unitAId = default!;
    private string _unitBId = default!;
    private string _bootstrapDefaultPassword = default!;
    private bool _p9SuccessorOverlayEnabled;

    public static async Task<int> RunAsync(string[] args)
    {
        var browserFixtureEnabled = args.Any(value => string.Equals(
            value,
            BrowserFixtureSwitch,
            StringComparison.OrdinalIgnoreCase));
        var through = browserFixtureEnabled && !args.Any(value => string.Equals(
            value,
            "--through",
            StringComparison.OrdinalIgnoreCase))
            ? 10
            : ParseThrough(args);
        if (!TryParseP9SuccessorOverlay(args, out var p9SuccessorOverlay,
                out var p9SuccessorOverlayError))
        {
            Console.Error.WriteLine(p9SuccessorOverlayError);
            return 2;
        }
        if (through is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11))
        {
            Console.Error.WriteLine(
                $"P8 stat-config gate currently supports only --through 1 through 11; requested {through}.");
            return 2;
        }
        if (browserFixtureEnabled && through != 10)
        {
            Console.Error.WriteLine(
                "--p8-browser-fixture is bound to the exact P8-10 seeded configuration surface; use --through 10 or omit --through.");
            return 2;
        }
        if (p9SuccessorOverlay && through != 11)
        {
            Console.Error.WriteLine(
                $"{P9SuccessorOverlaySwitch} {P9SuccessorOverlayId} is bound to the exact P8-11 sealed regression; use --through 11.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        if (through == 11)
            return await RunP811SealedAggregateAsync(args, cancellation.Token);

        return await new P8StatConfigProbe(through).ExecuteAsync(args, cancellation.Token);
    }

    private async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (!TryParseP9SuccessorOverlay(args,
                out _p9SuccessorOverlayEnabled,
                out var p9SuccessorOverlayError))
        {
            throw new InvalidOperationException(p9SuccessorOverlayError);
        }
        var browserFixture = P812BrowserFixtureOptions.Parse(args);
        var startedAtUtc = DateTime.UtcNow;
        var runKey = BuildRunKey();
        _paths = HarnessPaths.CreateP8(runKey, ChainId, _promptId);
        _iterationRoot = _paths.IterationRoot(1);
        MongoReplicaSetLease? mongo = null;
        BackendServerLease? backend = null;
        ApiHarnessClient? api = null;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
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
            var backendOptions = _through is 8 or 11
                ? BuildP808BackendOptions()
                : null;
            if (browserFixture.Enabled)
            {
                backendOptions = (backendOptions ?? new BackendServerOptions()) with
                {
                    FrontendOrigin = browserFixture.FrontendOrigin!
                        .GetLeftPart(UriPartial.Authority)
                };
            }
            backend = await BackendServerLease.StartAsync(
                _paths,
                _iterationRoot,
                runKey,
                mongo,
                ct,
                backendOptions);
            _backend = backend;
            RememberArtifactSecret("bootstrap-key", backend.BootstrapKey);
            RememberArtifactSecret("actor-password", backend.ActorPassword);
            api = new ApiHarnessClient(backend.BaseUri);
            _api = api;

            await EvidenceJson.WriteAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                new
                {
                    chainId = ChainId,
                    promptId = _promptId,
                    runKey,
                    commandLine = args,
                    startedAtUtc,
                    backendDll = _paths.ResolveBackendDll(),
                    kestrel = new
                    {
                        realProcess = true,
                        backend.ProcessId,
                        backend.Port,
                        baseUri = backend.BaseUri.ToString()
                    },
                    mongo = new
                    {
                        isolatedReplicaSet = true,
                        mongo.DatabaseName,
                        mongo.ReplicaSetName,
                        mongo.ProcessId,
                        mongo.Port,
                        directMongoOracle = true
                    },
                    disabledRuntime = new
                    {
                        redis = true,
                        hangfireServer = true,
                        hangfireDashboard = true,
                        hangfireRecurringRegistration = true
                    },
                    watchedCollections = ContractCollections,
                    prohibitedCollections = ProhibitedCollections
                },
                ct);

            await BootstrapAndSeedActorsAsync(ct);
            switch (_through)
            {
                case 1:
                    await RunCoreCasesAsync(ct);
                    await RunLabelCasesAsync(ct);
                    break;
                case 2:
                    await RunFieldCasesAsync(ct);
                    break;
                case 3:
                    await RunTableCasesAsync(ct);
                    break;
                case 4:
                    await RunBasicSummaryCasesAsync(ct);
                    break;
                case 5:
                    await RunAdvancedSummaryCasesAsync(ct);
                    break;
                case 6:
                    await RunDiffConfigurationCasesAsync(ct);
                    break;
                case 7:
                    await RunFlowContributionCasesAsync(ct);
                    break;
                case 8:
                    await RunOperationsReadinessCasesAsync(ct);
                    break;
                case 9:
                    await RunBundleAndPhaseBarrierCasesAsync(ct);
                    break;
                case 10:
                    await RunStatisticsConfigurationUiCasesAsync(args, ct);
                    if (browserFixture.Enabled)
                    {
                        await RunP812BrowserFixtureAsync(
                            browserFixture,
                            runKey,
                            ct);
                    }
                    break;
                case 11:
                    await RunCoreCasesAsync(ct);
                    await RunLabelCasesAsync(ct);
                    await RunFieldCasesAsync(ct);
                    await RunTableCasesAsync(ct);
                    await RunBasicSummaryCasesAsync(ct);
                    await RunAdvancedSummaryCasesAsync(ct);
                    await RunDiffConfigurationCasesAsync(ct);
                    await RunFlowContributionCasesAsync(ct);
                    await RunOperationsReadinessCasesAsync(ct);
                    await RunBundleAndPhaseBarrierCasesAsync(ct);
                    await RestoreP810OnlyUsersForP810Async(ct);
                    await RunStatisticsConfigurationUiCasesAsync(args, ct);
                    await RunP811RaceCasesAsync(ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            fatalFailure = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(ex);
        }
        finally
        {
            api?.Dispose();
            if (backend is not null)
            {
                try
                {
                    await backend.StopAsync();
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"backend-stop: {ex.Message}");
                }
                try
                {
                    await backend.DisposeAsync();
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"backend-dispose: {ex.Message}");
                }
            }

            if (mongo is not null)
            {
                try
                {
                    await mongo.DropDatabaseGuardedAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"mongo-drop: {ex.Message}");
                }
                try
                {
                    await mongo.StopProcessAsync();
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"mongo-stop: {ex.Message}");
                }
                try
                {
                    mongo.RemoveDataDirectoryGuarded();
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"mongo-data-remove: {ex.Message}");
                }
                try
                {
                    await mongo.DisposeAsync();
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"mongo-dispose: {ex.Message}");
                }
            }
        }

        await FillMissingCasesAsync(fatalFailure ?? "Gate stopped before case execution.");
        var results = _cases.Results.OrderBy(row => row.CaseId, StringComparer.Ordinal).ToArray();
        var expectedCaseIds = ExpectedCaseIds;
        var exactIds = results.Select(row => row.CaseId)
            .SequenceEqual(expectedCaseIds, StringComparer.Ordinal);
        var groupNormalizedSemanticSha256 = ExpectedGroups.ToDictionary(
            group => group.Group,
            group =>
            {
                var idSet = group.CaseIds.ToHashSet(StringComparer.Ordinal);
                return ComputeNormalizedSemanticSha256(results.Where(row => idSet.Contains(row.CaseId)));
            },
            StringComparer.Ordinal);
        var normalizedSemanticSha256 = ComputeNormalizedSemanticSha256(results);
        var semanticHashVerified = VerifyNormalizedSemanticSha256(normalizedSemanticSha256, results) &&
                                   ExpectedGroups.All(group =>
                                   {
                                       var idSet = group.CaseIds.ToHashSet(StringComparer.Ordinal);
                                       var groupResults = results.Where(row => idSet.Contains(row.CaseId));
                                       return VerifyNormalizedSemanticSha256(
                                           groupNormalizedSemanticSha256[group.Group],
                                           groupResults);
                                   });
        var p811ReplacementCleanupSucceeded = _through != 11 ||
                                              _p811BackendRestart is
                                                  { StopVerified: true, PortReleaseVerified: true };
        var cleanupSucceeded = cleanupErrors.Count == 0 &&
                               backend?.StopVerified == true &&
                               backend.PortReleaseVerified &&
                               mongo?.DatabaseDropVerified == true &&
                               mongo.ProcessStopVerified &&
                               mongo.PortReleaseVerified &&
                               mongo.DataDirectoryRemovalVerified &&
                               p811ReplacementCleanupSucceeded;
        var passed = fatalFailure is null &&
                     exactIds &&
                     results.Length == expectedCaseIds.Count &&
                     results.All(row => row.Verdict == HarnessVerdict.DAT) &&
                     cleanupSucceeded &&
                     semanticHashVerified;
        var failureReason = fatalFailure;
        if (failureReason is null && !exactIds)
            failureReason = $"Executed case IDs are not the exact {_promptId} owned set.";
        if (failureReason is null && results.Any(row => row.Verdict != HarnessVerdict.DAT))
            failureReason = $"One or more {_promptId} cases are not DAT.";
        if (failureReason is null && !cleanupSucceeded)
            failureReason = "Owned Kestrel/Mongo cleanup was not fully verified.";
        if (failureReason is null && !semanticHashVerified)
            failureReason = "Normalized semantic SHA-256 is not lowercase or failed exact recomputation.";

        var completedAtUtc = DateTime.UtcNow;
        var apiExchanges = api?.Exchanges.ToList() ?? [];
        if (_through == 11)
            apiExchanges.AddRange(_p811RestartApiExchanges);
        apiExchanges = apiExchanges
            .OrderBy(exchange => exchange.Sequence)
            .ToList();
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "api-exchanges.json"),
            new { chainId = ChainId, promptId = _promptId, runKey, apiExchanges });
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "collection-deltas.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                runKey,
                documentSetHashAlgorithm = "SHA-256(length-prefixed BSON documents sorted by _id)",
                cases = _caseEvidence.Select(item => new
                {
                    item.CaseId,
                    item.ExpectedChangedCollections,
                    item.RequiredChangedCollections,
                    item.Deltas
                }),
                prohibited = ProhibitedCollections.Select(collection => new
                {
                    collection,
                    changedCases = _caseEvidence
                        .Where(item => item.Deltas.Any(delta =>
                            delta.Changed && string.Equals(delta.Collection, collection, StringComparison.Ordinal)))
                        .Select(item => item.CaseId)
                        .ToArray()
                })
            });
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "owner-oracles.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                runKey,
                directMongo = true,
                cases = _caseEvidence.Select(item => new
                {
                    item.CaseId,
                    item.Actor,
                    item.CommandIds,
                    item.OwnerOracle
                })
            });
        foreach (var group in ExpectedGroups)
            await WriteGroupEvidenceAsync(group.Group, group.CaseIds, results);
        var securityScan = await RunArtifactSecurityScanAsync();
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "security-scan.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                runKey,
                securityScan
            });
        if (!securityScan.Passed)
        {
            passed = false;
            failureReason ??= "Artifact security scan found a raw credential or unreadable evidence file.";
        }
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                runKey,
                state = cleanupSucceeded ? "CLEANED" : "CLEANUP_FAILED",
                ownedRoots = new[] { _paths.RunRoot },
                backend = backend is null ? null : new
                {
                    backend.ProcessId,
                    backend.Port,
                    backend.StopVerified,
                    backend.PortReleaseVerified
                },
                replacementBackend = _through == 11
                    ? _p811BackendRestart
                    : null,
                mongo = mongo is null ? null : new
                {
                    mongo.DatabaseName,
                    mongo.ReplicaSetName,
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
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, $"{_promptId.ToLowerInvariant()}-gate-result.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                runKey,
                startedAtUtc,
                completedAtUtc,
                exactExpectedCaseCount = expectedCaseIds.Count,
                actualCaseCount = results.Length,
                exactIds,
                normalizedSemanticSha256,
                semanticHashVerified,
                groupNormalizedSemanticSha256,
                semanticNormalizationFields = new[] { "caseId", "verdict", "fingerprint" },
                results,
                directMongoEvidenceCases = _caseEvidence.Count,
                allMutationPathsUseRealKestrel = true,
                artifactSecurityScanPassed = securityScan.Passed,
                securityScanFindingCount = securityScan.Findings.Count,
                prohibitedCollectionDeltaZero = _caseEvidence.All(item =>
                    item.Deltas.Where(delta => ProhibitedCollections.Contains(delta.Collection, StringComparer.Ordinal))
                        .All(delta => !delta.Changed)),
                cleanupSucceeded,
                p811ReplacementCleanupSucceeded,
                fatalFailure,
                passed,
                failureReason
            });

        Console.WriteLine(
            passed
                ? $"[DAT] {_promptId} real integration gate passed {expectedCaseIds.Count}/{expectedCaseIds.Count}; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] {_promptId} gate failed: {failureReason}; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private async Task BootstrapAndSeedActorsAsync(CancellationToken ct)
    {
        var bootstrap = await _api.PostAsync(
            "api/system/bootstrap",
            new { },
            headers: new Dictionary<string, string>
            {
                ["X-System-Bootstrap-Key"] = _backend.BootstrapKey
            },
            ct: ct);
        ApiHarnessClient.ExpectStatus(bootstrap, HttpStatusCode.OK, "P8 system bootstrap");
        var password = ApiHarnessClient.RequiredString(bootstrap.Json, "defaultPassword");
        _bootstrapDefaultPassword = password;
        RememberArtifactSecret("bootstrap-default-password", password);
        var adminToken = await _api.LoginAsync("admin", password, ct);
        RememberArtifactSecret("admin-access-token", adminToken);
        var users = _database.GetCollection<AppUser>("users");
        var units = _database.GetCollection<Unit>("units");
        var admin = await users.Find(user => user.Username == "admin" && !user.IsDeleted).SingleAsync(ct);
        var root = await units.Find(unit => unit.Id == admin.UnitId && !unit.IsDeleted).SingleAsync(ct);
        _adminId = admin.Id;
        _rootUnitId = root.Id;
        _levelUnitId = ObjectId.GenerateNewId().ToString();
        _unitAId = ObjectId.GenerateNewId().ToString();
        _unitBId = ObjectId.GenerateNewId().ToString();
        var fixedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        var seededUnits = new[]
        {
            NewUnit(_levelUnitId, "P8 Level", "100801", root.Level + 1, root.Id, fixedAt),
            NewUnit(_unitAId, "P8 Unit A", "10080101", root.Level + 2, _levelUnitId, fixedAt),
            NewUnit(_unitBId, "P8 Unit B", "10080102", root.Level + 2, _levelUnitId, fixedAt)
        };
        await units.InsertManyAsync(seededUnits, cancellationToken: ct);

        var actorSeeds = new[]
        {
            NewActor("level_manager", "p8_level_manager", "P8 Level Manager", _levelUnitId,
                "LEVEL_MANAGER", ["MANAGER_LEVEL"], fixedAt),
            NewActor("unit_manager_a", "p8_unit_manager_a", "P8 Unit Manager A", _unitAId,
                "UNIT_MANAGER", [$"MANAGER_UNIT:{_unitAId}"], fixedAt),
            NewActor("unit_manager_b", "p8_unit_manager_b", "P8 Unit Manager B", _unitBId,
                "UNIT_MANAGER", [$"MANAGER_UNIT:{_unitBId}"], fixedAt),
            NewActor("ordinary_a", "p8_ordinary_a", "P8 Ordinary A", _unitAId,
                "NORMAL_USER", [], fixedAt),
            NewActor("outsider_b", "p8_outsider_b", "P8 Outsider B", _unitBId,
                "NORMAL_USER", [], fixedAt),
            NewActor("p810_owner", "p810_owner", "P8-10 Owner", _unitAId,
                "NORMAL_USER", [], fixedAt),
            NewActor("p810_issuer", "p810_issuer", "P8-10 Issuer", _unitAId,
                "UNIT_MANAGER", [$"MANAGER_UNIT:{_unitAId}"], fixedAt),
            NewActor("p810_reporter", "p810_reporter", "P8-10 Reporter", _unitAId,
                "NORMAL_USER", [], fixedAt),
            NewActor("p810_reviewer", "p810_reviewer", "P8-10 Reviewer", _unitAId,
                "NORMAL_USER", [], fixedAt),
            NewActor("p810_coordinator", "p810_coordinator", "P8-10 Coordinator", _unitAId,
                "NORMAL_USER", [], fixedAt),
            NewActor("p810_outsider", "p810_outsider", "P8-10 Outsider", _unitBId,
                "NORMAL_USER", [], fixedAt)
        };
        var hasher = new PasswordHasher<AppUser>();
        foreach (var seed in actorSeeds)
            seed.User.PasswordHash = hasher.HashPassword(seed.User, _backend.ActorPassword);
        await users.InsertManyAsync(actorSeeds.Select(seed => seed.User), cancellationToken: ct);

        _actors["system_admin"] = new P8Actor(
            "system_admin",
            admin.Id,
            admin.Username,
            admin.UnitId,
            admin.AccountKind ?? "SYSTEM_ADMIN",
            admin.Roles,
            adminToken);
        foreach (var seed in actorSeeds)
        {
            var token = await _api.LoginAsync(seed.User.Username, _backend.ActorPassword, ct);
            RememberArtifactSecret($"{seed.Key}-access-token", token);
            _actors[seed.Key] = new P8Actor(
                seed.Key,
                seed.User.Id,
                seed.User.Username,
                seed.User.UnitId,
                seed.User.AccountKind ?? string.Empty,
                seed.User.Roles,
                token);
        }

        _enumCatalogId = ObjectId.GenerateNewId().ToString();
        await _database.GetCollection<LabelEnumCatalog>("label_enum_catalogs")
            .InsertOneAsync(
                new LabelEnumCatalog
                {
                    Id = _enumCatalogId,
                    Code = "p8_dependency_catalog",
                    Name = "P8 Dependency Catalog",
                    NameLower = "p8 dependency catalog",
                    Description = "P8-01 immutable dependency pin fixture",
                    ScopeType = "GLOBAL",
                    ScopeId = null,
                    CreatedByUsername = admin.Username,
                    CreatedByAccountKind = admin.AccountKind,
                    OptionsRevision = 1,
                    Options =
                    [
                        new LabelEnumOption
                        {
                            Code = "alpha",
                            Label = "Alpha",
                            Order = 1,
                            IsActive = true
                        },
                        new LabelEnumOption
                        {
                            Code = "beta",
                            Label = "Beta",
                            Order = 2,
                            IsActive = true
                        }
                    ],
                    IsActive = true,
                    CreatedByUserId = admin.Id,
                    UpdatedByUserId = admin.Id,
                    CreatedAtUtc = fixedAt,
                    UpdatedAtUtc = fixedAt,
                    IsDeleted = false
                },
                cancellationToken: ct);
        await _database.GetCollection<BsonDocument>("dynamic_form_templates")
            .InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = ObjectId.GenerateNewId(),
                    ["code"] = "P8_REFERENCE_FIXTURE",
                    ["tagCodes"] = new BsonArray { "p8.lbl.referenced" },
                    ["sectionsJson"] = "[]",
                    ["fieldsJson"] = "[]",
                    ["excelBlockJson"] = BsonNull.Value,
                    ["blocksJson"] = "[]",
                    ["isDeleted"] = false
                },
                cancellationToken: ct);

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "actor-matrix.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                actors = _actors.Values.Select(actor => new
                {
                    actor.Key,
                    actor.Id,
                    actor.Username,
                    actor.UnitId,
                    actor.AccountKind,
                    actor.Roles,
                    authenticated = !string.IsNullOrWhiteSpace(actor.Token)
                }),
                units = new
                {
                    root = _rootUnitId,
                    level = _levelUnitId,
                    unitA = _unitAId,
                    unitB = _unitBId
                }
            },
            ct);
    }

    private async Task RunEvidenceCaseAsync(
        string caseId,
        string actorKey,
        IReadOnlyCollection<string> commandIds,
        IReadOnlyCollection<string> expectedChangedCollections,
        IReadOnlyCollection<string> requiredChangedCollections,
        Func<Task<CaseObservation>> action,
        CancellationToken ct)
    {
        await _cases.RunAsync(caseId, async () =>
        {
            var before = await CaptureDatabaseSnapshotAsync(ct);
            var exchangeStart = _api.Exchanges.Count;
            try
            {
                var observation = await action();
                var after = await CaptureDatabaseSnapshotAsync(ct);
                var deltas = BuildDeltas(before, after);
                VerifyCollectionContract(
                    caseId,
                    deltas,
                    expectedChangedCollections,
                    requiredChangedCollections);
                var oracle = await CaptureOwnerOracleAsync(commandIds, ct);
                var exchanges = _api.Exchanges.Skip(exchangeStart)
                    .Where(exchange => string.Equals(exchange.CaseId, caseId, StringComparison.Ordinal))
                    .Select(exchange => exchange.Sequence +
                        (_through == 11 && ReferenceEquals(_api, _p811RestartApi)
                            ? _apiExchangeSequenceOffset
                            : 0))
                    .ToArray();
                _caseEvidence.Add(new P8CaseEvidence(
                    caseId,
                    actorKey,
                    commandIds.ToArray(),
                    expectedChangedCollections.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    requiredChangedCollections.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    before.Values.OrderBy(x => x.Collection, StringComparer.Ordinal).ToArray(),
                    after.Values.OrderBy(x => x.Collection, StringComparer.Ordinal).ToArray(),
                    deltas,
                    exchanges,
                    oracle,
                    HarnessVerdict.DAT,
                    null));
                return observation;
            }
            catch (Exception ex)
            {
                IReadOnlyDictionary<string, P8CollectionState> after;
                try
                {
                    after = await CaptureDatabaseSnapshotAsync(CancellationToken.None);
                }
                catch
                {
                    after = new Dictionary<string, P8CollectionState>(StringComparer.Ordinal);
                }
                var deltas = BuildDeltas(before, after);
                P8OwnerOracle? oracle = null;
                try
                {
                    oracle = await CaptureOwnerOracleAsync(commandIds, CancellationToken.None);
                }
                catch
                {
                    // The original assertion remains the case failure.
                }
                var verdict = ex is HarnessCaseNotRunnableException
                    ? HarnessVerdict.CHUA_CHAY
                    : HarnessVerdict.KHONG_DAT;
                _caseEvidence.Add(new P8CaseEvidence(
                    caseId,
                    actorKey,
                    commandIds.ToArray(),
                    expectedChangedCollections.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    requiredChangedCollections.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    before.Values.OrderBy(x => x.Collection, StringComparer.Ordinal).ToArray(),
                    after.Values.OrderBy(x => x.Collection, StringComparer.Ordinal).ToArray(),
                    deltas,
                    _api.Exchanges.Skip(exchangeStart)
                        .Where(exchange => string.Equals(exchange.CaseId, caseId, StringComparison.Ordinal))
                        .Select(exchange => exchange.Sequence +
                            (_through == 11 && ReferenceEquals(_api, _p811RestartApi)
                                ? _apiExchangeSequenceOffset
                                : 0))
                        .ToArray(),
                    oracle,
                    verdict,
                    $"{ex.GetType().Name}: {ex.Message}"));
                throw;
            }
        });
    }

    private async Task<IReadOnlyDictionary<string, P8CollectionState>> CaptureDatabaseSnapshotAsync(
        CancellationToken ct)
    {
        var existing = (await (await _database.ListCollectionNamesAsync(cancellationToken: ct))
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var names = existing.Concat(ContractCollections)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var snapshot = new Dictionary<string, P8CollectionState>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!existing.Contains(name))
            {
                snapshot[name] = new P8CollectionState(
                    name,
                    false,
                    0,
                    Sha256(Array.Empty<byte>()));
                continue;
            }

            var documents = await _database.GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var document in documents)
            {
                var bytes = document.ToBson();
                hash.AppendData(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length)));
                hash.AppendData(bytes);
            }
            snapshot[name] = new P8CollectionState(
                name,
                true,
                documents.Count,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        return snapshot;
    }

    private static IReadOnlyList<P8CollectionDelta> BuildDeltas(
        IReadOnlyDictionary<string, P8CollectionState> before,
        IReadOnlyDictionary<string, P8CollectionState> after)
    {
        var missing = new P8CollectionState("", false, 0, Sha256(Array.Empty<byte>()));
        return before.Keys.Concat(after.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                var left = before.TryGetValue(name, out var beforeState)
                    ? beforeState
                    : missing with { Collection = name };
                var right = after.TryGetValue(name, out var afterState)
                    ? afterState
                    : missing with { Collection = name };
                return new P8CollectionDelta(
                    name,
                    left.Exists,
                    right.Exists,
                    left.Count,
                    right.Count,
                    right.Count - left.Count,
                    left.DocumentSetSha256,
                    right.DocumentSetSha256,
                    left.Exists != right.Exists ||
                    left.Count != right.Count ||
                    !string.Equals(left.DocumentSetSha256, right.DocumentSetSha256, StringComparison.Ordinal));
            })
            .ToArray();
    }

    private static void VerifyCollectionContract(
        string caseId,
        IReadOnlyList<P8CollectionDelta> deltas,
        IReadOnlyCollection<string> allowed,
        IReadOnlyCollection<string> required)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var unexpected = deltas.Where(delta => delta.Changed && !allowedSet.Contains(delta.Collection))
            .Select(delta => delta.Collection)
            .ToArray();
        HarnessAssert.True(
            unexpected.Length == 0,
            $"{caseId} changed unexpected collections: {string.Join(", ", unexpected)}");
        foreach (var name in required)
        {
            HarnessAssert.True(
                deltas.Any(delta => delta.Changed && string.Equals(delta.Collection, name, StringComparison.Ordinal)),
                $"{caseId} did not change required collection {name}");
        }
        foreach (var name in ProhibitedCollections)
        {
            HarnessAssert.True(
                deltas.All(delta => !string.Equals(delta.Collection, name, StringComparison.Ordinal) || !delta.Changed),
                $"{caseId} changed prohibited P8 result/job/outbox collection {name}");
        }
    }

    private async Task<P8OwnerOracle> CaptureOwnerOracleAsync(
        IReadOnlyCollection<string> commandIds,
        CancellationToken ct)
    {
        var labels = await _database.GetCollection<BsonDocument>(LabelsCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var forms = await _database.GetCollection<BsonDocument>(DynamicFormsCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var basicConfigs = await _database
            .GetCollection<BsonDocument>(BasicConfigsCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var advancedConfigs = await _database
            .GetCollection<BsonDocument>(AdvancedConfigsCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var diffConfigs = await _database
            .GetCollection<BsonDocument>(DiffConfigsCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var tokenLedgers = await _database
            .GetCollection<BsonDocument>(TokenLedgersCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var receiptFilter = commandIds.Count == 0
            ? Builders<BsonDocument>.Filter.In("commandId", Array.Empty<string>())
            : Builders<BsonDocument>.Filter.In("commandId", commandIds);
        var receipts = await _database.GetCollection<BsonDocument>(ReceiptsCollection)
            .Find(receiptFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var flowOwner = await CaptureP807FlowOwnerOracleAsync(
            commandIds,
            ct);
        return new P8OwnerOracle(
            labels.Select(document => new P8LabelOracle(
                BsonString(document, "_id"),
                BsonString(document, "code"),
                BsonString(document, "scopeType"),
                BsonString(document, "scopeId"),
                BsonString(document, "usage"),
                BsonString(document, "dataType"),
                BsonBool(document, "isActive"),
                BsonBool(document, "isDeleted"),
                BsonString(document, "configId"),
                BsonString(document, "versionId"),
                BsonInt(document, "versionNo"),
                BsonLong(document, "revision"),
                BsonString(document, "configHash"),
                document.TryGetValue("versionSnapshots", out var versions) && versions.IsBsonArray
                    ? versions.AsBsonArray.Count
                    : 0,
                Sha256(document.ToBson())))
                .ToArray(),
            forms.Select(document =>
            {
                var sections = document.TryGetValue("statisticConfigSections", out var sectionValue) &&
                               sectionValue.IsBsonDocument
                    ? sectionValue.AsBsonDocument
                    : new BsonDocument();
                var fieldSectionJson = BsonString(sections, "fieldSectionJson") ?? string.Empty;
                var tableSectionJson = BsonString(sections, "tableSectionJson") ?? string.Empty;
                return new P8DynamicFormOracle(
                    BsonString(document, "_id"),
                    BsonString(document, "code"),
                    BsonInt(document, "versionNo"),
                    BsonInt(document, "revision"),
                    BsonBool(document, "isPublished"),
                    BsonString(document, "familyId"),
                    BsonString(document, "previousVersionId"),
                    BsonString(document, "clonedFromVersionId"),
                    BsonString(document, "lineageStatus"),
                    BsonString(document, "publishedSchemaHash"),
                    Sha256(Encoding.UTF8.GetBytes(BsonString(document, "publishedSchemaSnapshotJson") ?? string.Empty)),
                    Sha256(Encoding.UTF8.GetBytes(BsonString(document, "sectionsJson") ?? string.Empty)),
                    Sha256(Encoding.UTF8.GetBytes(BsonString(document, "fieldsJson") ?? string.Empty)),
                    Sha256(Encoding.UTF8.GetBytes(BsonString(document, "blocksJson") ?? string.Empty)),
                    BsonString(document, "statisticConfigId"),
                    BsonString(document, "statisticConfigVersionId"),
                    BsonInt(document, "statisticConfigVersionNo"),
                    BsonLong(document, "statisticConfigRevision"),
                    BsonString(document, "statisticConfigHash"),
                    Sha256(Encoding.UTF8.GetBytes(fieldSectionJson)),
                    Sha256(Encoding.UTF8.GetBytes(tableSectionJson)),
                    document.TryGetValue("statisticConfigSnapshots", out var snapshots) && snapshots.IsBsonArray
                        ? snapshots.AsBsonArray.Count
                        : 0,
                    Sha256(document.ToBson()));
            }).ToArray(),
            basicConfigs.Select(document =>
            {
                var configJson = BsonString(document, "configJson") ?? string.Empty;
                var defaultMethodsJson = BsonString(document, "defaultMethodsJson") ?? string.Empty;
                var rulesJson = BsonString(document, "rulesJson") ?? string.Empty;
                var pins = document.TryGetValue("dependencyPins", out var pinValue) &&
                           pinValue.IsBsonArray
                    ? pinValue.AsBsonArray.Select(value => value.AsString).ToArray()
                    : Array.Empty<string>();
                var versions = document.GetValue("versions", new BsonArray());
                return new P8BasicSummaryOracle(
                    BsonString(document, "_id"),
                    BsonString(document, "workId"),
                    BsonString(document, "assignmentId"),
                    BsonString(document, "dynamicFormTemplateId"),
                    BsonString(document, "versionId"),
                    BsonString(document, "previousVersionId"),
                    BsonInt(document, "versionNo"),
                    BsonLong(document, "revision"),
                    BsonString(document, "status"),
                    BsonString(document, "configHash"),
                    Sha256(Encoding.UTF8.GetBytes(configJson)),
                    pins,
                    versions.IsBsonArray ? versions.AsBsonArray.Count : 0,
                    Sha256(new BsonDocument(
                        "versions",
                        versions).ToBson()),
                    Sha256(Encoding.UTF8.GetBytes(defaultMethodsJson)),
                    Sha256(Encoding.UTF8.GetBytes(rulesJson)),
                    BsonBool(document, "isActive"),
                    BsonBool(document, "isDeleted"),
                    Sha256(document.ToBson()));
            }).ToArray(),
            advancedConfigs.Select(document => new P8AdvancedSummaryOracle(
                BsonString(document, "_id"),
                BsonString(document, "workId"),
                BsonString(document, "assignmentId"),
                BsonString(document, "dynamicFormTemplateId"),
                BsonString(document, "sectionId"),
                BsonString(document, "status"),
                BsonInt(document, "versionNo"),
                BsonInt(document, "draftRevision"),
                BsonLong(document, "revision"),
                BsonString(document, "configHash"),
                Sha256(Encoding.UTF8.GetBytes(BsonString(document, "configJson") ?? string.Empty)),
                BsonString(document, "configId"),
                BsonString(document, "previousVersionId"),
                Sha256(Encoding.UTF8.GetBytes(
                    BsonString(document, "validationReceiptJson") ?? string.Empty)),
                BsonString(document, "validationReceiptHash"),
                BsonString(document, "lockedByUserId"),
                BsonString(document, "lockTokenId"),
                BsonString(document, "archivedByUserId"),
                BsonBool(document, "isActive"),
                BsonBool(document, "isDeleted"),
                Sha256(document.ToBson())))
                .ToArray(),
            diffConfigs.Select(document =>
            {
                var configJson = BsonString(document, "configJson")
                                 ?? string.Empty;
                var pins = document.TryGetValue(
                               "dependencyPins",
                               out var pinValue) &&
                           pinValue.IsBsonArray
                    ? pinValue.AsBsonArray
                        .Select(value => value.AsString)
                        .ToArray()
                    : Array.Empty<string>();
                return new P8DiffSummaryOracle(
                    BsonString(document, "_id"),
                    BsonString(document, "configId"),
                    BsonString(document, "versionId"),
                    BsonString(document, "workId"),
                    BsonString(document, "assignmentId"),
                    BsonString(document, "dynamicFormTemplateId"),
                    BsonString(document, "previousVersionId"),
                    BsonInt(document, "versionNo"),
                    BsonLong(document, "revision"),
                    BsonString(document, "status"),
                    BsonString(document, "configHash"),
                    Sha256(Encoding.UTF8.GetBytes(configJson)),
                    pins,
                    BsonString(document, "lockedByUserId"),
                    BsonBool(document, "isActive"),
                    BsonBool(document, "isDeleted"),
                    Sha256(document.ToBson()));
            }).ToArray(),
            tokenLedgers.Select(document => new P8TokenLedgerOracle(
                BsonString(document, "_id"),
                BsonString(document, "ownerUserId"),
                BsonString(document, "ownerUnitId"),
                BsonString(document, "actorUserId"),
                BsonString(document, "issuerUserId"),
                BsonString(document, "tokenKind"),
                BsonString(document, "direction"),
                BsonInt(document, "units"),
                BsonInt(document, "monthlyQuota"),
                BsonString(document, "periodMonthKey"),
                BsonString(document, "requestTokenId"),
                BsonString(document, "workId"),
                BsonString(document, "workAssignmentId"),
                BsonString(document, "dynamicFormTemplateId"),
                BsonString(document, "sectionId"),
                BsonString(document, "configId"),
                BsonInt(document, "configVersionNo"),
                BsonString(document, "configHash"),
                BsonString(document, "reason"),
                BsonString(document, "outcome"),
                BsonString(document, "error"),
                Sha256(document.ToBson())))
                .ToArray(),
            receipts.Select(document => new P8ReceiptOracle(
                BsonString(document, "_id"),
                BsonString(document, "ownerKind"),
                BsonString(document, "ownerId"),
                BsonString(document, "commandKind"),
                BsonString(document, "commandId"),
                BsonString(document, "requestHash"),
                BsonString(document, "responseHash"),
                BsonString(document, "resultConfigId"),
                BsonString(document, "resultVersionId"),
                BsonInt(document, "resultVersionNo"),
                BsonLong(document, "resultRevision"),
                BsonString(document, "resultStatus"),
                BsonString(document, "resultConfigHash"),
                BsonString(document, "actorUserId"),
                Sha256(document.ToBson())))
                .ToArray(),
            flowOwner.Families,
            flowOwner.Versions,
            flowOwner.DefinitionReceipts);
    }

    private async Task WriteGroupEvidenceAsync(
        string group,
        IReadOnlyCollection<string> expectedIds,
        IReadOnlyCollection<HarnessCaseResult> results)
    {
        var idSet = expectedIds.ToHashSet(StringComparer.Ordinal);
        var groupResults = results.Where(result => idSet.Contains(result.CaseId))
            .OrderBy(result => result.CaseId, StringComparer.Ordinal)
            .ToArray();
        var normalizedSemanticSha256 = ComputeNormalizedSemanticSha256(groupResults);
        var semanticHashVerified = VerifyNormalizedSemanticSha256(normalizedSemanticSha256, groupResults);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, $"{group}.evidence.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                group,
                expectedCaseCount = expectedIds.Count,
                actualCaseCount = groupResults.Length,
                exactIds = groupResults.Select(result => result.CaseId)
                    .SequenceEqual(expectedIds, StringComparer.Ordinal),
                allDat = groupResults.All(result => result.Verdict == HarnessVerdict.DAT),
                normalizedSemanticSha256,
                semanticHashVerified,
                semanticNormalizationFields = new[] { "caseId", "verdict", "fingerprint" },
                cases = groupResults,
                evidence = _caseEvidence.Where(item => idSet.Contains(item.CaseId))
                    .OrderBy(item => item.CaseId, StringComparer.Ordinal)
            });
    }

    private static string ComputeNormalizedSemanticSha256(IEnumerable<HarnessCaseResult> results)
    {
        var normalized = new JsonArray(
            results
                .OrderBy(result => result.CaseId, StringComparer.Ordinal)
                .Select(result => (JsonNode)new JsonObject
                {
                    ["caseId"] = result.CaseId,
                    ["verdict"] = result.Verdict.ToString(),
                    ["fingerprint"] = result.Fingerprint
                })
                .ToArray());
        var canonicalJson = Canonicalize(normalized);
        return Sha256(Encoding.UTF8.GetBytes(canonicalJson));
    }

    private static bool VerifyNormalizedSemanticSha256(
        string normalizedSemanticSha256,
        IEnumerable<HarnessCaseResult> results)
    {
        var isLowercaseSha256 = normalizedSemanticSha256.Length == 64 &&
                                normalizedSemanticSha256.All(character =>
                                    character is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (!isLowercaseSha256)
            return false;

        var recomputed = ComputeNormalizedSemanticSha256(results);
        return string.Equals(normalizedSemanticSha256, recomputed, StringComparison.Ordinal);
    }

    private async Task FillMissingCasesAsync(string reason)
    {
        var executed = _cases.Results.Select(result => result.CaseId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var caseId in ExpectedCaseIds.Where(id => !executed.Contains(id)))
        {
            await _cases.RunAsync(
                caseId,
                () => Task.FromException<CaseObservation>(
                    new HarnessCaseNotRunnableException(reason)));
        }
    }

    private P8Actor Actor(string key)
        => _actors.TryGetValue(key, out var actor)
            ? actor
            : throw new HarnessCaseNotRunnableException($"Actor {key} is not seeded.");

    private AppUserSeed NewActor(
        string key,
        string username,
        string fullName,
        string unitId,
        string accountKind,
        IReadOnlyCollection<string> roles,
        DateTime fixedAt)
    {
        var user = new AppUser
        {
            Id = ObjectId.GenerateNewId().ToString(),
            Username = username,
            FullName = fullName,
            UnitId = unitId,
            AccountKind = accountKind,
            Roles = roles.ToList(),
            CreatedByUserId = _adminId,
            UpdatedByUserId = _adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        return new AppUserSeed(key, user);
    }

    private void RememberArtifactSecret(string kind, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length >= 8)
            _artifactSecrets.Add((kind, value));
    }

    private async Task<P8SecurityScanResult> RunArtifactSecurityScanAsync(
        bool deferLockedProcessLogs = false)
    {
        var findings = new List<P8SecurityScanFinding>();
        var deferredReadErrors = new List<P8SecurityScanFinding>();
        var files = Directory.Exists(_paths.RunRoot)
            ? Directory.EnumerateFiles(_paths.RunRoot, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        var secrets = _artifactSecrets
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .DistinctBy(item => item.Value, StringComparer.Ordinal)
            .ToArray();
        var credentialedMongo = new Regex(
            @"mongodb(?:\+srv)?://[^\s:/]+:[^\s@/]+@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var bearerJwt = new Regex(
            @"Bearer\s+eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var ownedIterationPrefix = Path.GetFullPath(_iterationRoot)
            .TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            if (deferLockedProcessLogs &&
                Path.GetFullPath(file).StartsWith(
                    ownedIterationPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                deferredReadErrors.Add(new P8SecurityScanFinding(
                    Path.GetRelativePath(_paths.RunRoot, file),
                    "LIVE_PROCESS_FILE_DEFERRED",
                    "POST_CLEANUP_SCAN_REQUIRED"));
                continue;
            }

            string text;
            try
            {
                await using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                text = await reader.ReadToEndAsync();
            }
            catch (IOException ex) when (
                deferLockedProcessLogs &&
                Path.GetFullPath(file).StartsWith(
                    ownedIterationPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    Path.GetExtension(file),
                    ".log",
                    StringComparison.OrdinalIgnoreCase))
            {
                deferredReadErrors.Add(new P8SecurityScanFinding(
                    Path.GetRelativePath(_paths.RunRoot, file),
                    "LIVE_PROCESS_LOG_READ_DEFERRED",
                    ex.GetType().Name));
                continue;
            }
            catch (Exception ex)
            {
                findings.Add(new P8SecurityScanFinding(
                    Path.GetRelativePath(_paths.RunRoot, file),
                    "READ_ERROR",
                    ex.GetType().Name));
                continue;
            }

            var relative = Path.GetRelativePath(_paths.RunRoot, file);
            foreach (var secret in secrets)
            {
                if (text.Contains(secret.Value, StringComparison.Ordinal))
                    findings.Add(new P8SecurityScanFinding(relative, secret.Kind, "EXACT_SECRET_MATCH"));
            }
            if (credentialedMongo.IsMatch(text))
                findings.Add(new P8SecurityScanFinding(relative, "MONGO_CREDENTIAL", "CREDENTIALED_URI"));
            if (bearerJwt.IsMatch(text))
                findings.Add(new P8SecurityScanFinding(relative, "BEARER_TOKEN", "JWT_PATTERN"));
        }
        return new P8SecurityScanResult(
            findings.Count == 0,
            files.Length - deferredReadErrors.Count,
            secrets.Length,
            findings,
            deferredReadErrors);
    }

    private Unit NewUnit(
        string id,
        string name,
        string code,
        int level,
        string parentId,
        DateTime fixedAt)
        => new()
        {
            Id = id,
            FullName = name,
            ShortName = name,
            Symbol = code,
            Code = code,
            Level = level,
            Version = 1,
            UnitTypeCodes = [],
            ParentUnitId = parentId,
            CreatedByUserId = _adminId,
            UpdatedByUserId = _adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };

    private static int ParseThrough(string[] args)
    {
        var through = 1;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--through", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length &&
                int.TryParse(args[index + 1], out var parsed))
            {
                through = parsed;
            }
        }
        return through;
    }

    private string BuildRunKey()
        => $"p8{_through:00}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string? BsonString(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var value) || value.IsBsonNull)
            return null;
        return value.IsObjectId ? value.AsObjectId.ToString() : value.ToString();
    }

    private static bool? BsonBool(BsonDocument document, string name)
        => document.TryGetValue(name, out var value) && value.IsBoolean
            ? value.AsBoolean
            : null;

    private static int? BsonInt(BsonDocument document, string name)
        => document.TryGetValue(name, out var value) && value.IsNumeric
            ? value.ToInt32()
            : null;

    private static long? BsonLong(BsonDocument document, string name)
        => document.TryGetValue(name, out var value) && value.IsNumeric
            ? value.ToInt64()
            : null;
}

internal sealed record AppUserSeed(string Key, AppUser User);

internal sealed record P8Actor(
    string Key,
    string Id,
    string Username,
    string? UnitId,
    string AccountKind,
    IReadOnlyList<string> Roles,
    string Token);

internal sealed record P8CollectionState(
    string Collection,
    bool Exists,
    long Count,
    string DocumentSetSha256);

internal sealed record P8CollectionDelta(
    string Collection,
    bool ExistedBefore,
    bool ExistsAfter,
    long BeforeCount,
    long AfterCount,
    long CountDelta,
    string BeforeDocumentSetSha256,
    string AfterDocumentSetSha256,
    bool Changed);

internal sealed record P8LabelOracle(
    string? Id,
    string? Code,
    string? ScopeType,
    string? ScopeId,
    string? Usage,
    string? DataType,
    bool? IsActive,
    bool? IsDeleted,
    string? ConfigId,
    string? VersionId,
    int? VersionNo,
    long? Revision,
    string? ConfigHash,
    int VersionSnapshotCount,
    string DocumentSha256);

internal sealed record P8DynamicFormOracle(
    string? Id,
    string? Code,
    int? TemplateVersionNo,
    int? TemplateRevision,
    bool? IsPublished,
    string? FamilyId,
    string? PreviousVersionId,
    string? ClonedFromVersionId,
    string? LineageStatus,
    string? PublishedSchemaHash,
    string PublishedSchemaSnapshotSha256,
    string SectionsJsonSha256,
    string FieldsJsonSha256,
    string BlocksJsonSha256,
    string? ConfigId,
    string? VersionId,
    int? ConfigVersionNo,
    long? ConfigRevision,
    string? ConfigHash,
    string FieldSectionJsonSha256,
    string TableSectionJsonSha256,
    int ConfigSnapshotCount,
    string DocumentSha256);

internal sealed record P8BasicSummaryOracle(
    string? Id,
    string? WorkId,
    string? AssignmentId,
    string? DynamicFormTemplateId,
    string? VersionId,
    string? PreviousVersionId,
    int? VersionNo,
    long? Revision,
    string? Status,
    string? ConfigHash,
    string ConfigJsonSha256,
    IReadOnlyList<string> DependencyPins,
    int VersionSnapshotCount,
    string VersionsSha256,
    string DefaultMethodsJsonSha256,
    string RulesJsonSha256,
    bool? IsActive,
    bool? IsDeleted,
    string DocumentSha256);

internal sealed record P8ReceiptOracle(
    string? Id,
    string? OwnerKind,
    string? OwnerId,
    string? CommandKind,
    string? CommandId,
    string? RequestHash,
    string? ResponseHash,
    string? ResultConfigId,
    string? ResultVersionId,
    int? ResultVersionNo,
    long? ResultRevision,
    string? ResultStatus,
    string? ResultConfigHash,
    string? ActorUserId,
    string DocumentSha256);

internal sealed record P8AdvancedSummaryOracle(
    string? Id,
    string? WorkId,
    string? AssignmentId,
    string? DynamicFormTemplateId,
    string? SectionId,
    string? Status,
    int? VersionNo,
    int? DraftRevision,
    long? Revision,
    string? ConfigHash,
    string ConfigJsonSha256,
    string? ConfigId,
    string? PreviousVersionId,
    string ValidationReceiptJsonSha256,
    string? ValidationReceiptHash,
    string? LockedByUserId,
    string? LockTokenId,
    string? ArchivedByUserId,
    bool? IsActive,
    bool? IsDeleted,
    string DocumentSha256);

internal sealed record P8DiffSummaryOracle(
    string? Id,
    string? ConfigId,
    string? VersionId,
    string? WorkId,
    string? AssignmentId,
    string? DynamicFormTemplateId,
    string? PreviousVersionId,
    int? VersionNo,
    long? Revision,
    string? Status,
    string? ConfigHash,
    string ConfigJsonSha256,
    IReadOnlyList<string> DependencyPins,
    string? LockedByUserId,
    bool? IsActive,
    bool? IsDeleted,
    string DocumentSha256);

internal sealed record P8TokenLedgerOracle(
    string? Id,
    string? OwnerUserId,
    string? OwnerUnitId,
    string? ActorUserId,
    string? IssuerUserId,
    string? TokenKind,
    string? Direction,
    int? Units,
    int? MonthlyQuota,
    string? PeriodMonthKey,
    string? RequestTokenId,
    string? WorkId,
    string? WorkAssignmentId,
    string? DynamicFormTemplateId,
    string? SectionId,
    string? ConfigId,
    int? ConfigVersionNo,
    string? ConfigHash,
    string? Reason,
    string? Outcome,
    string? Error,
    string DocumentSha256);

internal sealed record P8OwnerOracle(
    IReadOnlyList<P8LabelOracle> Labels,
    IReadOnlyList<P8DynamicFormOracle> DynamicForms,
    IReadOnlyList<P8BasicSummaryOracle> BasicSummaryConfigs,
    IReadOnlyList<P8AdvancedSummaryOracle> AdvancedSummaryConfigs,
    IReadOnlyList<P8DiffSummaryOracle> DiffSummaryConfigs,
    IReadOnlyList<P8TokenLedgerOracle> TokenLedgers,
    IReadOnlyList<P8ReceiptOracle> Receipts,
    IReadOnlyList<P8FlowFamilyOracle> FlowFamilies,
    IReadOnlyList<P8FlowVersionOracle> FlowVersions,
    IReadOnlyList<P8FlowDefinitionReceiptOracle> FlowDefinitionReceipts);

internal sealed record P8SecurityScanFinding(
    string RelativePath,
    string Kind,
    string Detection);

internal sealed record P8SecurityScanResult(
    bool Passed,
    int ScannedFileCount,
    int ExactSecretValueCount,
    IReadOnlyList<P8SecurityScanFinding> Findings,
    IReadOnlyList<P8SecurityScanFinding> DeferredReadErrors);

internal sealed record P8CaseEvidence(
    string CaseId,
    string Actor,
    IReadOnlyList<string> CommandIds,
    IReadOnlyList<string> ExpectedChangedCollections,
    IReadOnlyList<string> RequiredChangedCollections,
    IReadOnlyList<P8CollectionState> Before,
    IReadOnlyList<P8CollectionState> After,
    IReadOnlyList<P8CollectionDelta> Deltas,
    IReadOnlyList<int> ApiExchangeSequences,
    P8OwnerOracle? OwnerOracle,
    HarnessVerdict Verdict,
    string? Failure);

internal sealed record P8ConfigIdentity(
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    IReadOnlyList<string> DependencyPins,
    string LabelId,
    string LabelCode,
    string LabelScopeType,
    string? LabelScopeId,
    string LabelUsage,
    string LabelDataType,
    bool LabelIsActive,
    string? ReceiptId,
    JsonArray Versions,
    JsonObject Raw);
