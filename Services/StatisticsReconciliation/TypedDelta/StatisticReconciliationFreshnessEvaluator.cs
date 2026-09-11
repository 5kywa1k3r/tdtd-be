using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public sealed class StatisticReconciliationFreshnessEvaluator
{
    public StatisticReconciliationFreshnessAssessment Evaluate(
        StatisticReconciliationFreshnessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = Normalize(request.ExpectedBinding, "expectedBinding");
        var captured = Normalize(request.CapturedActual, "capturedActual");
        var current = Normalize(request.CurrentOwners, "currentOwners");
        var expectedSha = BindingSha(expected);
        var capturedSha = PinsSha(captured);
        var currentSha = PinsSha(current);

        if (!request.ActualGenerationComplete || !request.RequiredLayersComplete)
        {
            return Assessment(
                StatisticReconciliationFreshnessStates.Stale,
                StatisticReconciliationFreshnessDomains.Capture,
                StatisticReconciliationFreshnessReasons.IncompleteCapture,
                false,
                expectedSha,
                capturedSha,
                currentSha);
        }
        if (!request.CaptureCoherent)
        {
            return Assessment(
                StatisticReconciliationFreshnessStates.Stale,
                StatisticReconciliationFreshnessDomains.Capture,
                StatisticReconciliationFreshnessReasons.MixedRevision,
                true,
                expectedSha,
                capturedSha,
                currentSha);
        }

        var drift = FirstDrift(
                        expected,
                        captured.Binding,
                        compareSourceLineage: false) ??
                    FirstDrift(captured, current);
        if (drift is not null)
        {
            return Assessment(
                StatisticReconciliationFreshnessStates.Stale,
                drift.Value.Domain,
                drift.Value.Reason,
                true,
                expectedSha,
                capturedSha,
                currentSha);
        }
        return Assessment(
            StatisticReconciliationFreshnessStates.Fresh,
            StatisticReconciliationFreshnessDomains.None,
            StatisticReconciliationFreshnessReasons.None,
            true,
            expectedSha,
            capturedSha,
            currentSha);
    }

    private static (string Domain, string Reason)? FirstDrift(
        StatisticReconciliationComparisonBindingPins left,
        StatisticReconciliationComparisonBindingPins right,
        bool compareSourceLineage)
    {
        var neutralMembership = left.MembershipSemanticSha256 is not null ||
            right.MembershipSemanticSha256 is not null;
        if (neutralMembership &&
            (left.MembershipSemanticSha256 is null ||
             right.MembershipSemanticSha256 is null ||
             !Same(left.MembershipSemanticSha256,
                 right.MembershipSemanticSha256)) ||
            !neutralMembership &&
                !Same(left.SourceSetSha256, right.SourceSetSha256) ||
            neutralMembership && compareSourceLineage &&
                !Same(left.SourceSetSha256, right.SourceSetSha256))
            return (StatisticReconciliationFreshnessDomains.Source,
                StatisticReconciliationFreshnessReasons.SourceDrift);
        if (!Same(left.P8ConfigurationBundleSha256,
                right.P8ConfigurationBundleSha256) ||
            !Same(left.ActualConfigurationBundleSha256,
                right.ActualConfigurationBundleSha256))
            return (StatisticReconciliationFreshnessDomains.Configuration,
                StatisticReconciliationFreshnessReasons.ConfigurationDrift);
        if (!Same(left.CatalogPinSetSha256, right.CatalogPinSetSha256))
            return (StatisticReconciliationFreshnessDomains.Catalog,
                StatisticReconciliationFreshnessReasons.CatalogDrift);
        if (!Same(left.RuntimePinSetSha256, right.RuntimePinSetSha256))
            return (StatisticReconciliationFreshnessDomains.Runtime,
                StatisticReconciliationFreshnessReasons.RuntimeDrift);
        return null;
    }

    private static (string Domain, string Reason)? FirstDrift(
        StatisticReconciliationActualFreshnessPins left,
        StatisticReconciliationActualFreshnessPins right)
    {
        var binding = FirstDrift(
            left.Binding, right.Binding, compareSourceLineage: true);
        if (binding is not null)
            return binding;
        if ((left.ConfigurationPinSetSha256 is null) !=
                (right.ConfigurationPinSetSha256 is null) ||
            left.ConfigurationPinSetSha256 is not null &&
            !Same(left.ConfigurationPinSetSha256,
                right.ConfigurationPinSetSha256!))
            return (StatisticReconciliationFreshnessDomains.Configuration,
                StatisticReconciliationFreshnessReasons.ConfigurationDrift);
        if (!Same(left.ResultOwnerSha256, right.ResultOwnerSha256) ||
            !Same(left.GenerationSemanticSha256, right.GenerationSemanticSha256))
            return (StatisticReconciliationFreshnessDomains.Result,
                StatisticReconciliationFreshnessReasons.ResultDrift);
        if (!Same(left.ExportOwnerSha256, right.ExportOwnerSha256))
            return (StatisticReconciliationFreshnessDomains.Export,
                StatisticReconciliationFreshnessReasons.ExportDrift);
        return null;
    }

    private static StatisticReconciliationComparisonBindingPins Normalize(
        StatisticReconciliationComparisonBindingPins pins,
        string path)
    {
        ArgumentNullException.ThrowIfNull(pins);
        RequireSha(pins.SourceSetSha256, $"{path}.sourceSetSha256");
        RequireSha(pins.P8ConfigurationBundleSha256,
            $"{path}.p8ConfigurationBundleSha256");
        RequireSha(pins.ActualConfigurationBundleSha256,
            $"{path}.actualConfigurationBundleSha256");
        RequireSha(pins.CatalogPinSetSha256, $"{path}.catalogPinSetSha256");
        RequireSha(pins.RuntimePinSetSha256, $"{path}.runtimePinSetSha256");
        if (pins.MembershipSemanticSha256 is not null)
            RequireSha(pins.MembershipSemanticSha256,
                $"{path}.membershipSemanticSha256");
        return pins;
    }

    private static StatisticReconciliationActualFreshnessPins Normalize(
        StatisticReconciliationActualFreshnessPins pins,
        string path)
    {
        ArgumentNullException.ThrowIfNull(pins);
        _ = Normalize(pins.Binding, $"{path}.binding");
        RequireSha(pins.ResultOwnerSha256, $"{path}.resultOwnerSha256");
        RequireSha(pins.GenerationSemanticSha256,
            $"{path}.generationSemanticSha256");
        RequireSha(pins.ExportOwnerSha256, $"{path}.exportOwnerSha256");
        if (pins.ConfigurationPinSetSha256 is not null)
            RequireSha(pins.ConfigurationPinSetSha256,
                $"{path}.configurationPinSetSha256");
        return pins;
    }

    private static StatisticReconciliationFreshnessAssessment Assessment(
        string state,
        string domain,
        string reason,
        bool completeEvidence,
        string expectedSha,
        string capturedSha,
        string currentSha)
    {
        var fresh = state == StatisticReconciliationFreshnessStates.Fresh;
        return new StatisticReconciliationFreshnessAssessment(
            state,
            domain,
            reason,
            completeEvidence,
            fresh,
            fresh,
            fresh,
            expectedSha,
            capturedSha,
            currentSha,
            HashFields(
                "P10_FRESHNESS_ASSESSMENT_V1",
                state,
                domain,
                reason,
                completeEvidence ? "1" : "0",
                expectedSha,
                capturedSha,
                currentSha));
    }

    private static string BindingSha(StatisticReconciliationComparisonBindingPins pins)
        => pins.MembershipSemanticSha256 is null
            ? HashFields(
                "P10_COMPARISON_BINDING_PINS_V1",
                pins.SourceSetSha256,
                pins.P8ConfigurationBundleSha256,
                pins.ActualConfigurationBundleSha256,
                pins.CatalogPinSetSha256,
                pins.RuntimePinSetSha256)
            : HashFields(
                "P10_COMPARISON_BINDING_PINS_V2",
                pins.SourceSetSha256,
                pins.MembershipSemanticSha256,
                pins.P8ConfigurationBundleSha256,
                pins.ActualConfigurationBundleSha256,
                pins.CatalogPinSetSha256,
                pins.RuntimePinSetSha256);

    private static string PinsSha(StatisticReconciliationActualFreshnessPins pins)
        => pins.ConfigurationPinSetSha256 is null
            ? HashFields(
                pins.Binding.MembershipSemanticSha256 is null
                    ? "P10_ACTUAL_FRESHNESS_PINS_V1"
                    : "P10_ACTUAL_FRESHNESS_PINS_V2",
                BindingSha(pins.Binding),
                pins.ResultOwnerSha256,
                pins.GenerationSemanticSha256,
                pins.ExportOwnerSha256)
            : HashFields(
                "P10_ACTUAL_FRESHNESS_PINS_V3",
                BindingSha(pins.Binding),
                pins.ConfigurationPinSetSha256,
                pins.ResultOwnerSha256,
                pins.GenerationSemanticSha256,
                pins.ExportOwnerSha256);

    private static void RequireSha(string value, string path)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                                             not (>= 'a' and <= 'f')))
        {
            throw new StatisticReconciliationTypedComparisonException(
                StatisticReconciliationFreshnessFailureCodes.PinsInvalid,
                $"Canonical lowercase SHA-256 required at {path}.");
        }
    }

    private static bool Same(string left, string right)
        => StringComparer.Ordinal.Equals(left, right);

    private static string HashFields(string domain, params string[] fields)
    {
        var builder = new StringBuilder();
        AppendHashField(builder, domain);
        foreach (var field in fields)
            AppendHashField(builder, field);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendHashField(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value)
                .ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }
}

