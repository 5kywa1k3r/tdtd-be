using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

internal static class P711FreshRegressionVerifier
{
    private const string Contract =
        "P7-11-FRESH-REGRESSION-SUITE-1";

    private static readonly string[] CommandIds =
    [
        "01-BACKEND-RELEASE-BUILD",
        "02-INTEGRATION-RELEASE-BUILD",
        "03-P0-P4-CLEAN",
        "04-P0-P4-DELIBERATE",
        "05-P5-CLEAN",
        "06-P5-DELIBERATE",
        "07-P6-CLEAN",
        "08-P6-DELIBERATE",
        "09-BACKEND-CONTRACTS",
        "10-FRONTEND-APP-TYPES",
        "11-FRONTEND-TEST-TYPES",
        "12-FRONTEND-FULL-TESTS",
        "13-FRONTEND-FULL-LINT",
        "14-FRONTEND-PRODUCTION-BUILD",
        "15-CATALOG-GENERATOR-CHECK",
        "16-CATALOG-HISTORY-VALIDATOR"
    ];

    private static readonly IReadOnlyDictionary<string, SourceOverlayPin>
        ExactV13RollbackOverlay =
            new Dictionary<string, SourceOverlayPin>(StringComparer.Ordinal)
            {
                ["tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json"] =
                    new(
                        "c327f32b4fbe9a840a07129338b520a31441c1956991b51bf83fd6bb80a8539c",
                        226,
                        "2f4206ad631b44f82484d10d5aabff64dcdb3fc2fcb386b5d81e136d69cf6c5c",
                        226),
                ["tdtd-be/Common/Capabilities/DynamicFormFlowCapabilityCatalog.g.cs"] =
                    new(
                        "2ba8bfd5a3354ebb3c0826ac46afc215dc5c38dcdec8bc508c8ddfd09c581998",
                        13347,
                        "a95e8f6ec05317380e9e8ed2afd201e6e484c2bb069bd9f484e0af878fbda78f",
                        10447),
                ["tdtd-fe/src/generated/dynamicFormFlowCapabilityCatalog.generated.ts"] =
                    new(
                        "e9b613ab997d82f5670ea618908c76bce6056f9e7c017af701fa02da08107e90",
                        16746,
                        "4c18ea9833706e4664fc2e89581ce5a345d4f3ec79b2ade0b75e938ee24627ec",
                        12823)
            };

    public static object Build(
        HarnessPaths paths,
        string childRunKey)
    {
        var configured = Environment.GetEnvironmentVariable(
            "TDTD_P7_11_REGRESSION_MANIFEST");
        var manifestPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                paths.WorkspaceRoot,
                ".p7-artifacts",
                "test-runs",
                "P7-11-fresh-regression-manifest.json")
            : Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : ResolveWorkspacePath(paths, configured);
        var manifestRelative = RelativeToWorkspace(
            paths,
            manifestPath);
        Require(
            manifestRelative.StartsWith(
                ".p7-artifacts/test-runs/",
                StringComparison.Ordinal),
            "Fresh regression manifest escaped the test-runs root.");
        var manifest = LoadObject(manifestPath);
        RequireEqual(
            Contract,
            RequiredString(manifest, "contract"),
            "Fresh regression contract drift.");
        RequireEqual(
            "P7-11",
            RequiredString(manifest, "gate"),
            "Fresh regression gate drift.");
        RequireTrue(manifest, "passed");

        var execution = RequiredObject(manifest, "execution");
        RequireTrue(execution, "strictSequential");
        RequireTrue(execution, "callerPinnedSourceFingerprint");
        RequireFalse(execution, "reusedP710RegressionEvidence");
        RequireFalse(execution, "commandsRunInsideP7Children");
        RequireEqual(
            CommandIds.Length,
            RequiredInt32(execution, "commandCount"),
            "Fresh regression command count drift.");
        RequireSequence(
            CommandIds,
            RequiredArray(execution, "commandIds")
                .EnumerateArray()
                .Select(RequiredArrayString)
                .ToArray(),
            "Fresh regression command sequence drift.");

