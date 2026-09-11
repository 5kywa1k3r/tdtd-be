using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Strict, fail-closed parser for the non-secret P6-12 browser result.
///
/// The contract deliberately accepts expectations from the fixture instead of
/// trusting IDs, URLs, test IDs, actor coverage, or semantic labels supplied by
/// the browser runner. Referenced evidence artifacts must be regular files
/// beneath the isolated run root and must match their declared SHA-256.
/// </summary>
public static class P612BrowserEvidenceContract
{
    public const int SchemaVersion = 1;

    private const long MaxBrowserResultBytes = 4 * 1024 * 1024;
    private const long MaxTextArtifactBytes = 32 * 1024 * 1024;
    private const int MaxTraceArchiveEntries = 10_000;
    private const long MaxTraceArchiveEntryBytes = 64 * 1024 * 1024;
    private const long MaxTraceArchiveTotalBytes = 512 * 1024 * 1024;

    private static readonly string[] ExactJourneyIds =
        Enumerable.Range(1, 12).Select(value => $"T{value:00}").ToArray();

    private static readonly string[] ExactActorIds =
    [
        "owner",
        "coordinator",
        "reporter",
        "reviewer",
        "outsider"
    ];

    private static readonly string[] ExactViewports = ["desktop", "mobile"];

    /// <summary>
    /// Parses and validates one completed browser result plus every referenced
    /// screenshot, trace, and production frontend manifest.
    ///
    /// Returns only after the JSON shape, fixture bindings, coverage, artifact
    /// paths, hashes, and textual secret scan all pass. Any mismatch throws
    /// <see cref="P612BrowserEvidenceContractException"/>.
    /// </summary>
    public static async Task<P612BrowserEvidenceValidationResult>
        ParseAndValidateFileAsync(
            string browserResultPath,
            P612BrowserEvidenceExpectations expectations,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        var expected = ValidateExpectations(expectations);
        var claimedArtifactPaths = new HashSet<string>(PathComparer);
        var resultPath = ResolveExistingFileWithinRunRoot(
            expected.RunRoot,
            browserResultPath,
            "$resultFile");
        claimedArtifactPaths.Add(resultPath);

        var resultInfo = new FileInfo(resultPath);
        Require(
            resultInfo.Length is > 0 and <= MaxBrowserResultBytes,
            "$resultFile",
            $"browser result must contain 1..{MaxBrowserResultBytes} bytes.");

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(
                resultPath,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException or
            DecoderFallbackException)
        {
            throw Failure(
                "$resultFile",
                $"browser result could not be read: {error.Message}",
                error);
        }

        RequireNoForbiddenSecrets(
            raw,
            expected.ForbiddenSecrets,
            "$resultFile");
        var resultSha256 = await ComputeSha256Async(
            resultPath,
            cancellationToken);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
        }
        catch (JsonException error)
        {
            throw Failure(
                "$",
                $"browser result is not strict JSON: {error.Message}",
                error);
        }

