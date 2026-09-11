using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class DynamicFlowMappingP711ApplyFaultContractTests
{
    public static void Run()
    {
        ExactPointCatalogIsFrozen();
        TestingBindingsAreExactAndOneShot();
        ApplyPauseRunsBeforeFaultAndIsOneShot();
        ReconcileClaimPauseIsExactAndOneShot();
        UnsafePauseConfigurationIsRejected();
        ProductionIsInert();
        ApplyPipelineOwnsEveryExactBoundary();
        ReconcileClaimPauseOwnsTheLeaseBoundary();
        DependencyInjectionAndHarnessBindingsAreExplicit();
    }

    private static void ExactPointCatalogIsFrozen()
    {
        var expected = new[]
        {
            "BEFORE_RECEIPT",
            "AFTER_RECEIPT",
            "BEFORE_PAYLOAD",
            "AFTER_PAYLOAD",
            "BEFORE_PROJECTION",
            "AFTER_PROJECTION",
            "BEFORE_INTENT_COMMIT",
            "AFTER_INTENT_COMMIT",
            "BEFORE_OUTBOX",
            "AFTER_OUTBOX",
            "BEFORE_AUDIT",
            "AFTER_AUDIT",
            "BEFORE_TRANSACTION_COMMIT",
            "AFTER_TRANSACTION_COMMIT"
        };
        Require(
            DynamicFlowMappingApplyFaultPoints.All.Count == expected.Length &&
            DynamicFlowMappingApplyFaultPoints.All.SetEquals(expected),
            "P7-11 mapping apply fault point catalog drifted.");
    }

    private static void TestingBindingsAreExactAndOneShot()
    {
        const string command = "p711-apply-fault-command-001";
        const string secondCommand = "p711-apply-fault-command-002";
        var values = new Dictionary<string, string?>();
        var points = DynamicFlowMappingApplyFaultPoints.All
            .OrderBy(point => point, StringComparer.Ordinal)
            .ToArray();
        values[
                $"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:0:CommandId"] =
            command;
        for (var index = 0; index < points.Length; index++)
        {
            values[
                    $"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:0:Points:{index}"] =
                points[index];
        }
        values[
                $"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:1:CommandId"] =
            secondCommand;
        values[
                $"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:1:Points:0"] =
            DynamicFlowMappingApplyFaultPoints.BeforeReceipt;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var injector = new DynamicFlowMappingApplyFaultInjector(
            new ContractHostEnvironment("Testing"),
            configuration);
        foreach (var point in points)
        {
            injector.ThrowIfConfigured("different-command", point);
            var threw = false;
            try
            {
                injector.ThrowIfConfigured(command, point);
            }
            catch (DynamicFlowMappingApplyInjectedFaultException error)
            {
                threw = error.Message ==
                        $"{DynamicFlowMappingApplyFaultInjector.FailureMessage}:{point}";
            }
            Require(
                threw,
                $"Testing apply fault point {point} did not throw its exact marker.");
            injector.ThrowIfConfigured(command, point);
        }

        var secondThrew = false;
        try
        {
            injector.ThrowIfConfigured(
                secondCommand,
                DynamicFlowMappingApplyFaultPoints.BeforeReceipt);
        }
        catch (DynamicFlowMappingApplyInjectedFaultException)
        {
            secondThrew = true;
        }
        Require(
            secondThrew,
            "A second exact command binding did not retain independent one-shot state.");

        var invalidConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:0:CommandId"] =
                        command,
                    [$"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:0:Points:0"] =
                        "UNSUPPORTED_POINT"
                })
            .Build();
        var rejected = false;
        try
        {
            _ = new DynamicFlowMappingApplyFaultInjector(
                new ContractHostEnvironment("Testing"),
                invalidConfiguration);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Require(
            rejected,
            "Testing configuration accepted an unsupported apply fault point.");
    }

    private static void ProductionIsInert()
    {
        const string command = "p711-production-inert-command";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:0:CommandId"] =
                        command,
                    [$"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:0:Points:0"] =
                        DynamicFlowMappingApplyFaultPoints.BeforeReceipt,
                    [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:CommandId"] =
                        command,
                    [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:Point"] =
                        DynamicFlowMappingApplyFaultPoints.BeforeReceipt,
                    [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:ReachedFile"] =
                        "production-must-ignore-relative-reached",
                    [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:ReleaseFile"] =
                        "production-must-ignore-relative-release"
                })
            .Build();
        var injector = new DynamicFlowMappingApplyFaultInjector(
            new ContractHostEnvironment("Production"),
            configuration);
        foreach (var point in DynamicFlowMappingApplyFaultPoints.All)
            injector.ThrowIfConfigured(command, point);

        var reconcileConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{DynamicFlowMappingReconcileFaultInjector.PauseBindingsConfigurationKey}:0:CommandId"] =
                        command,
                    [$"{DynamicFlowMappingReconcileFaultInjector.PauseBindingsConfigurationKey}:0:OutboxId"] =
                        "not-an-object-id",
                    [$"{DynamicFlowMappingReconcileFaultInjector.PauseBindingsConfigurationKey}:0:Point"] =
                        "NOT_A_REAL_POINT",
                    [$"{DynamicFlowMappingReconcileFaultInjector.PauseBindingsConfigurationKey}:0:ReachedFile"] =
                        "production-must-ignore-relative-reached",
                    [$"{DynamicFlowMappingReconcileFaultInjector.PauseBindingsConfigurationKey}:0:ReleaseFile"] =
                        "production-must-ignore-relative-release"
                })
            .Build();
        var reconcileInjector =
            new DynamicFlowMappingReconcileFaultInjector(
                new ContractHostEnvironment("Production"),
                reconcileConfiguration);
        reconcileInjector.PauseAfterOutboxClaimIfConfigured(
            command,
            "64a000000000000000000001");
    }

    private static void ApplyPauseRunsBeforeFaultAndIsOneShot()
    {
        const string command = "p711-apply-pause-command-001";
        const string otherCommand = "p711-apply-pause-command-002";
        const string point = DynamicFlowMappingApplyFaultPoints.BeforeReceipt;
        var markerDirectory = CreateMarkerDirectory("apply");
        var reachedFile = Path.Combine(markerDirectory, "reached.marker");
        var releaseFile = Path.Combine(markerDirectory, "release.marker");
        try
        {
            File.WriteAllText(releaseFile, "release");
            var values = new Dictionary<string, string?>
            {
                [$"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:0:CommandId"] =
                    command,
                [$"{DynamicFlowMappingApplyFaultInjector.BindingsConfigurationKey}:0:Points:0"] =
                    point,
                [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:CommandId"] =
                    command,
                [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:Point"] =
                    point,
                [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:ReachedFile"] =
                    reachedFile,
                [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:ReleaseFile"] =
                    releaseFile
            };
            var injector = new DynamicFlowMappingApplyFaultInjector(
                new ContractHostEnvironment("Testing"),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(values)
                    .Build());

            injector.ThrowIfConfigured(
                otherCommand,
                point);
            injector.ThrowIfConfigured(
                command,
                DynamicFlowMappingApplyFaultPoints.AfterReceipt);
            Require(
                !File.Exists(reachedFile),
                "Apply pause matched a different command or point.");

            var threw = false;
            try
            {
                injector.ThrowIfConfigured(command, point);
            }
            catch (DynamicFlowMappingApplyInjectedFaultException error)
            {
                threw = error.Message ==
                        $"{DynamicFlowMappingApplyFaultInjector.FailureMessage}:{point}";
            }
            Require(
                threw,
                "Apply pause did not continue into the configured fault.");
            Require(
                File.ReadAllText(reachedFile) ==
                $"{command}\n{point}\n",
                "Apply pause did not publish its exact reached marker before throwing.");

            File.Delete(reachedFile);
            injector.ThrowIfConfigured(command, point);
            Require(
                !File.Exists(reachedFile),
                "Apply pause/fault binding was not one-shot.");
        }
        finally
        {
            DeleteMarkerDirectory(markerDirectory);
        }
    }

    private static void ReconcileClaimPauseIsExactAndOneShot()
    {
        const string command = "p711-reconcile-pause-command-001";
        const string otherCommand =
            "p711-reconcile-pause-command-002";
        const string outboxId = "64a000000000000000000001";
        const string otherOutboxId = "64a000000000000000000002";
        var markerDirectory = CreateMarkerDirectory("reconcile");
        var reachedFile = Path.Combine(markerDirectory, "reached.marker");
        var releaseFile = Path.Combine(markerDirectory, "release.marker");
        try
        {
            File.WriteAllText(releaseFile, "release");
            var key =
                DynamicFlowMappingReconcileFaultInjector
                    .PauseBindingsConfigurationKey;
            var injector =
                new DynamicFlowMappingReconcileFaultInjector(
                    new ContractHostEnvironment("Testing"),
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                [$"{key}:0:CommandId"] = command,
                                [$"{key}:0:OutboxId"] = outboxId,
                                [$"{key}:0:Point"] =
                                    DynamicFlowMappingReconcilePausePoints
                                        .AfterOutboxClaim,
                                [$"{key}:0:ReachedFile"] = reachedFile,
                                [$"{key}:0:ReleaseFile"] = releaseFile
                            })
                        .Build());

            injector.PauseAfterOutboxClaimIfConfigured(
                otherCommand,
                outboxId);
            injector.PauseAfterOutboxClaimIfConfigured(
                command,
                otherOutboxId);
            Require(
                !File.Exists(reachedFile),
                "Reconcile pause matched a different command or outbox.");

            injector.PauseAfterOutboxClaimIfConfigured(
                command,
                outboxId);
            Require(
                File.ReadAllText(reachedFile) ==
                $"{command}\n{outboxId}\n{DynamicFlowMappingReconcilePausePoints.AfterOutboxClaim}\n",
                "Reconcile pause did not publish its exact lease-claim marker.");

            File.Delete(reachedFile);
            injector.PauseAfterOutboxClaimIfConfigured(
                command,
                outboxId);
            Require(
                !File.Exists(reachedFile),
                "Reconcile command/outbox pause binding was not one-shot.");
        }
        finally
        {
            DeleteMarkerDirectory(markerDirectory);
        }
    }

    private static void UnsafePauseConfigurationIsRejected()
    {
        const string command = "p711-unsafe-pause-command-001";
        var applyRejected = false;
        try
        {
            _ = new DynamicFlowMappingApplyFaultInjector(
                new ContractHostEnvironment("Testing"),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:CommandId"] =
                                command,
                            [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:Point"] =
                                DynamicFlowMappingApplyFaultPoints.BeforeReceipt,
                            [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:ReachedFile"] =
                                "relative-reached.marker",
                            [$"{DynamicFlowMappingApplyFaultInjector.PauseBindingsConfigurationKey}:0:ReleaseFile"] =
                                "relative-release.marker"
                        })
                    .Build());
        }
        catch (InvalidOperationException)
        {
            applyRejected = true;
        }
        Require(
            applyRejected,
            "Testing apply pause accepted relative marker paths.");

        var reconcileRejected = false;
        try
        {
            var key =
                DynamicFlowMappingReconcileFaultInjector
                    .PauseBindingsConfigurationKey;
            _ = new DynamicFlowMappingReconcileFaultInjector(
                new ContractHostEnvironment("Testing"),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            [$"{key}:0:CommandId"] = command,
                            [$"{key}:0:OutboxId"] = "not-an-object-id",
                            [$"{key}:0:Point"] =
                                DynamicFlowMappingReconcilePausePoints
                                    .AfterOutboxClaim,
                            [$"{key}:0:ReachedFile"] =
                                Path.GetFullPath("reached.marker"),
                            [$"{key}:0:ReleaseFile"] =
                                Path.GetFullPath("release.marker")
                        })
                    .Build());
        }
        catch (InvalidOperationException)
        {
            reconcileRejected = true;
        }
        Require(
            reconcileRejected,
            "Testing reconcile pause accepted a non-ObjectId outbox binding.");
    }

    private static void ApplyPipelineOwnsEveryExactBoundary()
    {
        var source = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var method = Slice(
            source,
            "public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(",
            "private async Task<WorkAssignmentReportResponse>\n        PreflightDynamicFlowMappingApplyAsync(");
        AssertOrdered(
            method,
            "BeforeReceipt",
            "DynamicFlowMappingApplyReceipts.InsertOneAsync",
            "AfterReceipt",
            "BeforePayload",
            "_payloadWriter.SaveReportPayloadAsync",
            "AfterPayload",
            "BeforeProjection",
            "_sectionProjection.ProjectAndVerifyAsync",
            "AfterProjection",
            "BeforeIntentCommit",
            "DynamicFlowMappingProvenanceRecords.InsertOneAsync",
            "DynamicFlowMappingEvents.InsertOneAsync",
            "AfterIntentCommit",
            "BeforeOutbox",
            "DynamicFlowMappingOutbox.InsertOneAsync",
            "AfterOutbox",
            "BeforeAudit",
            "WorkAssignmentReportLogs.InsertOneAsync",
            "AfterAudit",
            "BeforeTransactionCommit",
            "AfterTransactionCommit");
        Require(
            Count(
                method,
                "_dynamicFlowMappingApplyFaultInjector.ThrowIfConfigured(") ==
            DynamicFlowMappingApplyFaultPoints.All.Count,
            "Apply pipeline must own exactly one hook for every P7-11 point.");
        Require(
            method.Contains(
                "// ExecuteAsync returned only after the Mongo transaction commit.",
                StringComparison.Ordinal) &&
            method.Contains(
                "if (!transactionOutcome.IsReplay)",
                StringComparison.Ordinal),
            "AFTER_TRANSACTION_COMMIT must stay outside ExecuteAsync and skip receipt replay.");
    }

    private static void ReconcileClaimPauseOwnsTheLeaseBoundary()
    {
        var source = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/DynamicFlowMappingOutboxReconciler.cs");
        var method = Slice(
            source,
            "public async Task<bool> ProcessByIdAsync(",
            "private async Task<DynamicFlowMappingOutboxItem?> TryClaimAsync(");
        AssertOrdered(
            method,
            "var claimed = await TryClaimAsync(",
            "_faults.PauseAfterOutboxClaimIfConfigured(",
            "MarkRetryingAsync(claimed, ct)");
        Require(
            Count(
                method,
                "_faults.PauseAfterOutboxClaimIfConfigured(") == 1,
            "Reconcile must pause exactly once after a successful outbox claim.");
    }

    private static void DependencyInjectionAndHarnessBindingsAreExplicit()
    {
        var program = ReadBackendSource("Program.cs");
        Require(
            program.Contains(
                "IDynamicFlowMappingApplyFaultInjector",
                StringComparison.Ordinal) &&
            program.Contains(
                "DynamicFlowMappingApplyFaultInjector",
                StringComparison.Ordinal),
            "P7-11 mapping apply fault injector is missing from DI.");
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        Require(
            service.Contains(
                "_dynamicFlowMappingApplyFaultInjector",
                StringComparison.Ordinal),
            "WorkAssignmentReportService does not receive the P7-11 injector.");
        var harness = ReadBackendSource(
            "tests/tdtd-be.IntegrationTests/BackendServerLease.cs");
        foreach (var marker in new[]
                 {
                     "DynamicFlowMappingApplyFaultBindings",
                     "TestingApplyFault__Bindings__{bindingIndex}__CommandId",
                     "TestingApplyFault__Bindings__{bindingIndex}__Points__{pointIndex}",
                     "DynamicFlowMappingApplyFaultPoints.All.Contains",
                     "DynamicFlowMappingApplyPauseBindings",
                     "TestingApplyPause__Bindings__{bindingIndex}",
                     "DynamicFlowMappingReconcilePauseBindings",
                     "TestingReconcilePause__Bindings__{bindingIndex}",
                     "Path.IsPathFullyQualified",
                     "DynamicFlowMappingReconcilePausePoints",
                     "ObjectId.TryParse"
                 })
        {
            Require(
                harness.Contains(marker, StringComparison.Ordinal),
                $"BackendServerLease is missing safe exact binding marker {marker}.");
        }
        Require(
            !harness.Contains(
                "TestingApplyFault__CommandIdPrefix",
                StringComparison.Ordinal),
            "P7-11 apply faults must not accept a command prefix.");

        var applyInjector = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/DynamicFlowMappingApplyFaultInjector.cs");
        AssertOrdered(
            applyInjector,
            "PauseIfConfigured(normalizedCommandId, faultPoint);",
            "_bindings.TryGetValue(",
            "throw new DynamicFlowMappingApplyInjectedFaultException(");
        Require(
            applyInjector.Contains(
                "TimeSpan.FromSeconds(120)",
                StringComparison.Ordinal) &&
            applyInjector.Contains(
                "flushToDisk: true",
                StringComparison.Ordinal),
            "Apply pause must use a durable marker and a bounded 120-second wait.");

        var reconcileInjector = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/DynamicFlowMappingReconcileFaultInjector.cs");
        Require(
            reconcileInjector.Contains(
                "TimeSpan.FromSeconds(120)",
                StringComparison.Ordinal) &&
            reconcileInjector.Contains(
                "BuildPauseKey(",
                StringComparison.Ordinal),
            "Reconcile pause must be exact-command/outbox and bounded.");
    }

    private static string CreateMarkerDirectory(string suffix)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"tdtd-p711-contract-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Path.GetFullPath(directory);
    }

    private static void DeleteMarkerDirectory(string directory)
    {
        foreach (var fileName in new[]
                 {
                     "reached.marker",
                     "release.marker"
                 })
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        if (Directory.Exists(directory))
            Directory.Delete(directory);
    }

    private static void AssertOrdered(
        string source,
        params string[] markers)
    {
        var cursor = 0;
        foreach (var marker in markers)
        {
            var index = source.IndexOf(
                marker,
                cursor,
                StringComparison.Ordinal);
            Require(
                index >= 0,
                $"Apply fault boundary marker {marker} is missing/out of order.");
            cursor = index + marker.Length;
        }
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var cursor = 0;
        while (true)
        {
            var index = source.IndexOf(
                value,
                cursor,
                StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            cursor = index + value.Length;
        }
    }

    private static string Slice(
        string source,
        string start,
        string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = startIndex < 0
            ? -1
            : source.IndexOf(
                end,
                startIndex + start.Length,
                StringComparison.Ordinal);
        Require(
            startIndex >= 0 && endIndex > startIndex,
            $"Source anchors were not found: {start} -> {end}.");
        return source[startIndex..endIndex];
    }

    private static string ReadBackendSource(string relativePath)
    {
        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(seed);
                 directory is not null;
                 directory = directory.Parent)
            {
                var direct = Path.Combine(
                    directory.FullName,
                    "tdtd-be.csproj");
                if (File.Exists(direct))
                {
                    return File.ReadAllText(
                            Path.Combine(
                                directory.FullName,
                                relativePath.Replace(
                                    '/',
                                    Path.DirectorySeparatorChar)))
                        .Replace(
                            "\r\n",
                            "\n",
                            StringComparison.Ordinal);
                }
                var nested = Path.Combine(
                    directory.FullName,
                    "tdtd-be");
                if (File.Exists(
                        Path.Combine(nested, "tdtd-be.csproj")))
                {
                    return File.ReadAllText(
                            Path.Combine(
                                nested,
                                relativePath.Replace(
                                    '/',
                                    Path.DirectorySeparatorChar)))
                        .Replace(
                            "\r\n",
                            "\n",
                            StringComparison.Ordinal);
                }
            }
        }
        throw new InvalidOperationException(
            "Could not locate the tdtd-be source root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ContractHostEnvironment : IHostEnvironment
    {
        public ContractHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "tdtd-be.Tests";
        public string ContentRootPath { get; set; } =
            AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
