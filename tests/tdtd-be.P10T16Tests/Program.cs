using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

var tests = new (string Name, Func<Task> Run)[]
{
    ("P10-SOURCE-01", Tests.DraftSubmittedZero),
    ("P10-SOURCE-02", Tests.ApprovedEffectiveLockedCurrent),
    ("P10-SOURCE-03", Tests.RecalledReturnedZero),
    ("P10-SOURCE-04", Tests.TerminatedInvalidatedSupersededZero),
    ("P10-SOURCE-05", Tests.AmbiguousApprovedFails),
    ("P10-SOURCE-06", Tests.DefaultExclude),
    ("P10-SOURCE-07", P10ExpectedCases.IncludeAndReplay),
    ("P10-SOURCE-08", Tests.AmbiguousContributionFails),
    ("P10-TYPED-01", Tests.CountsMeanAndStates),
    ("P10-TYPED-02", Tests.AllTypedValueFamilies),
    ("P10-TYPED-03", Tests.NoNumericStringCoercion),
    ("P10-TYPED-04", Tests.DecimalOverflowFails),
    ("P10-TYPED-05", Tests.DeterministicTypedHash),
    ("P10-TYPED-06", P10ExpectedCases.OrderedAndUnordered),
    ("P10-TYPED-07", P10ExpectedCases.MissingNullEmptyDistinct),
    ("P10-TYPED-08", P10ExpectedCases.MeanUsesNumericValueCount),
    ("P10-ESUM-01", Tests.DirectIdentities),
    ("P10-ESUM-02", Tests.BasicSixScopes),
    ("P10-ESUM-03", Tests.AdvancedThreeGrains),
    ("P10-ESUM-04", Tests.DiffThreeKinds),
    ("P10-ESUM-05", Tests.AppendCommitLast),
    ("P10-ESUM-06", Tests.ExactReplay),
    ("P10-ESUM-07", P10ExpectedCases.ConflictAndIncompleteFailClosed),
    ("P10-ESUM-08", Tests.CompleteLineageRecords),
    ("P10-HASH-01", P10ExpectedCases.CleanRecomputeDeterministic),
    ("P10-HASH-02", P10ExpectedCases.SourceConfigRuntimeChangesHashes),
    ("P10-HASH-03", P10ExpectedCases.IndependentDependencyGraph),
    ("P10-HASH-04", P10ExpectedCases.WrongLedgerDetectedRejectedAndRecovered),
    ("P10-PROD-01", Tests.LockedNonFlowPolicyFailClosed),
    ("P10-PROD-02", Tests.AuthoritativeRuntimePinTamperFails),
    ("P10-PROD-03", Tests.ExactGenerationBindingTamperFails),
    ("P10-PROD-04", Tests.StableMembershipTupleTamperFails),
    ("P10-PROD-05", Tests.DiffTransitionLegProjectionIsExact),
    ("P10-PROD-06", Tests.TableBlockOneCellRebuilt),
    ("P10-PROD-07", Tests.TableBlockTamperFails),
    ("P10-PROD-08", Tests.TableBlockOmissionFails),
    ("P10-PROD-09", NonFlowRuntimePinCases.LockedIncludeUsesNonFlowShape),
    ("P10-PROD-10", Tests.FlowSourceSkipsNonFlowPolicyParser)
};

const int expectedCaseCount = 38;
if (tests.Length != expectedCaseCount ||
    tests.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() !=
    expectedCaseCount)
    throw new InvalidOperationException("P10-EXPECTED exact registry drifted.");

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {error.GetType().Name}: {error.Message}");
        Environment.ExitCode = 1;
        return;
    }
}

Console.WriteLine($"P10_T16_EXPECTED_OK cases={passed} next=P10-03");

