using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Statistics;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

internal static class WorkReportStatisticDiffP806ContractTests
{
    private const string FlowInstanceId =
        "abcdef000000000000000001";
    private const string ForeignFlowInstanceId =
        "abcdef000000000000000002";

    public static void Run()
    {
        StrictNestedPayloadShapeIsClosed();
        TypedDiffNormalizerIsExact();
        ConfigHashBindsOwnerPayloadAndPins();
        PublicContractAndRowPerVersionShapeAreStable();
        LockedRowsAndLineageAreImmutable();
        DependenciesAreLiveBoundWithoutMetricFallback();
        AuthorizationReplayAndRoutesAreStrict();
        LegacyAndRuntimePathsFailClosed();
    }

    private static void StrictNestedPayloadShapeIsClosed()
    {
        using var document = JsonDocument.Parse(
            ValidEnvelope(
                leftExtra: ",\"callerOwnedScope\":true"));
        ExpectSchema(
            () => StatConfigCanonicalJson.DeserializeStrict<
                StatConfigMutationEnvelope<
                    WorkReportStatisticDiffConfigPayload>>(
                    document.RootElement),
            "unknown nested side property");

        foreach (var type in new[]
                 {
                     typeof(WorkReportStatisticDiffConfigPayload),
                     typeof(WorkReportStatisticDiffSidePayload),
                     typeof(WorkReportStatisticDiffSelectorPayload),
                     typeof(WorkReportStatisticDiffPeriodPayload),
                     typeof(WorkReportStatisticDiffSourceScopePayload),
                     typeof(WorkReportStatisticDiffEmptyCommandPayload)
                 })
        {
            var strict = type.GetCustomAttribute<
                JsonUnmappedMemberHandlingAttribute>();
            AssertEqual(
                JsonUnmappedMemberHandling.Disallow,
                strict?.UnmappedMemberHandling,
                $"{type.Name} unmapped-member handling");
        }

        AssertPropertyNames(
            typeof(WorkReportStatisticDiffConfigPayload),
            ["Name", "Left", "Right", "Direction", "MissingPolicy", "EmptyPolicy"]);
        AssertPropertyNames(
            typeof(WorkReportStatisticDiffSidePayload),
            ["Selector", "Period", "SourceScope"]);
        AssertPropertyNames(
            typeof(WorkReportStatisticDiffSelectorPayload),
            ["ConceptKind", "ConceptKey", "ConceptCode", "DataType"]);
        AssertPropertyNames(
            typeof(WorkReportStatisticDiffPeriodPayload),
            ["Mode", "PeriodKey", "PeriodKeyFrom", "PeriodKeyTo"]);
        AssertPropertyNames(
            typeof(WorkReportStatisticDiffSourceScopePayload),
            ["Mode", "FlowInstanceId", "FlowStepId", "FlowBranchId", "FlowEffectiveStatus"]);
        AssertPropertyNames(
            typeof(WorkReportStatisticDiffEmptyCommandPayload),
            []);
    }

