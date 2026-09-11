using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public static class StatisticReconciliationStatefulValueTypes
{
    public const string Number = "NUMBER";
}

public static class StatisticReconciliationObservationValueStates
{
    public const string Missing = "MISSING";
    public const string Extra = "EXTRA";
    public const string Null = "NULL";
    public const string Empty = "EMPTY";
    public const string Redacted = "REDACTED";
    public const string Value = "VALUE";
}

public static class StatisticReconciliationIdentityDeltaCodes
{
    public const string None = "NONE";
    public const string MissingIdentity = "MISSING_IDENTITY";
    public const string ExtraIdentity = "EXTRA_IDENTITY";
    public const string StateMismatch = "STATE_MISMATCH";
    public const string ValueTypeMismatch = "VALUE_TYPE_MISMATCH";
    public const string ValueMismatch = "VALUE_MISMATCH";
}

public static class StatisticReconciliationLayerDeltaVerdicts
{
    public const string ZeroDelta = "ZERO_DELTA";
    public const string NonzeroDelta = "NONZERO_DELTA";
}

public static class StatisticReconciliationStatefulDeltaFailureCodes
{
    public const string ObservationInvalid = "P10_DELTA_STATEFUL_OBSERVATION_INVALID";
    public const string IdentityPairInvalid = "P10_DELTA_IDENTITY_PAIR_INVALID";
}

public sealed record StatisticReconciliationStatefulObservation(
    StatisticReconciliationStableIdentity Identity,
    string ValueType,
    string ValueState,
    string? CanonicalValue,
    int DecimalScale = 0,
    string? CollectionSemantics = null);

public sealed record StatisticReconciliationStatefulComparison(
    string IdentityKind,
    ImmutableArray<string> IdentityComponents,
    string IdentitySha256,
    string ExpectedState,
    string ActualState,
    string DeltaState,
    string? ExpectedValueType,
    string? ActualValueType,
    string? ExpectedCollectionSemantics,
    string? ActualCollectionSemantics,
    string? ExpectedCanonicalValue,
    string? ActualCanonicalValue,
    string? NumericDeltaCanonicalValue,
    int? NumericDeltaScale,
    string DeltaCode,
    string LayerVerdict,
    bool Equal,
    string ComparisonSha256);

