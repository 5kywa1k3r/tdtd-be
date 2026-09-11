using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsRun;

const string RunId = "0123456789abcdef01234567";
const string ActorId = "1123456789abcdef01234567";
const string ComparisonBinding =
    "615bdb6f6ddee2c0acb6ab68089dc387a52604c705f2af977330e2362fa6db3f";
var at = new DateTime(2026, 8, 12, 1, 0, 0, DateTimeKind.Utc);

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-REMEDIATION-01", MissingAndExtraRecoverThroughTrustedBinding),
    ("P10-REMEDIATION-02", MissingOrChangedEvidenceFailsWithoutVerdictWrite),
    ("P10-REMEDIATION-03", MissingOrTamperedP9ReceiptFailsBeforeMarker),
    ("P10-REMEDIATION-04", FoundationRefreshV2IntegrityIsVersioned),
    ("P10-REMEDIATION-05", LifecycleOperationVersionAndTerminalStateIntegrity)
};

foreach (var item in cases)
{
    try
    {
        await item.Run();
        Console.WriteLine($"PASS {item.Id}");
    }
    catch (Exception exception)
    {
        Console.WriteLine(
            $"FAIL {item.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}
Console.WriteLine(
    "P10_RECHECK_REMEDIATION_OK cases=5 missing=true extra=true " +
    "serverOwned=true markerBound=true receiptTuple=true zeroWrite=true");
return 0;

async Task MissingAndExtraRecoverThroughTrustedBinding()
{
    foreach (var kind in new[]
             {
                 RemediationLayerKind.Missing,
                 RemediationLayerKind.Extra
             })
    {
        var backend = new MemoryVerdictBackend();
        var publisher = new StatisticReconciliationFinalVerdictPublisher(
            backend);
        var baseRequest = VerdictRequest(kind, H('1'), H('2'));
        var baseVerdict = await publisher.PublishAsync(baseRequest, at);
        var (run, capture, p9) = RemediationFixture(baseVerdict,
            kind == RemediationLayerKind.Missing ? '3' : '4');
        var evidence =
            StatisticReconciliationTrustedRemediationEvidenceCanonical.Build(
                run, baseVerdict, capture, p9, ActorId, H('a'),
                at.AddSeconds(1));
        var replayCandidate =
            StatisticReconciliationTrustedRemediationEvidenceCanonical.Build(
                run, baseVerdict, capture, p9, ActorId, H('b'),
                at.AddSeconds(9));
        Require(
            StatisticReconciliationTrustedRemediationEvidenceCanonical
                .SameTarget(evidence, replayCandidate) &&
            evidence.EvidenceSha256 != replayCandidate.EvidenceSha256,
            $"{kind}_AUTHORIZATION_RETRY_CONVERGES");
        var changedCapture =
            BsonSerializer.Deserialize<
                StatisticReconciliationRecheckCaptureBinding>(
                capture.ToBson());
        changedCapture.ActualCapturePlan.Export.ContentSha256 = H('d');
        changedCapture.ActualCapturePlan.PlanSha256 =
            StatisticReconciliationActualCapturePlanIntegrity.PlanSha(
                changedCapture.ActualCapturePlan);
        changedCapture.ActualCapturePlanSha256 =
            changedCapture.ActualCapturePlan.PlanSha256;
        StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(
            changedCapture);
        var changedTarget =
            StatisticReconciliationTrustedRemediationEvidenceCanonical.Build(
                run, baseVerdict, changedCapture, p9, ActorId, H('b'),
                at.AddSeconds(10));
        Require(
            !StatisticReconciliationTrustedRemediationEvidenceCanonical
                .SameTarget(evidence, changedTarget),
            $"{kind}_CHANGED_TARGET_REJECTED");

        var remediation =
            StatisticReconciliationTrustedRemediationEvidenceCanonical
                .ToBinding(evidence);
        capture.RemediationEvidenceSha256 = evidence.EvidenceSha256;
        StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(capture);
        var marker = StatisticReconciliationRecheckCanonical.NewMarker(
            run.Id, ActorId,
            new("begin-remediation", 7, H('b')),
            run.Status, run.CurrentGenerationId!, run.CurrentGenerationHash!,
            baseVerdict.VerdictGenerationId,
            baseVerdict.VerdictGenerationSha256,
            capture, at.AddSeconds(2), remediation);

        var successorRequest = VerdictRequest(
            RemediationLayerKind.Zero, H('5'), H('6'));
        var remediationRequest =
            StatisticReconciliationTrustedRemediationEvidenceCanonical
                .BuildVerdictRequest(marker.RemediationBinding!,
                    successorRequest.ActualGenerationId,
                    successorRequest.ActualGenerationSha256);
        var successor = await publisher.PublishSupersedingRecheckAsync(
            RunId, baseVerdict.VerdictGenerationId, successorRequest,
            remediationRequest, at.AddSeconds(3));
        Require(remediationRequest.AfterResultSha256 ==
                    marker.RemediationBinding!.SuccessorP9GenerationSha256 &&
                remediationRequest.AfterResultSha256 !=
                    successorRequest.ActualGenerationSha256,
            $"{kind}_RESULT_HASH_DOMAIN");
        Require(successor.Verdict ==
                    StatisticReconciliationFinalVerdicts.Matched &&
                successor.Remediation is not null &&
                StatisticReconciliationTrustedRemediationEvidenceCanonical
                    .MatchesStored(marker.RemediationBinding,
                        successor.Remediation,
                        successor.ActualGenerationId,
                        successor.ActualGenerationSha256) &&
                backend.Writes == 2,
            $"{kind}_TRUSTED_RECOVERY");
        successor.Remediation!.AfterResultSha256 =
            successor.ActualGenerationSha256;
        Require(!StatisticReconciliationTrustedRemediationEvidenceCanonical
                .MatchesStored(marker.RemediationBinding,
                    successor.Remediation,
                    successor.ActualGenerationId,
                    successor.ActualGenerationSha256),
            $"{kind}_P9_AFTER_RESULT_DRIFT_REJECTED");
    }
}

async Task MissingOrChangedEvidenceFailsWithoutVerdictWrite()
{
    var backend = new MemoryVerdictBackend();
    var publisher = new StatisticReconciliationFinalVerdictPublisher(backend);
    var baseVerdict = await publisher.PublishAsync(
        VerdictRequest(RemediationLayerKind.Missing, H('1'), H('2')), at);
    var successorRequest = VerdictRequest(
        RemediationLayerKind.Zero, H('3'), H('4'));
    var before = backend.Writes;
    await ExpectCodeAsync(() => publisher.PublishSupersedingRecheckAsync(
            RunId, baseVerdict.VerdictGenerationId, successorRequest,
            null, at.AddSeconds(1)),
        StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid);
    Require(backend.Writes == before, "MISSING_EVIDENCE_ZERO_WRITE");

    var (run, capture, p9) = RemediationFixture(baseVerdict, '5');
    var evidence =
        StatisticReconciliationTrustedRemediationEvidenceCanonical.Build(
            run, baseVerdict, capture, p9, ActorId, H('a'), at.AddSeconds(2));
    var binding =
        StatisticReconciliationTrustedRemediationEvidenceCanonical
            .ToBinding(evidence);
    binding.ReferenceSha256 = H('f');
    Expect<InvalidOperationException>(() =>
        StatisticReconciliationTrustedRemediationEvidenceCanonical
            .RequireValid(binding));
    Require(backend.Writes == before, "TAMPERED_EVIDENCE_ZERO_WRITE");

    capture.RemediationEvidenceSha256 = evidence.EvidenceSha256;
    StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(capture);
    Expect<StatisticReconciliationRecheckException>(() =>
        StatisticReconciliationRecheckCanonical.NewMarker(
            run.Id, ActorId, new("missing-binding", 7, H('b')),
            run.Status, run.CurrentGenerationId!, run.CurrentGenerationHash!,
            baseVerdict.VerdictGenerationId,
            baseVerdict.VerdictGenerationSha256,
            capture, at.AddSeconds(3), remediationBinding: null));
    Require(backend.Writes == before, "MISSING_MARKER_BINDING_ZERO_WRITE");
}

Task MissingOrTamperedP9ReceiptFailsBeforeMarker()
{
    foreach (var tamper in new[] { "RECEIPT", "REQUEST", "ROUTE" })
    {
        var baseVerdict = new StatisticReconciliationReview
        {
            VerdictGenerationId = H('1'),
            VerdictGenerationSha256 = H('2'),
            ActualGenerationId = H('3'),
            ActualGenerationSha256 = H('4'),
            Verdict = StatisticReconciliationFinalVerdicts.Mismatched,
            RootCauseClass =
                StatisticReconciliationRootCauseClasses.ExtraIdentity,
            MissingOrExtraIdentity = true
        };
        var (run, capture, p9) = RemediationFixture(baseVerdict, '6');
        if (tamper == "RECEIPT") p9.ReceiptId = null;
        if (tamper == "REQUEST") p9.RequestHash = H('e');
        if (tamper == "ROUTE") p9.RouteId = "P9_WRONG_ROUTE";
        Expect<InvalidOperationException>(() =>
            StatisticReconciliationTrustedRemediationEvidenceCanonical.Build(
                run, baseVerdict, capture, p9, ActorId, H('a'), at));
    }
    return Task.CompletedTask;
}

Task FoundationRefreshV2IntegrityIsVersioned()
{
    var baseVerdict = new StatisticReconciliationReview
    {
        VerdictGenerationId = H('1'),
        VerdictGenerationSha256 = H('2'),
        ActualGenerationId = H('3'),
        ActualGenerationSha256 = H('4'),
        Verdict = StatisticReconciliationFinalVerdicts.Mismatched,
        RootCauseClass =
            StatisticReconciliationRootCauseClasses.ExtraIdentity,
        MissingOrExtraIdentity = true
    };
    var (run, capture, p9) = RemediationFixture(baseVerdict, '7');
    var legacyReference =
        StatisticReconciliationTrustedRemediationEvidenceCanonical
            .AuthorizedOperationReferenceSha(p9);
    PromoteToFoundationRefreshV2(capture, p9);
    var evidence =
        StatisticReconciliationTrustedRemediationEvidenceCanonical.Build(
            run, baseVerdict, capture, p9, ActorId, H('a'), at);
    Require(evidence.ReferenceId == p9.ReceiptId &&
            evidence.ReferenceSha256 != legacyReference &&
            StatisticReconciliationTrustedRemediationEvidenceCanonical
                .Matches(evidence, run, baseVerdict, capture, p9),
        "FOUNDATION_REFRESH_V2_ACCEPTED");

    foreach (var tamper in new[] { "IDENTITY", "COMMAND", "GENERATION" })
    {
        var candidate =
            BsonSerializer.Deserialize<WorkReportStatisticRebuildJob>(
                p9.ToBson());
        var successor =
            BsonSerializer.Deserialize<
                StatisticReconciliationRecheckCaptureBinding>(
                capture.ToBson());
        if (tamper == "IDENTITY")
            candidate.DirectProjectionIdentityKey = H('f');
        if (tamper == "COMMAND")
            candidate.CommandId = "forged-client-command";
        if (tamper == "GENERATION")
        {
            candidate.GenerationId = H('0');
            successor.P9GenerationId = candidate.GenerationId;
            StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(
                successor);
        }
        StatRunService.RefreshP10TrustedLifecycleOperationIntegrity(candidate);
        Expect<InvalidOperationException>(() =>
            StatisticReconciliationTrustedRemediationEvidenceCanonical.Build(
                run, baseVerdict, successor, candidate, ActorId, H('a'), at));
    }
    return Task.CompletedTask;
}

Task LifecycleOperationVersionAndTerminalStateIntegrity()
{
    var baseVerdict = new StatisticReconciliationReview
    {
        VerdictGenerationId = H('1'),
        VerdictGenerationSha256 = H('2'),
        ActualGenerationId = H('3'),
        ActualGenerationSha256 = H('4'),
        Verdict = StatisticReconciliationFinalVerdicts.Mismatched,
        RootCauseClass =
            StatisticReconciliationRootCauseClasses.ExtraIdentity,
        MissingOrExtraIdentity = true
    };

    var (_, _, legacy) = RemediationFixture(baseVerdict, '8');
    legacy.CandidatePromptId = "P9-08";
    StatRunService.RefreshP10TrustedLifecycleOperationIntegrity(legacy);
    Require(
        StatRunService.HasLifecycleOperationsHeaderIntegrity(legacy),
        "LEGACY_OPERATIONS_PROMPT_ACCEPTED");
    legacy.CandidatePromptId = StatRunCapabilityActivation.PublishedPromptId;
    StatRunService.RefreshP10TrustedLifecycleOperationIntegrity(legacy);
    Require(
        !StatRunService.HasLifecycleOperationsHeaderIntegrity(legacy),
        "LEGACY_OPERATIONS_PROMPT_VERSION_FENCE");

    var (_, v2Capture, v2) = RemediationFixture(baseVerdict, '9');
    PromoteToFoundationRefreshV2(v2Capture, v2);
    Require(
        StatRunService.HasLifecycleOperationsHeaderIntegrity(v2),
        "FOUNDATION_V2_OPERATIONS_PROMPT_ACCEPTED");
    var candidateV2 =
        BsonSerializer.Deserialize<WorkReportStatisticRebuildJob>(
            v2.ToBson());
    RebindFoundationRefreshPrompt(
        candidateV2,
        StatRunCapabilityActivation.LifecycleRequiredPromptId);
    Require(
        StatRunService.HasLifecycleOperationsHeaderIntegrity(
            candidateV2),
        "FOUNDATION_V2_CANDIDATE_PROMPT_ACCEPTED");

    var wrongV2Prompt =
        BsonSerializer.Deserialize<WorkReportStatisticRebuildJob>(
            v2.ToBson());
    RebindFoundationRefreshPrompt(wrongV2Prompt, "P9-08");
    Require(
        !StatRunService.HasLifecycleOperationsHeaderIntegrity(
            wrongV2Prompt),
        "FOUNDATION_V2_OPERATIONS_PROMPT_VERSION_FENCE");

    foreach (var version in new[] { "V1", "V2" })
    {
        var fixture = RemediationFixture(
            baseVerdict,
            version == "V1" ? 'a' : 'b');
        if (version == "V2")
            PromoteToFoundationRefreshV2(fixture.Capture, fixture.P9);
        fixture.P9.NextRetryAtUtc = at.AddMinutes(1);
        fixture.P9.LeaseUntilUtc = at.AddMinutes(2);
        fixture.P9.ClaimToken = "residual-claim";
        fixture.P9.LeaseOwnerId = "residual-worker";
        StatRunService.RefreshP10TrustedLifecycleOperationIntegrity(
            fixture.P9);
        Expect<InvalidOperationException>(() =>
            StatisticReconciliationTrustedRemediationEvidenceCanonical.Build(
                fixture.Run,
                baseVerdict,
                fixture.Capture,
                fixture.P9,
                ActorId,
                H('a'),
                at));
    }

    return Task.CompletedTask;
}

void RebindFoundationRefreshPrompt(
    WorkReportStatisticRebuildJob job,
    string promptId)
{
    job.CandidatePromptId = promptId;
    job.GenerationId = StatRunDirectProjectionService
        .BuildFoundationRefreshGenerationId(
            job.DirectProjectionIdentityKey!,
            job.CandidateChainId!,
            promptId,
            job.SourceReportId!,
            job.SourceLifecycleEventKey!,
            job.PublicationScopeKey!,
            job.SourceMembershipSignature!);
    StatRunService.RefreshP10TrustedLifecycleOperationIntegrity(job);
}

void PromoteToFoundationRefreshV2(
    StatisticReconciliationRecheckCaptureBinding capture,
    WorkReportStatisticRebuildJob p9)
{
    var identity = StatRunDirectProjectionService
        .BuildFoundationRefreshCanonicalIdentity(
            p9.SourceReportId!,
            p9.SourceLifecycleEventKey!,
            p9.PublicationScopeKey!,
            p9.SourceMembershipSignature!);
    p9.DirectProjectionIdentityVersion = identity.Version;
    p9.DirectProjectionIdentityKey = identity.Key;
    p9.Id = identity.RunId;
    p9.DedupeKey = identity.DedupeKey;
    p9.ReceiptId = identity.ReceiptId;
    p9.CommandId = null;
    p9.GenerationId = StatRunDirectProjectionService
        .BuildFoundationRefreshGenerationId(
            identity.Key,
            p9.CandidateChainId!,
            p9.CandidatePromptId!,
            p9.SourceReportId!,
            p9.SourceLifecycleEventKey!,
            p9.PublicationScopeKey!,
            p9.SourceMembershipSignature!);

    capture.P9RunId = p9.Id;
    capture.P9ResultId = p9.Id;
    capture.P9GenerationId = p9.GenerationId;
    capture.P9GenerationHash = p9.GenerationHash!;
    capture.ActualCapturePlan.Api.OwnerResultId = p9.Id;
    capture.ActualCapturePlan.Export.ResultId = p9.Id;
    capture.ActualCapturePlan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(
            capture.ActualCapturePlan);
    capture.ActualCapturePlanSha256 =
        capture.ActualCapturePlan.PlanSha256;
    StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(capture);
    StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(
        capture);
    StatRunService.RefreshP10TrustedLifecycleOperationIntegrity(p9);
}
(StatisticReconciliationRun Run,
    StatisticReconciliationRecheckCaptureBinding Capture,
    WorkReportStatisticRebuildJob P9) RemediationFixture(
        StatisticReconciliationReview baseVerdict,
        char seed)
{
    var capture = CaptureBinding(seed);
    var eventIdentity =
        $"{capture.SourceReportId}\n{capture.SourceLifecycleEventKey}";
    var p9Id = StatisticReconciliationCanonicalJson.HashText(
        $"P9_LFC_RUN\n{eventIdentity}")[..24];
    capture.P9RunId = p9Id;
    capture.P9ResultId = p9Id;
    capture.ActualCapturePlan.Api.OwnerResultId = p9Id;
    capture.ActualCapturePlan.Export.ResultId = p9Id;
    capture.ActualCapturePlan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(
            capture.ActualCapturePlan);
    capture.ActualCapturePlanSha256 =
        capture.ActualCapturePlan.PlanSha256;
    StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(capture);
    StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(capture);
    var receiptId = StatisticReconciliationCanonicalJson.HashText(
        $"P9_LIFECYCLE_DIRECT_RECEIPT_V2\n{eventIdentity}");
    var p9 = new WorkReportStatisticRebuildJob
    {
        Id = p9Id,
        DedupeKey =
            $"p9-lfc:{capture.SourceReportId}:{capture.SourceLifecycleEventKey}",
        ReceiptId = receiptId,
        CommandId = "approved-lifecycle-command",
        ReceiptResponseHash = null,
        ReceiptAcceptedAtUtc = at,
        ActorUserId = ActorId,
        RequestedByUserId = ActorId,
        TenantUnitId = "b123456789abcdef01234567",
        RunKind = WorkReportStatisticRebuildJobRunKinds
            .LifecycleDirectProjection,
        CapabilityId = StatRunCapabilities.DirectFieldTableLabel,
        RouteId = StatRunRouteRegistry.LifecycleDirectProjector,
        ScopeType = "WORK_PERIOD_TEMPLATE",
        ScopeId = "7123456789abcdef01234567",
        ScopeKind = WorkReportStatisticRebuildJobScopeKinds.Bounded,
        WorkId = "7123456789abcdef01234567",
        WorkAssignmentId = "8123456789abcdef01234567",
        GenerationId = capture.P9GenerationId,
        GenerationHash = capture.P9GenerationHash,
        SourceReportId = capture.SourceReportId,
        SourcePayloadRevision = capture.SourcePayloadRevision,
        SourcePayloadHash = capture.SourcePayloadHash,
        SourceLifecycleRevision = capture.SourceLifecycleRevision,
        SourceLifecycleEventKey = capture.SourceLifecycleEventKey,
        SourceMembershipSignature = H('d'),
        DirectSourceRevision = 2,
        PublicationScopeKey = H('e'),
        DirectPublicationRevision = 1,
        IsCurrentPublication = true,
        SourceStatus = capture.SourceLifecycleStatus,
        ConfigId = "c123456789abcdef01234567",
        ConfigVersionId = "d123456789abcdef01234567",
        ConfigVersionNo = 1,
        ConfigRevision = 1,
        ConfigHash = H('e'),
        CatalogVersion = StatRunCapabilityActivation
            .RequiredCatalogVersion,
        CatalogRawSha256 = StatRunCapabilityActivation
            .PublishedCatalogRawSha256,
        CatalogSemanticSha256 = StatRunCapabilityActivation
            .PublishedCatalogSemanticSha256,
        SchemaRawSha256 = StatRunCapabilityActivation
            .PublishedSchemaRawSha256,
        SchemaSemanticSha256 = StatRunCapabilityActivation
            .PublishedSchemaSemanticSha256,
        StageLockSha256 = StatRunCapabilityActivation
            .PublishedSealStageLockRawSha256,
        CandidateChainId = StatRunCapabilityActivation.RequiredChainId,
        CandidatePromptId = StatRunCapabilityActivation.PublishedPromptId,
        DynamicFormFamilyId = "e123456789abcdef01234567",
        DynamicFormTemplateId = capture.DynamicFormVersionId,
        DynamicFormVersionNo = 1,
        DynamicFormSchemaHash = capture.DynamicFormSchemaHash,
        PeriodKey = capture.PeriodKey,
        PeriodInstanceKey = capture.PeriodInstanceKey,
        PeriodKind = capture.PeriodKind,
        PeriodStartUtc = capture.PeriodStartUtc,
        PeriodEndUtc = capture.PeriodEndUtc,
        FlowEffectiveStatus = "APPROVED",
        TotalReportCount = 1,
        Status = WorkReportStatisticRebuildJobStatuses.Completed,
        StateRevision = 3,
        IsActive = false,
        PublishedAtUtc = at,
        CompletedAtUtc = at,
        ComputedAtUtc = at,
        FreshnessState = WorkReportStatisticRebuildJobFreshnessStates.Fresh
    };
    StatRunService.RefreshP10TrustedLifecycleOperationIntegrity(p9);
    var run = new StatisticReconciliationRun
    {
        Id = RunId,
        WorkId = "7123456789abcdef01234567",
        ScopeAssignmentId = "8123456789abcdef01234567",
        Status = StatisticReconciliationRunStatuses.Mismatched,
        CurrentGenerationId = baseVerdict.ActualGenerationId,
        CurrentGenerationHash = baseVerdict.ActualGenerationSha256,
        P9GenerationHash = H('0'),
        SourcePayloadHash = H('0')
    };
    return (run, capture, p9);
}

StatisticReconciliationRecheckCaptureBinding CaptureBinding(char seed)
{
    var oid = $"{seed}123456789abcdef01234567";
    var plan = new StatisticReconciliationActualCapturePlan
    {
        BoundaryRegistryVersion =
            StatisticReconciliationActualCapturePlanIntegrity
                .BoundaryRegistryVersion,
        ActualConfigurationBundleSha256 = H(seed),
        Basic = new()
        {
            SnapshotId = oid,
            Mode = "FLOW_FINAL",
            ImmutableSelectorSha256 = H('1')
        },
        Advanced = new()
        {
            SectionId = "section",
            DayNodeIds = ["2123456789abcdef01234567"],
            MonthNodeIds = ["3123456789abcdef01234567"],
            YearNodeIds = ["4123456789abcdef01234567"],
            ImmutableSelectorSha256 = H('2')
        },
        Diff = new()
        {
            ResultId = "5123456789abcdef01234567",
            RunId = "6123456789abcdef01234567",
            ImmutableSelectorSha256 = H('3')
        },
        Api = new()
        {
            Surface = "DIRECT_FIELD",
            OwnerResultId = oid,
            ExpectedTotalRows = 0,
            PageSize = StatisticReconciliationActualCapturePlanIntegrity
                .ApiPageSize,
            PageCount = 1
        },
        Export = new()
        {
            ExportId = "export-id",
            ResultKind = "DIRECT",
            WorkId = "7123456789abcdef01234567",
            ScopeType = "ASSIGNMENT",
            ScopeId = "8123456789abcdef01234567",
            ResultId = oid,
            RequestSha256 = H('4'),
            AuthorizationSnapshotSha256 = H('5'),
            ContentSha256 = H('6'),
            ColumnManifestSha256 = H('7'),
            OwnerSemanticSha256 = H('8')
        }
    };
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    var binding = new StatisticReconciliationRecheckCaptureBinding
    {
        P9ResultId = oid,
        P9RunId = oid,
        P9GenerationId = H(seed),
        P9GenerationHash = H(seed),
        P9RunKind = WorkReportStatisticRebuildJobRunKinds
            .LifecycleDirectProjection,
        P9CapabilityId = StatRunCapabilities.DirectFieldTableLabel,
        P9RouteId = StatRunRouteRegistry.LifecycleDirectProjector,
        P9CandidateChainId = StatRunCapabilityActivation.RequiredChainId,
        P9CandidatePromptId = StatRunCapabilityActivation.PublishedPromptId,
        SourceReportId = "9123456789abcdef01234567",
        SourcePayloadRevision = 2,
        SourcePayloadHash = H('9'),
        SourceLifecycleRevision = 2,
        SourceLifecycleEventKey = H('a'),
        SourceLifecycleHash = H('b'),
        SourceLifecycleStatus = "APPROVED",
        DynamicFormVersionId = "a123456789abcdef01234567",
        DynamicFormSchemaHash = H('c'),
        P8ConfigBundleHash = H('d'),
        P9CatalogVersion = "v1",
        P9CatalogRawSha256 = H('e'),
        P9CatalogSemanticSha256 = H('f'),
        P9SchemaRawSha256 = H('0'),
        P9SchemaSemanticSha256 = H('1'),
        P9StageLockSha256 = H('2'),
        PeriodKey = "2026",
        PeriodInstanceKey = "2026",
        PeriodKind = "YEAR",
        PeriodStartUtc = at,
        PeriodEndUtc = at.AddYears(1),
        TimeAxis = "UTC_GREGORIAN",
        ActualCapturePlan = plan,
        ActualCapturePlanSha256 = plan.PlanSha256,
        ActualConfigurationBundleSha256 = H(seed)
    };
    StatisticReconciliationRecheckCaptureBindingCanonical.Refresh(binding);
    return binding;
}

StatisticReconciliationFinalVerdictRequest VerdictRequest(
    RemediationLayerKind kind,
    string actualGenerationId,
    string actualGenerationHash)
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    var permission = classifier.CreatePermissionEvidence(
        StatisticReconciliationRootCausePermissionStates.Authorized,
        H('9'));
    var root = new StatisticReconciliationRootCauseRequest(
        ComparisonBinding, permission, Fresh(), Layers(kind));
    return new(
        RunId,
        ComparisonBinding,
        H('6'),
        H('7'),
        actualGenerationId,
        actualGenerationHash,
        H('5'),
        permission,
        root,
        StatisticReconciliationFinalVerdictFailureKinds.None,
        null);
}

ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> Layers(
    RemediationLayerKind kind)
    => [.. Enumerable.Range(0, 8).Select(index => Layer(index,
        index == 1 ? kind : RemediationLayerKind.Zero))];

StatisticReconciliationRootCauseLayerEvidence Layer(
    int ordinal,
    RemediationLayerKind kind)
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    if (kind is RemediationLayerKind.Missing or RemediationLayerKind.Extra)
    {
        var code = kind == RemediationLayerKind.Missing
            ? StatisticReconciliationIdentityDeltaCodes.MissingIdentity
            : StatisticReconciliationIdentityDeltaCodes.ExtraIdentity;
        var identity = classifier.CreateIdentityEvidence(
            code, H('1'), H('2'));
        return classifier.CreateLayerEvidence(
            ordinal,
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            ComparisonBinding,
            true, H('b'), H('c'), H('d'), 1, 1,
            kind == RemediationLayerKind.Missing ? 1 : 0,
            kind == RemediationLayerKind.Extra ? 1 : 0,
            0,
            StatisticReconciliationRootCauseLayerDeltaStates.Nonzero,
            StatisticReconciliationRootCauseAttributionStates.Proven,
            null, null, [identity]);
    }
    return classifier.CreateLayerEvidence(
        ordinal,
        StatisticReconciliationRootCauseLayers.Ordered[ordinal],
        ComparisonBinding,
        true, H('b'), H('c'), H('d'), 1, 0, 0, 0, 0,
        "ZERO_DELTA", "NOT_REQUIRED", null, null, []);
}

StatisticReconciliationFreshnessAssessment Fresh()
{
    var pins = new StatisticReconciliationActualFreshnessPins(
        new StatisticReconciliationComparisonBindingPins(
            H('1'), H('2'), H('3'), H('4'), H('5')),
        H('6'), H('7'), H('8'));
    return new StatisticReconciliationFreshnessEvaluator().Evaluate(
        new(pins.Binding, pins, pins, true, true, true));
}

static string H(char value) => new(value, 64);

static void Require(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

static T Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T error) { return error; }
    throw new InvalidOperationException($"EXPECTED_{typeof(T).Name}");
}