    private static void TypedDiffNormalizerIsExact()
    {
        var normalized = Normalize(
            Payload(
                WorkReportStatisticDiffConfigContract.TableMetric,
                "block-1:metric:net",
                "Revenue",
                WorkReportStatisticDiffConfigContract.Number,
                WorkReportStatisticDiffConfigContract.AsZero));
        AssertEqual(
            "revenue",
            normalized.Left!.Selector!.ConceptCode,
            "concept code is canonical lowercase");
        AssertEqual(
            "block-1:metric:net",
            normalized.Left.Selector.ConceptKey,
            "TABLE_METRIC preserves metric keys containing colon");

        ExpectSchema(
            () => Normalize(
                Payload(
                    WorkReportStatisticDiffConfigContract.RowLabel,
                    "block-1:row:forged",
                    "row",
                    WorkReportStatisticDiffConfigContract.Number)),
            "ROW_LABEL accepts exactly one separator");
        ExpectSchema(
            () => Normalize(
                Payload(
                    WorkReportStatisticDiffConfigContract.Field,
                    "field-1",
                    "title",
                    WorkReportStatisticDiffConfigContract.Text,
                    WorkReportStatisticDiffConfigContract.AsZero)),
            "AS_ZERO is numeric only");

        AssertSequenceEqual(
            new[]
            {
                "DIRECT_CHILDREN_OR_SELF",
                "DIRECT_CHILDREN",
                "SELF",
                "FLOW_BRANCH",
                "FLOW_STEP",
                "FLOW_EFFECTIVE_PATH",
                "FLOW_FINAL"
            },
            WorkReportStatisticDiffConfigContract.SourceScopeModes,
            "seven source-scope modes");

        var assignment = new WorkAssignment
        {
            Id = "100000000000000000000001",
            FlowInstanceId = FlowInstanceId,
            FlowStepId = "step-1",
            FlowBranchId = "abcdef000000000000000003",
            FlowEffectiveStatus = WorkReportStatisticDiffConfigContract.Effective,
            IsFlowFinalNode = false
        };
        var foreign = new WorkReportStatisticDiffSourceScopePayload(
            WorkReportStatisticDiffConfigContract.FlowStep,
            ForeignFlowInstanceId,
            "step-1",
            null,
            WorkReportStatisticDiffConfigContract.Effective);
        var error = ExpectReflectedSchema(
            () => InvokeScopeValidation(
                foreign,
                assignment,
                "$.payload.left.sourceScope"),
            "foreign flow instance");
        var details = JsonSerializer.Serialize(error.Details);
        AssertContains(
            details,
            "$.payload.left.sourceScope.flowInstanceId",
            "foreign scope error path");
        AssertContains(
            details,
            "DIFF_SOURCE_SCOPE_FOREIGN",
            "foreign scope reason");
    }