        var sourceBinding =
            RequiredObject(manifest, "sourceBinding");
        var workspaceSha = RequiredString(
            sourceBinding,
            "workspaceSemanticSha256");
        Require(
            IsLowerSha256(workspaceSha),
            "Fresh source fingerprint SHA is invalid.");
        var sourceFiles = RequiredArray(
                sourceBinding,
                "sourceFiles")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        Require(
            sourceFiles.Length > 100,
            "Fresh source fingerprint covers too few files.");
        RequireEqual(
            sourceFiles.Length,
            RequiredInt32(sourceBinding, "sourceFileCount"),
            "Fresh source file count drift.");
        var orderedPaths = sourceFiles
            .Select(item => NormalizeRelative(
                RequiredString(item, "path")))
            .ToArray();
        RequireSequence(
            orderedPaths.OrderBy(
                item => item,
                StringComparer.Ordinal),
            orderedPaths,
            "Fresh source rows are not canonical-path ordered.");
        var uniquePaths =
            new HashSet<string>(StringComparer.Ordinal);
        var canonicalRows =
            new List<string>(sourceFiles.Length);
        var appliedSourceOverlay =
            new List<AppliedSourceOverlay>();
        foreach (var sourceFile in sourceFiles)
        {
            var relative = NormalizeRelative(
                RequiredString(sourceFile, "path"));
            Require(
                uniquePaths.Add(relative),
                $"Duplicate source fingerprint path {relative}.");
            var sha = RequiredString(sourceFile, "sha256");
            var bytes = RequiredInt64(sourceFile, "bytes");
            Require(
                IsLowerSha256(sha) && bytes >= 0,
                $"Invalid source fingerprint row {relative}.");
            var overlay = VerifyExistingSourceFile(
                paths,
                relative,
                sha,
                bytes);
            if (overlay is not null)
                appliedSourceOverlay.Add(overlay);
            canonicalRows.Add(
                $"{relative}\n{sha}\n{bytes}");
        }
        Require(
            appliedSourceOverlay.Count is 0 ||
            appliedSourceOverlay.Count == ExactV13RollbackOverlay.Count,
            "Fresh source rollback overlay must be inactive or exact/all-or-none.");
        if (appliedSourceOverlay.Count > 0)
        {
            RequireSequence(
                ExactV13RollbackOverlay.Keys.OrderBy(
                    item => item,
                    StringComparer.Ordinal),
                appliedSourceOverlay.Select(item => item.Path).OrderBy(
                    item => item,
                    StringComparer.Ordinal),
                "Fresh source rollback overlay path set drift.");
        }
        RequireEqual(
            workspaceSha,
            HashText(string.Join('\n', canonicalRows)),
            "Fresh workspace source fingerprint drift.");

        VerifyReference(
            paths,
            RequiredObject(
                sourceBinding,
                "sourceFingerprintArtifact"),
            allowDetached: false);
        foreach (var property in new[]
                 {
                     "backendReleaseAssembly",
                     "integrationReleaseAssembly",
                     "backendContractsAssembly"
                 })
        {
            VerifyReference(
                paths,
                RequiredObject(sourceBinding, property),
                allowDetached: false);
        }

        foreach (var property in new[]
                 {
                     "p0ThroughP6Full",
                     "backendContracts",
                     "backendBuild",
                     "frontendTypes",
                     "frontendTests",
                     "frontendLint",
                     "frontendProductionBuild",
                     "historicalCatalogPins"
                 })
        {
            RequireTrue(manifest, property);
        }
        RequireTrue(
            RequiredObject(manifest, "p0ThroughP6"),
            "passed");
        var backend = RequiredObject(manifest, "backend");
        RequireTrue(backend, "passed");
        RequireEqual(
            209,
            RequiredInt32(backend, "contractsPassed"),
            "Backend contract count drift.");
        RequireEqual(
            0,
            RequiredInt32(backend, "contractsFailed"),
            "Backend contract failures remain.");
        var frontend = RequiredObject(manifest, "frontend");
        RequireTrue(frontend, "passed");
        RequireEqual(
            38,
            RequiredInt32(frontend, "testFilesPassed"),
            "Frontend test-file count drift.");
        RequireEqual(
            263,
            RequiredInt32(frontend, "testsPassed"),
            "Frontend test count drift.");
        RequireEqual(
            0,
            RequiredInt32(frontend, "testsFailed"),
            "Frontend test failures remain.");
        RequireTrue(
            RequiredObject(manifest, "catalog"),
            "passed");

        var cleanup = RequiredObject(manifest, "cleanup");
        foreach (var property in new[]
                 {
                     "allProcessesStopped",
                     "allPortsReleased",
                     "allDatabasesDropped",
                     "allMongoDataDirectoriesRemoved",
                     "isolatedFrontendBuildRemoved",
                     "passed"
                 })
        {
            RequireTrue(cleanup, property);
        }
        VerifyReference(
            paths,
            RequiredObject(cleanup, "artifact"),
            allowDetached: false);
        var security = RequiredObject(manifest, "security");
        RequireTrue(security, "passed");
        RequireEqual(
            0,
            RequiredInt32(security, "sensitiveMatches"),
            "Fresh regression security matches remain.");
        VerifyReference(
            paths,
            RequiredObject(security, "artifact"),
            allowDetached: false);

