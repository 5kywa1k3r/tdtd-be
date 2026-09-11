namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public static class StatisticReconciliationFreshnessStates
{
    public const string Fresh = "FRESH";
    public const string Stale = "STALE";
}

public static class StatisticReconciliationFreshnessDomains
{
    public const string None = "NONE";
    public const string Capture = "CAPTURE";
    public const string Source = "SOURCE";
    public const string Configuration = "CONFIGURATION";
    public const string Catalog = "CATALOG";
    public const string Runtime = "RUNTIME";
    public const string Result = "RESULT";
    public const string Export = "EXPORT";
}

public static class StatisticReconciliationFreshnessReasons
{
    public const string None = "NONE";
    public const string IncompleteCapture = "INCOMPLETE_CAPTURE";
    public const string MixedRevision = "MIXED_REVISION";
    public const string SourceDrift = "SOURCE_DRIFT";
    public const string ConfigurationDrift = "CONFIGURATION_DRIFT";
    public const string CatalogDrift = "CATALOG_DRIFT";
    public const string RuntimeDrift = "RUNTIME_DRIFT";
    public const string ResultDrift = "RESULT_DRIFT";
    public const string ExportDrift = "EXPORT_DRIFT";
}

public static class StatisticReconciliationFreshnessFailureCodes
{
    public const string PinsInvalid = "P10_DELTA_FRESHNESS_PINS_INVALID";
}

public sealed record StatisticReconciliationComparisonBindingPins(
    string SourceSetSha256,
    string P8ConfigurationBundleSha256,
    string ActualConfigurationBundleSha256,
    string CatalogPinSetSha256,
    string RuntimePinSetSha256,
    string? MembershipSemanticSha256 = null);

public sealed record StatisticReconciliationActualFreshnessPins(
    StatisticReconciliationComparisonBindingPins Binding,
    string ResultOwnerSha256,
    string GenerationSemanticSha256,
    string ExportOwnerSha256,
    string? ConfigurationPinSetSha256 = null);

public sealed record StatisticReconciliationFreshnessRequest(
    StatisticReconciliationComparisonBindingPins ExpectedBinding,
    StatisticReconciliationActualFreshnessPins CapturedActual,
    StatisticReconciliationActualFreshnessPins CurrentOwners,
    bool ActualGenerationComplete,
    bool RequiredLayersComplete,
    bool CaptureCoherent);

public sealed record StatisticReconciliationFreshnessAssessment(
    string State,
    string DriftDomain,
    string ReasonCode,
    bool CompleteEvidence,
    bool MatchAllowed,
    bool MismatchAsDataAllowed,
    bool Signable,
    string ExpectedBindingSha256,
    string CapturedPinsSha256,
    string CurrentPinsSha256,
    string AssessmentSha256);

