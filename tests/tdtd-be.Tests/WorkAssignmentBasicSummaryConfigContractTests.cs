using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsConfiguration;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignments.BasicSummary;

internal static class WorkAssignmentBasicSummaryConfigContractTests
{
    private const string AssignmentId = "100000000000000000000001";
    private const string TemplateId = "200000000000000000000001";
    private const string ConfigId = "300000000000000000000001";
    private const string VersionId = "400000000000000000000001";
    private const string ActorId = "500000000000000000000001";

    public static void Run()
    {
        StrictEnvelopeAndPublicPayloadShapeRejectUnknowns();
        ExactTypedOperationMatrixHasNoAliasOrFallback();
        SourcePeriodGroupingAndDetailShapesAreStrict();
        EveryFlowReadbackIsBlockedUntilP9();
        IdentityVersionEmptyAndMeanReadbacksAreTyped();
        CasReceiptLockNextDraftAndLineageAreFrozen();
        AuthorizationPrecedesStrictSchemaParsing();
        ConfigPathIsExecutorFreeAndRuntimeRoutesStayBlocked();
    }

    private static void StrictEnvelopeAndPublicPayloadShapeRejectUnknowns()
    {
        using var validDocument = JsonDocument.Parse(ValidEnvelope());
        var envelope = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkAssignmentBasicSummaryConfigPayload>>(
            validDocument.RootElement);
        var payload = WorkAssignmentBasicSummaryService
            .NormalizeP804Payload(envelope.Payload);
        var command = StatConfigCanonicalJson.NormalizeCommand(
            new StatConfigMutationEnvelope<
                WorkAssignmentBasicSummaryConfigPayload>(
                envelope.CommandId,
                envelope.ExpectedRevision,
                envelope.ExpectedConfigHash,
                payload),
            StatConfigCommandKinds.UpsertBasicSummaryConfig);

        AssertEqual("basic-put-1", command.CommandId, "strict command id");
        AssertEqual(0L, command.ExpectedRevision, "strict expected revision");
        AssertEqual(
            StatConfigCanonicalJson.EmptyConfigHash,
            command.ExpectedConfigHash,
            "strict expected hash");

        AssertPropertyNames(
            typeof(WorkAssignmentBasicSummaryConfigPayload),
            ["SourceScope", "PeriodRule", "GroupingHints", "DetailHints", "Targets", "NativeTargets"]);
        AssertPropertyNames(
            typeof(WorkAssignmentBasicSummaryNativeTargetPayload),
            ["TableId", "TargetId", "OperationId"]);
        AssertPropertyNames(
            typeof(WorkAssignmentBasicSummarySourceScopePayload),
            ["Mode", "FlowInstanceId", "FlowStepId", "FlowBranchId", "FlowEffectiveStatus"]);
        AssertPropertyNames(
            typeof(WorkAssignmentBasicSummaryPeriodRulePayload),
            ["Mode", "PeriodKey", "PeriodKeyFrom", "PeriodKeyTo"]);
        AssertPropertyNames(
            typeof(WorkAssignmentBasicSummaryDetailHintsPayload),
            ["IncludeSourceRows", "MaxTextChars"]);
        AssertPropertyNames(
            typeof(WorkAssignmentBasicSummaryTargetPayload),
            ["ConceptKind", "ConceptKey", "DataType", "Operation"]);
        AssertPropertyNames(
            typeof(WorkAssignmentBasicSummaryEmptyCommandPayload),
            []);

        var unknownDocuments = new[]
        {
            ValidEnvelope(envelopeExtra: ",\"ownerId\":\"caller-forged\""),
            ValidEnvelope(payloadJson: AddFirstProperty(
                ValidPayloadJson(),
                "\"defaultOperations\":{}")),
            ValidEnvelope(payloadJson: ValidPayloadJson(
                sourceScopeJson:
                    "{\"mode\":\"DIRECT_CHILDREN_OR_SELF\",\"subtree\":true}")),
            ValidEnvelope(payloadJson: ValidPayloadJson(
                periodRuleJson:
                    "{\"mode\":\"ALL_PERIODS\",\"timeAxis\":\"UTC\"}")),
            ValidEnvelope(payloadJson: ValidPayloadJson(
                detailHintsJson:
                    "{\"includeSourceRows\":true,\"maxTextChars\":1000," +
                    "\"summaryPreview\":true}")),
            ValidEnvelope(payloadJson: ValidPayloadJson(
                targetsJson:
                    "[{\"conceptKind\":\"FIELD\",\"conceptKey\":\"field-1\"," +
                    "\"conceptCode\":\"legacy\",\"dataType\":\"NUMBER\"," +
                    "\"operation\":\"SUM\"}]")),
            ValidEnvelope(payloadJson: ValidPayloadJson(
                targetsJson:
                    "[{\"conceptKind\":\"FIELD\",\"conceptKey\":\"field-1\"," +
                    "\"dataType\":\"NUMBER\",\"operation\":\"SUM\"," +
                    "\"operation\":\"COUNT\"}]")),
            ValidEnvelope(payloadJson: ValidPayloadJson(
                groupingHintsJson: "{\"value\":\"UNIT\"}"))
        };
        foreach (var json in unknownDocuments)
            ExpectSchemaJson(json);

        using var emptyCommand = JsonDocument.Parse(
            "{\"commandId\":\"lock-1\",\"expectedRevision\":1," +
            "\"expectedConfigHash\":\"" + new string('a', 64) +
            "\",\"payload\":{}}" );
        _ = StatConfigCanonicalJson.DeserializeStrict<
            StatConfigMutationEnvelope<
                WorkAssignmentBasicSummaryEmptyCommandPayload>>(
            emptyCommand.RootElement);

        using var forgedEmptyCommand = JsonDocument.Parse(
            "{\"commandId\":\"lock-1\",\"expectedRevision\":1," +
            "\"expectedConfigHash\":\"" + new string('a', 64) +
            "\",\"payload\":{\"force\":true}}" );
        ExpectError(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            () => StatConfigCanonicalJson.DeserializeStrict<
                StatConfigMutationEnvelope<
                    WorkAssignmentBasicSummaryEmptyCommandPayload>>(
                forgedEmptyCommand.RootElement));
    }

