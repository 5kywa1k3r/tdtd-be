using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using tdtd_be.Controllers;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;

internal static class StatConfigOperationsP808ContractTests
{
    private static readonly string[] ForbiddenIdentityNames =
    [
        "DatasetId",
        "DatasetVersionId",
        "ReportId",
        "WorkReportId",
        "WorkAssignmentReportId",
        "WorkReportPeriodId",
        "ResultId",
        "ExportId"
    ];

    public static void Run()
    {
        StateVocabularyAndExternalMappingAreExact();
        PersistenceAndApiShapesCarryNoDatasetOrReportIdentity();
        PublicDtosRedactWorkerAndDiagnosticSecrets();
        ReadinessRoutesAuthenticateAndAuthorizeBeforeLookup();
        ServiceAtomicityQuotaAndLeaseGuardsAreFrozen();
        ReadinessWorkerCannotCallResultMaterializers();
        ReadinessIndexesAreSingleOwnerAndFailClosed();
        StateHashUsesBsonMillisecondPrecision();
    }

    private static void StateVocabularyAndExternalMappingAreExact()
    {
        AssertEqual(
            "stat-config-readiness",
            StatConfigValidationQueue.Name,
            "dedicated readiness queue");
        AssertSetEqual(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "PENDING",
                "RUNNING",
                "RETRY_WAITING",
                "COMPLETED",
                "FAILED",
                "CANCELLED",
                "RESET"
            },
            StatConfigValidationJobStatuses.All,
            "internal readiness states");

