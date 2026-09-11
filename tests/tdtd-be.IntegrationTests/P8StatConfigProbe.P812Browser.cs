using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] P812BrowserActorRoles =
    [
        "OWNER",
        "ISSUER",
        "REPORTER",
        "REVIEWER",
        "COORDINATOR",
        "SYSTEM_ADMIN",
        "OUTSIDER"
    ];

    private async Task RunP812BrowserFixtureAsync(
        P812BrowserFixtureOptions options,
        string runKey,
        CancellationToken ct)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        var secretPath = Path.Combine(
            _iterationRoot,
            "p8-12-browser-fixture.runtime.secret.json");
        var redactedPath = Path.Combine(
            _iterationRoot,
            "p8-12-browser-fixture.json");
        var lifecyclePath = Path.Combine(
            _iterationRoot,
            "p8-12-browser-fixture.lifecycle.json");
        var browserResultPath = Path.Combine(
            _iterationRoot,
            "p8-12-browser-result.json");
        var stopPath = Path.Combine(
            _iterationRoot,
            "p8-12-browser.stop");
        var oraclePath = Path.Combine(
            _iterationRoot,
            "p8-12-browser-direct-mongo-oracle.json");
        var cleanupPath = Path.Combine(
            _iterationRoot,
            "p8-12-browser-cleanup.json");
        var resultPath = Path.Combine(
            _iterationRoot,
            "p8-12-browser-fixture-result.json");
        var cleanupErrors = new List<string>();
        var readyAtUtc = DateTime.UtcNow;
        var expiresAtUtc = readyAtUtc.Add(options.Timeout);
        string? redactedSha256 = null;
        string? browserResultSha256 = null;
        string? oracleSha256 = null;
        string? stopReason = null;
        string? failure = null;
        var passed = false;

        try
        {
            var beforeConfigs = await CaptureP812ConfigStatesAsync(ct);
            var beforeResults = await CaptureP812SixResultStatesAsync(ct);
            RequireP812SixResultsEmpty(beforeResults, "before browser");
            RequireP812ActorMatrix();

            await EvidenceJson.WriteAsync(
                redactedPath,
                BuildP812BrowserManifest(
                    containsSecrets: false,
                    state: "READY",
                    options,
                    runKey,
                    nonce,
                    readyAtUtc,
                    expiresAtUtc,
                    browserResultPath,
                    stopPath,
                    redactedSha256: null,
                    stopReason: null,
                    secretDeleted: false),
                ct);
            redactedSha256 = await P812FileShaAsync(redactedPath, ct);
            await EvidenceJson.WriteAsync(
                secretPath,
                BuildP812BrowserManifest(
                    containsSecrets: true,
                    state: "READY",
                    options,
                    runKey,
                    nonce,
                    readyAtUtc,
                    expiresAtUtc,
                    browserResultPath,
                    stopPath,
                    redactedSha256,
                    stopReason: null,
                    secretDeleted: false),
                ct);

            Console.WriteLine(
                $"P8_BROWSER_FIXTURE_READY={Path.GetFullPath(secretPath)}");
            Console.WriteLine(
                $"P8_BROWSER_FIXTURE_REDACTED={Path.GetFullPath(redactedPath)}");
            Console.WriteLine(
                $"P8_BROWSER_FIXTURE_STOP_FILE={Path.GetFullPath(stopPath)}");
            Console.Out.Flush();

            P812BrowserValidation? browser = null;
            if (options.ReadinessOnly)
            {
                stopReason = "READINESS_ONLY";
            }
            else
            {
                stopReason = await WaitForP812BrowserStopAsync(
                    browserResultPath,
                    stopPath,
                    expiresAtUtc,
                    ct);
                HarnessAssert.Equal(
                    "STOP_FILE",
                    stopReason,
                    "P8-12 browser fixture expired before the result/stop handshake");
                browser = await ValidateP812BrowserResultAsync(
                    browserResultPath,
                    options,
                    runKey,
                    nonce,
                    ct);
                browserResultSha256 = browser.ResultSha256;
            }

            var afterConfigs = await CaptureP812ConfigStatesAsync(ct);
            var afterResults = await CaptureP812SixResultStatesAsync(ct);
            RequireP812ConfigUnchanged(beforeConfigs, afterConfigs);
            RequireP812SixResultsEmpty(afterResults, "after browser");
            RequireP812ResultStatesUnchanged(beforeResults, afterResults);
            var oracle = await BuildP812DirectMongoOracleAsync(
                runKey,
                nonce,
                beforeConfigs,
                afterConfigs,
                beforeResults,
                afterResults,
                browser,
                ct);
            await EvidenceJson.WriteAsync(oraclePath, oracle, ct);
            oracleSha256 = await P812FileShaAsync(oraclePath, ct);

            await EvidenceJson.WriteAsync(
                lifecyclePath,
                BuildP812BrowserManifest(
                    containsSecrets: false,
                    state: "STOPPED",
                    options,
                    runKey,
                    nonce,
                    readyAtUtc,
                    expiresAtUtc,
                    browserResultPath,
                    stopPath,
                    redactedSha256,
                    stopReason,
                    secretDeleted: true),
                ct);
            passed = true;
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            TryDeleteP812BrowserSecret(secretPath, cleanupErrors);
            TryDeleteP812BrowserSecret(stopPath, cleanupErrors);
            await EvidenceJson.WriteAsync(
                cleanupPath,
                new
                {
                    chainId = ChainId,
                    promptId = "P8-12",
                    runKey,
                    secretManifestDeleted = !File.Exists(secretPath),
                    stopFileDeleted = !File.Exists(stopPath),
                    browserResultPreserved = File.Exists(browserResultPath),
                    cleanupErrors,
                    completedAtUtc = DateTime.UtcNow
                },
                CancellationToken.None);
            await EvidenceJson.WriteAsync(
                resultPath,
                new
                {
                    chainId = ChainId,
                    promptId = "P8-12",
                    runKey,
                    nonce,
                    productionFrontend = new
                    {
                        origin = options.FrontendOrigin?.GetLeftPart(UriPartial.Authority),
                        options.FrontendBuildId,
                        options.FrontendSourceRevision
                    },
                    realKestrel = true,
                    isolatedMongoReplicaSet = true,
                    networkMockCount = 0,
                    actorCount = P812BrowserActorRoles.Length,
                    browserResultSha256,
                    oracleSha256,
                    redactedManifestSha256 = redactedSha256,
                    stopReason,
                    secretManifestDeleted = !File.Exists(secretPath),
                    cleanupErrors,
                    failure,
                    passed = passed && cleanupErrors.Count == 0
                },
                CancellationToken.None);
        }

        if (!passed || cleanupErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"P8-12 browser fixture failed: {failure ?? string.Join("; ", cleanupErrors)}");
        }
    }

    private object BuildP812BrowserManifest(
        bool containsSecrets,
        string state,
        P812BrowserFixtureOptions options,
        string runKey,
        string nonce,
        DateTime readyAtUtc,
        DateTime expiresAtUtc,
        string browserResultPath,
        string stopPath,
        string? redactedSha256,
        string? stopReason,
        bool secretDeleted)
    {
        var frontendOrigin = options.FrontendOrigin!
            .GetLeftPart(UriPartial.Authority);
        var backendOrigin = _backend.BaseUri.GetLeftPart(UriPartial.Authority);
        var routePrefix =
            $"{frontendOrigin}/works/{Uri.EscapeDataString(_p810Assignment.WorkId)}/" +
            $"statistics/{Uri.EscapeDataString(_p810Assignment.Id)}/config";
        return new
        {
            schemaVersion = 1,
            containsSecrets,
            doNotPublish = containsSecrets,
            chainId = ChainId,
            promptId = "P8-12",
            runKey,
            nonce,
            runRoot = Path.GetFullPath(_paths.RunRoot),
            artifactRoot = Path.GetFullPath(_iterationRoot),
            backendBaseUrl = backendOrigin,
            apiBaseUrl = $"{backendOrigin}/api",
            frontendOrigin,
            databaseName = _mongo.DatabaseName,
            productionFrontend = new
            {
                buildId = options.FrontendBuildId,
                sourceRevision = options.FrontendSourceRevision,
                builtAssetsRequired = !options.ReadinessOnly
            },
            browserSession = new
            {
                tokenStorage = "sessionStorage",
                tokenStorageKey = "tdtd_access_token",
                switchActorProtocol =
                    "Set the selected secret accessToken in sessionStorage, reload the canonical route, and never copy credentials into result artifacts."
            },
            fixture = new
            {
                workId = _p810Assignment.WorkId,
                scopeAssignmentId = _p810Assignment.Id,
                dynamicFormTemplateId = _p810BasicFixture.DynamicFormTemplateId,
                advancedSectionId = _p810AdvancedFixture.SectionId,
                readOnlyBrowserGate = true
            },
            routes = new
            {
                canonicalTemplate =
                    "/works/:workId/statistics/:scopeAssignmentId/config/:tab?",
                overview = $"{routePrefix}/overview",
                labelsForm = $"{routePrefix}/labels-form",
                basic = $"{routePrefix}/basic",
                advanced =
                    $"{routePrefix}/advanced?section={Uri.EscapeDataString(_p810AdvancedFixture.SectionId)}",
                diff = $"{routePrefix}/diff",
                readiness = $"{routePrefix}/readiness"
            },
            actors = BuildP812ManifestActors(containsSecrets),
            evidenceContract = new
            {
                schemaVersion = 1,
                verdict = "PASS",
                networkMockCount = 0,
                exactActorRoles = P812BrowserActorRoles,
                exactJourneyCount = 7,
                canonicalJourneyRoute = $"{routePrefix}/basic",
                screenshotMinimum = 2,
                screenshotPathsAbsolute = true,
                screenshotSha256Required = true,
                traceRequired = true,
                tracePathAbsolute = true,
                consoleErrorsMustBeEmpty = true,
                pageErrorsMustBeEmpty = true,
                networkErrorsMustBeEmpty = true,
                minimumRealApiRequests = 7,
                readOnlyBrowserGate = true,
                forbiddenActions = new[] { "Run", "Export", "Reconcile" },
                forbiddenActionVisibleCount = 0,
                forbiddenActionInvokedCount = 0,
                resultMustNotContainCredentials = true
            },
            lifecycle = new
            {
                state,
                readyAtUtc,
                expiresAtUtc,
                browserResultFile = Path.GetFullPath(browserResultPath),
                stopFile = Path.GetFullPath(stopPath),
                stopReason,
                secretManifestDeleted = secretDeleted,
                protocol =
                    "Write bound browser result JSON first, then atomically create the stop file."
            },
            handshake = new
            {
                runKey,
                nonce,
                readyManifestSha256 = redactedSha256,
                staleResultRejected = true
            }
        };
    }

    private object[] BuildP812ManifestActors(bool containsSecrets)
        => P810ActorDefinitions.Select(definition =>
        {
            var actor = Actor(definition.ActorKey);
            var matrix = _p810ActorMatrix.Single(row => string.Equals(
                row.Role,
                definition.Role,
                StringComparison.Ordinal));
            return (object)new
            {
                role = definition.Role,
                actorKey = containsSecrets ? actor.Key : "REDACTED",
                userId = containsSecrets ? actor.Id : "REDACTED",
                username = containsSecrets ? actor.Username : "REDACTED",
                password = containsSecrets
                    ? string.Equals(definition.Role, "SYSTEM_ADMIN", StringComparison.Ordinal)
                        ? _bootstrapDefaultPassword
                        : _backend.ActorPassword
                    : "REDACTED",
                accessToken = containsSecrets ? actor.Token : "REDACTED",
                expected = new
                {
                    matrix.KnownOwnerStatus,
                    matrix.CanReadConfig,
                    matrix.CanManageDraft,
                    matrix.CanLockVersion,
                    matrix.UnknownOwnerStatus,
                    matrix.NonLeakEquivalent
                }
            };
        }).ToArray();

    private async Task<P812BrowserValidation> ValidateP812BrowserResultAsync(
        string resultPath,
        P812BrowserFixtureOptions options,
        string runKey,
        string nonce,
        CancellationToken ct)
    {
        var raw = await File.ReadAllTextAsync(resultPath, ct);
        foreach (var secret in BuildP812BrowserSecrets())
        {
            HarnessAssert.True(
                !raw.Contains(secret, StringComparison.Ordinal),
                "P8-12 browser result echoed a credential");
        }
        using var document = JsonDocument.Parse(
            raw,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        var root = document.RootElement;
        HarnessAssert.Equal(1, P812RequiredInt(root, "schemaVersion"),
            "P8-12 browser result schemaVersion mismatch");
        HarnessAssert.Equal(runKey, P812RequiredString(root, "runKey"),
            "P8-12 browser result runKey mismatch");
        HarnessAssert.Equal(nonce, P812RequiredString(root, "nonce"),
            "P8-12 browser result nonce mismatch");
        HarnessAssert.Equal("PASS", P812RequiredString(root, "verdict"),
            "P8-12 browser result verdict mismatch");
        HarnessAssert.Equal(0, P812RequiredInt(root, "networkMockCount"),
            "P8-12 browser result used a network mock");
        HarnessAssert.True(P812RequiredInt(root, "apiRequestCount") >= 7,
            "P8-12 browser result lacks seven real API request observations");
        HarnessAssert.Equal(
            _backend.BaseUri.GetLeftPart(UriPartial.Authority),
            P812RequiredString(root, "observedApiOrigin"),
            "P8-12 browser result is not bound to the fixture Kestrel origin");
        HarnessAssert.Equal(
            options.FrontendOrigin!.GetLeftPart(UriPartial.Authority),
            P812RequiredString(root, "frontendOrigin"),
            "P8-12 browser result frontend origin mismatch");
        HarnessAssert.Equal(options.FrontendBuildId!,
            P812RequiredString(root, "frontendBuildId"),
            "P8-12 browser result build ID mismatch");
        HarnessAssert.Equal(options.FrontendSourceRevision!,
            P812RequiredString(root, "frontendSourceRevision"),
            "P8-12 browser result source revision mismatch");

        RequireP812EmptyArray(root, "consoleErrors");
        RequireP812EmptyArray(root, "pageErrors");
        RequireP812EmptyArray(root, "networkErrors");

        var forbidden = P812RequiredProperty(root, "forbiddenActions");
        HarnessAssert.Equal(0, P812RequiredInt(forbidden, "visibleCount"),
            "P8-12 exposed Run, Export or Reconcile");
        HarnessAssert.Equal(0, P812RequiredInt(forbidden, "invokedCount"),
            "P8-12 invoked Run, Export or Reconcile");

        var journeys = P812RequiredProperty(root, "journeys");
        HarnessAssert.True(journeys.ValueKind == JsonValueKind.Array,
            "P8-12 journeys must be an array");
        HarnessAssert.Equal(P812BrowserActorRoles.Length, journeys.GetArrayLength(),
            "P8-12 browser result must contain exactly seven actor journeys");
        var expectedStatus = _p810ActorMatrix.ToDictionary(
            row => row.Role,
            row => row.KnownOwnerStatus,
            StringComparer.Ordinal);
        var expectedRoute =
            $"{options.FrontendOrigin.GetLeftPart(UriPartial.Authority)}/works/" +
            $"{Uri.EscapeDataString(_p810Assignment.WorkId)}/statistics/" +
            $"{Uri.EscapeDataString(_p810Assignment.Id)}/config/basic";
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var journey in journeys.EnumerateArray())
        {
            var role = P812RequiredString(journey, "role");
            HarnessAssert.True(covered.Add(role),
                $"P8-12 browser result duplicated actor role {role}");
            HarnessAssert.True(expectedStatus.TryGetValue(role, out var status),
                $"P8-12 browser result contains unknown actor role {role}");
            HarnessAssert.Equal(status, P812RequiredInt(journey, "statusCode"),
                $"P8-12 {role} status mismatch");
            HarnessAssert.Equal(expectedRoute, P812RequiredString(journey, "route"),
                $"P8-12 {role} did not use the canonical Basic route");
            HarnessAssert.True(
                !string.IsNullOrWhiteSpace(P812RequiredString(journey, "surfaceState")),
                $"P8-12 {role} omitted surface state");
            HarnessAssert.Equal(0, P812RequiredInt(journey, "forbiddenActionCount"),
                $"P8-12 {role} exposed a forbidden action");
            HarnessAssert.True(P812RequiredInt(journey, "apiRequestCount") >= 1,
                $"P8-12 {role} lacks a real API observation");
        }
        HarnessAssert.True(P812BrowserActorRoles.All(covered.Contains),
            "P8-12 actor journey coverage is incomplete");

        var screenshots = P812RequiredProperty(root, "screenshots");
        HarnessAssert.True(screenshots.ValueKind == JsonValueKind.Array,
            "P8-12 screenshots must be an array");
        HarnessAssert.True(screenshots.GetArrayLength() >= 2,
            "P8-12 requires at least two screenshots");
        foreach (var screenshot in screenshots.EnumerateArray())
        {
            var path = P812ResolveArtifactFile(
                P812RequiredString(screenshot, "path"));
            var sha256 = P812RequiredString(screenshot, "sha256");
            HarnessAssert.True(P812IsLowerSha256(sha256),
                $"P8-12 screenshot SHA-256 is invalid: {path}");
            HarnessAssert.Equal(sha256, await P812FileShaAsync(path, ct),
                $"P8-12 screenshot SHA-256 mismatch: {path}");
            _ = P812RequiredString(screenshot, "viewport");
            _ = P812RequiredString(screenshot, "role");
        }

        var trace = P812RequiredProperty(root, "trace");
        var tracePath = P812ResolveArtifactFile(P812RequiredString(trace, "path"));
        var traceSha256 = P812RequiredString(trace, "sha256");
        HarnessAssert.True(P812IsLowerSha256(traceSha256),
            "P8-12 trace SHA-256 is invalid");
        HarnessAssert.Equal(traceSha256, await P812FileShaAsync(tracePath, ct),
            "P8-12 trace SHA-256 mismatch");
        return new P812BrowserValidation(
            await P812FileShaAsync(resultPath, ct),
            journeys.GetArrayLength(),
            screenshots.GetArrayLength(),
            tracePath,
            traceSha256,
            covered.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private async Task<IReadOnlyList<P812ConfigState>> CaptureP812ConfigStatesAsync(
        CancellationToken ct)
    {
        var basic = _p810BasicDraft
                    ?? throw new InvalidOperationException(
                        "P8-10 Basic browser config identity is missing.");
        var advanced = _p810AdvancedDraft
                       ?? throw new InvalidOperationException(
                           "P8-10 Advanced browser config identity is missing.");
        var diff = _p810DiffDraft
                   ?? throw new InvalidOperationException(
                       "P8-10 Diff browser config identity is missing.");
        var specs = new[]
        {
            new
            {
                Kind = "BASIC",
                Collection = BasicConfigsCollection,
                DocumentId = basic.ConfigId,
                ExpectedConfigId = basic.ConfigId,
                ExpectedHash = basic.ConfigHash
            },
            new
            {
                Kind = "ADVANCED",
                Collection = AdvancedConfigsCollection,
                DocumentId = advanced.VersionId,
                ExpectedConfigId = advanced.ConfigId,
                ExpectedHash = advanced.ConfigHash
            },
            new
            {
                Kind = "DIFF",
                Collection = DiffConfigsCollection,
                DocumentId = diff.VersionId,
                ExpectedConfigId = diff.ConfigId,
                ExpectedHash = diff.ConfigHash
            }
        };
        var states = new List<P812ConfigState>(specs.Length);
        foreach (var spec in specs)
        {
            var document = await _database.GetCollection<BsonDocument>(spec.Collection)
                .Find(new BsonDocument("_id", ObjectId.Parse(spec.DocumentId)))
                .SingleAsync(ct);
            var status = BsonString(document, "status");
            var configHash = BsonString(document, "configHash");
            var configId = BsonString(document, "configId") ??
                           BsonString(document, "_id");
            HarnessAssert.Equal("LOCKED", status,
                $"P8-12 {spec.Kind} config is not locked before/after browser");
            HarnessAssert.Equal(spec.ExpectedConfigId, configId,
                $"P8-12 {spec.Kind} configId drifted");
            HarnessAssert.Equal(spec.ExpectedHash, configHash,
                $"P8-12 {spec.Kind} configHash drifted");
            states.Add(new P812ConfigState(
                spec.Kind,
                spec.Collection,
                spec.DocumentId,
                configId!,
                BsonString(document, "versionId") ?? spec.DocumentId,
                BsonInt(document, "versionNo") ?? 0,
                BsonLong(document, "revision") ?? 0,
                status!,
                configHash!,
                Sha256(document.ToBson())));
        }
        return states;
    }

    private async Task<IReadOnlyList<P809ZeroWriteState>>
        CaptureP812SixResultStatesAsync(CancellationToken ct)
    {
        var all = await CaptureP809ZeroWriteInventoryAsync(ct);
        var expected = P809SixResultCollections.ToHashSet(StringComparer.Ordinal);
        return all.Where(row => expected.Contains(row.Collection))
            .OrderBy(row => row.Collection, StringComparer.Ordinal)
            .ToArray();
    }

    private static void RequireP812ConfigUnchanged(
        IReadOnlyList<P812ConfigState> before,
        IReadOnlyList<P812ConfigState> after)
    {
        HarnessAssert.Equal(before.Count, after.Count,
            "P8-12 config state count changed during browser gate");
        for (var index = 0; index < before.Count; index++)
        {
            HarnessAssert.Equal(before[index], after[index],
                $"P8-12 {before[index].Kind} config changed during read-only browser gate");
        }
    }

    private static void RequireP812SixResultsEmpty(
        IReadOnlyList<P809ZeroWriteState> rows,
        string context)
    {
        HarnessAssert.Equal(6, rows.Count,
            $"P8-12 six-result inventory mismatch {context}");
        foreach (var row in rows)
        {
            HarnessAssert.Equal(0L, row.Count,
                $"P8-12 {row.Collection} is not empty {context}");
        }
    }

    private static void RequireP812ResultStatesUnchanged(
        IReadOnlyList<P809ZeroWriteState> before,
        IReadOnlyList<P809ZeroWriteState> after)
    {
        HarnessAssert.True(before.SequenceEqual(after),
            "P8-12 browser gate changed a six-result collection");
    }

    private void RequireP812ActorMatrix()
    {
        HarnessAssert.Equal(P812BrowserActorRoles.Length, _p810ActorMatrix.Count,
            "P8-12 actor matrix is not exact seven");
        HarnessAssert.True(P812BrowserActorRoles.All(role =>
                _p810ActorMatrix.Any(row => string.Equals(
                    row.Role,
                    role,
                    StringComparison.Ordinal))),
            "P8-12 actor matrix role coverage drifted");
    }

    private async Task<object> BuildP812DirectMongoOracleAsync(
        string runKey,
        string nonce,
        IReadOnlyList<P812ConfigState> beforeConfigs,
        IReadOnlyList<P812ConfigState> afterConfigs,
        IReadOnlyList<P809ZeroWriteState> beforeResults,
        IReadOnlyList<P809ZeroWriteState> afterResults,
        P812BrowserValidation? browser,
        CancellationToken ct)
    {
        var assignment = await _database.GetCollection<WorkAssignment>("work_assignments")
            .Find(item => item.Id == _p810Assignment.Id && !item.IsDeleted)
            .SingleAsync(ct);
        var owner = Actor("p810_owner");
        var issuer = Actor("p810_issuer");
        var reporter = Actor("p810_reporter");
        var reviewer = Actor("p810_reviewer");
        var coordinator = Actor("p810_coordinator");
        var systemAdmin = Actor("system_admin");
        var outsider = Actor("p810_outsider");
        var ownerBound = assignment.CreatedByUserId == owner.Id;
        var issuerBound = assignment.IssuedByUnitId == issuer.UnitId &&
                          assignment.LeaderWatcherUserIds.Contains(
                              issuer.Id,
                              StringComparer.Ordinal);
        var reporterBound = assignment.Assignees.Any(item =>
            item.UserId == reporter.Id);
        var reviewerBound = assignment.LeaderWatcherUserIds.Contains(
            reviewer.Id,
            StringComparer.Ordinal);
        var coordinatorBound = assignment.LeaderWatcherUserIds.Contains(
            coordinator.Id,
            StringComparer.Ordinal);
        var systemAdminBound = assignment.LeaderWatcherUserIds.Contains(
            systemAdmin.Id,
            StringComparer.Ordinal);
        var outsiderAbsent = assignment.CreatedByUserId != outsider.Id &&
                             assignment.Assignees.All(item =>
                                 item.UserId != outsider.Id) &&
                             !assignment.LeaderWatcherUserIds.Contains(
                                 outsider.Id,
                                 StringComparer.Ordinal);
        HarnessAssert.True(
            ownerBound && issuerBound && reporterBound && reviewerBound &&
            coordinatorBound && systemAdminBound && outsiderAbsent,
            "P8-12 direct-Mongo actor relationship oracle failed");
        return new
        {
            schemaVersion = 1,
            chainId = ChainId,
            promptId = "P8-12",
            runKey,
            nonce,
            directMongo = true,
            configOracle = new
            {
                exactConfigCount = beforeConfigs.Count,
                before = beforeConfigs,
                after = afterConfigs,
                unchanged = beforeConfigs.SequenceEqual(afterConfigs),
                exactLocked = afterConfigs.All(row => row.Status == "LOCKED")
            },
            permissionOracle = new
            {
                assignmentId = assignment.Id,
                ownerBound,
                issuerBound,
                reporterBound,
                reviewerBound,
                coordinatorBound,
                systemAdminBound,
                outsiderAbsent,
                apiPermissionMatrix = _p810ActorMatrix
                    .OrderBy(row => row.Role, StringComparer.Ordinal)
                    .ToArray()
            },
            zeroResultOracle = new
            {
                exactCollectionCount = P809SixResultCollections.Length,
                exactCollections = P809SixResultCollections
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                before = beforeResults,
                after = afterResults,
                allCountsZero = afterResults.All(row => row.Count == 0),
                unchanged = beforeResults.SequenceEqual(afterResults)
            },
            browser = browser is null
                ? new
                {
                    readinessOnly = true,
                    journeyCount = 0,
                    screenshotCount = 0,
                    traceSha256 = (string?)null
                }
                : new
                {
                    readinessOnly = false,
                    journeyCount = browser.JourneyCount,
                    screenshotCount = browser.ScreenshotCount,
                    traceSha256 = (string?)browser.TraceSha256
                },
            passed = true
        };
    }

    private IReadOnlyList<string> BuildP812BrowserSecrets()
        => P810ActorDefinitions.SelectMany(definition =>
            {
                var actor = Actor(definition.ActorKey);
                var password = string.Equals(
                    definition.Role,
                    "SYSTEM_ADMIN",
                    StringComparison.Ordinal)
                    ? _bootstrapDefaultPassword
                    : _backend.ActorPassword;
                return new[] { password, actor.Token };
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private async Task<string> WaitForP812BrowserStopAsync(
        string resultPath,
        string stopPath,
        DateTime expiresAtUtc,
        CancellationToken ct)
    {
        while (DateTime.UtcNow < expiresAtUtc)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(stopPath))
            {
                HarnessAssert.True(File.Exists(resultPath),
                    "P8-12 stop file appeared before browser result");
                return "STOP_FILE";
            }
            await Task.Delay(150, ct);
        }
        return "TIMEOUT";
    }

    private string P812ResolveArtifactFile(string rawPath)
    {
        var path = Path.GetFullPath(rawPath);
        var prefix = Path.GetFullPath(_iterationRoot)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        HarnessAssert.True(
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path),
            $"P8-12 evidence path is absent or outside the iteration root: {path}");
        return path;
    }

    private static JsonElement P812RequiredProperty(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value))
            throw new InvalidOperationException($"P8-12 result omitted {name}.");
        return value;
    }

    private static string P812RequiredString(
        JsonElement element,
        string name)
    {
        var value = P812RequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"P8-12 result {name} must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static int P812RequiredInt(JsonElement element, string name)
    {
        var value = P812RequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw new InvalidOperationException(
                $"P8-12 result {name} must be an Int32.");
        }
        return result;
    }

    private static void RequireP812EmptyArray(
        JsonElement root,
        string name)
    {
        var value = P812RequiredProperty(root, name);
        HarnessAssert.True(
            value.ValueKind == JsonValueKind.Array &&
            value.GetArrayLength() == 0,
            $"P8-12 result {name} must be empty");
    }

    private static bool P812IsLowerSha256(string value)
        => value.Length == 64 && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static async Task<string> P812FileShaAsync(
        string path,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteP812BrowserSecret(
        string path,
        ICollection<string> errors)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception error)
        {
            errors.Add($"delete:{Path.GetFileName(path)}:{error.Message}");
        }
    }
}