    private static void ConfigHashBindsOwnerPayloadAndPins()
    {
        var payload = Normalize(Payload());
        var first = ComputeHash("assignment:template", payload, ["pin-a"]);
        var replay = ComputeHash("assignment:template", payload, ["pin-a"]);
        var changedPin = ComputeHash("assignment:template", payload, ["pin-b"]);
        var changedOwner = ComputeHash("other:template", payload, ["pin-a"]);
        AssertEqual(first, replay, "config hash determinism");
        AssertTrue(
            first.Length == 64 && first.All(value =>
                value is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "config hash is lowercase SHA-256");
        AssertFalse(first == changedPin, "config hash binds dependency pins");
        AssertFalse(first == changedOwner, "config hash binds owner identity");
    }

    private static void PublicContractAndRowPerVersionShapeAreStable()
    {
        AssertEqual(
            "BLOCKED_UNTIL_P9",
            WorkReportStatisticDiffConfigContract.RuntimeBlockedUntilP9,
            "runtime eligibility");
        AssertPropertyNames(
            typeof(WorkReportStatisticDiffConfigVersionsResult),
            ["OwnerKind", "OwnerId", "ConfigId", "Items"]);

        var model = typeof(WorkReportStatisticDiffConfig);
        AssertTrue(
            model.GetProperty("Id")?.GetCustomAttribute<BsonIdAttribute>() is not null,
            "row _id is the version identity");
        foreach (var (property, element) in new[]
                 {
                     ("ConfigId", "configId"),
                     ("PreviousVersionId", "previousVersionId"),
                     ("VersionNo", "versionNo"),
                     ("Revision", "revision"),
                     ("Status", "status"),
                     ("ConfigHash", "configHash"),
                     ("ConfigJson", "configJson"),
                     ("DependencyPins", "dependencyPins"),
                     ("LockedAtUtc", "lockedAtUtc"),
                     ("LockedByUserId", "lockedByUserId")
                 })
        {
            var actual = model.GetProperty(property)?
                .GetCustomAttribute<BsonElementAttribute>()?.ElementName;
            AssertEqual(element, actual, $"stored {property}");
        }
        AssertTrue(model.GetProperty("VersionId") is null,
            "version identity is not duplicated inside a row");
        AssertTrue(model.GetProperty("Versions") is null,
            "versions are separate rows, not an embedded array");
    }

    private static void LockedRowsAndLineageAreImmutable()
    {
        var state = ReadBackendSource(
            "Services/WorkAssignmentReports/Statistics/" +
            "WorkReportStatisticDiffService.P806.State.cs");
        foreach (var required in new[]
                 {
                     "filter.Eq(item => item.ConfigId, configId)",
                     "P806ValidateRows(owner, rows, configId);",
                     "DIFF_CONFIG_IDENTITY_INVALID",
                     "DIFF_CONFIG_HASH_MISMATCH",
                     "DIFF_CONFIG_DRAFT_STATE_INVALID",
                     "DIFF_CONFIG_LOCK_STATE_INVALID",
                     "DIFF_CONFIG_ACTIVE_STATE_AMBIGUOUS",
                     "if (current.Status == StatConfigStatuses.Locked)",
                     "DIFF_CONFIG_VERSION_LOCKED",
                     "Entity = null",
                     "PreviousVersionId = current.VersionId",
                     "VersionNo = versionNo",
                     "Revision = 0",
                     "Status = StatConfigStatuses.Draft",
                     "WorkReportStatisticDiffConfigs.InsertOneAsync",
                     "filter.Eq(item => item.Status, StatConfigStatuses.Draft)",
                     "new ReplaceOptions { IsUpsert = false }"
                 })
        {
            AssertContains(state, required, $"version lifecycle {required}");
        }
        var nextDraft = Slice(
            state,
            "P806ApplyNextDraftAsync(",
            "private async Task P806EnsureDependenciesCurrentAsync(");
        AssertOrder(
            nextDraft,
            "P806EnsureDependenciesCurrentAsync",
            "Entity = null",
            "next draft validates pins before creating a row");
        var persistence = Slice(
            state,
            "P806PersistStateAsync(",
            "P806ResolveDependencyPinsAsync(");
        AssertContains(
            persistence,
            "filter.Eq(item => item.Status, StatConfigStatuses.Draft)",
            "only a draft row can be replaced");
        AssertNotContains(
            persistence,
            "UpdateManyAsync",
            "version persistence cannot mutate historical rows in bulk");
    }

    private static void DependenciesAreLiveBoundWithoutMetricFallback()
    {
        var state = ReadBackendSource(
            "Services/WorkAssignmentReports/Statistics/" +
            "WorkReportStatisticDiffService.P806.State.cs");
        var dependencies = ReadBackendSource(
            "Services/WorkAssignmentReports/Statistics/" +
            "WorkReportStatisticDiffService.P806.Dependencies.cs");
        var metric = Slice(
            state,
            "selector.ConceptKind == WorkReportStatisticDiffConfigContract.TableMetric",
            "            else");
        AssertContains(metric, "table.MetricLabelTargets.Any", "trusted metric-label target");
        AssertContains(metric, "target.MetricKey == localKey &&", "exact metric key binding");
        AssertContains(
            metric,
            "target.StatisticLabelCode == selector.ConceptCode",
            "exact metric label binding");
        AssertNotContains(metric, "||", "metric label has no local-key fallback");

        foreach (var required in new[]
                 {
                     "GetP804LabelVisibilityFilter(me)",
                     "_ctx.Labels",
                     "P806LiveLabelMatches",
                     "owner.IsActive",
                     "!owner.IsDeleted",
                     "owner.VersionId",
                     "owner.ConfigHash",
                     "snapshot.IsActive",
                     "DIFF_SELECTOR_LABEL_FORBIDDEN_OR_STALE",
                     "DIFF_ROW_LABEL_NOT_ALLOWED",
                     "DIFF_SOURCE_SCOPE_FOREIGN",
                     "DIFF_SOURCE_SCOPE_STATUS_MISMATCH",
                     "DIFF_SOURCE_SCOPE_NOT_FINAL"
                 })
        {
            AssertContains(dependencies, required, $"dependency guard {required}");
        }
    }

    private static void AuthorizationReplayAndRoutesAreStrict()
    {
        var commands = ReadBackendSource(
            "Services/WorkAssignmentReports/Statistics/" +
            "WorkReportStatisticDiffService.P806.Commands.cs");
        foreach (var required in new[]
                 {
                     "P806Access.Read",
                     "P806Access.Manage",
                     "P806Access.Lock",
                     "DIFF_CONFIG_ACCESS_DENIED",
                     "DIFF_CONFIG_MANAGE_FORBIDDEN",
                     "DIFF_CONFIG_LOCK_FORBIDDEN",
                     "_statConfigTransactions.ExecuteAsync",
                     "P806LoadReplayAsync",
                     "P806EnsureCas(current, command);",
                     "StatConfigCommandReceipts.InsertOneAsync",
                     "receipt.RequestHash",
                     "STAT_CONFIG_COMMAND_REPLAY_CONFLICT",
                     "OwnerKind = StatConfigOwnerKinds.Diff"
                 })
        {
            AssertContains(commands, required, $"command contract {required}");
        }
        var put = Slice(commands, "PutP8ConfigAsync(", "LockP8ConfigAsync(");
        AssertOrder(
            put,
            "P806PrepareAsync",
            "NormalizeP806PutCommand(body)",
            "PUT authorization precedes strict body parsing");
        var transaction = Slice(
            commands,
            "P806ExecuteCommandAsync<TPayload>(",
            "private async Task<WorkReportStatisticDiffConfigReadback?>");
        AssertOrder(
            transaction,
            "P806LoadReplayAsync",
            "P806LoadStateAsync",
            "exact replay precedes state and CAS");
        AssertOrder(
            transaction,
            "P806PersistStateAsync",
            "StatConfigCommandReceipts.InsertOneAsync",
            "state and receipt share one transaction in owner-first order");

        var controller = typeof(WorkReportStatisticDiffController);
        AssertTrue(
            controller.GetCustomAttribute<AuthorizeAttribute>() is not null,
            "diff controller requires authenticated access");
        AssertHttpGet(
            nameof(WorkReportStatisticDiffController.GetP8Config),
            "assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config");
        AssertHttpGet(
            nameof(WorkReportStatisticDiffController.ListP8ConfigVersions),
            "assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/versions");
        AssertHttpGet(
            nameof(WorkReportStatisticDiffController.GetP8ConfigVersion),
            "assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/versions/{versionNo:int}");
        AssertHttpPut(
            nameof(WorkReportStatisticDiffController.PutP8Config),
            "assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config");
        AssertHttpPost(
            nameof(WorkReportStatisticDiffController.LockP8Config),
            "assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/lock");
        AssertHttpPost(
            nameof(WorkReportStatisticDiffController.CreateNextP8Draft),
            "assignments/{assignmentId}/templates/{dynamicFormTemplateId}/config/next-draft");
    }

    private static void LegacyAndRuntimePathsFailClosed()
    {
        var controller = ReadBackendSource(
            "Controllers/WorkReportStatisticDiffController.cs");
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/Statistics/" +
            "WorkReportStatisticDiffService.cs");
        foreach (var required in new[]
                 {
                     "DIFF_LEGACY_CONFIG_ROUTE_BLOCKED",
                     "DIFF_LEGACY_MUTATION_BLOCKED_USE_CAS_CONFIG_ROUTE",
                     "DIFF_LEGACY_DELETE_BLOCKED_USE_CAS_CONFIG_ROUTE",
                     "StatConfigIsolationGuard.RejectResultMaterializer",
                     "WORK_REPORT_STATISTIC_DIFF_RUN"
                 })
        {
            AssertContains(controller, required, $"controller barrier {required}");
            AssertContains(service, required, $"service barrier {required}");
        }
        var run = Slice(
            service,
            "RunAsync(",
            "private async Task<NormalizedRunRequest> NormalizeRunRequestAsync(");
        AssertOrder(
            run,
            "StatConfigIsolationGuard.RejectResultMaterializer",
            "Unreachable P9 barrier",
            "runtime barrier is the first terminal behavior");

        var narrowConfigPath =
            ReadBackendSource(
                "Services/WorkAssignmentReports/Statistics/" +
                "WorkReportStatisticDiffService.P806.Commands.cs") +
            ReadBackendSource(
                "Services/WorkAssignmentReports/Statistics/" +
                "WorkReportStatisticDiffService.P806.State.cs") +
            ReadBackendSource(
                "Services/WorkAssignmentReports/Statistics/" +
                "WorkReportStatisticDiffService.P806.Contract.cs") +
            ReadBackendSource(
                "Services/WorkAssignmentReports/Statistics/" +
                "WorkReportStatisticDiffService.P806.Dependencies.cs");
        foreach (var forbidden in new[]
                 {
                     "WorkReportFieldStatValues",
                     "WorkReportTableStatValues",
                     "WorkReportStatisticDiffRunResponse",
                     "BackgroundJob.",
                     ".Enqueue("
                 })
        {
            AssertNotContains(
                narrowConfigPath,
                forbidden,
                $"configuration path excludes {forbidden}");
        }
    }