        var external = typeof(StatConfigValidationExternalStatuses)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        AssertSetEqual(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "QUEUED",
                "RUNNING",
                "RETRYING",
                "DONE",
                "FAILED",
                "CANCELLED",
                "RESET"
            },
            external,
            "external readiness states");

        var source = ReadOperationsServiceSource();
        var exactMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(StatConfigValidationJobStatuses.Pending)] =
                nameof(StatConfigValidationExternalStatuses.Queued),
            [nameof(StatConfigValidationJobStatuses.Running)] =
                nameof(StatConfigValidationExternalStatuses.Running),
            [nameof(StatConfigValidationJobStatuses.RetryWaiting)] =
                nameof(StatConfigValidationExternalStatuses.Retrying),
            [nameof(StatConfigValidationJobStatuses.Completed)] =
                nameof(StatConfigValidationExternalStatuses.Done),
            [nameof(StatConfigValidationJobStatuses.Failed)] =
                nameof(StatConfigValidationExternalStatuses.Failed),
            [nameof(StatConfigValidationJobStatuses.Cancelled)] =
                nameof(StatConfigValidationExternalStatuses.Cancelled),
            [nameof(StatConfigValidationJobStatuses.Reset)] =
                nameof(StatConfigValidationExternalStatuses.Reset)
        };
        foreach (var (internalName, externalName) in exactMap)
        {
            AssertRegex(
                source,
                $@"StatConfigValidationJobStatuses\s*\.\s*{internalName}\s*=>\s*" +
                $@"StatConfigValidationExternalStatuses\s*\.\s*{externalName}",
                $"state map {internalName}->{externalName}");
        }
    }

    private static void PersistenceAndApiShapesCarryNoDatasetOrReportIdentity()
    {
        var forbidden = ForbiddenIdentityNames.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var contractTypes = new[]
        {
            typeof(StatConfigValidationJob),
            typeof(StatConfigAuditOutboxItem),
            typeof(StatConfigValidationEnqueuePayload),
            typeof(StatConfigValidationJobStatusResponse),
            typeof(StatConfigValidationJobDiagnosticsResponse)
        };
        foreach (var type in contractTypes)
        {
            var leaked = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Where(forbidden.Contains)
                .ToArray();
            AssertEqual(
                0,
                leaked.Length,
                $"{type.Name} dataset/report identities");
        }

        AssertPropertiesInclude(
            typeof(StatConfigValidationJob),
            "OwnerKind",
            "OwnerId",
            "ConfigId",
            "VersionId",
            "VersionNo",
            "ConfigRevision",
            "ConfigHash",
            "DependencyPinsHash",
            "BundleHash",
            "EnqueueCommandId",
            "CommandReceiptId",
            "AuditOutboxId");
        AssertPropertiesInclude(
            typeof(StatConfigAuditOutboxItem),
            "TargetKind",
            "TargetId",
            "ConfigId",
            "VersionId",
            "VersionNo",
            "ConfigRevision",
            "ConfigHash",
            "StateRevision",
            "StateHash");

        var source = ReadOperationsServiceSource();
        foreach (var symbol in ForbiddenIdentityNames)
            AssertDoesNotContain(
                source,
                symbol,
                $"readiness service identity '{symbol}'");
    }

    private static void PublicDtosRedactWorkerAndDiagnosticSecrets()
    {
        AssertPropertiesEqual(
            typeof(StatConfigValidationEnqueuePayload),
            "ConfigId",
            "VersionId",
            "VersionNo");
        AssertStrictJson(typeof(StatConfigValidationEnqueuePayload));
        AssertStrictJson(typeof(StatConfigValidationResetPayload));
        AssertStrictJson(typeof(StatConfigValidationCancelPayload));
        AssertStrictJson(typeof(StatConfigValidationCleanupPayload));

        AssertPropertiesExclude(
            typeof(StatConfigValidationJobStatusResponse),
            "QueueName",
            "DedupeKey",
            "DependencyPins",
            "DependencyPinsHash",
            "RequestedByUserId",
            "ClaimToken",
            "LeaseOwnerId",
            "LeaseUntilUtc",
            "StateRevision",
            "StateHash",
            "DiagnosticCode",
            "DiagnosticMessage",
            "FailureFingerprint",
            "StackTrace",
            "ConnectionString",
            "RawPayload",
            "SourceContent",
            "QueuePayload");
        AssertPropertiesExclude(
            typeof(StatConfigValidationJobDiagnosticsResponse),
            "QueueName",
            "DedupeKey",
            "DependencyPins",
            "ClaimToken",
            "LeaseOwnerId",
            "StackTrace",
            "ConnectionString",
            "RawPayload",
            "SourceContent",
            "QueuePayload");
    }

    private static void ReadinessRoutesAuthenticateAndAuthorizeBeforeLookup()
    {
        AssertAuthenticatedController(typeof(StatConfigReadinessController));
        AssertAuthenticatedController(typeof(StatConfigReadinessAdminController));
        AssertEqual(
            "api/stat-config",
            typeof(StatConfigReadinessController)
                .GetCustomAttribute<RouteAttribute>()?.Template,
            "public readiness route");
        AssertEqual(
            "api/admin/operations/job-runs/stat-config-readiness-jobs",
            typeof(StatConfigReadinessAdminController)
                .GetCustomAttribute<RouteAttribute>()?.Template,
            "admin readiness route");
        AssertEqual(
            "owners/{ownerKind}/{ownerId}/readiness-jobs",
            typeof(StatConfigReadinessController)
                .GetMethod(nameof(StatConfigReadinessController.Enqueue))!
                .GetCustomAttribute<HttpPostAttribute>()?.Template,
            "enqueue route");
        AssertEqual(
            "{jobId}",
            typeof(StatConfigReadinessAdminController)
                .GetMethod(nameof(StatConfigReadinessAdminController.GetDiagnostics))!
                .GetCustomAttribute<HttpGetAttribute>()?.Template,
            "privileged diagnostics route");

        var source = ReadBackendSource(
            "Controllers/StatConfigReadinessAdminController.cs");
        foreach (var serviceMethod in new[]
                 {
                     "SearchDiagnosticsAsync",
                     "GetDiagnosticsAsync",
                     "ProcessPendingAsync",
                     "ResetAsync",
                     "CancelAsync",
                     "CleanupAsync",
                     "ValidateIndexesAsync"
                 })
        {
            AssertRegex(
                source,
                $@"var\s+actor\s*=\s*RequireSystemAdmin\(\);[\s\S]{{0,900}}" +
                $@"_operations\s*\.\s*{serviceMethod}",
                $"admin authorization before {serviceMethod}");
        }
        AssertRegex(
            source,
            @"private[\s\S]{0,120}RequireSystemAdmin\(\)[\s\S]{0,300}" +
            @"RoleGuard\s*\.\s*RequireSystemAdmin\(actor\)",
            "system-admin role guard");
    }
    private static void ServiceAtomicityQuotaAndLeaseGuardsAreFrozen()
    {
        var service = ReadBackendSource(
            "Services/StatisticsConfiguration/StatConfigOperationsService.cs");
        var worker = ReadBackendSource(
            "Services/StatisticsConfiguration/StatConfigOperationsService.Worker.cs");
        var admin = ReadBackendSource(
            "Services/StatisticsConfiguration/StatConfigOperationsService.Admin.cs");
        var enqueue = Slice(
            service,
            "public async Task<StatConfigValidationJobStatusResponse> EnqueueAsync(",
            "public async Task<StatConfigValidationJobStatusResponse> GetSafeStatusAsync(");
        var fence = Slice(
            worker,
            "private static FilterDefinition<StatConfigValidationJob> FenceFilter(",
            "private int RetryDelaySeconds(");

        AssertContains(
            enqueue,
            "ArgumentNullException.ThrowIfNull(actor);",
            "enqueue actor null guard");
        var authOffset = enqueue.IndexOf(
            "RequireSystemAdmin(actor);",
            StringComparison.Ordinal);
        var receiptLookupOffset = enqueue.IndexOf(
            "FindReceiptAsync",
            StringComparison.Ordinal);
        AssertTrue(authOffset >= 0 && receiptLookupOffset > authOffset,
            "enqueue authorization before receipt/source lookup");
        AssertContains(
            enqueue,
            "AcquireActorEnqueueGateAsync",
            "per-actor quota race gate");
        AssertRegex(
            enqueue,
            @"dedupeKey[\s\S]{0,350}commandId\s*=\s*command\.CommandId" +
            @"[\s\S]{0,120}bundleHash",
            "command-aware canonical-bundle dedupe");
        var quotaCountOffset = enqueue.IndexOf(
            "CountDocumentsAsync",
            StringComparison.Ordinal);
        var quotaErrorOffset = enqueue.IndexOf(
            "STAT_CONFIG_READINESS_QUOTA_EXCEEDED",
            StringComparison.Ordinal);
        var jobInsertOffset = enqueue.IndexOf(
            "StatConfigValidationJobs.InsertOneAsync",
            StringComparison.Ordinal);
        AssertTrue(
            quotaCountOffset >= 0 &&
            quotaErrorOffset > quotaCountOffset &&
            jobInsertOffset > quotaErrorOffset,
            "quota rejection before job write");
        foreach (var point in new[]
                 {
                     "BeforeJobWrite",
                     "AfterJobWrite",
                     "BeforeReceiptWrite",
                     "AfterReceiptWrite",
                     "BeforeOutboxWrite",
                     "AfterOutboxWrite"
                 })
        {
            AssertContains(
                enqueue,
                $"StatConfigOperationsFaultPoints.{point}",
                $"atomic enqueue fault boundary {point}");
        }

        AssertContains(
            worker,
            "boundedValidation.CancelAfter",
            "validation bounded below lease window");
        AssertRegex(
            fence,
            @"LeaseUntilUtc[\s\S]{0,180}DateTime\.UtcNow",
            "final worker fence requires live lease");
        AssertRegex(
            admin,
            @"ResetAsync\([\s\S]{0,300}RequireSystemAdmin\(actor\)",
            "reset service authorization before lookup");
        AssertRegex(
            admin,
            @"CancelAsync\([\s\S]{0,300}RequireSystemAdmin\(actor\)",
            "cancel service authorization before lookup");
        AssertRegex(
            admin,
            @"CleanupAsync\([\s\S]{0,300}RequireSystemAdmin\(actor\)",
            "cleanup service authorization before lookup");
    }

    private static void ReadinessWorkerCannotCallResultMaterializers()
    {
        var source = ReadOperationsServiceSource();
        AssertContains(
            source,
            "StatConfigValidationQueue.Name",
            "dedicated readiness queue usage");
        foreach (var symbol in new[]
                 {
                     "WorkReportStatisticRebuildJob",
                     "IWorkReportStatisticRebuildJobService",
                     "IWorkReportFieldStatisticsService",
                     "IWorkReportTableStatisticsService",
                     "IWorkReportLabelStatisticsService",
                     "IWorkAssignmentAdvancedSummaryHierarchyService",
                     "WorkAssignmentBasicSummarySnapshot",
                     "WorkAssignmentAdvancedSummaryDayNode",
                     "WorkAssignmentAdvancedSummaryMonthNode",
                     "WorkAssignmentAdvancedSummaryYearNode",
                     "RefreshSnapshotJobAsync",
                     "ExportTextConcatCsvAsync"
                 })
        {
            AssertDoesNotContain(
                source,
                symbol,
                $"forbidden materializer symbol '{symbol}'");
        }
    }

    private static void ReadinessIndexesAreSingleOwnerAndFailClosed()
    {
        var source = ReadBackendSource("Data/Indexes/MongoIndexInitializer.cs");
        var bindings = Slice(
            source,
            "BuildStatConfigIndexBindings(MongoOptions opt)",
            "private static async Task EnsureStatConfigOperationsIndexesAsync");
        var ensure = Slice(
            source,
            "private static async Task EnsureStatConfigOperationsIndexesAsync",
            "public static async Task<IReadOnlyList<MongoIndexContractStatus>>");
        var failClosed = Slice(
            source,
            "public static async Task EnsureFailClosedBySpecAsync<T>",
            "private static async Task DropConflictsByKeyAsync<T>");

        var names = Regex.Matches(
                bindings,
                "\"(?<name>(?:ux|ix)_statConfig" +
                "(?:CommandReceipts|ValidationJobs|AuditOutbox)_[A-Za-z0-9_]+)\"",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
        AssertSetEqual(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ux_statConfigCommandReceipts_owner_command",
                "ix_statConfigCommandReceipts_owner_created",
                "ux_statConfigValidationJobs_dedupe",
                "ux_statConfigValidationJobs_correlation",
                "ix_statConfigValidationJobs_claim",
                "ix_statConfigValidationJobs_owner_history",
                "ix_statConfigValidationJobs_ttl",
                "ux_statConfigAuditOutbox_dedupe",
                "ix_statConfigAuditOutbox_due",
                "ix_statConfigAuditOutbox_target",
                "ix_statConfigAuditOutbox_ttl"
            },
            names,
            "P8-08 index inventory");

        AssertRegex(
            bindings,
            "ix_statConfigAuditOutbox_target[\\s\\S]{0,500}" +
            "\"targetKind\"[\\s\\S]{0,120}\"targetId\"",
            "outbox target query index");
        AssertContains(
            ensure,
            "MongoIndexEnsureHelper.EnsureFailClosedBySpecAsync",
            "P8 index fail-closed ensure path");
        AssertDoesNotContain(
            ensure,
            "MongoIndexEnsureHelper.EnsureBySpecAsync(",
            "P8 index path must not use the legacy rebuilding helper");
        AssertContains(
            ensure,
            "PrecheckUniqueByFieldsAsync",
            "unique-key precheck");
        AssertDoesNotContain(
            failClosed,
            "DropOneAsync",
            "fail-closed helper drop guard");
        AssertDoesNotContain(
            failClosed,
            "DropConflictsByKeyAsync",
            "fail-closed helper rebuild guard");
        AssertContains(
            failClosed,
            "P8IndexConfigurationInvalid",
            "mismatched index must fail startup");
        foreach (var option in new[]
                 {
                     "sparse",
                     "hidden",
                     "collation",
                     "wildcardProjection",
                     "storageEngine",
                     "weights",
                     "default_language",
                     "language_override"
                 })
        {
            AssertContains(
                source,
                option,
                $"unexpected index option guard '{option}'");
        }
        AssertEqual(
            1,
            CountOccurrences(source, "await EnsureStatConfigOperationsIndexesAsync("),
            "single startup index owner invocation");
    }

    private static void AssertAuthenticatedController(Type controllerType)
    {
        AssertTrue(
            controllerType.GetCustomAttribute<AuthorizeAttribute>(inherit: true)
                is not null,
            $"{controllerType.Name} must require authentication");
        AssertTrue(
            controllerType.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true)
                is null,
            $"{controllerType.Name} must not allow anonymous access");
        foreach (var method in controllerType.GetMethods(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.DeclaredOnly))
        {
            AssertTrue(
                method.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true)
                    is null,
                $"{controllerType.Name}.{method.Name} must not allow anonymous access");
        }
    }

    private static void AssertStrictJson(Type type)
    {
        var attribute =
            type.GetCustomAttribute<JsonUnmappedMemberHandlingAttribute>();
        AssertTrue(attribute is not null, $"{type.Name} strict JSON attribute");
        AssertEqual(
            JsonUnmappedMemberHandling.Disallow,
            attribute!.UnmappedMemberHandling,
            $"{type.Name} unknown-property policy");
    }

    private static void AssertPropertiesInclude(
        Type type,
        params string[] expected)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in expected)
            AssertTrue(
                properties.Contains(property),
                $"{type.Name} must contain {property}");
    }

    private static void AssertPropertiesExclude(
        Type type,
        params string[] forbidden)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in forbidden)
            AssertTrue(
                !properties.Contains(property),
                $"{type.Name} must redact {property}");
    }

    private static void AssertPropertiesEqual(
        Type type,
        params string[] expected)
    {
        var actual = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        AssertSetEqual(
            expected.ToHashSet(StringComparer.Ordinal),
            actual,
            $"{type.Name} properties");
    }

    private static void StateHashUsesBsonMillisecondPrecision()
    {
        var exactSecond = new DateTime(
            2026,
            8,
            2,
            16,
            18,
            14,
            DateTimeKind.Utc);
        var first = StateHashFixture(exactSecond.AddTicks(1_234));
        var samePersistedMillisecond =
            StateHashFixture(exactSecond.AddTicks(9_876));
        var nextPersistedMillisecond =
            StateHashFixture(exactSecond.AddMilliseconds(1).AddTicks(1));

        AssertEqual(
            StatConfigOperationsService.ComputeStateHash(first),
            StatConfigOperationsService.ComputeStateHash(
                samePersistedMillisecond),
            "state hash BSON millisecond stability");
        AssertTrue(
            !string.Equals(
                StatConfigOperationsService.ComputeStateHash(first),
                StatConfigOperationsService.ComputeStateHash(
                    nextPersistedMillisecond),
                StringComparison.Ordinal),
            "state hash must distinguish adjacent BSON milliseconds");
    }

    private static StatConfigValidationJob StateHashFixture(
        DateTime completedAtUtc)
        => new()
        {
            Id = "000000000000000000000001",
            QueueName = StatConfigValidationQueue.Name,
            Status = StatConfigValidationJobStatuses.Completed,
            IsActive = false,
            StateRevision = 4,
            RetryCount = 0,
            MaxRetryCount = 3,
            LastRunAtUtc = completedAtUtc.AddSeconds(-1),
            CompletedAtUtc = completedAtUtc,
            SafeCode = "READY"
        };

    private static string ReadOperationsServiceSource()
    {
        var directory = Path.Combine(
            FindBackendRoot(),
            "Services",
            "StatisticsConfiguration");
        var files = Directory
            .GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                Source = File.ReadAllText(path)
            })
            .Where(file =>
                Path.GetFileName(file.Path).Contains(
                    "StatConfigOperationsService",
                    StringComparison.Ordinal) ||
                file.Source.Contains(
                    "IStatConfigOperationsService",
                    StringComparison.Ordinal))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        AssertTrue(
            files.Any(file =>
                !Path.GetFileName(file.Path).StartsWith(
                    "I",
                    StringComparison.Ordinal)),
            "StatConfigOperationsService implementation source is missing");
        return string.Join(
            Environment.NewLine,
            files.Select(file =>
                $"// FILE: {file.Path}{Environment.NewLine}{file.Source}"));
    }

    private static string ReadBackendSource(string relativePath)
    {
        var path = Path.Combine(
            FindBackendRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Required backend source is missing: {path}");
        return File.ReadAllText(path);
    }

    private static string FindBackendRoot()
    {
        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(seed);
                 directory is not null;
                 directory = directory.Parent)
            {
                var direct = Path.Combine(
                    directory.FullName,
                    "tdtd-be.csproj");
                if (File.Exists(direct))
                    return directory.FullName;

                var nested = Path.Combine(
                    directory.FullName,
                    "tdtd-be",
                    "tdtd-be.csproj");
                if (File.Exists(nested))
                    return Path.GetDirectoryName(nested)!;
            }
        }
        throw new InvalidOperationException(
            "Unable to locate tdtd-be source root.");
    }

    private static string Slice(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException(
                $"Source marker is missing: {startMarker}");
        var end = source.IndexOf(
            endMarker,
            start + startMarker.Length,
            StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException(
                $"Source marker is missing: {endMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(
                   value,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static void AssertRegex(
        string actual,
        string pattern,
        string context)
    {
        if (!Regex.IsMatch(actual, pattern, RegexOptions.CultureInvariant))
            throw new InvalidOperationException(
                $"{context}: required source pattern was not found: {pattern}");
    }

    private static void AssertContains(
        string actual,
        string expected,
        string context)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{context}: expected '{expected}'.");
    }

    private static void AssertDoesNotContain(
        string actual,
        string forbidden,
        string context)
    {
        if (actual.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{context}: found forbidden '{forbidden}'.");
    }

    private static void AssertSetEqual<T>(
        IReadOnlySet<T> expected,
        IEnumerable<T> actual,
        string context)
    {
        var comparer = expected is HashSet<T> hashSet
            ? hashSet.Comparer
            : EqualityComparer<T>.Default;
        var actualSet = actual.ToHashSet(comparer);
        if (!expected.SetEquals(actualSet))
        {
            throw new InvalidOperationException(
                $"{context}: expected [{string.Join(",", expected)}], " +
                $"got [{string.Join(",", actualSet)}].");
        }
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertTrue(bool value, string context)
    {
        if (!value)
            throw new InvalidOperationException(context);
    }
}