internal static class Tests
{
    internal static Task DraftSubmittedZero()
    {
        foreach (var status in new[]
                 {
                     StatisticReconciliationExpectedLifecycleStatuses.Draft,
                     StatisticReconciliationExpectedLifecycleStatuses.Submitted
                 })
        {
            var plan = Fixture.Plan(
                Fixture.MinimalConfig(),
                [Fixture.Source("s1", "{\"value\":1}", status)]);
            Equal(0, plan.ApprovedEffectiveSources.Length, status);
            Equal(0, plan.IncludedSources.Length, status);
            Equal(
                StatisticReconciliationExpectedSourceDecisionReasons.DraftOrUnapproved,
                plan.SourceDecisions.Single().ReasonCode,
                status);
        }
        var rejectedOne = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}",
                StatisticReconciliationExpectedLifecycleStatuses.Draft)]);
        var rejectedTwo = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":2}",
                StatisticReconciliationExpectedLifecycleStatuses.Draft)]);
        True(rejectedOne.SourceSetSha256 != rejectedTwo.SourceSetSha256,
            "rejected source payload hash is source-set bound");
        return Task.CompletedTask;
    }

    internal static Task RecalledReturnedZero()
    {
        foreach (var status in new[]
                 {
                     StatisticReconciliationExpectedLifecycleStatuses.Recalled,
                     StatisticReconciliationExpectedLifecycleStatuses.Returned
                 })
        {
            var plan = Fixture.Plan(
                Fixture.MinimalConfig(),
                [Fixture.Source("s1", "{\"value\":1}", status)]);
            Equal(0, plan.ApprovedEffectiveSources.Length, status);
            Equal(
                StatisticReconciliationExpectedSourceDecisionReasons.RecalledOrReturned,
                plan.SourceDecisions.Single().ReasonCode,
                status);
        }
        return Task.CompletedTask;
    }

    internal static Task TerminatedInvalidatedSupersededZero()
    {
        var cases = new[]
        {
            (StatisticReconciliationExpectedLifecycleStatuses.Terminated,
                StatisticReconciliationExpectedRuntimeDispositions.Terminated,
                StatisticReconciliationExpectedSourceDecisionReasons.Terminated),
            (StatisticReconciliationExpectedLifecycleStatuses.Invalidated,
                StatisticReconciliationExpectedRuntimeDispositions.Invalidated,
                StatisticReconciliationExpectedSourceDecisionReasons.Invalidated),
            (StatisticReconciliationExpectedLifecycleStatuses.Superseded,
                StatisticReconciliationExpectedRuntimeDispositions.Superseded,
                StatisticReconciliationExpectedSourceDecisionReasons.Superseded)
        };
        foreach (var item in cases)
        {
            var plan = Fixture.Plan(
                Fixture.MinimalConfig(),
                [Fixture.Source("s1", "{\"value\":1}", item.Item1,
                    runtimeDisposition: item.Item2)]);
            Equal(0, plan.ApprovedEffectiveSources.Length, item.Item1);
            Equal(item.Item3, plan.SourceDecisions.Single().ReasonCode, item.Item1);
        }
        return Task.CompletedTask;
    }

    internal static Task ApprovedEffectiveLockedCurrent()
    {
        var plan = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}")],
            [Fixture.Contribution("s1", StatisticReconciliationExpectedContributionPolicies.Include)]);
        Equal(1, plan.ApprovedEffectiveSources.Length, "approved");
        Equal(1, plan.IncludedSources.Length, "included");
        Equal(
            StatisticReconciliationExpectedSourceDecisionReasons.Included,
            plan.SourceDecisions.Single().ReasonCode,
            "included reason");
        return Task.CompletedTask;
    }

    internal static Task AmbiguousApprovedFails()
    {
        ExpectReason(
            StatisticReconciliationExpectedSourcePlanningFailureReasons.LifecycleAmbiguous,
            () => Fixture.Plan(
                Fixture.MinimalConfig(),
                [
                    Fixture.Source("s1", "{\"value\":1}", payloadRevision: 1,
                        lifecycleRevision: 1),
                    Fixture.Source("s1", "{\"value\":2}", payloadRevision: 2,
                        lifecycleRevision: 2)
                ]));
        return Task.CompletedTask;
    }

    internal static Task LockedNonFlowPolicyFailClosed()
    {
        var report = new WorkAssignmentReport
        {
            Id = "report-policy-1",
            LifecycleRevision = 2
        };
        Equal<StatisticReconciliationExpectedMongoSnapshotReader
            .LockedNonFlowContribution?>(
                null,
                StatisticReconciliationExpectedMongoSnapshotReader
                    .ResolveLockedNonFlowContribution(report),
                "missing policy defaults exclude");

        report.CumulativeContributionPolicyJson = "{";
        ExpectSnapshotInvalid(
            () => StatisticReconciliationExpectedMongoSnapshotReader
                .ResolveLockedNonFlowContribution(report),
            "NON_FLOW_CONTRIBUTION_POLICY_JSON_INVALID");
        report.CumulativeContributionPolicyJson = JsonSerializer.Serialize(new
        {
            version = "P10_NON_FLOW_CONTRIBUTION_POLICY_V1",
            locked = false,
            policy = StatisticReconciliationExpectedContributionPolicies.Include,
            ownerId = "policy-owner-1",
            revision = 2,
            policySha256 = Fixture.Sha("wrong")
        });
        ExpectSnapshotInvalid(
            () => StatisticReconciliationExpectedMongoSnapshotReader
                .ResolveLockedNonFlowContribution(report),
            "NON_FLOW_CONTRIBUTION_POLICY_INVALID");

        var includeSha =
            StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                "P10_EXPECTED_NON_FLOW_LOCKED_POLICY_V1",
                [report.Id, "policy-owner-1", "2",
                 StatisticReconciliationExpectedContributionPolicies.Include]);
        report.CumulativeContributionPolicyJson = JsonSerializer.Serialize(new
        {
            version = "P10_NON_FLOW_CONTRIBUTION_POLICY_V1",
            locked = true,
            policy = StatisticReconciliationExpectedContributionPolicies.Include,
            ownerId = "policy-owner-1",
            revision = 2,
            policySha256 = includeSha
        });
        var included = StatisticReconciliationExpectedMongoSnapshotReader
            .ResolveLockedNonFlowContribution(report);
        Equal(StatisticReconciliationExpectedContributionPolicies.Include,
            included!.Policy, "explicit include");

        report.CumulativeContributionPolicyJson = JsonSerializer.Serialize(new
        {
            version = "P10_NON_FLOW_CONTRIBUTION_POLICY_V1",
            locked = true,
            policy = StatisticReconciliationExpectedContributionPolicies.Include,
            ownerId = "policy-owner-1",
            revision = 2,
            policySha256 = Fixture.Sha("tampered")
        });
        ExpectSnapshotInvalid(
            () => StatisticReconciliationExpectedMongoSnapshotReader
                .ResolveLockedNonFlowContribution(report),
            "NON_FLOW_CONTRIBUTION_POLICY_HASH_MISMATCH");
        return Task.CompletedTask;
    }

    internal static Task FlowSourceSkipsNonFlowPolicyParser()
    {
        var report = new WorkAssignmentReport
        {
            Id = "flow-report-policy-1",
            LifecycleRevision = 2,
            CumulativeContributionPolicyJson = JsonSerializer.Serialize(new
            {
                defaultMode = "EXCLUDE",
                rules = new[]
                {
                    new
                    {
                        targetKind = "FIELD",
                        targetKey = "number-field-1",
                        mode = "EXCLUDE",
                        source = "DYNAMIC_FLOW_MAPPING",
                        mappingId = "mapping-1"
                    }
                }
            })
        };

        True(
            StatisticReconciliationExpectedMongoSnapshotReader
                .ResolveSourceInclude(
                    report,
                    requestedRuntimeKind:
                        StatisticReconciliationExpectedRuntimeKinds.Flow,
                    hasFlowRuntime: true,
                    lockedFlowPolicy:
                        DynamicFlowContributionPolicyContract.Include,
                    hasFlowMapping: true),
            "flow source uses locked flow policy and mapping");

        var reachedNonFlowParser = false;
        ExpectSnapshotInvalid(
            () =>
            {
                StatisticReconciliationExpectedMongoSnapshotReader
                    .RequireFlowRuntimePin(
                        StatisticReconciliationExpectedRuntimeKinds.Flow,
                        hasFlowRuntime: false);
                reachedNonFlowParser = true;
                _ = StatisticReconciliationExpectedMongoSnapshotReader
                    .ResolveLockedNonFlowContribution(report);
            },
            "FLOW_RUNTIME_PIN_REQUIRED");
        Equal(
            false,
            reachedNonFlowParser,
            "missing flow runtime stops before non-flow parser");

        Equal(
            false,
            StatisticReconciliationExpectedMongoSnapshotReader
                .ResolveSourceInclude(
                    report,
                    requestedRuntimeKind:
                        StatisticReconciliationExpectedRuntimeKinds.Flow,
                    hasFlowRuntime: false,
                    lockedFlowPolicy: null,
                    hasFlowMapping: false),
            "flow request without runtime does not parse report policy");

        ExpectSnapshotInvalid(
            () => StatisticReconciliationExpectedMongoSnapshotReader
                .ResolveSourceInclude(
                    report,
                    requestedRuntimeKind:
                        StatisticReconciliationExpectedRuntimeKinds.NonFlow,
                    hasFlowRuntime: false,
                    lockedFlowPolicy: null,
                    hasFlowMapping: false),
            "NON_FLOW_CONTRIBUTION_POLICY_INVALID");
        return Task.CompletedTask;
    }

    internal static Task AuthoritativeRuntimePinTamperFails()
    {
        var context = Fixture.ProductionFlowContext();
        var baseline =
            StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .Create(
                    runtimeKind: context.RuntimeKind,
                    flowTemplateVersionId: context.FlowTemplateVersionId,
                    flowPayloadSha256: context.FlowPayloadSha256,
                    flowInstanceId: context.FlowInstanceId,
                    flowBranchId: "branch-1",
                    flowStepId: "step-1",
                    flowStepInstanceId: "step-instance-1",
                    flowStepRevision: 4,
                    flowAttemptNo: 2,
                    executionEpochId: context.ExecutionEpochId,
                    executionEpoch: 3,
                    currentExecutionEpochId: context.ExecutionEpochId,
                    currentExecutionEpoch: 3,
                    executionEpochRevision: 7,
                    isCanonicalEpoch: true,
                    approvalCommandId: "approval-command-1",
                    approvalEventKey: Fixture.Sha("approval-event"),
                    mappingReceiptId: "mapping-receipt-1",
                    mappingProvenanceId: "mapping-provenance-1",
                    mappingProvenanceSha256: Fixture.Sha("mapping-provenance"),
                    mappingResultSemanticSha256: Fixture.Sha("mapping-result"),
                    mappingResultPayloadRevision: 5,
                    mappingResultPayloadSha256: Fixture.Sha("mapping-payload"),
                    mappingFlowVersionId: "mapping-flow-version-1",
                    mappingFlowVersionNo: 2,
                    mappingFlowPayloadSha256: Fixture.Sha("mapping-flow-payload"),
                    mappingLocked: true,
                    configVersionId: "stat-config-version-1",
                    configSha256: Fixture.Sha("stat-config"),
                    membershipSignatureSha256:
                        context.SourceOwnerMembershipSha256!,
                    contributionPolicy:
                        StatisticReconciliationExpectedContributionPolicies.Include,
                    contributionPolicySha256: Fixture.Sha("include-policy"),
                    contributionProvenanceId: "mapping-provenance-1",
                    contributionProvenanceSha256:
                        Fixture.Sha("mapping-provenance"),
                    lifecycleOwnerSha256: Fixture.Sha("lifecycle-owner"));
        StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
            .Validate(baseline, context);

        var canonicalFalse = Rehash(baseline with { IsCanonicalEpoch = false });
        ExpectReason(
            StatisticReconciliationExpectedSourcePlanningFailureReasons
                .LifecycleCandidateInvalid,
            () => StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .Validate(canonicalFalse, context));
        var currentDrift = Rehash(baseline with { CurrentExecutionEpoch = 4 });
        ExpectReason(
            StatisticReconciliationExpectedSourcePlanningFailureReasons
                .LifecycleCandidateInvalid,
            () => StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .Validate(currentDrift, context));
        var attemptDrift = Rehash(baseline with { FlowAttemptNo = 0 });
        ExpectReason(
            StatisticReconciliationExpectedSourcePlanningFailureReasons
                .LifecycleCandidateInvalid,
            () => StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .Validate(attemptDrift, context));
        var mappingTamper = baseline with
        {
            MappingResultPayloadSha256 = Fixture.Sha("one-bit-drift")
        };
        ExpectReason(
            StatisticReconciliationExpectedSourcePlanningFailureReasons
                .LifecycleCandidateInvalid,
            () => StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .Validate(mappingTamper, context));
        return Task.CompletedTask;
    }

    internal static async Task ExactGenerationBindingTamperFails()
    {
        var generation = Fixture.TypedGeneration();
        var backend = new FakeObservationBackend();
        var appended = await Fixture.Store(backend).AppendGenerationAsync(
            generation,
            Fixture.UtcNow);
        var commitDocument = backend.Documents.Values.Single(item =>
            item.RecordKind ==
                StatisticReconciliationObservationRecordKinds.GenerationCommit);
        var commit = commitDocument.Commit!;
        var exact = appended.ExactBinding;
        StatisticReconciliationExpectedMongoProjectionInputReader
            .RequireExactBinding(commitDocument, commit, exact);
        try
        {
            StatisticReconciliationExpectedMongoProjectionInputReader
                .RequireExactBinding(
                    commitDocument,
                    commit,
                    exact with { DocumentCount = exact.DocumentCount + 1 });
            throw new InvalidOperationException("Expected exact binding tamper failure.");
        }
        catch (StatisticReconciliationExpectedProjectionInputException error)
            when (error.Reason == "EXACT_GENERATION_BINDING_MISMATCH")
        {
        }
        try
        {
            StatisticReconciliationExpectedMongoProjectionInputReader
                .RequireExactBinding(
                    commitDocument,
                    commit,
                    exact with
                    {
                        ManifestSha256 = Fixture.Sha("manifest-tamper")
                    });
            throw new InvalidOperationException(
                "Expected manifest binding tamper failure.");
        }
        catch (StatisticReconciliationExpectedProjectionInputException error)
            when (error.Reason == "EXACT_GENERATION_BINDING_MISMATCH")
        {
        }
    }


    internal static async Task StableMembershipTupleTamperFails()
    {
        var backend = new FakeObservationBackend();
        await Fixture.Store(backend).AppendGenerationAsync(
            Fixture.TypedGeneration(),
            Fixture.UtcNow);
        var sourceDocument = backend.Documents.Values.First(item =>
            item.RecordKind ==
                StatisticReconciliationObservationRecordKinds.SourceDecision);
        var source = sourceDocument.SourceDecision!;
        source.WorkAssignmentId += "-tampered";
        try
        {
            _ = StatisticReconciliationExpectedObservationIntegrity
                .BuildDocumentSemanticSha256(sourceDocument);
            throw new InvalidOperationException(
                "Expected stable membership tuple tamper failure.");
        }
        catch (StatisticReconciliationExpectedObservationIntegrityException error)
            when (error.Reason == "SOURCE_STABLE_IDENTITY_MISMATCH")
        {
        }
    }

    internal static Task DiffTransitionLegProjectionIsExact()
    {
        var changed = StatisticReconciliationExpectedMongoProjectionInputReader
            .TransitionLegs(
                StatisticReconciliationExpectedMetricFamilies.Diff,
                StatisticReconciliationExpectedDifferenceOperations.Changed);
        Equal(3, changed.Length, "non-subtract DIFF transition legs");
        True(!changed.Contains(
                StatisticReconciliationExpectedTransitionLegs.Delta),
            "non-subtract DIFF omits delta");
        var subtract = StatisticReconciliationExpectedMongoProjectionInputReader
            .TransitionLegs(
                StatisticReconciliationExpectedMetricFamilies.Diff,
                StatisticReconciliationExpectedDifferenceOperations.Subtract);
        Equal(4, subtract.Length, "subtract DIFF transition legs");
        True(subtract.Contains(
                StatisticReconciliationExpectedTransitionLegs.Delta),
            "subtract DIFF carries delta");
        var direct = StatisticReconciliationExpectedMongoProjectionInputReader
            .TransitionLegs(
                StatisticReconciliationExpectedMetricFamilies.Direct,
                null);
        Equal(StatisticReconciliationExpectedTransitionLegs.None,
            direct.Single(), "non-DIFF transition leg");
        return Task.CompletedTask;
    }

    internal static Task TableBlockOneCellRebuilt()
    {
        var (payload, block) = OneCellTablePayload();
        var owner = StatisticReconciliationExpectedAuthoritativePayloadOwner
            .BuildValidated(payload, [block]);
        Equal(1, owner.TableBlockCount, "table block count");
        True(owner.TableBlockManifestSha256.Length == 64,
            "table block manifest");
        using var document = JsonDocument.Parse(owner.CanonicalPayloadJson);
        var blocks = document.RootElement.GetProperty("tableValues")
            .GetProperty("blocks");
        Equal(1, blocks.GetArrayLength(), "rebuilt table block count");
        Equal(7, blocks[0].GetProperty("cells")[0]
            .GetProperty("value").GetInt32(), "rebuilt one cell");
        return Task.CompletedTask;
    }

    internal static Task TableBlockTamperFails()
    {
        var (payload, block) = OneCellTablePayload();
        block.ValuesJson = block.ValuesJson.Replace(
            "\"value\":7",
            "\"value\":8",
            StringComparison.Ordinal);
        block.SizeBytes = Encoding.UTF8.GetByteCount(block.ValuesJson);
        ExpectPayloadFailure(
            () => StatisticReconciliationExpectedAuthoritativePayloadOwner
                .BuildValidated(payload, [block]),
            "TABLE_BLOCK_HASH_MISMATCH");
        return Task.CompletedTask;
    }

    internal static Task TableBlockOmissionFails()
    {
        var (payload, _) = OneCellTablePayload();
        ExpectPayloadFailure(
            () => StatisticReconciliationExpectedAuthoritativePayloadOwner
                .BuildValidated(payload, []),
            "PAYLOAD_OWNER_HASH_MISMATCH");
        return Task.CompletedTask;
    }

    private static (WorkReportPayload Payload, WorkReportTableValue Block)
        OneCellTablePayload()
    {
        const string rootJson = "{\"blocks\":[]}";
        const string blockJson =
            "{\"blockId\":\"block-1\",\"tableMode\":\"FIXED_GRID\"," +
            "\"cells\":[{\"r\":0,\"c\":0,\"value\":7}]}";
        var blockHash = RawSha(blockJson);
        var payloadHash = WorkReportPayloadHash.Compute(
            "[]",
            null,
            rootJson,
            null,
            [new WorkReportPayloadBlockHash("block-1", 0, blockHash)]);
        var payload = new WorkReportPayload
        {
            Id = "payload-1",
            ReportId = "report-1",
            PayloadRevision = 3,
            Values1DJson = "[]",
            TableValuesRootJson = rootJson,
            PayloadHash = payloadHash,
            PayloadSizeBytes = Encoding.UTF8.GetByteCount("[]") +
                Encoding.UTF8.GetByteCount(rootJson),
            Status = WorkReportPayloadStatus.Ready,
            IsDeleted = false
        };
        var block = new WorkReportTableValue
        {
            Id = "table-block-1",
            ReportId = payload.ReportId,
            PayloadRevision = payload.PayloadRevision,
            BlockId = "block-1",
            BlockOrder = 0,
            TableMode = "FIXED_GRID",
            ValuesJson = blockJson,
            RowCount = 1,
            ColumnCount = 1,
            SizeBytes = Encoding.UTF8.GetByteCount(blockJson),
            PayloadHash = blockHash,
            Status = WorkReportPayloadStatus.Ready,
            IsDeleted = false
        };
        return (payload, block);
    }

    private static string RawSha(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void ExpectPayloadFailure(Action action, string reason)
    {
        try
        {
            action();
            throw new InvalidOperationException(
                $"Expected authoritative payload failure {reason}.");
        }
        catch (StatisticReconciliationExpectedAuthoritativePayloadException error)
            when (error.Reason == reason)
        {
        }
    }
    private static StatisticReconciliationExpectedAuthoritativeRuntimePin Rehash(
        StatisticReconciliationExpectedAuthoritativeRuntimePin value)
        => value with
        {
            RuntimeSemanticSha256 =
                StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                    .BuildSemanticSha256(value)
        };

    private static void ExpectSnapshotInvalid(Action action, string messagePart)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected {messagePart}.");
        }
        catch (StatisticReconciliationExpectedLedgerInputException error)
            when (error.Reason ==
                      StatisticReconciliationExpectedSourcePlanningFailureReasons
                          .SnapshotInvalid &&
                  error.Message.Contains(messagePart, StringComparison.Ordinal))
        {
        }
    }
    internal static Task DefaultExclude()
    {
        var plan = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}")]);
        Equal(1, plan.ApprovedEffectiveSources.Length, "approved");
        Equal(0, plan.IncludedSources.Length, "default exclude");
        var decision = plan.SourceDecisions.Single();
        Equal(StatisticReconciliationExpectedContributionPolicies.Exclude,
            decision.ContributionPolicy, "policy");
        Equal(StatisticReconciliationExpectedSourceDecisionReasons.ContributionDefaultExclude,
            decision.ReasonCode, "reason");
        return Task.CompletedTask;
    }

    internal static Task IncludeExactlyOnce()
    {
        var contribution = Fixture.Contribution(
            "s1",
            StatisticReconciliationExpectedContributionPolicies.Include);
        var plan = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}")],
            [contribution]);
        var included = plan.IncludedSources.Single();
        Equal(contribution.VersionId, included.Contribution.VersionId, "version");
        Equal(contribution.ProvenanceSha256,
            included.Contribution.ProvenanceSha256, "provenance");
        return Task.CompletedTask;
    }

    internal static Task ContributionReplayDeduped()
    {
        var contribution = Fixture.Contribution(
            "s1",
            StatisticReconciliationExpectedContributionPolicies.Include);
        var plan = Fixture.Plan(
            Fixture.MinimalConfig(),
            [Fixture.Source("s1", "{\"value\":1}")],
            [contribution, contribution]);
        Equal(1, plan.IncludedSources.Length, "replayed contribution");
        return Task.CompletedTask;
    }

    internal static Task AmbiguousContributionFails()
    {
        ExpectReason(
            StatisticReconciliationExpectedSourcePlanningFailureReasons.ContributionAmbiguous,
            () => Fixture.Plan(
                Fixture.MinimalConfig(),
                [Fixture.Source("s1", "{\"value\":1}")],
                [
                    Fixture.Contribution("s1",
                        StatisticReconciliationExpectedContributionPolicies.Include),
                    Fixture.Contribution("s1",
                        StatisticReconciliationExpectedContributionPolicies.Exclude,
                        revision: 2)
                ]));
        return Task.CompletedTask;
    }

    internal static Task CountsMeanAndStates()
    {
        var generation = Fixture.TypedGeneration();
        var atoms = generation.Atoms
            .Where(item => item.Identity.MetricId == "number")
            .ToArray();
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.ReportCount, "2", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.RowCount, "5", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.NumericValueCount, "3", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.Sum, "60", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.Mean, "20", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.Null, "1", 2, 5, 3);
        Atom(atoms, StatisticReconciliationExpectedAtomKinds.Empty, "1", 2, 5, 3);
        return Task.CompletedTask;
    }

    internal static Task AllTypedValueFamilies()
    {
        var generation = Fixture.TypedGeneration();
        HasValue(generation, "bucket", "S:A");
        HasValue(generation, "bucket", "N:2");
        HasValue(generation, "date", "YEAR:2026");
        HasValue(generation, "date", "MONTH:2026-08");
        HasValue(generation, "fullDate", "DAY:2026-08-10");
        HasValue(generation, "period", "MONTH:2026-08");
        HasValue(generation, "boolean", "true");
        HasValue(generation, "enum", "OPEN");
        HasValue(generation, "tags", "[\"a\",\"b\"]");
        HasValue(generation, "text", "hello");
        var missing = generation.Atoms.Single(item =>
            item.Identity.MetricId == "missing" &&
            item.AtomKind == StatisticReconciliationExpectedAtomKinds.Missing);
        Equal(2L, missing.OccurrenceCount, "missing count");
        return Task.CompletedTask;
    }

    internal static Task NoNumericStringCoercion()
    {
        var plan = Fixture.Plan(
            Fixture.NumberOnlyConfig(),
            [Fixture.Source("s1", "{\"nums\":[\"10\"]}")],
            [Fixture.Contribution("s1", StatisticReconciliationExpectedContributionPolicies.Include)]);
        var compiler = new StatisticReconciliationExpectedTypedCompiler(
            new StatisticReconciliationExpectedMetricIdentityCompiler());
        ExpectReason(
            StatisticReconciliationExpectedTypedFailureReasons.NumericInvalid,
            () => compiler.Compile(plan, Fixture.CatalogPins()));
        return Task.CompletedTask;
    }

    internal static Task DecimalOverflowFails()
    {
        const string maximum = "79228162514264337593543950335";
        var plan = Fixture.Plan(
            Fixture.NumberOnlyConfig(),
            [Fixture.Source("s1", $"{{\"nums\":[{maximum},{maximum}]}}")],
            [Fixture.Contribution("s1", StatisticReconciliationExpectedContributionPolicies.Include)]);
        var compiler = new StatisticReconciliationExpectedTypedCompiler(
            new StatisticReconciliationExpectedMetricIdentityCompiler());
        ExpectReason(
            StatisticReconciliationExpectedTypedFailureReasons.NumericInvalid,
            () => compiler.Compile(plan, Fixture.CatalogPins()));
        return Task.CompletedTask;
    }

    internal static Task DeterministicTypedHash()
    {
        var first = Fixture.TypedGeneration();
        var second = Fixture.TypedGeneration(reverseSources: true);
        Equal(first.SourceSetSha256, second.SourceSetSha256, "source hash");
        Equal(first.TypedSemanticSha256, second.TypedSemanticSha256, "typed hash");
        Equal(first.GenerationId, second.GenerationId, "generation id");
        return Task.CompletedTask;
    }

    internal static Task DirectIdentities()
    {
        var compiler = new StatisticReconciliationExpectedMetricIdentityCompiler();
        var field = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Direct,
            StatisticReconciliationExpectedMetricKinds.Field,
            "m-field", "MONTH:2026-08", fieldId: "f"));
        var table = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Direct,
            StatisticReconciliationExpectedMetricKinds.Table,
            "m-table", "MONTH:2026-08", tableId: "t"));
        var label = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Direct,
            StatisticReconciliationExpectedMetricKinds.RowLabel,
            "m-label", "MONTH:2026-08", tableId: "t", rowId: "r", labelId: "l"));
        Equal(3, new[] { field, table, label }
            .Select(item => item.IdentitySha256).Distinct().Count(), "direct identity count");
        return Task.CompletedTask;
    }

    internal static Task BasicSixScopes()
    {
        var compiler = new StatisticReconciliationExpectedMetricIdentityCompiler();
        var hashes = StatisticReconciliationExpectedBasicScopes.All
            .Select(scope => compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Basic,
                StatisticReconciliationExpectedMetricKinds.Field,
                "m-basic", "MONTH:2026-08", fieldId: "f", scopeKind: scope,
                scopeId: "scope-" + scope))
                .IdentitySha256)
            .ToArray();
        Equal(6, hashes.Distinct().Count(), "basic scopes");
        var branchA = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Basic,
            StatisticReconciliationExpectedMetricKinds.Field,
            "m-basic-branch", "MONTH:2026-08", fieldId: "f",
            scopeKind: StatisticReconciliationExpectedBasicScopes.FlowBranch,
            scopeId: "branch-a"));
        var branchB = compiler.Compile(new ExpectedMetricIdentityRequest(
            StatisticReconciliationExpectedMetricFamilies.Basic,
            StatisticReconciliationExpectedMetricKinds.Field,
            "m-basic-branch", "MONTH:2026-08", fieldId: "f",
            scopeKind: StatisticReconciliationExpectedBasicScopes.FlowBranch,
            scopeId: "branch-b"));
        True(branchA.IdentitySha256 != branchB.IdentitySha256,
            "basic branch scope IDs are distinct");
        return Task.CompletedTask;
    }

    internal static Task AdvancedThreeGrains()
    {
        var compiler = new StatisticReconciliationExpectedMetricIdentityCompiler();
        var hashes = StatisticReconciliationExpectedAdvancedGrains.All
            .Select(grain => compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Advanced,
                StatisticReconciliationExpectedMetricKinds.Field,
                "m-advanced", "MONTH:2026-08", fieldId: "f", grain: grain))
                .IdentitySha256)
            .ToArray();
        Equal(3, hashes.Distinct().Count(), "advanced grains");
        return Task.CompletedTask;
    }

    internal static Task DiffThreeKinds()
    {
        var compiler = new StatisticReconciliationExpectedMetricIdentityCompiler();
        var identities = new[]
        {
            compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Diff,
                StatisticReconciliationExpectedMetricKinds.Field,
                "m-diff-field", "MONTH:2026-08", fieldId: "f",
                diffKind: StatisticReconciliationExpectedDiffKinds.Field)),
            compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Diff,
                StatisticReconciliationExpectedMetricKinds.Table,
                "m-diff-table", "MONTH:2026-08", tableId: "t",
                diffKind: StatisticReconciliationExpectedDiffKinds.TableMetric)),
            compiler.Compile(new ExpectedMetricIdentityRequest(
                StatisticReconciliationExpectedMetricFamilies.Diff,
                StatisticReconciliationExpectedMetricKinds.RowLabel,
                "m-diff-label", "MONTH:2026-08", tableId: "t", rowId: "r", labelId: "l",
                diffKind: StatisticReconciliationExpectedDiffKinds.RowLabel))
        };
        Equal(3, identities.Select(item => item.IdentitySha256).Distinct().Count(),
            "diff identities");
        return Task.CompletedTask;
    }

    internal static async Task AppendCommitLast()
    {
        var backend = new FakeObservationBackend();
        var store = Fixture.Store(backend);
        var result = await store.AppendGenerationAsync(
            Fixture.TypedGeneration(),
            Fixture.UtcNow);
        True(backend.CommitCalled, "commit called");
        Equal(result.DocumentCount - 1, backend.ContentCountAtCommit, "commit last");
        Equal(result.DocumentCount, backend.Documents.Count, "complete count");
    }

    internal static async Task ExactReplay()
    {
        var backend = new FakeObservationBackend();
        var store = Fixture.Store(backend);
        var generation = Fixture.TypedGeneration();
        var first = await store.AppendGenerationAsync(generation, Fixture.UtcNow);
        var second = await store.AppendGenerationAsync(
            generation,
            Fixture.UtcNow.AddMinutes(1));
        True(!first.ExactReplay, "first append");
        True(second.ExactReplay, "second replay");
        Equal(first.ManifestSha256, second.ManifestSha256, "manifest replay");
    }

    internal static async Task ConflictFails()
    {
        var backend = new FakeObservationBackend();
        var generation = Fixture.TypedGeneration();
        backend.SeedUnexpected(generation.ContextPin.ReconciliationId, generation.GenerationId);
        var store = Fixture.Store(backend);
        await ExpectReasonAsync(
            StatisticReconciliationExpectedObservationFailureReasons.GenerationConflict,
            () => store.AppendGenerationAsync(generation, Fixture.UtcNow));
        True(!backend.CommitCalled, "conflict does not commit");
    }

    internal static async Task IncompleteDoesNotCommit()
    {
        var backend = new FakeObservationBackend { DropLastContent = true };
        var store = Fixture.Store(backend);
        await ExpectReasonAsync(
            StatisticReconciliationExpectedObservationFailureReasons.GenerationIncomplete,
            () => store.AppendGenerationAsync(Fixture.TypedGeneration(), Fixture.UtcNow));
        True(!backend.CommitCalled, "incomplete does not commit");
    }

    internal static async Task CompleteLineageRecords()
    {
        var backend = new FakeObservationBackend();
        var generation = Fixture.TypedGeneration();
        await Fixture.Store(backend).AppendGenerationAsync(generation, Fixture.UtcNow);
        Equal(generation.SourcePlan.SourceDecisions.Length,
            backend.Count(StatisticReconciliationObservationRecordKinds.SourceDecision),
            "source decisions");
        Equal(generation.SourcePlan.BoundInputs.LockedP8Configuration.Pins.Length,
            backend.Count(StatisticReconciliationObservationRecordKinds.ConfigurationPin),
            "configuration pins");
        Equal(generation.SourcePlan.BoundInputs.RuntimeMappingContributionLineage.Pins.Length,
            backend.Count(StatisticReconciliationObservationRecordKinds.LineagePin),
            "lineage pins");
        Equal(generation.Atoms.Length,
            backend.Count(StatisticReconciliationObservationRecordKinds.ExpectedAtom),
            "typed atoms");
        Equal(1,
            backend.Count(StatisticReconciliationObservationRecordKinds.GenerationCommit),
            "commit");
    }

    private static void Atom(
        IEnumerable<StatisticReconciliationExpectedTypedAtom> atoms,
        string kind,
        string canonical,
        long reports,
        long rows,
        long numeric)
    {
        var atom = atoms.Single(item => item.AtomKind == kind);
        Equal(canonical, atom.CanonicalValue, kind);
        Equal(reports, atom.ReportCount, kind + " reports");
        Equal(rows, atom.RowCount, kind + " rows");
        Equal(numeric, atom.NumericValueCount, kind + " numeric");
    }

    private static void HasValue(
        StatisticReconciliationExpectedCompiledGeneration generation,
        string metric,
        string value)
        => True(generation.Atoms.Any(item =>
            item.Identity.MetricId == metric &&
            item.ValueState == StatisticReconciliationExpectedValueStates.Value &&
            item.CanonicalValue == value), $"{metric}:{value}");

    private static void ExpectReason(string reason, Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected {reason}.");
        }
        catch (StatisticReconciliationExpectedLedgerInputException error)
            when (error.Reason == reason)
        {
        }
    }

    private static async Task ExpectReasonAsync(string reason, Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException($"Expected {reason}.");
        }
        catch (StatisticReconciliationExpectedObservationException error)
            when (error.ReasonCode == reason)
        {
        }
    }

    private static void True(bool value, string name)
    {
        if (!value)
            throw new InvalidOperationException($"Assertion failed: {name}.");
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"Assertion failed: {name}; expected={expected}; actual={actual}.");
    }
}

