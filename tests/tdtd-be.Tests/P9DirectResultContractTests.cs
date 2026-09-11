using System.Reflection;
using MongoDB.Bson.Serialization;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

internal static class P9DirectResultContractTests
{
    public static void Run()
    {
        RoutesRequireExactlyStageOne();
        PagingAndExplicitPeriodFailClosed();
        TypedOwnersSeparateZeroFalseNullEmptyAndMissing();
        ResponsesCarryGlobalTotalsPinsAndFourLabelLayers();
        ExactGenerationSelectionIsCanonicalAndFailClosed();
        LockedFlowContributionPolicyOverridesP7MappingExclusions();
        CanonicalReconciliationIdentityIsServerDerivedAndFailClosed();
    }

    private static void RoutesRequireExactlyStageOne()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StatRunRouteRegistry.DirectFieldResult] =
                StatRunCapabilities.DirectFieldTableLabel,
            [StatRunRouteRegistry.DirectTableResult] =
                StatRunCapabilities.DirectFieldTableLabel,
            [StatRunRouteRegistry.DirectLabelResult] =
                StatRunCapabilities.DirectFieldTableLabel,
            [StatRunRouteRegistry.DirectTextResult] =
                StatRunCapabilities.DirectFieldTableLabel
        };
        var routes = StatRunRouteRegistry.Snapshot();
        foreach (var item in expected)
        {
            Assert(
                routes.TryGetValue(item.Key, out var capability) &&
                capability == item.Value,
                $"Route {item.Key} must bind Direct capability.");
            Assert(
                StatRunRouteRegistry.MinimumStage(item.Key) == 1,
                $"Route {item.Key} must require candidate stage 1.");
        }
        Assert(
            routes.TryGetValue(StatRunRouteRegistry.DirectExport, out var exportCapability) &&
            exportCapability == StatRunCapabilities.DirectFieldTableLabel,
            "P9 Direct export must bind the Direct capability.");
        Assert(
            StatRunRouteRegistry.MinimumStage(StatRunRouteRegistry.DirectExport) == 7,
            "P9-03 candidate stage 1 must not activate the P9-09 export route.");
    }

    private static void PagingAndExplicitPeriodFailClosed()
    {
        AssertNormalizeError(
            AppErrorCode.COMMON_ARGUMENT_REQUIRED,
            workId: "100000000000000000000001",
            period: null,
            page: 0,
            pageSize: 50);
        AssertNormalizeError(
            AppErrorCode.STAT_RUN_PAGE_INVALID,
            workId: "100000000000000000000001",
            period: "DAY:2026-08-05",
            page: -1,
            pageSize: 50);
        AssertNormalizeError(
            AppErrorCode.STAT_RUN_PAGE_INVALID,
            workId: "100000000000000000000001",
            period: "DAY:2026-08-05",
            page: 0,
            pageSize: 201);
    }

    private static void TypedOwnersSeparateZeroFalseNullEmptyAndMissing()
    {
        var fieldMap = BsonClassMap.LookupClassMap(typeof(WorkReportFieldStatValue));
        Assert(fieldMap.GetMemberMap(nameof(WorkReportFieldStatValue.ValueKind)) is not null,
            "Field valueKind is required.");
        Assert(fieldMap.GetMemberMap(nameof(WorkReportFieldStatValue.NumericValue)) is not null,
            "Field nullable numeric value is required.");
        Assert(fieldMap.GetMemberMap(nameof(WorkReportFieldStatValue.BooleanValue)) is not null,
            "Field nullable boolean value is required.");
        Assert(fieldMap.GetMemberMap(nameof(WorkReportFieldStatValue.TextValue)) is not null,
            "Field text value is required.");

        var tableMap = BsonClassMap.LookupClassMap(typeof(WorkReportTableStatValue));
        Assert(tableMap.GetMemberMap(nameof(WorkReportTableStatValue.ValueKind)) is not null,
            "Table valueKind is required.");
        Assert(tableMap.GetMemberMap(nameof(WorkReportTableStatValue.NumericValue)) is not null,
            "Table nullable numeric value is required.");

        var typed = new[]
        {
            "MISSING", "NULL", "EMPTY", "NUMBER", "BOOLEAN", "DATE", "TEXT",
            "OPTION", "LABEL_IDENTITY", "REDACTED"
        };
        Assert(typed.Distinct(StringComparer.Ordinal).Count() == typed.Length,
            "Typed states must remain distinct.");
    }

    private static void ResponsesCarryGlobalTotalsPinsAndFourLabelLayers()
    {
        var response = new FieldStatisticSummaryResponse();
        Assert(response.Metadata is not null, "Direct metadata is required.");
        Assert(response.DrilldownRows is not null, "Typed drilldown rows are required.");
        Assert(
            typeof(FieldStatisticSummaryResponse).GetProperty(
                nameof(FieldStatisticSummaryResponse.TotalRows)) is not null,
            "Global totalRows is required.");
        Assert(
            typeof(P9DirectPublicationPin).GetProperties().Any(x =>
                x.Name == nameof(P9DirectPublicationPin.GenerationHash)),
            "Generation hash pin is required.");
        Assert(
            typeof(P9DirectPublicationPin).GetProperties().Any(x =>
                x.Name == nameof(P9DirectPublicationPin.ConfigHash)),
            "Config hash pin is required.");
        Assert(
            typeof(P9DirectPublicationPin).GetProperties().Any(x =>
                x.Name == nameof(P9DirectPublicationPin.SourceLifecycleEventKey)),
            "Lifecycle pin is required.");

        var label = new LabelStatisticSummaryRow();
        Assert(label.StatisticLabelLayer == "FIELD_STATISTIC_LABEL",
            "Statistic-label layer drift.");
        Assert(label.RuntimeLabelLayer == "RUNTIME_ROW_LABEL",
            "Runtime-row-label layer drift.");
        Assert(label.CatalogLabelLayer == "LABEL_CATALOG",
            "Catalog-label layer drift.");
        Assert(label.ConfigurationLayer == "LOCKED_P8_CONFIG",
            "Configuration-label layer drift.");
    }

    private static void ExactGenerationSelectionIsCanonicalAndFailClosed()
    {
        foreach (var requestType in new[]
                 {
                     typeof(FieldStatisticSummaryRequest),
                     typeof(TableStatisticSummaryRequest),
                     typeof(LabelStatisticSummaryRequest)
                 })
        {
            Assert(
                requestType.GetProperty("GenerationId")?.PropertyType == typeof(string),
                $"{requestType.Name} must expose the optional exact generation selector.");
        }

        var normalize = typeof(P9DirectResultService).GetMethod(
            "NormalizeOptionalGenerationId",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "NormalizeOptionalGenerationId was not found.");
        var generationId = new string('a', 64);
        Assert(
            string.Equals(
                normalize.Invoke(null, new object?[] { generationId }) as string,
                generationId,
                StringComparison.Ordinal),
            "Canonical generation selector must be preserved exactly.");
        Assert(
            normalize.Invoke(null, new object?[] { null }) is null,
            "Omitted generation selector must preserve legacy unpinned reads.");
        foreach (var invalid in new[]
                 {
                     string.Empty,
                     new string('a', 63),
                     new string('A', 64),
                     $" {generationId}"
                 })
        {
            AssertInvocationError(
                normalize,
                new object?[] { invalid },
                AppErrorCode.COMMON_VALIDATION_FAILED,
                $"Generation selector length {invalid.Length} must reject non-canonical SHA-256.");
        }

        var requireExact = typeof(P9DirectResultService).GetMethod(
            "RequireExactGenerationPublication",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "RequireExactGenerationPublication was not found.");
        var exact = new WorkReportStatisticRebuildJob { GenerationId = generationId };
        requireExact.Invoke(
            null,
            new object?[]
            {
                new List<WorkReportStatisticRebuildJob>(),
                generationId
            });
        requireExact.Invoke(
            null,
            new object?[]
            {
                new List<WorkReportStatisticRebuildJob> { exact },
                generationId
            });
        requireExact.Invoke(
            null,
            new object?[]
            {
                new List<WorkReportStatisticRebuildJob> { exact, exact },
                null
            });
        AssertInvocationError(
            requireExact,
            new object?[]
            {
                new List<WorkReportStatisticRebuildJob>
                {
                    exact,
                    new() { GenerationId = generationId }
                },
                generationId
            },
            AppErrorCode.STAT_RUN_RESULT_STALE,
            "One exact generation must not resolve to duplicate current publications.");
        AssertInvocationError(
            requireExact,
            new object?[]
            {
                new List<WorkReportStatisticRebuildJob>
                {
                    new() { GenerationId = new string('b', 64) }
                },
                generationId
            },
            AppErrorCode.STAT_RUN_RESULT_STALE,
            "A pinned read must reject a publication from another generation.");

        var source = ReadBackendSource(
            "Services/StatisticsRun/P9DirectResultService.cs");
        Assert(
            source.Contains(
                "filter &= fb.Eq(x => x.Id, pinnedRunId.Trim());",
                StringComparison.Ordinal) &&
            source.Contains(
                "filter &= fb.Eq(x => x.GenerationId, pinnedGenerationId);",
                StringComparison.Ordinal),
            "Run-id and generation-id publication selectors must remain separate.");
        foreach (var responseType in new[]
                 {
                     nameof(FieldStatisticSummaryResponse),
                     nameof(TableStatisticSummaryResponse),
                     nameof(LabelStatisticSummaryResponse)
                 })
        {
            var responseMarker = $"var response = new {responseType}";
            var responseAt = source.IndexOf(responseMarker, StringComparison.Ordinal);
            Assert(responseAt >= 0, $"{responseType} construction was not found.");
            var methodAt = source.LastIndexOf(
                "private async Task<",
                responseAt,
                StringComparison.Ordinal);
            var authorizationAt = source.IndexOf(
                "var access = await AuthorizeBeforeExistenceAsync(query, ct);",
                methodAt,
                StringComparison.Ordinal);
            var normalizationAt = source.IndexOf(
                "var pinnedGenerationId = NormalizeOptionalGenerationId(request.GenerationId);",
                methodAt,
                StringComparison.Ordinal);
            var resolutionAt = source.IndexOf(
                "var publication = await ResolvePublicationAsync(",
                methodAt,
                StringComparison.Ordinal);
            Assert(
                methodAt >= 0 &&
                authorizationAt > methodAt &&
                normalizationAt > authorizationAt &&
                resolutionAt > normalizationAt &&
                resolutionAt < responseAt,
                $"{responseType} must authorize before generation validation and publication lookup.");
        }
    }

    private static void LockedFlowContributionPolicyOverridesP7MappingExclusions()
    {
        const string reportId = "100000000000000000000001";
        const string flowInstanceId = "100000000000000000000006";
        const string mappedField = "field_1788273435070_r0eoxy";
        const string manuallyEnteredField = "field_1788273435300_s7lpnn";
        var report = new WorkAssignmentReport
        {
            Id = reportId,
            CumulativeContributionMode = "EXCLUDE",
            CumulativeContributionPolicyJson =
                "{\"defaultMode\":\"EXCLUDE\",\"rules\":[{" +
                $"\"targetKind\":\"FIELD\",\"targetKey\":\"{mappedField}\"," +
                "\"mode\":\"EXCLUDE\"}]}"
        };

        var lockedFlowBinding = StatRunDirectProjectionService.ResolveContributionBinding(
            report,
            flowInstanceId,
            "INCLUDE");
        Assert(
            lockedFlowBinding is { Mode: "INCLUDE", IsLockedFlowPolicy: true },
            "A flow member must bind its locked INCLUDE policy in the production resolver.");
        var lockedFlowContext = CreateGenerationContext(
            reportId,
            lockedFlowBinding);
        var lockedFlowPolicy = lockedFlowContext.ResolveContributionPolicy(report);
        Assert(
            lockedFlowPolicy.IncludesReport &&
            lockedFlowPolicy.ShouldIncludeField(mappedField) &&
            lockedFlowPolicy.ShouldIncludeField(manuallyEnteredField),
            "A locked flow INCLUDE policy must govern P9 and must not inherit P7 mapping exclusions.");

        var lockedExcludeBinding = StatRunDirectProjectionService.ResolveContributionBinding(
            report,
            flowInstanceId,
            "EXCLUDE");
        var lockedExcludeContext = CreateGenerationContext(
            reportId,
            lockedExcludeBinding);
        var lockedExcludePolicy = lockedExcludeContext.ResolveContributionPolicy(report);
        Assert(
            !lockedExcludePolicy.IncludesReport &&
            !lockedExcludePolicy.ShouldIncludeField(mappedField) &&
            !lockedExcludePolicy.ShouldIncludeTableMetric(
                "block-1",
                "metric-1",
                "row-1",
                "column-1",
                "source-1") &&
            !lockedExcludePolicy.ShouldIncludeLabel(
                "block-1",
                "row-1",
                "source-1",
                "label-1"),
            "A locked flow EXCLUDE policy must exclude every P9 surface.");

        var missingLockedPolicyBinding =
            StatRunDirectProjectionService.ResolveContributionBinding(
                report,
                flowInstanceId,
                lockedContributionPolicy: null);
        Assert(
            missingLockedPolicyBinding is { Mode: "EXCLUDE", IsLockedFlowPolicy: true },
            "A flow member without a locked policy must fail closed to EXCLUDE.");

        report.CumulativeContributionMode = "INCLUDE";
        var nonFlowBinding = StatRunDirectProjectionService.ResolveContributionBinding(
            report,
            flowInstanceId: null,
            lockedContributionPolicy: "EXCLUDE");
        Assert(
            nonFlowBinding is { Mode: "INCLUDE", IsLockedFlowPolicy: false },
            "A non-flow member must bind the report mode and ignore a flow-only policy value.");
        var nonFlowContext = CreateGenerationContext(
            reportId,
            nonFlowBinding);
        var nonFlowPolicy = nonFlowContext.ResolveContributionPolicy(report);
        Assert(
            nonFlowPolicy.IncludesReport &&
            !nonFlowPolicy.ShouldIncludeField(mappedField) &&
            !nonFlowPolicy.ShouldIncludeField(manuallyEnteredField),
            "Non-flow projection must continue honoring the report's granular contribution policy.");
    }

    private static WorkReportDirectGenerationContext CreateGenerationContext(
        string reportId,
        WorkReportDirectContributionBinding contributionBinding)
        => new(
            RunId: "100000000000000000000002",
            GenerationId: new string('a', 64),
            LifecycleEventKey: new string('b', 64),
            DirectSourceRevision: 1,
            DynamicFormFamilyId: "100000000000000000000003",
            DynamicFormTemplateId: "100000000000000000000004",
            DynamicFormVersionNo: 1,
            DynamicFormSchemaHash: new string('c', 64),
            ConfigId: "100000000000000000000004",
            ConfigVersionId: "100000000000000000000005",
            ConfigVersionNo: 1,
            ConfigRevision: 1,
            ConfigHash: new string('d', 64),
            CandidateChainId: "p9-test-chain",
            CatalogVersion: "test-catalog",
            CatalogRawSha256: new string('e', 64),
            CatalogSemanticSha256: new string('f', 64),
            SchemaRawSha256: new string('1', 64),
            SchemaSemanticSha256: new string('2', 64),
            StageLockSha256: new string('3', 64),
            SourceMembershipSignature: new string('4', 64),
            SourceContributionBindings:
                new Dictionary<string, WorkReportDirectContributionBinding>(
                    StringComparer.Ordinal)
                {
                    [reportId] = contributionBinding
                },
            ComputedAtUtc: DateTime.UnixEpoch);
    private static void CanonicalReconciliationIdentityIsServerDerivedAndFailClosed()
    {
        Assert(
            typeof(P9DirectResultMetadata).GetProperty(
                nameof(P9DirectResultMetadata.ReconciliationIdentity))?.PropertyType ==
            typeof(P9DirectReconciliationIdentity),
            "Direct result metadata must carry the server-derived reconciliation identity.");
        Assert(
            typeof(P9DirectReconciliationIdentity).GetProperty(
                nameof(P9DirectReconciliationIdentity.PeriodKey)) is not null,
            "Reconciliation identity must bind the server-derived period key.");

        var grain = typeof(P9DirectResultService).GetMethod(
            "ReconciliationGrain",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ReconciliationGrain was not found.");
        string? Resolve(string? periodKey, string periodInstanceKey) =>
            grain.Invoke(null, new object?[] { periodKey, periodInstanceKey }) as string;

        Assert(Resolve("opaque", "DAY:2026-08-23") == "DAY",
            "Canonical DAY instance must determine reconciliation grain.");
        Assert(Resolve("opaque", "MONTH:2026-08") == "MONTH",
            "Canonical MONTH instance must determine reconciliation grain.");
        Assert(Resolve("opaque", "YEAR:2026") == "YEAR",
            "Canonical YEAR instance must determine reconciliation grain.");
        Assert(Resolve("2026-08-P11-03", "opaque") == "MONTH",
            "The P11 period key must map to MONTH without using PeriodKind.");
        Assert(Resolve("2026-08-23", "opaque") == "DAY",
            "A canonical date period key must map to DAY.");
        Assert(Resolve("not-a-period", "SCHEDULED") is null,
            "PeriodKind-like input must not be trusted as reconciliation grain.");
    }

    private static void AssertNormalizeError(
        AppErrorCode expected,
        string? workId,
        string? period,
        int page,
        int pageSize)
    {
        var method = typeof(P9DirectResultService).GetMethod(
            "NormalizeQuery",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("NormalizeQuery was not found.");
        try
        {
            _ = method.Invoke(
                null,
                new object?[] { workId, "WORK", workId, period, page, pageSize });
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is AppException appException &&
                  appException.Code == expected)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {expected}.");
    }

    private static void AssertInvocationError(
        MethodInfo method,
        object?[] arguments,
        AppErrorCode expected,
        string message)
    {
        try
        {
            _ = method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is AppException appException &&
                  appException.Code == expected)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static string ReadBackendSource(string relativePath)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.CurrentDirectory, relativePath),
                     Path.Combine(Environment.CurrentDirectory, "tdtd-be", relativePath)
                 })
        {
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Backend source '{relativePath}' was not found.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
