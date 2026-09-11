using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using tdtd_be.Common.Errors;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports;

internal static class DynamicFormRuntimePersistenceContractTests
{
    private const string ReportId = "1000000000000000000000a1";

    public static void Run()
    {
        TableModeProjectionsFailClosedAgainstCanonicalShape();
        SummaryAndUnknownTableModesAreRejectedAtThePublishedBoundary();
        RuntimeChoiceResolutionCoversAllSixPublishedSources();
        PayloadCasRejectsMissingStaleAndMismatchedCommands();
        PayloadCasAcceptsAnExactCompletedReplay();
        SaveAndSubmitValidateAndPreflightBeforePayloadWrites();
        MappingAndAutomaticAggregateWritesUseThePayloadCasPipeline();
        AutomaticAggregateInvalidationUsesLifecycleCas();
        ReactivateDuplicateKeyMapsToStableConflict();
        RuntimeMutationCapabilitiesAreServerDerived();
    }

    private static void TableModeProjectionsFailClosedAgainstCanonicalShape()
    {
        InvokeTableProjectionValidation("FIXED_GRID", "{}");
        InvokeTableProjectionValidation("APPEND_ROWS", "{\"rows\":[]}");
        InvokeTableProjectionValidation("APPEND_COLUMNS", "{\"columns\":[]}");
        InvokeTableProjectionValidation("MATRIX", "{\"cells\":[]}");

        Reason(
            "DYNAMIC_FORM_TABLE_ROWS_REQUIRED",
            () => InvokeTableProjectionValidation("APPEND_ROWS", "{}"));
        Reason(
            "DYNAMIC_FORM_TABLE_COLUMNS_REQUIRED",
            () => InvokeTableProjectionValidation("APPEND_COLUMNS", "{}"));
        Reason(
            "DYNAMIC_FORM_TABLE_CELLS_REQUIRED",
            () => InvokeTableProjectionValidation("MATRIX", "{}"));
        Reason(
            "DYNAMIC_FORM_FIXED_GRID_PROJECTION_FORBIDDEN",
            () => InvokeTableProjectionValidation("FIXED_GRID", "{\"rows\":[]}"));
        Reason(
            "DYNAMIC_FORM_TABLE_MODE_UNSUPPORTED",
            () => InvokeTableProjectionValidation("UNKNOWN", "{}"));
    }

    private static void SummaryAndUnknownTableModesAreRejectedAtThePublishedBoundary()
    {
        var source = ReadBackendSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var body = Slice(
            source,
            "private static void ValidateDynamicTableRuntimeValues(",
            "private async Task<string?> CanonicalizeDynamicFieldRuntimeValuesAsync(");

        AssertContains(
            body,
            "contractMode is not (\"FIXED_GRID\" or \"APPEND_ROWS\" or \"APPEND_COLUMNS\" or \"MATRIX\" or \"SUMMARY_TEMPLATE\")",
            "published table modes must be allow-listed exactly");
        AssertContains(
            body,
            "DYNAMIC_FORM_TABLE_MODE_MISMATCH",
            "client mode must equal the immutable published mode");
        AssertContains(
            body,
            "DYNAMIC_FORM_SUMMARY_TEMPLATE_INPUT_FORBIDDEN",
            "SUMMARY_TEMPLATE must remain computed and reject client input");
        AssertBefore(
            body,
            "DYNAMIC_FORM_SUMMARY_TEMPLATE_INPUT_FORBIDDEN",
            "Values1DCompression.ReadBlockObjects",
            "summary input must fail before runtime values are accepted");
    }