internal sealed record SourceSpec(
    string StableSourceId,
    string PayloadJson,
    string LifecycleStatus,
    bool IsEffective,
    bool IsLocked,
    string RuntimeDisposition,
    int PayloadRevision,
    int LifecycleRevision);

internal static class Fixture
{
    internal static readonly DateTime UtcNow =
        new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    internal static SourceSpec Source(
        string sourceId,
        string payloadJson,
        string lifecycleStatus = StatisticReconciliationExpectedLifecycleStatuses.Approved,
        bool isEffective = true,
        bool isLocked = true,
        string runtimeDisposition = StatisticReconciliationExpectedRuntimeDispositions.Current,
        int payloadRevision = 1,
        int lifecycleRevision = 1)
        => new(sourceId, payloadJson, lifecycleStatus, isEffective, isLocked,
            runtimeDisposition, payloadRevision, lifecycleRevision);

    internal static ExpectedContributionCandidate Contribution(
        string sourceId,
        string policy,
        long revision = 1)
        => new(
            sourceId,
            policy,
            $"contribution-version-{revision}",
            revision,
            Sha($"policy-{sourceId}-{policy}-{revision}"),
            $"provenance-{sourceId}-{revision}",
            Sha($"provenance-{sourceId}-{policy}-{revision}"),
            true);

