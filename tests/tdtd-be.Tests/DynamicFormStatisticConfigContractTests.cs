using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using tdtd_be.Controllers;
using tdtd_be.Common.Errors;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsConfiguration;

internal static class DynamicFormStatisticConfigContractTests
{
    private static readonly string EmptyHash =
        StatConfigCanonicalJson.EmptyConfigHash;

    public static void Run()
    {
        StrictPayloadExposesOnlyTheFrozenStatisticAllowlist();
        ExactOperationAndBucketMatrixHasNoFallback();
        ResultAndModelPersistVersionedFieldAndTableSections();
        ControllerRetiresLegacyStatisticWrites();
        MutationSourceIsIsolatedFromResultMaterializers();
        PublishLocksThePersistedCurrentStatisticVersionAtomically();
        StableFieldStatisticErrorsAreBadRequests();
    }

    private static void StrictPayloadExposesOnlyTheFrozenStatisticAllowlist()
    {
        using var valid = JsonDocument.Parse(
            "{\"commandId\":\"field-stat-1\",\"expectedRevision\":0," +
            "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
            "\"payload\":{\"fields\":[{" +
            "\"fieldId\":\"amount\",\"isStatistic\":true," +
            "\"statistic\":{\"aggregateOps\":[\"COUNT\",\"SUM\"]," +
            "\"bucketMode\":\"NONE\",\"showInDetail\":true," +
            "\"showInTree\":false}," +
            "\"statisticLabelCodes\":[\"revenue\"]}]}}");
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<DynamicFormStatisticConfigPayload>>(
            valid.RootElement);
        AssertEqual("amount", envelope.Payload?.Fields?[0].FieldId, "field id");
        AssertEqual(
            DynamicFormStatisticAggregateOperations.Sum,
            envelope.Payload?.Fields?[0].Statistic?.AggregateOps?[1],
            "explicit operation");

        foreach (var json in new[]
                 {
                     "{\"commandId\":\"x\",\"expectedRevision\":0," +
                     "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
                     "\"payload\":{\"fields\":[],\"blocksJson\":\"[]\"}}",
                     "{\"commandId\":\"x\",\"expectedRevision\":0," +
                     "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
                     "\"payload\":{\"fields\":[{\"fieldId\":\"f\"," +
                     "\"isStatistic\":false,\"name\":\"forged\"}]}}",
                     "{\"commandId\":\"x\",\"expectedRevision\":0," +
                     "\"expectedConfigHash\":\"" + EmptyHash + "\"," +
                     "\"payload\":{\"fields\":[{\"fieldId\":\"f\"," +
                     "\"isStatistic\":true,\"statistic\":{" +
                     "\"aggregateOps\":[\"COUNT\"],\"bucketMode\":\"NONE\"," +
                     "\"showInDetail\":true,\"showInTree\":false," +
                     "\"operation\":\"COUNT\"}}]}}"
                 })
        {
            using var unknown = JsonDocument.Parse(json);
            ExpectError(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                () => StatConfigCanonicalJson.DeserializeStrict<
                    StatConfigMutationEnvelope<
                        DynamicFormStatisticConfigPayload>>(
                    unknown.RootElement));
        }
    }