    private static void ExactTypedOperationMatrixHasNoAliasOrFallback()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [WorkAssignmentBasicSummaryConfigContract.Number] =
                ["SUM", "MIN", "MAX", "MEAN", "COUNT"],
            [WorkAssignmentBasicSummaryConfigContract.Date] =
                ["MIN_DATE", "MAX_DATE", "COUNT"],
            [WorkAssignmentBasicSummaryConfigContract.Boolean] =
                ["TRUE_COUNT", "FALSE_COUNT", "COUNT"],
            [WorkAssignmentBasicSummaryConfigContract.Choice] =
                ["BUCKET_COUNT", "COUNT"],
            [WorkAssignmentBasicSummaryConfigContract.Text] =
                ["JOIN", "COUNT"]
        };
        var allOperations = expected.Values
            .SelectMany(value => value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var (dataType, allowed) in expected)
        {
            foreach (var operation in allowed)
            {
                var normalized = Normalize(TargetPayload(dataType, operation));
                AssertEqual(
                    operation,
                    normalized.Targets!.Single().Operation,
                    $"allowed operation {dataType}/{operation}");
            }

            foreach (var incompatible in allOperations.Except(
                         allowed,
                         StringComparer.Ordinal))
            {
                ExpectSchema(
                    () => Normalize(TargetPayload(dataType, incompatible)),
                    $"incompatible operation {dataType}/{incompatible}");
            }
        }

        foreach (var (dataType, alias) in new[]
                 {
                     (WorkAssignmentBasicSummaryConfigContract.Number, "AVG"),
                     (WorkAssignmentBasicSummaryConfigContract.Number, "AVERAGE"),
                     (WorkAssignmentBasicSummaryConfigContract.Date, "EARLIEST_DATE"),
                     (WorkAssignmentBasicSummaryConfigContract.Date, "LATEST_DATE"),
                     (WorkAssignmentBasicSummaryConfigContract.Text, "SAMPLE"),
                     (WorkAssignmentBasicSummaryConfigContract.Text, "TEXT_SAMPLE"),
                     (WorkAssignmentBasicSummaryConfigContract.Choice, "OPTION_COUNT"),
                     (WorkAssignmentBasicSummaryConfigContract.Text, "CONCAT"),
                     (WorkAssignmentBasicSummaryConfigContract.Date, "EARLIEST"),
                     (WorkAssignmentBasicSummaryConfigContract.Date, "LATEST")
                 })
        {
            ExpectSchema(
                () => Normalize(TargetPayload(dataType, alias)),
                $"legacy alias {dataType}/{alias}");
        }

        foreach (var unsupportedType in new[]
                 {
                     "FULL_DATE", "STRING", "SELECTION", "LONG_TEXT"
                 })
        {
            ExpectSchema(
                () => Normalize(TargetPayload(unsupportedType, "COUNT")),
                $"unsupported datatype {unsupportedType}");
        }

        ExpectSchema(
            () => Normalize(TargetPayload(
                WorkAssignmentBasicSummaryConfigContract.Number,
                null)),
            "missing operation has no fallback");
        ExpectSchema(
            () => Normalize(TargetPayload(
                WorkAssignmentBasicSummaryConfigContract.Number,
                "DEFAULT")),
            "default operation has no fallback");
    }

    private static void SourcePeriodGroupingAndDetailShapesAreStrict()
    {
        foreach (var mode in new[]
                 {
                     WorkAssignmentBasicSummaryConfigContract.DirectChildrenOrSelf,
                     WorkAssignmentBasicSummaryConfigContract.DirectChildren,
                     WorkAssignmentBasicSummaryConfigContract.Self
                 })
        {
            var normalized = Normalize(Payload(
                source: new WorkAssignmentBasicSummarySourceScopePayload(
                    mode, null, null, null, null)));
            AssertEqual(mode, normalized.SourceScope!.Mode, $"source mode {mode}");
            AssertNull(normalized.SourceScope.FlowInstanceId, $"{mode} flow id");
        }

        var flowCases = new[]
        {
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.FlowBranch,
                "flow-1", null, "branch-1", null),
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.FlowStep,
                "flow-1", "step-1", null, null),
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.FlowEffectivePath,
                "flow-1", null, null, null),
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.FlowFinal,
                "flow-1", null, null, null)
        };
        var statuses = new[]
        {
            WorkAssignmentBasicSummaryConfigContract.Effective,
            WorkAssignmentBasicSummaryConfigContract.Invalidated,
            WorkAssignmentBasicSummaryConfigContract.Terminated,
            WorkAssignmentBasicSummaryConfigContract.Any
        };
        foreach (var flowCase in flowCases)
        {
            foreach (var status in statuses)
            {
                var normalized = Normalize(Payload(
                    source: flowCase with { FlowEffectiveStatus = status }));
                AssertEqual(
                    status,
                    normalized.SourceScope!.FlowEffectiveStatus,
                    $"flow status {flowCase.Mode}/{status}");
            }
        }

        ExpectSchema(
            () => Normalize(Payload(
                source: flowCases[0] with { FlowInstanceId = null })),
            "FLOW instance id required");
        ExpectSchema(
            () => Normalize(Payload(
                source: flowCases[0] with
                {
                    FlowEffectiveStatus = null
                })),
            "FLOW effective status required");
        ExpectSchema(
            () => Normalize(Payload(
                source: flowCases[0] with
                {
                    FlowBranchId = null,
                    FlowEffectiveStatus = statuses[0]
                })),
            "FLOW_BRANCH branch id required");
        ExpectSchema(
            () => Normalize(Payload(
                source: flowCases[1] with
                {
                    FlowStepId = null,
                    FlowEffectiveStatus = statuses[0]
                })),
            "FLOW_STEP step id required");
        ExpectSchema(
            () => Normalize(Payload(
                source: flowCases[1] with
                {
                    FlowEffectiveStatus = statuses[0],
                    FlowBranchId = "branch-not-applicable"
                })),
            "FLOW_STEP rejects branch id");
        ExpectSchema(
            () => Normalize(Payload(
                source: flowCases[2] with
                {
                    FlowEffectiveStatus = statuses[0],
                    FlowStepId = "step-not-applicable"
                })),
            "FLOW_EFFECTIVE_PATH rejects step id");
        ExpectSchema(
            () => Normalize(Payload(
                source: new WorkAssignmentBasicSummarySourceScopePayload(
                    WorkAssignmentBasicSummaryConfigContract.Self,
                    "flow-not-applicable", null, null, null))),
            "non-FLOW rejects flow identity");
        ExpectSchema(
            () => Normalize(Payload(
                source: new WorkAssignmentBasicSummarySourceScopePayload(
                    "SUBTREE", null, null, null, null))),
            "SUBTREE is not a Basic source mode");

        var periodCases = new[]
        {
            new WorkAssignmentBasicSummaryPeriodRulePayload(
                WorkAssignmentBasicSummaryConfigContract.AllPeriods,
                null, null, null),
            new WorkAssignmentBasicSummaryPeriodRulePayload(
                WorkAssignmentBasicSummaryConfigContract.SinglePeriod,
                "2026-08", null, null),
            new WorkAssignmentBasicSummaryPeriodRulePayload(
                WorkAssignmentBasicSummaryConfigContract.PeriodRange,
                null, "2026-01", "2026-08")
        };
        foreach (var period in periodCases)
        {
            var normalized = Normalize(Payload(period: period));
            AssertEqual(
                period.Mode,
                normalized.PeriodRule!.Mode,
                $"period mode {period.Mode}");
        }
        ExpectSchema(
            () => Normalize(Payload(
                period: new WorkAssignmentBasicSummaryPeriodRulePayload(
                    "CUMULATIVE_TO_PERIOD", null, null, "2026-08"))),
            "Basic cumulative mode stays forbidden");
        ExpectSchema(
            () => Normalize(Payload(
                period: new WorkAssignmentBasicSummaryPeriodRulePayload(
                    WorkAssignmentBasicSummaryConfigContract.AllPeriods,
                    "not-applicable", null, null))),
            "ALL_PERIODS rejects keys");
        ExpectSchema(
            () => Normalize(Payload(
                period: new WorkAssignmentBasicSummaryPeriodRulePayload(
                    WorkAssignmentBasicSummaryConfigContract.SinglePeriod,
                    null, null, null))),
            "SINGLE_PERIOD requires one key");
        ExpectSchema(
            () => Normalize(Payload(
                period: new WorkAssignmentBasicSummaryPeriodRulePayload(
                    WorkAssignmentBasicSummaryConfigContract.PeriodRange,
                    null, "2026-09", "2026-08"))),
            "PERIOD_RANGE rejects reversed keys");
        ExpectSchema(
            () => Normalize(Payload(
                period: new WorkAssignmentBasicSummaryPeriodRulePayload(
                    WorkAssignmentBasicSummaryConfigContract.PeriodRange,
                    null, "2026-01", null))),
            "PERIOD_RANGE requires both keys");

        var grouped = Normalize(Payload(
            grouping:
            [
                WorkAssignmentBasicSummaryConfigContract.Period,
                WorkAssignmentBasicSummaryConfigContract.Unit,
                WorkAssignmentBasicSummaryConfigContract.Assignment
            ]));
        AssertSequenceEqual(
            ["PERIOD", "UNIT", "ASSIGNMENT"],
            grouped.GroupingHints!,
            "grouping hints preserve caller order");
        ExpectSchema(
            () => Normalize(Payload(grouping: ["UNIT", "UNIT"])),
            "grouping hints are unique");
        ExpectSchema(
            () => Normalize(Payload(grouping: ["TEAM"])),
            "grouping hint set is exact");

        foreach (var boundary in new[]
                 {
                     WorkAssignmentBasicSummaryConfigContract.MinimumMaxTextChars,
                     WorkAssignmentBasicSummaryConfigContract.MaximumMaxTextChars
                 })
        {
            var normalized = Normalize(Payload(
                detail: new WorkAssignmentBasicSummaryDetailHintsPayload(
                    false,
                    boundary)));
            AssertEqual(
                boundary,
                normalized.DetailHints!.MaxTextChars,
                $"maxTextChars boundary {boundary}");
        }
        foreach (var invalid in new[] { 999, 100001 })
        {
            ExpectSchema(
                () => Normalize(Payload(
                    detail: new WorkAssignmentBasicSummaryDetailHintsPayload(
                        true,
                        invalid))),
                $"maxTextChars rejects {invalid}");
        }
        ExpectSchema(
            () => Normalize(Payload(
                detail: new WorkAssignmentBasicSummaryDetailHintsPayload(
                    null,
                    1000))),
            "includeSourceRows is required");
        ExpectSchema(
            () => Normalize(Payload(
                detail: new WorkAssignmentBasicSummaryDetailHintsPayload(
                    true,
                    null))),
            "maxTextChars is required");
    }

    private static void EveryFlowReadbackIsBlockedUntilP9()
    {
        var flowScopes = new[]
        {
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.FlowBranch,
                "flow-1", null, "branch-1", "EFFECTIVE"),
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.FlowStep,
                "flow-1", "step-1", null, "EFFECTIVE"),
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.FlowEffectivePath,
                "flow-1", null, null, "EFFECTIVE"),
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.FlowFinal,
                "flow-1", null, null, "EFFECTIVE")
        };

        foreach (var scope in flowScopes)
        {
            var payload = Normalize(Payload(source: scope));
            var readback = InvokeReadback(payload, isVirtual: false);
            AssertEqual(
                scope.Mode,
                readback.Payload.SourceScope!.Mode,
                $"FLOW readback source {scope.Mode}");
            AssertEqual(
                WorkAssignmentBasicSummaryConfigContract
                    .RuntimeEligibilityBlockedUntilP9,
                readback.RuntimeEligibility,
                $"FLOW readback eligibility {scope.Mode}");
            AssertFalse(
                readback.Permissions.CanViewResult,
                $"FLOW result permission {scope.Mode}");
        }
    }

    private static void IdentityVersionEmptyAndMeanReadbacksAreTyped()
    {
        AssertPropertyType<StatConfigIdentity>(
            typeof(WorkAssignmentBasicSummaryConfigReadback),
            "Identity");
        AssertPropertyType<WorkAssignmentBasicSummaryConfigPayload>(
            typeof(WorkAssignmentBasicSummaryConfigReadback),
            "Payload");
        AssertPropertyType<StatConfigPermissionSet>(
            typeof(WorkAssignmentBasicSummaryConfigReadback),
            "Permissions");
        AssertPropertyType<IReadOnlyList<
            WorkAssignmentBasicSummaryConfigVersionDto>>(
            typeof(WorkAssignmentBasicSummaryConfigReadback),
            "Versions");
        AssertPropertyType<WorkAssignmentBasicSummaryMeanContract>(
            typeof(WorkAssignmentBasicSummaryConfigReadback),
            "MeanContract");
        AssertPropertyType<IReadOnlyList<string>>(
            typeof(WorkAssignmentBasicSummaryConfigVersionDto),
            "DependencyPins");
        AssertPropertyType<WorkAssignmentBasicSummaryConfigPayload>(
            typeof(WorkAssignmentBasicSummaryConfigVersionDto),
            "Payload");

        AssertPropertyType<string>(
            typeof(WorkAssignmentBasicSummaryConfig),
            "ConfigJson");
        AssertPropertyType<List<string>>(
            typeof(WorkAssignmentBasicSummaryConfig),
            "DependencyPins");
        AssertPropertyType<List<WorkAssignmentBasicSummaryConfigVersion>>(
            typeof(WorkAssignmentBasicSummaryConfig),
            "Versions");
        foreach (var property in new[]
                 {
                     "VersionId", "PreviousVersionId", "VersionNo",
                     "Revision", "Status", "ConfigHash", "LockedAtUtc",
                     "LockedByUserId"
                 })
        {
            AssertTrue(
                typeof(WorkAssignmentBasicSummaryConfig)
                    .GetProperty(property) is not null,
                $"owner identity property {property}");
        }

        var emptyPayload = Normalize(new WorkAssignmentBasicSummaryConfigPayload(
            new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.DirectChildrenOrSelf,
                null, null, null, null),
            new WorkAssignmentBasicSummaryPeriodRulePayload(
                WorkAssignmentBasicSummaryConfigContract.AllPeriods,
                null, null, null),
            Array.Empty<string>(),
            new WorkAssignmentBasicSummaryDetailHintsPayload(false, 12000),
            Array.Empty<WorkAssignmentBasicSummaryTargetPayload>()));
        var readback = InvokeReadback(emptyPayload, isVirtual: true);

        AssertEqual(
            StatConfigOwnerKinds.BasicSummary,
            readback.Identity.OwnerKind,
            "Basic owner kind");
        AssertEqual(
            $"{AssignmentId}:{TemplateId}",
            readback.Identity.OwnerId,
            "Basic owner identity");
        AssertEqual(ConfigId, readback.Identity.ConfigId, "config id");
        AssertEqual(VersionId, readback.Identity.VersionId, "version id");
        AssertEqual(1, readback.Identity.VersionNo, "virtual version number");
        AssertEqual(0L, readback.Identity.Revision, "virtual revision");
        AssertEqual(StatConfigStatuses.Draft, readback.Identity.Status, "virtual status");
        AssertEqual(
            StatConfigCanonicalJson.EmptyConfigHash,
            readback.Identity.ConfigHash,
            "virtual empty hash");
        AssertTrue(readback.IsVirtualEmpty, "virtual empty marker");
        AssertNull(readback.PreviousVersionId, "virtual lineage root");
        AssertEqual(0, readback.Payload.Targets!.Count, "empty config targets");
        AssertEqual(0, readback.Versions.Count, "empty config has no persisted versions");
        AssertEqual(
            "sum/numericValueCount",
            readback.MeanContract.Formula,
            "MEAN formula");
        AssertTrue(readback.MeanContract.MetadataOnly, "MEAN is metadata-only");
        AssertFalse(readback.Permissions.CanViewResult, "empty config is not an empty result");
        AssertFalse(
            typeof(WorkAssignmentBasicSummaryConfigPayload)
                .GetProperties()
                .Any(property => property.Name is
                    "Fields" or "Tables" or "Sources" or
                    "SummaryValues" or "Warnings"),
            "configuration payload exposes no result shape");
    }

    private static void CasReceiptLockNextDraftAndLineageAreFrozen()
    {
        AssertEqual(
            "UPSERT_BASIC_SUMMARY_CONFIG",
            StatConfigCommandKinds.UpsertBasicSummaryConfig,
            "PUT command kind");
        AssertEqual(
            "LOCK_BASIC_SUMMARY_CONFIG",
            StatConfigCommandKinds.LockBasicSummaryConfig,
            "lock command kind");
        AssertEqual(
            "CREATE_BASIC_SUMMARY_DRAFT",
            StatConfigCommandKinds.CreateBasicSummaryDraft,
            "next-draft command kind");

        const string assignmentUpper = "ABCDEFABCDEFABCDEFABCDEF";
        const string assignmentLower = "abcdefabcdefabcdefabcdef";
        const string templateUpper = "FEDCBAFEDCBAFEDCBAFEDCBA";
        const string templateLower = "fedcbafedcbafedcbafedcba";
        var normalizedAssignment = InvokePrivateIdNormalizer(
            "P804NormalizeAssignmentId",
            assignmentUpper);
        var normalizedTemplate = InvokePrivateIdNormalizer(
            "P804NormalizeTemplateId",
            templateUpper);
        AssertEqual(
            assignmentLower,
            normalizedAssignment,
            "assignment route ObjectId is canonical lowercase");
        AssertEqual(
            templateLower,
            normalizedTemplate,
            "template route ObjectId is canonical lowercase");
        var canonicalOwnerId = WorkAssignmentBasicSummaryService.P804OwnerId(
            normalizedAssignment,
            normalizedTemplate);
        var lowerOwnerId = WorkAssignmentBasicSummaryService.P804OwnerId(
            assignmentLower,
            templateLower);
        AssertEqual(lowerOwnerId, canonicalOwnerId, "canonical Basic owner id");
        AssertEqual(
            WorkAssignmentBasicSummaryService.P804ReceiptId(
                lowerOwnerId,
                "case-stable-command"),
            WorkAssignmentBasicSummaryService.P804ReceiptId(
                canonicalOwnerId,
                "case-stable-command"),
            "route casing cannot fork receipt identity");

        var ownerId = $"{AssignmentId}:{TemplateId}";
        var receiptId = WorkAssignmentBasicSummaryService.P804ReceiptId(
            ownerId,
            "command-1");
        AssertEqual(
            receiptId,
            WorkAssignmentBasicSummaryService.P804ReceiptId(
                ownerId,
                "command-1"),
            "receipt identity is deterministic");
        AssertFalse(
            string.Equals(
                receiptId,
                WorkAssignmentBasicSummaryService.P804ReceiptId(
                    ownerId,
                    "command-2"),
                StringComparison.Ordinal),
            "receipt identity includes command id");

        var replayPayload = Normalize(Payload());
        var replayResponse = InvokeReadback(replayPayload, isVirtual: false);
        var responseJson = StatConfigCanonicalJson.Canonicalize(replayResponse);
        var receipt = new StatConfigCommandReceipt
        {
            Id = receiptId,
            OwnerKind = StatConfigOwnerKinds.BasicSummary,
            OwnerId = ownerId,
            CommandKind = StatConfigCommandKinds.UpsertBasicSummaryConfig,
            CommandId = "command-1",
            RequestHash = new string('b', 64),
            ResponseJson = responseJson,
            ResponseHash = StatConfigCanonicalJson.HashUtf8(responseJson),
            ResultConfigId = ConfigId,
            ResultVersionId = VersionId,
            ResultVersionNo = 1,
            ResultRevision = 0,
            ResultStatus = StatConfigStatuses.Draft,
            ResultConfigHash = StatConfigCanonicalJson.EmptyConfigHash,
            ActorUserId = ActorId
        };
        var restored = InvokeRestoreReplay(receipt, receipt.RequestHash);
        AssertEqual(
            replayResponse.Identity.OwnerId,
            restored.Identity.OwnerId,
            "exact replay restores typed response");
        ExpectReflectedError(
            AppErrorCode.STAT_CONFIG_COMMAND_REPLAY_CONFLICT,
            () => InvokeRestoreReplay(receipt, new string('c', 64)),
            "changed replay is conflict");

        var commandSource = ReadBackendSource(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P804.Config.cs");
        var contractSource = ReadBackendSource(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P804.Config.Contract.cs");
        var stateSource = ReadBackendSource(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P804.Config.State.cs");

        foreach (var required in new[]
                 {
                     "_statConfigTransactions.ExecuteAsync",
                     "P804LoadReplayAsync",
                     "P804EnsureCas(current, command);",
                     "P804CreateReceipt",
                     "STAT_CONFIG_COMMAND_REPLAY_CONFLICT",
                     "BASIC_SUMMARY_CONFIG_VERSION_LOCKED",
                     "BASIC_SUMMARY_CONFIG_DEPENDENCY_STALE",
                     "P804BuildEntityCasFilter",
                     "ModifiedCount != 1",
                     "StatConfigIsolationGuard.EnterConfigurationMutation"
                 })
        {
            AssertContains(commandSource, required, $"command lifecycle {required}");
        }
        AssertOrder(
            commandSource,
            "var replay = await P804LoadReplayAsync",
            "var current = await P804LoadStateAsync",
            "exact replay precedes state/CAS");
        AssertOrder(
            commandSource,
            "P804EnsureCas(current, command);",
            "var next = await apply",
            "CAS precedes mutation");
        AssertOrder(
            commandSource,
            "next = await P804PersistStateAsync",
            "P804CreateReceipt(",
            "owner persistence precedes receipt in one transaction");
        AssertMatches(
            commandSource,
            @"ReplaceOneAsync\(\s*session,\s*P804BuildEntityCasFilter\(current\)",
            "owner replacement uses session and CAS filter");
        AssertMatches(
            commandSource,
            @"StatConfigCommandReceipts\s*\.InsertOneAsync\(\s*session,",
            "durable receipt uses the same transaction session");

        var lockSection = Slice(
            commandSource,
            "P804ApplyLockAsync(",
            "P804ApplyNextDraftAsync(");
        AssertOrder(
            lockSection,
            "P804EnsureDependenciesCurrentAsync",
            "Status = StatConfigStatuses.Locked",
            "lock revalidates dependency pins before LOCKED");
        AssertContains(
            lockSection,
            "Revision = checked(current.Revision + 1)",
            "lock advances revision");

        var nextDraftSection = Slice(
            commandSource,
            "P804ApplyNextDraftAsync(",
            "private async Task P804EnsureDependenciesCurrentAsync(");
        AssertOrder(
            nextDraftSection,
            "await P804EnsureDependenciesCurrentAsync",
            "VersionId = ObjectId.GenerateNewId().ToString()",
            "next draft revalidates pins before new identity");
        foreach (var required in new[]
                 {
                     "PreviousVersionId = current.VersionId",
                     "VersionNo = checked(current.VersionNo + 1)",
                     "Revision = 0",
                     "Status = StatConfigStatuses.Draft"
                 })
        {
            AssertContains(nextDraftSection, required, $"next-draft {required}");
        }
        AssertNotContains(nextDraftSection, "Payload =", "next draft copies payload");
        AssertNotContains(nextDraftSection, "ConfigHash =", "next draft copies hash");
        AssertNotContains(nextDraftSection, "DependencyPins =", "next draft copies pins");

        var hashSection = Slice(
            contractSource,
            "P804ComputeConfigHash(",
            "P804DeserializeStoredPayload(");
        AssertMatches(
            hashSection,
            @"new\s*\{\s*payload,\s*dependencyPins\s*\}",
            "config hash contains normalized payload and pins only");

        foreach (var required in new[]
                 {
                     "BASIC_SUMMARY_CONFIG_LINEAGE_ROOT_INVALID",
                     "BASIC_SUMMARY_CONFIG_LINEAGE_LINK_INVALID",
                     "BASIC_SUMMARY_CONFIG_DRAFT_NOT_CURRENT",
                     "BASIC_SUMMARY_CONFIG_VERSION_SEQUENCE_INVALID",
                     "BASIC_SUMMARY_CONFIG_CURRENT_VERSION_MISMATCH"
                 })
        {
            AssertContains(stateSource, required, $"lineage guard {required}");
        }
    }

    private static void AuthorizationPrecedesStrictSchemaParsing()
    {
        var commandSource = ReadBackendSource(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P804.Config.cs");
        var put = Slice(
            commandSource,
            "PutP8ConfigAsync(",
            "LockP8ConfigAsync(");
        var lockCommand = Slice(
            commandSource,
            "LockP8ConfigAsync(",
            "CreateNextP8DraftAsync(");
        var nextDraft = Slice(
            commandSource,
            "CreateNextP8DraftAsync(",
            "private async Task<P804PreparedCommand>");
        AssertOrder(
            put,
            "P804AuthorizeBeforeBodyAsync",
            "NormalizeP804PutCommand(body)",
            "PUT authorization precedes schema parsing");
        AssertOrder(
            lockCommand,
            "P804AuthorizeBeforeBodyAsync",
            "NormalizeP804EmptyCommand",
            "lock authorization precedes schema parsing");
        AssertOrder(
            nextDraft,
            "P804AuthorizeBeforeBodyAsync",
            "NormalizeP804EmptyCommand",
            "next-draft authorization precedes schema parsing");

        var authorization = Slice(
            commandSource,
            "private async Task<P804PreparedCommand>",
            "private async Task<WorkAssignmentBasicSummaryConfigReadback>");
        AssertOrder(
            authorization,
            "_me.RequireMe()",
            "P804LoadAuthorizedAssignmentAsync",
            "actor is established before scoped owner read");
        AssertContains(
            authorization,
            "requireManage: true",
            "mutations require manage authority");
        AssertOrder(
            authorization,
            "P804LoadAuthorizedAssignmentAsync",
            "P804LoadTemplateAsync",
            "owner scope is authorized before dependency details");
        AssertNotContains(
            authorization,
            "JsonElement",
            "authorization helper never parses the body");
    }

    private static void ConfigPathIsExecutorFreeAndRuntimeRoutesStayBlocked()
    {
        var command = ReadBackendSource(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P804.Config.cs");
        var contract = ReadBackendSource(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P804.Config.Contract.cs");
        var state = ReadBackendSource(
            "Services/WorkAssignments/BasicSummary/" +
            "WorkAssignmentBasicSummaryService.P804.Config.State.cs");
        var narrowPath = command + Environment.NewLine +
                         contract + Environment.NewLine + state;

        foreach (var forbidden in new[]
                 {
                     "WorkAssignmentBasicSummarySnapshots",
                     "WorkAssignmentBasicSummarySnapshot",
                     "WorkAssignmentReports",
                     "WorkAssignmentReportSections",
                     "WorkReportPayload",
                     "WorkReportStatistic",
                     "_payloadReader",
                     "_backgroundJobs",
                     "GetSummaryAsync",
                     "BuildSummaryAsync",
                     "RefreshSnapshotJobAsync",
                     "ResetSnapshotJobAsync",
                     "BackgroundJob.",
                     ".Enqueue(",
                     "SummaryValues",
                     "SourcesPage"
                 })
        {
            AssertNotContains(
                narrowPath,
                forbidden,
                $"configuration path excludes {forbidden}");
        }

        var accessedCollections = Regex.Matches(
                narrowPath,
                @"_ctx\.(?<name>[A-Za-z0-9_]+)",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var allowedCollections = new HashSet<string>(
            [
                "DynamicFormTemplates",
                "Labels",
                "StatConfigCommandReceipts",
                "WorkAssignmentBasicSummaryConfigs",
                "WorkAssignments",
                "Works"
            ],
            StringComparer.Ordinal);
        AssertTrue(
            accessedCollections.All(allowedCollections.Contains),
            "config collection allowlist: " +
            string.Join(',', accessedCollections));

        var controller = ReadBackendSource(
            "Controllers/WorkAssignmentBasicSummaryController.cs");
        foreach (var route in new[]
                 {
                     "config\")]",
                     "config/versions\")]",
                     "config/versions/{versionNo:int}\")]",
                     "config/lock\")]",
                     "config/next-draft\")]"
                 })
        {
            AssertContains(controller, route, $"typed config route {route}");
        }
        AssertOccurrenceCountAtLeast(
            controller,
            "[FromBody] JsonElement body",
            3,
            "all mutations keep raw strict envelopes");

        var summary = Slice(
            controller,
            "Task<IActionResult> Summary(",
            "[HttpPost(\"once\")]" );
        var once = Slice(
            controller,
            "Task<IActionResult> Once(",
            "private string GetActorUserId()");
        AssertOrder(
            summary,
            "_candidateActivation.RequireCapability(",
            "_service.GetSummaryAsync",
            "/summary validates candidate before executor call");
        AssertOrder(
            once,
            "_candidateActivation.RequireCapability(",
            "_service.GetSummaryAsync",
            "/once validates candidate before executor call");
        foreach (var required in new[]
                 {
                     "StatRunCapabilities.BasicSummary",
                     "StatRunRouteRegistry.BasicResult",
                     "_candidateActivation.RequireCapability"
                 })
        {
            AssertContains(summary, required, $"candidate runtime gate {required}");
            AssertContains(once, required, $"candidate runtime gate {required}");
        }
    }

    private static WorkAssignmentBasicSummaryConfigPayload TargetPayload(
        string dataType,
        string? operation)
        => Payload(
            targets:
            [
                new WorkAssignmentBasicSummaryTargetPayload(
                    WorkAssignmentBasicSummaryConfigContract.Field,
                    "field-1",
                    dataType,
                    operation)
            ]);

    private static WorkAssignmentBasicSummaryConfigPayload Payload(
        WorkAssignmentBasicSummarySourceScopePayload? source = null,
        WorkAssignmentBasicSummaryPeriodRulePayload? period = null,
        IReadOnlyList<string>? grouping = null,
        WorkAssignmentBasicSummaryDetailHintsPayload? detail = null,
        IReadOnlyList<WorkAssignmentBasicSummaryTargetPayload>? targets = null)
        => new(
            source ?? new WorkAssignmentBasicSummarySourceScopePayload(
                WorkAssignmentBasicSummaryConfigContract.DirectChildrenOrSelf,
                null, null, null, null),
            period ?? new WorkAssignmentBasicSummaryPeriodRulePayload(
                WorkAssignmentBasicSummaryConfigContract.AllPeriods,
                null, null, null),
            grouping ?? Array.Empty<string>(),
            detail ?? new WorkAssignmentBasicSummaryDetailHintsPayload(
                true,
                WorkAssignmentBasicSummaryConfigContract
                    .MinimumMaxTextChars),
            targets ??
            [
                new WorkAssignmentBasicSummaryTargetPayload(
                    WorkAssignmentBasicSummaryConfigContract.Field,
                    "field-1",
                    WorkAssignmentBasicSummaryConfigContract.Number,
                    WorkAssignmentBasicSummaryConfigContract.Sum)
            ]);

    private static WorkAssignmentBasicSummaryConfigPayload Normalize(
        WorkAssignmentBasicSummaryConfigPayload payload)
        => WorkAssignmentBasicSummaryService.NormalizeP804Payload(payload);

    private static WorkAssignmentBasicSummaryConfigReadback InvokeReadback(
        WorkAssignmentBasicSummaryConfigPayload payload,
        bool isVirtual)
    {
        var serviceType = typeof(WorkAssignmentBasicSummaryService);
        var stateType = serviceType.GetNestedType(
                            "P804ConfigState",
                            BindingFlags.NonPublic) ??
                        throw new InvalidOperationException(
                            "P804ConfigState was not found.");
        var constructor = stateType.GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .Single(item => item.GetParameters().Length == 12);
        var state = constructor.Invoke(
        [
            null,
            ConfigId,
            VersionId,
            null,
            1,
            0L,
            StatConfigStatuses.Draft,
            StatConfigCanonicalJson.EmptyConfigHash,
            Array.Empty<string>(),
            payload,
            Array.Empty<WorkAssignmentBasicSummaryConfigVersion>(),
            isVirtual
        ]);
        var method = serviceType.GetMethod(
                         "P804ToReadback",
                         BindingFlags.Static | BindingFlags.NonPublic) ??
                     throw new InvalidOperationException(
                         "P804ToReadback was not found.");
        var assignment = new WorkAssignment
        {
            Id = AssignmentId,
            CreatedByUserId = ActorId
        };
        var template = new DynamicFormTemplate { Id = TemplateId };
        var me = new MeResponse(
            ActorId,
            "p8-reviewer",
            "P8 Reviewer",
            new List<string>(),
            "600000000000000000000001",
            "UNIT",
            "Unit",
            "UNIT",
            new List<string>(),
            null,
            false);
        return (WorkAssignmentBasicSummaryConfigReadback)
            (method.Invoke(
                 null,
                 [assignment, template, state, me, null]) ??
             throw new InvalidOperationException(
                 "P804ToReadback returned null."));
    }

    private static string InvokePrivateIdNormalizer(
        string methodName,
        string value)
    {
        var method = typeof(WorkAssignmentBasicSummaryService).GetMethod(
                         methodName,
                         BindingFlags.Static | BindingFlags.NonPublic) ??
                     throw new InvalidOperationException(
                         $"{methodName} was not found.");
        return (string)(method.Invoke(null, [value]) ??
                        throw new InvalidOperationException(
                            $"{methodName} returned null."));
    }

    private static WorkAssignmentBasicSummaryConfigReadback
        InvokeRestoreReplay(
            StatConfigCommandReceipt receipt,
            string requestHash)
    {
        var method = typeof(WorkAssignmentBasicSummaryService).GetMethod(
                         "P804RestoreReplay",
                         BindingFlags.Static | BindingFlags.NonPublic) ??
                     throw new InvalidOperationException(
                         "P804RestoreReplay was not found.");
        return (WorkAssignmentBasicSummaryConfigReadback)
            (method.Invoke(null, [receipt, requestHash]) ??
             throw new InvalidOperationException(
                 "P804RestoreReplay returned null."));
    }

    private static void ExpectSchemaJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        ExpectError(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            () =>
            {
                var envelope = StatConfigCanonicalJson.DeserializeStrict<
                    StatConfigMutationEnvelope<
                        WorkAssignmentBasicSummaryConfigPayload>>(
                    document.RootElement);
                _ = Normalize(envelope.Payload!);
            });
    }

    private static string ValidEnvelope(
        string? payloadJson = null,
        string envelopeExtra = "")
        => "{\"commandId\":\"basic-put-1\",\"expectedRevision\":0," +
           "\"expectedConfigHash\":\"" +
           StatConfigCanonicalJson.EmptyConfigHash + "\"," +
           "\"payload\":" + (payloadJson ?? ValidPayloadJson()) +
           envelopeExtra + "}";

    private static string ValidPayloadJson(
        string sourceScopeJson =
            "{\"mode\":\"DIRECT_CHILDREN_OR_SELF\"}",
        string periodRuleJson = "{\"mode\":\"ALL_PERIODS\"}",
        string groupingHintsJson = "[]",
        string detailHintsJson =
            "{\"includeSourceRows\":true,\"maxTextChars\":1000}",
        string targetsJson =
            "[{\"conceptKind\":\"FIELD\",\"conceptKey\":\"field-1\"," +
            "\"dataType\":\"NUMBER\",\"operation\":\"SUM\"}]")
        => "{\"sourceScope\":" + sourceScopeJson +
           ",\"periodRule\":" + periodRuleJson +
           ",\"groupingHints\":" + groupingHintsJson +
           ",\"detailHints\":" + detailHintsJson +
           ",\"targets\":" + targetsJson + "}";

    private static string AddFirstProperty(
        string jsonObject,
        string property)
        => "{" + property + "," + jsonObject[1..];

    private static AppException ExpectSchema(
        Action action,
        string context)
        => ExpectError(
            AppErrorCode.STAT_CONFIG_SCHEMA_INVALID,
            action,
            context);

    private static AppException ExpectError(
        AppErrorCode expected,
        Action action,
        string context = "stable error")
    {
        try
        {
            action();
        }
        catch (AppException error)
        {
            AssertEqual(expected, error.Code, context);
            return error;
        }

        throw new InvalidOperationException(
            $"{context}: expected {expected}, but no AppException was thrown.");
    }

    private static void ExpectReflectedError(
        AppErrorCode expected,
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
            AssertEqual(expected, appError.Code, context);
            return;
        }
        throw new InvalidOperationException(
            $"{context}: expected reflected {expected}.");
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

    private static string Slice(
        string source,
        string start,
        string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0)
            throw new InvalidOperationException($"Slice start '{start}' was not found.");
        var endIndex = source.IndexOf(
            end,
            startIndex + start.Length,
            StringComparison.Ordinal);
        if (endIndex < 0)
            throw new InvalidOperationException($"Slice end '{end}' was not found.");
        return source[startIndex..endIndex];
    }

    private static void AssertPropertyNames(
        Type type,
        IReadOnlyList<string> expected)
    {
        var actual = type.GetProperties()
            .Select(property => property.Name)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        AssertSequenceEqual(
            expected.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            actual,
            $"{type.Name} exact public properties");
    }

    private static void AssertPropertyType<T>(Type owner, string property)
    {
        var actual = owner.GetProperty(property)?.PropertyType;
        AssertEqual(typeof(T), actual, $"{owner.Name}.{property} type");
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

    private static void AssertOrder(
        string source,
        string first,
        string second,
        string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
        {
            throw new InvalidOperationException(
                $"{context}: expected '{first}' before '{second}'.");
        }
    }

    private static void AssertMatches(
        string source,
        string pattern,
        string context)
    {
        if (!Regex.IsMatch(
                source,
                pattern,
                RegexOptions.CultureInvariant |
                RegexOptions.Singleline))
        {
            throw new InvalidOperationException(
                $"{context}: source pattern '{pattern}' was not found.");
        }
    }

    private static void AssertOccurrenceCountAtLeast(
        string source,
        string expected,
        int minimum,
        string context)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(
                   expected,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }
        if (count < minimum)
        {
            throw new InvalidOperationException(
                $"{context}: expected at least {minimum}, got {count}.");
        }
    }

    private static void AssertSequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string context)
    {
        if (expected.Count != actual.Count ||
            !expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{context}: expected [{string.Join(',', expected)}], " +
                $"got [{string.Join(',', actual)}].");
        }
    }

    private static void AssertTrue(bool condition, string context)
    {
        if (!condition)
            throw new InvalidOperationException($"{context}: expected true.");
    }

    private static void AssertFalse(bool condition, string context)
        => AssertTrue(!condition, context);

    private static void AssertNull(object? value, string context)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(
                $"{context}: expected null, got '{value}'.");
        }
    }

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