    private static void RuntimeChoiceResolutionCoversAllSixPublishedSources()
    {
        var source = ReadBackendSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var resolver = Slice(
            source,
            "private async Task<IReadOnlyCollection<string>> ResolveRuntimeFieldAllowedChoiceCodesAsync(",
            "private static string AttachRuntimeFieldSourceProvenance(");
        var canonicalizer = Slice(
            source,
            "private async Task<string?> CanonicalizeDynamicFieldRuntimeValuesAsync(",
            "private static bool IsRuntimeChoiceFieldType(");
        var provenance = Slice(
            source,
            "private static string AttachRuntimeFieldSourceProvenance(",
            "private static void ValidateRuntimeTableValueSlots(");

        foreach (var sourceType in new[]
                 {
                     "LabelValueSourceTypes.None",
                     "LabelValueSourceTypes.FixedEnum",
                     "LabelValueSourceTypes.EnumCatalog",
                     "LabelValueSourceTypes.SystemUnit",
                     "LabelValueSourceTypes.SystemUser",
                     "LabelValueSourceTypes.SystemPosition",
                     "LabelValueSourceTypes.SystemUnitType"
                 })
        {
            AssertContains(resolver, sourceType, $"runtime source resolver branch {sourceType}");
        }

        AssertContains(resolver, "optionSet.Codes", "enum catalog must use active server-resolved codes");
        AssertContains(resolver, "!x.IsDeleted && x.Code != \"ROOT\"", "unit ids must be live and ROOT must remain hidden");
        AssertContains(resolver, "ids.Contains(x.Id) && !x.IsDeleted", "user ids must resolve against live users");
        AssertContains(resolver, "codes.Contains(x.Code) && !x.IsDeleted", "position and unit-type codes must resolve against live catalogs");
        AssertContains(canonicalizer, "DynamicFormRuntimeFieldCanonicalizer.Canonicalize", "resolved source codes must feed strict canonicalization");
        AssertBefore(
            canonicalizer,
            "ResolveRuntimeFieldAllowedChoiceCodesAsync",
            "DynamicFormRuntimeFieldCanonicalizer.Canonicalize",
            "server source resolution must happen before choice-code validation");
        foreach (var property in new[] { "sourceType", "sourceId", "sourceVersion", "codes" })
            AssertContains(provenance, $"[\"{property}\"]", $"persisted source provenance {property}");
    }

