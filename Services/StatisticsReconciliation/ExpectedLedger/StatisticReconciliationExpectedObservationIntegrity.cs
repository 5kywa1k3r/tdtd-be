using System.Collections.Immutable;
using System.Globalization;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class StatisticReconciliationExpectedObservationIntegrity
{
    internal const string ObservationSchemaVersion = "P10_EXPECTED_OBSERVATION_V2";
    internal const int MaxGenerationDocuments = 100_000;

    internal static readonly ImmutableArray<string> RecordKindOrder =
    [
        StatisticReconciliationObservationRecordKinds.ConfigurationPin,
        StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan,
        StatisticReconciliationObservationRecordKinds.ExpectedAtom,
        StatisticReconciliationObservationRecordKinds.GenerationCommit,
        StatisticReconciliationObservationRecordKinds.LineagePin,
        StatisticReconciliationObservationRecordKinds.SourceDecision
    ];

    internal static int RecordKindRank(string recordKind)
    {
        for (var index = 0; index < RecordKindOrder.Length; index++)
        {
            if (string.Equals(RecordKindOrder[index], recordKind, StringComparison.Ordinal))
                return index;
        }
        throw Invalid("RECORD_KIND_INVALID");
    }

    internal static int RequireGenerationDocumentCount(
        int sourceDecisionCount,
        int configurationPinCount,
        int lineagePinCount,
        int atomCount)
        => RequireGenerationDocumentCount(
            sourceDecisionCount,
            configurationPinCount,
            lineagePinCount,
            0,
            atomCount);

    internal static int RequireGenerationDocumentCount(
        int sourceDecisionCount,
        int configurationPinCount,
        int lineagePinCount,
        int metricPlanEntryCount,
        int atomCount)
    {
        if (sourceDecisionCount < 0 || configurationPinCount < 0 ||
            lineagePinCount < 0 || metricPlanEntryCount < 0 || atomCount < 0)
            throw Invalid("GENERATION_DOCUMENT_COUNT_OUT_OF_RANGE");
        var total = checked(
            (long)sourceDecisionCount + configurationPinCount + lineagePinCount +
            metricPlanEntryCount + atomCount + 1L);
        if (total > MaxGenerationDocuments)
            throw Invalid("GENERATION_DOCUMENT_COUNT_OUT_OF_RANGE");
        return checked((int)total);
    }

    internal static StatisticReconciliationObservation ValidateGeneration(
        IReadOnlyList<StatisticReconciliationObservation> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count is < 1 or > MaxGenerationDocuments)
            throw Invalid("GENERATION_DOCUMENT_COUNT_OUT_OF_RANGE");
        if (documents.Select(item => item.Id)
            .Distinct(StringComparer.Ordinal).Count() != documents.Count)
            throw Invalid("DUPLICATE_DOCUMENT_ID");

        var commits = documents.Where(item =>
            item.RecordKind == StatisticReconciliationObservationRecordKinds.GenerationCommit)
            .ToArray();
        if (commits.Length != 1)
            throw Invalid("EXACT_COMMIT_REQUIRED");
        var commitDocument = commits[0];
        var commit = commitDocument.Commit ?? throw Invalid("COMMIT_PAYLOAD_REQUIRED");

        var headerFingerprint = HeaderFingerprint(commitDocument);
        foreach (var document in documents)
        {
            if (!string.Equals(
                    HeaderFingerprint(document),
                    headerFingerprint,
                    StringComparison.Ordinal))
                throw Invalid("COMMON_HEADER_MISMATCH");
            ValidateDocument(document);
        }

        var sourceCount = Count(
            documents,
            StatisticReconciliationObservationRecordKinds.SourceDecision);
        var configurationCount = Count(
            documents,
            StatisticReconciliationObservationRecordKinds.ConfigurationPin);
        var lineageCount = Count(
            documents,
            StatisticReconciliationObservationRecordKinds.LineagePin);
        var metricPlanCount = Count(
            documents,
            StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan);
        var atomCount = Count(
            documents,
            StatisticReconciliationObservationRecordKinds.ExpectedAtom);
        if (commit.SourceDecisionCount != sourceCount ||
            commit.MetricPlanEntryCount != metricPlanCount ||
            commit.ConfigurationPinCount != configurationCount ||
            commit.LineagePinCount != lineageCount ||
            commit.AtomCount != atomCount ||
            commit.DocumentCount != documents.Count ||
            commit.DocumentCount != checked(
                sourceCount + configurationCount + lineageCount +
                metricPlanCount + atomCount + 1))
            throw Invalid("COMMIT_COUNT_MISMATCH");

        var manifestSha256 = BuildManifestSha256(documents.Where(item =>
            item.RecordKind != StatisticReconciliationObservationRecordKinds.GenerationCommit));
        if (!string.Equals(commit.ManifestSha256, manifestSha256, StringComparison.Ordinal))
            throw Invalid("MANIFEST_SHA256_MISMATCH");

        var metricPlanSha256 =
            StatisticReconciliationExpectedMetricPlanIntegrity.BuildPlanSha256(
                documents
                    .Where(item =>
                        item.RecordKind ==
                        StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan)
                    .Select(item => ToPlanEntry(item.MetricPlan!)));
        if (!string.Equals(
                commitDocument.MetricPlanSha256,
                metricPlanSha256,
                StringComparison.Ordinal))
            throw Invalid("METRIC_PLAN_SHA256_MISMATCH");
        if (metricPlanCount > 0)
        {
            var planByIdentity = documents
                .Where(item => item.RecordKind ==
                    StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan)
                .Select(item => ToPlanEntry(item.MetricPlan!))
                .ToDictionary(
                    item => item.Identity.IdentitySha256,
                    StringComparer.Ordinal);
            foreach (var atomDocument in documents.Where(item =>
                         item.RecordKind ==
                         StatisticReconciliationObservationRecordKinds.ExpectedAtom))
            {
                var atom = atomDocument.Atom!;
                if (!planByIdentity.TryGetValue(atom.IdentitySha256, out var plan))
                    throw Invalid("ATOM_METRIC_PLAN_MISSING");
                if (atom.ValueType ==
                    StatisticReconciliationExpectedValueTypes.StringList)
                {
                    var expected = plan.Unordered
                        ? StatisticReconciliationExpectedCollectionSemantics.Unordered
                        : StatisticReconciliationExpectedCollectionSemantics.Ordered;
                    if (!string.Equals(
                            atom.CollectionSemantics,
                            expected,
                            StringComparison.Ordinal))
                        throw Invalid("ATOM_COLLECTION_PLAN_MISMATCH");
                }
            }
        }

        if (metricPlanCount > 0 &&
            string.IsNullOrWhiteSpace(commit.MembershipSemanticSha256))
            throw Invalid("MEMBERSHIP_SEMANTIC_SHA256_REQUIRED");
        var membershipSemanticSha256 =
            StatisticReconciliationExpectedMembershipIntegrity.BuildManifestSha256(
                documents
                    .Where(item =>
                        item.RecordKind ==
                            StatisticReconciliationObservationRecordKinds.SourceDecision &&
                        string.Equals(
                            item.SourceDecision!.ReasonCode,
                            StatisticReconciliationExpectedSourceDecisionReasons.Included,
                            StringComparison.Ordinal))
                    .Select(item =>
                        item.SourceDecision!.SourceStableIdentitySha256 ??
                        throw Invalid("SOURCE_STABLE_IDENTITY_REQUIRED")));
        if (metricPlanCount > 0 &&
            !string.Equals(
                commit.MembershipSemanticSha256,
                membershipSemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("MEMBERSHIP_SEMANTIC_SHA256_MISMATCH");

        var typedSemanticSha256 = H(
            "P10_EXPECTED_TYPED_LEDGER_V1",
            documents
                .Where(item =>
                    item.RecordKind ==
                    StatisticReconciliationObservationRecordKinds.ExpectedAtom)
                .OrderBy(item => item.Atom!.IdentitySha256, StringComparer.Ordinal)
                .ThenBy(item => item.Atom!.AtomKind, StringComparer.Ordinal)
                .ThenBy(item => item.Atom!.ValueIdentitySha256, StringComparer.Ordinal)
                .Select(item => item.Atom!.AtomSemanticSha256));
        if (!string.Equals(
                commitDocument.TypedSemanticSha256,
                typedSemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("TYPED_SEMANTIC_SHA256_MISMATCH");

        var generationSemanticSha256 = BuildGenerationSemanticSha256(commitDocument);
        if (!string.Equals(
                commitDocument.GenerationSemanticSha256,
                generationSemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("GENERATION_SEMANTIC_SHA256_MISMATCH");
        var generationId = BuildGenerationId(
            commitDocument.ReconciliationId,
            generationSemanticSha256);
        if (!string.Equals(commitDocument.GenerationId, generationId, StringComparison.Ordinal))
            throw Invalid("GENERATION_ID_MISMATCH");
        return commitDocument;
    }

    internal static void ValidateDocument(StatisticReconciliationObservation document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(
                document.SchemaVersion,
                ObservationSchemaVersion,
                StringComparison.Ordinal))
            throw Invalid("OBSERVATION_SCHEMA_INVALID");
        if (document.CreatedAtUtc.Kind != DateTimeKind.Utc)
            throw Invalid("CREATED_AT_NOT_UTC");
        ValidatePayloadExclusivity(document);
        ValidateCatalogPins(document.CatalogPins);

        var expectedId = BuildDocumentId(document);
        if (!string.Equals(document.Id, expectedId, StringComparison.Ordinal))
            throw Invalid("DOCUMENT_ID_MISMATCH");
        var expectedSemanticSha256 = BuildDocumentSemanticSha256(document);
        if (!string.Equals(
                document.DocumentSemanticSha256,
                expectedSemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("DOCUMENT_SEMANTIC_SHA256_MISMATCH");
    }

    internal static void RequireCommonHeader(
        StatisticReconciliationObservation document,
        StatisticReconciliationObservation commit)
    {
        ValidateDocument(document);
        if (!string.Equals(
                HeaderFingerprint(document),
                HeaderFingerprint(commit),
                StringComparison.Ordinal))
            throw Invalid("COMMON_HEADER_MISMATCH");
    }

    internal static string BuildManifestSha256(
        IEnumerable<StatisticReconciliationObservation> content)
        => H(
            "P10_EXPECTED_OBSERVATION_MANIFEST_V1",
            content
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.DocumentSemanticSha256));

    internal static string BuildGenerationSemanticSha256(
        StatisticReconciliationObservation document)
        => H(
            StatisticReconciliationExpectedTypedCompiler.GenerationSchemaVersion,
            document.ReconciliationId,
            document.InputBindingSha256,
            document.SourceSetSha256,
            document.Commit?.MembershipSemanticSha256 ?? "~",
            document.AlgorithmRevision,
            document.AlgorithmSha256,
            document.CatalogPins.CatalogPinSetSha256,
            document.MetricPlanSha256,
            document.TypedSemanticSha256);

    internal static string BuildGenerationId(
        string reconciliationId,
        string generationSemanticSha256)
        => H(
            "P10_EXPECTED_GENERATION_ID_V3",
            reconciliationId,
            generationSemanticSha256);

    internal static string BuildDocumentSemanticSha256(
        StatisticReconciliationObservation document)
    {
        var payload = document.RecordKind switch
        {
            StatisticReconciliationObservationRecordKinds.SourceDecision =>
                SourcePayload(document),
            StatisticReconciliationObservationRecordKinds.ConfigurationPin =>
                ConfigurationPayload(document.ConfigurationPin!),
            StatisticReconciliationObservationRecordKinds.LineagePin =>
                LineagePayload(document.LineagePin!),
            StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan =>
                MetricPlanPayload(document.MetricPlan!),
            StatisticReconciliationObservationRecordKinds.ExpectedAtom =>
                AtomPayload(document.Atom!),
            StatisticReconciliationObservationRecordKinds.GenerationCommit =>
                CommitPayload(document.Commit!),
            _ => throw Invalid("RECORD_KIND_INVALID")
        };
        return H(
            ObservationSchemaVersion,
            CommonDocumentFields(document).Concat(payload));
    }

    private static IEnumerable<string> SourcePayload(
        StatisticReconciliationObservation document)
    {
        var source = document.SourceDecision ??
            throw Invalid("SOURCE_DECISION_REQUIRED");
        var runtime = source.AuthoritativeRuntime;
        if (runtime is not null)
            ValidateRuntime(document, source, runtime);
        var decisionSemanticSha256 = BuildSourceDecisionSemanticSha256(source);
        if (!string.Equals(
                source.DecisionSemanticSha256,
                decisionSemanticSha256,
                StringComparison.Ordinal))
            throw Invalid("SOURCE_DECISION_SEMANTIC_SHA256_MISMATCH");
        if (!string.Equals(source.StableSourceId, source.IdentityKey, StringComparison.Ordinal))
            throw Invalid("SOURCE_IDENTITY_KEY_MISMATCH");
        if (string.IsNullOrWhiteSpace(source.WorkAssignmentId) ||
            string.IsNullOrWhiteSpace(source.SourceStableIdentitySha256) ||
            !string.Equals(
                source.SourceStableIdentitySha256,
                StatisticReconciliationExpectedMembershipIntegrity
                    .BuildStableIdentitySha256(
                        document.WorkId,
                        source.WorkAssignmentId,
                        source.ReportId),
                StringComparison.Ordinal))
            throw Invalid("SOURCE_STABLE_IDENTITY_MISMATCH");
        return
        [
            source.DecisionSemanticSha256,
            source.IdentityKey,
            source.ReportId,
            source.WorkAssignmentId ?? "~",
            source.SourceStableIdentitySha256 ?? "~",
            source.PayloadDocumentId,
            source.PayloadOwnerSha256,
            source.PayloadCanonicalSha256,
            source.LifecycleSha256,
            runtime?.RuntimeSemanticSha256 ?? "~"
        ];
    }

    private static IEnumerable<string> ConfigurationPayload(
        StatisticReconciliationObservationConfigurationPin pin)
        =>
        [
            pin.Kind,
            pin.OwnerId,
            pin.ConfigId,
            pin.VersionId,
            I(pin.VersionNo),
            I(pin.Revision),
            pin.ConfigSha256
        ];

    private static IEnumerable<string> LineagePayload(
        StatisticReconciliationObservationLineagePin pin)
        =>
        [
            pin.Layer,
            pin.OwnerId,
            pin.VersionId,
            I(pin.Revision),
            pin.Sha256
        ];

    private static IEnumerable<string> MetricPlanPayload(
        StatisticReconciliationObservationMetricPlan plan)
    {
        var entry = ToPlanEntry(plan);
        return
        [
            plan.SchemaVersion,
            plan.IdentitySha256,
            plan.Family,
            plan.Kind,
            plan.MetricId,
            plan.PeriodKey,
            plan.FieldId ?? "~",
            plan.TableId ?? "~",
            plan.RowId ?? "~",
            plan.LabelId ?? "~",
            plan.BasicScope ?? "~",
            plan.BasicScopeId ?? "~",
            plan.AdvancedGrain ?? "~",
            plan.DiffKind ?? "~",
            plan.JsonPointer,
            plan.ValueType,
            plan.Unordered ? "1" : "0",
            plan.ExpandArray ? "1" : "0",
            H("P10_EXPECTED_OPERATIONS_V2", plan.Operations),
            plan.TransitionMode,
            plan.BeforeJsonPointer ?? "~",
            plan.AfterJsonPointer ?? "~",
            plan.DifferenceOperation ?? "~",
            plan.TransitionKind ?? "~",
            entry.PlanEntrySha256
        ];
    }

    private static StatisticReconciliationExpectedMetricPlanEntry ToPlanEntry(
        StatisticReconciliationObservationMetricPlan plan)
    {
        var identity = new StatisticReconciliationExpectedMetricIdentity(
            StatisticReconciliationExpectedMetricIdentityCompiler.SchemaVersion,
            plan.Family,
            plan.Kind,
            plan.MetricId,
            plan.PeriodKey,
            plan.FieldId,
            plan.TableId,
            plan.RowId,
            plan.LabelId,
            plan.BasicScope,
            plan.BasicScopeId,
            plan.AdvancedGrain,
            plan.DiffKind,
            plan.IdentitySha256);
        var entry = new StatisticReconciliationExpectedMetricPlanEntry(
            identity,
            plan.JsonPointer,
            plan.ValueType,
            plan.Unordered,
            plan.ExpandArray,
            plan.Operations.ToImmutableArray(),
            plan.PlanEntrySha256,
            plan.SchemaVersion,
            plan.TransitionMode,
            plan.BeforeJsonPointer,
            plan.AfterJsonPointer,
            plan.DifferenceOperation,
            plan.TransitionKind);
        var expected = StatisticReconciliationExpectedMetricPlanIntegrity
            .BuildEntrySha256(
                identity,
                entry.JsonPointer,
                entry.ValueType,
                entry.Unordered,
                entry.ExpandArray,
                entry.Operations,
                entry.TransitionMode,
                entry.BeforeJsonPointer,
                entry.AfterJsonPointer,
                entry.DifferenceOperation,
                entry.TransitionKind);
        if (plan.SchemaVersion !=
                StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2 ||
            plan.PlanEntrySha256 != expected)
            throw Invalid("METRIC_PLAN_ENTRY_SHA256_MISMATCH");
        return entry;
    }
    private static IEnumerable<string> AtomPayload(
        StatisticReconciliationObservationAtom atom)
    {
        ValidateAtom(atom);
        return
        [
            atom.AtomSemanticSha256,
            atom.IdentitySha256,
            atom.TransitionLeg ?? StatisticReconciliationExpectedTransitionLegs.None,
            atom.TransitionKind ?? "~",
            atom.CollectionSemantics ?? "~",
            atom.ValueIdentitySha256
        ];
    }

    private static IEnumerable<string> CommitPayload(
        StatisticReconciliationObservationCommit commit)
        =>
        [
            I(commit.SourceDecisionCount),
            I(commit.MetricPlanEntryCount),
            I(commit.ConfigurationPinCount),
            I(commit.LineagePinCount),
            I(commit.AtomCount),
            I(commit.DocumentCount),
            commit.ManifestSha256,
            commit.MembershipSemanticSha256 ?? "~"
        ];

    private static string BuildSourceDecisionSemanticSha256(
        StatisticReconciliationObservationSourceDecision source)
    {
        var lifecycleFingerprint = H(
            "P10_EXPECTED_LIFECYCLE_CANDIDATE_V2",
            source.StableSourceId,
            source.ReportId,
            I(source.PayloadRevision),
            source.PayloadOwnerSha256,
            source.PayloadCanonicalSha256,
            I(source.LifecycleRevision),
            source.LifecycleSha256,
            source.LifecycleStatus,
            source.IsEffective ? "1" : "0",
            source.IsLocked ? "1" : "0",
            source.RuntimeDisposition,
            source.AuthoritativeRuntime?.RuntimeSemanticSha256 ?? "~");
        return H(
            "P10_EXPECTED_SOURCE_DECISION_V2",
            lifecycleFingerprint,
            source.StableSourceId,
            I(source.PayloadRevision),
            source.PayloadDocumentId,
            source.LifecycleStatus,
            source.IsEffective ? "true" : "false",
            source.IsLocked ? "true" : "false",
            source.RuntimeDisposition,
            source.Disposition,
            source.ReasonCode,
            source.ContributionPolicy,
            source.ContributionVersionId,
            I(source.ContributionRevision),
            source.ContributionPolicySha256,
            source.ContributionProvenanceId,
            source.ContributionProvenanceSha256,
            source.AuthoritativeRuntime?.RuntimeSemanticSha256 ?? "~");
    }

    private static void ValidateRuntime(
        StatisticReconciliationObservation document,
        StatisticReconciliationObservationSourceDecision source,
        StatisticReconciliationObservationAuthoritativeRuntimePin stored)
    {
        var value = new StatisticReconciliationExpectedAuthoritativeRuntimePin(
            stored.RuntimeKind,
            stored.FlowTemplateVersionId,
            stored.FlowPayloadSha256,
            stored.FlowInstanceId,
            stored.FlowBranchId,
            stored.FlowStepId,
            stored.FlowStepInstanceId,
            stored.FlowStepRevision,
            stored.FlowAttemptNo,
            stored.ExecutionEpochId,
            stored.ExecutionEpoch,
            stored.CurrentExecutionEpochId,
            stored.CurrentExecutionEpoch,
            stored.ExecutionEpochRevision,
            stored.IsCanonicalEpoch,
            stored.ApprovalCommandId,
            stored.ApprovalEventKey,
            stored.MappingReceiptId,
            stored.MappingProvenanceId,
            stored.MappingProvenanceSha256,
            stored.MappingResultSemanticSha256,
            stored.MappingResultPayloadRevision,
            stored.MappingResultPayloadSha256,
            stored.MappingFlowVersionId,
            stored.MappingFlowVersionNo,
            stored.MappingFlowPayloadSha256,
            stored.MappingLocked,
            stored.ConfigVersionId,
            stored.ConfigSha256,
            stored.MembershipSignatureSha256,
            stored.ContributionPolicy,
            stored.ContributionPolicySha256,
            stored.ContributionProvenanceId,
            stored.ContributionProvenanceSha256,
            stored.LifecycleOwnerSha256,
            stored.RuntimeSemanticSha256);
        var expected =
            StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .BuildSemanticSha256(value);
        if (!StringComparer.Ordinal.Equals(expected, value.RuntimeSemanticSha256) ||
            !StringComparer.Ordinal.Equals(value.RuntimeKind, document.RuntimeKind) ||
            !StringComparer.Ordinal.Equals(
                value.ContributionPolicy,
                source.ContributionPolicy) ||
            !StringComparer.Ordinal.Equals(
                value.ContributionPolicySha256,
                source.ContributionPolicySha256) ||
            !StringComparer.Ordinal.Equals(
                value.ContributionProvenanceId,
                source.ContributionProvenanceId) ||
            !StringComparer.Ordinal.Equals(
                value.ContributionProvenanceSha256,
                source.ContributionProvenanceSha256) ||
            !StringComparer.Ordinal.Equals(
                value.LifecycleOwnerSha256,
                source.LifecycleSha256))
            throw Invalid("AUTHORITATIVE_RUNTIME_SEMANTIC_MISMATCH");
        if (value.RuntimeKind == StatisticReconciliationExpectedRuntimeKinds.Flow)
        {
            if (!StringComparer.Ordinal.Equals(
                    value.FlowTemplateVersionId,
                    document.FlowTemplateVersionId) ||
                !StringComparer.Ordinal.Equals(
                    value.FlowPayloadSha256,
                    document.FlowPayloadSha256) ||
                !StringComparer.Ordinal.Equals(
                    value.FlowInstanceId,
                    document.FlowInstanceId) ||
                !StringComparer.Ordinal.Equals(
                    value.ExecutionEpochId,
                    document.ExecutionEpochId) ||
                value.ExecutionEpoch != document.ExecutionEpoch ||
                value.ExecutionEpochRevision != document.ExecutionEpochRevision ||
                value.IsCanonicalEpoch != true ||
                value.ExecutionEpochId != value.CurrentExecutionEpochId ||
                value.ExecutionEpoch != value.CurrentExecutionEpoch)
                throw Invalid("AUTHORITATIVE_FLOW_RUNTIME_MISMATCH");
        }
        else if (value.RuntimeKind !=
                     StatisticReconciliationExpectedRuntimeKinds.NonFlow ||
                 value.FlowTemplateVersionId is not null ||
                 value.FlowPayloadSha256 is not null ||
                 value.FlowInstanceId is not null ||
                 value.ExecutionEpochId is not null)
            throw Invalid("AUTHORITATIVE_NON_FLOW_RUNTIME_MISMATCH");
    }
    private static void ValidateAtom(StatisticReconciliationObservationAtom atom)
    {
        if (atom.OccurrenceCount < 0 || atom.ReportCount < 0 ||
            atom.RowCount < 0 || atom.NumericValueCount < 0 || atom.DecimalScale < 0)
            throw Invalid("ATOM_COUNT_INVALID");
        if (atom.BasicScope == "FLOW_BRANCH")
        {
            if (!string.Equals(atom.FlowBranchId, atom.BasicScopeId, StringComparison.Ordinal) ||
                atom.FlowStepId is not null)
                throw Invalid("ATOM_FLOW_SCOPE_MISMATCH");
        }
        else if (atom.BasicScope == "FLOW_STEP")
        {
            if (!string.Equals(atom.FlowStepId, atom.BasicScopeId, StringComparison.Ordinal) ||
                atom.FlowBranchId is not null)
                throw Invalid("ATOM_FLOW_SCOPE_MISMATCH");
        }
        else if (atom.FlowBranchId is not null || atom.FlowStepId is not null)
        {
            throw Invalid("ATOM_FLOW_SCOPE_MISMATCH");
        }

        var identitySha256 = H(
            StatisticReconciliationExpectedMetricIdentityCompiler.SchemaVersion,
            atom.Family,
            atom.Kind,
            atom.MetricId,
            atom.PeriodKey ?? "~",
            atom.FieldId ?? "~",
            atom.TableId ?? "~",
            atom.RowId ?? "~",
            atom.LabelId ?? "~",
            atom.BasicScope ?? "~",
            atom.BasicScopeId ?? "~",
            atom.AdvancedGrain ?? "~",
            atom.DiffKind ?? "~");
        if (!string.Equals(atom.IdentitySha256, identitySha256, StringComparison.Ordinal))
            throw Invalid("ATOM_IDENTITY_SHA256_MISMATCH");

        var transitionLeg =
            atom.TransitionLeg ?? StatisticReconciliationExpectedTransitionLegs.None;
        if (transitionLeg == StatisticReconciliationExpectedTransitionLegs.None &&
            atom.TransitionKind is not null)
            throw Invalid("ATOM_TRANSITION_KIND_WITHOUT_LEG");
        if (transitionLeg != StatisticReconciliationExpectedTransitionLegs.None &&
            (atom.TransitionKind is null ||
             !StatisticReconciliationExpectedTransitionKinds.All.Contains(
                 atom.TransitionKind)))
            throw Invalid("ATOM_TRANSITION_KIND_REQUIRED");

        var expectedCollection = atom.ValueType ==
            StatisticReconciliationExpectedValueTypes.StringList;
        if (expectedCollection != (atom.CollectionSemantics is not null) ||
            atom.CollectionSemantics is not null &&
            !StatisticReconciliationExpectedCollectionSemantics.All.Contains(
                atom.CollectionSemantics))
            throw Invalid("ATOM_COLLECTION_SEMANTICS_MISMATCH");

        var valueIdentitySha256 = H(
            "P10_EXPECTED_TYPED_VALUE_IDENTITY_V3",
            atom.IdentitySha256,
            transitionLeg,
            atom.TransitionKind ?? "~",
            atom.CollectionSemantics ?? "~",
            atom.AtomKind,
            atom.ValueType,
            atom.ValueState,
            atom.CanonicalValue,
            I(atom.DecimalScale));
        if (!string.Equals(
                atom.ValueIdentitySha256,
                valueIdentitySha256,
                StringComparison.Ordinal))
            throw Invalid("ATOM_VALUE_IDENTITY_SHA256_MISMATCH");
        var atomSemanticSha256 = H(
            StatisticReconciliationExpectedTypedCompiler.AtomSchemaVersion,
            atom.IdentitySha256,
            transitionLeg,
            atom.TransitionKind ?? "~",
            atom.CollectionSemantics ?? "~",
            atom.AtomKind,
            atom.ValueType,
            atom.ValueState,
            atom.CanonicalValue,
            I(atom.DecimalScale),
            I(atom.OccurrenceCount),
            I(atom.ReportCount),
            I(atom.RowCount),
            I(atom.NumericValueCount),
            atom.ValueIdentitySha256);
        if (!string.Equals(atom.AtomSemanticSha256, atomSemanticSha256, StringComparison.Ordinal))
            throw Invalid("ATOM_SEMANTIC_SHA256_MISMATCH");
    }
    private static void ValidateCatalogPins(
        StatisticReconciliationObservationCatalogPins pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var rebuilt = StatisticReconciliationExpectedCatalogPins.Create(
            pins.P9CatalogVersion,
            pins.P9CatalogRawSha256,
            pins.P9CatalogSemanticSha256,
            pins.P9SchemaRawSha256,
            pins.P9SchemaSemanticSha256,
            pins.P9StageLockSha256,
            pins.CandidateChainId,
            pins.CandidatePromptId,
            pins.CandidateCatalogVersion,
            pins.CandidateCatalogRawSha256,
            pins.CandidateCatalogSemanticSha256,
            pins.CandidateSchemaRawSha256,
            pins.CandidateSchemaSemanticSha256,
            pins.CandidateStageLockSha256);
        if (!string.Equals(
                rebuilt.CatalogPinSetSha256,
                pins.CatalogPinSetSha256,
                StringComparison.Ordinal))
            throw Invalid("CATALOG_PIN_SET_SHA256_MISMATCH");
    }

    private static void ValidatePayloadExclusivity(
        StatisticReconciliationObservation document)
    {
        var present = new object?[]
        {
            document.SourceDecision,
            document.ConfigurationPin,
            document.LineagePin,
            document.MetricPlan,
            document.Atom,
            document.Commit
        }.Count(value => value is not null);
        if (present != 1)
            throw Invalid("RECORD_PAYLOAD_EXCLUSIVITY_INVALID");
        var valid = document.RecordKind switch
        {
            StatisticReconciliationObservationRecordKinds.SourceDecision =>
                document.SourceDecision is not null,
            StatisticReconciliationObservationRecordKinds.ConfigurationPin =>
                document.ConfigurationPin is not null,
            StatisticReconciliationObservationRecordKinds.LineagePin =>
                document.LineagePin is not null,
            StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan =>
                document.MetricPlan is not null,
            StatisticReconciliationObservationRecordKinds.ExpectedAtom =>
                document.Atom is not null,
            StatisticReconciliationObservationRecordKinds.GenerationCommit =>
                document.Commit is not null,
            _ => false
        };
        if (!valid)
            throw Invalid("RECORD_KIND_PAYLOAD_MISMATCH");
    }

    private static string BuildDocumentId(StatisticReconciliationObservation document)
        => document.RecordKind switch
        {
            StatisticReconciliationObservationRecordKinds.SourceDecision => H(
                "P10_EXPECTED_OBSERVATION_SOURCE_ID_V1",
                document.ReconciliationId,
                document.GenerationId,
                document.SourceDecision!.StableSourceId,
                I(document.SourceDecision.PayloadRevision),
                I(document.SourceDecision.LifecycleRevision)),
            StatisticReconciliationObservationRecordKinds.ConfigurationPin => H(
                "P10_EXPECTED_OBSERVATION_CONFIG_ID_V1",
                document.ReconciliationId,
                document.GenerationId,
                document.ConfigurationPin!.Kind,
                document.ConfigurationPin.ConfigId,
                document.ConfigurationPin.VersionId,
                I(document.ConfigurationPin.Revision)),
            StatisticReconciliationObservationRecordKinds.LineagePin => H(
                "P10_EXPECTED_OBSERVATION_LINEAGE_ID_V1",
                document.ReconciliationId,
                document.GenerationId,
                document.LineagePin!.Layer,
                document.LineagePin.OwnerId,
                document.LineagePin.VersionId,
                I(document.LineagePin.Revision)),
            StatisticReconciliationObservationRecordKinds.ExpectedMetricPlan => H(
                "P10_EXPECTED_OBSERVATION_METRIC_PLAN_ID_V2",
                document.ReconciliationId,
                document.GenerationId,
                document.MetricPlan!.IdentitySha256),
            StatisticReconciliationObservationRecordKinds.ExpectedAtom => H(
                "P10_EXPECTED_OBSERVATION_ATOM_ID_V2",
                document.ReconciliationId,
                document.GenerationId,
                document.Atom!.IdentitySha256,
                document.Atom.AtomKind,
                document.Atom.ValueIdentitySha256),
            StatisticReconciliationObservationRecordKinds.GenerationCommit => H(
                "P10_EXPECTED_OBSERVATION_COMMIT_ID_V1",
                document.ReconciliationId,
                document.GenerationId),
            _ => throw Invalid("RECORD_KIND_INVALID")
        };

    private static IEnumerable<string> CommonDocumentFields(
        StatisticReconciliationObservation document)
        =>
        [
            document.RecordKind,
            document.ReconciliationId,
            document.ImmutableIdentitySha256,
            document.ImmutableHeaderSha256,
            document.GenerationId,
            document.GenerationSemanticSha256,
            document.AlgorithmRevision,
            document.AlgorithmSha256,
            document.SourceSetSha256,
            document.TypedSemanticSha256,
            document.MetricPlanSha256,
            document.InputBindingSha256,
            document.LifecycleSemanticSha256,
            document.ContributionSemanticSha256,
            document.WorkId,
            document.ScopeAssignmentId,
            document.PeriodKey,
            document.PeriodInstanceKey,
            document.ConceptKey,
            document.Grain,
            document.TimeAxis,
            document.FilterSha256,
            document.DynamicFormVersionId,
            document.DynamicFormSchemaSha256,
            document.RuntimeKind ?? "~",
            document.FlowTemplateVersionId ?? "~",
            document.FlowPayloadSha256 ?? "~",
            document.FlowInstanceId ?? "~",
            document.ExecutionEpochId ?? "~",
            I(document.ExecutionEpoch),
            I(document.ExecutionEpochRevision),
            document.P8ConfigurationOwnerId,
            document.P8ConfigurationBundleSha256,
            document.CatalogPins.CatalogPinSetSha256
        ];

    internal static string HeaderFingerprint(StatisticReconciliationObservation document)
    {
        var pins = document.CatalogPins;
        return H(
            "P10_EXPECTED_OBSERVATION_HEADER_COHERENCE_V1",
            new[]
            {
                document.SchemaVersion,
                document.ReconciliationId,
                document.ImmutableIdentitySha256,
                document.ImmutableHeaderSha256,
                document.GenerationId,
                document.GenerationSemanticSha256,
                document.AlgorithmRevision,
                document.AlgorithmSha256,
                document.SourceSetSha256,
                document.TypedSemanticSha256,
                document.MetricPlanSha256,
                document.InputBindingSha256,
                document.LifecycleSemanticSha256,
                document.ContributionSemanticSha256,
                document.TenantUnitId ?? "~",
                document.WorkId,
                document.ScopeAssignmentId,
                document.PeriodKey,
                document.PeriodInstanceKey,
                document.ConceptKey,
                document.Grain,
                document.TimeAxis,
                document.FilterSha256,
                document.DynamicFormVersionId,
                document.DynamicFormSchemaSha256,
                document.FlowTemplateVersionId,
                document.FlowPayloadSha256,
                document.FlowInstanceId,
                document.ExecutionEpochId,
                I(document.ExecutionEpoch),
                I(document.ExecutionEpochRevision),
                document.P8ConfigurationOwnerId,
                document.P8ConfigurationBundleSha256,
                pins.P9CatalogVersion,
                pins.P9CatalogRawSha256,
                pins.P9CatalogSemanticSha256,
                pins.P9SchemaRawSha256,
                pins.P9SchemaSemanticSha256,
                pins.P9StageLockSha256,
                pins.CandidateChainId,
                pins.CandidatePromptId,
                pins.CandidateCatalogVersion,
                pins.CandidateCatalogRawSha256,
                pins.CandidateCatalogSemanticSha256,
                pins.CandidateSchemaRawSha256,
                pins.CandidateSchemaSemanticSha256,
                pins.CandidateStageLockSha256,
                pins.CatalogPinSetSha256,
                document.CreatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)
            });
    }

    private static int Count(
        IEnumerable<StatisticReconciliationObservation> documents,
        string recordKind)
        => documents.Count(item =>
            string.Equals(item.RecordKind, recordKind, StringComparison.Ordinal));

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string H(string domain, IEnumerable<string> fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string I(long? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "~";

    private static StatisticReconciliationExpectedObservationIntegrityException Invalid(
        string reason)
        => new(reason);
}

internal sealed class StatisticReconciliationExpectedObservationIntegrityException(
    string reason)
    : Exception(reason)
{
    internal string Reason { get; } = reason;
}
