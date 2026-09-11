using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationTrustedLifecycleProofStates
{
    internal const string Valid = "VALID";
    internal const string BindingMissing = "BINDING_MISSING";
    internal const string EvidenceMissing = "EVIDENCE_MISSING";
    internal const string EvidenceInvalid = "EVIDENCE_INVALID";
    internal const string ReplayChanged = "REPLAY_CHANGED";
}

internal sealed record StatisticReconciliationTrustedLifecycleMismatch(
    string IdentitySha256,
    string RootCause,
    string StepSemanticSha256,
    string DescriptorSha256);

internal sealed record StatisticReconciliationTrustedLifecycleProof(
    string State,
    string Outcome,
    string? RootCause,
    int MissingCount,
    int ExtraCount,
    bool EvidenceComplete,
    bool CaptureCoherent,
    string? ManifestSha256,
    int? ObservationCount,
    string? LifecycleRowSetSha256,
    string? ResultSemanticSha256,
    string? MismatchSetSha256,
    ImmutableArray<StatisticReconciliationTrustedLifecycleMismatch> Mismatches,
    string ProofSha256);

internal interface IStatisticReconciliationTrustedLifecycleVerifier
{
    Task<StatisticReconciliationTrustedLifecycleProof> ReadAsync(
        StatisticReconciliationActualAppendResult actual,
        CancellationToken cancellationToken);

    StatisticReconciliationTrustedLifecycleProof RequireStable(
        StatisticReconciliationTrustedLifecycleProof before,
        StatisticReconciliationTrustedLifecycleProof after);
}

