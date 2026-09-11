using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class DynamicFlowMappingApplyDurabilityContractTests
{
    public static void Run()
    {
        ApplyAuthorizesBeforeReceiptResolutionAndReturnsGenericDenials();
        ApplyResolvesExactReceiptBeforeMutableValidation();
        ExactReplayReturnsReceiptBoundMetadataWithoutRawSnapshotData();
        BlockingConflictErrorsNeverSerializeProjectionChanges();
        ApplyUsesOneMandatoryTransactionAndSessionAwareWriters();
        HeaderCasBindsPayloadLifecycleAndUnclaimedMappingState();
        DurableWriteSetAndTamperGuardAreAtomic();
        ReconcileConsumesOnlyTheImmutableCommittedIntent();
        ProjectorCheckpointsAreDeterministicAndFenced();
        PersistenceCollectionsAndUniqueIndexesAreFrozen();
        SnapshotsAndIntentHashesSerializeDeterministically();
        CanonicalSourceAndCallerRedactionContractsAreOnTheRootPath();
        AuthorizationScopeIsTokenAndReceiptBound();
        P707PreflightValidatesParityAndTokenBeforeTheP708WriterBarrier();
    }

    private static void ApplyAuthorizesBeforeReceiptResolutionAndReturnsGenericDenials()
    {
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = Slice(
            service,
            "public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(",
            "private async Task<(WorkAssignmentReport entity,");

        AssertBefore(
            apply,
            "EnsureDynamicFlowMappingApplyEnabled();",
            "LoadDynamicFlowMappingAuthorizedTargetAsync(",
            "the stage blocker must remain ahead of all apply target reads");
        AssertBefore(
            apply,
            "EnsureDynamicFlowMappingRequestDoesNotOverrideConfig(req);",
            "LoadDynamicFlowMappingAuthorizedTargetAsync(",
            "caller-owned mapping configuration must fail before target reads");
        AssertBefore(
            apply,
            "LoadDynamicFlowMappingAuthorizedTargetAsync(",
            "LoadDynamicFlowMappingReceiptAsync(",
            "target authorization and non-enumeration must precede receipt lookup");
        AssertNotContains(
            apply,
            "throw ReportNotFound(id);",
            "enabled mapping apply must not distinguish a missing target from a hidden target");

        var authorization = Slice(
            service,
            "private async Task<(WorkAssignmentReport entity, (WorkAssignment assignment, bool isOwner, bool isAssignee) reportAccess)>",
            "private async Task<WorkReportPeriod?>");
        AssertContains(
            authorization,
            "if (entity is null)",
            "authorization must handle a missing target");
        AssertContains(
            authorization,
            "throw DynamicFlowMappingTargetAccessForbidden();",
            "missing and denied targets must share the generic forbidden contract");
        AssertContains(
            authorization,
            "await EnsureReportAccessAsync(",
            "receipt access must be backed by the canonical report ACL");
        AssertContains(
            authorization,
            "!reportAccess.isAssignee || entity.AssigneeUserId != actorUserId",
            "mapping receipt access must remain bound to the target assignee");
    }

    private static void
        P707PreflightValidatesParityAndTokenBeforeTheP708WriterBarrier()
    {
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = Slice(
            service,
            "public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(",
            "private async Task<(WorkAssignmentReport entity,");

        AssertBefore(
            apply,
            "EnsureDynamicFlowMappingPreviewParity(",
            "DynamicFlowMappingSecurityContract.ValidatePreviewToken(",
            "apply must compare the server-recomputed semantic result before accepting the token");
        AssertBefore(
            apply,
            "DynamicFlowMappingSecurityContract.ValidatePreviewToken(",
            "EnsureDynamicFlowMappingPersistenceEnabled();",
            "P7-07 must validate the signed actor/target/epoch/snapshot binding before the P7-08 barrier");
        AssertBefore(
            apply,
            "EnsureDynamicFlowMappingPersistenceEnabled();",
            "_dynamicFlowTransactions.ExecuteAsync(",
            "no P7-08 transaction or durable writer may run during the P7-07 preflight");

        var activation = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingActivation.cs");
        AssertContains(
            activation,
            "CanPreflightApply:",
            "activation must expose the P7-07 preflight slice independently");
        AssertContains(
            activation,
            "activationThrough >= DynamicFlowMappingPreviewTokenPhase",
            "P7-07 preflight must activate with preview-token issuance");
        AssertContains(
            activation,
            "private void EnsureDynamicFlowMappingPersistenceEnabled()",
            "durable apply must retain its independent P7-08 barrier");
    }

    private static void BlockingConflictErrorsNeverSerializeProjectionChanges()
    {
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = Slice(
            service,
            "public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(",
            "private async Task<(WorkAssignmentReport entity,");
        AssertContains(
            apply,
            "throw DynamicFlowMappingBlockingConflict();",
            "blocking apply conflicts must use the safe error contract");
        AssertNotContains(
            apply,
            "conflicts = projection.Changes",
            "blocking conflict errors must never serialize raw projection changes");

        var safeError = Slice(
            service,
            "internal static AppException DynamicFlowMappingBlockingConflict()",
            "private static void EnsureDynamicFlowMappingRequestDoesNotOverrideConfig(");
        AssertContains(
            safeError,
            "reason = \"DYNAMIC_FLOW_MAPPING_CONFLICT\"",
            "blocking conflicts must retain a stable machine reason");
        AssertContains(
            safeError,
            "hasBlockingConflicts = true",
            "blocking conflicts must retain a safe boolean summary");
        foreach (var forbidden in new[]
                 {
                     "projection",
                     "Changes",
                     "Sources",
                     "ValueJson",
                     "SourceReportId",
                     "reportId",
                     "workAssignmentId",
                     "actorUserId"
                 })
        {
            AssertNotContains(
                safeError,
                forbidden,
                $"blocking conflict details must not expose {forbidden}");
        }
    }

    private static void ApplyResolvesExactReceiptBeforeMutableValidation()
    {
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = Slice(
            service,
            "public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(",
            "private async Task<(WorkAssignmentReport entity,");

        AssertBefore(
            apply,
            "ResolveDynamicFlowMappingCommandId(",
            "LoadDynamicFlowMappingReceiptAsync(",
            "receipt lookup must require only the stable command id");
        AssertBefore(
            apply,
            "ComputeDynamicFlowMappingApplyRequestHash(",
            "LoadDynamicFlowMappingReceiptAsync(",
            "canonical request hash must be available before receipt resolution");
        AssertBefore(
            apply,
            "ValidateDynamicFlowMappingReceiptReplayRequest(",
            "ResolveDynamicFlowMappingApplyCommand(",
            "changed receipt replays must conflict before mutable payload validation");
        AssertBefore(
            apply,
            "LoadDynamicFlowMappingReceiptAsync(",
            "ValidateDynamicFlowMappingDraftTargetAsync(",
            "exact receipt replay must precede draft/lifecycle validation");
        AssertBefore(
            apply,
            "LoadDynamicFlowMappingReceiptAsync(",
            "ResolveDynamicFlowMappingRuntimeAsync(",
            "exact receipt replay must precede mutable runtime revalidation");
        AssertBefore(
            apply,
            "ValidateDynamicFlowMappingReceiptReplay(",
            "DynamicFlowMappingSecurityContract.ValidatePreviewToken(",
            "exact committed replay must not be rejected by token expiry");
        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        AssertContains(
            persistence,
            "DYNAMIC_FLOW_MAPPING_COMMAND_REPLAY_MISMATCH",
            "changed command replay must use the frozen mapping mismatch contract");
        AssertNotContains(
            apply,
            "ReservePayloadMutationCommandAsync(",
            "mapping apply must not expose a non-transactional reservation window");

        var replayResponse = Slice(
            persistence,
            "private async Task<WorkAssignmentReportResponse>\n        MapDynamicFlowMappingReceiptReplayResponseAsync(",
            "private static void EnsureDynamicFlowMappingExpectedTarget(");
        AssertBefore(
            replayResponse,
            "MapToResponseAsync(",
            "BindDynamicFlowMappingReceiptReplayMetadata(",
            "receipt metadata must be applied only after current ACL/redaction mapping");
        AssertContains(
            replayResponse,
            "response.DynamicFlowMappingApplyState",
            "exact replay must require an observable durable apply state");
        AssertContains(
            replayResponse,
            "response.DynamicFlowMappingReceiptId",
            "exact replay must require an observable canonical receipt id");
    }

    private static void
        ExactReplayReturnsReceiptBoundMetadataWithoutRawSnapshotData()
    {
        var reportId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var receiptId = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var receipt = new DynamicFlowMappingApplyReceipt
        {
            Id = receiptId,
            TargetReportId = reportId,
            CommandId = "mapping-command-replay-001",
            State = DynamicFlowMappingApplyStates.Committed,
            ResultPayloadRevision = 2,
            ResultPayloadHash = Hash('a'),
            ResultLifecycleRevision = 1,
            ResultSemanticHash = Hash('b'),
            ResultSnapshot = new BsonDocument
            {
                { "hiddenSourceValue", "must-not-be-exposed" },
                { "rawFieldValue", "must-not-be-exposed-either" }
            }
        };
        var response = new WorkAssignmentReportResponse
        {
            Id = reportId,
            PayloadRevision = 9,
            PayloadHash = Hash('c'),
            LifecycleRevision = 4,
            Values1DJson = "[]",
            FieldValuesJson = "{\"visible\":\"redacted-view\"}",
            TableValuesJson = "{}",
            SummarySourceJson = "{\"kind\":\"dynamic-flow-mapping\"}",
            DynamicFlowMappingReceiptId = receiptId,
            DynamicFlowMappingCommandId = receipt.CommandId,
            DynamicFlowMappingResultSemanticHash =
                receipt.ResultSemanticHash,
            DynamicFlowMappingApplyState =
                DynamicFlowMappingApplyStates.Reconciled
        };

        var bound =
            WorkAssignmentReportService
                .BindDynamicFlowMappingReceiptReplayMetadata(
                    response,
                    receipt);
        Require(
            ReferenceEquals(bound, response) &&
            bound.Id == reportId &&
            bound.PayloadRevision == receipt.ResultPayloadRevision &&
            bound.PayloadHash == receipt.ResultPayloadHash &&
            bound.LifecycleRevision ==
            receipt.ResultLifecycleRevision,
            "exact replay must restore the immutable receipt result metadata");
        Require(
            bound.DynamicFlowMappingReceiptId == receipt.Id &&
            bound.DynamicFlowMappingCommandId == receipt.CommandId &&
            bound.DynamicFlowMappingResultSemanticHash ==
            receipt.ResultSemanticHash &&
            bound.DynamicFlowMappingApplyState ==
            DynamicFlowMappingApplyStates.Reconciled,
            "exact replay must expose the canonical receipt and current durable apply state");
        Require(
            bound.Values1DJson == "[]" &&
            bound.FieldValuesJson ==
            "{\"visible\":\"redacted-view\"}" &&
            bound.TableValuesJson == "{}" &&
            !System.Text.Json.JsonSerializer.Serialize(bound).Contains(
                "must-not-be-exposed",
                StringComparison.Ordinal),
            "receipt replay must retain the ACL-filtered response without copying raw receipt snapshot data");

        var missingState = new WorkAssignmentReportResponse
        {
            Id = reportId,
            DynamicFlowMappingReceiptId = receiptId,
            DynamicFlowMappingCommandId = receipt.CommandId,
            DynamicFlowMappingResultSemanticHash =
                receipt.ResultSemanticHash
        };
        var missingStateRejected = false;
        try
        {
            WorkAssignmentReportService
                .BindDynamicFlowMappingReceiptReplayMetadata(
                    missingState,
                    receipt);
        }
        catch (AppException error) when (
            error.Code ==
            AppErrorCode.DYNAMIC_FLOW_MAPPING_COMMAND_REPLAY_MISMATCH)
        {
            missingStateRejected = true;
        }
        Require(
            missingStateRejected,
            "exact replay must fail closed when apply-state response binding is absent");

        var missingReceipt = new WorkAssignmentReportResponse
        {
            Id = reportId,
            DynamicFlowMappingCommandId = receipt.CommandId,
            DynamicFlowMappingResultSemanticHash =
                receipt.ResultSemanticHash,
            DynamicFlowMappingApplyState =
                DynamicFlowMappingApplyStates.Reconciled
        };
        var missingReceiptRejected = false;
        try
        {
            WorkAssignmentReportService
                .BindDynamicFlowMappingReceiptReplayMetadata(
                    missingReceipt,
                    receipt);
        }
        catch (AppException error) when (
            error.Code ==
            AppErrorCode.DYNAMIC_FLOW_MAPPING_COMMAND_REPLAY_MISMATCH)
        {
            missingReceiptRejected = true;
        }
        Require(
            missingReceiptRejected,
            "exact replay must fail closed when receipt-id response binding is absent");
    }

    private static void ApplyUsesOneMandatoryTransactionAndSessionAwareWriters()
    {
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = Slice(
            service,
            "public async Task<WorkAssignmentReportResponse> ApplyDynamicFlowMappingAsync(",
            "private async Task<(WorkAssignmentReport entity,");
        AssertContains(
            apply,
            "_dynamicFlowTransactions.ExecuteAsync(",
            "mapping apply must use the mandatory Mongo transaction runner");
        AssertContains(
            apply,
            "DYNAMIC_FLOW_MAPPING_TRANSACTION_REQUIRED",
            "unsupported transaction topology must fail with the mapping error");
        foreach (var sessionWrite in new[]
                 {
                     "_payloadWriter.SaveReportPayloadAsync(",
                     "_sectionProjection.ProjectAndVerifyAsync(",
                     "_ctx.DynamicFlowMappingApplyReceipts.InsertOneAsync(",
                     "_ctx.DynamicFlowMappingProvenanceRecords.InsertOneAsync(",
                     "_ctx.DynamicFlowMappingEvents.InsertOneAsync(",
                     "_ctx.DynamicFlowMappingOutbox.InsertOneAsync(",
                     "_ctx.WorkAssignmentReportLogs.InsertOneAsync("
                 })
        {
            AssertContains(
                apply,
                sessionWrite,
                $"transaction lost write {sessionWrite}");
        }
        Require(
            Count(apply, "session,") >= 10,
            "payload/header/period/section/receipt/provenance/event/outbox/audit writes must share the session");

        var writer = ReadBackendSource(
            "Services/WorkAssignmentReports/Payloads/WorkReportPayloadService.cs");
        var writerBody = Slice(
            writer,
            "public async Task<WorkReportPayloadWriteResult> SaveReportPayloadAsync(",
            "public static WorkReportPayloadWriteResult PreflightReportPayload(");
        AssertContains(
            writerBody,
            "IClientSessionHandle? session = null",
            "payload writer must accept the transaction session");
        AssertContains(
            writerBody,
            ".Find(session, payloadFilter)",
            "payload writer reads must join the transaction");
        var sessionRootWrite = Slice(
            writerBody,
            "else\n        {\n            await _ctx.WorkReportPayloads.ReplaceOneAsync(",
            "\n        }\n\n        await SaveTableBlocksAsync(");
        AssertContains(
            sessionRootWrite,
            "session,",
            "payload root write must join the transaction");

        var sections = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkAssignmentReportSectionProjectionService.cs");
        AssertContains(
            sections,
            "IClientSessionHandle? session = null",
            "section projection must accept the transaction session");
        AssertContains(
            sections,
            ".Find(session, sectionFilter)",
            "section verification reads must join the transaction");
    }

    private static void HeaderCasBindsPayloadLifecycleAndUnclaimedMappingState()
    {
        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        var filter = Slice(
            persistence,
            "private static FilterDefinition<WorkAssignmentReport>\n        BuildDynamicFlowMappingCommitFilter(",
            "private static UpdateDefinition<WorkAssignmentReport>\n        ApplyDynamicFlowMappingHeaderCommit(");
        foreach (var predicate in new[]
                 {
                     "item => item.PayloadRevision",
                     "item => item.PayloadHash",
                     "item => item.LifecycleRevision",
                     "item => item.Status",
                     "item => item.IsActive",
                     "item => item.IsCurrent",
                     "item => item.PayloadMutationCommandId",
                     "item => item.DynamicFlowMappingReceiptId",
                     "item => item.DynamicFlowMappingProvenanceId"
                 })
        {
            AssertContains(
                filter,
                predicate,
                $"target CAS lost predicate {predicate}");
        }

        var boundary = Slice(
            persistence,
            "private async Task<WorkAssignmentReport>\n        RevalidateDynamicFlowMappingWriteBoundaryAsync(",
            "private async Task EnsureDynamicFlowMappingMutationScopeOpenAsync(");
        AssertContains(
            boundary,
            "DynamicFlowMappingCanonicalSourceContract.Validate(",
            "write boundary must rerun the canonical source contract");
        AssertContains(
            boundary,
            "ComputeSourceSignature(",
            "write boundary must recompute the exact source signature");
        AssertContains(
            boundary,
            "ComputeDynamicFlowMappingAuthorizationScopeHash(",
            "write boundary must recompute effective authorization scope");
        AssertBefore(
            boundary,
            "EnsureDynamicFlowMappingExpectedTarget(",
            "return report;",
            "target pins must be rechecked before the transaction can write");
    }

    private static void DurableWriteSetAndTamperGuardAreAtomic()
    {
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var save = Slice(
            service,
            "public async Task<WorkAssignmentReportResponse> SaveDraftAsync(",
            "public async Task<WorkAssignmentReportResponse> SaveDraftPatchAsync(");
        AssertBefore(
            save,
            "EnsureDynamicFlowMappingProvenanceIntegrityAsync(entity, ct);",
            "ResolvePayloadMutationCommand(",
            "manual save must fail closed before claiming or writing payload");
        AssertBefore(
            save,
            "EnsureDynamicFlowMappingProvenanceOverrideAllowed(",
            "ReservePayloadMutationCommandAsync(",
            "manual provenance removal or forgery must fail before reservation");

        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        var guard = Slice(
            persistence,
            "private async Task EnsureDynamicFlowMappingProvenanceIntegrityAsync(",
            "private static string ComputeDynamicFlowMappingSourceFactHash(");
        foreach (var durableMember in new[]
                 {
                     "DynamicFlowMappingApplyReceipts",
                     "DynamicFlowMappingProvenanceRecords",
                     "DynamicFlowMappingEvents",
                     "DynamicFlowMappingOutbox",
                     "WorkReportPayloads",
                     "receipt.ResultSnapshotHash",
                     "provenance.ProvenanceHash",
                     "mappingEvent.PayloadHash",
                     "outbox.IntentHash",
                     "receipt.WriteSetHash"
                 })
        {
            AssertContains(
                guard,
                durableMember,
                $"tamper guard lost {durableMember}");
        }
        AssertContains(
            persistence,
            "DYNAMIC_FLOW_MAPPING_PROVENANCE_TAMPERED",
            "tamper guard must fail with the stable mapping code");
        AssertContains(
            persistence,
            "DYNAMIC_FLOW_MAPPING_MANUAL_PROVENANCE_OVERRIDE",
            "manual mapping-owned metadata mutation must use a stable tamper reason");
    }

    private static void ReconcileConsumesOnlyTheImmutableCommittedIntent()
    {
        var reconciler = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/DynamicFlowMappingOutboxReconciler.cs");
        var reconcile = Slice(
            reconciler,
            "public async Task<bool> ProcessByIdAsync(",
            "public async Task<DynamicFlowMappingApplyResult?> GetApplyResultAsync(");
        foreach (var exactPin in new[]
                 {
                     "LoadAndValidateIntentAsync(claimed, ct)",
                     "claimed.Intent.TargetAssignmentId",
                     "reconcile.ActorUserId",
                     "RenewLeaseAsync(claimed, ct)",
                     "CompleteReconciledAsync(claimed, ct)"
                 })
        {
            AssertContains(
                reconcile,
                exactPin,
                $"reconcile lost immutable pin {exactPin}");
        }
        foreach (var forbidden in new[]
                 {
                     "DynamicFlowMappingEngine.",
                     "_dynamicFlowPolicyEvaluator",
                     "ResolveCanonicalDynamicFlowMappingSourceReportsAsync",
                     "ResolveDynamicFlowMappingRuntimeAsync"
                 })
        {
            AssertNotContains(
                reconcile,
                forbidden,
                $"reconcile must never re-evaluate mutable input {forbidden}");
        }
        AssertContains(
            reconcile,
            "CompletePartialAsync(",
            "projector failure must enter the durable retry path");
        AssertContains(
            reconciler,
            "Task<int> ProcessPendingAsync(",
            "the reconciler must expose a leased recurring-worker path");
        AssertContains(
            reconciler,
            "Task<bool> ProcessByIdAsync(",
            "foreground apply must share the exact worker path");
        AssertContains(
            reconciler,
            "GetApplyResultAsync(",
            "API response decoration must read immutable receipt identity and current apply state");

        var envelope = Slice(
            reconciler,
            "private static void ValidateImmutableEnvelope(",
            "private static FilterDefinition<DynamicFlowMappingOutboxItem>");
        foreach (var immutableBinding in new[]
                 {
                     "intent.ActorUserId",
                     "outbox.IntentHash",
                     "ComputeIntentHash(intent)",
                     "intent.ProjectionSnapshotHash",
                     "intent.AuditSnapshotHash"
                 })
        {
            AssertContains(
                envelope,
                immutableBinding,
                $"immutable reconcile envelope lost {immutableBinding}");
        }

        var fence = Slice(
            reconciler,
            "BuildFence(DynamicFlowMappingOutboxItem claimed)",
            "BuildReceiptBinding(DynamicFlowMappingOutboxItem outbox)");
        foreach (var fencedMember in new[]
                 {
                     "item.LeaseId",
                     "item.RepairEpoch",
                     "item.IntentHash",
                     "DynamicFlowMappingOutboxStates.Processing"
                 })
        {
            AssertContains(
                fence,
                fencedMember,
                $"lease CAS lost {fencedMember}");
        }

        var terminal = Slice(
            reconciler,
            "private async Task<bool> CompleteReconciledAsync(",
            "private async Task CompletePartialAsync(");
        AssertContains(
            terminal,
            "_transactions.ExecuteAsync(",
            "RECONCILED outbox and receipt must share one transaction");
        AssertContains(
            terminal,
            "BuildFence(claimed, now)",
            "terminal state must retain the exact lease/epoch fence");
        AssertContains(
            terminal,
            "outboxUpdate.ModifiedCount != 1",
            "terminal state must honor the fenced outbox write result");
        AssertContains(
            terminal,
            "DynamicFlowMappingApplyStates.Reconciled",
            "terminal transaction must update the receipt");

        var partial = Slice(
            reconciler,
            "private async Task CompletePartialAsync(",
            "private async Task RepairReconciledReceiptAsync(");
        AssertContains(
            partial,
            "_transactions.ExecuteAsync(",
            "PARTIAL outbox and receipt must share one transaction");
        AssertContains(
            partial,
            "outboxUpdate.ModifiedCount != 1",
            "a stale lease holder must not downgrade durable state");
        AssertContains(
            partial,
            "DynamicFlowMappingApplyStates.Partial",
            "projector failure must remain durably retryable");
        AssertContains(
            partial,
            "LastErrorSnapshotHash",
            "projector diagnostics must stay hash-only");

        var terminalRepair = Slice(
            reconciler,
            "private async Task RepairReconciledReceiptAsync(",
            "private async Task<ReconcileContext> LoadAndValidateIntentAsync(");
        AssertContains(
            terminalRepair,
            "DynamicFlowMappingOutboxStates.Reconciled",
            "terminal split repair must prove the outbox is already RECONCILED");
        AssertContains(
            terminalRepair,
            "DynamicFlowMappingApplyStates.Reconciled",
            "terminal split repair must converge the receipt");
        AssertContains(
            terminalRepair,
            "_transactions.ExecuteAsync(",
            "terminal split repair must be transactionally fenced");

        var committedIntent = Slice(
            reconciler,
            "private async Task<ReconcileContext> LoadAndValidateIntentAsync(",
            "private static void ValidateImmutableEnvelope(");
        foreach (var exactPin in new[]
                 {
                     "intent.TargetPayloadRevision",
                     "intent.TargetPayloadHash",
                     "intent.TargetLifecycleRevision",
                     "intent.ProvenanceId",
                     "intent.ProvenanceHash",
                     "intent.ActorUserId"
                 })
        {
            AssertContains(
                committedIntent,
                exactPin,
                $"reconcile lost immutable pin {exactPin}");
        }
        AssertContains(
            committedIntent,
            "report.PayloadRevision >= intent.TargetPayloadRevision",
            "a valid newer manual-save payload must not strand committed mapping intent");
        AssertContains(
            committedIntent,
            "payload.PayloadRevision == report.PayloadRevision",
            "newer payload validation must still prove current payload/header integrity");
        AssertContains(
            committedIntent,
            "DynamicFlowMappingProvenanceStates.Invalidated",
            "a selected mapping invalidated by a concurrent source lifecycle change must still reconcile its immutable committed intent");
        AssertNotContains(
            committedIntent,
            "DynamicFlowMappingProvenanceStates.Superseded",
            "a superseded mapping must never be accepted as the selected reconcile surface");
        AssertNotContains(
            committedIntent,
            "report.PayloadRevision == intent.TargetPayloadRevision",
            "reconcile must not require the current payload to remain at the historical mapping revision");

        var faultInjector = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/DynamicFlowMappingReconcileFaultInjector.cs");
        AssertContains(
            faultInjector,
            "environment.IsEnvironment(\"Testing\")",
            "post-commit fault seams must be inert outside Testing");
        AssertContains(
            faultInjector,
            "BeforeProjectors",
            "durability probe needs a deterministic post-commit projector fault");

        var program = ReadBackendSource("Program.cs");
        AssertContains(
            program,
            "IDynamicFlowMappingOutboxReconciler",
            "the durable mapping worker must be registered in DI");
        AssertContains(
            program,
            "IDynamicFlowMappingReconcileFaultInjector",
            "the Testing-only durability seam must be registered once");
        var jobs = ReadBackendSource(
            "Jobs/HangfireRecurringJobRegistrar.cs");
        AssertContains(
            jobs,
            "dynamic-flow:mapping-outbox",
            "mapping repair must have a stable recurring job identity");
        var admin = ReadBackendSource(
            "Controllers/AdminOperationsController.cs");
        AssertContains(
            admin,
            "job-runs/dynamic-flow-mapping-outbox/process",
            "system administrators need a bounded manual repair trigger");
    }

    private static void ProjectorCheckpointsAreDeterministicAndFenced()
    {
        var outboxId =
            MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var assignmentId =
            MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var periodId =
            MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var intentHash = Hash('9');
        var first = DynamicFlowMappingProjectorContract.BuildPlan(
            outboxId,
            intentHash,
            assignmentId,
            periodId);
        var replay = DynamicFlowMappingProjectorContract.BuildPlan(
            outboxId,
            intentHash,
            assignmentId,
            periodId);
        Require(
            first.Count == 3 &&
            first.Select(item => item.Projector)
                .SequenceEqual(
                    new[]
                    {
                        DynamicFlowMappingProjectors.QueuePeriod,
                        DynamicFlowMappingProjectors.AssignmentStatus,
                        DynamicFlowMappingProjectors.DocRolePeriod
                    }) &&
            first.Select(item => item.IdempotencyKey)
                .SequenceEqual(
                    replay.Select(item => item.IdempotencyKey)) &&
            first.Select(item => item.IdempotencyKey)
                .Distinct(StringComparer.Ordinal)
                .Count() == 3,
            "mapping projectors must retain three stable, distinct committed-intent identities");
        Require(
            first.All(item =>
                item.State ==
                DynamicFlowMappingProjectorCheckpointStates.Pending &&
                item.AttemptCount == 0 &&
                item.ActiveRepairEpoch is null),
            "new projector checkpoints must start pending without an attempt fence");

        var status = first.Single(item =>
            item.Projector ==
            DynamicFlowMappingProjectors.AssignmentStatus);
        var completionAtEpochOne =
            DynamicFlowMappingProjectorContract.ComputeCompletionHash(
                intentHash,
                status,
                1);
        var replayCompletionAtEpochOne =
            DynamicFlowMappingProjectorContract.ComputeCompletionHash(
                intentHash,
                replay.Single(item =>
                    item.Projector ==
                    DynamicFlowMappingProjectors.AssignmentStatus),
                1);
        var completionAtEpochTwo =
            DynamicFlowMappingProjectorContract.ComputeCompletionHash(
                intentHash,
                status,
                2);
        Require(
            completionAtEpochOne == replayCompletionAtEpochOne &&
            completionAtEpochOne != completionAtEpochTwo,
            "projector completion must bind the immutable identity and exact repair epoch");

        var claimed = new DynamicFlowMappingOutboxItem
        {
            Id = outboxId,
            ReceiptId =
                MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
            IntentHash = intentHash,
            State = DynamicFlowMappingOutboxStates.Processing,
            LeaseId = "lease-seven",
            RepairEpoch = 7
        };
        var cutoff = new DateTime(
            2026,
            7,
            31,
            0,
            0,
            0,
            DateTimeKind.Utc);
        var fenceMethod =
            typeof(DynamicFlowMappingOutboxReconciler).GetMethod(
                "BuildFence",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static,
                binder: null,
                types:
                [
                    typeof(DynamicFlowMappingOutboxItem),
                    typeof(DateTime)
                ],
                modifiers: null)
            ?? throw new InvalidOperationException(
                "mapping lease fence builder not found");
        var leaseFence =
            fenceMethod.Invoke(null, [claimed, cutoff])
            as FilterDefinition<DynamicFlowMappingOutboxItem>
            ?? throw new InvalidOperationException(
                "mapping lease fence did not render");
        var renderedLeaseFence = leaseFence.Render(
            new RenderArgs<DynamicFlowMappingOutboxItem>(
                BsonSerializer.LookupSerializer<
                    DynamicFlowMappingOutboxItem>(),
                BsonSerializer.SerializerRegistry));
        Require(
            renderedLeaseFence["leaseId"].AsString ==
            claimed.LeaseId &&
            renderedLeaseFence["repairEpoch"].ToInt64() == 7 &&
            renderedLeaseFence["leaseUntilUtc"].AsBsonDocument
                .Contains("$gt"),
            "the real Mongo fence must reject stale repair epochs and expired leases");

        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        AssertContains(
            persistence,
            "ProjectorCheckpoints =\n                DynamicFlowMappingProjectorContract.BuildPlan(",
            "the transactional writer must persist the deterministic projector plan with the intent");

        var reconciler = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/DynamicFlowMappingOutboxReconciler.cs");
        AssertNotContains(
            reconciler,
            "receipt.OutboxIntentId != string.Empty",
            "ObjectId-backed receipt outbox ids must never be compared with an invalid empty ObjectId");
        var execute = Slice(
            reconciler,
            "private async Task ExecuteProjectorAsync(",
            "private async Task BeginProjectorAsync(");
        AssertBefore(
            execute,
            "DynamicFlowMappingProjectorCheckpointStates.Completed",
            "await projector(ct);",
            "BEFORE_FINALIZE retry must skip every already-completed projector");
        foreach (var required in new[]
                 {
                     "EnsureAndValidateProjectorPlanAsync(",
                     "ExecuteProjectorAsync(",
                     "SyncFromAssignmentIdempotentAsync(",
                     "BuildAllProjectorsCompletedFilter(claimed)"
                 })
        {
            AssertContains(
                reconciler,
                required,
                $"durable projector reconcile lost {required}");
        }

        var start = Slice(
            reconciler,
            "private async Task BeginProjectorAsync(",
            "private async Task CompleteProjectorAsync(");
        foreach (var fence in new[]
                 {
                     "BuildFence(claimed, now)",
                     "activeRepairEpoch",
                     "claimed.RepairEpoch",
                     "result.ModifiedCount != 1"
                 })
        {
            AssertContains(
                start,
                fence,
                $"projector start lost lease/epoch fence {fence}");
        }

        var complete = Slice(
            reconciler,
            "private async Task CompleteProjectorAsync(",
            "private async Task<bool> CompleteReconciledAsync(");
        foreach (var fence in new[]
                 {
                     "BuildFence(claimed, now)",
                     "BuildProjectorCompletionFence(",
                     "ComputeCompletionHash(",
                     "completedRepairEpoch",
                     "completionHash",
                     "result.ModifiedCount != 1"
                 })
        {
            AssertContains(
                complete,
                fence,
                $"projector completion lost durable fence {fence}");
        }

        var fenceBuilder = Slice(
            reconciler,
            "BuildFence(DynamicFlowMappingOutboxItem claimed)",
            "BuildReceiptBinding(DynamicFlowMappingOutboxItem outbox)");
        AssertContains(
            fenceBuilder,
            "fb.Gt(\"leaseUntilUtc\", now)",
            "an expired lease holder must fail its fence before another projector transition");

        var statusSync = ReadBackendSource(
            "Services/WorkAssignments/Runtime/WorkAssignmentStatusSyncService.cs");
        AssertContains(
            statusSync,
            "SyncFromAssignmentIdempotentAsync(",
            "mapping status projection needs a deterministic idempotent entrypoint");
        AssertContains(
            statusSync,
            "_statusLog.WriteIdempotentAsync(",
            "status-sync replay must not append a fresh operation log");
    }

    private static void PersistenceCollectionsAndUniqueIndexesAreFrozen()
    {
        Require(
            Collection<DynamicFlowMappingApplyReceipt>() ==
            "dynamic_flow_mapping_apply_receipts",
            "mapping receipt collection drift");
        Require(
            Collection<DynamicFlowMappingProvenanceRecord>() ==
            "dynamic_flow_mapping_provenance",
            "mapping provenance collection drift");
        Require(
            Collection<DynamicFlowMappingEvent>() ==
            "dynamic_flow_mapping_events",
            "mapping event collection drift");
        Require(
            Collection<DynamicFlowMappingOutboxItem>() ==
            "dynamic_flow_mapping_outbox",
            "mapping outbox collection drift");

        var indexes = ReadBackendSource(
            "Data/Indexes/MongoIndexInitializer.cs");
        foreach (var index in new[]
                 {
                     "ux_dynamicFlowMappingApplyReceipts_target_command",
                     "ux_dynamicFlowMappingProvenance_receipt",
                     "ux_dynamicFlowMappingProvenance_target_payload_revision",
                     "ux_dynamicFlowMappingEvents_event_key",
                     "ux_dynamicFlowMappingEvents_receipt_type",
                     "ux_dynamicFlowMappingOutbox_dedupe",
                     "ux_dynamicFlowMappingOutbox_receipt",
                     "ix_dynamicFlowMappingOutbox_due_lease"
                 })
        {
            AssertContains(indexes, index, $"mapping index lost {index}");
            Require(
                Count(indexes, index) == 1,
                $"mapping index {index} must be declared exactly once");
        }

        var filter = Builders<DynamicFlowMappingApplyReceipt>.Filter.And(
            Builders<DynamicFlowMappingApplyReceipt>.Filter.Eq(
                item => item.TargetReportId,
                MongoDB.Bson.ObjectId.GenerateNewId().ToString()),
            Builders<DynamicFlowMappingApplyReceipt>.Filter.Eq(
                item => item.CommandId,
                "mapping-command-001"));
        var rendered = filter.Render(
            new RenderArgs<DynamicFlowMappingApplyReceipt>(
                BsonSerializer.LookupSerializer<
                    DynamicFlowMappingApplyReceipt>(),
                BsonSerializer.SerializerRegistry));
        Require(
            rendered.Contains("targetReportId") &&
            rendered.Contains("commandId"),
            "receipt lookup grain must remain targetReportId + commandId");
    }

    private static void CanonicalSourceAndCallerRedactionContractsAreOnTheRootPath()
    {
        var service = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var sourceResolution = Slice(
            service,
            "ResolveCanonicalDynamicFlowMappingSourceReportsAsync(",
            "private static bool RuleRequiresDynamicFlowMappingSource(");
        AssertBefore(
            sourceResolution,
            "DynamicFlowMappingCanonicalSourceContract.Validate(",
            "result.Add(new DynamicFlowMappingSourceReport(",
            "canonical source validation must run before payload hydration/evaluation");
        var redaction = Slice(
            service,
            "private async Task RedactDynamicFlowMappingSourceIdentityWithoutRawAccessAsync(",
            "private async Task<string?> EnsureDynamicFlowMappingTargetTableShapeAsync(");
        AssertContains(
            redaction,
            "DynamicFlowMappingCallerRedaction.RedactSourceIdentities(",
            "caller preview must use the root redaction contract");
    }

    private static void SnapshotsAndIntentHashesSerializeDeterministically()
    {
        var oid = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
        var runtimePin = new DynamicFlowMappingRuntimePin
        {
            FlowFamilyId = oid,
            FlowVersionId = oid,
            FlowVersionNo = 1,
            FlowPayloadHash = Hash('a'),
            CatalogVersion = "1.0",
            CatalogSemanticHash = Hash('b'),
            MappingRuleSetHash = Hash('c'),
            EvaluatorVersion = "p7",
            FunctionRegistryVersion = "p7",
            FunctionRegistryHash = Hash('d'),
            FlowInstanceId = oid,
            ExecutionEpoch = 1,
            StepInstanceId = oid,
            StepId = "target",
            BranchId = oid,
            AttemptNo = 1,
            FormFamilyId = oid,
            FormVersionId = oid,
            FormVersionNo = 1,
            FormSchemaHash = Hash('e'),
            FormSnapshotHash = Hash('f')
        };
        var intent = new DynamicFlowMappingReconcileIntent
        {
            ActorUserId = oid,
            ReceiptId = oid,
            ProvenanceId = oid,
            EventId = oid,
            TargetReportId = oid,
            TargetAssignmentId = oid,
            CommandId = "mapping-command-001",
            TargetPayloadRevision = 2,
            TargetPayloadHash = Hash('1'),
            TargetLifecycleRevision = 0,
            SourceSignatureVersion = "P7-SIG-1",
            SourceSignature = Hash('2'),
            ResultSemanticHash = Hash('3'),
            MappingRuleSetHash = Hash('4'),
            ProvenanceHash = Hash('5'),
            RuntimePin = runtimePin,
            ProjectionSnapshot = new BsonDocument("payloadHash", Hash('1')),
            ProjectionSnapshotHash = Hash('6'),
            AuditSnapshot = new BsonDocument("provenanceHash", Hash('5')),
            AuditSnapshotHash = Hash('7'),
            ProjectionBusinessKeys = new List<string> { $"report:{oid}" },
            CommittedAtUtc =
                new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc)
        };
        var bson = intent.ToBsonDocument();
        Require(
            bson.Contains("actorUserId") &&
            bson.Contains("runtimePin") &&
            bson.Contains("projectionSnapshot") &&
            bson.Contains("auditSnapshot"),
            "immutable reconcile intent must BSON serialize as one typed snapshot");
        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        AssertContains(
            persistence,
            "ActorUserId = actorUserId",
            "the committed intent must own the actor used by delayed reconciliation");

        var service = typeof(WorkAssignmentReportService);
        var snapshotMethod = service.GetMethod(
                                 "ToDynamicFlowMappingSnapshot",
                                 System.Reflection.BindingFlags.NonPublic |
                                 System.Reflection.BindingFlags.Static)
                             ?? throw new InvalidOperationException(
                                 "mapping snapshot builder not found");
        var snapshot = snapshotMethod.Invoke(
            null,
            new object[]
            {
                new
                {
                    runtimePin,
                    resultSnapshot = new BsonDocument(
                        "payloadHash",
                        Hash('1'))
                }
            }) as BsonDocument;
        Require(
            snapshot is not null &&
            snapshot.Contains("runtimePin") &&
            snapshot.Contains("resultSnapshot"),
            "anonymous hash-only persistence snapshots must BSON serialize");
        AssertContains(
            persistence,
            "value.ToBsonDocument(value.GetType())",
            "snapshot serialization must use the runtime nominal type instead of ObjectSerializer");

        var hashMethod = service.GetMethod(
                             "ComputeDynamicFlowMappingIntentHash",
                             System.Reflection.BindingFlags.NonPublic |
                             System.Reflection.BindingFlags.Static)
                         ?? throw new InvalidOperationException(
                             "mapping intent hash builder not found");
        var first = (string?)hashMethod.Invoke(null, new object[] { intent });
        var second = (string?)hashMethod.Invoke(null, new object[] { intent });
        Require(
            first is { Length: 64 } &&
            first == second &&
            first.All(character =>
                character is >= '0' and <= '9' ||
                character is >= 'a' and <= 'f'),
            "immutable reconcile intent hash must be deterministic lowercase SHA-256");
    }

    private static void AuthorizationScopeIsTokenAndReceiptBound()
    {
        var security = ReadBackendSource(
            "Services/DynamicFlows/DynamicFlowMappingSecurityContract.cs");
        foreach (var tokenBinding in new[]
                 {
                     "string AuthorizationScopeHash",
                     "JsonPropertyName(\"authorizationScopeHash\")",
                     "RequireLowerSha256(claims.AuthorizationScopeHash)",
                     "claims.AuthorizationScopeHash",
                     "expected.AuthorizationScopeHash"
                 })
        {
            AssertContains(
                security,
                tokenBinding,
                $"preview token lost authorization binding {tokenBinding}");
        }
        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        AssertContains(
            persistence,
            "AuthorizationSnapshotHash = authorizationSnapshotHash",
            "receipt must bind the exact effective authorization scope");
        AssertContains(
            persistence,
            "previewClaims.AuthorizationScopeHash",
            "receipt authorization scope must equal the signed preview claim");
    }

    private static string Collection<T>()
        => typeof(T)
               .GetCustomAttributes(
                   typeof(BsonCollectionAttribute),
                   inherit: false)
               .OfType<BsonCollectionAttribute>()
               .SingleOrDefault()
               ?.Name
           ?? throw new InvalidOperationException(
               $"{typeof(T).Name} has no collection contract");

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
                var directProject = Path.Combine(
                    directory.FullName,
                    "tdtd-be.csproj");
                if (File.Exists(directProject))
                {
                    return File.ReadAllText(
                        Path.Combine(
                            directory.FullName,
                            relativePath.Replace(
                                '/',
                                Path.DirectorySeparatorChar)))
                        .Replace("\r\n", "\n", StringComparison.Ordinal);
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
                        .Replace("\r\n", "\n", StringComparison.Ordinal);
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
        var endIndex = startIndex < 0
            ? -1
            : source.IndexOf(
                end,
                startIndex + start.Length,
                StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0)
        {
            throw new InvalidOperationException(
                $"Source contract anchors were not found: {start} -> {end}");
        }
        return source[startIndex..endIndex];
    }

    private static int Count(string source, string value)
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

    private static string Hash(char value) => new(value, 64);

    private static void AssertBefore(
        string source,
        string first,
        string second,
        string context)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 ||
            secondIndex < 0 ||
            firstIndex >= secondIndex)
        {
            throw new InvalidOperationException(
                $"{context}: expected '{first}' before '{second}'.");
        }
    }

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
                $"{context}: unexpected '{forbidden}'.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