internal sealed record P812BrowserFixtureOptions(
    bool Enabled,
    Uri? FrontendOrigin,
    TimeSpan Timeout,
    bool ReadinessOnly,
    string? FrontendBuildId,
    string? FrontendSourceRevision)
{
    private const int DefaultTimeoutSeconds = 1_800;

    public static P812BrowserFixtureOptions Parse(string[] args)
    {
        var enabled = args.Any(value => string.Equals(
            value,
            P8StatConfigProbe.BrowserFixtureSwitch,
            StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            return new P812BrowserFixtureOptions(
                false,
                null,
                TimeSpan.Zero,
                false,
                null,
                null);
        }

        string? origin = null;
        string? buildId = null;
        string? sourceRevision = null;
        var timeoutSeconds = DefaultTimeoutSeconds;
        var readinessOnly = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, P8StatConfigProbe.BrowserFixtureSwitch,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, P8StatConfigProbe.CommandLineSwitch,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(argument, "--through",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--p8-ui-component-oracle",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--p8-ui-browser-oracle",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length)
                    throw new ArgumentException($"{argument} requires a value.");
                continue;
            }
            if (string.Equals(argument, "--frontend-origin",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length)
                    throw new ArgumentException("--frontend-origin requires a value.");
                origin = args[index];
                continue;
            }
            if (string.Equals(argument,
                    "--p8-browser-fixture-timeout-seconds",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length ||
                    !int.TryParse(args[index], out timeoutSeconds))
                {
                    throw new ArgumentException(
                        "--p8-browser-fixture-timeout-seconds requires an integer.");
                }
                continue;
            }
            if (string.Equals(argument,
                    "--p8-browser-fixture-readiness-only",
                    StringComparison.OrdinalIgnoreCase))
            {
                readinessOnly = true;
                continue;
            }
            if (string.Equals(argument, "--frontend-build-id",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length)
                    throw new ArgumentException("--frontend-build-id requires a value.");
                buildId = args[index].Trim();
                continue;
            }
            if (string.Equals(argument, "--frontend-source-revision",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length)
                {
                    throw new ArgumentException(
                        "--frontend-source-revision requires a value.");
                }
                sourceRevision = args[index].Trim();
                continue;
            }
            throw new ArgumentException(
                $"Unknown/incomplete P8 browser fixture argument: {argument}");
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin) ||
            parsedOrigin.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(parsedOrigin.Host))
        {
            throw new ArgumentException(
                "--frontend-origin must be an absolute HTTP(S) origin.");
        }
        if (timeoutSeconds is < 1 or > 3_600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "P8 browser fixture timeout must be between 1 and 3600 seconds.");
        }
        if (!readinessOnly)
        {
            if (string.IsNullOrWhiteSpace(buildId))
                throw new ArgumentException("--frontend-build-id is required.");
            if (sourceRevision is null || !P812IsLowerSha256(sourceRevision))
            {
                throw new ArgumentException(
                    "--frontend-source-revision must be lowercase SHA-256.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(buildId) ||
                 !string.IsNullOrWhiteSpace(sourceRevision))
        {
            throw new ArgumentException(
                "Readiness-only P8 browser fixture rejects frontend build metadata.");
        }
        return new P812BrowserFixtureOptions(
            true,
            new Uri(
                parsedOrigin.GetLeftPart(UriPartial.Authority),
                UriKind.Absolute),
            TimeSpan.FromSeconds(timeoutSeconds),
            readinessOnly,
            buildId,
            sourceRevision);
    }

    private static bool P812IsLowerSha256(string value)
        => value.Length == 64 && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

internal sealed record P812BrowserValidation(
    string ResultSha256,
    int JourneyCount,
    int ScreenshotCount,
    string TracePath,
    string TraceSha256,
    IReadOnlyList<string> CoveredRoles);

internal sealed record P812ConfigState(
    string Kind,
    string Collection,
    string DocumentId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    string DocumentSha256);