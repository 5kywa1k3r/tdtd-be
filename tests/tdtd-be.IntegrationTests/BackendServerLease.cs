using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

namespace tdtd_be.IntegrationTests;

internal sealed class BackendServerLease : IAsyncDisposable
{
    internal const string FixedUtcEnvironmentVariable =
        "TDTD_P10_FIXED_UTC_NOW";
    private static readonly Regex MappingCommandIdRegex =
        new(
            "^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$",
            RegexOptions.CultureInvariant);
    private static readonly Regex UriUserInfoRegex =
        new(
            @"(?<scheme>[A-Za-z][A-Za-z0-9+.-]*://)[^/\s?#@]+@",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal const string P3LifecycleProjectionFailureCommandId = "p3-outbox-failure-recovery-submit-001";
    internal const string P4DefinitionFaultCommandIdPrefix = "p4-fault-matrix-";
    internal static readonly string[] P4DefinitionFaultPoints =
    [
        DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite,
        DynamicFlowDefinitionFaultPoints.AfterFamilyWrite,
        DynamicFlowDefinitionFaultPoints.BeforeVersionWrite,
        DynamicFlowDefinitionFaultPoints.AfterVersionWrite,
        DynamicFlowDefinitionFaultPoints.BeforeReceiptWrite,
        DynamicFlowDefinitionFaultPoints.AfterReceiptWrite,
        DynamicFlowDefinitionFaultPoints.BeforeAuditWrite,
        DynamicFlowDefinitionFaultPoints.AfterAuditWrite
    ];

    private readonly ManagedChildProcess _process;
    private readonly int _processId;

    private BackendServerLease(
        ManagedChildProcess process,
        Uri baseUri,
        string bootstrapKey,
        string actorPassword,
        string dynamicFlowMappingPreviewSigningKey,
        string stdoutPath,
        string stderrPath,
        int port)
    {
        _process = process;
        _processId = process.Id;
        BaseUri = baseUri;
        BootstrapKey = bootstrapKey;
        ActorPassword = actorPassword;
        DynamicFlowMappingPreviewSigningKey =
            dynamicFlowMappingPreviewSigningKey;
        StdoutPath = stdoutPath;
        StderrPath = stderrPath;
        Port = port;
    }

    public Uri BaseUri { get; }
    public string BootstrapKey { get; }
    public string ActorPassword { get; }
    public string DynamicFlowMappingPreviewSigningKey { get; }
    public string StdoutPath { get; }
    public string StderrPath { get; }
    public int Port { get; }
    public int ProcessId => _processId;
    public bool StopVerified { get; private set; }
    public bool PortReleaseVerified { get; private set; }

    public static async Task<BackendServerLease> StartAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        MongoReplicaSetLease mongo,
        CancellationToken ct,
        BackendServerOptions? options = null)
    {
        options ??= new BackendServerOptions();
        var fixedUtcNow = options.SuppressTestingFixedUtcNow
            ? null
            : options.FixedUtcNow;
        var fixedUtcRaw = options.SuppressTestingFixedUtcNow
            ? null
            : Environment.GetEnvironmentVariable(FixedUtcEnvironmentVariable);
        if (fixedUtcNow is null && fixedUtcRaw is not null)
        {
            if (!DateTime.TryParseExact(fixedUtcRaw, "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsedFixedUtc))
                throw new InvalidOperationException(
                    "P10_FIXED_UTC_ENVIRONMENT_INVALID");
            fixedUtcNow = parsedFixedUtc;
        }
        if (fixedUtcNow is { Kind: not DateTimeKind.Utc })
            throw new ArgumentException("FixedUtcNow must be UTC.", nameof(options));
        var mappingApplyFaultBindings =
            ValidateMappingApplyFaultBindings(
                options.DynamicFlowMappingApplyFaultBindings);
        var mappingApplyPauseBindings =
            ValidateMappingApplyPauseBindings(
                options.DynamicFlowMappingApplyPauseBindings);
        var mappingReconcilePauseBindings =
            ValidateMappingReconcilePauseBindings(
                options.DynamicFlowMappingReconcilePauseBindings);
        var backendDll = paths.ResolveBackendDll();
        var port = PortAllocator.GetFreeBrowserHttpPort();
        var baseUri = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        var bootstrapKey = $"p1-bootstrap-{Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant()}";
        var actorPassword = options.ActorPasswordOverride ??
            $"P1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}a";
        if (actorPassword.Length < 16 || actorPassword.Any(char.IsWhiteSpace))
            throw new ArgumentException(
                "Actor password override must be at least 16 non-whitespace characters.", nameof(options));
        var jwtKey = options.JwtSigningKey ??
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        if (string.IsNullOrWhiteSpace(jwtKey) ||
            Encoding.UTF8.GetByteCount(jwtKey) < 32)
        {
            throw new ArgumentException(
                "JWT signing key must contain at least 32 UTF-8 bytes.",
                nameof(options));
        }
        var p10ActualApiOwnerAuthorizationParameter =
            options.P10ActualApiOwnerServiceAuthorizationParameter;
        if (p10ActualApiOwnerAuthorizationParameter is not null &&
            string.IsNullOrWhiteSpace(
                p10ActualApiOwnerAuthorizationParameter))
        {
            throw new ArgumentException(
                "P10 actual API-owner authorization parameter cannot be blank.",
                nameof(options));
        }
        var generatedMappingPreviewSigningKey =
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var configuredMappingPreviewSigningKey =
            options.DynamicFlowMappingPreviewTokenSigningKey ??
            (options.EnableDynamicFlowP7MappingCandidate
                ? generatedMappingPreviewSigningKey
                : null);
        if (configuredMappingPreviewSigningKey is not null &&
            Encoding.UTF8.GetByteCount(
                configuredMappingPreviewSigningKey) < 32)
        {
            throw new ArgumentException(
                "Dynamic Flow mapping preview-token signing key must contain at least 32 UTF-8 bytes.",
                nameof(options));
        }
        var mappingPreviewSigningKey =
            configuredMappingPreviewSigningKey ??
            generatedMappingPreviewSigningKey;
        var stdoutPath = Path.Combine(iterationRoot, "backend.stdout.log");
        var stderrPath = Path.Combine(iterationRoot, "backend.stderr.log");
        var localAppData = Path.Combine(iterationRoot, "localappdata");
        var appData = Path.Combine(iterationRoot, "appdata");
        var tusPath = Path.Combine(iterationRoot, "tus");
        var uploadPath = Path.Combine(iterationRoot, "upload-sessions");
        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(tusPath);
        Directory.CreateDirectory(uploadPath);

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet))
            dotnet = "dotnet";

        var hangfirePrefix = $"p1hf_{new string(runKey.Where(char.IsLetterOrDigit).Take(24).ToArray()).ToLowerInvariant()}";
        var environment = new Dictionary<string, string?>
        {
            [P11ContinuationSnapshot.KeyEnvironmentVariable] = null,
            ["ASPNETCORE_ENVIRONMENT"] =
                options.EnvironmentNameOverride ?? "Testing",
            ["DOTNET_ENVIRONMENT"] =
                options.EnvironmentNameOverride ?? "Testing",
            ["AppTime__TestingFixedUtcNow"] =
                fixedUtcNow?.ToString("O"),
            ["ASPNETCORE_URLS"] = baseUri.GetLeftPart(UriPartial.Authority),
            ["Mongo__ConnectionString"] =
                options.MongoConnectionStringOverride ??
                mongo.ConnectionString,
            ["Mongo__Database"] =
                options.MongoDatabaseNameOverride ??
                mongo.DatabaseName,
            ["Mongo__TestingSkipIndexInitialization"] =
                options.SkipMongoIndexInitializationForTesting
                    ? "true"
                    : null,
            ["StatRunCandidate__Enabled"] =
                options.P9StatRunCandidate?.Enabled == true
                    ? "true"
                    : options.P9StatRunCandidate is null
                        ? null
                        : "false",
            ["StatRunCandidate__ChainId"] =
                options.P9StatRunCandidate?.ChainId,
            ["StatRunCandidate__ExpectedDatabase"] =
                options.P9StatRunCandidate?.ExpectedDatabase,
            ["StatRunCandidate__ExpectedDatabasePrefix"] =
                options.P9StatRunCandidate?.ExpectedDatabasePrefix,
            ["StatRunCandidate__CatalogPath"] =
                options.P9StatRunCandidate?.CatalogPath,
            ["StatRunCandidate__CatalogRawSha256"] =
                options.P9StatRunCandidate?.CatalogRawSha256,
            ["StatRunCandidate__CatalogSemanticSha256"] =
                options.P9StatRunCandidate?.CatalogSemanticSha256,
            ["StatRunCandidate__SchemaPath"] =
                options.P9StatRunCandidate?.SchemaPath,
            ["StatRunCandidate__SchemaRawSha256"] =
                options.P9StatRunCandidate?.SchemaRawSha256,
            ["StatRunCandidate__SchemaSemanticSha256"] =
                options.P9StatRunCandidate?.SchemaSemanticSha256,
            ["StatRunCandidate__StageLockPath"] =
                options.P9StatRunCandidate?.StageLockPath,
            ["StatRunCandidate__StageLockSha256"] =
                options.P9StatRunCandidate?.StageLockSha256,
            ["StatisticReconciliationCandidate__Enabled"] =
                options.P10ReconciliationCandidate?.Enabled == true
                    ? "true"
                    : options.P10ReconciliationCandidate is null
                        ? null
                        : "false",
            ["StatisticReconciliationCandidate__ChainId"] =
                options.P10ReconciliationCandidate?.ChainId,
            ["StatisticReconciliationCandidate__ExpectedDatabase"] =
                options.P10ReconciliationCandidate?.ExpectedDatabase,
            ["StatisticReconciliationCandidate__ExpectedDatabasePrefix"] =
                options.P10ReconciliationCandidate?.ExpectedDatabasePrefix,
            ["StatisticReconciliationCandidate__CatalogPath"] =
                options.P10ReconciliationCandidate?.CatalogPath,
            ["StatisticReconciliationCandidate__CatalogRawSha256"] =
                options.P10ReconciliationCandidate?.CatalogRawSha256,
            ["StatisticReconciliationCandidate__CatalogSemanticSha256"] =
                options.P10ReconciliationCandidate?.CatalogSemanticSha256,
            ["StatisticReconciliationCandidate__SchemaPath"] =
                options.P10ReconciliationCandidate?.SchemaPath,
            ["StatisticReconciliationCandidate__SchemaRawSha256"] =
                options.P10ReconciliationCandidate?.SchemaRawSha256,
            ["StatisticReconciliationCandidate__SchemaSemanticSha256"] =
                options.P10ReconciliationCandidate?.SchemaSemanticSha256,
            ["StatisticReconciliationCandidate__EvidencePath"] =
                options.P10ReconciliationCandidate?.EvidencePath,
            ["StatisticReconciliationCandidate__EvidenceSha256"] =
                options.P10ReconciliationCandidate?.EvidenceSha256,
            ["StatisticReconciliationCandidate__StageLockPath"] =
                options.P10ReconciliationCandidate?.StageLockPath,
            ["StatisticReconciliationCandidate__StageLockSha256"] =
                options.P10ReconciliationCandidate?.StageLockSha256,
            ["StatisticReconciliation__MaxRetryCount"] =
                options.P10ReconciliationMaxRetryCount?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["StatisticReconciliation__LeaseSeconds"] =
                options.P10ReconciliationLeaseSeconds?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["StatisticReconciliation__RetryBaseSeconds"] =
                options.P10ReconciliationRetryBaseSeconds?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["StatisticReconciliation__RetryMaxSeconds"] =
                options.P10ReconciliationRetryMaxSeconds?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["StatisticReconciliation__SlaSeconds"] =
                options.P10ReconciliationSlaSeconds?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["StatisticReconciliation__ActualObservation__ApiOwner__BaseUri"] =
                p10ActualApiOwnerAuthorizationParameter is null
                    ? null
                    : baseUri.ToString(),
            ["StatisticReconciliation__ActualObservation__ApiOwner__ServiceAuthorizationScheme"] =
                p10ActualApiOwnerAuthorizationParameter is null
                    ? null
                    : "Bearer",
            ["StatisticReconciliation__ActualObservation__ApiOwner__ServiceAuthorizationParameter"] =
                p10ActualApiOwnerAuthorizationParameter,
            ["Redis__Enabled"] = "false",
            ["Hangfire__ServerEnabled"] =
                options.HangfireServerEnabled ? "true" : "false",
            ["Hangfire__DashboardEnabled"] = "false",
            ["Hangfire__RecurringRegistrationEnabled"] =
                options.HangfireRecurringRegistrationEnabled ? "true" : "false",
            ["Hangfire__Prefix"] = hangfirePrefix,
            ["Logging__LogLevel__Hangfire"] = "Warning",
            ["StatConfigOperations__MaxActiveJobsPerActor"] =
                options.StatConfigOperationsMaxActiveJobsPerActor?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["StatConfigOperations__TestingFault__CommandId"] = options.StatConfigOperationsFaultCommandId,
            ["StatConfigOperations__TestingFault__CommandIdPrefix"] = options.StatConfigOperationsFaultCommandIdPrefix,
            ["StatConfigOperations__TestingFault__Points"] = null,
            ["WorkReportLifecycleProjectionOutbox__TestingFailOnceCommandId"] = P3LifecycleProjectionFailureCommandId,
            ["DynamicFlowDefinition__TestingFault__CommandId"] = null,
            ["DynamicFlowDefinition__TestingFault__CommandIdPrefix"] = P4DefinitionFaultCommandIdPrefix,
            ["DynamicFlowRuntime__TestingCandidateActivationEnabled"] =
                options.EnableDynamicFlowRuntimeCandidate ? "true" : "false",
            ["DynamicFlowRuntime__TestingCandidateActivationThrough"] =
                options.DynamicFlowRuntimeCandidateActivationThrough?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["DynamicFlowRuntime__TestingP7MappingCandidateActivationEnabled"] =
                options.EnableDynamicFlowP7MappingCandidate ? "true" : "false",
            ["DynamicFlowMapping__TestingCandidateActivationThrough"] =
                options.DynamicFlowP7MappingActivationThrough?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["DynamicFlowMapping__PreviewTokenSigningKey"] =
                configuredMappingPreviewSigningKey,
            ["DynamicFlowMapping__TestingReconcileFault__CommandId"] =
                options.DynamicFlowMappingReconcileFaultCommandId,
            ["DynamicFlowRuntime__TestingFault__CommandId"] =
                options.DynamicFlowRuntimeFaultCommandId,
            ["DynamicFlowRuntime__TestingFault__BranchOrdinal"] =
                options.DynamicFlowRuntimeFaultBranchOrdinal?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["DynamicFlowRuntime__TestingPause__CommandId"] =
                options.DynamicFlowRuntimePauseCommandId,
            ["DynamicFlowRuntime__TestingPause__BranchOrdinal"] =
                options.DynamicFlowRuntimePauseBranchOrdinal?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["DynamicFlowRuntime__TestingPause__Point"] =
                options.DynamicFlowRuntimePausePoint,
            ["DynamicFlowRuntime__TestingPause__ReachedFile"] =
                options.DynamicFlowRuntimePauseReachedFile,
            ["DynamicFlowRuntime__TestingPause__ReleaseFile"] =
                options.DynamicFlowRuntimePauseReleaseFile,
            ["DynamicFlowRuntime__TestingStateProjectionFault__CommandId"] =
                options.DynamicFlowRuntimeStateProjectionFaultCommandId,
            ["Cors__AllowedOrigins__0"] = options.FrontendOrigin,
            ["Frontend__Enabled"] = "false",
            ["SystemBootstrap__Key"] = bootstrapKey,
            ["Jwt__Issuer"] = "tdtd-p1-integration",
            ["Jwt__Audience"] = "tdtd-p1-integration-client",
            ["Jwt__Key"] = jwtKey,
            ["Jwt__AccessTokenMinutes"] = "30",
            ["Jwt__RefreshTokenDays"] = "1",
            ["Tus__TempPath"] = tusPath,
            ["Uploads__SessionDiskPath"] = uploadPath,
            ["Uploads__Bucket"] = "p1-integration",
            ["Minio__Endpoint"] = "127.0.0.1:1",
            ["Minio__AccessKey"] = "p1-integration",
            ["Minio__SecretKey"] = "p1-integration-secret",
            ["Minio__Bucket"] = "p1-integration",
            ["Logging__UseWindowsEventLog"] = "false",
            ["LOCALAPPDATA"] = localAppData,
            ["APPDATA"] = appData
        };
        for (var index = 0; index < options.StatConfigOperationsFaultPoints.Count; index++)
        {
            environment[$"StatConfigOperations__TestingFault__Points__{index}"] = options.StatConfigOperationsFaultPoints[index];
        }
        for (var index = 0; index < P4DefinitionFaultPoints.Length; index++)
        {
            environment[$"DynamicFlowDefinition__TestingFault__Points__{index}"] =
                P4DefinitionFaultPoints[index];
        }
        for (var index = 0;
             index <
             options.DynamicFlowMappingReconcileFaultPoints.Count;
             index++)
        {
            environment[
                    $"DynamicFlowMapping__TestingReconcileFault__Points__{index}"] =
                options.DynamicFlowMappingReconcileFaultPoints[index];
        }
        for (var bindingIndex = 0;
             bindingIndex < mappingApplyFaultBindings.Count;
             bindingIndex++)
        {
            var binding = mappingApplyFaultBindings[bindingIndex];
            environment[
                    $"DynamicFlowMapping__TestingApplyFault__Bindings__{bindingIndex}__CommandId"] =
                binding.CommandId;
            for (var pointIndex = 0;
                 pointIndex < binding.Points.Count;
                 pointIndex++)
            {
                environment[
                        $"DynamicFlowMapping__TestingApplyFault__Bindings__{bindingIndex}__Points__{pointIndex}"] =
                    binding.Points[pointIndex];
            }
        }
        for (var bindingIndex = 0;
             bindingIndex < mappingApplyPauseBindings.Count;
             bindingIndex++)
        {
            var binding = mappingApplyPauseBindings[bindingIndex];
            var prefix =
                $"DynamicFlowMapping__TestingApplyPause__Bindings__{bindingIndex}";
            environment[$"{prefix}__CommandId"] = binding.CommandId;
            environment[$"{prefix}__Point"] = binding.Point;
            environment[$"{prefix}__ReachedFile"] =
                binding.ReachedFile;
            environment[$"{prefix}__ReleaseFile"] =
                binding.ReleaseFile;
        }
        for (var bindingIndex = 0;
             bindingIndex < mappingReconcilePauseBindings.Count;
             bindingIndex++)
        {
            var binding = mappingReconcilePauseBindings[bindingIndex];
            var prefix =
                $"DynamicFlowMapping__TestingReconcilePause__Bindings__{bindingIndex}";
            environment[$"{prefix}__CommandId"] = binding.CommandId;
            environment[$"{prefix}__OutboxId"] = binding.OutboxId;
            environment[$"{prefix}__Point"] = binding.Point;
            environment[$"{prefix}__ReachedFile"] =
                binding.ReachedFile;
            environment[$"{prefix}__ReleaseFile"] =
                binding.ReleaseFile;
        }
        for (var index = 0; index < options.DynamicFlowRuntimeFaultPoints.Count; index++)
        {
            environment[$"DynamicFlowRuntime__TestingFault__Points__{index}"] =
                options.DynamicFlowRuntimeFaultPoints[index];
        }
        for (var index = 0;
             index < options.DynamicFlowRuntimeStateProjectionFaultPoints.Count;
             index++)
        {
            environment[
                $"DynamicFlowRuntime__TestingStateProjectionFault__Points__{index}"] =
                options.DynamicFlowRuntimeStateProjectionFaultPoints[index];
        }

        var contentRoot = ResolveContentRoot(paths, options);
        var processArguments = new List<string>
        {
            backendDll,
            "--urls",
            baseUri.GetLeftPart(UriPartial.Authority)
        };
        if (options.ContentRootPathOverride is not null)
        {
            processArguments.Add("--contentRoot");
            processArguments.Add(contentRoot);
        }

        var process = ManagedChildProcess.Start(
            dotnet,
            processArguments,
            paths.BackendRoot,
            environment,
            stdoutPath,
            stderrPath,
            stdoutLineSanitizer: SanitizeDurableProcessLogLine,
            stderrLineSanitizer: SanitizeDurableProcessLogLine);

        try
        {
            await WaitForReadyAsync(baseUri, process, stdoutPath, stderrPath, ct);
            return new BackendServerLease(
                process,
                baseUri,
                bootstrapKey,
                actorPassword,
                mappingPreviewSigningKey,
                stdoutPath,
                stderrPath,
                port);
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!StopVerified)
        {
            await _process.StopAsync(TimeSpan.FromSeconds(15));
            if (!_process.HasExited)
                throw new InvalidOperationException($"Backend child process {_process.Id} is still running after stop.");
            StopVerified = true;
        }

        if (!PortReleaseVerified)
        {
            await PortAllocator.WaitUntilNotAcceptingAsync(
                Port,
                TimeSpan.FromSeconds(10),
                "Backend");
            PortReleaseVerified = true;
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(StdoutPath))!,
                "backend-cleanup.json"),
            new
            {
                processId = ProcessId,
                port = Port,
                processStopped = StopVerified,
                portReleased = PortReleaseVerified
            });
    }

    public async ValueTask DisposeAsync() => await _process.DisposeAsync();

    internal static string SanitizeDurableProcessLogLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return UriUserInfoRegex.Replace(line, "${scheme}");
    }

    private static string ResolveContentRoot(
        HarnessPaths paths,
        BackendServerOptions options)
    {
        if (options.ContentRootPathOverride is null)
            return Path.GetFullPath(paths.BackendRoot);

        if (string.IsNullOrWhiteSpace(options.ContentRootPathOverride) ||
            !Path.IsPathFullyQualified(options.ContentRootPathOverride))
        {
            throw new ArgumentException(
                "Backend content-root override must be fully qualified.",
                nameof(options));
        }

        var ownedRoot = Path.GetFullPath(paths.RunRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var contentRoot = Path.GetFullPath(options.ContentRootPathOverride)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(ownedRoot, contentRoot);
        if (relative is "." or ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            !string.Equals(
                Path.GetFileName(contentRoot),
                "tdtd-be",
                StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(contentRoot) ||
            !File.Exists(Path.Combine(contentRoot, "tdtd-be.csproj")))
        {
            throw new ArgumentException(
                "Backend content-root override must be an owned tdtd-be fixture beneath the run root.",
                nameof(options));
        }

        var current = ownedRoot;
        if (!Directory.Exists(current) ||
            (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                "Backend content-root run root is unavailable or reparsed.",
                nameof(options));
        }
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((!Directory.Exists(current) && !File.Exists(current)) ||
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    "Backend content-root override contains a missing or reparsed path segment.",
                    nameof(options));
            }
        }
        return contentRoot;
    }

    private static IReadOnlyList<DynamicFlowMappingApplyFaultBinding>
        ValidateMappingApplyFaultBindings(
            IReadOnlyList<DynamicFlowMappingApplyFaultBinding> bindings)
    {
        var validated =
            new List<DynamicFlowMappingApplyFaultBinding>(
                bindings.Count);
        var commandIds =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var commandId = binding.CommandId;
            if (commandId is null ||
                !MappingCommandIdRegex.IsMatch(commandId))
            {
                throw new ArgumentException(
                    "Dynamic Flow mapping apply fault CommandId must match the production command-id contract.",
                    nameof(bindings));
            }
            if (!commandIds.Add(commandId))
            {
                throw new ArgumentException(
                    $"Duplicate Dynamic Flow mapping apply fault CommandId {commandId}.",
                    nameof(bindings));
            }
            if (binding.Points.Count == 0)
            {
                throw new ArgumentException(
                    $"Dynamic Flow mapping apply fault binding {commandId} requires at least one point.",
                    nameof(bindings));
            }
            var points =
                new HashSet<string>(StringComparer.Ordinal);
            foreach (var point in binding.Points)
            {
                if (!DynamicFlowMappingApplyFaultPoints.All.Contains(
                        point))
                {
                    throw new ArgumentException(
                        $"Unsupported Dynamic Flow mapping apply fault point {point}.",
                        nameof(bindings));
                }
                if (!points.Add(point))
                {
                    throw new ArgumentException(
                        $"Dynamic Flow mapping apply fault binding {commandId} repeats {point}.",
                        nameof(bindings));
                }
            }
            validated.Add(
                new DynamicFlowMappingApplyFaultBinding(
                    commandId,
                    points
                        .OrderBy(point => point, StringComparer.Ordinal)
                        .ToArray()));
        }
        return validated;
    }

    private static IReadOnlyList<DynamicFlowMappingApplyPauseBinding>
        ValidateMappingApplyPauseBindings(
            IReadOnlyList<DynamicFlowMappingApplyPauseBinding> bindings)
    {
        var validated =
            new List<DynamicFlowMappingApplyPauseBinding>(
                bindings.Count);
        var commandIds =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            ValidateMappingCommandId(
                binding.CommandId,
                nameof(bindings));
            if (!commandIds.Add(binding.CommandId))
            {
                throw new ArgumentException(
                    $"Duplicate Dynamic Flow mapping apply pause CommandId {binding.CommandId}.",
                    nameof(bindings));
            }
            if (!DynamicFlowMappingApplyFaultPoints.All.Contains(
                    binding.Point))
            {
                throw new ArgumentException(
                    $"Unsupported Dynamic Flow mapping apply pause point {binding.Point}.",
                    nameof(bindings));
            }
            var (reachedFile, releaseFile) =
                ValidateMarkerPaths(
                    binding.ReachedFile,
                    binding.ReleaseFile,
                    nameof(bindings));
            validated.Add(
                binding with
                {
                    ReachedFile = reachedFile,
                    ReleaseFile = releaseFile
                });
        }
        return validated;
    }

    private static IReadOnlyList<
        DynamicFlowMappingReconcilePauseBinding>
        ValidateMappingReconcilePauseBindings(
            IReadOnlyList<DynamicFlowMappingReconcilePauseBinding>
                bindings)
    {
        var validated =
            new List<DynamicFlowMappingReconcilePauseBinding>(
                bindings.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            ValidateMappingCommandId(
                binding.CommandId,
                nameof(bindings));
            if (!ObjectId.TryParse(binding.OutboxId, out _))
            {
                throw new ArgumentException(
                    $"Dynamic Flow mapping reconcile pause OutboxId {binding.OutboxId} is invalid.",
                    nameof(bindings));
            }
            if (!string.Equals(
                    binding.Point,
                    DynamicFlowMappingReconcilePausePoints
                        .AfterOutboxClaim,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Unsupported Dynamic Flow mapping reconcile pause point {binding.Point}.",
                    nameof(bindings));
            }
            if (!keys.Add(
                    $"{binding.CommandId}\n{binding.OutboxId}"))
            {
                throw new ArgumentException(
                    $"Duplicate Dynamic Flow mapping reconcile pause binding {binding.CommandId}/{binding.OutboxId}.",
                    nameof(bindings));
            }
            var (reachedFile, releaseFile) =
                ValidateMarkerPaths(
                    binding.ReachedFile,
                    binding.ReleaseFile,
                    nameof(bindings));
            validated.Add(
                binding with
                {
                    ReachedFile = reachedFile,
                    ReleaseFile = releaseFile
                });
        }
        return validated;
    }

    private static void ValidateMappingCommandId(
        string? commandId,
        string parameterName)
    {
        if (commandId is null ||
            !MappingCommandIdRegex.IsMatch(commandId))
        {
            throw new ArgumentException(
                "Dynamic Flow mapping test CommandId must match the production command-id contract.",
                parameterName);
        }
    }

    private static (string ReachedFile, string ReleaseFile)
        ValidateMarkerPaths(
            string? reachedFile,
            string? releaseFile,
            string parameterName)
    {
        if (string.IsNullOrWhiteSpace(reachedFile) ||
            string.IsNullOrWhiteSpace(releaseFile) ||
            !Path.IsPathFullyQualified(reachedFile) ||
            !Path.IsPathFullyQualified(releaseFile))
        {
            throw new ArgumentException(
                "Dynamic Flow mapping pause marker paths must be fully qualified.",
                parameterName);
        }
        reachedFile = Path.GetFullPath(reachedFile);
        releaseFile = Path.GetFullPath(releaseFile);
        var reachedDirectory = Path.GetDirectoryName(reachedFile);
        var releaseDirectory = Path.GetDirectoryName(releaseFile);
        if (string.IsNullOrWhiteSpace(reachedDirectory) ||
            !string.Equals(
                reachedDirectory,
                releaseDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                reachedFile,
                releaseFile,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetPathRoot(reachedDirectory),
                reachedDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Dynamic Flow mapping pause marker paths must be distinct files in the same non-root directory.",
                parameterName);
        }
        return (reachedFile, releaseFile);
    }

    private static async Task WaitForReadyAsync(
        Uri baseUri,
        ManagedChildProcess process,
        string stdoutPath,
        string stderrPath,
        CancellationToken ct)
    {
        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(120);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Backend exited with code {process.ExitCode}.\nSTDOUT:\n{LogTail.Read(stdoutPath)}\nSTDERR:\n{LogTail.Read(stderrPath)}");
            }

            try
            {
                using var response = await client.GetAsync("api/auth/me", ct);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException(
            $"Backend readiness timeout. Last={last?.Message}\nSTDOUT:\n{LogTail.Read(stdoutPath)}\nSTDERR:\n{LogTail.Read(stderrPath)}");
    }
}