internal sealed class StatisticReconciliationTrustedLifecycleVerifier(
    IStatisticReconciliationActualLifecycleLedger ledger)
    : IStatisticReconciliationTrustedLifecycleVerifier
{
    public async Task<StatisticReconciliationTrustedLifecycleProof> ReadAsync(
        StatisticReconciliationActualAppendResult actual,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var manifest = actual.LifecycleManifestSha256;
        var count = actual.LifecycleObservationCount;
        if (manifest is null || count is not > 0 ||
            actual.CommittedRunBinding.LifecycleManifestSha256 != manifest ||
            actual.CommittedRunBinding.LifecycleObservationCount != count)
        {
            return Incomplete(
                actual,
                StatisticReconciliationTrustedLifecycleProofStates.BindingMissing,
                manifest,
                count);
        }

        StatisticReconciliationActualLifecycleEvidence? evidence;
        try
        {
            evidence = await ledger.ReadAndValidateByManifestAsync(
                    actual.ReconciliationId,
                    manifest,
                    count.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Incomplete(
                actual,
                StatisticReconciliationTrustedLifecycleProofStates.EvidenceInvalid,
                manifest,
                count);
        }

        if (evidence is null)
        {
            return Incomplete(
                actual,
                StatisticReconciliationTrustedLifecycleProofStates.EvidenceMissing,
                manifest,
                count);
        }

        var result = evidence.Result;
        var mismatches = BuildMismatches(result);
        var mismatchSet =
            StatisticReconciliationActualLifecycleEvidenceIntegrity
                .ComputeResultMismatchSetSha(result);
        var validOutcome = result.Outcome is
            StatisticReconciliationLifecycleOutcomes.Matched or
            StatisticReconciliationLifecycleOutcomes.Mismatched or
            StatisticReconciliationLifecycleOutcomes.Stale;
        var exact = evidence.ManifestSha256 == manifest &&
            evidence.ObservationCount == count &&
            evidence.Rows.Length == count &&
            evidence.Manifest.ReconciliationId == actual.ReconciliationId &&
            result.ReconciliationId == actual.ReconciliationId &&
            evidence.Manifest.ResultSemanticSha256 ==
                result.ResultSemanticSha256 &&
            evidence.Manifest.ResultRootCause == result.RootCause &&
            evidence.Manifest.ResultMissingCount == result.MissingCount &&
            evidence.Manifest.ResultExtraCount == result.ExtraCount &&
            evidence.Manifest.ResultMismatchSetSha256 == mismatchSet &&
            result.EvidenceComplete &&
            validOutcome &&
            Sha(evidence.Manifest.LifecycleRowSetSha256) &&
            Sha(result.ResultSemanticSha256) &&
            Sha(mismatchSet) &&
            mismatches.All(static item =>
                Sha(item.IdentitySha256) &&
                Sha(item.StepSemanticSha256) &&
                Sha(item.DescriptorSha256));
        if (!exact)
        {
            return Incomplete(
                actual,
                StatisticReconciliationTrustedLifecycleProofStates.EvidenceInvalid,
                manifest,
                count);
        }

        return Create(
            actual,
            StatisticReconciliationTrustedLifecycleProofStates.Valid,
            result.Outcome,
            result.RootCause,
            result.MissingCount,
            result.ExtraCount,
            evidenceComplete: true,
            captureCoherent:
                result.Outcome != StatisticReconciliationLifecycleOutcomes.Stale,
            manifest,
            count,
            evidence.Manifest.LifecycleRowSetSha256,
            result.ResultSemanticSha256,
            mismatchSet,
            mismatches);
    }

    public StatisticReconciliationTrustedLifecycleProof RequireStable(
        StatisticReconciliationTrustedLifecycleProof before,
        StatisticReconciliationTrustedLifecycleProof after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.EvidenceComplete && after.EvidenceComplete &&
            before.State ==
                StatisticReconciliationTrustedLifecycleProofStates.Valid &&
            after.State ==
                StatisticReconciliationTrustedLifecycleProofStates.Valid &&
            before.Outcome == after.Outcome &&
            before.RootCause == after.RootCause &&
            before.MissingCount == after.MissingCount &&
            before.ExtraCount == after.ExtraCount &&
            before.ManifestSha256 == after.ManifestSha256 &&
            before.ObservationCount == after.ObservationCount &&
            before.LifecycleRowSetSha256 == after.LifecycleRowSetSha256 &&
            before.ResultSemanticSha256 == after.ResultSemanticSha256 &&
            before.MismatchSetSha256 == after.MismatchSetSha256 &&
            before.Mismatches.SequenceEqual(after.Mismatches) &&
            before.ProofSha256 == after.ProofSha256)
        {
            return after with
            {
                ProofSha256 = H(
                    "P10_TRUSTED_LIFECYCLE_STABLE_READ_V2",
                    before.ProofSha256,
                    after.ProofSha256)
            };
        }

        return new StatisticReconciliationTrustedLifecycleProof(
            StatisticReconciliationTrustedLifecycleProofStates.ReplayChanged,
            "~",
            null,
            0,
            0,
            false,
            false,
            after.ManifestSha256 ?? before.ManifestSha256,
            after.ObservationCount ?? before.ObservationCount,
            after.LifecycleRowSetSha256 ?? before.LifecycleRowSetSha256,
            after.ResultSemanticSha256 ?? before.ResultSemanticSha256,
            after.MismatchSetSha256 ?? before.MismatchSetSha256,
            [],
            H(
                "P10_TRUSTED_LIFECYCLE_STABLE_READ_V2",
                before.ProofSha256,
                after.ProofSha256));
    }

    private static StatisticReconciliationTrustedLifecycleProof Incomplete(
        StatisticReconciliationActualAppendResult actual,
        string state,
        string? manifest,
        int? count)
        => Create(
            actual,
            state,
            "~",
            null,
            0,
            0,
            false,
            false,
            manifest,
            count,
            null,
            null,
            null,
            []);

    private static StatisticReconciliationTrustedLifecycleProof Create(
        StatisticReconciliationActualAppendResult actual,
        string state,
        string outcome,
        string? rootCause,
        int missingCount,
        int extraCount,
        bool evidenceComplete,
        bool captureCoherent,
        string? manifest,
        int? count,
        string? rowSet,
        string? result,
        string? mismatchSet,
        ImmutableArray<StatisticReconciliationTrustedLifecycleMismatch>
            mismatches)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (mismatches.IsDefault)
        {
            throw new InvalidOperationException(
                "Lifecycle mismatch descriptors are uninitialized.");
        }
        var descriptorSet = H(
            "P10_TRUSTED_LIFECYCLE_MISMATCH_DESCRIPTOR_SET_V1",
            mismatches.Select(static item => item.DescriptorSha256).ToArray());
        var proof = H(
            "P10_TRUSTED_LIFECYCLE_PROOF_V2",
            actual.ReconciliationId,
            actual.GenerationId,
            actual.GenerationSemanticSha256,
            state,
            outcome,
            rootCause ?? "~",
            missingCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            extraCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            evidenceComplete ? "1" : "0",
            captureCoherent ? "1" : "0",
            manifest ?? "~",
            count?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "~",
            rowSet ?? "~",
            result ?? "~",
            mismatchSet ?? "~",
            descriptorSet);
        return new StatisticReconciliationTrustedLifecycleProof(
            state,
            outcome,
            rootCause,
            missingCount,
            extraCount,
            evidenceComplete,
            captureCoherent,
            manifest,
            count,
            rowSet,
            result,
            mismatchSet,
            mismatches,
            proof);
    }

    private static ImmutableArray<
        StatisticReconciliationTrustedLifecycleMismatch> BuildMismatches(
            StatisticReconciliationLifecycleResult result)
        => result.Steps
            .Where(static step => !step.Exact)
            .Select(static step =>
            {
                var identity = H(
                    "P10_TRUSTED_LIFECYCLE_IDENTITY_REDACTED_V1",
                    step.StableSourceId);
                var root = step.RootCause ?? "~";
                var descriptor = H(
                    "P10_TRUSTED_LIFECYCLE_MISMATCH_DESCRIPTOR_V1",
                    identity,
                    root,
                    step.StepSemanticSha256);
                return new StatisticReconciliationTrustedLifecycleMismatch(
                    identity,
                    root,
                    step.StepSemanticSha256,
                    descriptor);
            })
            .OrderBy(static item => item.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(static item => item.StepSemanticSha256,
                StringComparer.Ordinal)
            .ToImmutableArray();

    private static bool Sha(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationActualCanonical.Hash(domain, fields);
}