        using (document)
        {
            var root = ReadExactObject(
                document.RootElement,
                "$",
                "schemaVersion",
                "runKey",
                "nonce",
                "readyManifestSha256",
                "verdict",
                "networkMockCount",
                "journeys",
                "actorCoverage",
                "ui",
                "reliability",
                "telemetry",
                "productionFrontend",
                "artifacts");

            RequireExactInteger(
                root["schemaVersion"],
                SchemaVersion,
                "$.schemaVersion");
            RequireExactString(
                root["runKey"],
                expected.RunKey,
                "$.runKey");
            RequireExactString(
                root["nonce"],
                expected.Nonce,
                "$.nonce");
            RequireExactString(
                root["readyManifestSha256"],
                expected.ReadyManifestSha256,
                "$.readyManifestSha256");
            RequireExactString(
                root["verdict"],
                "PASS",
                "$.verdict");
            RequireExactInteger(
                root["networkMockCount"],
                0,
                "$.networkMockCount");

            ValidateJourneys(root["journeys"], expected);
            ValidateActorCoverage(root["actorCoverage"], expected);
            ValidateUi(root["ui"]);
            ValidateReliability(root["reliability"], expected);
            ValidateTelemetry(root["telemetry"]);

            var frontendArtifact = ValidateProductionFrontend(
                root["productionFrontend"],
                expected);
            var browserArtifacts = ValidateArtifactShape(root["artifacts"]);

            var artifactHashes = new Dictionary<string, string>(PathComparer);
            await ValidateArtifactFileAsync(
                expected,
                frontendArtifact.Path,
                frontendArtifact.Sha256,
                "$.productionFrontend.buildManifestPath",
                ArtifactKind.FrontendManifest,
                claimedArtifactPaths,
                artifactHashes,
                cancellationToken);

            foreach (var screenshot in browserArtifacts.Screenshots)
            {
                await ValidateArtifactFileAsync(
                    expected,
                    screenshot.Path,
                    screenshot.Sha256,
                    $"$.artifacts.screenshots[{screenshot.Index}].path",
                    ArtifactKind.Screenshot,
                    claimedArtifactPaths,
                    artifactHashes,
                    cancellationToken);
            }

            foreach (var trace in browserArtifacts.Traces)
            {
                await ValidateArtifactFileAsync(
                    expected,
                    trace.Path,
                    trace.Sha256,
                    $"$.artifacts.traces[{trace.Index}].path",
                    ArtifactKind.Trace,
                    claimedArtifactPaths,
                    artifactHashes,
                    cancellationToken);
            }

            return new P612BrowserEvidenceValidationResult(
                ResultPath: resultPath,
                ResultSha256: resultSha256,
                RunKey: expected.RunKey,
                ReadyManifestSha256: expected.ReadyManifestSha256,
                ProductionFrontendOrigin: expected.ProductionFrontendOrigin,
                JourneyCount: ExactJourneyIds.Length,
                ActorCount: ExactActorIds.Length,
                ScreenshotCount: browserArtifacts.Screenshots.Count,
                TraceCount: browserArtifacts.Traces.Count,
                ArtifactSha256ByFullPath:
                    new Dictionary<string, string>(
                        artifactHashes,
                        PathComparer));
        }
    }

    private static ExpectedContract ValidateExpectations(
        P612BrowserEvidenceExpectations expectations)
    {
        var runRoot = RequireExistingRunRoot(expectations.RunRoot);
        var runKey = RequireExpectationToken(
            expectations.RunKey,
            "$expectations.runKey");
        var nonce = RequireExpectationToken(
            expectations.Nonce,
            "$expectations.nonce");
        Require(
            nonce.Length >= 16,
            "$expectations.nonce",
            "nonce must contain at least 16 characters.");
        var readyManifestSha256 = RequireValidSha256(
            expectations.ReadyManifestSha256,
            "$expectations.readyManifestSha256");
        var productionFrontendOrigin = NormalizeHttpOrigin(
            expectations.ProductionFrontendOrigin,
            "$expectations.productionFrontendOrigin");
        var productionApiBaseUrl = NormalizeHttpApiBaseUrl(
            expectations.ProductionApiBaseUrl,
            "$expectations.productionApiBaseUrl");
        var productionFrontendBuildId = RequireExpectationToken(
            expectations.ProductionFrontendBuildId,
            "$expectations.productionFrontendBuildId");
        var productionFrontendSourceRevision = RequireExpectationToken(
            expectations.ProductionFrontendSourceRevision,
            "$expectations.productionFrontendSourceRevision");

        var expectedJourneys = expectations.Journeys ??
            throw Failure(
                "$expectations.journeys",
                "expected journeys are required.");
        Require(
            expectedJourneys.Count == ExactJourneyIds.Length,
            "$expectations.journeys",
            "exactly twelve expected journeys are required.");
        var journeys = new Dictionary<string, P612ExpectedJourneyEvidence>(
            StringComparer.Ordinal);
        var observedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var journey in expectedJourneys)
        {
            if (journey is null)
            {
                throw Failure(
                    "$expectations.journeys",
                    "expected journey cannot be null.");
            }
            var path = $"$expectations.journeys[{journeys.Count}]";
            Require(
                ExactJourneyIds.Contains(
                    journey.JourneyId,
                    StringComparer.Ordinal),
                $"{path}.journeyId",
                "journeyId must be one of T01..T12.");
            Require(
                journeys.TryAdd(journey.JourneyId, journey),
                $"{path}.journeyId",
                $"duplicate expected journey '{journey.JourneyId}'.");
            RequireNonBlank(
                journey.ObservedId,
                $"{path}.observedId");
            Require(
                observedIds.Add(journey.ObservedId),
                $"{path}.observedId",
                $"duplicate expected observedId for '{journey.JourneyId}'.");
            Require(
                ExactActorIds.Contains(
                    journey.Actor,
                    StringComparer.Ordinal),
                $"{path}.actor",
                "actor must be one of the five canonical P6-12 actors.");
            ValidateExactFrontendUrl(
                journey.ExactUrl,
                productionFrontendOrigin,
                $"{path}.exactUrl");
            ValidateExpectedStringList(
                journey.RequiredTestIds,
                $"{path}.requiredTestIds");
            ValidateExpectedStringList(
                journey.StateSemantics,
                $"{path}.stateSemantics");
        }
        RequireExactSet(
            journeys.Keys,
            ExactJourneyIds,
            "$expectations.journeys");

        var expectedActorCoverage = expectations.ActorCoverage ??
            throw Failure(
                "$expectations.actorCoverage",
                "expected actor coverage is required.");
        Require(
            expectedActorCoverage.Count == ExactActorIds.Length,
            "$expectations.actorCoverage",
            "exactly five expected actor coverage rows are required.");
        var actors = new Dictionary<string, P612ExpectedActorCoverage>(
            StringComparer.Ordinal);
        var actorJourneyUnion = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in expectedActorCoverage)
        {
            if (actor is null)
            {
                throw Failure(
                    "$expectations.actorCoverage",
                    "expected actor coverage cannot be null.");
            }
            var path = $"$expectations.actorCoverage[{actors.Count}]";
            Require(
                ExactActorIds.Contains(actor.Actor, StringComparer.Ordinal),
                $"{path}.actor",
                "actor must be one of the five canonical P6-12 actors.");
            Require(
                actors.TryAdd(actor.Actor, actor),
                $"{path}.actor",
                $"duplicate expected actor '{actor.Actor}'.");
            ValidateExpectedJourneyList(
                actor.CoveredJourneyIds,
                $"{path}.coveredJourneyIds",
                requireNonEmpty: true);
            foreach (var journeyId in actor.CoveredJourneyIds)
                actorJourneyUnion.Add(journeyId);
        }
        RequireExactSet(
            actors.Keys,
            ExactActorIds,
            "$expectations.actorCoverage");
        RequireExactSet(
            actorJourneyUnion,
            ExactJourneyIds,
            "$expectations.actorCoverage.coveredJourneyIds");
        foreach (var journey in journeys.Values)
        {
            Require(
                actors[journey.Actor].CoveredJourneyIds.Contains(
                    journey.JourneyId,
                    StringComparer.Ordinal),
                "$expectations.actorCoverage",
                $"actor '{journey.Actor}' does not cover expected journey '{journey.JourneyId}'.");
        }

        Require(
            ExactJourneyIds.Contains(
                expectations.RetryJourneyId,
                StringComparer.Ordinal),
            "$expectations.retryJourneyId",
            "retryJourneyId must be one of T01..T12.");
        Require(
            ExactJourneyIds.Contains(
                expectations.ConflictJourneyId,
                StringComparer.Ordinal),
            "$expectations.conflictJourneyId",
            "conflictJourneyId must be one of T01..T12.");

        var forbiddenSecrets = new List<string>();
        var seenSecrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secret in expectations.ForbiddenSecrets ??
                               Array.Empty<string>())
        {
            RequireNonBlank(secret, "$expectations.forbiddenSecrets");
            Require(
                seenSecrets.Add(secret),
                "$expectations.forbiddenSecrets",
                "forbidden secrets must not contain duplicates.");
            forbiddenSecrets.Add(secret);
        }

        return new ExpectedContract(
            RunRoot: runRoot,
            RunKey: runKey,
            Nonce: nonce,
            ReadyManifestSha256: readyManifestSha256,
            ProductionFrontendOrigin: productionFrontendOrigin,
            ProductionApiBaseUrl: productionApiBaseUrl,
            ProductionFrontendBuildId: productionFrontendBuildId,
            ProductionFrontendSourceRevision:
                productionFrontendSourceRevision,
            RetryJourneyId: expectations.RetryJourneyId,
            ConflictJourneyId: expectations.ConflictJourneyId,
            Journeys: journeys,
            ActorCoverage: actors,
            ForbiddenSecrets: forbiddenSecrets);
    }

    private static void ValidateJourneys(
        JsonElement value,
        ExpectedContract expected)
    {
        RequireKind(value, JsonValueKind.Array, "$.journeys");
        Require(
            value.GetArrayLength() == ExactJourneyIds.Length,
            "$.journeys",
            "exactly twelve journey checks are required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var path = $"$.journeys[{index}]";
            var row = ReadExactObject(
                item,
                path,
                "journeyId",
                "observedId",
                "actor",
                "exactUrl",
                "requiredTestIdsFound",
                "stateSemantics",
                "refreshPassed",
                "backNavigationPassed");
            var journeyId = ReadString(row["journeyId"], $"{path}.journeyId");
            Require(
                expected.Journeys.TryGetValue(
                    journeyId,
                    out var expectedJourney),
                $"{path}.journeyId",
                $"unexpected journey '{journeyId}'.");
            Require(
                seen.Add(journeyId),
                $"{path}.journeyId",
                $"duplicate journey '{journeyId}'.");
            RequireExactString(
                row["observedId"],
                expectedJourney!.ObservedId,
                $"{path}.observedId");
            RequireExactString(
                row["actor"],
                expectedJourney.Actor,
                $"{path}.actor");
            RequireExactString(
                row["exactUrl"],
                expectedJourney.ExactUrl,
                $"{path}.exactUrl");
            RequireExactStringArray(
                row["requiredTestIdsFound"],
                expectedJourney.RequiredTestIds,
                $"{path}.requiredTestIdsFound");
            RequireExactStringArray(
                row["stateSemantics"],
                expectedJourney.StateSemantics,
                $"{path}.stateSemantics");
            RequireExactBoolean(
                row["refreshPassed"],
                expected: true,
                $"{path}.refreshPassed");
            RequireExactBoolean(
                row["backNavigationPassed"],
                expected: true,
                $"{path}.backNavigationPassed");
            index++;
        }
        RequireExactSet(seen, ExactJourneyIds, "$.journeys");
    }

    private static void ValidateActorCoverage(
        JsonElement value,
        ExpectedContract expected)
    {
        RequireKind(value, JsonValueKind.Array, "$.actorCoverage");
        Require(
            value.GetArrayLength() == ExactActorIds.Length,
            "$.actorCoverage",
            "exactly five actor coverage rows are required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var path = $"$.actorCoverage[{index}]";
            var row = ReadExactObject(
                item,
                path,
                "actor",
                "coveredJourneyIds",
                "coveragePassed",
                "outsiderNegativeHiddenNonLeakingPassed");
            var actor = ReadString(row["actor"], $"{path}.actor");
            Require(
                expected.ActorCoverage.TryGetValue(
                    actor,
                    out var expectedActor),
                $"{path}.actor",
                $"unexpected actor '{actor}'.");
            Require(
                seen.Add(actor),
                $"{path}.actor",
                $"duplicate actor '{actor}'.");
            RequireExactStringArray(
                row["coveredJourneyIds"],
                expectedActor!.CoveredJourneyIds,
                $"{path}.coveredJourneyIds");
            RequireExactBoolean(
                row["coveragePassed"],
                expected: true,
                $"{path}.coveragePassed");
            RequireExactBoolean(
                row["outsiderNegativeHiddenNonLeakingPassed"],
                expected: string.Equals(
                    actor,
                    "outsider",
                    StringComparison.Ordinal),
                $"{path}.outsiderNegativeHiddenNonLeakingPassed");
            index++;
        }
        RequireExactSet(seen, ExactActorIds, "$.actorCoverage");
    }

    private static void ValidateUi(JsonElement value)
    {
        var ui = ReadExactObject(
            value,
            "$.ui",
            "desktop",
            "mobile",
            "keyboard",
            "focus",
            "aria",
            "responsive");
        foreach (var key in new[]
                 {
                     "desktop",
                     "mobile",
                     "keyboard",
                     "focus",
                     "aria",
                     "responsive"
                 })
        {
            RequireExactBoolean(
                ui[key],
                expected: true,
                $"$.ui.{key}");
        }
    }

    private static void ValidateReliability(
        JsonElement value,
        ExpectedContract expected)
    {
        var reliability = ReadExactObject(
            value,
            "$.reliability",
            "retryJourneyId",
            "retryPassed",
            "conflictJourneyId",
            "conflictPassed");
        RequireExactString(
            reliability["retryJourneyId"],
            expected.RetryJourneyId,
            "$.reliability.retryJourneyId");
        RequireExactBoolean(
            reliability["retryPassed"],
            expected: true,
            "$.reliability.retryPassed");
        RequireExactString(
            reliability["conflictJourneyId"],
            expected.ConflictJourneyId,
            "$.reliability.conflictJourneyId");
        RequireExactBoolean(
            reliability["conflictPassed"],
            expected: true,
            "$.reliability.conflictPassed");
    }

    private static void ValidateTelemetry(JsonElement value)
    {
        var telemetry = ReadExactObject(
            value,
            "$.telemetry",
            "unexpectedConsoleErrorCount",
            "unexpectedNetworkErrorCount");
        RequireExactInteger(
            telemetry["unexpectedConsoleErrorCount"],
            0,
            "$.telemetry.unexpectedConsoleErrorCount");
        RequireExactInteger(
            telemetry["unexpectedNetworkErrorCount"],
            0,
            "$.telemetry.unexpectedNetworkErrorCount");
    }

    private static ArtifactReference ValidateProductionFrontend(
        JsonElement value,
        ExpectedContract expected)
    {
        var frontend = ReadExactObject(
            value,
            "$.productionFrontend",
            "mode",
            "origin",
            "buildId",
            "sourceRevision",
            "buildManifestPath",
            "buildManifestSha256");
        RequireExactString(
            frontend["mode"],
            "production",
            "$.productionFrontend.mode");
        var origin = NormalizeHttpOrigin(
            ReadString(
                frontend["origin"],
                "$.productionFrontend.origin"),
            "$.productionFrontend.origin");
        Require(
            string.Equals(
                origin,
                expected.ProductionFrontendOrigin,
                StringComparison.Ordinal),
            "$.productionFrontend.origin",
            $"expected '{expected.ProductionFrontendOrigin}', got '{origin}'.");
        RequireExactString(
            frontend["buildId"],
            expected.ProductionFrontendBuildId,
            "$.productionFrontend.buildId");
        RequireExactString(
            frontend["sourceRevision"],
            expected.ProductionFrontendSourceRevision,
            "$.productionFrontend.sourceRevision");
        var path = ReadString(
            frontend["buildManifestPath"],
            "$.productionFrontend.buildManifestPath");
        var sha256 = RequireValidSha256(
            ReadString(
                frontend["buildManifestSha256"],
                "$.productionFrontend.buildManifestSha256"),
            "$.productionFrontend.buildManifestSha256");
        return new ArtifactReference(path, sha256);
    }

    private static BrowserArtifacts ValidateArtifactShape(JsonElement value)
    {
        var artifacts = ReadExactObject(
            value,
            "$.artifacts",
            "screenshots",
            "traces");
        var screenshotsElement = artifacts["screenshots"];
        RequireKind(
            screenshotsElement,
            JsonValueKind.Array,
            "$.artifacts.screenshots");
        Require(
            screenshotsElement.GetArrayLength() ==
            ExactJourneyIds.Length * ExactViewports.Length,
            "$.artifacts.screenshots",
            "exactly one desktop and one mobile screenshot per journey are required.");
        var screenshots = new List<ScreenshotArtifact>();
        var screenshotCoverage = new HashSet<string>(StringComparer.Ordinal);
        var screenshotIndex = 0;
        foreach (var item in screenshotsElement.EnumerateArray())
        {
            var path = $"$.artifacts.screenshots[{screenshotIndex}]";
            var row = ReadExactObject(
                item,
                path,
                "journeyId",
                "viewport",
                "path",
                "sha256");
            var journeyId = ReadString(
                row["journeyId"],
                $"{path}.journeyId");
            Require(
                ExactJourneyIds.Contains(
                    journeyId,
                    StringComparer.Ordinal),
                $"{path}.journeyId",
                "journeyId must be one of T01..T12.");
            var viewport = ReadString(
                row["viewport"],
                $"{path}.viewport");
            Require(
                ExactViewports.Contains(
                    viewport,
                    StringComparer.Ordinal),
                $"{path}.viewport",
                "viewport must be exactly 'desktop' or 'mobile'.");
            Require(
                screenshotCoverage.Add($"{journeyId}\n{viewport}"),
                path,
                $"duplicate screenshot coverage for {journeyId}/{viewport}.");
            screenshots.Add(
                new ScreenshotArtifact(
                    Index: screenshotIndex,
                    JourneyId: journeyId,
                    Viewport: viewport,
                    Path: ReadString(row["path"], $"{path}.path"),
                    Sha256: RequireValidSha256(
                        ReadString(row["sha256"], $"{path}.sha256"),
                        $"{path}.sha256")));
            screenshotIndex++;
        }
        var expectedScreenshotCoverage = ExactJourneyIds.SelectMany(
            journeyId => ExactViewports.Select(
                viewport => $"{journeyId}\n{viewport}"));
        RequireExactSet(
            screenshotCoverage,
            expectedScreenshotCoverage,
            "$.artifacts.screenshots");

        var tracesElement = artifacts["traces"];
        RequireKind(
            tracesElement,
            JsonValueKind.Array,
            "$.artifacts.traces");
        Require(
            tracesElement.GetArrayLength() > 0,
            "$.artifacts.traces",
            "at least one browser trace is required.");
        var traces = new List<TraceArtifact>();
        var traceNames = new HashSet<string>(StringComparer.Ordinal);
        var traceJourneyCoverage = new HashSet<string>(StringComparer.Ordinal);
        var traceIndex = 0;
        foreach (var item in tracesElement.EnumerateArray())
        {
            var path = $"$.artifacts.traces[{traceIndex}]";
            var row = ReadExactObject(
                item,
                path,
                "name",
                "journeyIds",
                "path",
                "sha256");
            var name = ReadString(row["name"], $"{path}.name");
            Require(
                traceNames.Add(name),
                $"{path}.name",
                $"duplicate trace name '{name}'.");
            var journeyIds = ReadStringArray(
                row["journeyIds"],
                $"{path}.journeyIds",
                requireNonEmpty: true);
            foreach (var journeyId in journeyIds)
            {
                Require(
                    ExactJourneyIds.Contains(
                        journeyId,
                        StringComparer.Ordinal),
                    $"{path}.journeyIds",
                    $"unexpected journeyId '{journeyId}'.");
                traceJourneyCoverage.Add(journeyId);
            }
            traces.Add(
                new TraceArtifact(
                    Index: traceIndex,
                    Name: name,
                    JourneyIds: journeyIds,
                    Path: ReadString(row["path"], $"{path}.path"),
                    Sha256: RequireValidSha256(
                        ReadString(row["sha256"], $"{path}.sha256"),
                        $"{path}.sha256")));
            traceIndex++;
        }
        RequireExactSet(
            traceJourneyCoverage,
            ExactJourneyIds,
            "$.artifacts.traces[*].journeyIds");
        return new BrowserArtifacts(screenshots, traces);
    }

    private static async Task ValidateArtifactFileAsync(
        ExpectedContract expected,
        string claimedPath,
        string claimedSha256,
        string contractPath,
        ArtifactKind kind,
        ISet<string> claimedArtifactPaths,
        IDictionary<string, string> artifactHashes,
        CancellationToken cancellationToken)
    {
        var fullPath = ResolveExistingFileWithinRunRoot(
            expected.RunRoot,
            claimedPath,
            contractPath);
        Require(
            claimedArtifactPaths.Add(fullPath),
            contractPath,
            $"artifact path is referenced more than once: '{claimedPath}'.");
        ValidateArtifactExtension(fullPath, kind, contractPath);
        var info = new FileInfo(fullPath);
        Require(
            info.Length > 0,
            contractPath,
            "artifact file must not be empty.");
        var actualSha256 = await ComputeSha256Async(
            fullPath,
            cancellationToken);
        Require(
            string.Equals(
                actualSha256,
                claimedSha256,
                StringComparison.Ordinal),
            contractPath,
            $"artifact SHA-256 mismatch; expected {claimedSha256}, got {actualSha256}.");

        if (kind == ArtifactKind.FrontendManifest ||
            IsTextualArtifact(fullPath))
        {
            Require(
                info.Length <= MaxTextArtifactBytes,
                contractPath,
                $"textual artifact exceeds {MaxTextArtifactBytes} bytes.");
            string text;
            try
            {
                text = await File.ReadAllTextAsync(
                    fullPath,
                    Encoding.UTF8,
                    cancellationToken);
            }
            catch (Exception error) when (
                error is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException)
            {
                throw Failure(
                    contractPath,
                    $"textual artifact could not be read: {error.Message}",
                    error);
            }
            RequireNoForbiddenSecrets(
                text,
                expected.ForbiddenSecrets,
                contractPath);
            if (kind == ArtifactKind.FrontendManifest)
            {
                ValidateFrontendBuildManifest(
                    text,
                    expected,
                    contractPath);
            }
        }
        else if (kind == ArtifactKind.Trace &&
                 string.Equals(
                     Path.GetExtension(fullPath),
                     ".zip",
                     StringComparison.OrdinalIgnoreCase))
        {
            await RequireTraceArchiveHasNoForbiddenSecretsAsync(
                fullPath,
                expected.ForbiddenSecrets,
                contractPath,
                cancellationToken);
        }
        artifactHashes.Add(fullPath, actualSha256);
    }

    private static void ValidateFrontendBuildManifest(
        string raw,
        ExpectedContract expected,
        string contractPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
        }
        catch (JsonException error)
        {
            throw Failure(
                contractPath,
                $"production frontend build manifest is not strict JSON: {error.Message}",
                error);
        }
        using (document)
        {
            RequireKind(
                document.RootElement,
                JsonValueKind.Object,
                contractPath);
            var root = new Dictionary<string, JsonElement>(
                StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                Require(
                    root.TryAdd(property.Name, property.Value),
                    contractPath,
                    $"production frontend build manifest has duplicate root property '{property.Name}'.");
            }
            foreach (var key in new[]
                     {
                         "schemaVersion",
                         "mode",
                         "buildId",
                         "sourceRevision",
                         "frontendOrigin",
                         "apiBaseUrl"
                     })
            {
                Require(
                    root.ContainsKey(key),
                    contractPath,
                    $"production frontend build manifest is missing '{key}'.");
            }
            RequireExactInteger(
                root["schemaVersion"],
                1,
                $"{contractPath}.schemaVersion");
            RequireExactString(
                root["mode"],
                "production",
                $"{contractPath}.mode");
            RequireExactString(
                root["buildId"],
                expected.ProductionFrontendBuildId,
                $"{contractPath}.buildId");
            RequireExactString(
                root["sourceRevision"],
                expected.ProductionFrontendSourceRevision,
                $"{contractPath}.sourceRevision");
            var origin = NormalizeHttpOrigin(
                ReadString(
                    root["frontendOrigin"],
                    $"{contractPath}.frontendOrigin"),
                $"{contractPath}.frontendOrigin");
            Require(
                string.Equals(
                    origin,
                    expected.ProductionFrontendOrigin,
                    StringComparison.Ordinal),
                $"{contractPath}.frontendOrigin",
                $"expected '{expected.ProductionFrontendOrigin}', got '{origin}'.");
            var apiBaseUrl = NormalizeHttpApiBaseUrl(
                ReadString(
                    root["apiBaseUrl"],
                    $"{contractPath}.apiBaseUrl"),
                $"{contractPath}.apiBaseUrl");
            Require(
                string.Equals(
                    apiBaseUrl,
                    expected.ProductionApiBaseUrl,
                    StringComparison.Ordinal),
                $"{contractPath}.apiBaseUrl",
                $"expected '{expected.ProductionApiBaseUrl}', got '{apiBaseUrl}'.");
        }
    }

    private static async Task
        RequireTraceArchiveHasNoForbiddenSecretsAsync(
            string fullPath,
            IReadOnlyList<string> forbiddenSecrets,
            string contractPath,
            CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous |
                         FileOptions.SequentialScan);
            using var archive = new ZipArchive(
                file,
                ZipArchiveMode.Read,
                leaveOpen: false);
            Require(
                archive.Entries.Count <= MaxTraceArchiveEntries,
                contractPath,
                $"trace archive exceeds {MaxTraceArchiveEntries} entries.");
            long totalLength = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                Require(
                    entry.Length is >= 0 and <= MaxTraceArchiveEntryBytes,
                    contractPath,
                    $"trace archive entry '{entry.FullName}' exceeds the per-entry safety limit.");
                totalLength = checked(totalLength + entry.Length);
                Require(
                    totalLength <= MaxTraceArchiveTotalBytes,
                    contractPath,
                    "trace archive exceeds the total uncompressed safety limit.");
                await using var entryStream = entry.Open();
                using var buffer = new MemoryStream(
                    checked((int)entry.Length));
                await entryStream.CopyToAsync(
                    buffer,
                    cancellationToken);
                Require(
                    buffer.Length == entry.Length,
                    contractPath,
                    $"trace archive entry '{entry.FullName}' length drifted while reading.");
                RequireNoForbiddenSecrets(
                    buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)),
                    forbiddenSecrets,
                    contractPath);
            }
        }
        catch (P612BrowserEvidenceContractException)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            OverflowException)
        {
            throw Failure(
                contractPath,
                $"trace archive could not be safely inspected: {error.Message}",
                error);
        }
    }

    private static Dictionary<string, JsonElement> ReadExactObject(
        JsonElement element,
        string path,
        params string[] exactKeys)
    {
        RequireKind(element, JsonValueKind.Object, path);
        var allowed = new HashSet<string>(
            exactKeys,
            StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            Require(
                allowed.Contains(property.Name),
                path,
                $"unknown property '{property.Name}'.");
            Require(
                values.TryAdd(property.Name, property.Value),
                path,
                $"duplicate property '{property.Name}'.");
        }
        foreach (var key in exactKeys)
        {
            Require(
                values.ContainsKey(key),
                path,
                $"missing required property '{key}'.");
        }
        return values;
    }

    private static string ReadString(JsonElement value, string path)
    {
        RequireKind(value, JsonValueKind.String, path);
        var result = value.GetString() ?? string.Empty;
        RequireNonBlank(result, path);
        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement value,
        string path,
        bool requireNonEmpty)
    {
        RequireKind(value, JsonValueKind.Array, path);
        Require(
            !requireNonEmpty || value.GetArrayLength() > 0,
            path,
            "array must not be empty.");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var text = ReadString(item, $"{path}[{index}]");
            Require(
                seen.Add(text),
                $"{path}[{index}]",
                $"duplicate value '{text}'.");
            result.Add(text);
            index++;
        }
        return result;
    }

    private static void RequireExactStringArray(
        JsonElement value,
        IReadOnlyList<string> expected,
        string path)
    {
        var actual = ReadStringArray(
            value,
            path,
            requireNonEmpty: true);
        Require(
            actual.SequenceEqual(expected, StringComparer.Ordinal),
            path,
            $"expected exact ordered values [{string.Join(", ", expected)}].");
    }

    private static void RequireExactString(
        JsonElement value,
        string expected,
        string path)
    {
        var actual = ReadString(value, path);
        Require(
            string.Equals(actual, expected, StringComparison.Ordinal),
            path,
            $"expected '{expected}', got '{actual}'.");
    }

    private static void RequireExactInteger(
        JsonElement value,
        int expected,
        string path)
    {
        RequireKind(value, JsonValueKind.Number, path);
        Require(
            value.TryGetInt32(out var actual) && actual == expected,
            path,
            $"expected integer {expected}.");
    }

    private static void RequireExactBoolean(
        JsonElement value,
        bool expected,
        string path)
    {
        var expectedKind = expected
            ? JsonValueKind.True
            : JsonValueKind.False;
        RequireKind(value, expectedKind, path);
    }

    private static void RequireKind(
        JsonElement value,
        JsonValueKind expected,
        string path)
    {
        Require(
            value.ValueKind == expected,
            path,
            $"expected JSON {expected}, got {value.ValueKind}.");
    }

    private static void ValidateExpectedStringList(
        IReadOnlyList<string> values,
        string path)
    {
        if (values is null)
            throw Failure(path, "expected values are required.");
        Require(
            values.Count > 0,
            path,
            "at least one exact value is required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            RequireNonBlank(values[index], $"{path}[{index}]");
            Require(
                seen.Add(values[index]),
                $"{path}[{index}]",
                $"duplicate expected value '{values[index]}'.");
        }
    }

    private static void ValidateExpectedJourneyList(
        IReadOnlyList<string> values,
        string path,
        bool requireNonEmpty)
    {
        if (values is null)
            throw Failure(path, "expected journey list is required.");
        Require(
            !requireNonEmpty || values.Count > 0,
            path,
            "expected journey list must not be empty.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var journeyId = values[index];
            Require(
                ExactJourneyIds.Contains(
                    journeyId,
                    StringComparer.Ordinal),
                $"{path}[{index}]",
                $"unexpected journeyId '{journeyId}'.");
            Require(
                seen.Add(journeyId),
                $"{path}[{index}]",
                $"duplicate journeyId '{journeyId}'.");
        }
    }

    private static string RequireExistingRunRoot(string? value)
    {
        RequireNonBlank(value, "$expectations.runRoot");
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value!);
        }
        catch (Exception error) when (
            error is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw Failure(
                "$expectations.runRoot",
                $"run root is invalid: {error.Message}",
                error);
        }
        Require(
            Directory.Exists(fullPath),
            "$expectations.runRoot",
            $"run root does not exist: '{fullPath}'.");
        RejectReparsePoints(
            fullPath,
            fullPath,
            "$expectations.runRoot");
        return fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static string ResolveExistingFileWithinRunRoot(
        string runRoot,
        string claimedPath,
        string contractPath)
    {
        RequireNonBlank(claimedPath, contractPath);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                Path.IsPathFullyQualified(claimedPath)
                    ? claimedPath
                    : Path.Combine(runRoot, claimedPath));
        }
        catch (Exception error) when (
            error is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw Failure(
                contractPath,
                $"artifact path is invalid: {error.Message}",
                error);
        }

        var rootPrefix =
            runRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        Require(
            fullPath.StartsWith(rootPrefix, PathComparison),
            contractPath,
            $"artifact path escapes the run root: '{claimedPath}'.");
        Require(
            File.Exists(fullPath),
            contractPath,
            $"artifact file does not exist: '{claimedPath}'.");
        Require(
            !Directory.Exists(fullPath),
            contractPath,
            $"artifact path is not a regular file: '{claimedPath}'.");
        RejectReparsePoints(runRoot, fullPath, contractPath);
        return fullPath;
    }

    private static void RejectReparsePoints(
        string runRoot,
        string fullPath,
        string contractPath)
    {
        var current = fullPath;
        while (true)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception error) when (
                error is IOException or
                UnauthorizedAccessException)
            {
                throw Failure(
                    contractPath,
                    $"cannot inspect path safety for '{current}': {error.Message}",
                    error);
            }
            Require(
                !attributes.HasFlag(FileAttributes.ReparsePoint),
                contractPath,
                $"reparse points are not allowed in evidence paths: '{current}'.");
            if (string.Equals(current, runRoot, PathComparison))
                break;
            current = Path.GetDirectoryName(current) ??
                      throw Failure(
                          contractPath,
                          "artifact path has no parent before reaching run root.");
            Require(
                current.StartsWith(runRoot, PathComparison),
                contractPath,
                "artifact parent escaped the run root.");
        }
    }

    private static string NormalizeHttpOrigin(string? value, string path)
    {
        RequireNonBlank(value, path);
        Require(
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo),
            path,
            "value must be an absolute HTTP(S) origin without user info.");
        Require(
            uri!.AbsolutePath == "/" &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment),
            path,
            "origin must not contain a path, query, or fragment.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string NormalizeHttpApiBaseUrl(
        string? value,
        string path)
    {
        RequireNonBlank(value, path);
        Require(
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo),
            path,
            "value must be an absolute HTTP(S) API base URL without user info.");
        Require(
            uri!.AbsolutePath.TrimEnd('/') == "/api" &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment),
            path,
            "API base URL path must be exactly '/api' without query or fragment.");
        return $"{uri.GetLeftPart(UriPartial.Authority)}/api";
    }

    private static void ValidateExactFrontendUrl(
        string value,
        string expectedOrigin,
        string path)
    {
        RequireNonBlank(value, path);
        Require(
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(uri.UserInfo),
            path,
            "exactUrl must be an absolute HTTP(S) URL without user info.");
        Require(
            string.Equals(
                uri!.GetLeftPart(UriPartial.Authority),
                expectedOrigin,
                StringComparison.Ordinal),
            path,
            $"exactUrl must use production frontend origin '{expectedOrigin}'.");
    }

    private static string RequireExpectationToken(string? value, string path)
    {
        RequireNonBlank(value, path);
        Require(
            value!.All(ch =>
                char.IsLetterOrDigit(ch) ||
                ch is '_' or '-' or '.' or ':'),
            path,
            "token contains an unsupported character.");
        return value!;
    }

    private static string RequireValidSha256(string? value, string path)
    {
        RequireNonBlank(value, path);
        Require(
            value!.Length == 64 &&
            value.All(ch =>
                ch is >= '0' and <= '9' or >= 'a' and <= 'f'),
            path,
            "value must be a lowercase 64-character SHA-256.");
        return value;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous |
                         FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            var digest = await sha256.ComputeHashAsync(
                stream,
                cancellationToken);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        catch (Exception error) when (
            error is IOException or
            UnauthorizedAccessException)
        {
            throw Failure(
                "$artifact",
                $"could not hash '{path}': {error.Message}",
                error);
        }
    }

    private static void RequireNoForbiddenSecrets(
        string text,
        IReadOnlyList<string> forbiddenSecrets,
        string path)
    {
        foreach (var secret in forbiddenSecrets)
        {
            Require(
                !text.Contains(secret, StringComparison.Ordinal),
                path,
                "textual evidence contains a forbidden secret value.");
        }
    }

    private static void RequireNoForbiddenSecrets(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<string> forbiddenSecrets,
        string path)
    {
        foreach (var secret in forbiddenSecrets)
        {
            var utf8 = Encoding.UTF8.GetBytes(secret);
            var utf16 = Encoding.Unicode.GetBytes(secret);
            Require(
                bytes.IndexOf(utf8) < 0 &&
                bytes.IndexOf(utf16) < 0,
                path,
                "trace archive contains a forbidden secret value.");
        }
    }

    private static void ValidateArtifactExtension(
        string fullPath,
        ArtifactKind kind,
        string path)
    {
        var fileName = Path.GetFileName(fullPath);
        var extension = Path.GetExtension(fullPath);
        var valid = kind switch
        {
            ArtifactKind.FrontendManifest =>
                string.Equals(
                    extension,
                    ".json",
                    StringComparison.OrdinalIgnoreCase),
            ArtifactKind.Screenshot =>
                extension is not null &&
                new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(
                    extension,
                    StringComparer.OrdinalIgnoreCase),
            ArtifactKind.Trace =>
                fileName.EndsWith(
                    ".trace.zip",
                    StringComparison.OrdinalIgnoreCase) ||
                new[] { ".zip", ".json", ".har", ".trace" }.Contains(
                    extension,
                    StringComparer.OrdinalIgnoreCase),
            _ => false
        };
        Require(
            valid,
            path,
            $"unsupported {kind} artifact extension for '{fileName}'.");
    }

    private static bool IsTextualArtifact(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        return new[]
        {
            ".json",
            ".har",
            ".trace",
            ".txt",
            ".log",
            ".md",
            ".csv",
            ".html",
            ".xml",
            ".yaml",
            ".yml"
        }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static void RequireExactSet(
        IEnumerable<string> actual,
        IEnumerable<string> expected,
        string path)
    {
        var actualSet = new HashSet<string>(
            actual,
            StringComparer.Ordinal);
        var expectedSet = new HashSet<string>(
            expected,
            StringComparer.Ordinal);
        Require(
            actualSet.SetEquals(expectedSet),
            path,
            $"expected exact set [{string.Join(", ", expectedSet.OrderBy(x => x, StringComparer.Ordinal))}], " +
            $"got [{string.Join(", ", actualSet.OrderBy(x => x, StringComparer.Ordinal))}].");
    }

    private static void RequireNonBlank(string? value, string path)
    {
        Require(
            !string.IsNullOrWhiteSpace(value),
            path,
            "value must be a non-empty string.");
    }

    private static void Require(
        bool condition,
        string path,
        string message)
    {
        if (!condition)
            throw Failure(path, message);
    }

    private static P612BrowserEvidenceContractException Failure(
        string path,
        string message,
        Exception? inner = null)
        => new($"{path}: {message}", inner);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private enum ArtifactKind
    {
        FrontendManifest,
        Screenshot,
        Trace
    }

    private sealed record ExpectedContract(
        string RunRoot,
        string RunKey,
        string Nonce,
        string ReadyManifestSha256,
        string ProductionFrontendOrigin,
        string ProductionApiBaseUrl,
        string ProductionFrontendBuildId,
        string ProductionFrontendSourceRevision,
        string RetryJourneyId,
        string ConflictJourneyId,
        IReadOnlyDictionary<string, P612ExpectedJourneyEvidence> Journeys,
        IReadOnlyDictionary<string, P612ExpectedActorCoverage> ActorCoverage,
        IReadOnlyList<string> ForbiddenSecrets);

    private sealed record ArtifactReference(string Path, string Sha256);

    private sealed record ScreenshotArtifact(
        int Index,
        string JourneyId,
        string Viewport,
        string Path,
        string Sha256);

    private sealed record TraceArtifact(
        int Index,
        string Name,
        IReadOnlyList<string> JourneyIds,
        string Path,
        string Sha256);

    private sealed record BrowserArtifacts(
        IReadOnlyList<ScreenshotArtifact> Screenshots,
        IReadOnlyList<TraceArtifact> Traces);
}