internal sealed record BackendServerOptions
{
    public string? ActorPasswordOverride { get; init; }
    public string? EnvironmentNameOverride { get; init; }
    public string? ContentRootPathOverride { get; init; }
    public DateTime? FixedUtcNow { get; init; }
    public bool SuppressTestingFixedUtcNow { get; init; }
    public string? MongoConnectionStringOverride { get; init; }
    public string? MongoDatabaseNameOverride { get; init; }
    public string? JwtSigningKey { get; init; }
    public string? P10ActualApiOwnerServiceAuthorizationParameter
    {
        get;
        init;
    }
    public bool SkipMongoIndexInitializationForTesting { get; init; }
    public P9StatRunCandidateOptions? P9StatRunCandidate { get; init; }
    public P10ReconciliationCandidateOptions? P10ReconciliationCandidate { get; init; }
    public int? P10ReconciliationMaxRetryCount { get; init; }
    public int? P10ReconciliationLeaseSeconds { get; init; }
    public int? P10ReconciliationRetryBaseSeconds { get; init; }
    public int? P10ReconciliationRetryMaxSeconds { get; init; }
    public int? P10ReconciliationSlaSeconds { get; init; }
    public bool HangfireServerEnabled { get; init; }
    public bool HangfireRecurringRegistrationEnabled { get; init; }
    public bool EnableDynamicFlowRuntimeCandidate { get; init; }
    public bool EnableDynamicFlowP7MappingCandidate { get; init; }
    public int? DynamicFlowP7MappingActivationThrough { get; init; }
    public string? DynamicFlowMappingPreviewTokenSigningKey { get; init; }
    public int? DynamicFlowRuntimeCandidateActivationThrough { get; init; }
    public string? FrontendOrigin { get; init; }
    public int? StatConfigOperationsMaxActiveJobsPerActor { get; init; }
    public string? StatConfigOperationsFaultCommandId { get; init; }
    public string? StatConfigOperationsFaultCommandIdPrefix { get; init; }
    public IReadOnlyList<string> StatConfigOperationsFaultPoints { get; init; } = Array.Empty<string>();
    public string? DynamicFlowRuntimeFaultCommandId { get; init; }
    public int? DynamicFlowRuntimeFaultBranchOrdinal { get; init; }
    public IReadOnlyList<string> DynamicFlowRuntimeFaultPoints { get; init; } = Array.Empty<string>();
    public string? DynamicFlowRuntimePauseCommandId { get; init; }
    public int? DynamicFlowRuntimePauseBranchOrdinal { get; init; }
    public string? DynamicFlowRuntimePausePoint { get; init; }
    public string? DynamicFlowRuntimePauseReachedFile { get; init; }
    public string? DynamicFlowRuntimePauseReleaseFile { get; init; }
    public string? DynamicFlowRuntimeStateProjectionFaultCommandId { get; init; }
    public IReadOnlyList<string> DynamicFlowRuntimeStateProjectionFaultPoints { get; init; } =
        Array.Empty<string>();
    public string? DynamicFlowMappingReconcileFaultCommandId { get; init; }
    public IReadOnlyList<string> DynamicFlowMappingReconcileFaultPoints
    {
        get;
        init;
    } = Array.Empty<string>();
    public IReadOnlyList<DynamicFlowMappingApplyFaultBinding>
        DynamicFlowMappingApplyFaultBindings
    {
        get;
        init;
    } = Array.Empty<DynamicFlowMappingApplyFaultBinding>();
    public IReadOnlyList<DynamicFlowMappingApplyPauseBinding>
        DynamicFlowMappingApplyPauseBindings
    {
        get;
        init;
    } = Array.Empty<DynamicFlowMappingApplyPauseBinding>();
    public IReadOnlyList<DynamicFlowMappingReconcilePauseBinding>
        DynamicFlowMappingReconcilePauseBindings
    {
        get;
        init;
    } = Array.Empty<DynamicFlowMappingReconcilePauseBinding>();
}

internal sealed record P9StatRunCandidateOptions(
    bool Enabled,
    string ChainId,
    string ExpectedDatabase,
    string ExpectedDatabasePrefix,
    string CatalogPath,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaPath,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string StageLockPath,
    string StageLockSha256);

internal sealed record P10ReconciliationCandidateOptions(
    bool Enabled,
    string ChainId,
    string ExpectedDatabase,
    string ExpectedDatabasePrefix,
    string CatalogPath,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaPath,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string EvidencePath,
    string EvidenceSha256,
    string StageLockPath,
    string StageLockSha256);

internal sealed record DynamicFlowMappingApplyFaultBinding(
    string CommandId,
    IReadOnlyList<string> Points);

internal sealed record DynamicFlowMappingApplyPauseBinding(
    string CommandId,
    string Point,
    string ReachedFile,
    string ReleaseFile);

internal sealed record DynamicFlowMappingReconcilePauseBinding(
    string CommandId,
    string OutboxId,
    string Point,
    string ReachedFile,
    string ReleaseFile);


