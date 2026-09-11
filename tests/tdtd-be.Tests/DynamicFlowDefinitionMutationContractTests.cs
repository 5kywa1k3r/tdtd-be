using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using tdtd_be.Controllers;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowDefinitionMutationContractTests
{
    public static void Run()
    {
        AssertCanonicalMutationRoutes();
        AssertMutationCommandAndCasTokens();
        AssertClonePinsExactSourceAndReplaysBeforeSourceRevalidation();
        AssertReplayIsResolvedFromTamperEvidentSnapshotsBeforeMutableValidation();
        AssertAggregateWritesUseOneTransactionSession();
        AssertReceiptAndSuccessAuditShareTheTransaction();
        AssertFaultInjectionCoversEveryAtomicWriteBoundary();
        AssertFaultInjectionOrderPerMutation();
        AssertRuntimeAndMappingExecutionStopBeforeWriters();
        AssertPermissionMetadataIsServerDerivedAndFailClosed();
        AssertActiveActorIsCheckedBeforeWritesAndPostCommitProjectionIsPure();
    }

    private static void AssertCanonicalMutationRoutes()
    {
        AssertRoute(nameof(DynamicFlowTemplatesController.Create), typeof(HttpPostAttribute), null);
        AssertRoute(nameof(DynamicFlowTemplatesController.Update), typeof(HttpPutAttribute), "{familyId}");
        AssertRoute(nameof(DynamicFlowTemplatesController.Delete), typeof(HttpDeleteAttribute), "{familyId}");
        AssertRoute(nameof(DynamicFlowTemplatesController.Archive), typeof(HttpPostAttribute), "{familyId}/archive");
        AssertRoute(nameof(DynamicFlowTemplatesController.Clone), typeof(HttpPostAttribute), "{familyId}/clone");
        AssertRoute(
            nameof(DynamicFlowTemplatesController.SaveDraftVersion),
            typeof(HttpPutAttribute),
            "{familyId}/versions/{versionId}/draft");
        AssertRoute(
            nameof(DynamicFlowTemplatesController.LockVersion),
            typeof(HttpPostAttribute),
            "{familyId}/versions/{versionId}/lock");
        AssertRoute(
            nameof(DynamicFlowTemplatesController.ReopenVersion),
            typeof(HttpPostAttribute),
            "{familyId}/versions/{versionId}/reopen");
        AssertRoute(
            nameof(DynamicFlowTemplatesController.DiffVersions),
            typeof(HttpPostAttribute),
            "{familyId}/versions/diff");

        Require(
            GetMethod(nameof(DynamicFlowTemplatesController.SaveDraftVersionLegacy))
                .GetCustomAttribute<ObsoleteAttribute>() is not null,
            "the family-only draft-save compatibility route must be explicitly obsolete");
        Require(
            GetMethod(nameof(DynamicFlowTemplatesController.LockVersionLegacy))
                .GetCustomAttribute<ObsoleteAttribute>() is not null,
            "the family-less lock compatibility route must be explicitly obsolete");
    }

    private static void AssertMutationCommandAndCasTokens()
    {
        AssertProperties<CreateDynamicFlowTemplateRequest>("CommandId");
        AssertProperties<UpdateDynamicFlowTemplateRequest>("CommandId", "ExpectedFamilyRevision");
        AssertProperties<DeleteDynamicFlowTemplateRequest>("CommandId", "ExpectedFamilyRevision");
        AssertProperties<ArchiveDynamicFlowTemplateRequest>("CommandId", "ExpectedFamilyRevision");
        AssertProperties<CloneDynamicFlowTemplateRequest>(
            "CommandId",
            "ExpectedFamilyRevision",
            "SourceVersionId",
            "SourceDraftRevision",
            "SourcePayloadHash");
        AssertProperties<SaveDynamicFlowTemplateVersionDraftRequest>(
            "CommandId",
            "ExpectedDraftRevision",
            "ExpectedPayloadHash");
        AssertProperties<LockDynamicFlowTemplateVersionRequest>(
            "CommandId",
            "ExpectedFamilyRevision",
            "ExpectedDraftRevision",
            "ExpectedPayloadHash");
        AssertProperties<ReopenDynamicFlowTemplateVersionRequest>("CommandId", "ExpectedFamilyRevision");

        var mutations = ReadSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        Require(
            Count(mutations, "EnsureCommandId(req.CommandId)") == 8,
            "all eight P4 mutation families must require a body/header command id");
        Require(
            Count(mutations, "EnsurePositiveRevision(req.ExpectedFamilyRevision") >= 6,
            "every family-CAS mutation must validate expectedFamilyRevision");
        Require(
            Count(mutations, "EnsurePositiveRevision(req.ExpectedDraftRevision") == 2,
            "save and lock must validate expectedDraftRevision");
        Require(
            Count(mutations, "EnsurePositiveRevision(req.SourceDraftRevision") == 1,
            "clone must validate the exact sourceDraftRevision");
        Require(
            Count(mutations, "NormalizeExpectedHash(req.ExpectedPayloadHash") == 2,
            "save and lock must validate expectedPayloadHash");
        Require(
            Count(mutations, "NormalizeExpectedHash(req.SourcePayloadHash") == 1,
            "clone must validate the exact sourcePayloadHash");
        Require(
            mutations.Contains("DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT", StringComparison.Ordinal) &&
            mutations.Contains("RequestHash", StringComparison.Ordinal),
            "changed command replay must be rejected by canonical request hash");
    }

    private static void AssertClonePinsExactSourceAndReplaysBeforeSourceRevalidation()
    {
        var mutations = ReadSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        var clone = Slice(
            mutations,
            "public async Task<DynamicFlowTemplateDto> CloneAsync(",
            "public async Task<DynamicFlowTemplateVersionDto> ReopenVersionAsync(");
        var coordinator = clone.IndexOf("var result = await RunCommandAsync(", StringComparison.Ordinal);
        Require(coordinator > 0, "clone must use the idempotent command coordinator");
        Require(
            !clone[..coordinator].Contains("_ctx.DynamicFlowTemplateVersions", StringComparison.Ordinal),
            "exact clone replay must be resolved before mutable source-version state is re-read");
        foreach (var sourcePin in new[]
                 {
                     "x.Id == sourceVersionId",
                     "x.DraftRevision == req.SourceDraftRevision",
                     "x.PayloadHash == sourcePayloadHash"
                 })
        {
            Require(
                clone.Contains(sourcePin, StringComparison.Ordinal),
                $"clone transaction is missing exact source CAS predicate {sourcePin}");
        }
        Require(
            clone.IndexOf("x.DraftRevision == req.SourceDraftRevision", coordinator, StringComparison.Ordinal) <
            clone.IndexOf("DynamicFlowTemplates.InsertOneAsync", coordinator, StringComparison.Ordinal),
            "clone must recheck every source CAS pin before the first aggregate write");
        Require(
            clone.Contains("sourceVersionId,", StringComparison.Ordinal) &&
            clone.Contains("req.SourceDraftRevision,", StringComparison.Ordinal) &&
            clone.Contains("sourcePayloadHash,", StringComparison.Ordinal),
            "clone idempotency hash must bind all exact source pins");
    }

    private static void AssertReplayIsResolvedFromTamperEvidentSnapshotsBeforeMutableValidation()
    {
        var mutations = ReadSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        var cases = new[]
        {
            (
                Name: "create",
                Body: Slice(
                    mutations,
                    "private async Task<DynamicFlowTemplateDto> CreateDefinitionAsync(",
                    "private async Task<DynamicFlowTemplateDto> UpdateDefinitionMetadataAsync("),
                SensitiveRead: "var prepared = await PreparePayloadAsync("),
            (
                Name: "update",
                Body: Slice(
                    mutations,
                    "private async Task<DynamicFlowTemplateDto> UpdateDefinitionMetadataAsync(",
                    "public async Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync("),
                SensitiveRead: "var versionsBefore = await LoadVersionsAsync("),
            (
                Name: "save",
                Body: Slice(
                    mutations,
                    "public async Task<DynamicFlowTemplateVersionDto> SaveDraftVersionAsync(",
                    "public async Task<DynamicFlowTemplateVersionDto> LockVersionAsync("),
                SensitiveRead: "EnsureTemplateNotArchived(template);"),
            (
                Name: "lock",
                Body: Slice(
                    mutations,
                    "public async Task<DynamicFlowTemplateVersionDto> LockVersionAsync(",
                    "public async Task<DynamicFlowTemplateDto> ArchiveAsync("),
                SensitiveRead: "EnsureTemplateNotArchived(template);"),
            (
                Name: "archive",
                Body: Slice(
                    mutations,
                    "public async Task<DynamicFlowTemplateDto> ArchiveAsync(",
                    "public async Task DeleteAsync("),
                SensitiveRead: "var versionsBefore = await LoadVersionsAsync("),
            (
                Name: "delete",
                Body: Slice(
                    mutations,
                    "public async Task DeleteAsync(",
                    "public async Task<DynamicFlowTemplateDto> CloneAsync("),
                SensitiveRead: "var template = await LoadTemplateForManageAsync("),
            (
                Name: "clone",
                Body: Slice(
                    mutations,
                    "public async Task<DynamicFlowTemplateDto> CloneAsync(",
                    "public async Task<DynamicFlowTemplateVersionDto> ReopenVersionAsync("),
                SensitiveRead: "var result = await RunCommandAsync("),
            (
                Name: "reopen",
                Body: Slice(
                    mutations,
                    "public async Task<DynamicFlowTemplateVersionDto> ReopenVersionAsync(",
                    "public async Task<DiffDynamicFlowTemplateVersionsDto> DiffVersionsAsync("),
                SensitiveRead: "EnsureTemplateNotArchived(template);")
        };

        foreach (var replayCase in cases)
        {
            var receipt = replayCase.Body.IndexOf(
                "LoadMatchingReplayReceiptAsync(",
                StringComparison.Ordinal);
            var sensitiveRead = replayCase.Body.IndexOf(
                replayCase.SensitiveRead,
                StringComparison.Ordinal);
            Require(
                receipt >= 0 && sensitiveRead > receipt,
                $"{replayCase.Name} must resolve an exact receipt before mutable/domain validation");
        }

        var create = cases.Single(test => test.Name == "create").Body;
        var save = cases.Single(test => test.Name == "save").Body;
        foreach (var body in new[] { create, save })
        {
            Require(
                body.IndexOf("CanonicalRequestPayload(req.Payload)", StringComparison.Ordinal) <
                body.IndexOf("PreparePayloadAsync(", StringComparison.Ordinal),
                "create/save replay hash must canonicalize only request JSON before Form-aware preparation");
        }
        Require(
            !mutations.Contains("payloadHash = prepared.PayloadHash", StringComparison.Ordinal),
            "request replay identity must not depend on mutable server-pinned Form preparation");

        var reopen = cases.Single(test => test.Name == "reopen").Body;
        var reopenHash = Slice(
            reopen,
            "var requestHash = RequestHash(new",
            "var existingReceipt = await LoadMatchingReplayReceiptAsync(");
        Require(
            reopenHash.Contains("sourceVersionId = versionId", StringComparison.Ordinal) &&
            !reopenHash.Contains("source.PayloadHash", StringComparison.Ordinal),
            "reopen replay identity must bind the route source id, not mutable source payload state");

        var replayCoordinator = Slice(
            mutations,
            "private async Task<DynamicFlowDefinitionCommandReceipt?> LoadMatchingReplayReceiptAsync(",
            "private static void EnsureReplayMatches(");
        Require(
            replayCoordinator.Contains("LoadRequiredActorAsync(actorUserId, ct)", StringComparison.Ordinal),
            "receipt replay must recheck that the actor is still active immediately before exposure");

        var receiptModel = ReadSource("Models/DynamicFlowDefinitionCommandReceipt.cs");
        foreach (var member in new[]
                 {
                     "ResultFamilySnapshot",
                     "ResultFamilySnapshotSha256",
                     "ResultVersionSnapshot",
                     "ResultVersionSnapshotSha256"
                 })
        {
            Require(
                receiptModel.Contains(member, StringComparison.Ordinal),
                $"receipt result lost tamper-evident snapshot member {member}");
        }

        var snapshots = ReadSource("Services/DynamicFlows/DynamicFlowDefinitionReceiptSnapshots.cs");
        Require(
            snapshots.Contains("SHA256.HashData(snapshot.ToBson())", StringComparison.Ordinal) &&
            snapshots.Contains("BsonSerializer.Deserialize<DynamicFlowTemplate>", StringComparison.Ordinal) &&
            snapshots.Contains("BsonSerializer.Deserialize<DynamicFlowTemplateVersion>", StringComparison.Ordinal) &&
            snapshots.Contains("DYNAMIC_FLOW_COMMAND_RESULT_SNAPSHOT_INVALID", StringComparison.Ordinal),
            "receipt replay snapshots must be hash-verified, typed and fail with a stable conflict");
    }

    private static void AssertAggregateWritesUseOneTransactionSession()
    {
        var runner = ReadSource("Services/DynamicFlows/DynamicFlowDefinitionTransactionRunner.cs");
        Require(runner.Contains("StartSessionAsync", StringComparison.Ordinal), "transaction runner must open a Mongo session");
        Require(runner.Contains("session.StartTransaction", StringComparison.Ordinal), "transaction runner must start a transaction");
        Require(runner.Contains("operation(session, ct)", StringComparison.Ordinal), "mutation must receive the same session");
        Require(runner.Contains("CommitTransactionAsync", StringComparison.Ordinal), "transaction runner must commit explicitly");
        Require(runner.Contains("AbortTransactionAsync", StringComparison.Ordinal), "transaction runner must abort on failure");
        Require(
            runner.Contains("DYNAMIC_FLOW_TRANSACTION_UNSUPPORTED", StringComparison.Ordinal),
            "unsupported transaction topology must fail closed with a stable error");
        Require(
            DynamicFlowDefinitionTransactionRunner.IsUnsupportedTransactionFailure(
                new NotSupportedException("Standalone servers do not support transactions.")),
            "the MongoDB standalone driver failure must map to the stable unsupported-topology error");
        Require(
            !runner.Contains("operation(null", StringComparison.Ordinal) &&
            !runner.Contains("operation(default", StringComparison.Ordinal),
            "transaction runner must never fall back to a sessionless mutation");

        var mutations = ReadSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        var aggregateWrite = new Regex(
            @"_ctx\.(?:DynamicFlowTemplates|DynamicFlowTemplateVersions|DynamicFlowDefinitionCommandReceipts|UserActionLogs)\.(?:InsertOneAsync|ReplaceOneAsync|UpdateOneAsync|UpdateManyAsync|DeleteOneAsync|DeleteManyAsync)\s*\(",
            RegexOptions.CultureInvariant);
        var sessionWrite = new Regex(
            @"_ctx\.(?:DynamicFlowTemplates|DynamicFlowTemplateVersions|DynamicFlowDefinitionCommandReceipts|UserActionLogs)\.(?:InsertOneAsync|ReplaceOneAsync|UpdateOneAsync|UpdateManyAsync|DeleteOneAsync|DeleteManyAsync)\s*\(\s*session\b",
            RegexOptions.CultureInvariant);
        var allWriteCount = aggregateWrite.Matches(mutations).Count;
        Require(allWriteCount >= 16, "P4 aggregate mutation source unexpectedly lost write coverage");
        Require(
            sessionWrite.Matches(mutations).Count == allWriteCount,
            "every family/version/receipt/audit write must carry the transaction session as its first argument");
        Require(
            Count(mutations, "_transactions.ExecuteAsync(") == 1,
            "the command coordinator must enter exactly one transaction runner per first execution");
    }

    private static void AssertReceiptAndSuccessAuditShareTheTransaction()
    {
        var mutations = ReadSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        var method = Slice(
            mutations,
            "private async Task InsertReceiptAndAuditAsync(",
            "private async Task<DynamicFlowTemplate> LoadReceiptFamilyAsync(");
        Require(
            method.Contains("DynamicFlowDefinitionCommandOutcomes.Succeeded", StringComparison.Ordinal),
            "only a committed mutation may persist a success receipt");
        Require(
            method.Contains("DynamicFlowDefinitionCommandReceipts.InsertOneAsync(\r\n            session", StringComparison.Ordinal) ||
            method.Contains("DynamicFlowDefinitionCommandReceipts.InsertOneAsync(\n            session", StringComparison.Ordinal),
            "command receipt must be inserted with the transaction session");
        Require(
            method.Contains("UserActionLogs.InsertOneAsync(session, audit", StringComparison.Ordinal),
            "success audit must be inserted with the same transaction session");
        Require(
            method.Contains("DynamicFlowCommandReceiptId = receipt.Id", StringComparison.Ordinal),
            "success audit must be uniquely linked to its receipt");

        foreach (var point in DynamicFlowDefinitionFaultPoints.All)
        {
            Require(
                mutations.Contains($"DynamicFlowDefinitionFaultPoints.{ToMemberName(point)}", StringComparison.Ordinal),
                $"mutation pipeline is missing rollback fault boundary {point}");
        }
    }

    private static void AssertFaultInjectionCoversEveryAtomicWriteBoundary()
    {
        const string commandId = "p4-contract-command";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DynamicFlowDefinitionFaultInjector.CommandIdConfigurationKey] = commandId,
                [DynamicFlowDefinitionFaultInjector.PointsConfigurationKey] =
                    string.Join(',', DynamicFlowDefinitionFaultPoints.All)
            })
            .Build();
        var injector = new DynamicFlowDefinitionFaultInjector(
            new ContractHostEnvironment("Testing"),
            configuration);

        foreach (var point in DynamicFlowDefinitionFaultPoints.All.OrderBy(x => x, StringComparer.Ordinal))
        {
            injector.ThrowIfConfigured("different-command", point);
            var threw = false;
            try
            {
                injector.ThrowIfConfigured(commandId, point);
            }
            catch (DynamicFlowDefinitionInjectedFaultException error)
            {
                threw = error.Message == $"{DynamicFlowDefinitionFaultInjector.FailureMessage}:{point}";
            }
            Require(threw, $"testing fault point {point} did not throw its deterministic marker");
            injector.ThrowIfConfigured(commandId, point);
        }

        var prefixConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DynamicFlowDefinitionFaultInjector.CommandIdPrefixConfigurationKey] = "p4-fault-",
                [DynamicFlowDefinitionFaultInjector.PointsConfigurationKey] =
                    string.Join(',', DynamicFlowDefinitionFaultPoints.All)
            })
            .Build();
        var prefixInjector = new DynamicFlowDefinitionFaultInjector(
            new ContractHostEnvironment("Testing"),
            prefixConfiguration);
        const string firstPrefixCommand = "p4-fault-lock";
        const string secondPrefixCommand = "p4-fault-archive";
        const string prefixPoint = DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite;
        prefixInjector.ThrowIfConfigured("other-command", prefixPoint);
        foreach (var prefixCommand in new[] { firstPrefixCommand, secondPrefixCommand })
        {
            var threw = false;
            try
            {
                prefixInjector.ThrowIfConfigured(prefixCommand, prefixPoint);
            }
            catch (DynamicFlowDefinitionInjectedFaultException error)
            {
                threw = error.Message ==
                        $"{DynamicFlowDefinitionFaultInjector.FailureMessage}:{prefixPoint}";
            }
            Require(threw, $"testing fault prefix did not isolate command {prefixCommand}");
            prefixInjector.ThrowIfConfigured(prefixCommand, prefixPoint);
        }

        var productionInjector = new DynamicFlowDefinitionFaultInjector(
            new ContractHostEnvironment("Production"),
            prefixConfiguration);
        foreach (var point in DynamicFlowDefinitionFaultPoints.All)
            productionInjector.ThrowIfConfigured(firstPrefixCommand, point);
    }

    private static void AssertFaultInjectionOrderPerMutation()
    {
        var mutations = ReadSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        AssertFaultHookOrder(
            Slice(
                mutations,
                "private async Task<DynamicFlowTemplateDto> CreateDefinitionAsync(",
                "private async Task<DynamicFlowTemplateDto> UpdateDefinitionMetadataAsync("),
            "create",
            "BeforeFamilyWrite",
            "AfterFamilyWrite",
            "BeforeVersionWrite",
            "AfterVersionWrite",
            "InsertReceiptAndAuditAsync");
        AssertFaultHookOrder(
            Slice(
                mutations,
                "public async Task<DynamicFlowTemplateVersionDto> LockVersionAsync(",
                "public async Task<DynamicFlowTemplateDto> ArchiveAsync("),
            "lock",
            "BeforeVersionWrite",
            "AfterVersionWrite",
            "BeforeFamilyWrite",
            "AfterFamilyWrite",
            "InsertReceiptAndAuditAsync");
        var archive = Slice(
            mutations,
            "public async Task<DynamicFlowTemplateDto> ArchiveAsync(",
            "public async Task DeleteAsync(");
        AssertFaultHookOrder(
            archive,
            "archive",
            "BeforeFamilyWrite",
            "AfterFamilyWrite",
            "InsertReceiptAndAuditAsync");
        Require(
            !archive.Contains("DynamicFlowDefinitionFaultPoints.BeforeVersionWrite", StringComparison.Ordinal) &&
            !archive.Contains("DynamicFlowDefinitionFaultPoints.AfterVersionWrite", StringComparison.Ordinal),
            "archive must not advertise inapplicable version-write fault boundaries");
        AssertFaultHookOrder(
            Slice(
                mutations,
                "public async Task DeleteAsync(",
                "public async Task<DynamicFlowTemplateDto> CloneAsync("),
            "delete",
            "BeforeFamilyWrite",
            "AfterFamilyWrite",
            "BeforeVersionWrite",
            "AfterVersionWrite",
            "InsertReceiptAndAuditAsync");
        AssertFaultHookOrder(
            Slice(
                mutations,
                "public async Task<DynamicFlowTemplateDto> CloneAsync(",
                "public async Task<DynamicFlowTemplateVersionDto> ReopenVersionAsync("),
            "clone",
            "BeforeFamilyWrite",
            "AfterFamilyWrite",
            "BeforeVersionWrite",
            "AfterVersionWrite",
            "InsertReceiptAndAuditAsync");
        AssertFaultHookOrder(
            Slice(
                mutations,
                "public async Task<DynamicFlowTemplateVersionDto> ReopenVersionAsync(",
                "public async Task<DiffDynamicFlowTemplateVersionsDto> DiffVersionsAsync("),
            "reopen",
            "BeforeVersionWrite",
            "AfterVersionWrite",
            "BeforeFamilyWrite",
            "AfterFamilyWrite",
            "InsertReceiptAndAuditAsync");
        AssertFaultHookOrder(
            Slice(
                mutations,
                "private async Task InsertReceiptAndAuditAsync(",
                "private async Task<DynamicFlowTemplate> LoadReceiptFamilyAsync("),
            "receipt/audit",
            "BeforeReceiptWrite",
            "AfterReceiptWrite",
            "BeforeAuditWrite",
            "AfterAuditWrite");
    }

    private static void AssertRuntimeAndMappingExecutionStopBeforeWriters()
    {
        var runtime = Slice(
            ReadSource("Services/DynamicFlows/DynamicFlowRuntimeService.cs"),
            "public async Task<DynamicFlowInstanceLaunchResponse> CreateInstanceAsync(",
            "public Task<DynamicFlowBranchActionResponse> RollbackBranchAsync(");
        var runtimeBlock = runtime.IndexOf("throw ExecutionBlocked(version);", StringComparison.Ordinal);
        Require(runtimeBlock > 0, "P4 runtime launch must throw the stable target-phase blocker");
        var beforeRuntimeBlock = runtime[..runtimeBlock];
        Require(
            !beforeRuntimeBlock.Contains("_assignments.CreateAsync", StringComparison.Ordinal) &&
            !beforeRuntimeBlock.Contains("WriteFlowEventAsync", StringComparison.Ordinal) &&
            !beforeRuntimeBlock.Contains("RebuildAssignmentAsync", StringComparison.Ordinal),
            "runtime blocker must execute before assignment/event/projection writers");
        Require(
            beforeRuntimeBlock.Contains("BuildAssignmentParticipantFilter", StringComparison.Ordinal),
            "executeGrant must be server-derived before returning the blocked eligibility envelope");

        var reports = ReadSource("Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var resolver = Slice(
            reports,
            "private async Task<DynamicFlowMappingRuntimeContext> ResolveDynamicFlowMappingRuntimeAsync(",
            "private static void EnsureDynamicFlowMappingRequestDoesNotOverrideConfig(");
        var mappingBlock = resolver.IndexOf("DYNAMIC_FLOW_MAPPING_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE", StringComparison.Ordinal);
        var rulesRead = resolver.IndexOf("DynamicFlowMappingEngine.ReadRulesFromPayloadJson", StringComparison.Ordinal);
        Require(mappingBlock >= 0 && rulesRead > mappingBlock, "P7 blocker must fire before mapping rules are interpreted");
        var beforeMappingBlock = resolver[..mappingBlock];
        Require(
            beforeMappingBlock.Contains("CanExecuteP7MappingPin", StringComparison.Ordinal) &&
            beforeMappingBlock.Contains("instance.CatalogVersion", StringComparison.Ordinal) &&
            beforeMappingBlock.Contains("instance.CatalogSemanticHash", StringComparison.Ordinal),
            "P7 blocker must remain exact-catalog gated so legacy or rolled-back catalogs stop before rule interpretation");

        var apply = Slice(
            reports,
            "public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(",
            "private async Task<DynamicFlowMappingProjectionResult> BuildDynamicFlowMappingProjectionAsync(");
        var earlyMappingBlock = apply.IndexOf(
            "var mappingRuntime = await ResolveDynamicFlowMappingRuntimeAsync",
            StringComparison.Ordinal);
        var payloadCommandResolution = apply.IndexOf(
            "var payloadCommand = ResolvePayloadMutationCommand",
            StringComparison.Ordinal);
        var payloadHydration = apply.IndexOf(
            "await HydrateReportPayloadAsync",
            StringComparison.Ordinal);
        Require(
            earlyMappingBlock >= 0 &&
            (payloadCommandResolution < 0 ||
             payloadCommandResolution > earlyMappingBlock) &&
            (payloadHydration < 0 ||
             payloadHydration > earlyMappingBlock),
            "P7 blocker must run before payload CAS resolution or hydration can observe an in-flight manual save");
        Require(
            apply.Contains("mappingRuntime,", StringComparison.Ordinal),
            "mapping apply must reuse the exact runtime snapshot resolved by the early catalog barrier");
        Require(
            apply.IndexOf("BuildDynamicFlowMappingProjectionAsync", StringComparison.Ordinal) <
            apply.IndexOf("_payloadWriter.SaveReportPayloadAsync", StringComparison.Ordinal),
            "P7 projection/blocker must run before the first mapping payload writer");
    }

    private static void AssertPermissionMetadataIsServerDerivedAndFailClosed()
    {
        AssertProperties<DynamicFlowTemplateDto>(
            "CanRead",
            "CanManage",
            "ExecuteGrant",
            "DefinitionLockable",
            "ExecutionEligibility",
            "ExecutionBlockedReason",
            "BlockedUntilPhase",
            "CanExecute");
        AssertProperties<DynamicFlowTemplateVersionDto>(
            "CanRead",
            "CanManage",
            "ExecuteGrant",
            "DefinitionLockable",
            "ExecutionEligibility",
            "ExecutionBlockedReason",
            "BlockedUntilPhase",
            "CanExecute");

        var mutations = ReadSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        var permissions = Slice(
            mutations,
            "private async Task ApplyTemplatePermissionsAsync(",
            "private static string EnsureCommandId(");
        Require(
            permissions.Contains("LoadExecuteGrantVersionNosAsync(", StringComparison.Ordinal) &&
            Count(permissions, "executeGrantVersionNos.Contains(") >= 3,
            "family and exact-version executeGrant flags must come from one server assignment projection");
        Require(
            Count(permissions, "CanExecute = false") >= 3,
            "every family/version permission projection must remain non-executable in P4");
        Require(
            permissions.Contains("BlockedUntilTargetPhase", StringComparison.Ordinal) &&
            permissions.Contains("TargetPhaseNotImplemented", StringComparison.Ordinal),
            "permission projection must expose the stable blocked eligibility metadata");
    }

    private static void AssertActiveActorIsCheckedBeforeWritesAndPostCommitProjectionIsPure()
    {
        var mutations = ReadSource("Services/DynamicFlows/DynamicFlowTemplateDefinitionMutations.cs");
        var coordinator = Slice(
            mutations,
            "private async Task<TResult> RunCommandAsync<TResult>(",
            "private async Task<DynamicFlowDefinitionCommandReceipt?> LoadReceiptAsync(");
        var activeActorCheck = coordinator.IndexOf(
            ".Find(session, user => user.Id == actorUserId && !user.IsDeleted)",
            StringComparison.Ordinal);
        var operation = coordinator.IndexOf(
            "return await operation(session, transactionCt);",
            StringComparison.Ordinal);
        Require(
            activeActorCheck >= 0 && operation > activeActorCheck,
            "every first execution must revalidate the active actor inside the transaction before mutation writes");

        Require(
            !mutations.Contains("return await GetAsync(result.Id, actorUserId, ct)", StringComparison.Ordinal),
            "committed update/archive responses must not depend on a fallible follow-up read");
        Require(
            Count(mutations, "await ApplyTemplatePermissionsAsync(dto, result") == 0 &&
            Count(mutations, "await ApplyVersionPermissionsAsync(dto, template, result") == 0,
            "committed responses must use the preloaded actor/grant snapshot and pure permission projection");
    }

    private static void AssertRoute(string methodName, Type attributeType, string? expectedTemplate)
    {
        var method = GetMethod(methodName);
        var attributes = method.GetCustomAttributes(attributeType, inherit: true)
            .Cast<HttpMethodAttribute>()
            .ToArray();
        Require(attributes.Length == 1, $"{methodName} must declare exactly one canonical HTTP route");
        Require(
            string.Equals(attributes[0].Template, expectedTemplate, StringComparison.Ordinal),
            $"{methodName} route expected '{expectedTemplate ?? "<root>"}', got '{attributes[0].Template ?? "<root>"}'");
    }

    private static MethodInfo GetMethod(string name)
        => typeof(DynamicFlowTemplatesController).GetMethod(name, BindingFlags.Instance | BindingFlags.Public)
           ?? throw new MissingMethodException(typeof(DynamicFlowTemplatesController).FullName, name);

    private static void AssertProperties<T>(params string[] properties)
    {
        foreach (var property in properties)
        {
            Require(
                typeof(T).GetProperty(property, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"{typeof(T).Name} must expose {property}");
        }
    }

    private static void AssertFaultHookOrder(
        string source,
        string mutation,
        params string[] orderedMembers)
    {
        var offset = 0;
        foreach (var member in orderedMembers)
        {
            var needle = string.Equals(member, "InsertReceiptAndAuditAsync", StringComparison.Ordinal)
                ? member
                : $"DynamicFlowDefinitionFaultPoints.{member}";
            var index = source.IndexOf(needle, offset, StringComparison.Ordinal);
            Require(index >= offset, $"{mutation} fault pipeline lost or reordered {member}");
            offset = index + needle.Length;
        }
    }

    private static string ToMemberName(string point)
        => string.Concat(point
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

    private static int Count(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex + Math.Max(start.Length, 1), StringComparison.Ordinal);
        Require(startIndex >= 0 && endIndex > startIndex, $"source slice not found: {start} .. {end}");
        return source[startIndex..endIndex];
    }

    private static string ReadSource(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (File.Exists(direct))
                return File.ReadAllText(direct);

            var nested = Path.Combine(directory.FullName, "tdtd-be", relative);
            if (File.Exists(nested))
                return File.ReadAllText(nested);
        }
        throw new FileNotFoundException($"Unable to locate backend source '{relativePath}'.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ContractHostEnvironment : IWebHostEnvironment
    {
        public ContractHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string ApplicationName { get; set; } = "tdtd-be.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