        var commands = RequiredArray(manifest, "commands")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        RequireEqual(
            CommandIds.Length,
            commands.Length,
            "Fresh command ledger count drift.");
        var projectedCommands = commands
            .Select((command, index) =>
            {
                var commandId = RequiredString(
                    command,
                    "commandId");
                RequireEqual(
                    CommandIds[index],
                    commandId,
                    "Fresh command ordering drift.");
                RequireEqual(
                    index + 1,
                    RequiredInt32(command, "ordinal"),
                    $"{commandId} ordinal drift.");
                RequireTrue(command, "passed");
                RequireEqual(
                    RequiredInt32(command, "expectedExitCode"),
                    RequiredInt32(command, "actualExitCode"),
                    $"{commandId} exit code drift.");
                var semanticSha = RequiredString(
                    command,
                    "semanticSha256");
                Require(
                    IsLowerSha256(semanticSha),
                    $"{commandId} semantic SHA is invalid.");
                RequireEqual(
                    P711FreshRegressionSemanticHash.Compute(command),
                    semanticSha,
                    $"{commandId} semantic SHA drift.");
                var logs = RequiredArray(command, "logs")
                    .EnumerateArray()
                    .Select(item => item.Clone())
                    .ToArray();
                Require(
                    logs.Length > 0,
                    $"{commandId} has no captured log.");
                foreach (var log in logs)
                {
                    VerifyReference(
                        paths,
                        log,
                        allowDetached: false);
                }
                foreach (var artifact in RequiredArray(
                             command,
                             "artifacts").EnumerateArray())
                {
                    VerifyReference(
                        paths,
                        artifact,
                        allowDetached: true);
                }
                return new
                {
                    commandId,
                    semanticSha256 = semanticSha,
                    sourceArtifact =
                        RequiredString(logs[0], "path"),
                    passed = true
                };
            })
            .ToArray();