    private static WorkReportStatisticDiffConfigPayload Payload(
        string conceptKind = WorkReportStatisticDiffConfigContract.Field,
        string conceptKey = "field-1",
        string conceptCode = "revenue",
        string dataType = WorkReportStatisticDiffConfigContract.Number,
        string missingPolicy = WorkReportStatisticDiffConfigContract.Reject)
    {
        var side = new WorkReportStatisticDiffSidePayload(
            new WorkReportStatisticDiffSelectorPayload(
                conceptKind, conceptKey, conceptCode, dataType),
            new WorkReportStatisticDiffPeriodPayload(
                WorkReportStatisticDiffConfigContract.Exact,
                "2026-07", null, null),
            new WorkReportStatisticDiffSourceScopePayload(
                WorkReportStatisticDiffConfigContract.Self,
                null, null, null, null));
        return new WorkReportStatisticDiffConfigPayload(
            "Monthly difference",
            side,
            side,
            WorkReportStatisticDiffConfigContract.LeftToRight,
            missingPolicy,
            WorkReportStatisticDiffConfigContract.Reject);
    }

    private static WorkReportStatisticDiffConfigPayload Normalize(
        WorkReportStatisticDiffConfigPayload payload)
    {
        var method = typeof(WorkReportStatisticDiffService).GetMethod(
            "P806NormalizePayload",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "P806NormalizePayload was not found.");
        try
        {
            return (WorkReportStatisticDiffConfigPayload)
                (method.Invoke(null, [payload]) ??
                 throw new InvalidOperationException(
                     "P806NormalizePayload returned null."));
        }
        catch (TargetInvocationException error)
            when (error.InnerException is AppException appError)
        {
            throw appError;
        }
    }

