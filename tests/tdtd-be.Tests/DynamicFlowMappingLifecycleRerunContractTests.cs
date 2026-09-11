using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class DynamicFlowMappingLifecycleRerunContractTests
{
    private sealed record ContractCase(string Id, string Semantic, Action Run);

    public static IReadOnlyList<string> RequirementRegistry { get; } =
    [
        "P7-MAP-021",
        "P7-MAP-022"
    ];

    private static readonly IReadOnlyList<ContractCase> Cases =
    [
        new(
            "MAP-RERUN-01",
            "Exact rerun retry resolves the committed successor without a second payload, row, provenance, event, audit, or outbox write",
            ExactRerunRetryResolvesBeforeMutableValidation),
        new(
            "MAP-RERUN-02",
            "Changed command replay and stale preview, source, target, or lifecycle pins fail closed with zero writes",
            ChangedReplayAndStalePinsFailClosed),
        new(
            "MAP-RERUN-03",
            "Source payload drift invalidates current dependents exactly once and requires a fresh preview before a canonical successor",
            SourcePayloadDriftBuildsDeterministicInvalidation),
        new(
            "MAP-RERUN-04",
            "Source lifecycle drift invalidates current dependents exactly once while preserving non-leaking historical audit",
            SourceLifecycleDriftBuildsDeterministicInvalidation),
        new(
            "MAP-RERUN-05",
            "Concurrent reruns use target and predecessor CAS so exactly one canonical successor wins without duplicate ledger rows",
            ConcurrentRerunsHaveOneCanonicalSuccessor),
        new(
            "MAP-RERUN-06",
            "Submit, approve, auto-approve, return, recall, and withdraw bind the exact mapped payload and tamper-proof provenance",
            LifecycleMatrixBindsExactMappedPayload),
        new(
            "MAP-RERUN-07",
            "Return to draft permits only a fresh rerun successor while the predecessor remains readonly and auditable",
            ReturnToDraftUsesFreshSuccessorAndHistoricalMode),
        new(
            "MAP-RERUN-08",
            "Rollback, restart, and new epoch invalidate old mapping provenance, emit rebuild intent only, and never make old results canonical",
            EpochInvalidationIsHistoricalAndRebuildOnly)
    ];

    public static IReadOnlyDictionary<string, string> SemanticRegistry { get; } =
        Cases.ToDictionary(item => item.Id, item => item.Semantic, StringComparer.Ordinal);

    public static void Run()
    {
        Require(
            RequirementRegistry.SequenceEqual(
                new[] { "P7-MAP-021", "P7-MAP-022" },
                StringComparer.Ordinal),
            "P7-09 must own exactly P7-MAP-021 and P7-MAP-022");
        Require(Cases.Count == 8, "MAP-RERUN registry must contain exactly eight cases");
        Require(SemanticRegistry.Count == 8, "MAP-RERUN ids must be unique");
        for (var number = 1; number <= 8; number++)
        {
            Require(
                SemanticRegistry.ContainsKey($"MAP-RERUN-{number:00}"),
                $"missing MAP-RERUN-{number:00}");
        }

        foreach (var contractCase in Cases)
        {
            try
            {
                contractCase.Run();
                Console.WriteLine($"PASS {contractCase.Id} {contractCase.Semantic}");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"{contractCase.Id} ({contractCase.Semantic}) failed: {error.Message}",
                    error);
            }
        }
    }

    private static void ExactRerunRetryResolvesBeforeMutableValidation()
    {
        var mappedReport = MappingTargetReport();
        var binding =
            DynamicFlowMappingLifecycleBinding.FromReport(mappedReport);
        Require(
            binding == MappingBinding(),
            "a mapped report header must reproduce the exact immutable lifecycle binding");
        Require(
            DynamicFlowMappingLifecycleBinding.FromReport(
                new WorkAssignmentReport()) is null,
            "an unmapped report must not synthesize a lifecycle binding");
        var incompleteHeader = MappingTargetReport();
        incompleteHeader.DynamicFlowMappingResultPayloadHash = null;
        InvalidOperationException? incomplete = null;
        try
        {
            _ = DynamicFlowMappingLifecycleBinding.FromReport(
                incompleteHeader);
        }
        catch (InvalidOperationException error)
        {
            incomplete = error;
        }
        Require(
            incomplete?.Message ==
            DynamicFlowMappingLifecycleContract
                .HeaderReferenceIncompleteReason,
            "a partial mapped header must fail closed with the frozen reason");

        var reportService = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = MemberBody(reportService, "ApplyDynamicFlowMappingAsync(");

        AssertBefore(
            apply,
            "LoadDynamicFlowMappingReceiptAsync(",
            "ValidateDynamicFlowMappingDraftTargetAsync(",
            "exact retry must resolve its immutable receipt before mutable draft validation");
        AssertBefore(
            apply,
            "ValidateDynamicFlowMappingReceiptReplayRequest(",
            "ValidateDynamicFlowMappingDraftTargetAsync(",
            "changed replay must conflict before mutable target validation");
        AssertContains(
            apply,
            "BuildDynamicFlowMappingSuccessorAsync(",
            "an already-mapped draft must enter the canonical successor path");
        var reportPartials = ReadBackendSources(
            "Services/WorkAssignmentReports",
            "WorkAssignmentReportService*.cs");
        var successor = MemberBody(
            reportPartials,
            "BuildDynamicFlowMappingSuccessorAsync(");
        AssertContains(
            successor,
            "RerunBlockedReason",
            "P7-08 must keep rerun blocked until P7-09");
        AssertBefore(
            successor,
            "RerunBlockedReason",
            "BuildSuccessorPlan(",
            "the P7-09 rerun gate must run before successor planning");

        var lifecycle = LifecycleContractSource();
        AssertContains(
            lifecycle,
            "DYNAMIC_FLOW_MAPPING_RERUN_BLOCKED_UNTIL_P7_09",
            "the rerun barrier must expose the stable P7-09 reason");
        AssertContains(
            lifecycle,
            "ApplyRerunOperation",
            "rerun must have a frozen outbox operation");
        AssertContains(
            lifecycle,
            "BuildSuccessorPlan(",
            "rerun must be described by a pure successor plan");

        var indexes = ReadBackendSource("Data/Indexes/MongoIndexInitializer.cs");
        foreach (var uniqueIdentity in new[]
                 {
                     "ux_dynamicFlowMappingApplyReceipts_target_command",
                     "ux_dynamicFlowMappingProvenance_receipt",
                     "ux_dynamicFlowMappingProvenance_target_payload_revision",
                     "ux_dynamicFlowMappingEvents_event_key",
                     "ux_dynamicFlowMappingOutbox_dedupe"
                 })
        {
            Require(
                Count(indexes, uniqueIdentity) == 1,
                $"{uniqueIdentity} must remain a single unique ledger identity");
        }
    }

    private static void ChangedReplayAndStalePinsFailClosed()
    {
        var reportService = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        var apply = MemberBody(reportService, "ApplyDynamicFlowMappingAsync(");

        AssertBefore(
            apply,
            "ValidateDynamicFlowMappingReceiptReplayRequest(",
            "BuildDynamicFlowMappingSuccessorAsync(",
            "changed command replay must fail before successor planning");
        AssertContains(
            apply,
            "DynamicFlowMappingSecurityContract.ValidatePreviewToken(",
            "rerun must validate the immutable preview token");
        AssertContains(
            apply,
            "_dynamicFlowTransactions.ExecuteAsync(",
            "rerun writes must stay inside the mandatory mapping transaction");

        var boundary = MemberBody(
            persistence,
            "RevalidateDynamicFlowMappingWriteBoundaryAsync(");
        foreach (var exactPin in new[]
                 {
                     "ComputeSourceSignature(",
                     "EnsureDynamicFlowMappingExpectedTarget(",
                     "ComputeDynamicFlowMappingAuthorizationScopeHash("
                 })
        {
            AssertContains(
                boundary,
                exactPin,
                $"write-boundary revalidation lost {exactPin}");
        }
        AssertContains(
            persistence,
            "DYNAMIC_FLOW_MAPPING_COMMAND_REPLAY_MISMATCH",
            "changed replay must retain its frozen mismatch code");
        AssertContains(
            persistence,
            "DYNAMIC_FLOW_MAPPING_TARGET_REVISION_CONFLICT",
            "stale target CAS must retain its stable conflict code");
    }

    private static void SourcePayloadDriftBuildsDeterministicInvalidation()
    {
        var sourcePin = MappingSourcePin();
        var source = MappingSourceReport();
        source.PayloadRevision++;
        source.PayloadHash = Hash('b');
        Require(
            DynamicFlowMappingLifecycleContract.ResolveSourceDriftReason(
                sourcePin,
                source) ==
            DynamicFlowMappingLifecycleContract.SourcePayloadDriftReason,
            "payload revision/hash drift must win over lifecycle drift");

        var provenance = MappingProvenance();
        var first = DynamicFlowMappingLifecycleContract.BuildInvalidationPlan(
            provenance,
            ObjectId(20),
            DynamicFlowMappingLifecycleContract.SourcePayloadDriftReason,
            new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc));
        var retry = DynamicFlowMappingLifecycleContract.BuildInvalidationPlan(
            provenance,
            ObjectId(20),
            DynamicFlowMappingLifecycleContract.SourcePayloadDriftReason,
            new DateTime(2026, 7, 31, 0, 5, 0, DateTimeKind.Utc));
        AssertStableInvalidationIdentity(first, retry, "source payload drift");

        var lifecycle = LifecycleContractSource();
        AssertContains(
            lifecycle,
            "SourcePayloadDriftReason",
            "source payload drift must use an exact frozen reason");
        AssertContains(
            lifecycle,
            "BuildInvalidationPlan(",
            "source drift must use the common deterministic invalidation plan");
        foreach (var pin in new[]
                 {
                     "SourceReportId",
                     "SourcePayloadRevision",
                     "SourcePayloadHash"
                 })
        {
            AssertContains(
                lifecycle,
                pin,
                $"source payload invalidation lost pin {pin}");
        }

        var reportService = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        AssertContains(
            MemberBody(reportService, "SaveDraftAsync("),
            "ValidateDynamicFlowMappingLifecycleBoundaryAsync(",
            "draft save must preserve mapping provenance without enabling P7-09 lifecycle");
        AssertContains(
            MemberBody(reportService, "SubmitAsync("),
            "ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(",
            "submit must cross the staged P7-09 mapping lifecycle boundary");
        AssertContains(
            MemberBody(reportService, "SaveDraftPatchAsync("),
            "SaveDraftAsync(",
            "patch saves must converge through the guarded full-draft mutation path");

        var reconciler = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs");
        var reconcileClaim = MemberBody(reconciler, "ReconcileClaimAsync(");
        AssertContains(
            reconcileClaim,
            "InvalidateSourceDependentsAsync(",
            "durable lifecycle reconciliation must invalidate mappings that consumed the mutated source");
        AssertBefore(
            reconcileClaim,
            "InvalidateSourceDependentsAsync(",
            "CompletePendingEntriesAsync(",
            "source-dependent invalidation must finish before the lifecycle entry is acknowledged");

        var invalidateDependents = MemberBody(
            lifecycle,
            "InvalidateSourceDependentsAsync(");
        foreach (var convergenceMember in new[]
                 {
                     "transactions.ExecuteAsync",
                     "HasSourceDrift(",
                     "ResolveSourceDriftReason(",
                     "BuildInvalidationPlan(",
                     "DynamicFlowMappingProvenanceStates.Invalidated",
                     "DynamicFlowMappingEvents.InsertOneAsync(",
                     "DynamicFlowMappingOutbox.InsertOneAsync(",
                     "ObjectId.TryParse(actorUserId",
                     "ActorUserId = effectiveActorUserId",
                     "RebuildOnly = true",
                     "P8ExecutionEnabled = false",
                     "P9ExecutionEnabled = false"
                 })
        {
            AssertContains(
                invalidateDependents,
                convergenceMember,
                $"source-dependent convergence lost {convergenceMember}");
        }
        AssertNoMongoDelete(
            invalidateDependents,
            "source drift must preserve historical provenance");
    }

    private static void SourceLifecycleDriftBuildsDeterministicInvalidation()
    {
        var sourcePin = MappingSourcePin();
        var source = MappingSourceReport();
        source.LifecycleRevision++;
        source.Status = WorkAssignmentReportStatus.Submitted;
        Require(
            DynamicFlowMappingLifecycleContract.ResolveSourceDriftReason(
                sourcePin,
                source) ==
            DynamicFlowMappingLifecycleContract.SourceLifecycleDriftReason,
            "lifecycle-only drift must use the lifecycle reason");

        var provenance = MappingProvenance();
        var first = DynamicFlowMappingLifecycleContract.BuildInvalidationPlan(
            provenance,
            ObjectId(21),
            DynamicFlowMappingLifecycleContract.SourceLifecycleDriftReason,
            new DateTime(2026, 7, 31, 1, 0, 0, DateTimeKind.Utc));
        var retry = DynamicFlowMappingLifecycleContract.BuildInvalidationPlan(
            provenance,
            ObjectId(21),
            DynamicFlowMappingLifecycleContract.SourceLifecycleDriftReason,
            new DateTime(2026, 7, 31, 1, 5, 0, DateTimeKind.Utc));
        AssertStableInvalidationIdentity(first, retry, "source lifecycle drift");

        var lifecycle = LifecycleContractSource();
        AssertContains(
            lifecycle,
            "SourceLifecycleDriftReason",
            "source lifecycle drift must use an exact frozen reason");
        foreach (var pin in new[]
                 {
                     "SourceLifecycleRevision",
                     "SourceLifecycleStatus"
                 })
        {
            AssertContains(
                lifecycle,
                pin,
                $"source lifecycle invalidation lost pin {pin}");
        }

        var reportService = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        foreach (var member in new[]
                 {
                     "ReturnAsync(",
                     "WithdrawSubmittedAsync("
                 })
        {
            AssertContains(
                MemberBody(reportService, member),
                "ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(",
                $"{member} must invalidate dependent source lifecycle pins");
        }

        var reviewService = ReadBackendSource(
            "Services/WorkAssignments/Review/WorkAssignmentReviewService.cs");
        foreach (var member in new[]
                 {
                     "ApproveReportAsync(",
                     "ReturnReportAsync(",
                     "RecallApprovedReportAsync("
                 })
        {
            AssertContains(
                MemberBody(reviewService, member),
                "TryCommitLifecycleCommandAsync(",
                $"{member} must converge through the guarded review lifecycle CAS");
        }
        var reviewCommit = MemberBody(reviewService, "TryCommitLifecycleCommandAsync(");
        Require(
            reviewCommit.Contains(
                "ValidateDynamicFlowMappingLifecycleBoundaryAsync(",
                StringComparison.Ordinal) ||
            reviewCommit.Contains(
                "DynamicFlowMappingLifecycleContract.ValidateAsync(",
                StringComparison.Ordinal),
            "review lifecycle CAS must cross the shared mapping lifecycle boundary");
    }

    private static void ConcurrentRerunsHaveOneCanonicalSuccessor()
    {
        var predecessor = MappingBinding();
        var first = DynamicFlowMappingLifecycleContract.BuildSuccessorPlan(
            predecessor,
            ObjectId(30));
        var retry = DynamicFlowMappingLifecycleContract.BuildSuccessorPlan(
            predecessor,
            ObjectId(30));
        Require(first == retry, "same predecessor and successor must produce the same plan");
        Require(first.IsRerun, "existing binding must select rerun semantics");
        Require(
            first.EventType == DynamicFlowMappingEventTypes.RerunCommitted,
            "rerun must use the successor event type");
        Require(
            first.OutboxOperation ==
            DynamicFlowMappingLifecycleContract.ApplyRerunOperation,
            "rerun must use the frozen outbox operation");
        Require(
            first.PredecessorReceiptId == predecessor.ReceiptId &&
            first.PredecessorProvenanceId == predecessor.ProvenanceId,
            "successor plan must bind the exact predecessor");
        Require(
            first.InvalidationReason ==
            DynamicFlowMappingLifecycleContract.SupersededByRerunReason,
            "successor plan must freeze the supersession reason");

        var initial = DynamicFlowMappingLifecycleContract.BuildSuccessorPlan(
            predecessor: null,
            ObjectId(30));
        Require(!initial.IsRerun, "missing predecessor must remain initial apply");
        Require(
            initial.OutboxOperation ==
            DynamicFlowMappingLifecycleContract.ApplyInitialOperation,
            "initial apply must not masquerade as rerun");

        var lifecycle = LifecycleContractSource();
        foreach (var symbol in new[]
                 {
                     "DynamicFlowMappingSuccessorPlan",
                     "BuildSuccessorPlan(",
                     "SupersededByRerunReason"
                 })
        {
            AssertContains(
                lifecycle,
                symbol,
                $"successor CAS contract lost {symbol}");
        }

        var reportPartials = ReadBackendSources(
            "Services/WorkAssignmentReports",
            "WorkAssignmentReportService*.cs");
        var successorPlan = MemberBody(
            reportPartials,
            "BuildDynamicFlowMappingSuccessorAsync(");
        AssertContains(
            successorPlan,
            "DynamicFlowMappingIntegrityMode.AllowHistorical",
            "successor planning must verify invalidated predecessors without making them mutable");
        AssertContains(
            successorPlan,
            "BuildSuccessorPlan(",
            "successor planning must use the frozen pure contract");

        var successorCommit = MemberBody(
            reportPartials,
            "CommitDynamicFlowMappingSuccessorAsync(");
        foreach (var predecessorCasMember in new[]
                 {
                     "IClientSessionHandle",
                     "DynamicFlowMappingProvenanceStates.Current",
                     "DynamicFlowMappingProvenanceStates.Invalidated",
                     "SupersededByProvenanceId",
                     "ModifiedCount",
                     "DYNAMIC_FLOW_MAPPING_SUCCESSOR_CAS_LOST"
                 })
        {
            AssertContains(
                successorCommit,
                predecessorCasMember,
                $"predecessor CAS lost {predecessorCasMember}");
        }

        var reportService = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = MemberBody(
            reportService,
            "ApplyDynamicFlowMappingAsync(");
        foreach (var atomicMember in new[]
                 {
                     "CommitDynamicFlowMappingSuccessorAsync(",
                     "DynamicFlowMappingApplyReceipts",
                     "DynamicFlowMappingProvenanceRecords",
                     "DynamicFlowMappingEvents",
                     "DynamicFlowMappingOutbox"
                 })
        {
            AssertContains(
                apply,
                atomicMember,
                $"successor transaction lost {atomicMember}");
        }
        AssertNoMongoDelete(
            successorCommit + apply,
            "rerun must never delete its predecessor ledger");
    }

    private static void LifecycleMatrixBindsExactMappedPayload()
    {
        var binding = MappingBinding();
        var mappedReport = MappingTargetReport();
        mappedReport.Status = WorkAssignmentReportStatus.Draft;
        mappedReport.IsActive = true;
        mappedReport.LifecycleRevision = 3;
        mappedReport.PayloadRevision = binding.ResultPayloadRevision;
        mappedReport.PayloadHash = binding.ResultPayloadHash;
        var lifecycleCommand = new WorkReportLifecycleCommand(
            ExpectedLifecycleRevision: 3,
            ExpectedPayloadRevision: binding.ResultPayloadRevision,
            CommandId: "lifecycle-command-001",
            Operation: "SUBMIT",
            CommandHash: Hash('f'),
            PayloadRevisionDelta: 0);
        var seed = WorkReportLifecycleOutboxContract.FromCommand(
            mappedReport,
            lifecycleCommand,
            WorkAssignmentReportStatus.Approved,
            resultIsActive: true,
            actorUserId: ObjectId(9),
            committedAtUtc:
                new DateTime(2026, 7, 31, 2, 0, 0, DateTimeKind.Utc),
            resultPayloadHash: binding.ResultPayloadHash);
        Require(
            seed.MappingBinding == binding,
            "lifecycle outbox seed must derive the exact mapping binding from the report");
        var entry = WorkReportLifecycleOutboxContract.CreateEntry(seed);
        Require(
            entry.DynamicFlowMappingReceiptId == binding.ReceiptId &&
            entry.DynamicFlowMappingProvenanceId == binding.ProvenanceId &&
            entry.DynamicFlowMappingProvenanceHash == binding.ProvenanceHash &&
            entry.DynamicFlowMappingResultPayloadRevision ==
            binding.ResultPayloadRevision &&
            entry.DynamicFlowMappingResultPayloadHash ==
            binding.ResultPayloadHash,
            "lifecycle outbox must persist every exact binding member");
        Require(
            entry.BusinessEvents.Count == 2,
            "submit auto-approve must persist both submit and approve business events");
        foreach (var businessEvent in entry.BusinessEvents)
        {
            var auditData = businessEvent.Data ??
                            throw new InvalidOperationException(
                                "mapped lifecycle event must persist binding audit data");
            foreach (var auditKey in new[]
                     {
                         "dynamicFlowMappingReceiptId",
                         "dynamicFlowMappingProvenanceId",
                         "dynamicFlowMappingProvenanceHash",
                         "dynamicFlowMappingResultPayloadRevision",
                         "dynamicFlowMappingResultPayloadHash"
                     })
            {
                Require(
                    auditData.ContainsKey(auditKey),
                    $"mapped lifecycle audit lost {auditKey}");
            }
        }

        var lifecycle = LifecycleContractSource();
        AssertContains(
            lifecycle,
            "sealed record DynamicFlowMappingLifecycleBinding",
            "mapping lifecycle binding must be immutable");
        foreach (var exactBindingMember in new[]
                 {
                     "ReceiptId",
                     "ProvenanceId",
                     "ProvenanceHash",
                     "ResultPayloadRevision",
                     "ResultPayloadHash"
                 })
        {
            AssertContains(
                lifecycle,
                exactBindingMember,
                $"lifecycle binding lost {exactBindingMember}");
        }

        var lifecycleOutbox = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkReportLifecycleOutboxContract.cs");
        var reportModel = ReadBackendSource("Models/WorkAssignmentReport.cs");
        AssertContains(
            lifecycleOutbox,
            "DynamicFlowMappingLifecycleBinding",
            "lifecycle outbox writer must consume the immutable mapping binding");
        foreach (var persistedMember in new[]
                 {
                     "DynamicFlowMappingReceiptId",
                     "DynamicFlowMappingProvenanceId",
                     "DynamicFlowMappingProvenanceHash",
                     "DynamicFlowMappingResultPayloadRevision",
                     "DynamicFlowMappingResultPayloadHash"
                 })
        {
            AssertContains(
                reportModel,
                persistedMember,
                $"lifecycle outbox model lost {persistedMember}");
        }
        var createEntry = MemberBody(lifecycleOutbox, "CreateEntry(");
        AssertContains(
            createEntry,
            "MappingBinding",
            "lifecycle outbox entry must copy the exact mapping binding");

        var reportService = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = MemberBody(
            reportService,
            "ApplyDynamicFlowMappingAsync(");
        AssertBefore(
            apply,
            "ValidateDynamicFlowMappingDraftTargetAsync(",
            "_dynamicFlowTransactions.ExecuteAsync(",
            "mapping apply must prove the target is a valid draft before any write transaction");
        AssertContains(
            apply,
            "validateRequiredFields: false",
            "draft mapping apply must permit a still-incomplete effective draft");
        var draftTarget = MemberBody(
            reportService,
            "ValidateDynamicFlowMappingDraftTargetAsync(");
        foreach (var draftOnlyGuard in new[]
                 {
                     "EnsureReportIsActive(",
                     "DynamicFlowEffectiveStatuses.Effective",
                     "entity.Status != WorkAssignmentReportStatus.Draft",
                     "DynamicFormTemplateId"
                 })
        {
            AssertContains(
                draftTarget,
                draftOnlyGuard,
                $"draft-only mapping apply lost guard {draftOnlyGuard}");
        }

        var submit = MemberBody(reportService, "SubmitAsync(");
        AssertBefore(
            submit,
            "ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(",
            "CommitLegacyLifecycleWithDirectSourceFenceAsync(",
            "submit must bind current provenance before its lifecycle write");
        AssertContains(
            submit,
            "ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(",
            "submit must validate and capture mapping provenance before lifecycle CAS");
        AssertContains(
            submit,
            "validateRequired: true",
            "submit must validate required values on the effective mapped payload");
        AssertContains(
            submit,
            "EnsureDynamicFlowMappingSummaryOverrideAllowed(",
            "submit must preserve mapping-owned metadata");
        AssertContains(
            submit,
            "validateRequiredFields: true",
            "submit must validate required values against the canonical effective payload");
        foreach (var mappingOwnedMember in new[]
                 {
                     "hasFlowOwnedMappingMetadata",
                     "entity.DataOrigin",
                     "entity.CumulativeContributionMode",
                     "entity.CumulativeContributionPolicyJson",
                     "entity.SummarySourceJson"
                 })
        {
            AssertContains(
                submit,
                mappingOwnedMember,
                $"submit must preserve mapping-owned member {mappingOwnedMember}");
        }

        var legacyLifecycleCommit = MemberBody(
            reportService,
            "private async Task<UpdateResult> CommitLegacyLifecycleWithDirectSourceFenceAsync(");
        AssertContains(
            legacyLifecycleCommit,
            "_dynamicFlowTransactions.ExecuteAsync(",
            "legacy lifecycle CAS, durable outbox, and Work fence must share one transaction");
        AssertBefore(
            legacyLifecycleCommit,
            "UpdateOneAsync(",
            "WorkDirectSourceRevisionFence.IncrementAsync(",
            "legacy lifecycle CAS must succeed before its Work Direct-source fence");
        Require(
            Count(
                legacyLifecycleCommit,
                "WorkDirectSourceRevisionFence.IncrementAsync(") == 1,
            "legacy lifecycle transaction must increment exactly one Work Direct-source fence");

        var saveDraft = MemberBody(reportService, "SaveDraftAsync(");
        AssertContains(
            saveDraft,
            "EnsureDynamicFlowMappingProvenanceOverrideAllowed(",
            "manual draft save must reject mapping provenance replacement");
        AssertContains(
            saveDraft,
            "ValidateDynamicFlowMappingLifecycleBoundaryAsync(",
            "manual draft save must verify current mapping provenance");
        AssertContains(
            MemberBody(reportService, "SaveDraftPatchAsync("),
            "SaveDraftAsync(",
            "manual patch must converge through the same mapping-owned metadata guards");

        var reviewService = ReadBackendSource(
            "Services/WorkAssignments/Review/WorkAssignmentReviewService.cs");
        var reviewCommit = MemberBody(
            reviewService,
            "TryCommitLifecycleCommandAsync(");
        AssertContains(
            reviewCommit,
            "DynamicFlowMappingLifecycleContract.ValidateAsync(",
            "approve/return/recall lifecycle CAS must validate mapping provenance");
        AssertContains(
            reviewCommit,
            "WorkReportLifecycleOutboxContract.FromCommand(",
            "approve/return/recall lifecycle CAS must persist the exact mapping binding");
        AssertBefore(
            reviewCommit,
            "DynamicFlowMappingLifecycleContract.ValidateAsync(",
            "UpdateOneAsync(",
            "approve/return/recall must validate exact mapping provenance before review CAS");

        foreach (var lifecycleMember in new[]
                 {
                     "AcceptAsync(",
                     "ReturnAsync(",
                     "WithdrawSubmittedAsync("
                 })
        {
            var lifecycleBody = MemberBody(
                reportService,
                lifecycleMember);
            AssertBefore(
                lifecycleBody,
                "ValidateDynamicFlowMappingLifecycleMutationBoundaryAsync(",
                "CommitLegacyLifecycleWithDirectSourceFenceAsync(",
                $"{lifecycleMember} must bind mapping provenance before lifecycle CAS");
            AssertContains(
                lifecycleBody,
                "WorkReportLifecycleOutboxContract.FromCommand(",
                $"{lifecycleMember} must copy mapping identity into the lifecycle outbox");
        }

        var mappingEngine = ReadBackendSource(
            "Services/DynamicFlows/DynamicFlowMappingEngine.cs");
        AssertContains(
            mappingEngine,
            "CumulativeContributionMode = WorkReportCumulativeContributionMode.Exclude",
            "mapped reports must remain excluded from P8/P9 contribution by default");

        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        var committedReport = MemberBody(
            persistence,
            "BuildDynamicFlowMappingCommittedReport(");
        AssertContains(
            committedReport,
            "committed.CumulativeContributionMode = WorkReportCumulativeContributionMode.Exclude",
            "P7 apply must persist mapped reports outside the P8/P9 contribution set");

        var reconciler = ReadBackendSource(
            "Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs");
        var deferStatistics = SourceBetween(
            reconciler,
            "private static bool ShouldDeferDynamicFlowMappingStatistics(",
            "private async Task<LifecycleProjectionClaim?>");
        AssertContains(
            deferStatistics,
            "DynamicFlowMappingLifecycleBinding.FromReport(report) is not null",
            "every complete P7 mapping binding must defer P8/P9 statistics regardless of future contribution rules");
        AssertNotContains(
            deferStatistics,
            "CumulativeContributionMode",
            "P7 must not enable statistics from a caller or future INCLUDE contribution mode");

        var reconcileClaim = MemberBody(
            reconciler,
            "ReconcileClaimAsync(");
        AssertBefore(
            reconcileClaim,
            "pending =",
            "ShouldDeferDynamicFlowMappingStatistics(report)",
            "the statistics decision must use the reloaded report and durable pending entries");
        var firstDeferredBlock = BlockAtOccurrence(
            reconcileClaim,
            "if (!deferDynamicFlowMappingStatistics)",
            1);
        foreach (var deferredExecutor in new[]
                 {
                     "_labelStatistics.RebuildForReportAsync(",
                     "_tableStatistics.RebuildForReportAsync(",
                     "_fieldStatistics.RebuildForReportAsync("
                 })
        {
            AssertContains(
                firstDeferredBlock,
                deferredExecutor,
                $"mapped P7 lifecycle must defer {deferredExecutor}");
            Require(
                Count(reconcileClaim, deferredExecutor) == 1,
                $"{deferredExecutor} must not escape its P7 defer guard");
        }
        var secondDeferredBlock = BlockAtOccurrence(
            reconcileClaim,
            "if (!deferDynamicFlowMappingStatistics)",
            2);
        AssertContains(
            secondDeferredBlock,
            "_advancedSummaryDirty.MarkReportStatusMutationDirtyAsync(",
            "mapped P7 lifecycle must defer advanced-summary dirty execution");
        Require(
            Count(
                reconcileClaim,
                "_advancedSummaryDirty.MarkReportStatusMutationDirtyAsync(") == 1,
            "advanced-summary dirty execution must not escape its P7 defer guard");

        foreach (var preservedProjection in new[]
                 {
                     "_sectionProjection.ProjectCurrentAndVerifyAsync(",
                     "InvalidateSourceDependentsAsync(",
                     "ReconcilePeriodAsync(",
                     "_queue.UpsertPeriodAsync(",
                     "_statusSync.SyncFromAssignmentAsync(",
                     "_docRoleProjection.RebuildReportPeriodAsync(",
                     "_dynamicFlowRuntimeStateProjector.ProjectReportLifecycleAsync("
                 })
        {
            AssertBefore(
                reconcileClaim,
                preservedProjection,
                "if (!deferDynamicFlowMappingStatistics)",
                $"mapped P7 lifecycle must preserve {preservedProjection} before deferred statistics");
        }
        AssertNotContains(
            firstDeferredBlock + secondDeferredBlock,
            "_aggregateDependentRecovery.RecoverPendingAsync(",
            "aggregate dependent recovery is a core lifecycle projection, not a P8/P9 statistic");
        AssertNotContains(
            firstDeferredBlock + secondDeferredBlock,
            "_businessLogProjector.ProjectAndVerifyAsync(",
            "business-log projection must remain outside the P8/P9 defer guards");
        AssertNotContains(
            firstDeferredBlock + secondDeferredBlock,
            "CompletePendingEntriesAsync(",
            "durable lifecycle completion must remain outside the P8/P9 defer guards");
        AssertBefore(
            reconcileClaim,
            "_advancedSummaryDirty.MarkReportStatusMutationDirtyAsync(",
            "_businessLogProjector.ProjectAndVerifyAsync(",
            "business logs must still project after the deferred-statistics region");
        AssertBefore(
            reconcileClaim,
            "_businessLogProjector.ProjectAndVerifyAsync(",
            "CompletePendingEntriesAsync(",
            "mapped EXCLUDE lifecycle entries must still reach durable completion");
    }

    private static void ReturnToDraftUsesFreshSuccessorAndHistoricalMode()
    {
        Require(
            DynamicFlowMappingLifecycleContract.IsStateAllowed(
                DynamicFlowMappingProvenanceStates.Current,
                DynamicFlowMappingIntegrityMode.RequireCurrent),
            "current provenance must pass mutable integrity");
        Require(
            !DynamicFlowMappingLifecycleContract.IsStateAllowed(
                DynamicFlowMappingProvenanceStates.Invalidated,
                DynamicFlowMappingIntegrityMode.RequireCurrent),
            "invalidated provenance must not pass mutable integrity");
        Require(
            DynamicFlowMappingLifecycleContract.IsStateAllowed(
                DynamicFlowMappingProvenanceStates.Invalidated,
                DynamicFlowMappingIntegrityMode.AllowHistorical) &&
            DynamicFlowMappingLifecycleContract.IsStateAllowed(
                DynamicFlowMappingProvenanceStates.Superseded,
                DynamicFlowMappingIntegrityMode.AllowHistorical),
            "invalidated and superseded predecessors must remain auditable");

        var lifecycle = LifecycleContractSource();
        foreach (var mode in new[]
                 {
                     "DynamicFlowMappingIntegrityMode",
                     "RequireCurrent",
                     "AllowHistorical"
                 })
        {
            AssertContains(
                lifecycle,
                mode,
                $"mapping integrity modes lost {mode}");
        }
        AssertContains(
            lifecycle,
            "SupersededByRerunReason",
            "return-to-draft rerun must use the frozen supersession reason");

        var persistence = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.DynamicFlowMappingPersistence.cs");
        var guard = MemberBody(
            persistence,
            "EnsureDynamicFlowMappingProvenanceIntegrityAsync(");
        AssertContains(
            guard,
            "DynamicFlowMappingIntegrityMode",
            "provenance verification must distinguish mutable current state from historical audit");
        AssertContains(
            guard,
            "DynamicFlowMappingLifecycleContract.IsStateAllowed(",
            "old provenance verification must use the shared current/historical state policy");

        var reportService = ReadBackendSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var apply = MemberBody(reportService, "ApplyDynamicFlowMappingAsync(");
        AssertContains(
            apply,
            "BuildDynamicFlowMappingSuccessorAsync(",
            "a fresh preview on a returned draft must build a successor");
        var reportPartials = ReadBackendSources(
            "Services/WorkAssignmentReports",
            "WorkAssignmentReportService*.cs");
        var successor = MemberBody(
            reportPartials,
            "BuildDynamicFlowMappingSuccessorAsync(");
        AssertContains(
            apply + successor,
            "AllowHistorical",
            "fresh rerun must verify an invalidated predecessor before successor CAS");
    }

    private static void EpochInvalidationIsHistoricalAndRebuildOnly()
    {
        var provenance = MappingProvenance();
        var first = DynamicFlowMappingLifecycleContract.BuildInvalidationPlan(
            provenance,
            ObjectId(40),
            DynamicFlowMappingLifecycleContract.EpochInvalidatedReason,
            new DateTime(2026, 7, 31, 3, 0, 0, DateTimeKind.Utc));
        var replay = DynamicFlowMappingLifecycleContract.BuildInvalidationPlan(
            provenance,
            ObjectId(40),
            DynamicFlowMappingLifecycleContract.EpochInvalidatedReason,
            new DateTime(2026, 7, 31, 3, 10, 0, DateTimeKind.Utc));
        AssertStableInvalidationIdentity(first, replay, "epoch command replay");
        Require(
            first.Reason ==
            DynamicFlowMappingLifecycleContract.EpochInvalidatedReason,
            "epoch invalidation plan must retain its frozen reason");
        var nextEpoch = DynamicFlowMappingLifecycleContract.BuildInvalidationPlan(
            provenance,
            ObjectId(41),
            DynamicFlowMappingLifecycleContract.EpochInvalidatedReason,
            new DateTime(2026, 7, 31, 3, 0, 0, DateTimeKind.Utc));
        Require(
            first.EventKey != nextEpoch.EventKey &&
            first.OutboxDedupeKey != nextEpoch.OutboxDedupeKey,
            "a new epoch event must not reuse an old invalidation identity");

        var lifecycle = LifecycleContractSource();
        foreach (var symbol in new[]
                 {
                     "DynamicFlowMappingInvalidationPlan",
                     "BuildInvalidationPlan(",
                     "EpochInvalidatedReason",
                     "RebuildIntentOperation"
                 })
        {
            AssertContains(
                lifecycle,
                symbol,
                $"epoch invalidation contract lost {symbol}");
        }

        var epochCommands = ReadBackendSource(
            "Services/DynamicFlows/DynamicFlowRuntimeEpochCommands.cs");
        var epochCommand = MemberBody(
            epochCommands,
            "ExecuteEpochCommandAsync(");
        AssertContains(
            epochCommand,
            "InvalidateDynamicFlowMappingProvenanceAsync(",
            "rollback/restart must invalidate mapping provenance in the P6 transaction");
        AssertContains(
            epochCommand,
            "session",
            "mapping invalidation must share the epoch transaction session");
        AssertBefore(
            epochCommand,
            "InvalidateDynamicFlowMappingProvenanceAsync(",
            "InvalidateEpochClosureAsync(",
            "mapping provenance must be invalidated by the same event before epoch closure");

        var runtimePartials = ReadBackendSources(
            "Services/DynamicFlows",
            "DynamicFlowRuntime*.cs");
        var invalidation = MemberBody(
            runtimePartials,
            "InvalidateDynamicFlowMappingProvenanceAsync(");
        foreach (var ledgerMember in new[]
                 {
                     "DynamicFlowMappingProvenanceRecords",
                     "DynamicFlowMappingProvenanceStates.Invalidated",
                     "InvalidatedByEventId",
                     "EpochInvalidatedReason",
                     "SupersededByProvenanceId",
                     "ModifiedCount"
                 })
        {
            AssertContains(
                invalidation,
                ledgerMember,
                $"epoch invalidation lost durable ledger member {ledgerMember}");
        }
        AssertNoMongoDelete(
            invalidation,
            "old epoch mapping history must never be deleted");
        foreach (var forbiddenExecution in new[]
                 {
                     "DynamicFlowMappingEngine",
                     "EvaluateAsync(",
                     "CalculateStatistic"
                 })
        {
            AssertNotContains(
                invalidation,
                forbiddenExecution,
                $"epoch invalidation must emit rebuild intent only, not execute {forbiddenExecution}");
        }

        foreach (var intentMember in new[]
                 {
                     "DynamicFlowRuntimeOutbox.InsertOneAsync(",
                     "mappingProvenanceIds",
                     "\"mappingRebuildOnly\", true",
                     "\"p8ExecutionEnabled\", false",
                     "\"p9ExecutionEnabled\", false",
                     "RebuildIntentOperation"
                 })
        {
            AssertContains(
                epochCommand,
                intentMember,
                $"rollback/restart rebuild intent lost {intentMember}");
        }
    }

    private static string LifecycleContractSource()
        => ReadBackendSource(
            "Services/DynamicFlows/DynamicFlowMappingLifecycleContract.cs");

    private static DynamicFlowMappingLifecycleBinding MappingBinding()
        => new(
            ObjectId(10),
            ObjectId(11),
            Hash('c'),
            7,
            Hash('d'));

    private static WorkAssignmentReport MappingTargetReport()
        => new()
        {
            Id = ObjectId(3),
            WorkId = ObjectId(1),
            WorkAssignmentId = ObjectId(2),
            DynamicFlowMappingReceiptId = ObjectId(10),
            DynamicFlowMappingProvenanceId = ObjectId(11),
            DynamicFlowMappingProvenanceHash = Hash('c'),
            DynamicFlowMappingResultPayloadRevision = 7,
            DynamicFlowMappingResultPayloadHash = Hash('d')
        };

    private static DynamicFlowMappingProvenanceRecord MappingProvenance()
        => new()
        {
            Id = ObjectId(11),
            ReceiptId = ObjectId(10),
            WorkId = ObjectId(1),
            TargetAssignmentId = ObjectId(2),
            TargetReportId = ObjectId(3),
            CommandId = "mapping-command-001",
            State = DynamicFlowMappingProvenanceStates.Current
        };

    private static DynamicFlowMappingSourcePin MappingSourcePin()
        => new()
        {
            SourceReportId = ObjectId(4),
            SourceAssignmentId = ObjectId(5),
            SourceFlowInstanceId = ObjectId(6),
            SourceExecutionEpoch = 1,
            SourceStepInstanceId = ObjectId(7),
            SourceStepId = "source-step",
            SourceBranchId = ObjectId(8),
            SourceAttemptNo = 1,
            SourceFormFamilyId = ObjectId(9),
            SourceFormVersionId = ObjectId(10),
            SourceFormVersionNo = 1,
            SourceFormSchemaHash = Hash('a'),
            SourcePayloadRevision = 3,
            SourcePayloadHash = Hash('a'),
            SourceLifecycleRevision = 2,
            SourceLifecycleStatus = WorkAssignmentReportStatus.Approved.ToString(),
            SourcePeriodInstanceKey = "period-001",
            SourceFactHash = Hash('e')
        };

    private static WorkAssignmentReport MappingSourceReport()
        => new()
        {
            Id = ObjectId(4),
            WorkId = ObjectId(1),
            WorkAssignmentId = ObjectId(5),
            PayloadRevision = 3,
            PayloadHash = Hash('a'),
            LifecycleRevision = 2,
            Status = WorkAssignmentReportStatus.Approved,
            IsActive = true,
            IsCurrent = true
        };

    private static void AssertStableInvalidationIdentity(
        DynamicFlowMappingInvalidationPlan first,
        DynamicFlowMappingInvalidationPlan retry,
        string context)
    {
        Require(first.EventId == retry.EventId, $"{context} event id must be deterministic");
        Require(first.EventKey == retry.EventKey, $"{context} event key must be deterministic");
        Require(first.OutboxId == retry.OutboxId, $"{context} outbox id must be deterministic");
        Require(
            first.OutboxDedupeKey == retry.OutboxDedupeKey,
            $"{context} outbox dedupe key must be deterministic");
    }

    private static string ReadBackendSources(
        string relativeDirectory,
        string searchPattern)
    {
        var root = FindBackendRoot();
        var directory = Path.Combine(
            root,
            relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        var files = Directory
            .EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Require(files.Count > 0, $"no backend source matched {relativeDirectory}/{searchPattern}");
        return string.Join(
            "\n",
            files.Select(path => File.ReadAllText(path)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ReadBackendSource(string relativePath)
    {
        var path = Path.Combine(
            FindBackendRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException($"Backend source was not found: {relativePath}");
        return File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
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

        throw new InvalidOperationException("Could not locate the tdtd-be source root.");
    }

    private static string MemberBody(string source, string memberToken)
    {
        var searchOffset = 0;
        var tokenIndex = -1;
        var openBrace = -1;
        while ((tokenIndex = source.IndexOf(
                   memberToken,
                   searchOffset,
                   StringComparison.Ordinal)) >= 0)
        {
            openBrace = source.IndexOf('{', tokenIndex + memberToken.Length);
            if (openBrace < 0)
                break;
            var semicolon = source.IndexOf(';', tokenIndex + memberToken.Length);
            if (semicolon < 0 || openBrace < semicolon)
                break;
            searchOffset = tokenIndex + memberToken.Length;
        }
        if (tokenIndex < 0)
            throw new InvalidOperationException($"Source member was not found: {memberToken}");
        if (openBrace < 0)
            throw new InvalidOperationException($"Source member has no body: {memberToken}");

        var depth = 0;
        var inString = false;
        var inVerbatimString = false;
        var inChar = false;
        var escaped = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var index = openBrace; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n')
                    inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }
            if (inVerbatimString)
            {
                if (current == '"' && next == '"')
                {
                    index++;
                    continue;
                }
                if (current == '"')
                    inVerbatimString = false;
                continue;
            }
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (current == '"')
                    inString = false;
                continue;
            }
            if (inChar)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (current == '\'')
                    inChar = false;
                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }
            if (current == '@' && next == '"')
            {
                inVerbatimString = true;
                index++;
                continue;
            }
            if (current == '"')
            {
                inString = true;
                continue;
            }
            if (current == '\'')
            {
                inChar = true;
                continue;
            }
            if (current == '{')
            {
                depth++;
                continue;
            }
            if (current != '}')
                continue;

            depth--;
            if (depth == 0)
                return source[tokenIndex..(index + 1)];
        }

        throw new InvalidOperationException($"Source member body was not closed: {memberToken}");
    }

    private static string BlockAtOccurrence(
        string source,
        string token,
        int occurrence)
    {
        Require(occurrence > 0, "block occurrence must be positive");
        var offset = 0;
        for (var index = 1; index <= occurrence; index++)
        {
            offset = source.IndexOf(
                token,
                offset,
                StringComparison.Ordinal);
            if (offset < 0)
            {
                throw new InvalidOperationException(
                    $"Source block occurrence was not found: {token} #{occurrence}");
            }

            if (index < occurrence)
                offset += token.Length;
        }

        return MemberBody(source[offset..], token);
    }

    private static string SourceBetween(
        string source,
        string startToken,
        string endToken)
    {
        var start = source.IndexOf(
            startToken,
            StringComparison.Ordinal);
        Require(start >= 0, $"source start token was not found: {startToken}");
        var end = source.IndexOf(
            endToken,
            start + startToken.Length,
            StringComparison.Ordinal);
        Require(end > start, $"source end token was not found: {endToken}");
        return source[start..end];
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Hash(char value) => new(value, 64);

    private static string ObjectId(int seed)
        => $"1000000000000000000000{seed:00}";

    private static void AssertBefore(
        string source,
        string first,
        string second,
        string context)
    {
        source = WithoutWhitespace(source);
        first = WithoutWhitespace(first);
        second = WithoutWhitespace(second);
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
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
        source = WithoutWhitespace(source);
        expected = WithoutWhitespace(expected);
        if (!source.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: expected '{expected}'.");
    }

    private static void AssertNotContains(
        string source,
        string forbidden,
        string context)
    {
        source = WithoutWhitespace(source);
        forbidden = WithoutWhitespace(forbidden);
        if (source.Contains(forbidden, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: unexpected '{forbidden}'.");
    }

    private static string WithoutWhitespace(string value)
        => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static void AssertNoMongoDelete(string source, string context)
    {
        foreach (var forbidden in new[]
                 {
                     "DeleteOneAsync(",
                     "DeleteManyAsync(",
                     "FindOneAndDeleteAsync("
                 })
        {
            AssertNotContains(source, forbidden, context);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
