using System.Collections.Immutable;
using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

internal static class StatisticReconciliationActualLifecycleEvidenceIntegrity
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    internal static StatisticReconciliationActualLifecycleEvidence Create(
        StatisticReconciliationActualLifecycleBuildInput input,
        StatisticReconciliationLifecycleRequest request,
        StatisticReconciliationLifecycleResult result,
        StatisticReconciliationActualLifecyclePriorCompaction priorCompaction)
    {
        var observationCount = RequireObservationBudget(
            request.Sources.Length,
            request.Actuals.Length);
        var evidenceRows = request.Sources
            .Select(source => CreateRow(
                StatisticReconciliationActualLifecycleLedgerSchema.SourceKind,
                input,
                source.ObservationId,
                source.SemanticSha256,
                source))
            .Concat(request.Actuals.Select(actual => CreateRow(
                StatisticReconciliationActualLifecycleLedgerSchema.ActualKind,
                input,
                actual.ObservationId,
                actual.SemanticSha256,
                actual)))
            .Append(CreateRow(
                StatisticReconciliationActualLifecycleLedgerSchema.ResultKind,
                input,
                $"RESULT:{input.BaseCoherentGenerationId}",
                result.ResultSemanticSha256,
                result))
            .OrderBy(static row => row.RecordKind, StringComparer.Ordinal)
            .ThenBy(static row => row.ObservationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var rowSetSha = HashRowSet(evidenceRows);
        var prior = input.Prior;
        var audit = input.P9ContributionAudit;
        var expected = input.Expected;
        var expectedDecisionSetSha = ComputeExpectedDecisionSetSha(expected);
        var mismatchSetSha = ComputeResultMismatchSetSha(result);
        var unsigned = new StatisticReconciliationActualLifecycleManifest(
            StatisticReconciliationActualLifecycleLedgerSchema.Version,
            StatisticReconciliationActualLifecycleLedgerSchema.MembershipUnit,
            input.ReconciliationId,
            input.BaseCoherentGenerationId,
            input.BaseCoherentGenerationSha256,
            input.ComparisonBindingSha256,
            expected.LifecycleManifestSha256,
            expectedDecisionSetSha,
            expected.DoubleCollectProofSha256,
            expected.Rows.Length,
            expected.Binding.GenerationId,
            expected.Binding.GenerationSemanticSha256,
            expected.Binding.ManifestSha256,
            expected.Binding.DocumentCount,
            expected.Binding.MetricPlanSha256,
            expected.Binding.MetricPlanEntryCount,
            expected.Binding.MembershipSemanticSha256,
            expected.Binding.RuntimeKind,
            input.Source.CaptureSemanticSha256,
            input.Direct.CaptureSemanticSha256,
            input.Source.SourceSetSha256,
            input.Direct.ActualSourceSetSha256,
            input.Direct.BoundarySemanticSha256,
            audit.SnapshotSemanticSha256,
            audit.LedgerHash,
            audit.ReversalBaselineHash,
            audit.Reversal?.AuditHash,
            audit,
            prior?.BaseCoherentGenerationId,
            prior?.BaseCoherentGenerationSha256,
            prior?.CommittedGenerationId,
            prior?.CommittedGenerationSha256,
            prior?.FinalVerdictGenerationId,
            prior?.FinalVerdictGenerationSha256,
            prior?.LifecycleManifestSha256,
            prior?.LifecycleObservationCount ?? 0,
            prior?.LifecycleRowSetSha256,
            prior?.P9RunId,
            prior?.P9GenerationId,
            prior?.P9GenerationSha256,
            prior?.P9ContributionLedgerSha256,
            prior?.P9ReversalBaselineSha256,
            prior?.P9AuditSnapshotSha256,
            prior is null
                ? null
                : StatisticReconciliationActualLifecycleLedgerSchema
                    .CompactedPriorBaseline,
            priorCompaction.ProofSha256,
            priorCompaction.IdentityCount,
            result.RequestSemanticSha256,
            result.ResultSemanticSha256,
            result.RootCause,
            result.MissingCount,
            result.ExtraCount,
            mismatchSetSha,
            request.Sources.Length,
            request.Actuals.Length,
            observationCount,
            rowSetSha,
            string.Empty);
        var manifest = unsigned with
        {
            ManifestSha256 = ComputeManifestSha(unsigned)
        };
        var manifestRow = CreateRow(
            StatisticReconciliationActualLifecycleLedgerSchema.ManifestKind,
            input,
            $"MANIFEST:{input.BaseCoherentGenerationId}",
            manifest.ManifestSha256,
            manifest);
        var rows = evidenceRows.Append(manifestRow)
            .OrderBy(static row => row.RecordKind, StringComparer.Ordinal)
            .ThenBy(static row => row.ObservationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var evidence = new StatisticReconciliationActualLifecycleEvidence(
            request,
            result,
            manifest,
            rows);
        Validate(evidence);
        return evidence;
    }

    internal static void Validate(
        StatisticReconciliationActualLifecycleEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var manifest = evidence.Manifest;
        StatRunLifecycleContributionAuditCanonical.RequireValid(
            manifest.P9ContributionAudit);
        var expectedObservationCount = RequireObservationBudget(
            evidence.Request.Sources.Length,
            evidence.Request.Actuals.Length);
        var hasPrior = manifest.PriorLifecycleManifestSha256 is not null;
        var compactionValid = hasPrior
            ? string.Equals(
                  manifest.PriorCompactionSchema,
                  StatisticReconciliationActualLifecycleLedgerSchema
                      .CompactedPriorBaseline,
                  StringComparison.Ordinal) &&
              IsSha(manifest.PriorCompactionProofSha256) &&
              manifest.PriorCompactionIdentityCount > 0 &&
              string.Equals(
                  manifest.PriorCompactionProofSha256,
                  ComputePriorCompactionProofSha(manifest, evidence.Request),
                  StringComparison.Ordinal)
            : manifest.PriorCompactionSchema is null &&
              manifest.PriorCompactionProofSha256 is null &&
              manifest.PriorCompactionIdentityCount == 0;
        if (!string.Equals(
                manifest.SchemaVersion,
                StatisticReconciliationActualLifecycleLedgerSchema.Version,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.MembershipUnit,
                StatisticReconciliationActualLifecycleLedgerSchema.MembershipUnit,
                StringComparison.Ordinal) ||
            manifest.ExpectedLifecycleRowCount <= 0 ||
            manifest.ExpectedDocumentCount <= 0 ||
            manifest.ExpectedMetricPlanEntryCount <= 0 ||
            string.IsNullOrWhiteSpace(manifest.ExpectedRuntimeKind) ||
            !IsSha(manifest.ExpectedLifecycleProjectionSha256) ||
            !IsSha(manifest.ExpectedLifecycleDecisionSetSha256) ||
            !IsSha(manifest.ExpectedDoubleCollectProofSha256) ||
            !IsSha(manifest.ExpectedGenerationId) ||
            !IsSha(manifest.ExpectedGenerationSha256) ||
            !IsSha(manifest.ExpectedManifestSha256) ||
            !IsSha(manifest.ExpectedMetricPlanSha256) ||
            !IsSha(manifest.ExpectedMembershipSha256) ||
            !IsSha(manifest.ResultMismatchSetSha256) ||
            manifest.ResultMissingCount < 0 ||
            manifest.ResultExtraCount < 0 ||
            manifest.ObservationCount != expectedObservationCount ||
            !compactionValid ||
            !string.Equals(
                manifest.P9AuditSnapshotSha256,
                manifest.P9ContributionAudit.SnapshotSemanticSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.P9ContributionLedgerSha256,
                manifest.P9ContributionAudit.LedgerHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.P9ReversalBaselineSha256,
                manifest.P9ContributionAudit.ReversalBaselineHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.P9ReversalAuditSha256,
                manifest.P9ContributionAudit.Reversal?.AuditHash,
                StringComparison.Ordinal) ||
            evidence.Rows.Length != manifest.ObservationCount ||
            evidence.Request.Sources.Length != manifest.SourceObservationCount ||
            evidence.Request.Actuals.Length != manifest.ActualObservationCount ||
            !string.Equals(
                evidence.Result.RequestSemanticSha256,
                manifest.RequestSemanticSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.Result.ResultSemanticSha256,
                manifest.ResultSemanticSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.Result.RootCause,
                manifest.ResultRootCause,
                StringComparison.Ordinal) ||
            evidence.Result.MissingCount != manifest.ResultMissingCount ||
            evidence.Result.ExtraCount != manifest.ResultExtraCount ||
            !string.Equals(
                ComputeResultMismatchSetSha(evidence.Result),
                manifest.ResultMismatchSetSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                ComputeManifestSha(manifest),
                manifest.ManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Lifecycle manifest cardinality or identity is invalid.");
        }

        foreach (var row in evidence.Rows)
        {
            ValidateRow(row, manifest);
        }
        var nonManifestRows = evidence.Rows
            .Where(static row => row.RecordKind !=
                StatisticReconciliationActualLifecycleLedgerSchema.ManifestKind)
            .ToImmutableArray();
        if (!string.Equals(
                HashRowSet(nonManifestRows),
                manifest.LifecycleRowSetSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Lifecycle row-set hash is invalid.");
        }

        var manifestRows = evidence.Rows.Where(static row => row.RecordKind ==
            StatisticReconciliationActualLifecycleLedgerSchema.ManifestKind).ToArray();
        if (manifestRows.Length != 1 ||
            !string.Equals(
                manifestRows[0].SemanticSha256,
                manifest.ManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Lifecycle manifest row is missing or ambiguous.");
        }

        var rebuilt = evidence.Request.Sources.IsEmpty
            ? CreateEmptyResult(evidence.Request)
            : new StatisticReconciliationLifecycleEvaluator().Evaluate(evidence.Request);
        if (!string.Equals(
                Serialize(rebuilt),
                Serialize(evidence.Result),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Lifecycle result does not recompute from its persisted timeline.");
        }
    }

    internal static StatisticReconciliationActualLifecycleEvidence Rehydrate(
        ImmutableArray<StatisticReconciliationActualLifecycleLedgerRow> rows)
    {
        if (rows.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("Lifecycle evidence rows are empty.");
        }
        var ordered = rows.OrderBy(static row => row.RecordKind, StringComparer.Ordinal)
            .ThenBy(static row => row.ObservationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var manifest = Deserialize<StatisticReconciliationActualLifecycleManifest>(
            ordered.Single(static row => row.RecordKind ==
                StatisticReconciliationActualLifecycleLedgerSchema.ManifestKind));
        var sources = ordered.Where(static row => row.RecordKind ==
                StatisticReconciliationActualLifecycleLedgerSchema.SourceKind)
            .Select(Deserialize<StatisticReconciliationLifecycleSourceState>)
            .ToImmutableArray();
        var actuals = ordered.Where(static row => row.RecordKind ==
                StatisticReconciliationActualLifecycleLedgerSchema.ActualKind)
            .Select(Deserialize<StatisticReconciliationLifecycleActualContribution>)
            .ToImmutableArray();
        var result = Deserialize<StatisticReconciliationLifecycleResult>(
            ordered.Single(static row => row.RecordKind ==
                StatisticReconciliationActualLifecycleLedgerSchema.ResultKind));
        var request = new StatisticReconciliationLifecycleRequest(
            manifest.ReconciliationId,
            manifest.ComparisonBindingSha256,
            sources,
            actuals);
        var evidence = new StatisticReconciliationActualLifecycleEvidence(
            request,
            result,
            manifest,
            ordered);
        Validate(evidence);
        return evidence;
    }

    internal static string HashRowSet(
        IEnumerable<StatisticReconciliationActualLifecycleLedgerRow> rows)
    {
        var ordered = rows
            .OrderBy(static row => row.RecordKind, StringComparer.Ordinal)
            .ThenBy(static row => row.ObservationId, StringComparer.Ordinal)
            .Select(static row => row.DocumentSemanticSha256);
        return StatisticReconciliationLifecycleCanonical.Hash(
            new[] { "P10_LIFECYCLE_ROW_SET_V3" }
                .Concat(ordered)
                .ToArray());
    }

    internal static string ComputeManifestSha(
        StatisticReconciliationActualLifecycleManifest value) =>
        StatisticReconciliationLifecycleCanonical.Hash(
            "P10_ACTUAL_LIFECYCLE_MANIFEST_V5",
            value.SchemaVersion,
            value.MembershipUnit,
            value.ReconciliationId,
            value.BaseCoherentGenerationId,
            value.BaseCoherentGenerationSha256,
            value.ComparisonBindingSha256,
            value.ExpectedLifecycleProjectionSha256,
            value.ExpectedLifecycleDecisionSetSha256,
            value.ExpectedDoubleCollectProofSha256,
            value.ExpectedLifecycleRowCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.ExpectedGenerationId,
            value.ExpectedGenerationSha256,
            value.ExpectedManifestSha256,
            value.ExpectedDocumentCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.ExpectedMetricPlanSha256,
            value.ExpectedMetricPlanEntryCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.ExpectedMembershipSha256,
            value.ExpectedRuntimeKind,
            value.SourceCaptureSemanticSha256,
            value.DirectCaptureSemanticSha256,
            value.SourceSetSha256,
            value.DirectSourceSetSha256,
            value.DirectBoundarySemanticSha256,
            value.P9AuditSnapshotSha256,
            value.P9ContributionLedgerSha256,
            value.P9ReversalBaselineSha256,
            value.P9ReversalAuditSha256,
            value.PriorBaseCoherentGenerationId,
            value.PriorBaseCoherentGenerationSha256,
            value.PriorCommittedGenerationId,
            value.PriorCommittedGenerationSha256,
            value.PriorFinalVerdictGenerationId,
            value.PriorFinalVerdictGenerationSha256,
            value.PriorLifecycleManifestSha256,
            value.PriorLifecycleObservationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.PriorLifecycleRowSetSha256,
            value.PriorP9RunId,
            value.PriorP9GenerationId,
            value.PriorP9GenerationSha256,
            value.PriorP9ContributionLedgerSha256,
            value.PriorP9ReversalBaselineSha256,
            value.PriorP9AuditSnapshotSha256,
            value.PriorCompactionSchema,
            value.PriorCompactionProofSha256,
            value.PriorCompactionIdentityCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.RequestSemanticSha256,
            value.ResultSemanticSha256,
            value.ResultRootCause,
            value.ResultMissingCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.ResultExtraCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.ResultMismatchSetSha256,
            value.SourceObservationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.ActualObservationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.ObservationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.LifecycleRowSetSha256);

    internal static int RequireObservationBudget(
        int sourceObservationCount,
        int actualObservationCount)
    {
        if (sourceObservationCount < 0 || actualObservationCount < 0)
        {
            throw new InvalidOperationException(
                "P10_LIFECYCLE_OBSERVATION_LIMIT");
        }
        var count = (long)sourceObservationCount + actualObservationCount + 2L;
        if (count > StatisticReconciliationActualLifecycleMongoLedger
                .MaxObservationRows)
        {
            throw new InvalidOperationException(
                "P10_LIFECYCLE_OBSERVATION_LIMIT");
        }
        return checked((int)count);
    }

    internal static string ComputePriorCompactionPairBinding(
        StatisticReconciliationActualLifecyclePriorGeneration prior,
        string stableSourceId,
        string originalSourceSemanticSha256,
        string originalActualSemanticSha256) =>
        ComputePriorCompactionPairBinding(
            prior.LifecycleManifestSha256,
            prior.LifecycleRowSetSha256,
            prior.LifecycleObservationCount,
            prior.CommittedGenerationId,
            prior.CommittedGenerationSha256,
            prior.FinalVerdictGenerationId,
            prior.FinalVerdictGenerationSha256,
            prior.P9RunId,
            prior.P9GenerationId,
            prior.P9GenerationSha256,
            prior.P9AuditSnapshotSha256,
            stableSourceId,
            originalSourceSemanticSha256,
            originalActualSemanticSha256);

    internal static string ComputePriorCompactionProofSha(
        StatisticReconciliationActualLifecyclePriorGeneration prior,
        ImmutableArray<StatisticReconciliationLifecycleSourceState> sources,
        ImmutableArray<StatisticReconciliationLifecycleActualContribution> actuals) =>
        ComputePriorCompactionProofSha(
            prior.LifecycleManifestSha256,
            prior.LifecycleRowSetSha256,
            prior.LifecycleObservationCount,
            prior.CommittedGenerationId,
            prior.CommittedGenerationSha256,
            prior.FinalVerdictGenerationId,
            prior.FinalVerdictGenerationSha256,
            prior.P9RunId,
            prior.P9GenerationId,
            prior.P9GenerationSha256,
            prior.P9AuditSnapshotSha256,
            sources,
            actuals,
            sources.Length);

    private static string ComputePriorCompactionProofSha(
        StatisticReconciliationActualLifecycleManifest manifest,
        StatisticReconciliationLifecycleRequest request) =>
        ComputePriorCompactionProofSha(
            manifest.PriorLifecycleManifestSha256,
            manifest.PriorLifecycleRowSetSha256,
            manifest.PriorLifecycleObservationCount,
            manifest.PriorCommittedGenerationId,
            manifest.PriorCommittedGenerationSha256,
            manifest.PriorFinalVerdictGenerationId,
            manifest.PriorFinalVerdictGenerationSha256,
            manifest.PriorP9RunId,
            manifest.PriorP9GenerationId,
            manifest.PriorP9GenerationSha256,
            manifest.PriorP9AuditSnapshotSha256,
            request.Sources,
            request.Actuals,
            manifest.PriorCompactionIdentityCount);

    private static string ComputePriorCompactionProofSha(
        string? priorManifestSha256,
        string? priorRowSetSha256,
        int priorObservationCount,
        string? priorCommittedGenerationId,
        string? priorCommittedGenerationSha256,
        string? priorFinalVerdictGenerationId,
        string? priorFinalVerdictGenerationSha256,
        string? priorP9RunId,
        string? priorP9GenerationId,
        string? priorP9GenerationSha256,
        string? priorP9AuditSnapshotSha256,
        IEnumerable<StatisticReconciliationLifecycleSourceState> sources,
        IEnumerable<StatisticReconciliationLifecycleActualContribution> actuals,
        int expectedIdentityCount)
    {
        var actualBySource = actuals
            .GroupBy(static item => item.SourceObservationId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key,
                static group => group.ToArray(), StringComparer.Ordinal);
        var pairProofs = new List<(string StableSourceId, string Proof)>();
        foreach (var source in sources.Where(static item => item.Sequence == 1))
        {
            if (!actualBySource.TryGetValue(source.ObservationId, out var matches) ||
                matches.Length != 1 ||
                !IsSha(source.SourceGenerationId) ||
                !IsSha(matches[0].GenerationId))
            {
                continue;
            }
            var actual = matches[0];
            var pairBinding = ComputePriorCompactionPairBinding(
                priorManifestSha256,
                priorRowSetSha256,
                priorObservationCount,
                priorCommittedGenerationId,
                priorCommittedGenerationSha256,
                priorFinalVerdictGenerationId,
                priorFinalVerdictGenerationSha256,
                priorP9RunId,
                priorP9GenerationId,
                priorP9GenerationSha256,
                priorP9AuditSnapshotSha256,
                source.StableSourceId,
                source.SourceGenerationId,
                actual.GenerationId);
            if (!string.Equals(
                    source.SourceGenerationSha256,
                    pairBinding,
                    StringComparison.Ordinal))
            {
                continue;
            }
            var expectedSourceObservationId =
                StatisticReconciliationLifecycleCanonical.Hash(
                    StatisticReconciliationActualLifecycleLedgerSchema
                        .CompactedPriorBaseline,
                    "SOURCE_OBSERVATION",
                    pairBinding);
            var expectedActualObservationId =
                StatisticReconciliationLifecycleCanonical.Hash(
                    StatisticReconciliationActualLifecycleLedgerSchema
                        .CompactedPriorBaseline,
                    "ACTUAL_OBSERVATION",
                    pairBinding);
            if (!string.Equals(source.ObservationId,
                    expectedSourceObservationId, StringComparison.Ordinal) ||
                !string.Equals(source.EventKind,
                    StatisticReconciliationLifecycleEventKinds.Rebuild,
                    StringComparison.Ordinal) ||
                !string.Equals(actual.ObservationId,
                    expectedActualObservationId, StringComparison.Ordinal) ||
                !string.Equals(source.StableSourceId,
                    actual.StableSourceId, StringComparison.Ordinal) ||
                actual.SupersedesGenerationId is not null ||
                actual.PriorEffectiveGenerationSha256 is not null ||
                !string.Equals(actual.CanonicalDelta,
                    actual.CanonicalContribution, StringComparison.Ordinal) ||
                !actual.EvidenceComplete)
            {
                throw new InvalidOperationException(
                    "Compacted prior baseline pair is invalid.");
            }
            pairProofs.Add((source.StableSourceId,
                StatisticReconciliationLifecycleCanonical.Hash(
                    StatisticReconciliationActualLifecycleLedgerSchema
                        .CompactedPriorBaseline,
                    "PAIR_PROOF",
                    pairBinding,
                    source.SemanticSha256,
                    actual.SemanticSha256)));
        }
        if (expectedIdentityCount <= 0 ||
            pairProofs.Count != expectedIdentityCount ||
            pairProofs.Select(static item => item.StableSourceId)
                .Distinct(StringComparer.Ordinal).Count() != pairProofs.Count)
        {
            throw new InvalidOperationException(
                "Compacted prior baseline cardinality is invalid.");
        }
        var orderedPairProofSha = StatisticReconciliationLifecycleCanonical
            .HashSequence(pairProofs
                .OrderBy(static item => item.StableSourceId, StringComparer.Ordinal)
                .Select(static item => item.Proof));
        return StatisticReconciliationLifecycleCanonical.Hash(
            StatisticReconciliationActualLifecycleLedgerSchema
                .CompactedPriorBaseline,
            "PROOF",
            priorManifestSha256,
            priorRowSetSha256,
            priorObservationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            priorCommittedGenerationId,
            priorCommittedGenerationSha256,
            priorFinalVerdictGenerationId,
            priorFinalVerdictGenerationSha256,
            priorP9RunId,
            priorP9GenerationId,
            priorP9GenerationSha256,
            priorP9AuditSnapshotSha256,
            expectedIdentityCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            orderedPairProofSha);
    }

    private static string ComputePriorCompactionPairBinding(
        string? priorManifestSha256,
        string? priorRowSetSha256,
        int priorObservationCount,
        string? priorCommittedGenerationId,
        string? priorCommittedGenerationSha256,
        string? priorFinalVerdictGenerationId,
        string? priorFinalVerdictGenerationSha256,
        string? priorP9RunId,
        string? priorP9GenerationId,
        string? priorP9GenerationSha256,
        string? priorP9AuditSnapshotSha256,
        string stableSourceId,
        string originalSourceSemanticSha256,
        string originalActualSemanticSha256) =>
        StatisticReconciliationLifecycleCanonical.Hash(
            StatisticReconciliationActualLifecycleLedgerSchema
                .CompactedPriorBaseline,
            "PAIR_BINDING",
            priorManifestSha256,
            priorRowSetSha256,
            priorObservationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            priorCommittedGenerationId,
            priorCommittedGenerationSha256,
            priorFinalVerdictGenerationId,
            priorFinalVerdictGenerationSha256,
            priorP9RunId,
            priorP9GenerationId,
            priorP9GenerationSha256,
            priorP9AuditSnapshotSha256,
            stableSourceId,
            originalSourceSemanticSha256,
            originalActualSemanticSha256);


    internal static string ComputeResultMismatchSetSha(
        StatisticReconciliationLifecycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var mismatches = result.Steps.IsDefault
            ? throw new InvalidOperationException(
                "Lifecycle result steps are required.")
            : result.Steps.Where(static step => !step.Exact)
                .OrderBy(static step => step.StableSourceId,
                    StringComparer.Ordinal)
                .ThenBy(static step => step.Sequence)
                .ThenBy(static step => step.StepSemanticSha256,
                    StringComparer.Ordinal)
                .Select(static step =>
                    StatisticReconciliationLifecycleCanonical.Hash(
                        "P10_LIFECYCLE_REDACTED_MISMATCH_V1",
                        step.StableSourceId,
                        step.RootCause,
                        step.StepSemanticSha256));
        return StatisticReconciliationLifecycleCanonical.Hash(
            new[] { "P10_LIFECYCLE_REDACTED_MISMATCH_SET_V1" }
                .Concat(mismatches)
                .ToArray());
    }

    internal static string ComputeExpectedDecisionSetSha(
        StatisticReconciliationExpectedAuthoritativeLifecycleProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var rows = projection.Rows.IsDefault
            ? throw new InvalidOperationException(
                "Authoritative lifecycle rows are required.")
            : projection.Rows;
        return StatisticReconciliationLifecycleCanonical.Hash(
            new[] { "P10_EXPECTED_LIFECYCLE_DECISION_SET_V1" }
                .Concat(rows
                    .OrderBy(static row => row.SourceStableIdentitySha256,
                        StringComparer.Ordinal)
                    .ThenBy(static row => row.PayloadRevision)
                    .ThenBy(static row => row.ReasonCode,
                        StringComparer.Ordinal)
                    .Select(static row => row.DecisionSemanticSha256))
                .ToArray());
    }

    internal static string RecomputeExpectedLifecycleManifestSha(
        StatisticReconciliationExpectedAuthoritativeLifecycleProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var rows = projection.Rows.IsDefault
            ? throw new InvalidOperationException(
                "Authoritative lifecycle rows are required.")
            : projection.Rows;
        var rowHashes = rows.Select(static row =>
            StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                "P10_EXPECTED_AUTHORITATIVE_LIFECYCLE_ROW_V1",
                row.SourceStableIdentitySha256,
                row.DecisionSemanticSha256,
                row.DocumentSemanticSha256,
                row.RuntimePin.RuntimeSemanticSha256));
        return StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            "P10_EXPECTED_AUTHORITATIVE_LIFECYCLE_MANIFEST_V1",
            rowHashes);
    }
    private static StatisticReconciliationActualLifecycleLedgerRow CreateRow<T>(
        string kind,
        StatisticReconciliationActualLifecycleBuildInput input,
        string observationId,
        string semanticSha256,
        T payload)
    {
        var json = Serialize(payload);
        var payloadSha = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_LEDGER_PAYLOAD_V3",
            kind,
            json);
        var documentSha = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_LEDGER_DOCUMENT_V3",
            StatisticReconciliationActualLifecycleLedgerSchema.Version,
            kind,
            input.ReconciliationId,
            input.BaseCoherentGenerationId,
            input.BaseCoherentGenerationSha256,
            observationId,
            payloadSha,
            semanticSha256);
        return new StatisticReconciliationActualLifecycleLedgerRow(
            StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_LEDGER_ID_V3",
                input.ReconciliationId,
                input.BaseCoherentGenerationId,
                kind,
                observationId),
            StatisticReconciliationActualLifecycleLedgerSchema.Version,
            kind,
            input.ReconciliationId,
            input.BaseCoherentGenerationId,
            input.BaseCoherentGenerationSha256,
            observationId,
            json,
            payloadSha,
            semanticSha256,
            documentSha);
    }

    private static void ValidateRow(
        StatisticReconciliationActualLifecycleLedgerRow row,
        StatisticReconciliationActualLifecycleManifest manifest)
    {
        if (!string.Equals(
                row.SchemaVersion,
                StatisticReconciliationActualLifecycleLedgerSchema.Version,
                StringComparison.Ordinal) ||
            !StatisticReconciliationActualLifecycleLedgerSchema.IsOwnedKind(row.RecordKind) ||
            !string.Equals(row.ReconciliationId, manifest.ReconciliationId, StringComparison.Ordinal) ||
            !string.Equals(row.BaseCoherentGenerationId, manifest.BaseCoherentGenerationId, StringComparison.Ordinal) ||
            !string.Equals(row.BaseCoherentGenerationSha256, manifest.BaseCoherentGenerationSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Lifecycle row envelope is outside its manifest.");
        }
        var payloadSha = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_LEDGER_PAYLOAD_V3",
            row.RecordKind,
            row.PayloadCanonicalJson);
        var documentSha = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_LEDGER_DOCUMENT_V3",
            row.SchemaVersion,
            row.RecordKind,
            row.ReconciliationId,
            row.BaseCoherentGenerationId,
            row.BaseCoherentGenerationSha256,
            row.ObservationId,
            payloadSha,
            row.SemanticSha256);
        var id = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_LEDGER_ID_V3",
            row.ReconciliationId,
            row.BaseCoherentGenerationId,
            row.RecordKind,
            row.ObservationId);
        if (!string.Equals(row.PayloadSha256, payloadSha, StringComparison.Ordinal) ||
            !string.Equals(row.DocumentSemanticSha256, documentSha, StringComparison.Ordinal) ||
            !string.Equals(row.Id, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Lifecycle row integrity hash is invalid.");
        }
        switch (row.RecordKind)
        {
            case StatisticReconciliationActualLifecycleLedgerSchema.SourceKind:
                _ = Deserialize<StatisticReconciliationLifecycleSourceState>(row);
                break;
            case StatisticReconciliationActualLifecycleLedgerSchema.ActualKind:
                _ = Deserialize<StatisticReconciliationLifecycleActualContribution>(row);
                break;
            case StatisticReconciliationActualLifecycleLedgerSchema.ResultKind:
                _ = Deserialize<StatisticReconciliationLifecycleResult>(row);
                break;
            case StatisticReconciliationActualLifecycleLedgerSchema.ManifestKind:
                _ = Deserialize<StatisticReconciliationActualLifecycleManifest>(row);
                break;
            default:
                throw new InvalidOperationException("Unsupported lifecycle row kind.");
        }
    }

    private static T Deserialize<T>(
        StatisticReconciliationActualLifecycleLedgerRow row)
    {
        var value = JsonSerializer.Deserialize<T>(row.PayloadCanonicalJson, JsonOptions)
            ?? throw new InvalidOperationException("Lifecycle row payload is empty.");
        if (!string.Equals(
                Serialize(value),
                row.PayloadCanonicalJson,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Lifecycle row payload is not canonical JSON.");
        }
        return value;
    }

    private static StatisticReconciliationLifecycleResult CreateEmptyResult(
        StatisticReconciliationLifecycleRequest request)
    {
        var emptySha = StatisticReconciliationLifecycleCanonical.HashSequence([]);
        var requestSha = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_REQUEST_V1",
            request.ReconciliationId,
            request.ComparisonBindingSha256,
            emptySha,
            emptySha);
        var resultSha = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_RESULT_V1",
            request.ReconciliationId,
            request.ComparisonBindingSha256,
            StatisticReconciliationLifecycleOutcomes.Matched,
            null,
            "true",
            "0",
            "0",
            "0",
            "0",
            "0",
            emptySha,
            requestSha);
        return new StatisticReconciliationLifecycleResult(
            request.ReconciliationId,
            request.ComparisonBindingSha256,
            StatisticReconciliationLifecycleOutcomes.Matched,
            null,
            true,
            0,
            0,
            0,
            0,
            0,
            [],
            requestSha,
            resultSha);
    }

    private static bool IsSha(string? value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(static character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);
}
