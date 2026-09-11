using System.Collections.Immutable;
using System.Globalization;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed record WrongLedgerControlEvidence(
    string Detector,
    string BaselineTypedSemanticSha256,
    string MutatedTypedSemanticSha256,
    string BaselineGenerationSemanticSha256,
    string MutatedGenerationSemanticSha256,
    string BaselineGenerationId,
    string MutatedGenerationId,
    string MutatedAtomSemanticSha256,
    StatisticReconciliationExpectedCompiledGeneration PoisonedProductGeneration);

/// <summary>
/// Deliberately corrupts one expected atom in the T16 test assembly only.
/// It computes the counterfactual hashes as evidence, but never constructs a
/// coherent product generation from the mutated ledger. Product code receives
/// only a poisoned copy whose retained hashes must be rejected before I/O.
/// </summary>
internal static class WrongLedgerControl
{
    internal const string Detector = "EXPECTED_SEMANTIC_HASH_MISMATCH";
    internal const string MutationMarker = "P10-T16-TEST-ONLY-WRONG-LEDGER";

    internal static WrongLedgerControlEvidence Mutate(
        StatisticReconciliationExpectedCompiledGeneration baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var index = -1;
        for (var candidateIndex = 0;
             candidateIndex < baseline.Atoms.Length;
             candidateIndex++)
        {
            if (baseline.Atoms[candidateIndex].ValueState !=
                StatisticReconciliationExpectedValueStates.Value)
                continue;
            index = candidateIndex;
            break;
        }
        if (index < 0)
            throw new InvalidOperationException("A value atom is required by the T16 control.");

        var original = baseline.Atoms[index];
        var canonical = MutationMarker;
        if (StringComparer.Ordinal.Equals(original.CanonicalValue, canonical))
            canonical += "-2";

        var valueIdentity = H(
            "P10_EXPECTED_TYPED_VALUE_IDENTITY_V1",
            original.Identity.IdentitySha256,
            original.AtomKind,
            original.ValueType,
            original.ValueState,
            canonical,
            I(original.DecimalScale));
        var atomSemantic = H(
            StatisticReconciliationExpectedTypedCompiler.AtomSchemaVersion,
            original.Identity.IdentitySha256,
            original.AtomKind,
            original.ValueType,
            original.ValueState,
            canonical,
            I(original.DecimalScale),
            I(original.OccurrenceCount),
            I(original.ReportCount),
            I(original.RowCount),
            I(original.NumericValueCount),
            valueIdentity);
        var counterfactualAtom = original with
        {
            CanonicalValue = canonical,
            ValueIdentitySha256 = valueIdentity,
            AtomSemanticSha256 = atomSemantic
        };
        var counterfactualAtoms = baseline.Atoms
            .SetItem(index, counterfactualAtom)
            .OrderBy(item => item.Identity.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(item => item.AtomKind, StringComparer.Ordinal)
            .ThenBy(item => item.ValueIdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var typedSemantic = H(
            "P10_EXPECTED_TYPED_LEDGER_V1",
            counterfactualAtoms.Select(item => item.AtomSemanticSha256));
        var generationSemantic = H(
            StatisticReconciliationExpectedTypedCompiler.GenerationSchemaVersion,
            baseline.ContextPin.ReconciliationId,
            baseline.SourcePlan.BoundInputs.InputFingerprints.InputBindingSha256,
            baseline.SourcePlan.SourceSetSha256,
            StatisticReconciliationExpectedTypedCompiler.AlgorithmRevision,
            StatisticReconciliationExpectedTypedCompiler.AlgorithmSha256,
            baseline.CatalogPins.CatalogPinSetSha256,
            baseline.MetricPlanSha256,
            typedSemantic);
        var generationId = H(
            "P10_EXPECTED_GENERATION_ID_V1",
            baseline.ContextPin.ReconciliationId,
            generationSemantic);

        // Retaining the original hashes is intentional: this is the only
        // product-shaped poisoned object and the product store must reject it.
        var poisoned = baseline with
        {
            Atoms = baseline.Atoms.SetItem(
                index,
                original with { CanonicalValue = canonical })
        };

        return new WrongLedgerControlEvidence(
            Detector,
            baseline.TypedSemanticSha256,
            typedSemantic,
            baseline.GenerationSemanticSha256,
            generationSemantic,
            baseline.GenerationId,
            generationId,
            atomSemantic,
            poisoned);
    }

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string H(string domain, IEnumerable<string> fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string I(long value)
        => value.ToString(CultureInfo.InvariantCulture);
}

internal sealed class WrongLedgerRejectingBackend
    : IStatisticReconciliationExpectedObservationBackend
{
    internal int ReadCalls { get; private set; }
    internal int ContentWriteCalls { get; private set; }
    internal int CommitWriteCalls { get; private set; }

    public Task<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
    {
        ReadCalls++;
        return Task.FromResult<IReadOnlyList<
            StatisticReconciliationExpectedStoredObservation>>([]);
    }

    public Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        ContentWriteCalls++;
        return Task.CompletedTask;
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        CommitWriteCalls++;
        return Task.CompletedTask;
    }
}