    internal static StatisticReconciliationExpectedSourcePlan Plan(
        string configurationJson,
        IReadOnlyList<SourceSpec> sources,
        IReadOnlyList<ExpectedContributionCandidate>? contributions = null)
    {
        var context = Context();
        var candidates = sources.Select(source => Candidate(context, source)).ToArray();
        var membership = new CurrentEpochFlowMembership(
            context,
            context.FlowTemplateVersionId!,
            context.FlowPayloadSha256!,
            context.FlowInstanceId!,
            context.ExecutionEpochId!,
            3,
            7,
            sources.Select(item => item.StableSourceId).Distinct(StringComparer.Ordinal));
        var canonicalConfig = StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
            configurationJson,
            4 * 1024 * 1024,
            "$.fixture.config",
            StatisticReconciliationExpectedLedgerInputFailureReasons.ConfigurationInvalid);
        var configuration = new LockedP8Configuration(
            context,
            context.P8ConfigurationOwnerId,
            context.P8ConfigurationBundleSha256,
            canonicalConfig.Sha256,
            canonicalConfig.Value,
            [
                new LockedP8ConfigurationPin(
                    StatisticReconciliationExpectedLedgerConfigurationKinds.Field,
                    context.P8ConfigurationOwnerId,
                    "config-field",
                    "config-field-v1",
                    1,
                    1,
                    Sha("config-field"))
            ]);
        var lineage = new P5P7RuntimeMappingContributionLineage(
            context,
            StatisticReconciliationExpectedLedgerLineageLayers.Required.Select((layer, index) =>
                new ExpectedLedgerLineagePin(
                    layer,
                    $"owner-{index}",
                    $"version-{index}",
                    index + 1,
                    Sha($"lineage-{index}"))));
        var snapshot = new ExpectedAuthoritativeSourceSnapshot(
            context,
            candidates,
            membership,
            configuration,
            lineage,
            contributions ?? []);
        return new StatisticReconciliationExpectedSourcePlanner(
            new StatisticReconciliationExpectedLedgerCompiler()).Plan(snapshot);
    }

    internal static StatisticReconciliationExpectedCompiledGeneration TypedGeneration(
        bool reverseSources = false)
    {
        var sources = new[]
        {
            Source("s1", "{\"nums\":[10,20,null,\"\"],\"bucket\":\"A\",\"date\":\"2026\",\"fullDate\":\"10/08/2026\",\"period\":\"MONTH:2026-08\",\"flag\":true,\"enum\":\"OPEN\",\"tags\":[\"b\",\"a\",\"a\"],\"text\":\"hello\"}"),
            Source("s2", "{\"nums\":[30],\"bucket\":2,\"date\":\"08/2026\",\"fullDate\":\"11/08/2026\",\"period\":\"DAY:2026-08-11\",\"flag\":false,\"enum\":\"CLOSED\",\"tags\":[\"a\",\"b\"],\"text\":\"world\"}")
        };
        if (reverseSources)
            Array.Reverse(sources);
        var contributions = sources.Select(item => Contribution(
            item.StableSourceId,
            StatisticReconciliationExpectedContributionPolicies.Include)).ToArray();
        var plan = Plan(TypedConfig(), sources, contributions);
        return new StatisticReconciliationExpectedTypedCompiler(
            new StatisticReconciliationExpectedMetricIdentityCompiler())
            .Compile(plan, CatalogPins());
    }

    internal static IStatisticReconciliationExpectedObservationStore Store(
        FakeObservationBackend backend)
        => new StatisticReconciliationExpectedObservationStore(
            backend,
            new StatisticReconciliationExpectedMetricIdentityCompiler());

    internal static StatisticReconciliationExpectedCatalogPins CatalogPins()
        => StatisticReconciliationExpectedCatalogPins.Create(
            "P9-CATALOG-V1",
            Sha("p9-catalog-raw"),
            Sha("p9-catalog-semantic"),
            Sha("p9-schema-raw"),
            Sha("p9-schema-semantic"),
            Sha("p9-stage-lock"),
            "p10-chain",
            "P10-01",
            "P10-CANDIDATE-V1",
            Sha("candidate-catalog-raw"),
            Sha("candidate-catalog-semantic"),
            Sha("candidate-schema-raw"),
            Sha("candidate-schema-semantic"),
            Sha("candidate-stage-lock"));

    internal static string MinimalConfig()
        => JsonSerializer.Serialize(new
        {
            expectedMetrics = new[]
            {
                Metric("value", "/value", StatisticReconciliationExpectedValueTypes.Number,
                    [StatisticReconciliationExpectedMetricOperations.Sum])
            }
        });

    internal static string NumberOnlyConfig()
        => JsonSerializer.Serialize(new
        {
            expectedMetrics = new[]
            {
                Metric("number", "/nums", StatisticReconciliationExpectedValueTypes.Number,
                    [StatisticReconciliationExpectedMetricOperations.Sum,
                     StatisticReconciliationExpectedMetricOperations.Mean], expandArray: true)
            }
        });

    private static string TypedConfig()
    {
        var metrics = new List<Dictionary<string, object?>>
        {
            Metric("number", "/nums", StatisticReconciliationExpectedValueTypes.Number,
                [StatisticReconciliationExpectedMetricOperations.Sum,
                 StatisticReconciliationExpectedMetricOperations.Min,
                 StatisticReconciliationExpectedMetricOperations.Max,
                 StatisticReconciliationExpectedMetricOperations.Mean], expandArray: true),
            Metric("bucket", "/bucket", StatisticReconciliationExpectedValueTypes.Bucket,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("date", "/date", StatisticReconciliationExpectedValueTypes.Date,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("fullDate", "/fullDate", StatisticReconciliationExpectedValueTypes.FullDate,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("period", "/period", StatisticReconciliationExpectedValueTypes.Period,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("boolean", "/flag", StatisticReconciliationExpectedValueTypes.Boolean,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("enum", "/enum", StatisticReconciliationExpectedValueTypes.Enum,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("tags", "/tags", StatisticReconciliationExpectedValueTypes.StringList,
                [StatisticReconciliationExpectedMetricOperations.Values], unordered: true),
            Metric("text", "/text", StatisticReconciliationExpectedValueTypes.Text,
                [StatisticReconciliationExpectedMetricOperations.Values]),
            Metric("missing", "/does-not-exist", StatisticReconciliationExpectedValueTypes.Text,
                [StatisticReconciliationExpectedMetricOperations.Values])
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["expectedMetrics"] = metrics
        });
    }

    private static Dictionary<string, object?> Metric(
        string metricId,
        string pointer,
        string valueType,
        string[] operations,
        bool expandArray = false,
        bool unordered = false)
        => new()
        {
            ["family"] = StatisticReconciliationExpectedMetricFamilies.Direct,
            ["kind"] = StatisticReconciliationExpectedMetricKinds.Field,
            ["metricId"] = metricId,
            ["fieldId"] = "field-" + metricId,
            ["jsonPointer"] = pointer,
            ["valueType"] = valueType,
            ["operations"] = operations,
            ["expandArray"] = expandArray,
            ["unordered"] = unordered
        };

    private static ExpectedLifecycleRevisionCandidate Candidate(
        ExpectedLedgerCompilationContextPin context,
        SourceSpec source)
    {
        var canonical = StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
            source.PayloadJson,
            16 * 1024 * 1024,
            "$.fixture.payload",
            StatisticReconciliationExpectedLedgerInputFailureReasons.PayloadJsonInvalid);
        var identity = new ExpectedSourceIdentityPin(
            source.StableSourceId,
            "report-" + source.StableSourceId,
            context.WorkId,
            context.ScopeAssignmentId,
            source.PayloadRevision,
            Sha($"payload-owner-{source.StableSourceId}-{source.PayloadRevision}"),
            canonical.Sha256,
            source.LifecycleRevision,
            Sha($"lifecycle-{source.StableSourceId}-{source.LifecycleRevision}"),
            context.DynamicFormVersionId,
            context.FlowInstanceId!,
            context.ExecutionEpochId!);
        return new ExpectedLifecycleRevisionCandidate(
            source.StableSourceId,
            identity,
            $"payload-{source.StableSourceId}-{source.PayloadRevision}",
            canonical.Value,
            source.LifecycleStatus,
            source.IsEffective,
            source.IsLocked,
            source.RuntimeDisposition);
    }

    private static ExpectedLedgerCompilationContextPin Context()
        => new(
            "reconciliation-1",
            Sha("immutable-identity"),
            Sha("immutable-header"),
            "tenant-1",
            "work-1",
            "assignment-1",
            "p10-chain",
            "P10-01",
            "MONTH:2026-08",
            "period-instance-1",
            "concept-1",
            "MONTH",
            "APPROVED_AT",
            Sha("filter"),
            "form-version-1",
            Sha("form-schema"),
            "flow-template-version-1",
            Sha("flow-payload"),
            "flow-instance-1",
            "epoch-1",
            "p8-owner-1",
            Sha("p8-bundle"));

    internal static ExpectedLedgerCompilationContextPin ProductionFlowContext()
        => new(
            "reconciliation-production-1",
            Sha("production-immutable-identity"),
            Sha("production-immutable-header"),
            "tenant-1",
            "work-1",
            "assignment-1",
            "p10-chain",
            "P10-01",
            "MONTH:2026-08",
            "period-instance-1",
            "concept-1",
            "MONTH",
            "APPROVED_AT",
            Sha("production-filter"),
            "form-version-1",
            Sha("production-form-schema"),
            StatisticReconciliationExpectedRuntimeKinds.Flow,
            "flow-template-version-1",
            Sha("production-flow-payload"),
            "flow-instance-1",
            "epoch-current-1",
            "p8-owner-1",
            Sha("production-p8-bundle"),
            "p9-owner-run-1",
            Sha("p9-owner-generation-id"),
            Sha("p9-owner-generation"),
            Sha("p9-owner-membership"),
            3);
    internal static string Sha(string value)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            "P10_T14_FIXTURE_V1",
            value);
}

internal sealed class FakeObservationBackend
    : IStatisticReconciliationExpectedObservationBackend
{
    internal Dictionary<string, StatisticReconciliationObservation> Documents { get; } =
        new(StringComparer.Ordinal);
    internal bool DropLastContent { get; init; }
    internal bool CommitCalled { get; private set; }
    internal int ContentCountAtCommit { get; private set; }

    public Task<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>(
            Documents.Values
                .Where(item =>
                    item.ReconciliationId == reconciliationId &&
                    item.GenerationId == generationId)
                .Select(item => new StatisticReconciliationExpectedStoredObservation(
                    item.Id,
                    item.RecordKind,
                    item.DocumentSemanticSha256))
                .ToArray());

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        var count = DropLastContent && observations.Count > 0
            ? observations.Count - 1
            : observations.Count;
        for (var index = 0; index < count; index++)
            Documents.TryAdd(observations[index].Id, observations[index]);
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        CommitCalled = true;
        ContentCountAtCommit = Documents.Count;
        Documents.TryAdd(observation.Id, observation);
        return Task.CompletedTask;
    }

    internal int Count(string recordKind)
        => Documents.Values.Count(item => item.RecordKind == recordKind);

    internal void SeedUnexpected(string reconciliationId, string generationId)
    {
        Documents["unexpected"] = new StatisticReconciliationObservation
        {
            Id = "unexpected",
            RecordKind = StatisticReconciliationObservationRecordKinds.SourceDecision,
            ReconciliationId = reconciliationId,
            GenerationId = generationId,
            DocumentSemanticSha256 = Fixture.Sha("unexpected")
        };
    }
}