    private static void ExactOperationAndBucketMatrixHasNoFallback()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [DynamicFormStatisticFieldTypes.Number] =
                ["COUNT", "SUM", "AVG", "MIN", "MAX", "LATEST"],
            [DynamicFormStatisticFieldTypes.Date] =
                ["COUNT", "MIN", "MAX", "LATEST"],
            [DynamicFormStatisticFieldTypes.FullDate] =
                ["COUNT", "MIN", "MAX", "LATEST"],
            [DynamicFormStatisticFieldTypes.Boolean] =
                ["COUNT", "TRUE_COUNT", "FALSE_COUNT"],
            [DynamicFormStatisticFieldTypes.SingleSelect] =
                ["COUNT", "BUCKET_COUNT", "LATEST"],
            [DynamicFormStatisticFieldTypes.MultiSelect] =
                ["COUNT", "BUCKET_COUNT"],
            [DynamicFormStatisticFieldTypes.ShortText] =
                ["COUNT", "LATEST", "CONCAT"],
            [DynamicFormStatisticFieldTypes.LongText] =
                ["COUNT", "LATEST", "CONCAT"],
            [DynamicFormStatisticFieldTypes.StringList] = ["COUNT"]
        };

        AssertEqual(
            expected.Count,
            DynamicFormStatisticOperationContract
                .AllowedOperationsByFieldType.Count,
            "supported field type count");
        foreach (var (fieldType, operations) in expected)
        {
            var actual = DynamicFormStatisticOperationContract
                .AllowedOperationsByFieldType[fieldType];
            AssertSetEqual(operations, actual, $"operations for {fieldType}");
            AssertTrue(
                DynamicFormStatisticOperationContract.IsBucketModeAllowed(
                    fieldType,
                    DynamicFormStatisticBucketModes.None),
                $"NONE bucket for {fieldType}");
        }

        AssertFalse(
            DynamicFormStatisticOperationContract.IsSupportedFieldType(
                DynamicFormStatisticFieldTypes.RichText),
            "RICH_TEXT must be forbidden");
        AssertFalse(
            DynamicFormStatisticOperationContract.IsOperationAllowed(
                DynamicFormStatisticFieldTypes.Number,
                "MEAN"),
            "unknown number operation must not fall back");
        AssertTrue(
            DynamicFormStatisticOperationContract.IsBucketModeAllowed(
                DynamicFormStatisticFieldTypes.SingleSelect,
                DynamicFormStatisticBucketModes.Option),
            "choice OPTION bucket");
        AssertFalse(
            DynamicFormStatisticOperationContract.IsBucketModeAllowed(
                DynamicFormStatisticFieldTypes.StringList,
                DynamicFormStatisticBucketModes.Option),
            "STRING_LIST is not an option target");
        AssertTrue(
            DynamicFormStatisticOperationContract.IsBucketModeAllowed(
                DynamicFormStatisticFieldTypes.FullDate,
                DynamicFormStatisticBucketModes.Date),
            "date DATE bucket");
        AssertFalse(
            DynamicFormStatisticOperationContract.IsBucketModeAllowed(
                DynamicFormStatisticFieldTypes.Number,
                DynamicFormStatisticBucketModes.Date),
            "number DATE bucket must reject");
        AssertEqual(
            30,
            DynamicFormStatisticOperationContract.MaximumTargets,
            "30/31 target boundary");
    }

    private static void ResultAndModelPersistVersionedFieldAndTableSections()
    {
        var collection = typeof(DynamicFormTemplate)
            .GetCustomAttribute<BsonCollectionAttribute>();
        AssertEqual(
            "dynamic_form_templates",
            collection?.Name,
            "canonical plural owner collection");

        var sections = new DynamicFormStatisticConfigSections();
        AssertEqual("[]", sections.FieldSectionJson, "empty field section");
        AssertEqual("[]", sections.TableSectionJson, "P8-02 table staging");

        foreach (var property in new[]
                 {
                     "StatisticConfigId",
                     "StatisticConfigVersionId",
                     "StatisticConfigPreviousVersionId",
                     "StatisticConfigVersionNo",
                     "StatisticConfigRevision",
                     "StatisticConfigStatus",
                     "StatisticConfigHash",
                     "StatisticConfigDependencyPins",
                     "StatisticConfigSections",
                     "StatisticConfigSnapshots"
                 })
        {
            AssertTrue(
                typeof(DynamicFormTemplate).GetProperty(property) is not null,
                $"model property {property}");
        }

        foreach (var property in new[]
                 {
                     "OwnerKind",
                     "OwnerId",
                     "ConfigId",
                     "VersionId",
                     "VersionNo",
                     "Revision",
                     "Status",
                     "ConfigHash",
                     "DependencyPins",
                     "Permissions",
                     "Fields",
                     "TableConfig",
                     "FieldSectionHash",
                     "TableSectionHash",
                     "Versions",
                     "ReceiptId"
                 })
        {
            AssertTrue(
                typeof(DynamicFormStatisticConfigResult)
                    .GetProperty(property) is not null,
                $"result property {property}");
        }
        AssertTrue(
            typeof(DynamicFormStatisticConfigResult)
                .GetProperty("StatisticRebuildJobId") is null,
            "P8 result must not expose a rebuild materializer id");
        AssertTrue(
            typeof(DynamicFormStatisticFieldConfigDto)
                .GetProperty("StructureHash") is not null,
            "configured field structure hash readback");
    }

    private static void ControllerRetiresLegacyStatisticWrites()
    {
        var controller = ReadBackendSource(
            "Controllers/DynamicFormController.cs");
        var service = ReadBackendSource("Services/DynamicFormService.cs");
        AssertContains(
            controller,
            "[HttpGet(\"{id}/statistics\")]",
            "statistics GET route");
        AssertContains(
            controller,
            "[HttpPatch(\"{id}/statistics\")]",
            "retired statistics PATCH route returns a controlled error");
        AssertContains(
            controller,
            "[FromBody] JsonElement body",
            "raw body preserves authorization-before-schema ordering");
        AssertContains(
            controller,
            "FORM_LEGACY_STATISTIC_AUTHORING_DISABLED",
            "legacy statistics writes are disabled");
        AssertNotContains(
            controller,
            "_svc.UpdateStatisticConfigAsync(id, body, ct)",
            "retired route cannot forward mutations");
        AssertContains(
            service,
            "CanUpdateStatistics: false",
            "Form actions cannot advertise the retired writer");
        AssertContains(
            service,
            "FORM_LEGACY_STATISTIC_AUTHORING_DISABLED",
            "Form service also rejects legacy statistic writes");
        AssertNotContains(
            service,
            "_statisticConfig.PatchAsync(id, body, ct)",
            "Form service cannot reach the retired mutation engine");
        using var payload = JsonDocument.Parse("{\"payload\":{\"tables\":[]}}");
        ExpectError(AppErrorCode.COMMON_VALIDATION_FAILED,
            () => new DynamicFormController(null!).UpdateStatisticConfig(
                "form-id", payload.RootElement, CancellationToken.None));
        AssertNotContains(
            controller,
            "UpdateDynamicFormStatisticConfigReq",
            "obsolete permissive request DTO");
        AssertContains(
            service,
            "Task<DynamicFormStatisticConfigResult> UpdateStatisticConfigAsync(",
            "typed service result");
        AssertContains(
            service,
            "JsonElement body",
            "service accepts raw JSON");
    }

    private static void MutationSourceIsIsolatedFromResultMaterializers()
    {
        var command = ReadBackendSource(
            "Services/DynamicForms/DynamicFormStatisticConfigCommandService.cs");
        AssertContains(
            command,
            "StatConfigIsolationGuard.EnterConfigurationMutation",
            "configuration-only isolation guard");
        AssertContains(
            command,
            "NormalizeMutationSyntax(command.Payload)",
            "strict typed field mutation boundary");
        AssertContains(
            command,
            "\"isStatistic\", \"statisticLabelCodes\", \"statistic\"",
            "exact field statistic allowlist");
        foreach (var forbidden in new[]
                 {
                     "WorkReportStatisticRebuildJob",
                     "WorkReportFieldStatValues",
                     "WorkReportFieldStatAggregates",
                     "WorkReportTableStatValues",
                     "WorkReportTableStatAggregates",
                     "WorkReportLabelStatValues",
                     "WorkReportLabelStatAggregates"
                 })
        {
            AssertNotContains(
                command,
                forbidden,
                $"P8-02 must not reference {forbidden}");
        }
    }

    private static void StableFieldStatisticErrorsAreBadRequests()
    {
        foreach (var code in new[]
                 {
                     AppErrorCode.DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID,
                     AppErrorCode.DYNAMIC_FORM_STATISTIC_OPERATION_INVALID,
                     AppErrorCode.DYNAMIC_FORM_STATISTIC_BUCKET_MODE_INVALID,
                     AppErrorCode.DYNAMIC_FORM_STATISTIC_TARGET_LIMIT_EXCEEDED
                 })
        {
            AssertEqual(
                StatusCodes.Status400BadRequest,
                AppErrorCatalog.Get(code).HttpStatus,
                $"{code} status");
        }
    }

    private static void
        PublishLocksThePersistedCurrentStatisticVersionAtomically()
    {
        var service = ReadBackendSource(
            "Services/DynamicFormService.cs");
        var command = ReadBackendSource(
            "Services/DynamicForms/" +
            "DynamicFormStatisticConfigCommandService.cs");

        AssertContains(
            service,
            "PrepareStatisticConfigForPublish(doc)",
            "publish prepares the statistic lifecycle transition");
        AssertContains(
            service,
            "x => x.StatisticConfigStatus",
            "publish atomically persists root statistic status");
        AssertContains(
            service,
            "x => x.StatisticConfigSnapshots",
            "publish atomically persists promoted current snapshot");
        AssertContains(
            service,
            "current.Status = StatConfigStatuses.Locked",
            "current statistic version transitions DRAFT to LOCKED");
        AssertContains(
            command,
            "var expectedStatus = CurrentStatus(owner);",
            "persisted status is validated against owner lifecycle");
        AssertContains(
            command,
            "CONFIG_STATUS",
            "status drift fails closed");
        AssertContains(
            command,
            "owner.StatisticConfigStatus!",
            "readback trusts the validated persisted status");
        AssertContains(
            command,
            "UtcNowAtMillisecondPrecision()",
            "mutation response uses Mongo-stable timestamp precision");
        AssertContains(
            command,
            "CreatedAtUtc = createdAtUtc",
            "receipt and response share the stable mutation timestamp");
    }

    private static AppException ExpectError(
        AppErrorCode expected,
        Action action)
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            AssertEqual(expected, error.Code, "stable error code");
            return error;
        }

        throw new InvalidOperationException(
            $"Expected {expected}, but no AppException was thrown.");
    }

    private static string ReadBackendSource(string relativePath)
    {
        var root = FindBackendRoot();
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Backend source file was not found: {path}");
        }
        return File.ReadAllText(path);
    }

    private static string FindBackendRoot()
    {
        var seeds = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            for (var directory = new DirectoryInfo(seed);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(
                        Path.Combine(directory.FullName, "tdtd-be.csproj")))
                {
                    return directory.FullName;
                }
                var nested = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nested, "tdtd-be.csproj")))
                {
                    return nested;
                }
            }
        }
        throw new InvalidOperationException(
            "Could not locate the tdtd-be source root.");
    }

    private static void AssertSetEqual(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string context)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        if (!expectedSet.SetEquals(actualSet))
        {
            throw new InvalidOperationException(
                $"{context}: expected [{string.Join(',', expectedSet)}], " +
                $"got [{string.Join(',', actualSet)}].");
        }
    }

    private static void AssertContains(
        string source,
        string expected,
        string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{expected}'.");
        }
    }

    private static void AssertNotContains(
        string source,
        string forbidden,
        string context)
    {
        if (source.Contains(forbidden, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{context}: forbidden '{forbidden}'.");
        }
    }

    private static void AssertTrue(bool condition, string context)
    {
        if (!condition)
            throw new InvalidOperationException($"{context}: expected true.");
    }

    private static void AssertFalse(bool condition, string context)
        => AssertTrue(!condition, context);

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{context}: expected '{expected}', got '{actual}'.");
        }
    }
}
