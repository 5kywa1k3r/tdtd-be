using System.Net;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static string[] P809SixResultCollections =>
    [
        "work_report_field_stat_values",
        "work_report_field_stat_aggregates",
        "work_report_table_stat_values",
        "work_report_table_stat_aggregates",
        "work_report_label_stat_values",
        "work_report_label_stat_aggregates"
    ];

    private static string[] P809BasicSnapshotCollections =>
    [
        "work_assignment_basic_summary_snapshots"
    ];

    private static string[] P809AdvancedAndDiffOutputCollections =>
    [
        "work_assignment_advanced_summary_day_nodes",
        "work_assignment_advanced_summary_month_nodes",
        "work_assignment_advanced_summary_year_nodes",
        "work_report_statistic_diff_results",
        "work_report_statistic_diff_exports"
    ];

    private static string[] P809ReportProjectionAggregateExportReconcileCollections =>
    [
        "work_assignment_report",
        "work_status_operation_logs",
        "work_assignment_report_sections",
        "work_report_payloads",
        "work_report_table_values",
        "work_report_periods",
        "work_assignment_report_logs",
        "work_assignment_aggregate_configs",
        "doc_roles",
        "work_list_doc_roles",
        "assignment_list_doc_roles",
        "my_report_template_list_doc_roles",
        "my_report_period_list_doc_roles",
        "review_report_list_doc_roles",
        "review_assignment_summary_doc_roles",
        "docrole_read_model_projection_retry_jobs",
        "work_report_statistic_exports",
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
        "dynamic_flow_periodic_occurrences"
    ];

    private static string[] P809JobOutboxAndSideEffectCollections =>
    [
        "work_report_statistic_rebuild_jobs",
        "work_assignment_materialize_jobs",
        "work_assignment_queue",
        "stat_config_validation_jobs",
        "stat_config_command_receipts",
        "stat_config_audit_outbox",
        "dynamic_flow_runtime_outbox",
        "dynamic_flow_mapping_outbox",
        "user_action_logs",
        "user_action_log_retry_jobs",
        "notifications"
    ];

    private static string[] P809ZeroWriteCollections =>
        P809SixResultCollections
            .Concat(P809BasicSnapshotCollections)
            .Concat(P809AdvancedAndDiffOutputCollections)
            .Concat(P809ReportProjectionAggregateExportReconcileCollections)
            .Concat(P809JobOutboxAndSideEffectCollections)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private async Task<IReadOnlyList<P809ZeroWriteState>> CaptureP809ZeroWriteInventoryAsync(
        CancellationToken ct)
    {
        var existing = (await (await _database.ListCollectionNamesAsync(cancellationToken: ct))
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var rows = new List<P809ZeroWriteState>(P809ZeroWriteCollections.Length);
        foreach (var collection in P809ZeroWriteCollections)
        {
            if (!existing.Contains(collection))
            {
                rows.Add(new P809ZeroWriteState(
                    collection,
                    false,
                    0,
                    Sha256(Array.Empty<byte>())));
                continue;
            }

            var documents = await _database.GetCollection<BsonDocument>(collection)
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
            rows.Add(new P809ZeroWriteState(
                collection,
                true,
                documents.Count,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }
        return rows;
    }

    private static void RequireP809ZeroWrite(
        string context,
        IReadOnlyList<P809ZeroWriteState> before,
        IReadOnlyList<P809ZeroWriteState> after,
        IReadOnlyCollection<string>? category = null)
    {
        var expected = (category ?? P809ZeroWriteCollections)
            .ToHashSet(StringComparer.Ordinal);
        var beforeByName = before.ToDictionary(row => row.Collection, StringComparer.Ordinal);
        var afterByName = after.ToDictionary(row => row.Collection, StringComparer.Ordinal);
        foreach (var collection in expected.OrderBy(value => value, StringComparer.Ordinal))
        {
            HarnessAssert.True(beforeByName.TryGetValue(collection, out var left),
                $"{context} before snapshot omitted {collection}");
            HarnessAssert.True(afterByName.TryGetValue(collection, out var right),
                $"{context} after snapshot omitted {collection}");
            HarnessAssert.Equal(left!.Exists, right!.Exists,
                $"{context} created or removed prohibited store {collection}");
            HarnessAssert.Equal(left.Count, right.Count,
                $"{context} changed count in prohibited store {collection}");
            HarnessAssert.Equal(left.DocumentSetSha256, right.DocumentSetSha256,
                $"{context} changed document bytes in prohibited store {collection}");
        }
    }

    private static string[] P809HangfireInfrastructureSuffixes =>
    [
        ".jobGraph",
        ".locks",
        ".migrationLock",
        ".notifications",
        ".schema",
        ".server",
        ".stateHistory"
    ];

    private async Task AwaitP809InfrastructureBaselineAsync(
        CancellationToken ct)
    {
        var runKey = Path.GetFileName(_paths.RunRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(runKey))
        {
            throw new InvalidOperationException(
                "P8-09 infrastructure baseline cannot derive its run key.");
        }

        var runOwnedKey = new string(
                runKey.Where(char.IsLetterOrDigit).Take(24).ToArray())
            .ToLowerInvariant();
        if (runOwnedKey.Length == 0)
        {
            throw new InvalidOperationException(
                "P8-09 infrastructure baseline derived an empty run-owned key.");
        }

        var hangfirePrefix = $"p1hf_{runOwnedKey}";
        var expectedInfrastructureCollections =
            P809HangfireInfrastructureSuffixes
                .Select(suffix => hangfirePrefix + suffix)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        var expectedInfrastructureSet = expectedInfrastructureCollections
            .ToHashSet(StringComparer.Ordinal);
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var exchangeStart = _api.Exchanges.Count;
        var warmupFixture = new P8AdvancedFixture(
            "p809-infrastructure-baseline",
            ObjectId.GenerateNewId().ToString(),
            "p809-infrastructure",
            new tdtd_be.Models.WorkAssignment
            {
                Id = ObjectId.GenerateNewId().ToString()
            },
            "system_admin");
        var warmupResponse = await _api.GetAsync(
            AdvancedConfigRoute(warmupFixture),
            Actor("system_admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            warmupResponse,
            HttpStatusCode.Forbidden,
            "P8-09 missing-owner Advanced infrastructure warmup");
        HarnessAssert.Equal(
            "WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN",
            RequiredP809RecursiveString(warmupResponse.Json, "errorCode", "code"),
            "P8-09 infrastructure warmup error code drifted");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        IReadOnlyDictionary<string, P8CollectionState>? after = null;
        string? lastInfrastructureFingerprint = null;
        string[] missingInfrastructure = expectedInfrastructureCollections;
        var stableSamples = 0;
        while (DateTime.UtcNow < deadline)
        {
            var current = await CaptureDatabaseSnapshotAsync(ct);
            missingInfrastructure = expectedInfrastructureCollections
                .Where(name =>
                    !current.TryGetValue(name, out var state) ||
                    !state.Exists)
                .ToArray();
            if (missingInfrastructure.Length == 0)
            {
                var fingerprint = Sha256(Encoding.UTF8.GetBytes(string.Join(
                    "\n",
                    expectedInfrastructureCollections.Select(name =>
                    {
                        var state = current[name];
                        return $"{name}|{state.Exists}|{state.Count}|{state.DocumentSetSha256}";
                    }))));
                stableSamples = string.Equals(
                        fingerprint,
                        lastInfrastructureFingerprint,
                        StringComparison.Ordinal)
                    ? stableSamples + 1
                    : 1;
                lastInfrastructureFingerprint = fingerprint;
                if (stableSamples >= 3)
                {
                    after = current;
                    break;
                }
            }
            else
            {
                stableSamples = 0;
                lastInfrastructureFingerprint = null;
            }
            await Task.Delay(100, ct);
        }

        if (after is null)
        {
            throw new HarnessCaseNotRunnableException(
                "P8-09 could not establish a stable isolated Hangfire baseline before owned case snapshots; " +
                $"missing={string.Join(',', missingInfrastructure)};stableSamples={stableSamples}");
        }

        var deltas = BuildDeltas(before, after);
        var changed = deltas.Where(delta => delta.Changed).ToArray();
        var unexpected = changed
            .Where(delta =>
                !expectedInfrastructureSet.Contains(delta.Collection))
            .ToArray();
        var contractChanges = changed
            .Where(delta => ContractCollections.Contains(
                delta.Collection,
                StringComparer.Ordinal))
            .ToArray();
        var prohibitedChanges = changed
            .Where(delta => ProhibitedCollections.Contains(
                delta.Collection,
                StringComparer.Ordinal))
            .ToArray();

        VerifyCollectionContract(
            "P8-09/infrastructure-baseline",
            deltas,
            expectedInfrastructureCollections,
            Array.Empty<string>());
        HarnessAssert.True(unexpected.Length == 0,
            "P8-09 infrastructure baseline changed non-Hangfire collections: " +
            string.Join(", ", unexpected.Select(item => item.Collection)));
        HarnessAssert.True(contractChanges.Length == 0,
            "P8-09 infrastructure baseline changed business contract collections: " +
            string.Join(", ", contractChanges.Select(item => item.Collection)));
        HarnessAssert.True(prohibitedChanges.Length == 0,
            "P8-09 infrastructure baseline changed prohibited collections: " +
            string.Join(", ", prohibitedChanges.Select(item => item.Collection)));

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-infrastructure-baseline.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                baselineEstablishedBeforeFixturesAndOwnedCaseSnapshots = true,
                intentionallyForbiddenMissingOwnerAdvancedGet = true,
                warmupHttpStatus = (int)warmupResponse.StatusCode,
                warmupErrorCode = "WORK_ASSIGNMENT_AGGREGATE_READ_FORBIDDEN",
                hangfirePrefix,
                expectedInfrastructureCollections,
                stableSamples,
                infrastructureFingerprint = lastInfrastructureFingerprint,
                exchangeSequences = _api.Exchanges.Skip(exchangeStart)
                    .Select(exchange => exchange.Sequence).ToArray(),
                deltas,
                assertions = new
                {
                    exactRunOwnedInfrastructureOnly = unexpected.Length == 0,
                    businessContractDeltaZero = contractChanges.Length == 0,
                    prohibitedDeltaZero = prohibitedChanges.Length == 0,
                    caseSnapshotAllowlistUnchanged = true
                }
            },
            ct);
    }

    private async Task WriteP809CaseContractAsync(CancellationToken ct)
        => await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-case-contract.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                exactCaseCount = P809OwnedCaseContracts.Length,
                exactCaseIds = P809OwnedCaseContracts.Select(item => item.CaseId).ToArray(),
                contracts = P809OwnedCaseContracts
            },
            ct);

    private async Task WriteP809ZeroWriteInventoryAsync(CancellationToken ct)
    {
        HarnessAssert.Equal(53, P809ZeroWriteCollections.Length,
            "P8-09 exact zero-write physical collection count drifted");
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-zero-write-inventory.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                documentSetHashAlgorithm = "SHA-256(length-prefixed BSON documents sorted by _id)",
                exactCollectionCount = P809ZeroWriteCollections.Length,
                categories = new
                {
                    sixResultCollections = P809SixResultCollections,
                    basicSnapshots = P809BasicSnapshotCollections,
                    advancedHierarchyAndDiffOutput = P809AdvancedAndDiffOutputCollections,
                    reportProjectionAggregateExportReconcile = P809ReportProjectionAggregateExportReconcileCollections,
                    jobsOutboxAndSideEffects = P809JobOutboxAndSideEffectCollections
                },
                exactCollections = P809ZeroWriteCollections
            },
            ct);
    }

    private async Task WriteP809SourceFreezeAsync(CancellationToken ct)
    {
        var integrationDirectory = Path.Combine(_paths.BackendRoot, "tests", "tdtd-be.IntegrationTests");
        var actualRouteSourcePaths = new[]
        {
            Path.Combine(_paths.BackendRoot, "Controllers", "WorkReportFieldStatisticsController.cs"),
            Path.Combine(_paths.BackendRoot, "Controllers", "WorkReportTableStatisticsController.cs"),
            Path.Combine(_paths.BackendRoot, "Controllers", "WorkReportLabelStatisticsController.cs"),
            Path.Combine(_paths.BackendRoot, "Controllers", "WorkAssignmentBasicSummaryController.cs"),
            Path.Combine(_paths.BackendRoot, "Controllers", "WorkAssignmentAdvancedSummaryController.cs"),
            Path.Combine(_paths.BackendRoot, "Controllers", "WorkReportStatisticDiffController.cs"),
            Path.Combine(_paths.BackendRoot, "Controllers", "WorkAssignmentAggregateTableController.cs"),
            Path.Combine(_paths.BackendRoot, "Controllers", "WorkAssignmentReportsController.cs"),
            Path.Combine(_paths.BackendRoot, "Controllers", "AdminOperationsController.cs"),
            Path.Combine(_paths.BackendRoot, "DashboardModel", "Controllers", "DashboardMindMapController.cs"),
            Path.Combine(_paths.BackendRoot, "Services", "WorkAssignmentReports", "Statistics", "WorkReportFieldStatisticsService.cs"),
            Path.Combine(_paths.BackendRoot, "Services", "WorkAssignmentReports", "Statistics", "WorkReportTableStatisticsService.cs"),
            Path.Combine(_paths.BackendRoot, "Services", "WorkAssignmentReports", "Statistics", "WorkReportLabelStatisticsService.cs"),
            Path.Combine(_paths.BackendRoot, "Services", "WorkAssignmentReports", "Statistics", "WorkReportStatisticRebuildJobService.cs"),
            Path.Combine(_paths.BackendRoot, "Services", "WorkAssignments", "Aggregate", "AggregateTableService.cs"),
            Path.Combine(_paths.BackendRoot, "DashboardModel", "Services", "DashboardMindMapQueryService.cs")
        };
        HarnessAssert.True(actualRouteSourcePaths.All(File.Exists),
            "P8-09 source-freeze set lacks an actual-route guard source: " +
            string.Join(", ", actualRouteSourcePaths.Where(path => !File.Exists(path))));
        var sourcePaths = new[]
            {
                Path.Combine(_paths.BackendRoot, "Program.cs"),
                Path.Combine(_paths.BackendRoot, "Common", "Errors", "AppErrorCode.cs"),
                Path.Combine(_paths.BackendRoot, "Common", "Errors", "AppErrorCatalog.cs"),
                Path.Combine(_paths.BackendRoot, "Data", "MongoDbContext.cs"),
                Path.Combine(_paths.BackendRoot, "Data", "Infrastructure", "MongoOptions.cs"),
                Path.Combine(_paths.BackendRoot, "tests", "tdtd-be.IntegrationTests", "BackendServerLease.cs"),
                Path.Combine(_paths.BackendRoot, "tests", "tdtd-be.IntegrationTests", "P8StatConfigProbe.cs")
            }
            .Concat(actualRouteSourcePaths)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(_paths.BackendRoot, "Controllers"),
                "*StatConfig*.*",
                SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(
                Path.Combine(_paths.BackendRoot, "DTOs", "StatisticsConfiguration"),
                "*.cs",
                SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(
                Path.Combine(_paths.BackendRoot, "Services", "StatisticsConfiguration"),
                "*.cs",
                SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(
                integrationDirectory,
                "P8StatConfigProbe.P809*.cs",
                SearchOption.TopDirectoryOnly))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        HarnessAssert.True(
            sourcePaths.Any(path => Path.GetFileName(path).Contains("Bundle", StringComparison.OrdinalIgnoreCase)),
            "P8-09 source-freeze set lacks the production bundle contract.");

        var rows = new List<object>(sourcePaths.Length);
        var canonical = new StringBuilder();
        foreach (var path in sourcePaths)
        {
            var relativePath = Path.GetRelativePath(_paths.BackendRoot, path).Replace('\\', '/');
            var sha256 = Sha256(await File.ReadAllBytesAsync(path, ct));
            rows.Add(new { relativePath, sha256 });
            canonical.Append(relativePath).Append(':').Append(sha256).Append('\n');
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-source-freeze.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                frozenBeforeFirstOwnedApiCall = true,
                actualRouteGuardSourceCount = actualRouteSourcePaths.Length,
                sourceSetSha256 = Sha256(Encoding.UTF8.GetBytes(canonical.ToString())),
                sourceFiles = rows
            },
            ct);
    }

    private async Task WriteP809BundleAndBarrierEvidenceAsync(CancellationToken ct)
    {
        const int expectedActualRouteCount = 35;
        var routeIdentities = _p809ActualRoutes
            .Select(row => $"{row.Method} {row.Path}")
            .ToArray();
        var expectedEntryCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["P9_RUN"] = 7,
            ["P9_PROJECTION"] = 6,
            ["P9_RESULT"] = 21,
            ["P9_EXPORT"] = 1
        };
        var actualEntryCounts = _p809ActualRoutes
            .GroupBy(row => row.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        HarnessAssert.Equal(expectedActualRouteCount, _p809ActualRoutes.Count,
            "P8-09 actual-route matrix count drifted");
        HarnessAssert.Equal(expectedActualRouteCount,
            routeIdentities.Distinct(StringComparer.Ordinal).Count(),
            "P8-09 actual-route matrix contains duplicate method/path identities");
        HarnessAssert.True(
            expectedEntryCounts.Count == actualEntryCounts.Count &&
            expectedEntryCounts.All(pair =>
                actualEntryCounts.TryGetValue(pair.Key, out var count) &&
                count == pair.Value),
            "P8-09 actual-route entry distribution drifted: " +
            string.Join(",", actualEntryCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")));
        HarnessAssert.True(_p809ActualRoutes.All(row =>
                row.HttpStatus == 409 &&
                row.ErrorCode == "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE"),
            "P8-09 actual-route matrix contains a non-stable barrier response");
        HarnessAssert.True(_p809ActualRoutes.All(row =>
                row.ZeroWriteCollectionCount == 53 &&
                string.Equals(
                    row.BeforeInventorySha256,
                    row.AfterInventorySha256,
                    StringComparison.Ordinal)),
            "P8-09 actual-route matrix contains a non-zero exact inventory delta");

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-hash-oracle.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                algorithm = "SHA-256 canonical UTF-8 JSON",
                dependencyKinds = P809DependencyKinds,
                rows = _p809BundleHashes
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-phase-barrier-oracle.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                entries = new[] { "P9_RUN", "P9_PROJECTION", "P9_RESULT", "P9_EXPORT", "P10_RECONCILE" },
                rows = _p809Barriers
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-state-oracle.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                rows = _p809States
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-bnd-actual-route-matrix.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                exactRouteCount = expectedActualRouteCount,
                actualRouteCount = _p809ActualRoutes.Count,
                uniqueMethodPathCount = routeIdentities.Distinct(StringComparer.Ordinal).Count(),
                entryCounts = actualEntryCounts,
                exactZeroWriteCollectionCountPerRoute = 53,
                allStable409 = _p809ActualRoutes.All(row => row.HttpStatus == 409),
                rows = _p809ActualRoutes
            },
            ct);
    }
}

internal sealed record P809ZeroWriteState(
    string Collection,
    bool Exists,
    long Count,
    string DocumentSetSha256);