        return new
        {
            schemaVersion = 1,
            contract = "P7-11-REGRESSION-1",
            gate = "P7-11",
            runKey = childRunKey,
            sourceSuiteRunKey =
                RequiredString(manifest, "runKey"),
            sourceManifest = manifestRelative,
            sourceManifestSha256 =
                HashFile(manifestPath),
            sourceWorkspaceSemanticSha256 =
                workspaceSha,
            sourceOverlay = new
            {
                policy = "V1.4-MANIFEST-WITH-EXACT-V1.3-ROLLBACK-OVERLAY-1",
                active = appliedSourceOverlay.Count > 0,
                fileCount = appliedSourceOverlay.Count,
                files = appliedSourceOverlay
            },
            p0ThroughP6Full = true,
            backendContracts = true,
            backendBuild = true,
            frontendTypes = true,
            frontendTests = true,
            frontendLint = true,
            frontendProductionBuild = true,
            historicalCatalogPins = true,
            commands = projectedCommands,
            passed = true
        };
    }

    private static void VerifyReference(
        HarnessPaths paths,
        JsonElement reference,
        bool allowDetached)
    {
        var relative = NormalizeRelative(
            RequiredString(reference, "path"));
        var sha = RequiredString(reference, "sha256");
        var bytes = RequiredInt64(reference, "bytes");
        Require(
            IsLowerSha256(sha) && bytes >= 0,
            $"Invalid file reference {relative}.");
        var path = ResolveWorkspacePath(paths, relative);
        if (!File.Exists(path))
        {
            Require(
                allowDetached &&
                reference.TryGetProperty(
                    "removedAfterCapture",
                    out var removed) &&
                removed.ValueKind == JsonValueKind.True,
                $"Referenced file is missing: {relative}.");
            return;
        }
        VerifyExistingFile(
            paths,
            relative,
            sha,
            bytes);
    }

    private static void VerifyExistingFile(
        HarnessPaths paths,
        string relative,
        string sha,
        long bytes)
    {
        var path = ResolveWorkspacePath(paths, relative);
        Require(
            File.Exists(path),
            $"Referenced file is missing: {relative}.");
        var actualBytes = new FileInfo(path).Length;
        var actualSha = HashFile(path);
        if (actualBytes == bytes &&
            string.Equals(actualSha, sha, StringComparison.Ordinal))
        {
            return;
        }

        if (ExactV13RollbackOverlay.TryGetValue(relative, out var pin) &&
            pin.ManifestBytes == bytes &&
            string.Equals(
                pin.ManifestSha256,
                sha,
                StringComparison.Ordinal) &&
            pin.LiveBytes == actualBytes &&
            string.Equals(
                pin.LiveSha256,
                actualSha,
                StringComparison.Ordinal))
        {
            return;
        }

        RequireEqual(
            bytes,
            actualBytes,
            $"Referenced file byte count drift: {relative}.");
        RequireEqual(
            sha,
            actualSha,
            $"Referenced file SHA drift: {relative}.");
    }

    private static AppliedSourceOverlay? VerifyExistingSourceFile(
        HarnessPaths paths,
        string relative,
        string sha,
        long bytes)
    {
        var path = ResolveWorkspacePath(paths, relative);
        Require(
            File.Exists(path),
            $"Referenced source file is missing: {relative}.");
        var actualBytes = new FileInfo(path).Length;
        var actualSha = HashFile(path);
        if (actualBytes == bytes &&
            string.Equals(actualSha, sha, StringComparison.Ordinal))
        {
            return null;
        }

        if (!ExactV13RollbackOverlay.TryGetValue(relative, out var pin))
        {
            throw new InvalidOperationException(
                $"Fresh source fingerprint drift: {relative}.");
        }
        RequireEqual(
            pin.ManifestBytes,
            bytes,
            $"Fresh source rollback manifest byte pin drift: {relative}.");
        RequireEqual(
            pin.ManifestSha256,
            sha,
            $"Fresh source rollback manifest SHA pin drift: {relative}.");
        RequireEqual(
            pin.LiveBytes,
            actualBytes,
            $"Fresh source rollback live byte pin drift: {relative}.");
        RequireEqual(
            pin.LiveSha256,
            actualSha,
            $"Fresh source rollback live SHA pin drift: {relative}.");
        return new AppliedSourceOverlay(
            relative,
            sha,
            bytes,
            actualSha,
            actualBytes);
    }

    private static JsonElement LoadObject(string path)
    {
        Require(
            File.Exists(path),
            $"Fresh regression manifest is missing: {path}.");
        using var document = JsonDocument.Parse(
            File.ReadAllText(path));
        Require(
            document.RootElement.ValueKind ==
            JsonValueKind.Object,
            $"Expected JSON object: {path}.");
        return document.RootElement.Clone();
    }

    private static JsonElement RequiredObject(
        JsonElement root,
        string property)
    {
        Require(
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object,
            $"Required object {property} is missing.");
        return value;
    }

    private static JsonElement RequiredArray(
        JsonElement root,
        string property)
    {
        Require(
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Array,
            $"Required array {property} is missing.");
        return value;
    }

    private static string RequiredArrayString(
        JsonElement value)
    {
        Require(
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()),
            "Required array string is blank/missing.");
        return value.GetString()!;
    }

    private static string RequiredString(
        JsonElement root,
        string property)
    {
        Require(
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()),
            $"Required string {property} is missing.");
        return value.GetString()!;
    }

    private static int RequiredInt32(
        JsonElement root,
        string property)
    {
        var result = 0;
        Require(
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out result),
            $"Required integer {property} is missing.");
        return result;
    }

    private static long RequiredInt64(
        JsonElement root,
        string property)
    {
        var result = 0L;
        Require(
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out result),
            $"Required long integer {property} is missing.");
        return result;
    }

    private static void RequireTrue(
        JsonElement root,
        string property)
        => Require(
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.True,
            $"Required true {property} is missing/false.");

    private static void RequireFalse(
        JsonElement root,
        string property)
        => Require(
            root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.False,
            $"Required false {property} is missing/true.");

    private static string ResolveWorkspacePath(
        HarnessPaths paths,
        string relative)
    {
        Require(
            !Path.IsPathRooted(relative),
            "Referenced path must be workspace-relative.");
        var root = Path.GetFullPath(paths.WorkspaceRoot);
        var target = Path.GetFullPath(
            Path.Combine(
                root,
                relative.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        var check = Path.GetRelativePath(root, target);
        Require(
            !Path.IsPathRooted(check) &&
            !check.Equals("..", StringComparison.Ordinal) &&
            !check.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal),
            "Referenced path escaped the workspace.");
        return target;
    }

    private static string RelativeToWorkspace(
        HarnessPaths paths,
        string path)
        => NormalizeRelative(
            Path.GetRelativePath(
                paths.WorkspaceRoot,
                path));

    private static string NormalizeRelative(string value)
        => value.Replace('\\', '/');

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string HashText(string value)
        => Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool IsLowerSha256(string value)
        => value.Length == 64 &&
           value.All(character =>
               character is >= '0' and <= '9' or
                   >= 'a' and <= 'f');

    private static void RequireSequence(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string message)
        => Require(
            expected.SequenceEqual(
                actual,
                StringComparer.Ordinal),
            message);

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireEqual<T>(
        T expected,
        T actual,
        string message)
    {
        if (!EqualityComparer<T>.Default.Equals(
                expected,
                actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected={expected}; Actual={actual}.");
        }
    }

    private sealed record SourceOverlayPin(
        string ManifestSha256,
        long ManifestBytes,
        string LiveSha256,
        long LiveBytes);

    private sealed record AppliedSourceOverlay(
        string Path,
        string ManifestSha256,
        long ManifestBytes,
        string LiveSha256,
        long LiveBytes);
}