static async Task ExpectCodeAsync(Func<Task> action, string code)
{
    try { await action(); }
    catch (StatisticReconciliationFinalVerdictException error)
    {
        Require(error.Code == code, "FAILURE_CODE");
        return;
    }
    throw new InvalidOperationException("EXPECTED_VERDICT_FAILURE");
}

enum RemediationLayerKind
{
    Zero,
    Missing,
    Extra
}

sealed class MemoryVerdictBackend : IStatisticReconciliationReviewBackend
{
    private readonly Dictionary<string, StatisticReconciliationReview> _rows =
        new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    public Task<StatisticReconciliationReview?> ReadAsync(
        string reconciliationId,
        string verdictGenerationId,
        CancellationToken ct = default)
        => Task.FromResult(_rows.TryGetValue(verdictGenerationId,
                out var value) && value.ReconciliationId == reconciliationId
            ? value
            : null);

    public Task<IReadOnlyList<StatisticReconciliationReview>>
        ReadLineageAsync(
            string reconciliationId,
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StatisticReconciliationReview>>(
            _rows.Values.Where(value =>
                    value.ReconciliationId == reconciliationId)
                .OrderBy(value => value.CreatedAtUtc)
                .ThenBy(value => value.VerdictGenerationId,
                    StringComparer.Ordinal)
                .ToArray());

    public Task<bool> TryAppendAsync(
        StatisticReconciliationReview review,
        CancellationToken ct = default)
    {
        if (_rows.ContainsKey(review.VerdictGenerationId) ||
            review.SupersedesVerdictGenerationId is not null &&
            _rows.Values.Any(value =>
                value.ReconciliationId == review.ReconciliationId &&
                value.SupersedesVerdictGenerationId ==
                    review.SupersedesVerdictGenerationId))
            return Task.FromResult(false);
        _rows.Add(review.VerdictGenerationId, review);
        Writes++;
        return Task.FromResult(true);
    }
}