    private static string ComputeHash(
        string ownerId,
        WorkReportStatisticDiffConfigPayload payload,
        IReadOnlyList<string> pins)
    {
        var method = typeof(WorkReportStatisticDiffService).GetMethod(
            "P806ComputeConfigHash",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "P806ComputeConfigHash was not found.");
        return (string)(method.Invoke(null, [ownerId, payload, pins]) ??
            throw new InvalidOperationException(
                "P806ComputeConfigHash returned null."));
    }

    private static void InvokeScopeValidation(
        WorkReportStatisticDiffSourceScopePayload scope,
        WorkAssignment assignment,
        string path)
    {
        var method = typeof(WorkReportStatisticDiffService).GetMethod(
            "P806ValidateScopeOwner",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "P806ValidateScopeOwner was not found.");
        _ = method.Invoke(null, [scope, assignment, path]);
    }

    private static string ValidEnvelope(string leftExtra = "")
    {
        const string selector =
            "{\"conceptKind\":\"FIELD\",\"conceptKey\":\"field-1\"," +
            "\"conceptCode\":\"revenue\",\"dataType\":\"NUMBER\"}";
        const string period =
            "{\"mode\":\"EXACT\",\"periodKey\":\"2026-07\"}";
        var left = "{\"selector\":" + selector +
                   ",\"period\":" + period +
                   ",\"sourceScope\":{\"mode\":\"SELF\"}" +
                   leftExtra + "}";
        var right = "{\"selector\":" + selector +
                    ",\"period\":" + period +
                    ",\"sourceScope\":{\"mode\":\"SELF\"}}";
        return "{\"commandId\":\"diff-put-1\",\"expectedRevision\":0," +
               "\"expectedConfigHash\":\"" +
               StatConfigCanonicalJson.EmptyConfigHash +
               "\",\"payload\":{\"name\":\"Monthly difference\"," +
               "\"left\":" + left + ",\"right\":" + right +
               ",\"direction\":\"LEFT_TO_RIGHT\"," +
               "\"missingPolicy\":\"REJECT\"," +
               "\"emptyPolicy\":\"REJECT\"}}";
    }