    private static void PayloadCasRejectsMissingStaleAndMismatchedCommands()
    {
        var report = Report(payloadRevision: 4);

        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_REVISION_REQUIRED,
            () => ResolvePayloadCommand(report, null, "command_001", "SAVE_DRAFT", "hash-a"));
        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_COMMAND_ID_REQUIRED,
            () => ResolvePayloadCommand(report, 4, "short", "SAVE_DRAFT", "hash-a"));
        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_PAYLOAD_REVISION_CONFLICT,
            () => ResolvePayloadCommand(report, 3, "command_001", "SAVE_DRAFT", "hash-a"));

        report.PayloadMutationCommandId = "command_001";
        report.PayloadMutationCommandHash = "hash-a";
        report.PayloadMutationOperation = "SAVE_DRAFT";
        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_COMMAND_REPLAY_MISMATCH,
            () => ResolvePayloadCommand(report, 4, "command_001", "SAVE_DRAFT", "hash-b"));
    }

    private static void PayloadCasAcceptsAnExactCompletedReplay()
    {
        var report = Report(payloadRevision: 5);
        report.LastPayloadCommandId = "command_001";
        report.LastPayloadCommandHash = "hash-a";
        report.LastPayloadCommandOperation = "SAVE_DRAFT";
        report.LastPayloadCommandRevision = 5;

        var command = ResolvePayloadCommand(report, 4, " command_001 ", "SAVE_DRAFT", "hash-a");
        True(command is not null, "exact replay must resolve to a command");

        Code(
            AppErrorCode.WORK_ASSIGNMENT_REPORT_COMMAND_REPLAY_MISMATCH,
            () => ResolvePayloadCommand(report, 4, "command_001", "SAVE_DRAFT", "hash-b"));
    }

    private static void SaveAndSubmitValidateAndPreflightBeforePayloadWrites()
    {
        var source = ReadBackendSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var save = Slice(
            source,
            "public async Task<WorkAssignmentReportResponse> SaveDraftAsync(",
            "public async Task<WorkAssignmentReportResponse> SaveDraftPatchAsync(");
        var submit = Slice(
            source,
            "public async Task<WorkAssignmentReportResponse> SubmitAsync(",
            "public async Task<WorkAssignmentReportResponse> AcceptAsync(");

        AssertMutationOrder(save, "save draft");
        AssertMutationOrder(submit, "submit");
        AssertContains(save, "BuildPayloadMutationCommitFilter(entity, payloadCommand)", "save header commit must use CAS filter");
        AssertContains(submit, "BuildPayloadMutationCommitFilter(entity, payloadCommand)", "submit header commit must use CAS filter");

        var commitFilter = Slice(
            source,
            "private static FilterDefinition<WorkAssignmentReport> BuildPayloadMutationCommitFilter(",
            "private static UpdateDefinition<WorkAssignmentReport> ApplyPayloadCommandCompletion(");
        foreach (var invariant in new[]
                 {
                     "x.PayloadRevision, command.ExpectedPayloadRevision",
                     "x.PayloadMutationCommandId, command.CommandId",
                     "x.PayloadMutationCommandHash, command.CommandHash",
                     "x.PayloadMutationOperation, command.Operation"
                 })
        {
            AssertContains(commitFilter, invariant, $"CAS commit invariant {invariant}");
        }
        AssertContains(commitFilter, "fb.Exists(x => x.PayloadRevision, false)", "legacy revision-zero reports remain CAS-upgradable");
    }

    private static void MappingAndAutomaticAggregateWritesUseThePayloadCasPipeline()
    {
        var source = ReadBackendSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var mapping = Slice(
            source,
            "public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(",
            "private async Task<(WorkAssignmentReport entity,");
        var aggregate = Slice(
            source,
            "private async Task<WorkAssignmentReport?> RefreshDynamicFormAggregateReportFromSummaryAsync(",
            "private async Task<WorkAssignmentReport?> RefreshStackedDynamicFormAggregateReportFromSummaryAsync(");
        var stacked = Slice(
            source,
            "private async Task<WorkAssignmentReport?> RefreshStackedDynamicFormAggregateReportFromSummaryAsync(",
            "private static void ClearAggregateDraftTargetIndexes(");

        AssertBefore(
            mapping,
            "ValidateRuntimeDataPayloadAsync(",
            "WorkReportPayloadService.PreflightReportPayload(",
            "dynamic-flow mapping: runtime validation before pure payload preflight");
        AssertBefore(
            mapping,
            "WorkReportPayloadService.PreflightReportPayload(",
            "_dynamicFlowTransactions.ExecuteAsync(",
            "dynamic-flow mapping: payload preflight before the mandatory transaction");
        AssertBefore(
            mapping,
            "RevalidateDynamicFlowMappingWriteBoundaryAsync(",
            "_payloadWriter.SaveReportPayloadAsync(",
            "dynamic-flow mapping: in-transaction pins and CAS revalidation before payload write");
        AssertBefore(
            mapping,
            "_ctx.DynamicFlowMappingApplyReceipts.InsertOneAsync(",
            "_payloadWriter.SaveReportPayloadAsync(",
            "dynamic-flow mapping: idempotency receipt is claimed in the same transaction before payload write");
        AssertBefore(
            mapping,
            "_payloadWriter.SaveReportPayloadAsync(",
            "BuildDynamicFlowMappingCommitFilter(",
            "dynamic-flow mapping: session payload write before atomic mapping header CAS");
        AssertContains(
            mapping,
            "BuildDynamicFlowMappingCommitFilter(",
            "dynamic-flow mapping header commit must use exact target CAS");
        AssertContains(
            mapping,
            "BuildDynamicFlowMappingPersistenceBundle(",
            "dynamic-flow mapping must freeze its durable receipt/provenance/event/outbox bundle before commit");
        AssertMutationOrder(aggregate, "automatic aggregate refresh", "BuildPayloadMutationCommitFilter(report, payloadCommand, expectedStatus)");
        AssertMutationOrder(stacked, "stacked automatic aggregate refresh", "BuildPayloadMutationCommitFilter(report, payloadCommand, expectedStatus)");
        var mappingPersistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        AssertContains(
            mapping,
            "ResolveDynamicFlowMappingApplyCommand(",
            "mapping resolves its exact target revision and idempotency contract before persistence");
        AssertContains(
            mappingPersistence,
            "request.ExpectedPayloadRevision",
            "mapping request supplies the observed payload revision");
        AssertContains(
            mappingPersistence,
            "request.CommandId",
            "mapping request supplies an idempotency key");

        var mappingDto = ReadBackendSource("DTOs/DynamicFlows/DynamicFlowMappingDtos.cs");
        AssertContains(mappingDto, "public int? ExpectedPayloadRevision", "mapping DTO exposes payload CAS revision");
        AssertContains(mappingDto, "public string? CommandId", "mapping DTO exposes payload command id");
    }

    private static void AutomaticAggregateInvalidationUsesLifecycleCas()
    {
        var source = ReadBackendSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var invalidation = Slice(
            source,
            "private async Task MoveApprovedAggregateReportBackToSubmittedAsync(",
            "private async Task MarkAggregateSnapshotDirtyAsync(");

        AssertContains(invalidation, "WorkReportLifecycleCommandContract.Resolve", "system invalidation resolves a deterministic lifecycle command");
        AssertContains(invalidation, "WorkReportLifecycleCommandContract.BuildCommitFilter", "system invalidation commits through lifecycle CAS");
        AssertContains(invalidation, "WorkReportLifecycleCommandContract.ApplyCompletion", "system invalidation advances lifecycle metadata");
        AssertContains(invalidation, "WorkReportLifecycleCommandContract.ApplyCompletionInMemory", "system invalidation returns the committed revision in memory");
        AssertContains(
            invalidation,
            "WorkReportLifecycleOutboxContract.Append",
            "system invalidation atomically persists projection recovery work");
        AssertContains(
            invalidation,
            "ReconcileLifecycleProjectionUnlessAggregateRecoveryAsync",
            "system invalidation delegates section repair to the durable reconciler");
    }

    private static void ReactivateDuplicateKeyMapsToStableConflict()
    {
        var source = ReadBackendSource("Services/WorkAssignments/Review/WorkAssignmentReviewService.cs");
        var reactivate = Slice(
            source,
            "public async Task<WorkReportLifecycleCommitResponse> ReactivateReportAsync(",
            "public async Task<PagedResult<ReviewReportFlatRowDto>> SearchReportsForReviewAsync(");

        AssertContains(reactivate, "ServerErrorCategory.DuplicateKey", "reactivate catches the unique-current race");
        AssertContains(reactivate, "AppErrorCode.WORK_ASSIGNMENT_REPORT_CURRENT_CONFLICT", "reactivate maps duplicate-key to stable 409 code");
    }

    private static void RuntimeMutationCapabilitiesAreServerDerived()
    {
        var dto = ReadBackendSource("DTOs/WorkAssignmentReports/WorkAssignmentReportResponse.cs");
        foreach (var capability in new[] { "CanEditPayload", "CanSubmit", "CanWithdraw" })
            AssertContains(dto, $"public bool {capability}", $"runtime response capability {capability}");

        var source = ReadBackendSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var mapper = Slice(
            source,
            "private async Task<WorkAssignmentReportResponse> MapToResponseAsync(",
            "private async Task<WorkAssignmentReportResponse> MapPayloadCommandReplayResponseAsync(");

        AssertContains(mapper, "x.AssigneeUserId, actorUserId", "capabilities must use the authenticated actor identity");
        AssertContains(mapper, "IsReportMutationScopeOpenAsync(assignment, ct)", "capabilities must honor completed work/assignment locks");
        AssertContains(mapper, "actorOwnsReport && x.IsActive && mutationScopeOpen", "reviewer/admin reads must remain readonly");
        AssertContains(mapper, "CanEditPayload = canMutate && x.Status == WorkAssignmentReportStatus.Draft", "draft edit capability");
        AssertContains(mapper, "CanSubmit = canMutate && x.Status == WorkAssignmentReportStatus.Draft", "draft submit capability");
        AssertContains(mapper, "WorkAssignmentAutoApprovalState.CanReporterWithdraw(x)", "auto-approved reporter withdrawal capability");
    }

    private static void AssertMutationOrder(string body, string operation)
        => AssertMutationOrder(body, operation, "BuildPayloadMutationCommitFilter(entity, payloadCommand)");

    private static void AssertMutationOrder(string body, string operation, string commitMarker)
    {
        AssertBefore(
            body,
            "ValidateRuntimeDataPayloadAsync(",
            "WorkReportPayloadService.PreflightReportPayload(",
            $"{operation}: runtime validation before pure payload preflight");
        AssertBefore(
            body,
            "WorkReportPayloadService.PreflightReportPayload(",
            "ReservePayloadMutationCommandAsync(",
            $"{operation}: every payload document must preflight before CAS reservation");
        AssertBefore(
            body,
            "ReservePayloadMutationCommandAsync(",
            "_payloadWriter.SaveReportPayloadAsync(",
            $"{operation}: CAS reservation before external payload writes");
        AssertBefore(
            body,
            "_payloadWriter.SaveReportPayloadAsync(",
            commitMarker,
            $"{operation}: payload write before atomic header commit");
    }

    private static object? ResolvePayloadCommand(
        WorkAssignmentReport report,
        int? expectedRevision,
        string? commandId,
        string operation,
        string hash)
        => InvokePrivateStatic(
            "ResolvePayloadMutationCommand",
            report,
            expectedRevision,
            commandId,
            operation,
            hash);

    private static void InvokeTableProjectionValidation(string mode, string blockJson)
    {
        using var document = JsonDocument.Parse(blockJson);
        _ = InvokePrivateStatic(
            "EnsureRuntimeTableModeProjectionRequired",
            Report(payloadRevision: 0),
            "table_block",
            document.RootElement.Clone(),
            mode);
    }

    private static object? InvokePrivateStatic(string methodName, params object?[] arguments)
    {
        var method = typeof(WorkAssignmentReportService).GetMethod(
                         methodName,
                         BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException($"Private runtime contract method not found: {methodName}");
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static WorkAssignmentReport Report(int payloadRevision)
        => new()
        {
            Id = ReportId,
            PayloadRevision = payloadRevision
        };

    private static void Code(AppErrorCode expected, Action action)
    {
        var error = Throws<AppException>(action);
        Equal(expected, error.Code, "application error code");
    }

    private static void Reason(string expected, Action action)
    {
        var error = Throws<AppException>(action);
        var reason = error.Details?.GetType().GetProperty("reason")?.GetValue(error.Details)?.ToString();
        Equal(expected, reason, "runtime table validation reason");
    }

    private static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static string ReadBackendSource(string relativePath)
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                var direct = Path.Combine(directory.FullName, "tdtd-be.csproj");
                if (File.Exists(direct))
                    return File.ReadAllText(Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));

                var nestedRoot = Path.Combine(directory.FullName, "tdtd-be");
                if (File.Exists(Path.Combine(nestedRoot, "tdtd-be.csproj")))
                    return File.ReadAllText(Path.Combine(nestedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        throw new InvalidOperationException("Could not locate the tdtd-be source root.");
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = startIndex < 0 ? -1 : source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0)
            throw new InvalidOperationException($"Source contract anchors were not found: {start} -> {end}");

        return source[startIndex..endIndex];
    }

    private static void AssertBefore(string source, string first, string second, string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
            throw new InvalidOperationException($"{context}: expected '{first}' before '{second}'.");
    }

    private static void AssertContains(string source, string expected, string context)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{expected}'.");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected '{expected}', got '{actual}'.");
    }

    private static void True(bool value, string context)
    {
        if (!value)
            throw new InvalidOperationException($"{context}: expected true.");
    }
}
