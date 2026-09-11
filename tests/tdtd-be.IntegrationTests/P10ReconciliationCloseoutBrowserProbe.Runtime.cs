using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private static readonly string[] CloseoutReviewGates =
    [
        "FORM",
        "FLOW",
        "ASSIGNMENT",
        "MAPPING",
        "STATISTICS"
    ];

    private async Task SeedCloseoutReviewersAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var users = database.GetCollection<AppUser>("users");
        var assignments = database.GetCollection<WorkAssignment>(
            "work_assignments");
        var fixedAt = DateTime.UtcNow;
        var adminId = Actor("admin").Id;
        var seeds = CloseoutReviewGates.Select((gate, index) => NewActor(
                "reviewer-" + gate.ToLowerInvariant(),
                $"p10_close_reviewer_{index + 1}",
                $"P10 Close Reviewer {gate}",
                Fixture().UnitAId,
                ["MANAGER_LEVEL"],
                adminId,
                fixedAt))
            .ToArray();
        var hasher = new PasswordHasher<AppUser>();
        foreach (var seed in seeds)
        {
            seed.User.PasswordHash = hasher.HashPassword(
                seed.User,
                RequireBackend().ActorPassword);
        }
        await users.InsertManyAsync(
            seeds.Select(seed => seed.User),
            cancellationToken: ct);
        var assignment = await assignments.UpdateOneAsync(
            value => value.Id == Fixture().ScopeAssignmentId,
            Builders<WorkAssignment>.Update.AddToSetEach(
                value => value.LeaderWatcherUserIds,
                seeds.Select(seed => seed.User.Id)),
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            assignment.MatchedCount,
            "P10-CLOSE reviewer assignment scope");

        foreach (var seed in seeds)
        {
            var token = await RequireApi().LoginAsync(
                seed.User.Username,
                RequireBackend().ActorPassword,
                ct);
            RememberSecret(token);
            _actors[seed.Key] = new P10Actor(
                seed.Key,
                seed.User.Id,
                seed.User.Username,
                seed.User.UnitId ?? string.Empty,
                seed.User.AccountKind ?? "NORMAL_USER",
                seed.User.Roles,
                token);
            _cleanupHandles.Add(new P10CleanupHandle(
                "users",
                seed.User.Id));
        }
    }

    private async Task<P10CloseoutPreparedFixture>
        PrepareCloseoutProductionFixtureAsync(CancellationToken ct)
        => await PrepareCloseoutCanonicalStateRegistryAsync(ct);

    private async Task<string> CreateCloseoutRunAsync(
        string commandId,
        CancellationToken ct,
        Action<JsonObject>? configureRequest = null)
    {
        var request = NewCreateRequest(commandId);
        request["conceptKey"] = Fixture().ConceptKey;
        configureRequest?.Invoke(request);
        var response = await CreateRunAsync(
            request,
            Actor("executor").Token,
            ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Accepted,
            $"P10-CLOSE create {commandId}");
        var id = RequireResponseString(response, "reconciliationId");
        _cleanupHandles.Add(new P10CleanupHandle(RunCollection, id));
        return id;
    }

    private string TrackCloseoutProductionExportArtifactDirectory(
        string exportRoot,
        string exportId)
    {
        var canonicalExportId = CloseoutRequiredObjectId(
            exportId,
            "production export id");
        var expectedRoot = Path.GetFullPath(Path.Combine(
                CloseoutWorkspacePath("tdtd-be"),
                ".build",
                "stat-run-exports",
                RequireMongo().DatabaseName))
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(exportRoot)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        HarnessAssert.Equal(
            expectedRoot,
            normalizedRoot,
            "P10-CLOSE production export storage root");

        var rootInfo = new DirectoryInfo(normalizedRoot);
        if (rootInfo.Exists &&
            rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "P10-CLOSE production export root is redirected.");
        }

        var artifactDirectory = Path.GetFullPath(Path.Combine(
                normalizedRoot,
                canonicalExportId))
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(artifactDirectory)?
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) ??
            throw new InvalidOperationException(
                "P10-CLOSE production export directory has no parent.");
        HarnessAssert.Equal(
            normalizedRoot,
            parent,
            "P10-CLOSE production export directory parent");

        var artifactInfo = new DirectoryInfo(artifactDirectory);
        if (artifactInfo.Exists &&
            artifactInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "P10-CLOSE production export directory is redirected.");
        }
        if (_closeoutProductionExportArtifactDirectory is not null)
        {
            HarnessAssert.Equal(
                _closeoutProductionExportArtifactDirectory,
                artifactDirectory,
                "P10-CLOSE production export directory must be stable");
        }
        _closeoutProductionExportArtifactDirectory = artifactDirectory;
        return artifactDirectory;
    }

    private void DeleteCloseoutProductionExportArtifactDirectory(
        List<string> cleanupErrors)
    {
        var trackedDirectory = _closeoutProductionExportArtifactDirectory;
        if (trackedDirectory is null)
            return;
        if (_backend is not { StopVerified: true, PortReleaseVerified: true })
        {
            cleanupErrors.Add(
                "export-artifact-cleanup: backend stop is not verified");
            return;
        }

        try
        {
            var expectedRoot = Path.GetFullPath(Path.Combine(
                    CloseoutWorkspacePath("tdtd-be"),
                    ".build",
                    "stat-run-exports",
                    RequireMongo().DatabaseName))
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var directory = Path.GetFullPath(trackedDirectory)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(directory)?
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) ??
                throw new InvalidOperationException(
                    "tracked export directory has no parent");
            if (!string.Equals(
                    parent,
                    expectedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "tracked export directory escaped the owned root");
            }
            var directoryName = Path.GetFileName(directory);
            if (!ObjectId.TryParse(directoryName, out var parsedId) ||
                !string.Equals(
                    parsedId.ToString(),
                    directoryName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "tracked export directory name is not canonical");
            }

            var rootInfo = new DirectoryInfo(expectedRoot);
            if (rootInfo.Exists &&
                rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "owned export root is redirected");
            }
            if (!Directory.Exists(directory))
                return;
            var directoryInfo = new DirectoryInfo(directory);
            if (directoryInfo.Attributes.HasFlag(
                    FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    "owned export directory is redirected");
            }

            var entries = Directory
                .EnumerateFileSystemEntries(directory)
                .ToArray();
            if (entries.Length > 1)
            {
                throw new InvalidOperationException(
                    "owned export directory contains unexpected entries");
            }
            foreach (var entry in entries)
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException(
                        "owned export entry is not a regular file");
                }
                var expectedArtifact =
                    _closeoutCaptureOwnerFixture?.ExportStoragePath;
                if (expectedArtifact is not null &&
                    !string.Equals(
                        Path.GetFullPath(entry),
                        Path.GetFullPath(expectedArtifact),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "owned export file does not match the tracked artifact");
                }
                File.Delete(entry);
            }
            Directory.Delete(directory, recursive: false);
            if (Directory.Exists(directory))
            {
                throw new InvalidOperationException(
                    "owned export directory still exists after cleanup");
            }
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(
                "export-artifact-cleanup: " + exception.Message);
        }
    }

    private async Task WriteCloseoutBrowserFixturesAsync(CancellationToken ct)
    {
        var prepared = _closeoutPrepared ?? throw new InvalidOperationException(
            "P10-CLOSE prepared fixture is unavailable.");
        var outputRoot = Path.Combine(_paths.RunRoot, "browser");
        Directory.CreateDirectory(outputRoot);
        var secretPath = Path.Combine(
            outputRoot,
            "fixture.runtime.secret.json");
        var redactedPath = Path.Combine(
            _paths.RunRoot,
            "P10-CLOSE.browser-fixture.json");
        var resultPath = Path.Combine(
            outputRoot,
            "browser-result.json");
        var stateRegistry = _closeoutStateRegistry ??
            throw new InvalidOperationException(
                "P10-CLOSE production state registry is unavailable.");
        _closeoutSecretPaths.Add(secretPath);

        object ActorNode(string key, bool secrets)
        {
            var actor = Actor(key);
            return new
            {
                actor.Key,
                userId = actor.Id,
                actor.Username,
                actor.UnitId,
                actor.AccountKind,
                roles = actor.Roles,
                token = secrets ? actor.Token : null
            };
        }

        object FixtureNode(bool secrets) => new
        {
            schemaVersion = "P10_CLOSE_BROWSER_FIXTURE_V1",
            chainId = ChainId,
            promptId = CloseoutPromptId,
            groupId = CloseoutGroupId,
            runKey = _runKey,
            containsSecrets = secrets,
            doNotPublish = secrets,
            outputRoot = Path.GetFullPath(outputRoot),
            resultPath = Path.GetFullPath(resultPath),
            backendOrigin = RequireBackend().BaseUri
                .GetLeftPart(UriPartial.Authority),
            tokenStorageKey = "tdtd_access_token",
            frontend = new
            {
                distRoot = Path.GetFullPath(
                    CloseoutWorkspacePath("tdtd-fe/dist")),
                productionBuild = _closeoutBuild?.ExitCode == 0,
                componentSuite = new
                {
                    exactCaseCount = 24,
                    passed = _closeoutComponents?.ExitCode == 0 &&
                        _closeoutComponents.StdOut.Contains(
                            "24 passed",
                            StringComparison.Ordinal)
                }
            },
            routes = new
            {
                list = $"/works/{Fixture().WorkId}/statistics/{Fixture().ScopeAssignmentId}/reconciliations",
                empty = $"/works/{Fixture().WorkId}/statistics/{Fixture().SiblingAssignmentId}/reconciliations",
                emptyApiBase = $"/api/works/{Fixture().WorkId}/statistics/{Fixture().SiblingAssignmentId}/reconciliations",
                detail = $"/works/{Fixture().WorkId}/statistics/{Fixture().ScopeAssignmentId}/reconciliations/{prepared.TargetReconciliationId}",
                apiBase = $"/api/works/{Fixture().WorkId}/statistics/{Fixture().ScopeAssignmentId}/reconciliations",
                outsiderDetail = $"/works/{Fixture().WorkId}/statistics/{Fixture().ScopeAssignmentId}/reconciliations/{prepared.TargetReconciliationId}"
            },
            target = new
            {
                reconciliationId = prepared.TargetReconciliationId,
                stateRevision = prepared.TargetStateRevision,
                status = prepared.TargetStatus
            },
            productionStateRegistry = new
            {
                expectedStatuses = stateRegistry.ExpectedStatuses,
                stateRegistry.Expected,
                stateRegistry.Passed,
                stateRegistry.DirectMongoAsserted,
                stateRegistry.EmptyScopeMongoCount,
                stateRegistry.RegistrySha256
            },
            stateMatrix = stateRegistry.Rows.Select(item => new
            {
                reconciliationId = item.ReconciliationId,
                scopeAssignmentId = item.ScopeAssignmentId,
                status = item.Status,
                source = item.FixturePath,
                stateRevision = item.StateRevision,
                reviewDecisionRevision = item.ReviewDecisionRevision,
                stateHash = item.StateHash,
                sourceReportId = item.SourceReportId,
                sourcePayloadRevision = item.SourcePayloadRevision,
                sourcePayloadHash = item.SourcePayloadHash,
                sourceLifecycleRevision = item.SourceLifecycleRevision,
                sourceLifecycleHash = item.SourceLifecycleHash
            }),
            requiredColumns = new[]
            {
                "Identity", "Config", "Expected", "Actual", "Delta",
                "Freshness", "Permission", "Verdict"
            },
            reviewGates = CloseoutReviewGates.Select((gate, index) => new
            {
                gate,
                actorKey = "reviewer-" + gate.ToLowerInvariant(),
                commandId = $"p10-close-review-{index + 1:00}-{gate.ToLowerInvariant()}"
            }),
            caseIds = CloseoutBrowserCaseIds,
            actors = new
            {
                admin = ActorNode("admin", secrets),
                executor = ActorNode("executor", secrets),
                outsider = ActorNode("outsider", secrets),
                reviewers = CloseoutReviewGates.Select(gate => ActorNode(
                    "reviewer-" + gate.ToLowerInvariant(),
                    secrets))
            }
        };

        var fixtureJsonOptions = new JsonSerializerOptions(
            EvidenceJson.Options)
        {
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };
        await WriteStrictJsonAsync(
            redactedPath,
            JsonSerializer.SerializeToElement(
                FixtureNode(false),
                fixtureJsonOptions),
            ct);
        await WriteStrictJsonAsync(
            secretPath,
            JsonSerializer.SerializeToElement(
                FixtureNode(true),
                fixtureJsonOptions),
            ct);
    }

    private P10CloseoutSourceFingerprint CaptureCloseoutSourceFingerprint()
        => P10TrustedSourceFingerprintScanner.Capture(
            _paths.WorkspaceRoot);

    private async Task<P10CloseoutDatabaseSnapshot>
        CaptureCloseoutDatabaseSnapshotAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var names = await (await database.ListCollectionNamesAsync(
                cancellationToken: ct))
            .ToListAsync(ct);
        var rows = new List<P10CloseoutCollectionState>();
        foreach (var name in names
                     .Where(name => !name.StartsWith(
                         "system.",
                         StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            var documents = await database.GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(new BsonDocument("_id", 1))
                .ToListAsync(ct);
            rows.Add(new P10CloseoutCollectionState(
                name,
                documents.Count,
                HashBytes(documents.SelectMany(document => document.ToBson())
                    .ToArray())));
        }
        var normalized = JsonSerializer.Serialize(rows, EvidenceJson.Options);
        return new P10CloseoutDatabaseSnapshot(
            rows,
            HashText(normalized));
    }

    private async Task<P10CloseoutBoundaryProof>
        CaptureCloseoutBoundaryProofAsync(CancellationToken ct)
    {
        var catalogPath = CloseoutWorkspacePath(
            "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.json");
        using var catalog = JsonDocument.Parse(
            await File.ReadAllBytesAsync(catalogPath, ct));
        var profiles = catalog.RootElement.GetProperty("domains")
            .GetProperty("statisticsCapabilities")
            .EnumerateArray()
            .Where(value => value.GetProperty("id").GetString() ==
                "FLOW_STATISTIC_PROFILE")
            .ToArray();
        var profileBlocked = profiles.Length == 1 &&
            profiles[0].GetProperty("status").GetString() ==
                "INTENTIONAL_BLOCK" &&
            profiles[0].GetProperty("targetPhase").ValueKind ==
                JsonValueKind.Null;

        var manifestPath = CloseoutWorkspacePath(
            "docs/features/p10-reconcile/FULL_P10_RECONCILE_PROMPT_MANIFEST.json");
        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(manifestPath, ct));
        var manifestRoot = manifest.RootElement;
        var finalPrompt = manifestRoot.GetProperty("finalPrompt").GetString()
            ?? string.Empty;
        var phase = manifestRoot.GetProperty("barrier")
            .GetProperty("currentPhase").GetInt32();
        var barrierExact = phase == 8 &&
            manifestRoot.GetProperty("barrier")
                .GetProperty("entry").GetString() == "P10_RECONCILE" &&
            manifestRoot.GetProperty("barrier")
                .GetProperty("targetPhase").GetInt32() == 10 &&
            finalPrompt == "P10-12";

        var successor = await CaptureCloseoutSuccessorBarrierAsync(
            barrierExact,
            ct);
        var p11Count = successor.P11MongoNamespaceCount;
        var p12Count = successor.P12MongoNamespaceCount;
        var p11ArtifactAbsent = successor.P11ArtifactRootAbsent;
        var p12ArtifactAbsent = successor.P12ArtifactRootAbsent;
        var p11P12Blocked = successor.Blocked;

        var prepared = _closeoutPrepared ?? throw new InvalidOperationException(
            "P10-CLOSE prepared fixture is unavailable.");
        var fixture = Fixture();
        var p9Read = await RequireApi().PostAsync(
            "api/work-report-field-statistics/summary",
            new
            {
                workId = fixture.WorkId,
                scopeType = "WORK",
                scopeId = fixture.WorkId,
                dynamicFormTemplateId = fixture.DynamicFormVersionId,
                periodInstanceKey = fixture.PeriodInstanceKey,
                page = 0,
                pageSize = 50,
                includeDrilldown = false
            },
            Actor("executor").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            p9Read,
            HttpStatusCode.OK,
            "P10-CLOSE post-restore P9 direct regression read");
        var p9Root = ApiHarnessClient.RequiredObject(
            p9Read.Json,
            "P10-CLOSE post-restore P9 direct regression read");
        var p9Metadata = p9Root["metadata"] as JsonObject;
        var p9Publications = p9Metadata?["publications"] as JsonArray;
        var p9Publication = p9Publications is { Count: 1 }
            ? p9Publications.OfType<JsonObject>().Single()
            : null;
        var p9Rows = p9Root["rows"] as JsonArray;
        HarnessAssert.True(
            p9Metadata?["state"]?.GetValue<string>() == "READY" &&
            p9Publication?["runId"]?.GetValue<string>() ==
                fixture.P9RunId &&
            p9Publication["generationId"]?.GetValue<string>() ==
                fixture.P9GenerationId &&
            p9Publication["generationHash"]?.GetValue<string>() ==
                fixture.P9GenerationHash &&
            p9Rows is { Count: > 0 },
            "P10-CLOSE post-restore P9 direct publication identity");
        var p9Document = await RequireDatabase()
            .GetCollection<BsonDocument>(
                "work_report_statistic_rebuild_jobs")
            .Find(new BsonDocument(
                "_id",
                ObjectId.Parse(fixture.P9RunId)))
            .SingleAsync(ct);
        var p9AfterSha256 = HashBytes(p9Document.ToBson());
        HarnessAssert.Equal(
            prepared.P9StateSha256,
            p9AfterSha256,
            "P10-CLOSE P9 source bytes changed");
        HarnessAssert.True(
            profileBlocked && p11P12Blocked,
            "P10-CLOSE profile/P11/P12 boundary proof failed");
        return new P10CloseoutBoundaryProof(
            profileBlocked,
            "INTENTIONAL_BLOCK",
            p11P12Blocked,
            phase,
            finalPrompt,
            p11Count,
            p12Count,
            p11ArtifactAbsent,
            p12ArtifactAbsent,
            true,
            (int)p9Read.StatusCode,
            prepared.P9StateSha256,
            p9AfterSha256);
    }
}

internal sealed record P10CloseoutWorkerFence(
    string WorkerId,
    string ClaimToken);