    private static AppException ExpectSchema(Action action, string context)
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            AssertEqual(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                error.Code,
                context);
            return error;
        }
        throw new InvalidOperationException(
            $"{context}: expected schema error.");
    }

    private static AppException ExpectReflectedSchema(
        Action action,
        string context)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException error)
            when (error.InnerException is AppException appError)
        {
            AssertEqual(
                AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
                appError.Code,
                context);
            return appError;
        }
        throw new InvalidOperationException(
            $"{context}: expected reflected schema error.");
    }

    private static string ReadBackendSource(string relativePath)
    {
        var root = FindBackendRoot();
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Backend source file was not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindBackendRoot()
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
                if (File.Exists(Path.Combine(directory.FullName, "tdtd-be.csproj")))
                    return directory.FullName;
                var nested = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nested, "tdtd-be.csproj")))
                    return nested;
            }
        }
        throw new InvalidOperationException(
            "Could not locate the tdtd-be source root.");
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = startIndex < 0
            ? -1
            : source.IndexOf(
                end,
                startIndex + start.Length,
                StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0)
            throw new InvalidOperationException(
                $"Could not slice source from '{start}' to '{end}'.");
        return source[startIndex..endIndex];
    }

    private static void AssertHttpGet(string methodName, string expected)
        => AssertEqual(
            expected,
            typeof(WorkReportStatisticDiffController).GetMethod(methodName)?
                .GetCustomAttribute<HttpGetAttribute>()?.Template,
            $"GET route {methodName}");

    private static void AssertHttpPut(string methodName, string expected)
        => AssertEqual(
            expected,
            typeof(WorkReportStatisticDiffController).GetMethod(methodName)?
                .GetCustomAttribute<HttpPutAttribute>()?.Template,
            $"PUT route {methodName}");

    private static void AssertHttpPost(string methodName, string expected)
        => AssertEqual(
            expected,
            typeof(WorkReportStatisticDiffController).GetMethod(methodName)?
                .GetCustomAttribute<HttpPostAttribute>()?.Template,
            $"POST route {methodName}");

    private static void AssertPropertyNames(
        Type type,
        IReadOnlyList<string> expected)
        => AssertSequenceEqual(
            expected.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            type.GetProperties()
                .Select(property => property.Name)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            $"{type.Name} exact public properties");

    private static void AssertContains(
        string source,
        string expected,
        string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{context}: expected '{expected}'.");
    }

    private static void AssertNotContains(
        string source,
        string forbidden,
        string context)
    {
        if (source.Contains(forbidden, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{context}: forbidden '{forbidden}'.");
    }

    private static void AssertOrder(
        string source,
        string first,
        string second,
        string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
            throw new InvalidOperationException(
                $"{context}: expected '{first}' before '{second}'.");
    }

    private static void AssertSequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string context)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                $"{context}: expected [{string.Join(", ", expected)}], " +
                $"actual [{string.Join(", ", actual)}].");
    }

    private static void AssertTrue(bool value, string context)
    {
        if (!value)
            throw new InvalidOperationException(context);
    }

    private static void AssertFalse(bool value, string context)
        => AssertTrue(!value, context);

    private static void AssertEqual<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{context}: expected {expected}, actual {actual}.");
    }
}
