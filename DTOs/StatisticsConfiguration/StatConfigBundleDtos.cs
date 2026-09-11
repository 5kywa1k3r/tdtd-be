using System.Text.Json.Serialization;

namespace tdtd_be.DTOs.StatisticsConfiguration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigBundleReadRequest(
    string? OwnerKind,
    string? OwnerId,
    IReadOnlyList<StatConfigBundleDependencyReferenceRequest>? DependencyPins);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigBundleDependencyReferenceRequest(
    string? Kind,
    string? OwnerId,
    string? ConfigId,
    string? VersionId,
    int? VersionNo,
    long? Revision,
    string? ConfigHash,
    string? ContributionHash = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigBundleValidateRequest(
    string? CommandId,
    string? ExpectedBundleHash,
    StatConfigBundleReadRequest? Bundle);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatConfigPhaseBarrierRequest(
    string? OwnerKind,
    string? OwnerId,
    string? CommandId,
    string? ExpectedBundleHash);

public sealed record StatConfigBundleDependencyPin(
    string Kind,
    string OwnerKind,
    string OwnerId,
    string ConfigId,
    string VersionId,
    int VersionNo,
    long Revision,
    string Status,
    string ConfigHash,
    string ContributionHash,
    IReadOnlyList<string> DependencyPins);

public sealed record StatConfigBundleEligibility(
    string Configuration,
    string FutureResult,
    string Executor,
    string TargetPhase);

public sealed record StatConfigBundleReadback(
    string SchemaVersion,
    string OwnerKind,
    string OwnerId,
    bool IsEmpty,
    IReadOnlyList<StatConfigBundleDependencyPin> Pins,
    StatConfigBundleEligibility Eligibility,
    string Freshness,
    string CanonicalJson,
    string BundleHash);

public static class StatConfigBundleDependencyKinds
{
    public const string Label = "LABEL";
    public const string Field = "FIELD";
    public const string Table = "TABLE";
    public const string Basic = "BASIC";
    public const string Advanced = "ADVANCED";
    public const string Diff = "DIFF";
    public const string FlowContribution = "FLOW_CONTRIBUTION";
    public const string Readiness = "READINESS";

    public static readonly IReadOnlyList<string> Ordered =
    [
        Label,
        Field,
        Table,
        Basic,
        Advanced,
        Diff,
        FlowContribution,
        Readiness
    ];

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(Ordered, StringComparer.Ordinal);
}