public sealed record P612BrowserEvidenceExpectations
{
    public required string RunRoot { get; init; }
    public required string RunKey { get; init; }
    public required string Nonce { get; init; }
    public required string ReadyManifestSha256 { get; init; }
    public required string ProductionFrontendOrigin { get; init; }
    public required string ProductionApiBaseUrl { get; init; }
    public required string ProductionFrontendBuildId { get; init; }
    public required string ProductionFrontendSourceRevision { get; init; }
    public required string RetryJourneyId { get; init; }
    public required string ConflictJourneyId { get; init; }
    public required IReadOnlyList<P612ExpectedJourneyEvidence> Journeys
    {
        get;
        init;
    }
    public required IReadOnlyList<P612ExpectedActorCoverage> ActorCoverage
    {
        get;
        init;
    }
    public IReadOnlyCollection<string> ForbiddenSecrets { get; init; } =
        Array.Empty<string>();
}

public sealed record P612ExpectedJourneyEvidence(
    string JourneyId,
    string ObservedId,
    string Actor,
    string ExactUrl,
    IReadOnlyList<string> RequiredTestIds,
    IReadOnlyList<string> StateSemantics);

public sealed record P612ExpectedActorCoverage(
    string Actor,
    IReadOnlyList<string> CoveredJourneyIds);

public sealed record P612BrowserEvidenceValidationResult(
    string ResultPath,
    string ResultSha256,
    string RunKey,
    string ReadyManifestSha256,
    string ProductionFrontendOrigin,
    int JourneyCount,
    int ActorCount,
    int ScreenshotCount,
    int TraceCount,
    IReadOnlyDictionary<string, string> ArtifactSha256ByFullPath);

public sealed class P612BrowserEvidenceContractException
    : InvalidOperationException
{
    public P612BrowserEvidenceContractException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
