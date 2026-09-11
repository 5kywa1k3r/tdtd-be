using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunOperationsDiagnosticCasesCoreAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-OPS-DIAG-01",
            async () =>
            {
                var foundationId = RequireOperationsFoundationJobId();
                var currentLifecycle = await LoadCurrentOperationsPublicationAsync(ct);
                _opsCurrentLifecycleJobId = BsonString(currentLifecycle, "_id");

                var list = await RequireApi().GetAsync(
                    "api/operations/jobs?limit=50",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(list, HttpStatusCode.OK, "P9-OPS full list");
                var items = OperationsRequiredArray(list.Json, "items", "P9-OPS full list")
                    .OfType<JsonObject>()
                    .ToArray();
                HarnessAssert.True(
                    items.Any(item => string.Equals(RequiredString(item, "jobId"), foundationId, StringComparison.Ordinal)),
                    "P9-OPS list omitted Foundation job");
                HarnessAssert.True(
                    items.Any(item => string.Equals(RequiredString(item, "jobId"), _opsCurrentLifecycleJobId, StringComparison.Ordinal)),
                    "P9-OPS list omitted current Lifecycle job");

                var foundationFilter = await RequireApi().GetAsync(
                    $"api/operations/jobs?limit=50&status=DONE&runKind={WorkReportStatisticRebuildJobRunKinds.Foundation}&workId={Fixture().WorkId}",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(foundationFilter, HttpStatusCode.OK, "P9-OPS Foundation filter");
                var foundationItems = OperationsRequiredArray(
                        foundationFilter.Json,
                        "items",
                        "P9-OPS Foundation filter")
                    .OfType<JsonObject>()
                    .ToArray();
                HarnessAssert.True(
                    foundationItems.Any(item => string.Equals(RequiredString(item, "jobId"), foundationId, StringComparison.Ordinal)),
                    "P9-OPS DONE Foundation filter omitted completed job");
                HarnessAssert.True(
                    foundationItems.All(item =>
                        string.Equals(
                            RequiredString(item, "runKind"),
                            WorkReportStatisticRebuildJobRunKinds.Foundation,
                            StringComparison.Ordinal) &&
                        string.Equals(RequiredString(item, "status"), "DONE", StringComparison.Ordinal)),
                    "P9-OPS Foundation filter leaked another kind/status");

                var lifecycleFilter = await RequireApi().GetAsync(
                    "api/operations/jobs?limit=50&status=DONE&runKind=LIFECYCLE_DIRECT_PROJECTION",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(lifecycleFilter, HttpStatusCode.OK, "P9-OPS Lifecycle filter");
                var lifecycleItems = OperationsRequiredArray(
                        lifecycleFilter.Json,
                        "items",
                        "P9-OPS Lifecycle filter")
                    .OfType<JsonObject>()
                    .ToArray();
                HarnessAssert.True(
                    lifecycleItems.Any(item => string.Equals(
                        RequiredString(item, "jobId"),
                        _opsCurrentLifecycleJobId,
                        StringComparison.Ordinal)),
                    "P9-OPS Lifecycle filter omitted current publication");

                var foundationDetail = await RequireApi().GetAsync(
                    $"api/operations/jobs/{foundationId}",
                    Actor("admin").Token,
                    ct: ct);
                var lifecycleDetail = await RequireApi().GetAsync(
                    $"api/operations/jobs/{_opsCurrentLifecycleJobId}",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(foundationDetail, HttpStatusCode.OK, "P9-OPS Foundation detail");
                ApiHarnessClient.ExpectStatus(lifecycleDetail, HttpStatusCode.OK, "P9-OPS Lifecycle detail");
                HarnessAssert.Equal("DONE", RequiredString(foundationDetail.Json, "status"), "P9-OPS Foundation detail state");
                HarnessAssert.Equal(
                    "LIFECYCLE_DIRECT_PROJECTION",
                    RequiredString(lifecycleDetail.Json, "runKind"),
                    "P9-OPS Lifecycle detail kind");
                HarnessAssert.True(RequiredBool(lifecycleDetail.Json, "isCurrentPublication"), "P9-OPS Lifecycle detail not current");
                await TraceOperationsJobAsync("DIAG_FOUNDATION_DETAIL", foundationId, ct);
                await TraceOperationsJobAsync("DIAG_LIFECYCLE_DETAIL", _opsCurrentLifecycleJobId, ct);
                return new CaseObservation(
                    "Canonical list/detail routes exposed both Foundation and Lifecycle jobs with exact run-kind/status/work filters and current-publication identity.",
                    $"foundation={foundationId}/DONE;lifecycle={_opsCurrentLifecycleJobId}/DONE/current;all={items.Length};foundationFiltered={foundationItems.Length};lifecycleFiltered={lifecycleItems.Length}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-DIAG-02",
            async () =>
            {
                var foundationId = RequireOperationsFoundationJobId();
                var lifecycleId = _opsCurrentLifecycleJobId
                                  ?? throw new HarnessCaseNotRunnableException(
                                      "P9-OPS current Lifecycle job id is unavailable.");
                foreach (var (kind, jobId) in new[]
                         {
                             ("FOUNDATION", foundationId),
                             ("LIFECYCLE", lifecycleId)
                         })
                {
                    var diagnostic = await RequireApi().GetAsync(
                        $"api/operations/jobs/{jobId}/diagnostics",
                        Actor("admin").Token,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        diagnostic,
                        HttpStatusCode.OK,
                        $"P9-OPS {kind} diagnostics");
                    HarnessAssert.Equal("VALID", RequiredString(diagnostic.Json, "stateIntegrity"), $"{kind} state integrity");
                    HarnessAssert.Equal("VALID", RequiredString(diagnostic.Json, "headerIntegrity"), $"{kind} header integrity");
                    HarnessAssert.Equal("VALID", RequiredString(diagnostic.Json, "receiptIntegrity"), $"{kind} receipt integrity");
                    HarnessAssert.Equal("NONE", RequiredString(diagnostic.Json, "leaseState"), $"{kind} lease state");
                    HarnessAssert.True(
                        ApiHarnessClient.RequiredInt(diagnostic.Json, "maxRetryCount") >= 1,
                        $"{kind} max retry count");
                }

                var lifecycle = await LoadJobAsync(lifecycleId, ct);
                var immutableDirectBefore = await CaptureLifecycleDirectSnapshotAsync(ct);
                var databaseBefore = await CaptureDatabaseSnapshotAsync(ct);
                var rejected = await PostOperationsMutationAsync(
                    lifecycleId,
                    "retry",
                    BuildOperationsCas(lifecycle, "p9-ops-lifecycle-retry-blocked-018"),
                    ct);
                ExpectError(
                    rejected,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "P9-OPS Lifecycle retry mutation");
                HarnessAssert.Equal(
                    "OPERATION_RUN_KIND_NOT_MUTABLE",
                    ApiHarnessClient.FindStringRecursive(rejected.Json, "reason"),
                    "P9-OPS Lifecycle mutation reason");
                var databaseAfter = await CaptureDatabaseSnapshotAsync(ct);
                var immutableDirectAfter = await CaptureLifecycleDirectSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(databaseBefore, databaseAfter, "P9-OPS Lifecycle mutation zero-write");
                AssertLifecycleSnapshotEqual(
                    immutableDirectBefore,
                    immutableDirectAfter,
                    "P9-OPS operator mutation must not alter publication inventory");
                return new CaseObservation(
                    "Admin diagnostics validated state/header/receipt integrity for both run kinds; lifecycle jobs remained read-only and operator retry changed no publication inventory.",
                    $"foundation={foundationId}/VALID;lifecycle={lifecycleId}/VALID;lease=NONE;lifecycleRetry=409/OPERATION_RUN_KIND_NOT_MUTABLE;db={SnapshotSha256(databaseAfter)}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-DIAG-03",
            async () =>
            {
                var actualId = RequireOperationsFoundationJobId();
                var missingId = ObjectId.GenerateNewId().ToString();
                var routes = new[]
                {
                    (
                        Route: $"api/operations/jobs/{actualId}",
                        ExpectedCode: "STAT_RUN_FORBIDDEN"),
                    (
                        Route: $"api/operations/jobs/{missingId}",
                        ExpectedCode: "STAT_RUN_FORBIDDEN"),
                    (
                        Route: $"api/operations/jobs/{actualId}/diagnostics",
                        ExpectedCode: "AUTH_SYSTEM_ADMIN_REQUIRED"),
                    (
                        Route: $"api/operations/jobs/{missingId}/diagnostics",
                        ExpectedCode: "AUTH_SYSTEM_ADMIN_REQUIRED")
                };
                var before = await CaptureDatabaseSnapshotAsync(ct);
                var responses = new List<(
                    string Route,
                    string ExpectedCode,
                    ApiHarnessResponse Response)>();
                foreach (var route in routes)
                {
                    responses.Add((
                        route.Route,
                        route.ExpectedCode,
                        await RequireApi().GetAsync(
                            route.Route,
                            Actor("outsider").Token,
                            ct: ct)));
                }
                foreach (var jobId in new[] { actualId, missingId })
                {
                    var route = $"api/operations/jobs/{jobId}/retry";
                    responses.Add((
                        route,
                        "AUTH_SYSTEM_ADMIN_REQUIRED",
                        await RequireApi().PostAsync(
                            route,
                            new JsonObject(),
                            Actor("outsider").Token,
                            ct: ct)));
                }
                foreach (var item in responses)
                {
                    ExpectError(
                        item.Response,
                        HttpStatusCode.Forbidden,
                        item.ExpectedCode,
                        $"P9-OPS auth-before-existence {item.Route}");
                }
                var errorCodes = responses
                    .Select(item => ApiHarnessClient.FindStringRecursive(item.Response.Json, "errorCode") ??
                                    ApiHarnessClient.FindStringRecursive(item.Response.Json, "code"))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                HarnessAssert.Equal(
                    2,
                    errorCodes.Length,
                    "P9-OPS auth-before-existence error surfaces");
                foreach (var group in responses.GroupBy(
                             item => item.ExpectedCode,
                             StringComparer.Ordinal))
                {
                    HarnessAssert.True(
                        group.All(item => string.Equals(
                            ApiHarnessClient.FindStringRecursive(
                                item.Response.Json,
                                "errorCode") ??
                            ApiHarnessClient.FindStringRecursive(
                                item.Response.Json,
                                "code"),
                            group.Key,
                            StringComparison.Ordinal)),
                        $"P9-OPS auth-before-existence drift for {group.Key}");
                }
                var after = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, after, "P9-OPS auth-before-existence zero-write");
                foreach (var item in responses)
                {
                    _opsAuthTrace.Add(new P9OperationsAuthTrace(
                        "P9-OPS-DIAG-03",
                        item.Route,
                        (int)item.Response.StatusCode,
                        SnapshotSha256(before),
                        SnapshotSha256(after),
                        true,
                        true));
                }
                _opsAuthBeforeExistenceVerified = true;
                return new CaseObservation(
                    "For an unauthorized actor, real and random job IDs were indistinguishable across detail, diagnostics and malformed mutation bodies; every call was 403/zero-write.",
                    $"actual={actualId};random={missingId};calls={responses.Count};statuses={string.Join(',', responses.Select(item => (int)item.Response.StatusCode))};code={errorCodes[0]};db={SnapshotSha256(after)}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-DIAG-04",
            async () =>
            {
                var jobId = RequireOperationsCancelledJobId();
                var jobs = RequireDatabase().GetCollection<BsonDocument>(LifecycleJobCollection);
                var originalJob = await LoadJobAsync(jobId, ct);
                var originalHistory = originalJob.GetValue("operationReceiptHistoryHash").DeepClone();
                var invalidHistory = new string('f', 64);
                if (string.Equals(BsonString(originalJob, "operationReceiptHistoryHash"), invalidHistory, StringComparison.Ordinal))
                    invalidHistory = new string('0', 64);
                var before = await CaptureDatabaseSnapshotAsync(ct);
                ApiHarnessResponse? corruptedDiagnostic = null;
                try
                {
                    var corrupted = await jobs.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(jobId)),
                        Builders<BsonDocument>.Update.Set("operationReceiptHistoryHash", invalidHistory),
                        cancellationToken: ct);
                    HarnessAssert.Equal(1L, corrupted.ModifiedCount, "P9-OPS receipt corruption injection");
                    corruptedDiagnostic = await RequireApi().GetAsync(
                        $"api/operations/jobs/{jobId}/diagnostics",
                        Actor("admin").Token,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        corruptedDiagnostic,
                        HttpStatusCode.OK,
                        "P9-OPS corrupted receipt diagnostics");
                    HarnessAssert.Equal(
                        "INVALID",
                        RequiredString(corruptedDiagnostic.Json, "receiptIntegrity"),
                        "P9-OPS corrupted receipt integrity");
                    HarnessAssert.Equal("VALID", RequiredString(corruptedDiagnostic.Json, "stateIntegrity"), "P9-OPS corrupted state integrity");
                    HarnessAssert.Equal("VALID", RequiredString(corruptedDiagnostic.Json, "headerIntegrity"), "P9-OPS corrupted header integrity");
                }
                finally
                {
                    await jobs.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(jobId)),
                        Builders<BsonDocument>.Update.Set("operationReceiptHistoryHash", originalHistory),
                        cancellationToken: CancellationToken.None);
                }
                var afterRestore = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, afterRestore, "P9-OPS receipt corruption restoration");
                var restoredDiagnostic = await RequireApi().GetAsync(
                    $"api/operations/jobs/{jobId}/diagnostics",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(restoredDiagnostic, HttpStatusCode.OK, "P9-OPS restored receipt diagnostics");
                HarnessAssert.Equal("VALID", RequiredString(restoredDiagnostic.Json, "receiptIntegrity"), "P9-OPS restored receipt integrity");

                var lifecycleId = _opsCurrentLifecycleJobId
                                  ?? throw new HarnessCaseNotRunnableException(
                                      "P9-OPS current Lifecycle job id is unavailable for header tamper.");
                var lifecycleJob = await LoadJobAsync(lifecycleId, ct);
                var originalPromptPin = lifecycleJob.GetValue("candidatePromptId").DeepClone();
                HarnessAssert.Equal(
                    OperationsPromptId,
                    BsonString(lifecycleJob, "candidatePromptId"),
                    "P9-OPS Lifecycle producer prompt pin");
                try
                {
                    var tampered = await jobs.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(lifecycleId)),
                        Builders<BsonDocument>.Update.Set(
                            "candidatePromptId",
                            "P9-08-TAMPER"),
                        cancellationToken: ct);
                    HarnessAssert.Equal(1L, tampered.ModifiedCount, "P9-OPS Lifecycle header tamper injection");
                    var headerDiagnostic = await RequireApi().GetAsync(
                        $"api/operations/jobs/{lifecycleId}/diagnostics",
                        Actor("admin").Token,
                        ct: ct);
                    ApiHarnessClient.ExpectStatus(
                        headerDiagnostic,
                        HttpStatusCode.OK,
                        "P9-OPS tampered Lifecycle header diagnostics");
                    HarnessAssert.Equal(
                        "INVALID",
                        RequiredString(headerDiagnostic.Json, "headerIntegrity"),
                        "P9-OPS tampered Lifecycle header integrity");
                    HarnessAssert.Equal(
                        "VALID",
                        RequiredString(headerDiagnostic.Json, "stateIntegrity"),
                        "P9-OPS tampered Lifecycle state integrity");
                }
                finally
                {
                    await jobs.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(lifecycleId)),
                        Builders<BsonDocument>.Update.Set(
                            "candidatePromptId",
                            originalPromptPin),
                        cancellationToken: CancellationToken.None);
                }
                var afterHeaderRestore = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, afterHeaderRestore, "P9-OPS Lifecycle header restoration");
                var restoredHeaderDiagnostic = await RequireApi().GetAsync(
                    $"api/operations/jobs/{lifecycleId}/diagnostics",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    restoredHeaderDiagnostic,
                    HttpStatusCode.OK,
                    "P9-OPS restored Lifecycle header diagnostics");
                HarnessAssert.Equal(
                    "VALID",
                    RequiredString(restoredHeaderDiagnostic.Json, "headerIntegrity"),
                    "P9-OPS restored Lifecycle header integrity");

                var prohibitedTokens = new[]
                {
                    "connectionString",
                    "password",
                    "bearer",
                    "stackTrace",
                    "sourcePayloadJson",
                    "claimToken",
                    "leaseOwnerId",
                    "lastErrorType",
                    "lastError"
                };
                var fullResponseCorpus = string.Join(
                    "\n",
                    RequireApi().Exchanges
                        .Select(item => item.ResponseBody ?? string.Empty));
                var operationsResponseCorpus = string.Join(
                    "\n",
                    RequireApi().Exchanges
                        .Where(item =>
                            !string.Equals(
                                item.Path,
                                "api/system/bootstrap",
                                StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(
                                item.Path,
                                "api/auth/login",
                                StringComparison.OrdinalIgnoreCase) &&
                            !item.Path.StartsWith(
                                "api/testing/",
                                StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.ResponseBody ?? string.Empty));
                foreach (var token in prohibitedTokens)
                {
                    HarnessAssert.True(
                        !operationsResponseCorpus.Contains(token, StringComparison.OrdinalIgnoreCase),
                        $"P9-OPS response corpus exposed prohibited token {token}");
                }
                var allArtifactCorpus = new StringBuilder();
                var operationsArtifactCorpus = new StringBuilder();
                foreach (var path in Directory.GetFiles(_paths.RunRoot, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var content = await File.ReadAllTextAsync(path, ct);
                    allArtifactCorpus.AppendLine(content);
                    // This shared harness manifest intentionally carries the synthetic
                    // source payload needed to reproduce the fixture. It is neither an
                    // operations response nor an exported P9-OPS artifact; exact secret
                    // values are still scanned below through allArtifactCorpus.
                    if (!string.Equals(
                            Path.GetFileName(path),
                            "actor-fixture-matrix.json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        operationsArtifactCorpus.AppendLine(content);
                    }
                }
                foreach (var token in prohibitedTokens)
                {
                    HarnessAssert.True(
                        !operationsArtifactCorpus.ToString().Contains(token, StringComparison.OrdinalIgnoreCase),
                        $"P9-OPS pre-evidence artifact exposed prohibited token {token}");
                }
                foreach (var secret in new[]
                         {
                             Actor("admin").Token,
                             Actor("executor").Token,
                             Actor("outsider").Token,
                             _opsClaimToken ?? string.Empty
                         }.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    HarnessAssert.True(
                        !fullResponseCorpus.Contains(secret, StringComparison.Ordinal) &&
                        !allArtifactCorpus.ToString().Contains(secret, StringComparison.Ordinal),
                        "P9-OPS response/artifact corpus exposed a bearer or worker secret");
                }
                _opsSecurityScanVerified = true;
                _opsReceiptVerified = true;
                return new CaseObservation(
                    "Controlled receipt-history and Lifecycle-header corruption were reported as INVALID without raw diagnostics; exact restoration returned the database to baseline and all response/artifact secret scans passed.",
                    $"receiptJob={jobId};receipt=INVALID->VALID;lifecycle={lifecycleId};header=INVALID->VALID;producerPrompt={OperationsPromptId};db={SnapshotSha256(afterHeaderRestore)};responses={RequireApi().Exchanges.Count};prohibitedMatches=0");
            },
            ct);
    }

    private async Task RunOperationsCleanupCasesCoreAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-OPS-CLEANUP-01",
            async () =>
            {
                var candidateId = RequireOperationsFoundationJobId();
                var current = await LoadCurrentOperationsPublicationAsync(ct);
                _opsCurrentLifecycleJobId = BsonString(current, "_id");
                _opsCleanupReferencedJobId = _flwRunId
                                               ?? throw new HarnessCaseNotRunnableException(
                                                   "P9-OPS original approved run id is unavailable.");
                HarnessAssert.True(
                    !string.Equals(_opsCleanupReferencedJobId, _opsCurrentLifecycleJobId, StringComparison.Ordinal),
                    "P9-OPS cleanup referenced/current fixtures collapsed");
                var jobs = RequireDatabase().GetCollection<BsonDocument>(LifecycleJobCollection);
                var referencedByReversal = await jobs
                    .Find(new BsonDocument
                    {
                        ["isDeleted"] = false,
                        ["reversalAudit.priorRunId"] = ObjectId.Parse(_opsCleanupReferencedJobId)
                    })
                    .AnyAsync(ct);
                HarnessAssert.True(referencedByReversal, "P9-OPS cleanup reference fixture is not durable");

                var now = DateTime.UtcNow;
                _opsCleanupOlderThanUtc = new DateTime(
                    now.AddHours(-25).Ticks - now.AddHours(-25).Ticks % TimeSpan.TicksPerMillisecond,
                    DateTimeKind.Utc);
                var backdated = _opsCleanupOlderThanUtc.Value.AddHours(-24);
                foreach (var jobId in new[]
                         {
                             candidateId,
                             _opsCurrentLifecycleJobId,
                             _opsCleanupReferencedJobId
                         })
                {
                    var updated = await jobs.UpdateOneAsync(
                        new BsonDocument("_id", ObjectId.Parse(jobId)),
                        Builders<BsonDocument>.Update.Set("updatedAtUtc", backdated),
                        cancellationToken: ct);
                    HarnessAssert.Equal(1L, updated.ModifiedCount, $"P9-OPS cleanup backdate {jobId}");
                }

                var seeded = await CaptureDatabaseSnapshotAsync(ct);
                var dryRun = await RequireApi().PostAsync(
                    "api/operations/jobs/cleanup/dry-run",
                    new
                    {
                        olderThanUtc = _opsCleanupOlderThanUtc.Value,
                        maxJobs = 10
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(dryRun, HttpStatusCode.OK, "P9-OPS cleanup dry-run");
                var afterDryRun = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(seeded, afterDryRun, "P9-OPS cleanup dry-run zero-write");
                var root = ApiHarnessClient.RequiredObject(dryRun.Json, "P9-OPS cleanup dry-run");
                _opsCleanupPreview = (JsonObject)root.DeepClone();
                var candidates = OperationsRequiredArray(root, "candidates", "P9-OPS cleanup dry-run")
                    .OfType<JsonObject>()
                    .ToArray();
                HarnessAssert.Equal(1, candidates.Length, "P9-OPS cleanup candidate count");
                HarnessAssert.Equal(candidateId, RequiredString(candidates[0], "jobId"), "P9-OPS cleanup candidate id");
                HarnessAssert.Equal("DONE", RequiredString(candidates[0], "status"), "P9-OPS cleanup candidate status");
                HarnessAssert.True(
                    ApiHarnessClient.RequiredInt(root, "protectedCurrentCount") >= 1,
                    "P9-OPS cleanup did not protect current publication");
                HarnessAssert.True(
                    ApiHarnessClient.RequiredInt(root, "protectedReferencedCount") >= 1,
                    "P9-OPS cleanup did not protect referenced publication");
                HarnessAssert.True(IsCanonicalSha(RequiredString(root, "candidateHash")), "P9-OPS cleanup candidate hash");
                HarnessAssert.True(
                    candidates.All(item =>
                        !string.Equals(RequiredString(item, "jobId"), _opsCurrentLifecycleJobId, StringComparison.Ordinal) &&
                        !string.Equals(RequiredString(item, "jobId"), _opsCleanupReferencedJobId, StringComparison.Ordinal)),
                    "P9-OPS cleanup selected a protected publication");
                _opsCleanupTrace.Add(new P9OperationsCleanupTrace(
                    "P9-OPS-CLEANUP-01",
                    "DRY_RUN",
                    candidates.Length,
                    0,
                    SnapshotSha256(seeded),
                    SnapshotSha256(afterDryRun),
                    true,
                    true));
                return new CaseObservation(
                    "Cleanup dry-run was zero-write and selected only the backdated unreferenced Foundation job while protecting both current and reversal-referenced Lifecycle publications.",
                    $"candidate={candidateId};selected=1;current={_opsCurrentLifecycleJobId}/protected;referenced={_opsCleanupReferencedJobId}/protected;hash={RequiredString(root, "candidateHash")};db={SnapshotSha256(afterDryRun)}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-CLEANUP-02",
            async () =>
            {
                var candidateId = RequireOperationsFoundationJobId();
                var preview = _opsCleanupPreview
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P9-OPS cleanup preview is unavailable.");
                var cutoff = _opsCleanupOlderThanUtc
                             ?? throw new HarnessCaseNotRunnableException(
                                 "P9-OPS cleanup cutoff is unavailable.");
                var candidateHash = RequiredString(preview, "candidateHash");
                _opsCleanupApplyRequest = new JsonObject
                {
                    ["commandId"] = "p9-ops-cleanup-022",
                    ["olderThanUtc"] = FormatUtc(cutoff),
                    ["maxJobs"] = 10,
                    ["expectedCandidateHash"] = candidateHash
                };
                var beforeApply = await CaptureDatabaseSnapshotAsync(ct);
                var apply = await RequireApi().PostAsync(
                    "api/operations/jobs/cleanup/apply",
                    _opsCleanupApplyRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                await VerifyOperationsCleanupReceiptAsync(
                    "P9-OPS-CLEANUP-02",
                    candidateId,
                    "p9-ops-cleanup-022",
                    apply,
                    false,
                    ct);
                HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(apply.Json, "appliedCount"), "P9-OPS cleanup applied count");
                HarnessAssert.Equal(candidateHash, RequiredString(apply.Json, "candidateHash"), "P9-OPS cleanup applied hash");
                var afterApply = await CaptureDatabaseSnapshotAsync(ct);
                var deleted = await LoadOperationsJobIncludingDeletedAsync(candidateId, ct);
                HarnessAssert.True(BsonBool(deleted, "isDeleted"), "P9-OPS cleanup did not tombstone candidate");
                var current = await LoadOperationsJobIncludingDeletedAsync(
                    _opsCurrentLifecycleJobId!,
                    ct);
                var referenced = await LoadOperationsJobIncludingDeletedAsync(
                    _opsCleanupReferencedJobId!,
                    ct);
                HarnessAssert.True(!BsonBool(current, "isDeleted"), "P9-OPS cleanup deleted current publication");
                HarnessAssert.True(!BsonBool(referenced, "isDeleted"), "P9-OPS cleanup deleted referenced publication");

                var generationRows = 0L;
                var liveGenerationRows = 0L;
                foreach (var collectionName in LifecycleDirectCollections)
                {
                    var collection = RequireDatabase().GetCollection<BsonDocument>(collectionName);
                    var runFilter = new BsonDocument("directProjection.runId", ObjectId.Parse(candidateId));
                    generationRows += await collection.CountDocumentsAsync(runFilter, cancellationToken: ct);
                    liveGenerationRows += await collection.CountDocumentsAsync(
                        Builders<BsonDocument>.Filter.And(
                            runFilter,
                            new BsonDocument("isDeleted", false)),
                        cancellationToken: ct);
                }
                HarnessAssert.Equal(0L, liveGenerationRows, "P9-OPS cleanup left live generation rows");

                var beforeReplay = await CaptureDatabaseSnapshotAsync(ct);
                var replay = await RequireApi().PostAsync(
                    "api/operations/jobs/cleanup/apply",
                    _opsCleanupApplyRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
                await VerifyOperationsCleanupReceiptAsync(
                    "P9-OPS-CLEANUP-02",
                    candidateId,
                    "p9-ops-cleanup-022",
                    replay,
                    true,
                    ct);
                var afterReplay = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(beforeReplay, afterReplay, "P9-OPS cleanup exact replay zero-write");

                var wrongHash = string.Equals(candidateHash, new string('0', 64), StringComparison.Ordinal)
                    ? new string('f', 64)
                    : new string('0', 64);
                var staleRequest = new JsonObject
                {
                    ["commandId"] = "p9-ops-cleanup-stale-022",
                    ["olderThanUtc"] = FormatUtc(cutoff),
                    ["maxJobs"] = 10,
                    ["expectedCandidateHash"] = wrongHash
                };
                var beforeStale = await CaptureDatabaseSnapshotAsync(ct);
                var stale = await RequireApi().PostAsync(
                    "api/operations/jobs/cleanup/apply",
                    staleRequest,
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    stale,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "P9-OPS cleanup stale candidate");
                HarnessAssert.Equal(
                    "CLEANUP_CANDIDATE_SET_STALE",
                    ApiHarnessClient.FindStringRecursive(stale.Json, "reason"),
                    "P9-OPS cleanup stale reason");
                var afterStale = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(beforeStale, afterStale, "P9-OPS cleanup stale hash zero-write");
                _opsCleanupTrace.Add(new P9OperationsCleanupTrace(
                    "P9-OPS-CLEANUP-02",
                    "APPLY_REPLAY_STALE",
                    1,
                    1,
                    SnapshotSha256(beforeApply),
                    SnapshotSha256(afterApply),
                    !BsonBool(current, "isDeleted") && !BsonBool(referenced, "isDeleted"),
                    true));
                _opsReceiptVerified = true;
                _opsQueueTraceVerified = true;
                _opsDirectMongoVerified = true;
                _opsCollectionDeltaVerified = true;
                return new CaseObservation(
                    "Cleanup apply soft-deleted the exact hashed candidate and its generation rows, protected current/reference jobs, replayed without writes, and rejected a stale hash without writes.",
                    $"candidate={candidateId};applied=1;generationRows={generationRows};liveRows=0;currentProtected=true;referencedProtected=true;replay=true/zeroWrite;stale=409/CLEANUP_CANDIDATE_SET_STALE/zeroWrite");
            },
            ct);
    }
}
